using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using EntityFX.MqttBenchmark.Campaign;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace EntityFX.MqttBenchmark.Tests;

/// <summary>
/// Интеграционные тесты нативного (без Docker) времени исполнения стенда.
/// Требуют pwsh 7 (переменная <c>MQB_STAND_SHELL</c> — полный путь) и один из брокеров:
/// <list type="bullet">
/// <item>Mosquitto (по умолчанию): <c>MQB_NATIVE_MOSQUITTO_EXE</c> или <c>mosquitto</c> в PATH.</item>
/// <item>Произвольный (например, node + aedes): <c>MQB_NATIVE_BROKER_EXE</c> (исполняемый файл),
/// <c>MQB_NATIVE_BROKER_ENTRY</c> (сценарий входа; он же — config file, получает порт как argv[1]),
/// опционально <c>MQB_NATIVE_NODE_MODULES</c> — каталог node_modules для NODE_PATH.</item>
/// </list>
/// </summary>
[TestClass]
public class NativeBrokerStandTests : IntegrationTestBase
{
    private sealed record NativeBrokerSpec(
        string Name,
        string Executable,
        string[] ConfigFiles,
        string ProcessName,
        Func<int, string[]> ArgsForPort,
        Action<string, int>? WriteConfig = null);

    private static string RepoRoot()
    {
        // The stand controller script lives at <repo>/scripts/BrokerStand.ps1 (the .sln is one level below it).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "scripts", "BrokerStand.ps1"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static string? FindMosquitto()
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

    /// <summary>Выбирает брокер: произвольный (MQB_NATIVE_BROKER_*) в приоритете, иначе Mosquitto.</summary>
    private static NativeBrokerSpec? ResolveNativeBrokerSpec()
    {
        var exe = Environment.GetEnvironmentVariable("MQB_NATIVE_BROKER_EXE");
        var entry = Environment.GetEnvironmentVariable("MQB_NATIVE_BROKER_ENTRY");
        if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe) && !string.IsNullOrWhiteSpace(entry) && File.Exists(entry))
        {
            var nodeModules = Environment.GetEnvironmentVariable("MQB_NATIVE_NODE_MODULES");
            if (!string.IsNullOrWhiteSpace(nodeModules) && Directory.Exists(nodeModules))
                Environment.SetEnvironmentVariable("NODE_PATH", nodeModules); // inherited: test -> controller -> broker
            return new NativeBrokerSpec(
                "Aedes", exe, new[] { entry }, Path.GetFileNameWithoutExtension(exe),
                port => new[] { entry, port.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }
        var mosquitto = FindMosquitto();
        if (mosquitto == null) return null;
        var confPath = Path.Combine(Path.GetTempPath(), "mqb-native-stand-" + Guid.NewGuid().ToString("N"), "mosquitto.conf");
        return new NativeBrokerSpec(
            "Mosquitto", mosquitto, new[] { confPath }, "mosquitto", _ => new[] { "-c", confPath },
            (root, port) =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(confPath)!);
                File.WriteAllText(confPath, $"listener {port} 127.0.0.1\nallow_anonymous true\npersistence false\nlog_dest none\n");
            });
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

    private static NativeBrokerSpec RequireNativeBrokerSpec()
    {
        var spec = ResolveNativeBrokerSpec();
        if (spec == null)
            Assert.Inconclusive("A native broker is required: mosquitto (MQB_NATIVE_MOSQUITTO_EXE or PATH) or a generic broker (MQB_NATIVE_BROKER_EXE + MQB_NATIVE_BROKER_ENTRY).");
        return spec!;
    }

    private StandFixture BuildNativeStand(NativeBrokerSpec spec, int port)
    {
        var root = Path.Combine(Path.GetTempPath(), "mqb-native-stand-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        spec.WriteConfig?.Invoke(root, port);
        var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(spec.Executable))).ToLowerInvariant();
        var service = spec.Name.ToLowerInvariant();
        var inventory = new
        {
            schemaVersion = "broker-stand.v1",
            deploymentMode = "singleHostSequential",
            cpuMode = "singleCorePinned",
            activeBroker = spec.Name,
            dockerContext = "native-test",
            controlInterface = "127.0.0.1",
            networkMode = "host",
            brokers = new[]
            {
                new
                {
                    name = spec.Name, loaded = true, serviceName = service, containerName = service,
                    runtime = "native",
                    mqttUri = $"mqtt://127.0.0.1:{port}",
                    singleHostMqttPort = port,
                    distributedMqttPort = 1883,
                    executable = spec.Executable, executableSha256 = sha,
                    args = spec.ArgsForPort(port),
                    configFiles = spec.ConfigFiles,
                    managementUri = (string)null,
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
        var spec = RequireNativeBrokerSpec();
        using var fixture = BuildNativeStand(spec, ReserveFreeLoopbackPort());
        var script = Path.Combine(RepoRoot(), "scripts", "BrokerStand.ps1");

        var session = await StandSession.StartAsync(script, fixture.InventoryPath, new CampaignDefinition(),
            spec.Name, fixture.Output, "docker");
        var unitId = 0;
        try
        {
            var provenance = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Output, "run-provenance.json")));
            Assert.AreEqual("native", provenance.RootElement.GetProperty("runtime").GetString());
            unitId = int.Parse(provenance.RootElement.GetProperty("unitId").GetString()!);
            var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(spec.Executable))).ToLowerInvariant();
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
        }
        finally
        {
            await session.DisposeAsync(); // stop; also runs on assertion failure
        }
        await Task.Delay(300);
        Assert.IsFalse(System.Diagnostics.Process.GetProcessesByName(spec.ProcessName).Any(p => p.Id == unitId),
            "The native broker must be stopped.");
    }

    [TestMethod]
    public async Task NativeStand_RejectsExecutableHashMismatch()
    {
        var spec = RequireNativeBrokerSpec();
        using var fixture = BuildNativeStand(spec, ReserveFreeLoopbackPort());
        // Corrupt the pin: a well-formed 64-hex digest that is not the executable's real one.
        var json = File.ReadAllText(fixture.InventoryPath);
        var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(spec.Executable))).ToLowerInvariant();
        File.WriteAllText(fixture.InventoryPath, json.Replace(actual, "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"));
        var script = Path.Combine(RepoRoot(), "scripts", "BrokerStand.ps1");
        var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => StandSession.StartAsync(script, fixture.InventoryPath,
            new CampaignDefinition(), spec.Name, fixture.Output, "docker"));
        StringAssert.Contains(error.Message, "hash");
    }
}
