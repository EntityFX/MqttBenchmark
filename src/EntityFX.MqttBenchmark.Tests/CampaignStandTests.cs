using System.Text.Json.Nodes;
using EntityFX.MqttBenchmark.Campaign;

namespace EntityFX.MqttBenchmark.Tests;

[TestClass]
public class CampaignStandTests : IntegrationTestBase
{
    [TestMethod]
    public async Task StandSession_PropagatesTrustedBuildProvenanceToEveryCustomPull()
    {
        await using var broker = await LocalBroker.StartAsync();
        using var fixture = new CampaignStandFixture(broker.Uri);
        var inventory = JsonNode.Parse(File.ReadAllText(fixture.InventoryPath))!;
        var brokers = inventory["brokers"]!.AsArray();
        var aedes = brokers.Single(x => x!["name"]!.GetValue<string>() == "Aedes")!;
        brokers.Remove(aedes); brokers.Insert(0, aedes);
        foreach (var item in brokers) item!["loaded"] = item == aedes;
        inventory["activeBroker"] = "Aedes";
        aedes["mqttUri"] = broker.Uri.ToString();
        brokers.Single(x => x!["name"]!.GetValue<string>() == "Mosquitto")!["mqttUri"] = "mqtt://127.0.0.1:65534";
        File.WriteAllText(fixture.InventoryPath, inventory.ToJsonString());
        var trusted = Path.Combine(fixture.Path, "trusted");
        Directory.CreateDirectory(trusted);
        var inputs = new[] { ".dockerignore", "aedes/package-lock.json", "aedes/package.json", "aedes/server.js", "compose.yml", "Dockerfile.aedes" }
            .Select(path => new JsonObject { ["path"] = path, ["sha256"] = CampaignJson.HashFile(Path.Combine(fixture.Path, "docker", "brokers", path)) })
            .ToArray();
        var record = new JsonObject
        {
            ["broker"] = "Aedes", ["context"] = "default", ["image"] = aedes["image"]!.GetValue<string>(),
            ["imageId"] = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            ["buildInputs"] = new JsonArray(inputs)
        };
        File.WriteAllText(Path.Combine(trusted, "aedes.json"), record.ToJsonString());

        await using var session = await StandSession.StartAsync(fixture.Script, fixture.InventoryPath,
            new CampaignDefinition(), "Aedes", fixture.Output, fixture.Docker,
            trustedBuildProvenanceDirectory: trusted);

        Assert.AreEqual(2, Directory.GetFiles(fixture.Output, "aedes.json", SearchOption.AllDirectories).Length);
        Assert.IsFalse(File.ReadAllLines(Path.Combine(fixture.Path, "docker-calls.ndjson"))
            .Any(line => line.Contains("\"build\"")));
    }

    [TestMethod]
    public async Task StandClock_AlignedDaemonHighResolutionAvoidsWholeSecondHighRttFalseRejection()
    {
        await using var broker = await LocalBroker.StartAsync();
        using var fixture = new CampaignStandFixture(broker.Uri);
        File.WriteAllText(System.IO.Path.Combine(fixture.Path, "clock-delay"), "1100");
        File.WriteAllText(System.IO.Path.Combine(fixture.Path, "clock-whole-second-boundary"), "true");
        await using var session = await StandSession.StartAsync(fixture.Script, fixture.InventoryPath,
            new CampaignDefinition(), "Mosquitto", fixture.Output, fixture.Docker);
        var clock = JsonNode.Parse(File.ReadAllText(System.IO.Path.Combine(fixture.Output, "clock-alignment.json")))!;
        Assert.IsTrue(clock["ready"]!.GetValue<bool>());
        Assert.AreEqual(3, clock["probes"]!.AsArray().Count);
        Assert.IsTrue(clock["uncertaintyMs"]!.GetValue<double>() < 1000);
    }

    [TestMethod]
    public async Task StandClock_SelectsSmallestRttEvenWhenFirstProbeIsTooUncertain()
    {
        await using var broker = await LocalBroker.StartAsync();
        using var fixture = new CampaignStandFixture(broker.Uri);
        File.WriteAllText(System.IO.Path.Combine(fixture.Path, "clock-delay"), "1100");
        File.WriteAllText(System.IO.Path.Combine(fixture.Path, "clock-first-delay"), "2500");
        await using var session = await StandSession.StartAsync(fixture.Script, fixture.InventoryPath,
            new CampaignDefinition(), "Mosquitto", fixture.Output, fixture.Docker);
        var clock = JsonNode.Parse(File.ReadAllText(System.IO.Path.Combine(fixture.Output, "clock-alignment.json")))!;
        Assert.IsTrue(clock["ready"]!.GetValue<bool>());
        var probes = clock["probes"]!.AsArray();
        Assert.IsTrue(probes[0]!["roundTripMs"]!.GetValue<double>() > 2400);
        Assert.IsTrue(clock["uncertaintyMs"]!.GetValue<double>() < 1000, "The selected probe must not be the slow first probe.");
    }

    [DataTestMethod]
    [DataRow(195)] [DataRow(-195)]
    public async Task StandClock_RejectsMisalignmentAndRetainsOffsetEvidence(int seconds)
    {
        await using var broker = await LocalBroker.StartAsync();
        using var fixture = new CampaignStandFixture(broker.Uri);
        File.WriteAllText(System.IO.Path.Combine(fixture.Path, "clock-offset"), seconds.ToString());
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
        {
            await using var session = await StandSession.StartAsync(fixture.Script, fixture.InventoryPath,
                new CampaignDefinition(), "Mosquitto", fixture.Output, fixture.Docker);
        });
        var clock = JsonNode.Parse(File.ReadAllText(System.IO.Path.Combine(fixture.Output, "clock-alignment.json")))!;
        Assert.IsFalse(clock["ready"]!.GetValue<bool>());
        Assert.AreEqual(seconds * 1000d, clock["offsetMs"]!.GetValue<double>(), 2000);
        Assert.AreEqual(2000d, clock["thresholdMs"]!.GetValue<double>());
        Assert.AreEqual(3, clock["probes"]!.AsArray().Count);
        var state = JsonNode.Parse(File.ReadAllText(System.IO.Path.Combine(fixture.Path, "container-state.json")))!;
        Assert.AreEqual("exited", state["State"]!["Status"]!.GetValue<string>(), "An unready stand must be stopped.");
    }

    [TestMethod]
    public async Task StandClock_RejectsUncertainSlowProbeEvenWhenPointOffsetIsWithinThreshold()
    {
        await using var broker = await LocalBroker.StartAsync();
        using var fixture = new CampaignStandFixture(broker.Uri);
        File.WriteAllText(System.IO.Path.Combine(fixture.Path, "clock-delay"), "2500");
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
        {
            await using var session = await StandSession.StartAsync(fixture.Script, fixture.InventoryPath,
                new CampaignDefinition(), "Mosquitto", fixture.Output, fixture.Docker);
        });
        var clock = JsonNode.Parse(File.ReadAllText(System.IO.Path.Combine(fixture.Output, "clock-alignment.json")))!;
        Assert.IsFalse(clock["ready"]!.GetValue<bool>());
        Assert.IsTrue(Math.Abs(clock["offsetMs"]!.GetValue<double>()) < 2000);
        Assert.IsTrue(Math.Abs(clock["offsetMs"]!.GetValue<double>()) + clock["uncertaintyMs"]!.GetValue<double>() > 2000);
    }

    [TestMethod]
    public async Task TelemetryBarrier_InvokesMeasurementOnlyAfterFirstSampleWhileCaptureIsStillRunning()
    {
        await using var broker = await LocalBroker.StartAsync();
        using var fixture = new CampaignStandFixture(broker.Uri);
        File.WriteAllText(System.IO.Path.Combine(fixture.Path, "telemetry-delay"), "1500");
        await using var session = await StandSession.StartAsync(fixture.Script, fixture.InventoryPath,
            new CampaignDefinition(), "Mosquitto", fixture.Output, fixture.Docker);
        var measured = false;
        Func<CancellationToken, Task> measurement = _ =>
        {
            Assert.IsTrue(File.Exists(System.IO.Path.Combine(fixture.Output, "telemetry-ready.json")));
            Assert.IsFalse(File.Exists(System.IO.Path.Combine(fixture.Output, "sampler-ended.json")), "Sampling must still be active.");
            Assert.IsFalse(File.Exists(System.IO.Path.Combine(fixture.Output, "telemetry-manifest.json")), "Capture must still be active.");
            var first = JsonNode.Parse(File.ReadLines(System.IO.Path.Combine(fixture.Output, "telemetry.ndjson")).First())!;
            Assert.AreEqual("container-cgroup", first["cpuScope"]!.GetValue<string>());
            measured = true;
            return Task.CompletedTask;
        };
        var manifest = await session.RunWithTelemetryAsync(3, measurement);
        Assert.IsTrue(measured);
        Assert.IsTrue(File.Exists(System.IO.Path.Combine(fixture.Output, "telemetry-manifest.json")));
        var clock = JsonNode.Parse(File.ReadAllText(System.IO.Path.Combine(fixture.Output, "clock-alignment.json")))!;
        Assert.IsTrue(clock["ready"]!.GetValue<bool>());
        Assert.IsTrue(manifest.InputSha256.ContainsKey("workload-end-fence.json"));
        Assert.IsTrue(manifest.InputSha256.ContainsKey("sampler-ended.json"));
        foreach (var input in manifest.InputSha256)
            Assert.AreEqual(input.Value, CampaignJson.HashFile(System.IO.Path.Combine(fixture.Output, input.Key)),
                "Manifest must hash only finalized capture and fence artifacts.");
    }

    [TestMethod]
    public async Task TelemetryBarrier_RejectsFinishedSamplerBeforeDispatchDespiteDelayedLogs()
    {
        await using var broker = await LocalBroker.StartAsync();
        using var fixture = new CampaignStandFixture(broker.Uri);
        File.WriteAllText(System.IO.Path.Combine(fixture.Path, "logs-delay"), "3000");
        await using var session = await StandSession.StartAsync(fixture.Script, fixture.InventoryPath,
            new CampaignDefinition(), "Mosquitto", fixture.Output, fixture.Docker);
        var measured = false;
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => session.RunWithTelemetryAsync(1,
            _ => { measured = true; return Task.CompletedTask; }));
        Assert.IsFalse(measured, "A sampler with its only sample already emitted cannot cover a new workload.");
    }

    [TestMethod]
    public async Task TelemetryBarrier_RejectsSamplerEndingDuringWorkloadDespiteDelayedLogs()
    {
        await using var broker = await LocalBroker.StartAsync();
        using var fixture = new CampaignStandFixture(broker.Uri);
        File.WriteAllText(System.IO.Path.Combine(fixture.Path, "telemetry-delay"), "500");
        File.WriteAllText(System.IO.Path.Combine(fixture.Path, "logs-delay"), "3000");
        await using var session = await StandSession.StartAsync(fixture.Script, fixture.InventoryPath,
            new CampaignDefinition(), "Mosquitto", fixture.Output, fixture.Docker);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => session.RunWithTelemetryAsync(3, async ct =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            while (!File.Exists(System.IO.Path.Combine(fixture.Path, "logs-started")))
                await Task.Delay(10, timeout.Token);
            Assert.IsFalse(File.Exists(System.IO.Path.Combine(fixture.Output, "telemetry-manifest.json")),
                "Controller is still collecting logs, but sampling has already ended.");
        }));
    }

    [TestMethod]
    public async Task TelemetryBarrier_RequiresFinalSampleAfterRemoteWorkloadEndFence()
    {
        await using var broker = await LocalBroker.StartAsync();
        using var fixture = new CampaignStandFixture(broker.Uri);
        File.WriteAllText(System.IO.Path.Combine(fixture.Path, "telemetry-delay"), "500");
        // The remote completion fence is beyond the last sample even though the controller
        // has not received that last sample yet (e.g. transport buffering).
        File.WriteAllText(System.IO.Path.Combine(fixture.Path, "fence-uptime"), "103.0");
        await using var session = await StandSession.StartAsync(fixture.Script, fixture.InventoryPath,
            new CampaignDefinition(), "Mosquitto", fixture.Output, fixture.Docker);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => session.RunWithTelemetryAsync(3, _ => Task.CompletedTask));
    }

    [TestMethod]
    public async Task TelemetryBarrier_DoesNotMeasureWhenSamplerFailsBeforeFirstSample()
    {
        await using var broker = await LocalBroker.StartAsync();
        using var fixture = new CampaignStandFixture(broker.Uri);
        File.WriteAllText(System.IO.Path.Combine(fixture.Path, "telemetry-fail"), "true");
        await using var session = await StandSession.StartAsync(fixture.Script, fixture.InventoryPath,
            new CampaignDefinition(), "Mosquitto", fixture.Output, fixture.Docker);
        var measured = false;
        Func<CancellationToken, Task> measurement = _ => { measured = true; return Task.CompletedTask; };
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            await session.RunWithTelemetryAsync(3, measurement));
        Assert.IsFalse(measured);
    }

    [TestMethod]
    public async Task TelemetryBarrier_CancellationBeforeReadinessNeverInvokesMeasurement()
    {
        await using var broker = await LocalBroker.StartAsync();
        using var fixture = new CampaignStandFixture(broker.Uri);
        File.WriteAllText(System.IO.Path.Combine(fixture.Path, "telemetry-delay"), "5000");
        await using var session = await StandSession.StartAsync(fixture.Script, fixture.InventoryPath,
            new CampaignDefinition(), "Mosquitto", fixture.Output, fixture.Docker);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var measured = false;
        try
        {
            await session.RunWithTelemetryAsync(3, _ => { measured = true; return Task.CompletedTask; }, cancel.Token);
            Assert.Fail("Cancelled capture must not admit measurement.");
        }
        catch (OperationCanceledException) { }
        Assert.IsFalse(measured);
        Assert.IsFalse(File.Exists(System.IO.Path.Combine(fixture.Output, "measurement-started.json")));
    }

    [TestMethod]
    public async Task StandAdapter_UsesApprovedControllerAndRecordsRealProtocolEnvironmentAndTelemetry()
    {
        await using var broker = await LocalBroker.StartAsync();
        using var fixture = new CampaignStandFixture(broker.Uri);
        await using var session = await StandSession.StartAsync(fixture.Script, fixture.InventoryPath,
            new CampaignDefinition(), "Mosquitto", fixture.Output, fixture.Docker);
        var protocol = await new MqttCampaignRunner(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(20))
            .PreflightAsync("Mosquitto", session.Endpoint);
        var telemetry = await session.CaptureAsync(1);
        var report = PreflightReport.Create(CampaignArtifactTests.Identity(), new[] { new BrokerPreflight(protocol, telemetry) });
        CampaignJson.WriteNew(System.IO.Path.Combine(fixture.Output, "preflight.json"), report);
        Assert.IsTrue(report.Success);
        Assert.AreEqual(1, telemetry.SampleCount);
        Assert.AreEqual("host-shared", telemetry.NetworkScope);
        Assert.AreEqual("container-cgroup", telemetry.CpuScope);
        Assert.IsTrue(telemetry.InputSha256.ContainsKey("run-provenance.json"));
        Assert.IsTrue(telemetry.InputSha256.ContainsKey("versions.json"));
        Assert.IsTrue(telemetry.InputSha256.ContainsKey("config-hashes.json"));
        Assert.IsTrue(report.Environment.ProcessorCount > 0);
        Assert.IsFalse(string.IsNullOrWhiteSpace(report.Environment.OsDescription));
        Assert.ThrowsException<IOException>(() => CampaignJson.WriteNew(System.IO.Path.Combine(fixture.Output, "preflight.json"), report));
    }

    [TestMethod]
    public async Task StandAdapter_DoesNotRepairInvalidSourceInventoryBeforeControllerValidation()
    {
        await using var broker = await LocalBroker.StartAsync();
        using var fixture = new CampaignStandFixture(broker.Uri);
        var inventory = JsonNode.Parse(File.ReadAllText(fixture.InventoryPath))!;
        inventory["brokers"]![1]!["loaded"] = true;
        File.WriteAllText(fixture.InventoryPath, inventory.ToJsonString());
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => StandSession.StartAsync(fixture.Script, fixture.InventoryPath,
            new CampaignDefinition(), "Mosquitto", fixture.Output, fixture.Docker));
        Assert.IsFalse(File.Exists(System.IO.Path.Combine(fixture.Path, "docker-calls.ndjson")));
    }

    [TestMethod]
    public async Task StandCapture_RefusesSecondCaptureBeforeChangingAnyArtifact()
    {
        await using var broker = await LocalBroker.StartAsync();
        using var fixture = new CampaignStandFixture(broker.Uri);
        await using var session = await StandSession.StartAsync(fixture.Script, fixture.InventoryPath,
            new CampaignDefinition(), "Mosquitto", fixture.Output, fixture.Docker);
        await session.CaptureAsync(1);
        var path = System.IO.Path.Combine(fixture.Output, "telemetry.ndjson");
        var hash = CampaignJson.HashFile(path);
        await Assert.ThrowsExceptionAsync<IOException>(() => session.CaptureAsync(1));
        Assert.AreEqual(hash, CampaignJson.HashFile(path), "A rejected retry must preserve the original telemetry bytes.");
    }

    [TestMethod]
    public async Task StandAdapter_PreservesSourceCampaignMismatchForControllerToReject()
    {
        await using var broker = await LocalBroker.StartAsync();
        using var fixture = new CampaignStandFixture(broker.Uri);
        var inventory = JsonNode.Parse(File.ReadAllText(fixture.InventoryPath))!;
        inventory["campaign"] = new JsonObject { ["deploymentMode"] = "distributedHosts", ["cpuMode"] = "singleCorePinned" };
        File.WriteAllText(fixture.InventoryPath, inventory.ToJsonString());
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
        {
            await using var session = await StandSession.StartAsync(fixture.Script, fixture.InventoryPath,
                new CampaignDefinition(), "Mosquitto", fixture.Output, fixture.Docker);
        });
        Assert.IsFalse(File.Exists(System.IO.Path.Combine(fixture.Path, "docker-calls.ndjson")));
    }

    [TestMethod]
    public async Task StandAdapter_StartsCachedImmutableRegistryImageWhenRegistryIsOffline()
    {
        await using var broker = await LocalBroker.StartAsync();
        using var fixture = new CampaignStandFixture(broker.Uri);
        var docker = File.ReadAllText(fixture.Docker).Replace("$joined = $a -join ' '",
            "$joined = $a -join ' '\nif ($a[0] -eq 'pull') { throw 'Registry is offline; the pinned image is already cached.' }");
        File.WriteAllText(fixture.Docker, docker);
        await using var session = await StandSession.StartAsync(fixture.Script, fixture.InventoryPath,
            new CampaignDefinition(), "Mosquitto", fixture.Output, fixture.Docker);
        var telemetry = await session.CaptureAsync(1);
        Assert.AreEqual(1, telemetry.SampleCount);
        Assert.IsTrue(telemetry.InputSha256.ContainsKey("run-provenance.json"));
    }

    [DataTestMethod]
    [DataRow("singleHostSequential")] [DataRow("distributedHosts")]
    public async Task ContextLease_LocksActualControllerContextDespiteUnusedBrokerOverrides(string mode)
    {
        await using var broker = await LocalBroker.StartAsync();
        using var first = new CampaignStandFixture(broker.Uri);
        using var second = new CampaignStandFixture(broker.Uri);
        var context = "shared-" + Guid.NewGuid().ToString("N");
        SetContexts(first, mode, mode == "singleHostSequential" ? context : "unused-root-a",
            mode == "distributedHosts" ? context : "unused-selected-a", "unused-a");
        SetContexts(second, mode, mode == "singleHostSequential" ? context : "unused-root-b",
            mode == "distributedHosts" ? context : "unused-selected-b", "unused-b");
        var config = new CampaignDefinition { DeploymentMode = mode };
        await using var active = await StandSession.StartAsync(first.Script, first.InventoryPath, config,
            "Mosquitto", first.Output, first.Docker);
        await Assert.ThrowsExceptionAsync<IOException>(async () =>
        {
            await using var overlap = await StandSession.StartAsync(second.Script, second.InventoryPath, config,
                "Mosquitto", second.Output, second.Docker);
        });
        Assert.IsFalse(File.Exists(System.IO.Path.Combine(second.Path, "docker-calls.ndjson")));
        Assert.IsFalse(Directory.Exists(second.Output));
    }

    [TestMethod]
    public async Task ContextLease_DoesNotLockUnusedContextsForIndependentDistributedBrokers()
    {
        await using var broker = await LocalBroker.StartAsync();
        using var first = new CampaignStandFixture(broker.Uri);
        using var second = new CampaignStandFixture(broker.Uri);
        SetContexts(first, "distributedHosts", "unused-root", "target-a", "same-unused");
        SetContexts(second, "distributedHosts", "unused-root", "target-b", "same-unused");
        var config = new CampaignDefinition { DeploymentMode = "distributedHosts" };
        await using var active = await StandSession.StartAsync(first.Script, first.InventoryPath, config,
            "Mosquitto", first.Output, first.Docker);
        await using var independent = await StandSession.StartAsync(second.Script, second.InventoryPath, config,
            "Mosquitto", second.Output, second.Docker);
        Assert.IsTrue(File.Exists(System.IO.Path.Combine(first.Output, "run-provenance.json")));
        Assert.IsTrue(File.Exists(System.IO.Path.Combine(second.Output, "run-provenance.json")));
    }

    private static void SetContexts(CampaignStandFixture fixture, string mode, string root, string selected, string unused)
    {
        var inventory = JsonNode.Parse(File.ReadAllText(fixture.InventoryPath))!;
        inventory["deploymentMode"] = mode;
        inventory["dockerContext"] = root;
        foreach (var item in inventory["brokers"]!.AsArray())
            item!["dockerContext"] = item["name"]!.GetValue<string>() == "Mosquitto" ? selected : unused + item["name"]!.GetValue<string>();
        File.WriteAllText(fixture.InventoryPath, inventory.ToJsonString());
    }

    [DataTestMethod]
    [DataRow("endpoint")] [DataRow("cpuset")] [DataRow("digest")]
    public async Task StandIdentity_DriftBetweenAttemptsFailsBeforeAnyDeployment(string changedField)
    {
        await using var broker = await LocalBroker.StartAsync();
        await using var changedBroker = await LocalBroker.StartAsync();
        using var fixture = new CampaignStandFixture(broker.Uri);
        using var campaignDirectory = new CampaignTemp();
        var config = new CampaignDefinition();
        var identity = CampaignArtifactTests.Identity() with { StandSha256 = CampaignJson.HashFile(fixture.InventoryPath) };
        var keys = config.Expand().Where(x => x.Broker == "Mosquitto").Take(2).ToArray();
        var dockerCalls = System.IO.Path.Combine(fixture.Path, "docker-calls.ndjson");
        var callsAfterFirst = 0;
        using (var journal = CampaignJournal.Open(campaignDirectory.Path, identity, false))
        {
            await Assert.ThrowsExceptionAsync<CampaignExhaustedException>(() => journal.ExecuteAsync(keys, async (key, path, attempt, ct) =>
            {
                await using (var session = await StandSession.StartAsync(fixture.Script, fixture.InventoryPath, config, key.Broker,
                    System.IO.Path.Combine(path, "stand"), fixture.Docker, ct, expectedStandSha256: identity.StandSha256))
                    Assert.IsNotNull(session.Endpoint);
                if (key == keys[0])
                {
                    callsAfterFirst = File.ReadAllLines(dockerCalls).Length;
                    var source = JsonNode.Parse(File.ReadAllText(fixture.InventoryPath))!;
                    var selected = source["brokers"]![0]!;
                    if (changedField == "endpoint") selected["mqttUri"] = changedBroker.Uri.ToString();
                    else if (changedField == "cpuset") selected["cpuset"] = "1";
                    else selected["digest"] = "sha256:fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";
                    File.WriteAllText(fixture.InventoryPath, source.ToJsonString());
                }
                return CampaignArtifactTests.Observation(key);
            }));
        }
        var rows = CampaignJournal.ReadAttempts(campaignDirectory.Path);
        Assert.AreEqual(1, rows.Count(x => x.Status == "success"));
        Assert.AreEqual(3, rows.Count(x => x.Status == "failed" && x.Key == keys[1]));
        Assert.AreEqual(callsAfterFirst, File.ReadAllLines(dockerCalls).Length, "Changed input must fail before calling Docker.");
        foreach (var path in Directory.GetDirectories(System.IO.Path.Combine(campaignDirectory.Path, "attempts", keys[1].Key)))
            Assert.IsFalse(Directory.Exists(System.IO.Path.Combine(path, "stand")));
    }

    [TestMethod]
    public async Task StandIdentity_ControllerValidatesTheExactHashedInputBytes()
    {
        await using var broker = await LocalBroker.StartAsync();
        using var fixture = new CampaignStandFixture(broker.Uri);
        var bytes = File.ReadAllBytes(fixture.InventoryPath);
        var hash = CampaignJson.HashFile(fixture.InventoryPath);
        await using var session = await StandSession.StartAsync(fixture.Script, fixture.InventoryPath, new CampaignDefinition(),
            "Mosquitto", fixture.Output, fixture.Docker, expectedStandSha256: hash);
        var snapshot = System.IO.Path.Combine(fixture.Output, "inventory-check", "stand.source.json");
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(snapshot));
        Assert.AreEqual(hash, CampaignJson.HashFile(snapshot));
    }
}

internal sealed class CampaignStandFixture : IDisposable
{
    private readonly CampaignTemp temp = new();
    public string Path => temp.Path;
    public string Script => System.IO.Path.Combine(Path, "BrokerStand.ps1");
    public string InventoryPath => System.IO.Path.Combine(Path, "stand.json");
    public string Docker => System.IO.Path.Combine(Path, "docker.ps1");
    public string Output => System.IO.Path.Combine(Path, "output");
    public CampaignStandFixture(Uri endpoint)
    {
        Directory.CreateDirectory(Path);
        CopyTree(System.IO.Path.Combine(AppContext.BaseDirectory, "docker"), System.IO.Path.Combine(Path, "docker"));
        File.Copy(System.IO.Path.Combine(AppContext.BaseDirectory, "BrokerStand.ps1"), Script);
        File.Copy(System.IO.Path.Combine(AppContext.BaseDirectory, "TestData", "StrictDocker.ps1"), Docker);
        var source = JsonNode.Parse(File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "broker-stand.v1.json")))!;
        var brokers = source["brokers"]!.AsArray();
        var selected = brokers.Single(x => x!["name"]!.GetValue<string>() == "Mosquitto")!;
        brokers.Remove(selected); brokers.Insert(0, selected);
        selected["mqttUri"] = endpoint.ToString();
        File.WriteAllText(InventoryPath, source.ToJsonString());
    }
    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source)) File.Copy(file, System.IO.Path.Combine(target, System.IO.Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(source)) CopyTree(dir, System.IO.Path.Combine(target, System.IO.Path.GetFileName(dir)));
    }
    public void Dispose() => temp.Dispose();
}
