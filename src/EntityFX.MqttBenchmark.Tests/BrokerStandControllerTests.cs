using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace EntityFX.MqttBenchmark.Tests;

[TestClass]
public class BrokerStandControllerTests
{
    [TestMethod]
    public void Validate_RejectsInvalidIdentityEvenWithUnrelatedProvenance()
    {
        using var stand = new StandFixture("Mosquitto");
        stand.Broker["digest"] = "sha256:invalid";
        File.WriteAllText(Path.Combine(stand.Directory, "build-provenance.json"), "{\"broker\":\"Aedes\",\"imageId\":\"sha256:any\"}");
        stand.Fails("start", "digest");
        Assert.IsFalse(File.Exists(stand.CallsPath), "Invalid inventory must fail before Docker mutation.");
    }

    [TestMethod]
    public void Validate_EnforcesTopologyExclusiveLoadAndCpuModes()
    {
        using var stand = new StandFixture("Mosquitto");
        stand.Ok("validate");
        stand.Inventory["campaign"] = JsonNode.Parse("{\"deploymentMode\":\"distributedHosts\",\"cpuMode\":\"singleCorePinned\"}");
        stand.Fails("validate", "Campaign");
        stand.Inventory.Remove("campaign");
        stand.Broker["cpuset"] = "0-1";
        stand.Fails("validate", "exactly one CPU");
        stand.Broker["cpuset"] = "0";
        stand.Inventory["brokers"]!.AsArray().Add(JsonNode.Parse(stand.Broker.ToJsonString()));
        stand.Fails("validate", "active broker");
    }

    [DataTestMethod]
    [DataRow("Aedes", "1.1.2", "node")]
    [DataRow("ActiveMQ", "6.3.2", "java")]
    public void CustomBuild_EntireLifecycleUsesRecordedImageAndInputHashes(string broker, string version, string runtime)
    {
        using var stand = new StandFixture(broker);
        stand.Fails("start", "identity");
        stand.Ok("pull");
        stand.Ok("validate");
        stand.Ok("start");
        stand.Ok("health");
        stand.Ok("capture", 3);
        var provenance = stand.Json($"build/{broker.ToLowerInvariant()}.json");
        Assert.AreEqual(StandFixture.ImageId, provenance["imageId"]!.GetValue<string>());
        Assert.AreEqual(stand.Broker["image"]!.GetValue<string>(), provenance["image"]!.GetValue<string>());
        Assert.IsTrue(provenance["buildInputs"]!.AsArray().Count >= 3);
        foreach (var input in provenance["buildInputs"]!.AsArray())
            Assert.AreEqual(HashFile(Path.Combine(stand.Directory, "docker", "brokers", input!["path"]!.GetValue<string>())), input["sha256"]!.GetValue<string>());
        var versions = stand.Json("versions.json");
        Assert.AreEqual(StandFixture.ImageId, versions["runningImageId"]!.GetValue<string>());
        StringAssert.Contains(versions["brokerVersion"]!.GetValue<string>(), version);
        StringAssert.Contains(versions["runtimeVersion"]!.GetValue<string>().ToLowerInvariant(), runtime);
        var samples = File.ReadAllLines(Path.Combine(stand.Directory, "telemetry.ndjson")).Select(x => JsonNode.Parse(x)!).ToArray();
        Assert.AreEqual(3, samples.Length);
        Assert.AreEqual("container-cgroup", samples[0]["cpuScope"]!.GetValue<string>());
        Assert.AreEqual("broker-processes", samples[0]["processRssScope"]!.GetValue<string>());
        Assert.AreEqual("host-shared", samples[0]["networkScope"]!.GetValue<string>());
        Assert.IsTrue(samples[0]["processRssBytes"]!.GetValue<long>() > 0);
        Assert.AreEqual(2.0, samples[2]["monotonicSeconds"]!.GetValue<double>() - samples[0]["monotonicSeconds"]!.GetValue<double>());
        Assert.IsTrue(DateTimeOffset.Parse(samples[2]["timestamp"]!.GetValue<string>()) > DateTimeOffset.Parse(samples[0]["timestamp"]!.GetValue<string>()));
        Assert.IsTrue(stand.Json("config-hashes.json").AsArray().Any(x => x!["scope"]!.GetValue<string>() == "container-effective"));
        stand.Ok("stop");
        stand.Ok("reset");
        var calls = File.ReadAllText(stand.CallsPath);
        Assert.IsFalse(calls.Contains("@sha256:"), "Custom image IDs are not registry digests.");
        Assert.AreEqual(1, File.ReadAllLines(stand.CallsPath).Count(line => line.Contains("MQTTBENCHMARK_TELEMETRY_V1")), "One in-container sampler avoids per-command cadence drift.");
    }

    [DataTestMethod]
    [DataRow("Aedes")]
    [DataRow("ActiveMQ")]
    public void CustomBuild_RejectsChangedInputsWrongBrokerAndNonemptyDigest(string broker)
    {
        using var stand = new StandFixture(broker);
        stand.Ok("pull");
        var path = Path.Combine(stand.Directory, "build", broker.ToLowerInvariant() + ".json");
        var original = File.ReadAllText(path);
        var provenance = JsonNode.Parse(original)!;
        provenance["broker"] = "unrelated";
        File.WriteAllText(path, provenance.ToJsonString());
        stand.Fails("start", "provenance");
        File.WriteAllText(path, original);
        var buildInput = broker == "Aedes" ? "aedes/server.js" : "activemq/Dockerfile";
        File.AppendAllText(Path.Combine(stand.Directory, "docker/brokers", buildInput), "\n# changed\n");
        stand.Fails("start", "build inputs");
        stand.Broker["digest"] = StandFixture.ImageId;
        stand.Fails("start", "digest");
    }

    [DataTestMethod]
    [DataRow("Aedes")]
    [DataRow("ActiveMQ")]
    public void CustomBuild_SupportsOptionalProxyWithoutPersistingItsValue(string broker)
    {
        using var stand = new StandFixture(broker);
        stand.Ok("pull");
        var compose = stand.Json("build-compose.yml");
        var build = compose["services"]![broker.ToLowerInvariant()]!["build"]!;
        Assert.AreEqual("host", build["network"]?.GetValue<string>());
        Assert.AreEqual("${MQTTY_BUILD_HTTP_PROXY:-}", build["args"]?["HTTP_PROXY"]?.GetValue<string>());
        Assert.AreEqual("${MQTTY_BUILD_HTTP_PROXY:-}", build["args"]?["HTTPS_PROXY"]?.GetValue<string>());
    }

    [DataTestMethod]
    [DataRow("Aedes")]
    [DataRow("ActiveMQ")]
    public void CustomBuild_TrustedProvenanceReusesExactExistingImageWithoutBuild(string broker)
    {
        using var stand = new StandFixture(broker);
        stand.Ok("pull");
        var trustedDirectory = Path.Combine(stand.Directory, "trusted-build-provenance");
        Directory.CreateDirectory(trustedDirectory);
        var trustedPath = Path.Combine(trustedDirectory, broker.ToLowerInvariant() + ".json");
        File.Copy(Path.Combine(stand.Directory, "build", broker.ToLowerInvariant() + ".json"), trustedPath);
        File.Delete(Path.Combine(stand.Directory, "docker-calls.ndjson"));
        Directory.Delete(Path.Combine(stand.Directory, "build"), true);

        stand.TrustedBuildProvenanceDirectory = trustedDirectory;
        stand.Ok("pull");

        var calls = File.ReadAllLines(stand.CallsPath);
        Assert.IsFalse(calls.Any(line => line.Contains("\"build\"")), "A verified local image must not trigger an online build.");
        var provenance = stand.Json($"build/{broker.ToLowerInvariant()}.json");
        Assert.AreEqual("trusted-existing-image", provenance["source"]!.GetValue<string>());
        Assert.AreEqual(HashFile(trustedPath), provenance["trustedManifestSha256"]!.GetValue<string>());
        Assert.AreEqual(StandFixture.ImageId, provenance["imageId"]!.GetValue<string>());
    }

    [DataTestMethod]
    [DataRow("Aedes", true)]
    [DataRow("ActiveMQ", true)]
    [DataRow("Aedes", false)]
    [DataRow("ActiveMQ", false)]
    public void CustomBuild_TrustedProvenanceRejectsInputOrImageMismatch(string broker, bool changeInput)
    {
        using var stand = new StandFixture(broker);
        stand.Ok("pull");
        var trustedDirectory = Path.Combine(stand.Directory, "trusted-build-provenance");
        Directory.CreateDirectory(trustedDirectory);
        var trustedPath = Path.Combine(trustedDirectory, broker.ToLowerInvariant() + ".json");
        File.Copy(Path.Combine(stand.Directory, "build", broker.ToLowerInvariant() + ".json"), trustedPath);
        Directory.Delete(Path.Combine(stand.Directory, "build"), true);
        File.Delete(stand.CallsPath);
        stand.TrustedBuildProvenanceDirectory = trustedDirectory;
        if (changeInput)
        {
            var input = broker == "Aedes" ? "aedes/server.js" : "activemq/Dockerfile";
            File.AppendAllText(Path.Combine(stand.Directory, "docker", "brokers", input), "\n# changed\n");
            stand.Fails("pull", "build inputs");
        }
        else
        {
            File.WriteAllText(Path.Combine(stand.Directory, "tag-image-id"),
                "sha256:1123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
            stand.Fails("pull", "Image ID");
        }
        Assert.IsFalse(File.Exists(stand.CallsPath) && File.ReadAllLines(stand.CallsPath).Any(line => line.Contains("\"build\"")),
            "A rejected trust record must not fall back to an online build.");
    }

    [DataTestMethod]
    [DataRow("Aedes")]
    [DataRow("ActiveMQ")]
    public void Cleanup_RemainsAvailableWhenBuildInputsHaveChanged(string broker)
    {
        using var stand = new StandFixture(broker);
        stand.Ok("pull");
        stand.Ok("start");
        var input = broker == "Aedes" ? "aedes/server.js" : "activemq/Dockerfile";
        File.AppendAllText(Path.Combine(stand.Directory, "docker/brokers", input), "\n# subsequent edit\n");
        stand.Ok("stop");
        stand.Ok("reset");
    }

    [DataTestMethod]
    [DataRow("Mosquitto", "singleCorePinned", "singleHostSequential", 2883)]
    [DataRow("EMQX", "hostAllCores", "distributedHosts", 1883)]
    [DataRow("ActiveMQ", "singleCorePinned", "singleHostSequential", 3883)]
    [DataRow("ActiveMQ", "hostAllCores", "distributedHosts", 1883)]
    public void Start_TransfersEffectiveConfigurationWithoutHostBinds(string broker, string cpuMode, string deploymentMode, int mqttPort)
    {
        using var stand = new StandFixture(broker);
        stand.Inventory["cpuMode"] = cpuMode;
        stand.Inventory["deploymentMode"] = deploymentMode;
        stand.Broker["cpuset"] = cpuMode == "hostAllCores" ? null : "0";
        stand.Broker["dockerContext"] = "broker-context";
        if (broker == "ActiveMQ") stand.Ok("pull");
        stand.Ok("start");
        var compose = stand.Json("effective-compose.yml");
        Assert.AreEqual(1, compose["services"]!.AsObject().Count);
        var service = compose["services"]![broker.ToLowerInvariant()]!;
        Assert.AreEqual("host", service["network_mode"]!.GetValue<string>());
        Assert.IsNull(service["ports"]);
        Assert.IsNull(service["volumes"]);
        Assert.AreEqual("4g", service["mem_limit"]!.GetValue<string>());
        Assert.AreEqual(cpuMode == "hostAllCores" ? null : "0", service["cpuset"]?.GetValue<string>());
        var manifest = stand.Json("run-provenance.json");
        Assert.AreEqual(deploymentMode == "distributedHosts" ? "broker-context" : "stand-context", manifest["context"]!.GetValue<string>());
        var configs = manifest["effectiveConfigs"]!.AsArray();
        var primary = configs.Single(x => x!["containerPath"]!.GetValue<string>().EndsWith(broker == "ActiveMQ" ? "activemq.xml" : broker == "EMQX" ? "emqx.conf" : "mosquitto.conf"))!;
        var content = File.ReadAllText(primary["path"]!.GetValue<string>());
        StringAssert.Contains(content, mqttPort.ToString());
        if (broker == "ActiveMQ")
        {
            var setenv = configs.SingleOrDefault(x => x!["containerPath"]!.GetValue<string>().EndsWith("stand-setenv"));
            Assert.IsNotNull(setenv, "Provide an effective override for the distribution's JVM management agent.");
            StringAssert.Contains(File.ReadAllText(setenv!["path"]!.GetValue<string>()), "ACTIVEMQ_SUNJMX_START=\"\"");
            StringAssert.Contains(content, $"mqtt://127.0.0.1:{mqttPort}");
            StringAssert.Contains(content, "jetty-spring.xml");
            var jetty = configs.Single(x => x!["containerPath"]!.GetValue<string>().EndsWith("jetty-http.xml"))!;
            StringAssert.Contains(File.ReadAllText(jetty["path"]!.GetValue<string>()), "<Set name=\"host\">127.0.0.1</Set>");
            var logging = configs.Single(x => x!["containerPath"]!.GetValue<string>().EndsWith("log4j2.properties"))!;
            var logConfig = File.ReadAllText(logging["path"]!.GetValue<string>());
            StringAssert.Contains(logConfig, "rootLogger.level=WARN");
            Assert.IsFalse(logConfig.Contains("Rolling"));
        }
        if (broker == "Mosquitto")
        {
            StringAssert.Contains(content, "log_type warning");
            StringAssert.Contains(content, "log_type error");
        }
        if (broker == "EMQX")
        {
            StringAssert.Contains(content, "log.file.enable = false");
            Assert.IsTrue(content.Contains("dist_bind_address = \"127.0.0.1\""), "Erlang distribution must bind to the control address.");
            Assert.IsTrue(content.Contains("rpc.listen_address = \"127.0.0.1\""), "RPC must bind to the control address.");
        }
    }

    [TestMethod]
    public void Capture_UsesStartSnapshotAndRejectsDriftInRunningContainer()
    {
        using var stand = new StandFixture("Mosquitto");
        stand.Ok("start");
        var effectivePath = Path.Combine(stand.Directory, "effective-compose.yml");
        var before = HashFile(effectivePath);
        File.AppendAllText(Path.Combine(stand.Directory, "docker/brokers/mosquitto/mosquitto.conf"), "\n# changed after start");
        stand.Ok("capture", 2);
        Assert.AreEqual(before, HashFile(effectivePath));
        var configHash = stand.Json("config-hashes.json").AsArray().Single(x => x!["path"]!.GetValue<string>() == effectivePath)!;
        Assert.AreEqual(before, configHash["sha256"]!.GetValue<string>());
        File.WriteAllText(Path.Combine(stand.Directory, "config-drift"), "yes");
        stand.Fails("capture", "configuration");
    }

    [DataTestMethod]
    [DataRow("Aedes")]
    [DataRow("Mosquitto")]
    public void Capture_RejectsRunningImageThatNoLongerMatchesSelectedIdentity(string broker)
    {
        using var stand = new StandFixture(broker);
        if (broker == "Aedes") stand.Ok("pull");
        stand.Ok("start");
        const string differentId = "sha256:1123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        if (broker == "Aedes")
        {
            var provenance = stand.Json("build/aedes.json");
            provenance["imageId"] = differentId;
            File.WriteAllText(Path.Combine(stand.Directory, "build/aedes.json"), provenance.ToJsonString());
            File.WriteAllText(Path.Combine(stand.Directory, "tag-image-id"), differentId);
        }
        else stand.Broker["digest"] = differentId;
        stand.Fails("capture", "Running container");
    }

    [TestMethod]
    public void Health_WaitsForSuccessfulConnackAfterTransientStartupFailures()
    {
        using var stand = new StandFixture("Mosquitto");
        stand.FailFirstConnections = 2;
        stand.Ok("start");
        stand.Ok("health");
        Assert.AreEqual(3, stand.Connections);
    }

    [TestMethod]
    public void Health_NeverReadyTimesOutWithLastProtocolError()
    {
        using var stand = new StandFixture("Mosquitto");
        stand.ConnackCode = 5;
        stand.Ok("start");
        var timer = Stopwatch.StartNew();
        stand.Fails("health", "timed out");
        Assert.IsTrue(stand.Connections > 1, "Readiness must retry actual protocol probes.");
        Assert.IsTrue(timer.Elapsed < TimeSpan.FromSeconds(10));
        var health = stand.Json("health-readiness.json");
        Assert.IsFalse(health["ready"]!.GetValue<bool>());
        StringAssert.Contains(health["lastError"]!.GetValue<string>(), "CONNACK");
    }

    [TestMethod]
    public void Health_SlowConnackCannotExtendReadinessDeadline()
    {
        using var stand = new StandFixture("Mosquitto");
        stand.ConnackDelayMilliseconds = 3000;
        stand.Ok("start");
        var timer = Stopwatch.StartNew();
        stand.Fails("health", "timed out");
        Assert.IsTrue(timer.Elapsed < TimeSpan.FromSeconds(5), "A blocked stream read must obey the one-second test readiness deadline.");
        var health = stand.Json("health-readiness.json");
        Assert.IsFalse(health["ready"]!.GetValue<bool>());
        Assert.IsTrue(health["elapsedMs"]!.GetValue<double>() < 2000);
    }

    [TestMethod]
    public void Health_RequiresSuccessfulMqttConnack()
    {
        using var stand = new StandFixture("Mosquitto");
        stand.Ok("start");
        stand.Ok("health");
        stand.ConnackCode = 5;
        stand.Fails("health", "CONNACK");
    }

    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private sealed class StandFixture : IDisposable
    {
        public const string ImageId = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "mqttstand-test-" + Guid.NewGuid().ToString("N"));
        public string CallsPath => Path.Combine(Directory, "docker-calls.ndjson");
        public JsonObject Inventory { get; }
        public JsonObject Broker => Inventory["brokers"]![0]!.AsObject();
        public byte ConnackCode { get; set; }
        public string? TrustedBuildProvenanceDirectory { get; set; }
        private int connections;
        public int Connections => Volatile.Read(ref connections);
        public int FailFirstConnections { get; set; }
        public int ConnackDelayMilliseconds { get; set; }
        private volatile bool disposing;
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly Task acceptLoop;

        public StandFixture(string name)
        {
            System.IO.Directory.CreateDirectory(Directory);
            CopyTree(Path.Combine(AppContext.BaseDirectory, "docker"), Path.Combine(Directory, "docker"));
            File.Copy(Path.Combine(AppContext.BaseDirectory, "BrokerStand.ps1"), Path.Combine(Directory, "BrokerStand.ps1"));
            listener.Start();
            acceptLoop = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        using var client = await listener.AcceptTcpClientAsync();
                        var number = Interlocked.Increment(ref connections);
                        var buffer = new byte[64];
                        await client.GetStream().ReadAsync(buffer);
                        if (ConnackDelayMilliseconds > 0) await Task.Delay(ConnackDelayMilliseconds);
                        await client.GetStream().WriteAsync(new byte[] { 0x20, 2, 0, number <= FailFirstConnections ? (byte)5 : ConnackCode });
                    }
                }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) when (disposing) { }
            });
            var service = name.ToLowerInvariant();
            Inventory = new JsonObject
            {
                ["schemaVersion"] = "broker-stand.v1", ["deploymentMode"] = "singleHostSequential",
                ["cpuMode"] = "singleCorePinned", ["activeBroker"] = name, ["dockerContext"] = "stand-context",
                ["controlInterface"] = "127.0.0.1", ["networkMode"] = "host",
                ["brokers"] = new JsonArray(new JsonObject
                {
                    ["name"] = name, ["loaded"] = true, ["serviceName"] = service, ["containerName"] = service,
                    ["mqttUri"] = $"mqtt://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}",
                    ["singleHostMqttPort"] = name switch { "Aedes" => 1883, "Mosquitto" => 2883, "ActiveMQ" => 3883, _ => 4883 },
                    ["distributedMqttPort"] = 1883, ["managementUri"] = name == "ActiveMQ" ? "http://127.0.0.1:8161" : name == "EMQX" ? "http://127.0.0.1:18083" : null,
                    ["image"] = "test/" + service + ":pinned", ["digest"] = name is "Aedes" or "ActiveMQ" ? null : ImageId,
                    ["digestProvenance"] = new JsonObject { ["status"] = name is "Aedes" or "ActiveMQ" ? "unresolved" : "verified" },
                    ["cpuset"] = "0", ["memoryLimit"] = 4294967296L
                })
            };
            File.Copy(Path.Combine(AppContext.BaseDirectory, "TestData/StrictDocker.ps1"), Path.Combine(Directory, "docker.ps1"));
            Save();
        }

        public void Save() => File.WriteAllText(Path.Combine(Directory, "stand.json"), Inventory.ToJsonString());
        public JsonNode Json(string path) => JsonNode.Parse(File.ReadAllText(Path.Combine(Directory, path)))!;
        public void Ok(string action, int seconds = 1)
        {
            var result = Run(action, seconds);
            Assert.AreEqual(0, result.code, action + ": " + result.error + result.output);
        }
        public void Fails(string action, string message)
        {
            var result = Run(action, 1);
            Assert.AreNotEqual(0, result.code, action + " unexpectedly succeeded.");
            StringAssert.Contains(result.error, message);
        }
        private (int code, string output, string error) Run(string action, int seconds)
        {
            Save();
            var info = new ProcessStartInfo("pwsh") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var arg in new[] { "-NoProfile", "-File", Path.Combine(Directory, "BrokerStand.ps1"), "-Action", action,
                "-ConfigPath", Path.Combine(Directory, "stand.json"), "-OutputDirectory", Directory,
                "-DockerExecutable", Path.Combine(Directory, "docker.ps1"), "-CaptureSeconds", seconds.ToString() }) info.ArgumentList.Add(arg);
            info.ArgumentList.Add("-HealthTimeoutSeconds"); info.ArgumentList.Add("1");
            if (TrustedBuildProvenanceDirectory != null)
            {
                info.ArgumentList.Add("-TrustedBuildProvenanceDirectory");
                info.ArgumentList.Add(TrustedBuildProvenanceDirectory);
            }
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            Assert.IsTrue(process.WaitForExit(45000), "Controller timed out.");
            return (process.ExitCode, output.Result, error.Result);
        }
        public void Dispose()
        {
            disposing = true;
            listener.Stop();
            acceptLoop.GetAwaiter().GetResult();
            System.IO.Directory.Delete(Directory, true);
        }
        private static void CopyTree(string from, string to)
        {
            System.IO.Directory.CreateDirectory(to);
            foreach (var file in System.IO.Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
            foreach (var dir in System.IO.Directory.GetDirectories(from)) CopyTree(dir, Path.Combine(to, Path.GetFileName(dir)));
        }
    }
}
