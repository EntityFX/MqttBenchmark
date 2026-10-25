using System.Text.Json;
using EntityFX.MqttBenchmark.Campaign;

namespace EntityFX.MqttBenchmark.Tests;

[TestClass]
public class PublicationCohortTests
{
    [DataTestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public async Task ExactSubmissionBoundary_WarmupIds_AndConnectionStatesAreRecorded(int qos)
    {
        await using var broker = await LocalBroker.StartAsync();
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
        Assert.AreEqual(0L, observation.Measurement.UnexpectedDeliveries, "Warmup receipts cannot contaminate the measured ledger.");
    }
}
