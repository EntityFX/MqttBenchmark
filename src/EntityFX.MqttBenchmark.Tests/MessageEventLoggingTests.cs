using System.Collections.Concurrent;
using System.Text.Json;
using EntityFX.MqttBenchmark.Campaign;

namespace EntityFX.MqttBenchmark.Tests;

[TestClass]
public class MessageEventLoggingTests
{
    private static void SetLogging(CampaignDefinition config, bool enabled)
    {
        var setting = typeof(CampaignDefinition).GetProperty("WriteMessageEventLogs");
        Assert.IsNotNull(setting, "A recording-only switch must exist independently of RecordPublicationCohort.");
        setting!.SetValue(config, enabled);
    }

    [DataTestMethod]
    [DataRow(true, 1)]
    [DataRow(false, 1)]
    [DataRow(true, 2)]
    [DataRow(false, 2)]
    public async Task RecordingOnlySwitch_PreservesActualMqttIdsSubscriptionsAndExactPhases(bool enabled, int qos)
    {
        await using var broker = await LocalBroker.StartAsync();
        using var temp = new CampaignTemp();
        var measured = new ConcurrentDictionary<string, bool>();
        var warmup = new ConcurrentDictionary<string, bool>();
        broker.Server.InterceptingPublishAsync += args =>
        {
            var message = args.ApplicationMessage;
            if (message.Topic.StartsWith("mqttbenchmark/"))
            {
                Assert.AreEqual(16, message.PayloadSegment.Count);
                (message.Topic.EndsWith("/warmup") ? warmup : measured)
                    .TryAdd(IdentifiedPayload.ReadId(message.PayloadSegment.AsSpan()), true);
            }
            return Task.CompletedTask;
        };
        var config = new CampaignDefinition { RecordPublicationCohort = true, ObserveFullDrainWindow = true,
            WarmupSeconds = .05, MeasurementSeconds = .1, DrainTimeoutSeconds = .2,
            QuietPeriodSeconds = .01, CooldownSeconds = 0 };
        SetLogging(config, enabled);
        var observation = await new MqttCampaignRunner(TimeSpan.FromSeconds(2)).RunAsync(config,
            new CampaignKey("Mosquitto", 16, qos, 1, 1), broker.Uri, "logging-test", "one", temp.Path, .5);
        Assert.IsTrue(warmup.Count > 0);
        Assert.IsTrue(measured.Count > 0);
        Assert.AreEqual((long)measured.Count, observation.Measurement.AttemptedPublishes);
        Assert.AreEqual((long)measured.Count, observation.Measurement.CompletedPublishes);
        Assert.AreEqual((long)measured.Count, observation.Measurement.CompletedIdsDelivered);
        Assert.AreEqual(0L, observation.Measurement.FailedPublishes);
        Assert.AreEqual(0L, observation.Measurement.UnexpectedDeliveries);
        Assert.AreEqual("fixedObservationHorizon", observation.DrainReason);
        for (var sequence = 0; sequence < measured.Count; sequence++)
            Assert.IsTrue(measured.ContainsKey(IdentifiedPayload.ReadId(
                IdentifiedPayload.Create(16, "logging-test", "one", 0, sequence))));
        Assert.IsFalse(measured.Keys.Intersect(warmup.Keys).Any());
        using var phases = JsonDocument.Parse(File.ReadAllText(Path.Combine(temp.Path, "phases.json")));
        var phase = phases.RootElement;
        Assert.AreEqual(enabled, phase.GetProperty("messageEventLogsEnabled").GetBoolean());
        Assert.AreEqual(.1, phase.GetProperty("measurementEndSeconds").GetDouble()
            - phase.GetProperty("measurementStartSeconds").GetDouble(), 1e-12);
        Assert.AreEqual(.2, phase.GetProperty("drainEndSeconds").GetDouble()
            - phase.GetProperty("measurementEndSeconds").GetDouble(), 1e-12);
        var subscriptions = File.ReadAllLines(Path.Combine(temp.Path, "subscriptions.ndjson"))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        Assert.AreEqual(2, subscriptions.Length);
        Assert.IsTrue(subscriptions.All(row => row.GetProperty("grantedQos").GetInt32() == qos));
        Assert.AreEqual(1, subscriptions.Count(row => row.GetProperty("topic").GetString()!.EndsWith("/warmup")));
        Assert.IsTrue(File.ReadAllText(Path.Combine(temp.Path, "connections.ndjson")).Contains("subscriber"));
        Assert.AreEqual(enabled, File.Exists(Path.Combine(temp.Path, "publishes.ndjson")));
        Assert.AreEqual(enabled, File.Exists(Path.Combine(temp.Path, "deliveries.ndjson")));
        if (enabled)
        {
            var attempts = File.ReadAllLines(Path.Combine(temp.Path, "publishes.ndjson"))
                .Select(line => JsonDocument.Parse(line).RootElement.Clone())
                .Where(row => row.GetProperty("event").GetString() == "attempt").ToArray();
            Assert.AreEqual(measured.Count + warmup.Count, attempts.Length);
        }
    }

    [TestMethod]
    public void RecordingIsOnByDefault_AndCannotBeDisabledInLegacyOrEarlyDrainMode()
    {
        var config = new CampaignDefinition();
        var setting = typeof(CampaignDefinition).GetProperty("WriteMessageEventLogs");
        Assert.IsNotNull(setting);
        Assert.AreEqual(true, setting!.GetValue(config));
        SetLogging(config, false);
        Assert.ThrowsException<InvalidDataException>(config.Validate);
        config.RecordPublicationCohort = true;
        Assert.ThrowsException<InvalidDataException>(config.Validate);
        config.ObserveFullDrainWindow = true;
        config.Validate();
    }
}
