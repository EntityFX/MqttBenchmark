using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using EntityFX.MqttBenchmark.Calibration;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace EntityFX.MqttBenchmark.Campaign;

public sealed record ProtocolProbe(int Qos, bool Connect, bool Subscribe, bool Publish, bool Delivered, string? Error);
public sealed record ProtocolPreflight(string Broker, bool Success, IReadOnlyList<ProtocolProbe> Probes,
    IReadOnlyList<double> RttSamplesMs, double? RttBaselineMs, string? SysBrokerVersion, string SysVersionStatus,
    string? RttError = null, string? SysVersionError = null);

public sealed class MqttCampaignRunner
{
    private readonly TimeSpan timeout;
    private readonly TimeSpan sysWait;
    private readonly MqttFactory factory = new();
    public MqttCampaignRunner(TimeSpan? timeout = null, TimeSpan? sysWait = null) =>
        (this.timeout, this.sysWait) = (timeout ?? TimeSpan.FromSeconds(10), sysWait ?? TimeSpan.FromSeconds(10));

    public async Task<ProtocolPreflight> PreflightAsync(string broker, Uri endpoint, CancellationToken cancellationToken = default)
    {
        var probes = new List<ProtocolProbe>();
        var topic = "mqttbenchmark/preflight/" + Guid.NewGuid().ToString("N");
        for (var qos = 0; qos <= 2; qos++)
        {
            var probe = new ProtocolProbe(qos, false, false, false, false, null);
            using var client = factory.CreateMqttClient();
            try
            {
                await ConnectAsync(client, endpoint, cancellationToken);
                probe = probe with { Connect = true };
                var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var payload = IdentifiedPayload.Create(16, "preflight", topic, qos, 0);
                client.ApplicationMessageReceivedAsync += args =>
                {
                    if (args.ApplicationMessage.Topic == topic && args.ApplicationMessage.PayloadSegment.AsSpan().SequenceEqual(payload))
                        received.TrySetResult(true);
                    return Task.CompletedTask;
                };
                await SubscribeAsync(client, topic, qos, cancellationToken);
                probe = probe with { Subscribe = true };
                await PublishAsync(client, topic, payload, qos, cancellationToken);
                probe = probe with { Publish = true };
                await received.Task.WaitAsync(timeout, cancellationToken);
                probe = probe with { Delivered = true };
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested) { probe = probe with { Error = e.GetType().Name + ": " + e.Message }; }
            finally { await DisconnectAsync(client); }
            probes.Add(probe);
        }
        var rtts = new List<double>();
        string? rttError = null, version = null, versionError = null;
        using (var client = factory.CreateMqttClient())
        {
            try
            {
                var pending = new ConcurrentDictionary<string, TaskCompletionSource<bool>>();
                client.ApplicationMessageReceivedAsync += args =>
                {
                    if (args.ApplicationMessage.Topic == topic && args.ApplicationMessage.PayloadSegment.Count == 16 &&
                        pending.TryGetValue(IdentifiedPayload.ReadId(args.ApplicationMessage.PayloadSegment.AsSpan()), out var signal)) signal.TrySetResult(true);
                    return Task.CompletedTask;
                };
                await ConnectAsync(client, endpoint, cancellationToken);
                await SubscribeAsync(client, topic, 0, cancellationToken);
                for (var i = 0; i < 20; i++)
                {
                    var payload = IdentifiedPayload.Create(16, "rtt", topic, 0, i);
                    var id = IdentifiedPayload.ReadId(payload);
                    var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    pending[id] = received;
                    var clock = Stopwatch.StartNew();
                    await PublishAsync(client, topic, payload, 0, cancellationToken);
                    await received.Task.WaitAsync(timeout, cancellationToken);
                    rtts.Add(clock.Elapsed.TotalMilliseconds);
                    pending.TryRemove(id, out _);
                }
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested) { rttError = e.GetType().Name + ": " + e.Message; }
            finally { await DisconnectAsync(client); }
        }
        using (var client = factory.CreateMqttClient())
        {
            try
            {
                var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                client.ApplicationMessageReceivedAsync += args =>
                {
                    if (args.ApplicationMessage.Topic == "$SYS/broker/version" && args.ApplicationMessage.PayloadSegment.Count > 0)
                        received.TrySetResult(Encoding.UTF8.GetString(args.ApplicationMessage.PayloadSegment));
                    return Task.CompletedTask;
                };
                await ConnectAsync(client, endpoint, cancellationToken);
                await SubscribeAsync(client, "$SYS/broker/version", 0, cancellationToken);
                version = await received.Task.WaitAsync(sysWait, cancellationToken);
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested) { versionError = e.GetType().Name + ": " + e.Message; }
            finally { await DisconnectAsync(client); }
        }
        return new(broker, probes.All(x => x.Delivered) && rtts.Count == 20, probes, rtts,
            rtts.Count == 20 ? LatencyQuantiles.FromSamples(rtts).P50Ms : null,
            version, version == null ? "unavailable" : "observed", rttError, versionError);
    }

    public async Task<RunObservation> RunAsync(CampaignDefinition config, CampaignKey key, Uri endpoint, string campaignId,
        string runId, string directory, double rttBaselineMs, CancellationToken cancellationToken = default)
    {
        config.Validate();
        Directory.CreateDirectory(directory);
        using var publishLog = new EventLog(Path.Combine(directory, "publishes.ndjson"));
        using var deliveryLog = new EventLog(Path.Combine(directory, "deliveries.ndjson"));
        var clients = new List<IMqttClient>();
        using var subscriber = factory.CreateMqttClient();
        var ledger = new DeliveryLedger();
        var clock = Stopwatch.StartNew();
        var topic = "mqttbenchmark/" + IdentifiedPayload.ReadId(IdentifiedPayload.Create(16, campaignId, runId, 0, 0));
        var deliveryGate = new object();
        var collecting = true;
        subscriber.ApplicationMessageReceivedAsync += args =>
        {
            if (args.ApplicationMessage.Topic != topic) return Task.CompletedTask;
            lock (deliveryGate)
            {
                if (!collecting) return Task.CompletedTask;
                var payload = args.ApplicationMessage.PayloadSegment;
                var id = payload.Count == key.MessageBytes ? IdentifiedPayload.ReadId(payload.AsSpan())
                    : "malformed:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload.AsSpan()));
                var seconds = clock.Elapsed.TotalSeconds;
                ledger.Deliver(id, seconds);
                deliveryLog.Write(new { id, elapsedSeconds = seconds, payloadBytes = payload.Count });
            }
            return Task.CompletedTask;
        };
        try
        {
            await ConnectAsync(subscriber, endpoint, cancellationToken);
            await SubscribeAsync(subscriber, topic, key.Qos, cancellationToken);
            for (var i = 0; i < key.Publishers; i++)
            {
                var client = factory.CreateMqttClient(); clients.Add(client);
                await ConnectAsync(client, endpoint, cancellationToken);
            }
            await PublishWindowAsync(config.WarmupSeconds, false);
            var startedUtc = DateTimeOffset.UtcNow;
            var started = clock.Elapsed.TotalSeconds;
            await PublishWindowAsync(config.MeasurementSeconds, true);
            var actualSeconds = clock.Elapsed.TotalSeconds - started;
            var endedUtc = DateTimeOffset.UtcNow;
            var drainStart = clock.Elapsed.TotalSeconds;
            string? reason;
            while ((reason = DrainPolicy.Decide(ledger, drainStart, clock.Elapsed.TotalSeconds,
                config.QuietPeriodSeconds, config.DrainTimeoutSeconds)) == null)
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
            MeasurementSummary summary;
            lock (deliveryGate) { collecting = false; summary = ledger.Snapshot(key.Qos, actualSeconds); }
            var observation = new RunObservation(key, summary, startedUtc, endedUtc, reason,
                clock.Elapsed.TotalSeconds - drainStart, rttBaselineMs);
            await Task.Delay(TimeSpan.FromSeconds(config.CooldownSeconds), cancellationToken);
            return observation;
        }
        finally
        {
            lock (deliveryGate) collecting = false;
            foreach (var client in clients) { await DisconnectAsync(client); client.Dispose(); }
            await DisconnectAsync(subscriber);
        }

        async Task PublishWindowAsync(double seconds, bool measure)
        {
            var deadline = clock.Elapsed.TotalSeconds + seconds;
            await Task.WhenAll(clients.Select((client, publisher) => Task.Run(async () =>
            {
                long sequence = 0;
                while (clock.Elapsed.TotalSeconds < deadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var currentSequence = sequence++;
                    var payload = IdentifiedPayload.Create(key.MessageBytes, campaignId, runId + (measure ? "" : "/warmup"), publisher, currentSequence);
                    var id = IdentifiedPayload.ReadId(payload);
                    if (measure)
                    {
                        ledger.Attempt(id);
                        publishLog.Write(new { @event = "attempt", id, publisher, sequence = currentSequence,
                            payloadBytes = payload.Length, elapsedSeconds = clock.Elapsed.TotalSeconds });
                    }
                    var began = clock.Elapsed.TotalMilliseconds;
                    try
                    {
                        await PublishAsync(client, measure ? topic : topic + "/warmup", payload, key.Qos, cancellationToken);
                        var elapsed = clock.Elapsed.TotalMilliseconds - began;
                        if (measure)
                        {
                            ledger.Complete(id, elapsed);
                            publishLog.Write(new { @event = "completed", id, elapsedSeconds = clock.Elapsed.TotalSeconds,
                                latencyMs = key.Qos == 0 ? (double?)null : elapsed });
                        }
                    }
                    catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                    {
                        if (measure)
                        {
                            var failure = error.GetType().Name + ": " + error.Message;
                            ledger.Fail(id, failure);
                            publishLog.Write(new { @event = "failed", id, error = failure, elapsedSeconds = clock.Elapsed.TotalSeconds });
                        }
                    }
                }
            }, cancellationToken)));
        }
    }

    private async Task ConnectAsync(IMqttClient client, Uri endpoint, CancellationToken ct)
    {
        if (endpoint.Scheme != "mqtt") throw new InvalidDataException("Stand requires plain MQTT TCP endpoints.");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct); cancel.CancelAfter(timeout);
        var result = await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer(endpoint.Host, endpoint.Port)
            .WithClientId("mb-" + Guid.NewGuid().ToString("N")).WithProtocolVersion(MqttProtocolVersion.V311)
            .WithCleanSession().WithTimeout(timeout).Build(), cancel.Token);
        if (result.ResultCode != MqttClientConnectResultCode.Success) throw new IOException("CONNECT: " + result.ResultCode);
    }
    private async Task SubscribeAsync(IMqttClient client, string topic, int qos, CancellationToken ct)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct); cancel.CancelAfter(timeout);
        var result = await client.SubscribeAsync(factory.CreateSubscribeOptionsBuilder().WithTopicFilter(topic,
            (MqttQualityOfServiceLevel)qos).Build(), cancel.Token);
        if (result.Items.Count != 1 || (int)result.Items.Single().ResultCode != qos)
            throw new IOException("SUBSCRIBE did not grant requested QoS " + qos);
    }
    private async Task PublishAsync(IMqttClient client, string topic, byte[] payload, int qos, CancellationToken ct)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct); cancel.CancelAfter(timeout);
        var result = await client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload)
            .WithQualityOfServiceLevel((MqttQualityOfServiceLevel)qos).Build(), cancel.Token);
        if (result.ReasonCode != MqttClientPublishReasonCode.Success) throw new IOException("PUBLISH: " + result.ReasonCode);
    }
    private async Task DisconnectAsync(IMqttClient client)
    {
        try { if (client.IsConnected) await client.DisconnectAsync().WaitAsync(timeout); }
        catch { /* Preserve the original protocol outcome. */ }
    }

    private sealed class EventLog : IDisposable
    {
        private readonly StreamWriter writer;
        private readonly object gate = new();
        private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        public EventLog(string path) => writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
        public void Write<T>(T value) { lock (gate) writer.WriteLine(JsonSerializer.Serialize(value, Json)); }
        public void Dispose() { lock (gate) writer.Dispose(); }
    }
}
