using System.Text.Json.Nodes;

namespace EntityFX.MqttBenchmark.Campaign;

/// <summary>
/// Телеметрия стенда: периодический capture счётчиков юнита брокера (контейнер или нативный процесс),
/// проверка покрытия workload-end fence'ом и сборка неизменяемого <see cref="TelemetryManifest"/>.
/// Не знает о жизненном цикле (start/stop).
/// </summary>
public sealed class StandTelemetry
{
    /// <summary>CPU-скоуп для контейнерного стенда (счётчики cgroup брокера).</summary>
    public const string ContainerCpuScope = "container-cgroup";
    /// <summary>CPU-скоуп для нативного стенда (CPU времени самого брокер-процесса на закреплённых ядрах).</summary>
    public const string NativeCpuScope = "process-pinned";
    /// <summary>Единый сетевой скоуп: счётчики общего хоста (shared host NIC).</summary>
    public const string NetworkScope = "host-shared";

    private readonly StandLifecycle lifecycle;
    private readonly string selectedPath, output, cpuScope;

    public StandTelemetry(StandLifecycle lifecycle, string selectedPath, string output, string? cpuScope = null)
    {
        this.lifecycle = lifecycle;
        this.selectedPath = selectedPath;
        this.output = output;
        this.cpuScope = string.IsNullOrWhiteSpace(cpuScope) ? ContainerCpuScope : cpuScope!;
    }

    /// <summary>CPU-скоуп телеметрии для времени исполнения брокера (default — контейнер).</summary>
    public static string CpuScopeForRuntime(string? runtime) =>
        string.Equals(runtime, "native", StringComparison.OrdinalIgnoreCase) ? NativeCpuScope : ContainerCpuScope;

    public async Task<TelemetryManifest> CaptureAsync(int seconds, CancellationToken cancellationToken = default) =>
        WriteTelemetryManifest(await CaptureSamplesAsync(seconds, cancellationToken));

    public async Task<int> CaptureSamplesAsync(int seconds, CancellationToken cancellationToken)
    {
        await lifecycle.InvokeAsync("capture", selectedPath, output, seconds, cancellationToken);
        var samples = File.ReadAllLines(Path.Combine(output, "telemetry.ndjson")).Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => JsonNode.Parse(x)!).ToArray();
        ValidateSamples(samples, seconds, cpuScope);
        return samples.Length;
    }

    /// <summary>Чистая проверка NDJSON-сэмплов: минимум <paramref name="seconds"/> строк и точные скоупы юнита.</summary>
    public static void ValidateSamples(IReadOnlyList<JsonNode> samples, int seconds, string cpuScope)
    {
        if (seconds < 1) throw new ArgumentOutOfRangeException(nameof(seconds));
        if (samples.Count < seconds)
            throw new InvalidDataException($"Telemetry capture is incomplete: expected at least {seconds} samples, got {samples.Count}.");
        if (samples.Any(x => x["cpuScope"] == null || x["cpuScope"]!.GetValue<string>() != cpuScope ||
                x["networkScope"] == null || x["networkScope"]!.GetValue<string>() != NetworkScope))
            throw new InvalidDataException($"Telemetry capture has unexpected scopes (expected cpuScope='{cpuScope}', networkScope='{NetworkScope}').");
    }

    public TelemetryManifest WriteTelemetryManifest(int sampleCount)
    {
        var manifest = new TelemetryManifest(sampleCount, cpuScope, NetworkScope, CampaignJson.HashTree(output));
        CampaignJson.WriteNew(Path.Combine(output, "telemetry-manifest.json"), manifest);
        return manifest;
    }

    public void RequireActiveSampler()
    {
        if (File.Exists(Path.Combine(output, "sampler-ended.json")))
            throw new InvalidDataException("Telemetry sampler ended outside the required workload coverage.");
    }
}