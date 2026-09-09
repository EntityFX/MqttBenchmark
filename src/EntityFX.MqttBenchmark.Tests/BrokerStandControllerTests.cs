using System.Diagnostics;
using System.Text.Json;
using EntityFX.MqttBenchmark.Bomber;

namespace EntityFX.MqttBenchmark.Tests;

[TestClass]
public class BrokerStandControllerTests
{
    [TestMethod]
    public void InfraConfigResolver_ExpandsEnvironmentReferenceOrFailsWithoutEchoingValue()
    {
        const string variable = "MQTTBENCHMARK_TEST_TOKEN";
        var directory = CreateTemporaryDirectory();
        try
        {
            var configPath = Path.Combine(directory, "infra.json");
            File.WriteAllText(configPath, "{ \"token\": \"${MQTTBENCHMARK_TEST_TOKEN}\", \"items\": [\"${MQTTBENCHMARK_TEST_TOKEN}\"] }");
            Environment.SetEnvironmentVariable(variable, "quote-\\-line\nvalue");
            string resolvedPath;
            using (var lease = InfraConfigEnvironmentResolver.Resolve(configPath))
            {
                resolvedPath = lease.Path;
                var json = JsonDocument.Parse(File.ReadAllText(resolvedPath));
                Assert.AreEqual("quote-\\-line\nvalue", json.RootElement.GetProperty("token").GetString());
                Assert.AreEqual("quote-\\-line\nvalue", json.RootElement.GetProperty("items")[0].GetString());
            }
            Assert.IsFalse(File.Exists(resolvedPath));

            Environment.SetEnvironmentVariable(variable, null);
            var exception = Assert.ThrowsException<InvalidDataException>(() =>
                InfraConfigEnvironmentResolver.Resolve(configPath));
            StringAssert.Contains(exception.Message, variable);
            Assert.IsFalse(exception.Message.Contains("test-only-value"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
            Directory.Delete(directory, recursive: true);
        }
    }
    [TestMethod]
    public void Validate_AcceptsACompleteSingleHostInventory()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var configPath = Path.Combine(directory, "stand.json");
            File.WriteAllText(configPath, "{\n" +
                "  \"schemaVersion\": \"broker-stand.v1\",\n" +
                "  \"deploymentMode\": \"singleHostSequential\",\n" +
                "  \"cpuMode\": \"singleCorePinned\",\n" +
                "  \"activeBroker\": \"Aedes\",\n" +
                "  \"dockerContext\": \"default\",\n" +
                "  \"brokers\": [{\n" +
                "    \"name\": \"Aedes\", \"loaded\": true,\n" +
                "    \"mqttUri\": \"mqtt://127.0.0.1:1883\", \"managementUri\": null,\n" +
                "    \"image\": \"local/aedes:1.1.2\",\n" +
                "    \"digest\": \"sha256:abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789\", \"digestProvenance\": { \"status\": \"verified\" },\n" +
                "    \"cpuset\": \"0\", \"memoryLimit\": 4294967296\n" +
                "  }]\n" +
                "}\n");

            var result = RunController("validate", configPath, directory);

            Assert.AreEqual(0, result.ExitCode, result.StandardError);
            using var json = JsonDocument.Parse(result.StandardOutput);
            Assert.AreEqual("valid", json.RootElement.GetProperty("status").GetString());
            Assert.AreEqual("validate", json.RootElement.GetProperty("action").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void Validate_RejectsCampaignWithIncompatibleDeploymentOrCpuMode()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var configPath = Path.Combine(directory, "stand.json");
            File.WriteAllText(configPath, InventoryJson(
                "\"campaign\": { \"deploymentMode\": \"distributedHosts\", \"cpuMode\": \"hostAllCores\" }"));

            var result = RunController("validate", configPath, directory);

            Assert.AreNotEqual(0, result.ExitCode);
            StringAssert.Contains(result.StandardError, "Campaign deployment mode");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void Validate_RejectsUnsupportedSchemaAndUnpinnedDigest()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var configPath = Path.Combine(directory, "stand.json");
            File.WriteAllText(configPath, InventoryJson().Replace("broker-stand.v1", "broker-stand.v0"));
            var schemaResult = RunController("validate", configPath, directory);
            Assert.AreNotEqual(0, schemaResult.ExitCode);
            StringAssert.Contains(schemaResult.StandardError, "schema version");

            File.WriteAllText(configPath, InventoryJson().Replace("sha256:abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", "not-a-digest"));
            var digestResult = RunController("validate", configPath, directory);
            Assert.AreNotEqual(0, digestResult.ExitCode);
            StringAssert.Contains(digestResult.StandardError, "unresolved image identity");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void Validate_RejectsMultipleLoadedBrokersAndMultiCpuPin()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var configPath = Path.Combine(directory, "stand.json");
            File.WriteAllText(configPath, TwoLoadedInventoryJson());
            var loadedResult = RunController("validate", configPath, directory);
            Assert.AreNotEqual(0, loadedResult.ExitCode);
            StringAssert.Contains(loadedResult.StandardError, "active broker must be loaded");

            File.WriteAllText(configPath, InventoryJson().Replace("\"cpuset\": \"0\"", "\"cpuset\": \"0-1\""));
            var cpuResult = RunController("validate", configPath, directory);
            Assert.AreNotEqual(0, cpuResult.ExitCode);
            StringAssert.Contains(cpuResult.StandardError, "pin exactly one CPU");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void Capture_WritesVersionTelemetryLogAndConfigHashFromControlledDocker()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var configPath = Path.Combine(directory, "stand.json");
            File.WriteAllText(configPath, InventoryJson());
            var dockerPath = CreateControlledDocker(directory);

            var result = RunController("capture", configPath, directory, dockerPath);

            Assert.AreEqual(0, result.ExitCode, result.StandardError);
            Assert.IsTrue(File.Exists(Path.Combine(directory, "versions.json")));
            Assert.IsTrue(File.Exists(Path.Combine(directory, "telemetry.ndjson")));
            Assert.IsTrue(File.Exists(Path.Combine(directory, "logs", "Aedes.log")));
            Assert.IsTrue(File.Exists(Path.Combine(directory, "config-hashes.json")));
            var telemetry = File.ReadAllText(Path.Combine(directory, "telemetry.ndjson"));
            Assert.IsTrue(telemetry.Contains("timestamp"));
            Assert.IsTrue(telemetry.Contains("cgroupCpuAndThrottle"));
            Assert.IsTrue(telemetry.Contains("cgroupMemoryCurrentBytes"));
            Assert.IsTrue(telemetry.Contains("host-shared"));
            Assert.IsTrue(File.ReadAllText(Path.Combine(directory, "versions.json")).Contains("requestedImage"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void Reset_RemovesOnlyTheLoadedBroker()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var configPath = Path.Combine(directory, "stand.json");
            File.WriteAllText(configPath, InventoryJson());
            var dockerPath = CreateControlledDocker(directory);

            var result = RunController("reset", configPath, directory, dockerPath);

            Assert.AreEqual(0, result.ExitCode, result.StandardError);
            var calls = File.ReadAllText(Path.Combine(directory, "docker.log"));
            StringAssert.Contains(calls, "rm -f aedes");
            Assert.IsFalse(calls.Contains("mosquitto"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void RemainingControllerActionsInvokeOnlyTheActiveBroker()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var configPath = Path.Combine(directory, "stand.json");
            File.WriteAllText(configPath, InventoryJson());
            var dockerPath = CreateControlledDocker(directory, "running");

            foreach (var action in new[] { "pull", "start", "health", "stop" })
            {
                var result = RunController(action, configPath, directory, dockerPath);
                Assert.AreEqual(0, result.ExitCode, $"{action}: {result.StandardError}");
            }

            var calls = File.ReadAllText(Path.Combine(directory, "docker.log"));
            StringAssert.Contains(calls, "compose -f");
            StringAssert.Contains(calls, "build aedes");
            StringAssert.Contains(calls, "compose -f");
            StringAssert.Contains(calls, "inspect --format");
            StringAssert.Contains(calls, "stop aedes");
            StringAssert.Contains(calls, "rm -f mosquitto");
            var effectiveOverride = File.ReadAllText(Path.Combine(directory, "effective-compose.yml"));
            StringAssert.Contains(effectiveOverride, "network_mode: host");
            StringAssert.Contains(effectiveOverride, "MQTT_PORT=1883");
            Assert.IsFalse(effectiveOverride.Contains("ports:"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void Health_RejectsARunningContainerWhoseMqttEndpointIsUnavailable()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var configPath = Path.Combine(directory, "stand.json");
            File.WriteAllText(configPath, InventoryJson().Replace("127.0.0.1:1883", "127.0.0.1:65501"));
            var dockerPath = CreateControlledDocker(directory, "running");

            var result = RunController("health", configPath, directory, dockerPath);

            Assert.AreNotEqual(0, result.ExitCode);
            StringAssert.Contains(result.StandardError, "MQTT readiness");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static (int ExitCode, string StandardOutput, string StandardError) RunController(
        string action, string configPath, string outputDirectory, string? dockerExecutable = null)
    {
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "BrokerStand.ps1");
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "pwsh",
            Arguments = $"-NoProfile -File \"{scriptPath}\" -Action {action} -ConfigPath \"{configPath}\" -OutputDirectory \"{outputDirectory}\"" +
                (dockerExecutable is null ? string.Empty : $" -DockerExecutable \"{dockerExecutable}\""),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        })!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output, error);
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mqttbenchmark-brokerstand-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string CreateControlledDocker(string directory, string response = "controlled-docker")
    {
        var dockerPath = Path.Combine(directory, "controlled-docker.ps1");
        File.WriteAllText(dockerPath, "param([Parameter(ValueFromRemainingArguments = $true)][string[]]$DockerArguments)\n" +
            "$joined = $DockerArguments -join ' '\n" +
            "Add-Content -LiteralPath (Join-Path $PSScriptRoot 'docker.log') -Value $joined\n" +
            "if ($joined -match ' version ') { '{\"Server\":{\"Version\":\"29.2.1\"}}'; exit 0 }\n" +
            "if ($joined -match 'image inspect') { '{\"Id\":\"sha256:abcdef\"}'; exit 0 }\n" +
            "if ($joined -match ' stats ') { '{\"CPUPerc\":\"1.00%\",\"MemUsage\":\"100MiB / 4GiB\",\"NetIO\":\"1kB / 2kB\",\"BlockIO\":\"3kB / 4kB\"}'; exit 0 }\n" +
            "if ($joined -match 'cpu.stat') { 'usage_usec 1`nnr_throttled 2'; exit 0 }\n" +
            "if ($joined -match 'memory.current') { '104857600'; exit 0 }\n" +
            "if ($joined -match 'VmRSS') { 'VmRSS: 102400 kB'; exit 0 }\n" +
            "if ($joined -match 'ps -a') { 'mosquitto`naedes'; exit 0 }\n" +
            "if ($joined -match 'inspect') { 'running'; exit 0 }\n" +
            "if ($joined -match ' logs ') { '2026-09-09T00:00:00Z ready'; exit 0 }\n" +
            $"if ($joined -match ' compose | pull | build | rm | stop |/proc/net/dev|io.stat|ss -tan') {{ '{response}'; exit 0 }}\n" +
            "throw \"unsupported docker command: $joined\"\n");
        return dockerPath;
    }

    private static string InventoryJson(string? additionalRootProperty = null) => "{\n" +
        "  \"schemaVersion\": \"broker-stand.v1\",\n" +
        "  \"deploymentMode\": \"singleHostSequential\",\n" +
        "  \"cpuMode\": \"singleCorePinned\",\n" +
        "  \"activeBroker\": \"Aedes\",\n" +
        "  \"dockerContext\": \"default\",\n" +
        "  \"controlInterface\": \"127.0.0.1\",\n" +
        "  \"networkMode\": \"host\",\n" +
        (additionalRootProperty is null ? string.Empty : $"  {additionalRootProperty},\n") +
        "  \"brokers\": [{\n" +
        "    \"name\": \"Aedes\", \"loaded\": true,\n" +
        "    \"serviceName\": \"aedes\", \"containerName\": \"aedes\",\n" +
        "    \"mqttUri\": \"mqtt://127.0.0.1:1883\", \"singleHostMqttPort\": 1883, \"distributedMqttPort\": 1883, \"managementUri\": null,\n" +
        "    \"image\": \"local/aedes:1.1.2\",\n" +
        "    \"digest\": \"sha256:abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789\", \"digestProvenance\": { \"status\": \"verified\" },\n" +
        "    \"cpuset\": \"0\", \"memoryLimit\": 4294967296\n" +
        "  }]\n" +
        "}\n";

    private static string TwoLoadedInventoryJson() => "{\n" +
        "  \"schemaVersion\": \"broker-stand.v1\",\n" +
        "  \"deploymentMode\": \"singleHostSequential\",\n" +
        "  \"cpuMode\": \"singleCorePinned\",\n" +
        "  \"activeBroker\": \"Aedes\",\n" +
        "  \"dockerContext\": \"default\",\n" +
        "  \"brokers\": [\n" +
        "    { \"name\": \"Aedes\", \"loaded\": true, \"mqttUri\": \"mqtt://127.0.0.1:1883\", \"image\": \"local/aedes:1.1.2\", \"digest\": \"sha256:abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789\", \"digestProvenance\": { \"status\": \"verified\" }, \"cpuset\": \"0\", \"memoryLimit\": 4294967296 },\n" +
        "    { \"name\": \"Mosquitto\", \"loaded\": true, \"mqttUri\": \"mqtt://127.0.0.1:2883\", \"image\": \"local/mosquitto:2.1.2\", \"digest\": \"sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\", \"digestProvenance\": { \"status\": \"verified\" }, \"cpuset\": \"0\", \"memoryLimit\": 4294967296 }\n" +
        "  ]\n" +
        "}\n";
}
