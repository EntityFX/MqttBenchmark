using System.Diagnostics;
using System.Text.Json;

namespace EntityFX.MqttBenchmark.Tests;

[TestClass]
public class BrokerStandControllerTests
{
    [TestMethod]
    public void Validate_AcceptsACompleteSingleHostInventory()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var configPath = Path.Combine(directory, "stand.json");
            File.WriteAllText(configPath, "{\n" +
                "  \"schemaVersion\": \"broker-stand.v1\",\n" +
                "  \"deploymentMode\": \"singleHost\",\n" +
                "  \"cpuMode\": \"singleCorePinned\",\n" +
                "  \"activeBroker\": \"Aedes\",\n" +
                "  \"dockerContext\": \"default\",\n" +
                "  \"brokers\": [{\n" +
                "    \"name\": \"Aedes\", \"loaded\": true,\n" +
                "    \"mqttUri\": \"mqtt://127.0.0.1:1883\", \"managementUri\": null,\n" +
                "    \"image\": \"local/aedes:1.1.2\",\n" +
                "    \"digest\": \"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\n" +
                "    \"cpuset\": \"0\", \"memoryLimit\": \"512m\"\n" +
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
                "\"campaign\": { \"deploymentMode\": \"distributed\", \"cpuMode\": \"hostAllCores\" }"));

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

            File.WriteAllText(configPath, InventoryJson().Replace("sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "not-a-digest"));
            var digestResult = RunController("validate", configPath, directory);
            Assert.AreNotEqual(0, digestResult.ExitCode);
            StringAssert.Contains(digestResult.StandardError, "immutable sha256 digest");
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
            StringAssert.Contains(loadedResult.StandardError, "exactly one loaded broker");

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
            Assert.IsTrue(File.ReadAllText(Path.Combine(directory, "telemetry.ndjson")).Contains("timestamp"));
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
        var dockerPath = Path.Combine(directory, "controlled-docker.cmd");
        File.WriteAllText(dockerPath, $"@echo off\r\necho %*>> \"%~dp0docker.log\"\r\necho {response}\r\n");
        return dockerPath;
    }

    private static string InventoryJson(string? additionalRootProperty = null) => "{\n" +
        "  \"schemaVersion\": \"broker-stand.v1\",\n" +
        "  \"deploymentMode\": \"singleHost\",\n" +
        "  \"cpuMode\": \"singleCorePinned\",\n" +
        "  \"activeBroker\": \"Aedes\",\n" +
        "  \"dockerContext\": \"default\",\n" +
        (additionalRootProperty is null ? string.Empty : $"  {additionalRootProperty},\n") +
        "  \"brokers\": [{\n" +
        "    \"name\": \"Aedes\", \"loaded\": true,\n" +
        "    \"serviceName\": \"aedes\", \"containerName\": \"aedes\",\n" +
        "    \"mqttUri\": \"mqtt://127.0.0.1:1883\", \"managementUri\": null,\n" +
        "    \"image\": \"local/aedes:1.1.2\",\n" +
        "    \"digest\": \"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\n" +
        "    \"cpuset\": \"0\", \"memoryLimit\": \"512m\"\n" +
        "  }]\n" +
        "}\n";

    private static string TwoLoadedInventoryJson() => "{\n" +
        "  \"schemaVersion\": \"broker-stand.v1\",\n" +
        "  \"deploymentMode\": \"singleHost\",\n" +
        "  \"cpuMode\": \"singleCorePinned\",\n" +
        "  \"activeBroker\": \"Aedes\",\n" +
        "  \"dockerContext\": \"default\",\n" +
        "  \"brokers\": [\n" +
        "    { \"name\": \"Aedes\", \"loaded\": true, \"mqttUri\": \"mqtt://127.0.0.1:1883\", \"image\": \"local/aedes:1.1.2\", \"digest\": \"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\", \"cpuset\": \"0\", \"memoryLimit\": \"512m\" },\n" +
        "    { \"name\": \"Mosquitto\", \"loaded\": true, \"mqttUri\": \"mqtt://127.0.0.1:2883\", \"image\": \"local/mosquitto:2.1.2\", \"digest\": \"sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\", \"cpuset\": \"0\", \"memoryLimit\": \"512m\" }\n" +
        "  ]\n" +
        "}\n";
}
