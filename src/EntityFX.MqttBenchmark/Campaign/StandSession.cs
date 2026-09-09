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
    private readonly List<FileStream> leases = new();
    private bool started;
    public Uri Endpoint { get; }
    private StandSession(string script, string selectedPath, string output, string docker, Uri endpoint) =>
        (this.script, this.selectedPath, this.output, this.docker, Endpoint) = (script, selectedPath, output, docker, endpoint);

    public static async Task<StandSession> StartAsync(string script, string inventoryPath, CampaignDefinition config,
        string broker, string output, string dockerExecutable = "docker", CancellationToken cancellationToken = default)
    {
        config.Validate();
        var inventory = JsonNode.Parse(File.ReadAllBytes(inventoryPath)) ?? throw new InvalidDataException("Missing stand inventory.");
        var brokers = inventory["brokers"]!.AsArray();
        var selected = brokers.Single(x => x!["name"]!.GetValue<string>() == broker)!;
        output = Path.GetFullPath(output);
        var selectedPath = Path.Combine(output, "stand.selected.json");
        var session = new StandSession(Path.GetFullPath(script), selectedPath, output, dockerExecutable,
            new Uri(selected["mqttUri"]!.GetValue<string>()));
        try
        {
            // A context lease spans preflight, measurement, capture and stop, also across different campaigns.
            var contexts = brokers.Select(x => x!["dockerContext"]?.GetValue<string>() ?? inventory["dockerContext"]!.GetValue<string>())
                .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal);
            foreach (var context in contexts)
            {
                var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(context)));
                session.leases.Add(new FileStream(Path.Combine(Path.GetTempPath(), "mqttbenchmark-stand-" + id + ".lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            var sourcePath = Path.Combine(output, "inventory-check", "stand.source.json");
            CampaignJson.WriteNew(sourcePath, inventory);
            // pull validates the unmodified source topology before any Docker operation, and establishes
            // build provenance required by validate for a custom active broker. Selection happens afterward.
            await session.InvokeAsync("pull", sourcePath, Path.GetDirectoryName(sourcePath)!, 1, cancellationToken);
            await session.InvokeAsync("validate", sourcePath, Path.GetDirectoryName(sourcePath)!, 1, cancellationToken);
            inventory["campaign"] = new JsonObject { ["deploymentMode"] = config.DeploymentMode, ["cpuMode"] = config.CpuMode };
            inventory["activeBroker"] = broker;
            foreach (var item in brokers) item!["loaded"] = item["name"]!.GetValue<string>() == broker;
            CampaignJson.WriteNew(selectedPath, inventory);
            await session.InvokeAsync("pull", selectedPath, output, 1, cancellationToken);
            await session.InvokeAsync("validate", selectedPath, output, 1, cancellationToken);
            session.started = true;
            await session.InvokeAsync("start", selectedPath, output, 1, cancellationToken);
            await session.InvokeAsync("health", selectedPath, output, 1, cancellationToken);
            return session;
        }
        catch
        {
            try { await session.DisposeAsync(); } catch { /* Original start/validation failure remains primary. */ }
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
        finally { foreach (var lease in leases) lease.Dispose(); leases.Clear(); }
    }
}
