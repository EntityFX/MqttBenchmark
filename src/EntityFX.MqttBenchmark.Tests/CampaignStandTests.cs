using System.Text.Json.Nodes;
using EntityFX.MqttBenchmark.Campaign;

namespace EntityFX.MqttBenchmark.Tests;

[TestClass]
public class CampaignStandTests
{
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
