using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace EntityFX.MqttBenchmark.Campaign;

public sealed record TelemetryManifest(int SampleCount, string CpuScope, string NetworkScope, IReadOnlyDictionary<string, string> InputSha256);
public sealed record BenchmarkEnvironment(string OsDescription, int ProcessorCount, string FrameworkDescription,
    string Architecture, string MachineName, long StopwatchFrequency, DateTimeOffset CapturedAtUtc);
public sealed record BrokerPreflight(ProtocolPreflight Protocol, TelemetryManifest Telemetry);
public sealed record PreflightReport(int SchemaVersion, CampaignIdentity Identity, bool Success, BenchmarkEnvironment Environment,
    IReadOnlyList<BrokerPreflight> Brokers)
{
    public static PreflightReport Create(CampaignIdentity identity, IReadOnlyList<BrokerPreflight> brokers) =>
        new(3, identity, brokers.Count > 0 && brokers.All(x => x.Protocol.Success && x.Telemetry.SampleCount > 0),
            new(RuntimeInformation.OSDescription, System.Environment.ProcessorCount, RuntimeInformation.FrameworkDescription,
                RuntimeInformation.ProcessArchitecture.ToString(), System.Environment.MachineName, Stopwatch.Frequency, DateTimeOffset.UtcNow), brokers);
}

public sealed class StandSession : IAsyncDisposable
{
    private readonly string script, selectedPath, output, docker;
    private readonly Dictionary<string, FileStream> leases = new(StringComparer.Ordinal);
    private bool started;
    public Uri Endpoint { get; }
    private StandSession(string script, string selectedPath, string output, string docker, Uri endpoint) =>
        (this.script, this.selectedPath, this.output, this.docker, Endpoint) = (script, selectedPath, output, docker, endpoint);

    public static async Task<StandSession> StartAsync(string script, string inventoryPath, CampaignDefinition config,
        string broker, string output, string dockerExecutable = "docker", CancellationToken cancellationToken = default,
        string? expectedStandSha256 = null)
    {
        config.Validate();
        var inputBytes = File.ReadAllBytes(inventoryPath);
        if (expectedStandSha256 != null && CampaignJson.HashBytes(inputBytes) != expectedStandSha256)
            throw new InvalidDataException("Stand input bytes do not match the campaign identity.");
        // Parse and later persist exactly the buffer whose identity was verified. A source-file change
        // after this read cannot change this deployment, and the next attempt must verify again.
        var inventory = JsonNode.Parse(inputBytes) ?? throw new InvalidDataException("Missing stand inventory.");
        var brokers = inventory["brokers"]!.AsArray();
        var selected = brokers.Single(x => x!["name"]!.GetValue<string>() == broker)!;
        output = Path.GetFullPath(output);
        var selectedPath = Path.Combine(output, "stand.selected.json");
        var session = new StandSession(Path.GetFullPath(script), selectedPath, output, dockerExecutable,
            new Uri(selected["mqttUri"]!.GetValue<string>()));
        try
        {
            // A context lease spans preflight, measurement, capture and stop, also across different campaigns.
            session.LeaseContext(ControllerContext(inventory, selected));
            var sourcePath = Path.Combine(output, "inventory-check", "stand.source.json");
            CampaignJson.WriteBytesNew(sourcePath, inputBytes);
            // Custom images need the controller's build record before validate. Registry images can be
            // validated and started at their immutable digest from cache, without contacting the registry.
            if (inventory["activeBroker"]?.GetValue<string>() is "Aedes" or "ActiveMQ")
            {
                // Source custom-image validation also builds/inspects that source's active context.
                // Lease this used context too; unrelated broker entries never influence the leases.
                var sourceActive = brokers.Single(x => x!["name"]!.GetValue<string>() == inventory["activeBroker"]!.GetValue<string>())!;
                session.LeaseContext(ControllerContext(inventory, sourceActive));
                await session.InvokeAsync("pull", sourcePath, Path.GetDirectoryName(sourcePath)!, 1, cancellationToken);
            }
            await session.InvokeAsync("validate", sourcePath, Path.GetDirectoryName(sourcePath)!, 1, cancellationToken);
            inventory["campaign"] = new JsonObject { ["deploymentMode"] = config.DeploymentMode, ["cpuMode"] = config.CpuMode };
            inventory["activeBroker"] = broker;
            foreach (var item in brokers) item!["loaded"] = item["name"]!.GetValue<string>() == broker;
            CampaignJson.WriteNew(selectedPath, inventory);
            if (broker is "Aedes" or "ActiveMQ")
                await session.InvokeAsync("pull", selectedPath, output, 1, cancellationToken);
            await session.InvokeAsync("validate", selectedPath, output, 1, cancellationToken);
            session.started = true;
            await session.InvokeAsync("start", selectedPath, output, 1, cancellationToken);
            await session.InvokeAsync("health", selectedPath, output, 1, cancellationToken);
            await session.InvokeAsync("clock", selectedPath, output, 1, cancellationToken);
            return session;
        }
        catch
        {
            try { await session.DisposeAsync(); } catch { /* Original start/validation failure remains primary. */ }
            throw;
        }
    }

    private static string ControllerContext(JsonNode inventory, JsonNode selected)
    {
        var context = inventory["deploymentMode"]?.GetValue<string>() == "distributedHosts"
            ? selected["dockerContext"]?.GetValue<string>() : inventory["dockerContext"]?.GetValue<string>();
        return !string.IsNullOrWhiteSpace(context) ? context : throw new InvalidDataException("The controller's selected Docker context is required.");
    }

    private void LeaseContext(string context)
    {
        if (leases.ContainsKey(context)) return;
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(context)));
        leases.Add(context, new FileStream(Path.Combine(Path.GetTempPath(), "mqttbenchmark-stand-" + id + ".lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
    }

    public async Task<TelemetryManifest> RunWithTelemetryAsync(int seconds, Func<CancellationToken, Task> measurement,
        CancellationToken cancellationToken = default)
    {
        using var captureCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var capture = CaptureAsync(seconds, captureCancellation.Token);
        try
        {
            var startup = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(output, "telemetry-ready.json")))
            {
                if (capture.IsCompleted) { await capture; throw new InvalidDataException("Telemetry ended before measurement readiness."); }
                if (startup.Elapsed > TimeSpan.FromSeconds(60)) throw new TimeoutException("Telemetry did not become ready within 60 seconds.");
                await Task.Delay(25, cancellationToken);
            }
            if (capture.IsCompleted) { await capture; throw new InvalidDataException("Telemetry ended before measurement started."); }
            CampaignJson.WriteNew(Path.Combine(output, "measurement-started.json"), new { startedAtUtc = DateTimeOffset.UtcNow });
            await measurement(cancellationToken);
            if (capture.IsCompleted) { await capture; throw new InvalidDataException("Telemetry ended before measurement completed."); }
            return await capture;
        }
        catch
        {
            captureCancellation.Cancel();
            try { await capture; } catch { /* Preserve the readiness/measurement failure. */ }
            throw;
        }
    }

    public async Task<TelemetryManifest> CaptureAsync(int seconds, CancellationToken cancellationToken = default)
    {
        await InvokeAsync("capture", selectedPath, output, seconds, cancellationToken);
        var samples = File.ReadAllLines(Path.Combine(output, "telemetry.ndjson")).Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => JsonNode.Parse(x)!).ToArray();
        if (samples.Length < seconds || samples.Any(x => x["cpuScope"]!.GetValue<string>() != "container-cgroup" ||
            x["networkScope"]!.GetValue<string>() != "host-shared")) throw new InvalidDataException("Telemetry capture is incomplete or has unexpected scopes.");
        var manifest = new TelemetryManifest(samples.Length, "container-cgroup", "host-shared", CampaignJson.HashTree(output));
        CampaignJson.WriteNew(Path.Combine(output, "telemetry-manifest.json"), manifest);
        return manifest;
    }

    private async Task InvokeAsync(string action, string inventory, string directory, int seconds, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        // Reserve before launching: the controller legitimately writes its own output files.
        // A second invocation must fail before it can overwrite those files or append telemetry.
        CampaignJson.WriteNew(Path.Combine(directory, "controller-" + action + "-started.json"),
            new { action, startedAtUtc = DateTimeOffset.UtcNow });
        var info = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-File", script, "-Action", action, "-ConfigPath", inventory,
            "-OutputDirectory", directory, "-DockerExecutable", docker, "-CaptureSeconds", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("Unable to launch stand controller.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(ct); }
        catch { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); throw; }
        var result = new { action, exitCode = process.ExitCode, stdout = await stdout, stderr = await stderr };
        CampaignJson.WriteNew(Path.Combine(directory, "controller-" + action + ".json"), result);
        if (process.ExitCode != 0) throw new InvalidOperationException($"Stand controller {action} failed: {result.stderr.Trim()}");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (started)
            {
                started = false;
                using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await InvokeAsync("stop", selectedPath, output, 1, cancel.Token);
            }
        }
        finally { foreach (var lease in leases.Values) lease.Dispose(); leases.Clear(); }
    }
}
