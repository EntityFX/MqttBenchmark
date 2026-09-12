using System.Diagnostics;
using MQTTnet.Client;

namespace EntityFX.MqttBenchmark.Campaign;

/// <summary>
/// Исполнение одного нагрузочного run-цикла (warmup → measurement → bounded in-flight
/// completion → drain → cooldown) и сбор <see cref="RunObservation"/>. Транспорт
/// делегируется <see cref="MqttTransportClient"/>, учёт доставок — <see cref="DeliveryLedger"/>,
/// журнал — <see cref="CampaignEventLog"/>.
/// </summary>
public sealed class CampaignWorkloadRunner
{
    private readonly MqttTransportClient transport;

    public CampaignWorkloadRunner(MqttTransportClient transport)
    {
        this.transport = transport;
    }

    public async Task<RunObservation> RunAsync(CampaignDefinition config, CampaignKey key, Uri endpoint, string campaignId,
        string runId, string directory, double rttBaselineMs, CancellationToken cancellationToken = default,
        IMeasurementWindowObserver? measurementObserver = null)
    {
        config.Validate();
        Directory.CreateDirectory(directory);
        using var publishLog = new CampaignEventLog(Path.Combine(directory, "publishes.ndjson"));
        using var deliveryLog = new CampaignEventLog(Path.Combine(directory, "deliveries.ndjson"));
        var clients = new List<IMqttClient>();
        using var subscriber = transport.CreateClient();
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
            await transport.ConnectAsync(subscriber, endpoint, cancellationToken);
            await transport.SubscribeAsync(subscriber, topic, key.Qos, cancellationToken);
            for (var i = 0; i < key.Publishers; i++)
            {
                var client = transport.CreateClient(); clients.Add(client);
                await transport.ConnectAsync(client, endpoint, cancellationToken);
            }
            await PublishWindowAsync(config.WarmupSeconds, false);
            var startedUtc = DateTimeOffset.UtcNow;
            var started = Stopwatch.GetTimestamp();
            measurementObserver?.MeasurementStarted(started, startedUtc);
            await PublishWindowAsync(config.MeasurementSeconds, true);
            var ended = Stopwatch.GetTimestamp();
            var endedUtc = DateTimeOffset.UtcNow;
            measurementObserver?.MeasurementEnded(ended, endedUtc);
            var actualSeconds = (ended - started) / (double)Stopwatch.Frequency;
            var drainStart = clock.Elapsed.TotalSeconds;
            string? reason;
            while ((reason = DrainPolicy.Decide(ledger, drainStart, clock.Elapsed.TotalSeconds,
                config.QuietPeriodSeconds, config.DrainTimeoutSeconds)) == null)
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
            MeasurementSummary summary;
            lock (deliveryGate) { collecting = false; summary = ledger.Snapshot(key.Qos, actualSeconds); }
            var observation = new RunObservation(key, summary, startedUtc, endedUtc, reason,
                clock.Elapsed.TotalSeconds - drainStart, rttBaselineMs);
            // Cooldown — ровно из конфигурации кампании (Validate гарантирует конечность и неотрицательность).
            await Task.Delay(TimeSpan.FromSeconds(config.CooldownSeconds), cancellationToken);
            return observation;
        }
        finally
        {
            lock (deliveryGate) collecting = false;
            foreach (var client in clients) { await transport.DisconnectAsync(client); client.Dispose(); }
            await transport.DisconnectAsync(subscriber);
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
                        await transport.PublishAsync(client, measure ? topic : topic + "/warmup", payload, key.Qos, cancellationToken);
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
}