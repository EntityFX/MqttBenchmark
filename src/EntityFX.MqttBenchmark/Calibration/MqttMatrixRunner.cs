using System.Diagnostics;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace EntityFX.MqttBenchmark.Calibration;

public sealed class MqttMatrixRunner
{
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _deliveryDrain;

    public MqttMatrixRunner(TimeSpan? connectTimeout = null, TimeSpan? deliveryDrain = null)
    {
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(30);
        _deliveryDrain = deliveryDrain ?? TimeSpan.FromSeconds(2);
    }

    public async Task<RawBenchmarkResult> RunAsync(
        BenchmarkRunSpec spec, CancellationToken cancellationToken = default)
    {
        var runId = Guid.NewGuid().ToString("N");
        var factory = new MqttFactory();
        var publishers = new List<IMqttClient>();
        IMqttClient? subscriber = null;
        try
        {
            subscriber = factory.CreateMqttClient();
            var topicPrefix = $"mqttbenchmark/{spec.Broker.Name.ToLowerInvariant()}/{runId}";
            var warmupTopic = $"{topicPrefix}/warmup";
            var measurementTopic = $"{topicPrefix}/measurement";
            long received = 0;
            subscriber.ApplicationMessageReceivedAsync += args =>
            {
                if (string.Equals(args.ApplicationMessage.Topic, measurementTopic, StringComparison.Ordinal))
                    Interlocked.Increment(ref received);
                return Task.CompletedTask;
            };
            await ConnectAsync(subscriber, spec, $"mqttbenchmark-sub-{runId}", cancellationToken);
            var subscribe = factory.CreateSubscribeOptionsBuilder()
                .WithTopicFilter(filter => filter.WithTopic(measurementTopic)
                    .WithQualityOfServiceLevel((MqttQualityOfServiceLevel)spec.Qos))
                .Build();
            await subscriber.SubscribeAsync(subscribe, cancellationToken);

            for (var index = 0; index < spec.Clients; index++)
            {
                var client = factory.CreateMqttClient();
                await ConnectAsync(client, spec, $"mqttbenchmark-pub-{runId}-{index}", cancellationToken);
                publishers.Add(client);
            }

            var payload = BenchmarkPayload.Create(spec.MessageBytes, spec.Repeat);
            await PublishForAsync(publishers, warmupTopic, payload, spec.Qos,
                TimeSpan.FromSeconds(spec.WarmupSeconds), collect: false, cancellationToken);

            var measurementStarted = DateTimeOffset.UtcNow;
            var outcome = await PublishForAsync(publishers, measurementTopic, payload, spec.Qos,
                TimeSpan.FromSeconds(spec.MeasurementSeconds), collect: true, cancellationToken);
            var measurementEnded = DateTimeOffset.UtcNow;
            if (_deliveryDrain > TimeSpan.Zero)
                await Task.Delay(_deliveryDrain, cancellationToken);

            var latency = spec.Qos == 0 || outcome.LatencyMs.Count == 0
                ? null
                : LatencyQuantiles.FromSamples(outcome.LatencyMs);
            var result = new RawBenchmarkResult(runId, spec.Broker.Name, spec.Broker.Uri,
                spec.MessageBytes, spec.Qos, spec.Clients, spec.Repeat,
                spec.WarmupSeconds, spec.MeasurementSeconds,
                outcome.Ok + outcome.Failed, outcome.Ok, outcome.Failed,
                Interlocked.Read(ref received), latency, measurementStarted, measurementEnded);
            result.Validate();
            return result;
        }
        finally
        {
            foreach (var client in publishers)
                await DisconnectAndDisposeAsync(client);
            if (subscriber != null) await DisconnectAndDisposeAsync(subscriber);
        }
    }

    private async Task ConnectAsync(
        IMqttClient client, BenchmarkRunSpec spec, string clientId, CancellationToken cancellationToken)
    {
        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(spec.Broker.Uri.Host, spec.Broker.Uri.Port)
            .WithClientId(clientId)
            .WithCleanSession(true)
            .WithTimeout(_connectTimeout)
            .Build();
        var result = await client.ConnectAsync(options, cancellationToken);
        if (result.ResultCode != MqttClientConnectResultCode.Success)
            throw new InvalidOperationException(
                $"Broker {spec.Broker.Name} rejected client {clientId}: {result.ResultCode}.");
    }

    private static async Task<PublishOutcome> PublishForAsync(
        IReadOnlyList<IMqttClient> clients, string topic, byte[] payload, int qos,
        TimeSpan duration, bool collect, CancellationToken cancellationToken)
    {
        if (duration <= TimeSpan.Zero) return new PublishOutcome(0, 0, Array.Empty<double>());
        var stopwatch = Stopwatch.StartNew();
        var tasks = clients.Select(client => Task.Run(async () =>
        {
            long ok = 0;
            long failed = 0;
            var timings = new List<double>();
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithQualityOfServiceLevel((MqttQualityOfServiceLevel)qos)
                .WithPayload(payload)
                .Build();
            while (stopwatch.Elapsed < duration)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var publishStopwatch = Stopwatch.StartNew();
                try
                {
                    var result = await client.PublishAsync(message, cancellationToken);
                    if (result.ReasonCode == MqttClientPublishReasonCode.Success)
                    {
                        ok++;
                        if (collect && qos > 0) timings.Add(publishStopwatch.Elapsed.TotalMilliseconds);
                    }
                    else failed++;
                }
                catch when (!cancellationToken.IsCancellationRequested)
                {
                    failed++;
                }
            }
            return new PublishOutcome(ok, failed, timings);
        }, cancellationToken)).ToArray();
        var results = await Task.WhenAll(tasks);
        return new PublishOutcome(results.Sum(result => result.Ok),
            results.Sum(result => result.Failed),
            results.SelectMany(result => result.LatencyMs).ToArray());
    }

    private static async Task DisconnectAndDisposeAsync(IMqttClient client)
    {
        try
        {
            if (client.IsConnected) await client.DisconnectAsync();
        }
        catch
        {
            // Cleanup must not hide the measurement result or original failure.
        }
        finally
        {
            client.Dispose();
        }
    }

    private sealed record PublishOutcome(long Ok, long Failed, IReadOnlyList<double> LatencyMs);
}
