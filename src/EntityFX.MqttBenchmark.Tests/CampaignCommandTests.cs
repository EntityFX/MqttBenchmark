using EntityFX.MqttBenchmark.Campaign;
using System.Diagnostics;

namespace EntityFX.MqttBenchmark.Tests;

[TestClass]
public class CampaignCommandTests
{
    [TestMethod]
    public async Task PreflightCommand_PropagatesTrustedBuildProvenanceDirectory()
    {
        await using var broker = await LocalBroker.StartAsync();
        using var fixture = new CampaignStandFixture(broker.Uri);
        InitializeRepository(fixture.Path);
        var inventory = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(fixture.InventoryPath))!;
        var brokers = inventory["brokers"]!.AsArray();
        var aedes = brokers.Single(x => x!["name"]!.GetValue<string>() == "Aedes")!;
        brokers.Remove(aedes); brokers.Insert(0, aedes);
        foreach (var item in brokers) item!["loaded"] = item == aedes;
        inventory["activeBroker"] = "Aedes";
        aedes["mqttUri"] = broker.Uri.ToString();
        brokers.Single(x => x!["name"]!.GetValue<string>() == "Mosquitto")!["mqttUri"] = "mqtt://127.0.0.1:65534";
        File.WriteAllText(fixture.InventoryPath, inventory.ToJsonString());
        var configPath = Path.Combine(fixture.Path, "campaign.json");
        CampaignJson.WriteNew(configPath, new CampaignDefinition());
        var output = Path.Combine(fixture.Path, "preflight");
        var missingTrustedDirectory = Path.Combine(fixture.Path, "missing-trusted");
        var options = new Dictionary<string, string?>
        {
            ["config"] = configPath, ["stand"] = fixture.InventoryPath, ["output"] = output, ["broker"] = "Aedes",
            ["stand-script"] = fixture.Script, ["docker-executable"] = fixture.Docker,
            ["benchmark-repo"] = fixture.Path, ["trusted-build-provenance"] = missingTrustedDirectory
        };

        Assert.AreEqual(2, await CampaignCommands.ExecuteAsync("preflight", options));
        var report = CampaignJson.Read<PreflightReport>(Path.Combine(output, "preflight.json"));
        StringAssert.Contains(report.Brokers.Single().Protocol.RttError!, "Trusted build provenance is missing");
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Path, "docker-calls.ndjson")),
            "A missing trust record must fail before Docker and must not silently fall back to build.");
    }

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

    [TestMethod]
    public async Task MatrixCommand_PropagatesIdentityAndRefusesStandMutationAfterFirstRun()
    {
        await using var broker = await LocalBroker.StartAsync();
        using var fixture = new CampaignStandFixture(broker.Uri);
        File.WriteAllText(Path.Combine(fixture.Path, "telemetry-delay"), "200");
        InitializeRepository(fixture.Path);
        var originalHash = CampaignJson.HashFile(fixture.InventoryPath);
        var docker = File.ReadAllText(fixture.Docker).Replace(
            "$compose = Get-Content $a[2] -Raw | ConvertFrom-Json -AsHashtable",
            "$compose = Get-Content $a[2] -Raw | ConvertFrom-Json -AsHashtable\n" +
            "    $inventory.brokers[0].cpuset = '1'\n" +
            "    $inventory | ConvertTo-Json -Depth 30 | Set-Content (Join-Path $PSScriptRoot 'stand.json')");
        File.WriteAllText(fixture.Docker, docker);
        var configPath = Path.Combine(fixture.Path, "campaign.json");
        CampaignJson.WriteNew(configPath, new CampaignDefinition { WarmupSeconds = .05, MeasurementSeconds = .2,
            CooldownSeconds = 0, QuietPeriodSeconds = .05, DrainTimeoutSeconds = .1 });
        var campaignPath = Path.Combine(fixture.Path, "campaign");
        var options = new Dictionary<string, string?> { ["config"] = configPath, ["stand"] = fixture.InventoryPath,
            ["campaign"] = campaignPath, ["broker"] = "Mosquitto", ["max-runs"] = "2", ["stand-script"] = fixture.Script,
            ["docker-executable"] = fixture.Docker, ["benchmark-repo"] = fixture.Path };
        await Assert.ThrowsExceptionAsync<CampaignExhaustedException>(() => CampaignCommands.ExecuteAsync("matrix", options,
            loadCountersFactory: _ => new LoadGeneratorGuardTests.TestCounters()));
        var rows = CampaignJournal.ReadAttempts(campaignPath);
        Assert.AreEqual(1, rows.Count(x => x.Status == "success"));
        Assert.AreEqual(3, rows.Count(x => x.Status == "failed"));
        Assert.IsTrue(rows.All(x => x.Identity.StandSha256 == originalHash));
        Assert.AreEqual(1, Directory.GetFiles(campaignPath, "run-provenance.json", SearchOption.AllDirectories).Length);
        Assert.AreEqual(originalHash, CampaignJson.HashFile(Directory.GetFiles(campaignPath, "stand.source.json", SearchOption.AllDirectories).Single()));
        var localTelemetry = Directory.GetFiles(campaignPath, "load-generator-summary.json", SearchOption.AllDirectories);
        Assert.AreEqual(1, localTelemetry.Length, "Every measured attempt must have load-generator acceptance evidence.");
        var guard = CampaignJson.Read<LoadGeneratorReport>(localTelemetry[0]);
        Assert.IsTrue(guard.Assessment.Success);
        var observation = rows.Single(x => x.Status == "success").Observation!;
        Assert.AreEqual(observation.MeasurementStartedUtc, guard.Measurement!.StartedUtc);
        Assert.AreEqual(observation.MeasurementEndedUtc, guard.Measurement.EndedUtc);
    }

    [TestMethod]
    public async Task MatrixCommand_RetriesCpuOverloadWithoutClaimingSuccess_AndSealsLocalEvidence()
    {
        await using var broker = await LocalBroker.StartAsync();
        using var fixture = new CampaignStandFixture(broker.Uri);
        File.WriteAllText(Path.Combine(fixture.Path, "telemetry-delay"), "200");
        InitializeRepository(fixture.Path);
        var configPath = Path.Combine(fixture.Path, "campaign.json");
        CampaignJson.WriteNew(configPath, new CampaignDefinition { WarmupSeconds = .05, MeasurementSeconds = .2,
            CooldownSeconds = 0, QuietPeriodSeconds = .05, DrainTimeoutSeconds = .1 });
        var campaignPath = Path.Combine(fixture.Path, "campaign");
        var options = new Dictionary<string, string?> { ["config"] = configPath, ["stand"] = fixture.InventoryPath,
            ["campaign"] = campaignPath, ["broker"] = "Mosquitto", ["max-runs"] = "1", ["stand-script"] = fixture.Script,
            ["docker-executable"] = fixture.Docker, ["benchmark-repo"] = fixture.Path };
        await Assert.ThrowsExceptionAsync<CampaignExhaustedException>(() => CampaignCommands.ExecuteAsync("matrix", options,
            loadCountersFactory: _ => new LoadGeneratorGuardTests.TestCounters(overloaded: true)));
        var rows = CampaignJournal.ReadAttempts(campaignPath); // Verifies every sealed artifact hash too.
        Assert.AreEqual(3, rows.Count);
        Assert.IsTrue(rows.All(x => x.Status == "failed" && x.Observation == null && x.Failure!.Contains("Load-generator guard failed")));
        var files = Directory.GetFiles(campaignPath, "load-generator-summary.json", SearchOption.AllDirectories);
        Assert.AreEqual(3, files.Length);
        foreach (var file in files)
        {
            var report = CampaignJson.Read<LoadGeneratorReport>(file);
            Assert.IsFalse(report.Assessment.Success);
            Assert.AreEqual(80d, report.Assessment.Intervals.Max(x => x.CpuPercent));
        }
    }

    private static void InitializeRepository(string path)
    {
        foreach (var arguments in new[] { new[] { "init" }, new[] { "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
            "-c", "commit.gpgsign=false", "commit", "--allow-empty", "-m", "Fixture" } })
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = path, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd(); process.WaitForExit();
            Assert.AreEqual(0, process.ExitCode, output + error);
        }
    }
}
