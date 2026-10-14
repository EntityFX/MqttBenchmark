using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace EntityFX.MqttBenchmark.Campaign;

public sealed record ExternalSamplerReady(long TimestampFrequency, int ProcessId, int ParentProcessId, long FirstReadStartedTick);

/// <summary>Small independent process: generator GC pauses cannot suspend its counter reads.
/// Both processes use the OS monotonic clock and the same strict assessment contract.</summary>
public static class ExternalGuardSampler
{
    public static int Run(Uri endpoint, string directory, int parentId)
    {
        using var parent = Process.GetProcessById(parentId);
        var counters = LoadGeneratorGuard.CreateDefaultCounters(endpoint);
        var clock = new LoadGeneratorClock();
        using var writer = new StreamWriter(new FileStream(Path.Combine(directory, "load-generator-samples.ndjson"),
            FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        LoadGeneratorSample Read()
        {
            var started = clock.Timestamp; var utc = clock.UtcNow;
            var value = counters.Read();
            var sample = new LoadGeneratorSample(started, clock.Timestamp, utc, clock.UtcNow, value);
            writer.WriteLine(JsonSerializer.Serialize(sample, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            return sample;
        }
        var sample = Read();
        CampaignJson.WriteNew(Path.Combine(directory, "external-sampler-ready.json"), new ExternalSamplerReady(clock.Frequency, Environment.ProcessId, parentId, sample.ReadStartedTick));
        var ended = Path.Combine(directory, "load-generator-measurement-ended.json");
        while (!parent.HasExited && !File.Exists(Path.Combine(directory, "external-sampler-stop")))
        {
            clock.WaitUntil(sample.ReadStartedTick + clock.Frequency, CancellationToken.None);
            sample = Read();
            if (!File.Exists(ended)) continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(ended));
                if (sample.ReadStartedTick >= document.RootElement.GetProperty("tick").GetInt64()) return 0;
            }
            catch (IOException) { } // The parent can still hold the CreateNew writer.
            catch (JsonException) { } // Partial publication is retried; never accepted as a boundary.
        }
        return 0;
    }
}
