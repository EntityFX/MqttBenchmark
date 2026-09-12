using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using EntityFX.MqttBenchmark.Calibration;
using MQTTnet.Client;

namespace EntityFX.MqttBenchmark.Campaign;

/// <summary>
/// Протокольный preflight (Connect/Subscribe/Publish/Delivered для каждого QoS, 20 RTT-эхо,
/// чтение $SYS/broker/version). Результат — неизменяемый <see cref="ProtocolPreflight"/>;
/// нагрузки или учёта доставок здесь нет.
/// </summary>
public sealed class MqttPreflightProbe
{
    private readonly MqttTransportClient transport;
    private readonly TimeSpan timeout;
    private readonly TimeSpan sysWait;

    public MqttPreflightProbe(MqttTransportClient transport, TimeSpan? timeout = null, TimeSpan? sysWait = null)
    {
        this.transport = transport;
        this.timeout = timeout ?? TimeSpan.FromSeconds(10);
        this.sysWait = sysWait ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>Выполняет протокольный preflight. Число RTT-эхо проб задаётся вызывающей стороной
    /// (из конфигурации кампании); успех требует полного набора проб.</summary>
    public async Task<ProtocolPreflight> ProbeAsync(string broker, Uri endpoint, int rttProbes,
        CancellationToken cancellationToken = default)
    {
        var probes = new List<ProtocolProbe>();
        var topic = "mqttbenchmark/preflight/" + Guid.NewGuid().ToString("N");
        for (var qos = 0; qos <= 2; qos++)
        {
            var probe = new ProtocolProbe(qos, false, false, false, false, null);
            using var client = transport.CreateClient();
            try
            {
                await transport.ConnectAsync(client, endpoint, cancellationToken);
                probe = probe with { Connect = true };
                var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var payload = IdentifiedPayload.Create(16, "preflight", topic, qos, 0);
                client.ApplicationMessageReceivedAsync += args =>
                {
                    if (args.ApplicationMessage.Topic == topic && args.ApplicationMessage.PayloadSegment.AsSpan().SequenceEqual(payload))
                        received.TrySetResult(true);
                    return Task.CompletedTask;
                };
                await transport.SubscribeAsync(client, topic, qos, cancellationToken);
                probe = probe with { Subscribe = true };
                await transport.PublishAsync(client, topic, payload, qos, cancellationToken);
                probe = probe with { Publish = true };
                await received.Task.WaitAsync(timeout, cancellationToken);
                probe = probe with { Delivered = true };
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested) { probe = probe with { Error = e.GetType().Name + ": " + e.Message }; }
            finally { await transport.DisconnectAsync(client); }
            probes.Add(probe);
        }
        var rtts = new List<double>();
        string? rttError = null, version = null, versionError = null;
        using (var client = transport.CreateClient())
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
                await transport.ConnectAsync(client, endpoint, cancellationToken);
                await transport.SubscribeAsync(client, topic, 0, cancellationToken);
                for (var i = 0; i < rttProbes; i++)
                {
                    var payload = IdentifiedPayload.Create(16, "rtt", topic, 0, i);
                    var id = IdentifiedPayload.ReadId(payload);
                    var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    pending[id] = received;
                    var clock = Stopwatch.StartNew();
                    await transport.PublishAsync(client, topic, payload, 0, cancellationToken);
                    await received.Task.WaitAsync(timeout, cancellationToken);
                    rtts.Add(clock.Elapsed.TotalMilliseconds);
                    pending.TryRemove(id, out _);
                }
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested) { rttError = e.GetType().Name + ": " + e.Message; }
            finally { await transport.DisconnectAsync(client); }
        }
        using (var client = transport.CreateClient())
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
                await transport.ConnectAsync(client, endpoint, cancellationToken);
                await transport.SubscribeAsync(client, "$SYS/broker/version", 0, cancellationToken);
                version = await received.Task.WaitAsync(sysWait, cancellationToken);
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested) { versionError = e.GetType().Name + ": " + e.Message; }
            finally { await transport.DisconnectAsync(client); }
        }
        return new(broker, probes.All(x => x.Delivered) && rtts.Count == rttProbes, probes, rtts,
            rtts.Count == rttProbes ? LatencyQuantiles.FromSamples(rtts).P50Ms : null,
            version, version == null ? "unavailable" : "observed", rttError, versionError);
    }
}