using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using EntityFX.MqttBenchmark.Campaign;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EntityFX.MqttBenchmark.Tests;

/// <summary>
/// Интеграционные тесты нативного (без Docker) времени исполнения стенда:
/// требуется реальный Mosquitto (переменная <c>MQB_NATIVE_MOSQUITTO_EXE</c> или
/// <c>mosquitto</c> в PATH) и pwsh 7 (переменная <c>MQB_STAND_SHELL</c> — полный путь).
/// </summary>
[TestClass]
public class NativeBrokerStandTests : IntegrationTestBase
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "EntityFX.MqttBenchmark.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static string? FindNativeExecutable()
    {
        var explicitPath = Environment.GetEnvironmentVariable("MQB_NATIVE_MOSQUITTO_EXE");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath)) return explicitPath;
        var binary = OperatingSystem.IsWindows() ? "mosquitto.exe" : "mosquitto";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, binary);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private void EnsureNativeExecutableAvailable()
    {
        if (FindNativeExecutable() == null)
            Assert.Inconclusive("Native Mosquitto executable is required: set MQB_NATIVE_MOSQUITTO_EXE or put 'mosquitto' on PATH.");
    }

    private static int ReserveFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed record StandFixture(string InventoryPath, string Output) : IDisposable
    {
        public void Dispose() { Directory.Delete(Path.GetDirectoryName(InventoryPath)!, true); }
    }

    private StandFixture BuildNativeStand(string executable, string sha, int port)
    {
        var root = Path.Combine(Path.GetTempPath(), "mqb-native-stand-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var confPath = Path.Combine(root, "mosquitto.conf");
        File.WriteAllText(confPath, $"listener {port} 127.0.0.1\nallow_anonymous true\npersistence false\nlog_dest none\n");
        var inventory = new
        {
            schemaVersion = "broker-stand.v1",
            deploymentMode = "singleHostSequential",
            cpuMode = "singleCorePinned",
            activeBroker = "Mosquitto",
            dockerContext = "native-test",
            controlInterface = "127.0.0.1",
            networkMode = "host",
            brokers = new[]
            {
                new
                {
                    name = "Mosquitto", loaded = true, serviceName = "mosquitto", containerName = "mosquitto",
                    runtime = "native",
                    mqttUri = $"mqtt://127.0.0.1:{port}",
                    singleHostMqttPort = port,
                    distributedMqttPort = 1883,
                    executable, executableSha256 = sha,
                    args = new[] { "-c", confPath },
                    configFiles = new[] { confPath },
                    cpuset = "0",
                    memoryLimit = 4294967296
                }
            }
        };
        var inventoryPath = Path.Combine(root, "broker-stand.native.json");
        File.WriteAllText(inventoryPath, JsonSerializer.Serialize(inventory));
        return new StandFixture(inventoryPath, Path.Combine(root, "output"));
    }
    [TestMethod]
    public async Task NativeStand_RunsStartHealthClockCaptureAndStopsTheProcess()
    {
        EnsureNativeExecutableAvailable();
        var executable = FindNativeExecutable()!;
        var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(executable))).ToLowerInvariant();
        using var fixture = BuildNativeStand(executable, sha, ReserveFreeLoopbackPort());
        var script = Path.Combine(RepoRoot(), "scripts", "BrokerStand.ps1");

        await using var session = await StandSession.StartAsync(script, fixture.InventoryPath, new CampaignDefinition(),
            "Mosquitto", fixture.Output, "docker");

        var provenance = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Output, "run-provenance.json")));
        Assert.AreEqual("native", provenance.RootElement.GetProperty("runtime").GetString());
        var unitId = int.Parse(provenance.RootElement.GetProperty("unitId").GetString()!);
        Assert.AreEqual(sha, provenance.RootElement.GetProperty("imageId").GetString());
        Assert.IsTrue(provenance.RootElement.TryGetProperty("monotonicEpoch", out _));
        Assert.IsTrue(provenance.RootElement.TryGetProperty("monotonicFrequency", out _));

        var clock = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Output, "clock-alignment.json")));
        Assert.IsTrue(clock.RootElement.GetProperty("ready").GetBoolean(), "Same-host clock alignment must be ready.");

        var manifest = await session.CaptureAsync(2);
        Assert.AreEqual(StandTelemetry.NativeCpuScope, manifest.CpuScope);
        Assert.AreEqual(StandTelemetry.NetworkScope, manifest.NetworkScope);
        var samples = File.ReadAllLines(Path.Combine(fixture.Output, "telemetry.ndjson")).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        Assert.IsTrue(samples.Length >= 2);
        foreach (var line in samples)
        {
            var sample = JsonDocument.Parse(line).RootElement;
            Assert.AreEqual("process-pinned", sample.GetProperty("cpuScope").GetString());
            Assert.AreEqual("host-shared", sample.GetProperty("networkScope").GetString());
        }
        // DisposeAsync (IAsyncDisposable above) performs the stop; verify the process is gone.
        await Task.Delay(300);
        Assert.IsFalse(System.Diagnostics.Process.GetProcessesByName("mosquitto").Any(p => p.Id == unitId),
            "The native broker must be stopped.");
    }

    [TestMethod]
    public async Task NativeStand_RejectsExecutableHashMismatch()
    {
        EnsureNativeExecutableAvailable();
        var executable = FindNativeExecutable()!;
        using var fixture = BuildNativeStand(executable, new string('a', 64), ReserveFreeLoopbackPort());
        var script = Path.Combine(RepoRoot(), "scripts", "BrokerStand.ps1");
        var error = await Assert.ThrowsExceptionAsync<Exception>(() => StandSession.StartAsync(script, fixture.InventoryPath,
            new CampaignDefinition(), "Mosquitto", fixture.Output, "docker"));
        StringAssert.Contains(error.Message, "hash");
    }
}