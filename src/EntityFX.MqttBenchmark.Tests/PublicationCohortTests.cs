using System.Text.Json;
using EntityFX.MqttBenchmark.Campaign;

namespace EntityFX.MqttBenchmark.Tests;

[TestClass]
public class PublicationCohortTests
{
    [TestMethod]
    public async Task FixedObservationHorizon_DoesNotStopAtEarlyDeliveryOrQuietPeriod()
    {
        await using var broker = await LocalBroker.StartAsync();
        using var temp = new CampaignTemp();
        var config = new CampaignDefinition { RecordPublicationCohort = true, ObserveFullDrainWindow = true,
            WarmupSeconds = .01, MeasurementSeconds = .03, CooldownSeconds = 0,
            QuietPeriodSeconds = .01, DrainTimeoutSeconds = .15 };
        var observation = await new MqttCampaignRunner(TimeSpan.FromSeconds(2)).RunAsync(config,
            new CampaignKey("Mosquitto", 16, 1, 1, 1), broker.Uri, "horizon-test", "one", temp.Path, .5);
        using var phases = JsonDocument.Parse(File.ReadAllText(Path.Combine(temp.Path, "phases.json")));
        var end = phases.RootElement.GetProperty("measurementEndSeconds").GetDouble();
        Assert.AreEqual(end + .15, phases.RootElement.GetProperty("drainEndSeconds").GetDouble(), 1e-12);
        Assert.IsTrue(phases.RootElement.GetProperty("observationStoppedSeconds").GetDouble() >= end + .15);
        Assert.AreEqual("fixedObservationHorizon", observation.DrainReason);
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public async Task ExactSubmissionBoundary_WarmupIds_AndConnectionStatesAreRecorded(int qos)
    {
        await using var broker = await LocalBroker.StartAsync();
        var connectedIds = new System.Collections.Concurrent.ConcurrentBag<string>();
        broker.Server.ClientConnectedAsync += args =>
        {
            connectedIds.Add(args.ClientId);
            return Task.CompletedTask;
        };
        using var temp = new CampaignTemp();
        var config = new CampaignDefinition { RecordPublicationCohort = true,
            WarmupSeconds = .03, MeasurementSeconds = .08, CooldownSeconds = 0,
            QuietPeriodSeconds = .02, DrainTimeoutSeconds = .2 };
        var observation = await new MqttCampaignRunner(TimeSpan.FromSeconds(2)).RunAsync(config,
            new CampaignKey("Mosquitto", 16, qos, 2, 1), broker.Uri, "cohort-test", "one", temp.Path, .5);
        using var phase = JsonDocument.Parse(File.ReadAllText(Path.Combine(temp.Path, "phases.json")));
        var start = phase.RootElement.GetProperty("measurementStartSeconds").GetDouble();
        var end = phase.RootElement.GetProperty("measurementEndSeconds").GetDouble();
        Assert.AreEqual(.08, end - start, 1e-12);
        Assert.IsTrue(phase.RootElement.GetProperty("completed").GetBoolean());
        Assert.IsTrue(phase.RootElement.GetProperty("drainEndSeconds").GetDouble() >= end);
        var events = File.ReadAllLines(Path.Combine(temp.Path, "publishes.ndjson"))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        var attempts = events.Where(row => row.GetProperty("event").GetString() == "attempt").ToArray();
        Assert.IsTrue(attempts.Any(row => row.GetProperty("elapsedSeconds").GetDouble() < start), "Warmup must be observable.");
        var measured = attempts.Where(row => row.GetProperty("elapsedSeconds").GetDouble() >= start).ToArray();
        Assert.IsTrue(measured.All(row => row.GetProperty("elapsedSeconds").GetDouble() < end));
        Assert.AreEqual(observation.Measurement.AttemptedPublishes, (long)measured.Length);
        Assert.AreEqual(attempts.Length, attempts.Select(row => row.GetProperty("id").GetString()).Distinct().Count());
        Assert.IsTrue(File.ReadAllText(Path.Combine(temp.Path, "connections.ndjson")).Contains("subscriber"));
        var connections = File.ReadAllLines(Path.Combine(temp.Path, "connections.ndjson"))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        var subscriberEvents = connections.Where(row => row.GetProperty("client").GetString() == "subscriber").ToArray();
        var subscriberId = subscriberEvents[0].GetProperty("clientId").GetString();
        Assert.IsFalse(string.IsNullOrWhiteSpace(subscriberId));
        Assert.IsTrue(connectedIds.Contains(subscriberId!), "The log must identify the actual MQTT client seen by the broker.");
        Assert.IsTrue(subscriberEvents.All(row => row.GetProperty("clientId").GetString() == subscriberId));
        var subscriptions = File.ReadAllLines(Path.Combine(temp.Path, "subscriptions.ndjson"))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        Assert.AreEqual(2, subscriptions.Length, "Both measured and warmup subscriptions must be confirmed.");
        Assert.IsTrue(subscriptions.All(row => row.GetProperty("clientId").GetString() == subscriberId
            && row.GetProperty("client").GetString() == "subscriber"
            && row.GetProperty("requestedQos").GetInt32() == qos
            && row.GetProperty("grantedQos").GetInt32() == qos
            && row.GetProperty("elapsedSeconds").GetDouble() < start));
        Assert.AreEqual(1, subscriptions.Count(row => row.GetProperty("topic").GetString()!.EndsWith("/warmup")));
        Assert.AreEqual(0L, observation.Measurement.UnexpectedDeliveries, "Warmup receipts cannot contaminate the measured ledger.");
    }
}
