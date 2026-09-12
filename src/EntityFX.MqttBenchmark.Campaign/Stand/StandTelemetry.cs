using System.Text.Json.Nodes;

namespace EntityFX.MqttBenchmark.Campaign;

/// <summary>
/// Телеметрия стенда: периодический capture счётчиков контейнера, проверка покрытия
/// workload-end fence'ом и сборка неизменяемого <see cref="TelemetryManifest"/>.
/// Не знает о жизненном цикле (start/stop).
/// </summary>
public sealed class StandTelemetry
{
    private readonly StandLifecycle lifecycle;
    private readonly string selectedPath, output;

    public StandTelemetry(StandLifecycle lifecycle, string selectedPath, string output)
    {
        this.lifecycle = lifecycle;
        this.selectedPath = selectedPath;
        this.output = output;
    }

    public async Task<TelemetryManifest> CaptureAsync(int seconds, CancellationToken cancellationToken = default) =>
        WriteTelemetryManifest(await CaptureSamplesAsync(seconds, cancellationToken));

    public async Task<int> CaptureSamplesAsync(int seconds, CancellationToken cancellationToken)
    {
        await lifecycle.InvokeAsync("capture", selectedPath, output, seconds, cancellationToken);
        var samples = File.ReadAllLines(Path.Combine(output, "telemetry.ndjson")).Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => JsonNode.Parse(x)!).ToArray();
        if (samples.Length < seconds || samples.Any(x => x["cpuScope"]!.GetValue<string>() != "container-cgroup" ||
            x["networkScope"]!.GetValue<string>() != "host-shared")) throw new InvalidDataException("Telemetry capture is incomplete or has unexpected scopes.");
        return samples.Length;
    }

    public TelemetryManifest WriteTelemetryManifest(int sampleCount)
    {
        var manifest = new TelemetryManifest(sampleCount, "container-cgroup", "host-shared", CampaignJson.HashTree(output));
        CampaignJson.WriteNew(Path.Combine(output, "telemetry-manifest.json"), manifest);
        return manifest;
    }

    public void RequireActiveSampler()
    {
        if (File.Exists(Path.Combine(output, "sampler-ended.json")))
            throw new InvalidDataException("Telemetry sampler ended outside the required workload coverage.");
    }
}