using System.Text.Json;
using System.Text.Json.Nodes;
using EntityFX.MqttBenchmark.Campaign;

namespace EntityFX.MqttBenchmark.Tests;

// Uses an explicitly configured SSH endpoint. No SSH daemon, key or service is installed by tests.
[TestClass]
public class RemoteNativeStandTests : IntegrationTestBase
{
    private static (string Inventory, string Output, string Root) Fixture(bool badHash = false, bool perBrokerHost = false)
    {
        var configuration = Environment.GetEnvironmentVariable("MQB_NATIVE_SSH_TEST");
        if (string.IsNullOrWhiteSpace(configuration))
            Assert.Inconclusive("Set MQB_NATIVE_SSH_TEST to a JSON file with nativeHost, mqttUri, executable, executableSha256 and configFile.");
        var spec = JsonNode.Parse(File.ReadAllText(configuration!))!;
        var root = Path.Combine(Path.GetTempPath(), "mqb-ssh-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var endpoint = new Uri(spec["mqttUri"]!.GetValue<string>());
        var inventory = new JsonObject
        {
            ["schemaVersion"] = "broker-stand.v1", ["deploymentMode"] = "singleHostSequential",
            ["cpuMode"] = "hostAllCores", ["activeBroker"] = "Mosquitto", ["dockerContext"] = "native-ssh-test",
            ["controlInterface"] = "127.0.0.1", ["networkMode"] = "host",
            ["nativeHost"] = spec["nativeHost"]!.DeepClone(),
            ["brokers"] = new JsonArray(new JsonObject
            {
                ["name"] = "Mosquitto", ["loaded"] = true, ["serviceName"] = "mosquitto", ["containerName"] = "mosquitto",
                ["runtime"] = "native", ["mqttUri"] = endpoint.ToString(), ["singleHostMqttPort"] = endpoint.Port,
                ["distributedMqttPort"] = 1883, ["managementUri"] = null,
                ["executable"] = spec["executable"]!.GetValue<string>(),
                ["executableSha256"] = badHash ? new string('a', 32) + new string('b', 32) : spec["executableSha256"]!.GetValue<string>(),
                ["args"] = new JsonArray("-c", spec["configFile"]!.GetValue<string>()),
                ["configFiles"] = new JsonArray(spec["configFile"]!.GetValue<string>()),
                ["versionArgs"] = new JsonArray("-h"), ["cpuset"] = "", ["memoryLimit"] = 4294967296L
            })
        };
        if (perBrokerHost)
        {
            inventory["deploymentMode"] = "distributedHosts";
            var selected = inventory["brokers"]![0]!;
            selected["nativeHost"] = inventory["nativeHost"]!.DeepClone();
            selected["dockerContext"] = "native-ssh-selected-test";
            selected["distributedMqttPort"] = endpoint.Port;
            selected["singleHostMqttPort"] = 2883;
            // An unusable root host must not override the selected broker's target.
            inventory["nativeHost"]!["host"] = "invalid.example";
            inventory["brokers"]!.AsArray().Add(new JsonObject {
                ["name"]="Aedes", ["loaded"]=false, ["serviceName"]="aedes", ["containerName"]="aedes",
                ["runtime"]="native", ["mqttUri"]="mqtt://192.0.2.99:1883", ["singleHostMqttPort"]=1883,
                ["distributedMqttPort"]=1883, ["managementUri"]=null, ["dockerContext"]="native-other-os",
                ["executable"]="C:/other-host/node.exe", ["executableSha256"]=new string('a',32)+new string('b',32),
                ["configFiles"]=new JsonArray("C:/other-host/server.js"), ["args"]=new JsonArray(),
                ["cpuset"]="", ["memoryLimit"]=4294967296L
            });
        }
        if (spec["linuxLaunchMode"] != null)
            inventory["brokers"]![0]!["linuxLaunchMode"] = spec["linuxLaunchMode"]!.GetValue<string>();
        var path = Path.Combine(root, "inventory.json");
        File.WriteAllText(path, inventory.ToJsonString());
        return (path, Path.Combine(root, "stand"), root);
    }

    private static string Script()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var script = Path.Combine(directory.FullName, "scripts", "BrokerStand.ps1");
            if (File.Exists(script)) return script;
            directory = directory.Parent;
        }
        throw new FileNotFoundException("BrokerStand.ps1 not found.");
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SshNative_StreamsReadinessAndBracketsWorkloadWithRemoteFence(bool perBrokerHost)
    {
        var fixture = Fixture(perBrokerHost: perBrokerHost);
        try
        {
            await using var session = await StandSession.StartAsync(Script(), fixture.Inventory,
                new CampaignDefinition { CpuMode = "hostAllCores", DeploymentMode = perBrokerHost ? "distributedHosts" : "singleHostSequential" }, "Mosquitto", fixture.Output);
            var clock = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.Output, "clock-alignment.json")))!;
            Assert.AreEqual("remote-host-realtime", clock["clockScope"]!.GetValue<string>());
            Assert.IsTrue(clock["ready"]!.GetValue<bool>());
            Assert.AreEqual("ssh-native-clock-roundtrip", clock["method"]!.GetValue<string>());
            Assert.AreEqual(3, clock["probes"]!.AsArray().Count);
            foreach (var probe in clock["probes"]!.AsArray())
            {
                Assert.IsTrue(Math.Abs(probe!["offsetMs"]!.GetValue<double>()) +
                    probe["uncertaintyMs"]!.GetValue<double>() <= 3000);
                Assert.IsTrue(probe["wallClockDriftMs"]!.GetValue<double>() <= 100);
            }
            var manifest = await session.RunWithTelemetryAsync(7, async token =>
            {
                Assert.IsTrue(File.Exists(Path.Combine(fixture.Output, "telemetry-ready.json")));
                Assert.IsTrue(File.Exists(Path.Combine(fixture.Output, "telemetry.ndjson")));
                Assert.IsFalse(File.Exists(Path.Combine(fixture.Output, "sampler-ended.json")));
                await Task.Delay(150, token);
            });
            Assert.AreEqual(7, manifest.SampleCount);
            Assert.AreEqual(StandTelemetry.NativeCpuScope, manifest.CpuScope);
            var end = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.Output, "sampler-ended.json")))!;
            var fence = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.Output, "workload-end-fence.json")))!;
            Assert.AreEqual(end["unitId"]!.GetValue<string>(), fence["unitId"]!.GetValue<string>());
            Assert.IsTrue(end["lastSampleMonotonicSeconds"]!.GetValue<double>() > fence["monotonicSeconds"]!.GetValue<double>());
        }
        finally { Directory.Delete(fixture.Root, true); }
    }

    [TestMethod]
    public async Task SshNative_RejectsWrongExecutableHashBeforeStartingBroker()
    {
        var fixture = Fixture(badHash: true);
        try
        {
            var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                StandSession.StartAsync(Script(), fixture.Inventory, new CampaignDefinition { CpuMode = "hostAllCores" },
                    "Mosquitto", fixture.Output));
            StringAssert.Contains(error.Message, "hash");
            Assert.IsFalse(File.Exists(Path.Combine(fixture.Output, "run-provenance.json")));
        }
        finally { Directory.Delete(fixture.Root, true); }
    }
}
