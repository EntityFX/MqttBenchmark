using System.Globalization;
using System.Text;
using System.Text.Json;

namespace EntityFX.MqttBenchmark.Calibration;

public static class AggregatedArtifactStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static void WriteJsonNew(string path, IReadOnlyList<AggregatedBenchmarkPoint> points)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(stream, points, JsonOptions);
    }

    public static void WriteCsvNew(string path, IReadOnlyList<AggregatedBenchmarkPoint> points)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        var header = new List<string> { "broker", "messageBytes", "qos", "clients", "repeats" };
        foreach (var metric in new[] { "completedRps", "publishFailureRate", "deliveryLossRate" })
            AddStatHeader(header, metric);
        foreach (var knot in new[] { "latencyMinMs", "latencyP50Ms", "latencyP75Ms", "latencyP95Ms", "latencyP99Ms", "latencyMaxMs" })
            AddStatHeader(header, knot);
        writer.WriteLine(string.Join(',', header));

        foreach (var point in points)
        {
            var values = new List<string>
            {
                point.Broker, I(point.MessageBytes), I(point.Qos), I(point.Clients), I(point.Repeats)
            };
            AddStats(values, point.CompletedRps);
            AddStats(values, point.PublishFailureRate);
            AddStats(values, point.DeliveryLossRate);
            var latency = point.ProcessingLatencyQuantiles;
            foreach (var stat in latency == null
                ? Enumerable.Repeat<MetricStatistics?>(null, 6)
                : new MetricStatistics?[] { latency.MinMs, latency.P50Ms, latency.P75Ms,
                    latency.P95Ms, latency.P99Ms, latency.MaxMs })
            {
                if (stat == null) values.AddRange(Enumerable.Repeat(string.Empty, 7));
                else AddStats(values, stat);
            }
            writer.WriteLine(string.Join(',', values));
        }
    }

    private static void AddStatHeader(ICollection<string> header, string prefix)
    {
        foreach (var suffix in new[] { "Mean", "StdDev", "Ci95HalfWidth", "Ci95Lower", "Ci95Upper", "Min", "Max" })
            header.Add(prefix + suffix);
    }

    private static void AddStats(ICollection<string> values, MetricStatistics stat)
    {
        values.Add(D(stat.Mean));
        values.Add(D(stat.StandardDeviation));
        values.Add(D(stat.Ci95HalfWidth));
        values.Add(D(stat.Ci95Lower));
        values.Add(D(stat.Ci95Upper));
        values.Add(D(stat.Min));
        values.Add(D(stat.Max));
    }

    private static string D(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static string I(int value) => value.ToString(CultureInfo.InvariantCulture);
}
