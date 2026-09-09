using EntityFX.MqttBenchmark.Campaign;

namespace EntityFX.MqttBenchmark.Tests;

[TestClass]
public class CampaignCommandTests
{
    [TestMethod]
    public async Task AggregateCommand_PublishesV3OnlyForSealedCompleteCampaignAndRefusesOverwrite()
    {
        using var temp = new CampaignTemp();
        var config = new CampaignDefinition();
        var configPath = Path.Combine(temp.Path, "config.json");
        CampaignJson.WriteNew(configPath, config);
        var identity = CampaignArtifactTests.Identity() with { ConfigSha256 = CampaignJson.HashFile(configPath) };
        var raw = Path.Combine(temp.Path, "raw");
        using (var journal = CampaignJournal.Open(raw, identity, false))
            await journal.ExecuteAsync(config.Expand(), (key, path, number, ct) => Task.FromResult(CampaignArtifactTests.Observation(key)));
        var output = Path.Combine(temp.Path, "aggregate");
        var options = new Dictionary<string, string?> { ["config"] = configPath, ["raw"] = raw, ["output"] = output };
        Assert.AreEqual(0, await CampaignCommands.ExecuteAsync("aggregate", options));
        var doc = CampaignJson.Read<BrokerObservations>(Path.Combine(output, "broker-observations.v3.json"));
        Assert.AreEqual(288, doc.RunCount);
        Assert.IsTrue(doc.Provenance.InputSha256.Count >= 288);
        await Assert.ThrowsExceptionAsync<IOException>(() => CampaignCommands.ExecuteAsync("aggregate", options));
        config.MeasurementSeconds = 1;
        File.WriteAllText(configPath, System.Text.Json.JsonSerializer.Serialize(config, CampaignJson.Options));
        options["output"] = Path.Combine(temp.Path, "other");
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => CampaignCommands.ExecuteAsync("aggregate", options));
    }

    [TestMethod]
    public async Task MatrixDryRun_UsesV3ConfigAndRejectsUnknownBrokerWithoutDeploying()
    {
        using var temp = new CampaignTemp();
        var config = Path.Combine(temp.Path, "config.json");
        CampaignJson.WriteNew(config, new CampaignDefinition());
        var options = new Dictionary<string, string?> { ["config"] = config, ["stand"] = "does-not-exist",
            ["campaign"] = Path.Combine(temp.Path, "campaign"), ["dry-run"] = null, ["max-runs"] = "1" };
        Assert.AreEqual(0, await CampaignCommands.ExecuteAsync("matrix", options));
        Assert.IsFalse(Directory.Exists(options["campaign"]));
        options["broker"] = "unknown";
        await Assert.ThrowsExceptionAsync<ArgumentException>(() => CampaignCommands.ExecuteAsync("matrix", options));
    }
}
