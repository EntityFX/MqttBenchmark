using System.Security.Cryptography;
using System.Text.Json;
using EntityFX.MqttBenchmark.Calibration;

namespace EntityFX.MqttBenchmark.Campaign;

public sealed record CampaignIdentity(string CampaignId, string ConfigSha256, string StandSha256,
    string DeploymentMode, string CpuMode, string MqttBenchmarkCommitSha);
public sealed record RunObservation(CampaignKey Key, MeasurementSummary Measurement,
    DateTimeOffset MeasurementStartedUtc, DateTimeOffset MeasurementEndedUtc,
    string DrainReason, double DrainSeconds, double RttBaselineMs);
public sealed record AttemptResult(CampaignIdentity Identity, CampaignKey Key, int Attempt,
    string Status, string? Failure, RunObservation? Observation);
public sealed record ObservationProvenance(CampaignIdentity Identity, IReadOnlyDictionary<string, string> InputSha256);
public sealed record ObservationPoint(string Broker, int MessageBytes, int Qos, int Publishers,
    int Repeats, MetricStatistics AttemptedRps, MetricStatistics CompletedRps,
    MetricStatistics PublishFailureRate, MetricStatistics DeliveryLossRate,
    LatencyQuantiles? ObservedLatencyQuantiles, LatencyQuantileStatistics? ObservedLatencyStatistics,
    double RttBaselineMs);
public sealed record BrokerObservations(int SchemaVersion, int RunCount, ObservationProvenance Provenance,
    IReadOnlyList<ObservationPoint> Points);

public static class CampaignJson
{
    public static JsonSerializerOptions Options { get; } = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Options)
        ?? throw new InvalidDataException($"Empty JSON document: {path}");
    public static void WriteNew<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, value, Options);
        stream.Flush(true);
    }
    public static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    public static IReadOnlyDictionary<string, string> HashTree(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Where(p => Path.GetFileName(p) != ".lock")
        .OrderBy(p => p, StringComparer.Ordinal).ToDictionary(p => Path.GetRelativePath(root, p).Replace('\\', '/'), HashFile, StringComparer.Ordinal);
}

public sealed class CampaignExhaustedException : IOException
{
    public CampaignExhaustedException(string key) : base($"Campaign stopped: {key} exhausted three attempts.") { }
}

public sealed class CampaignJournal : IDisposable
{
    private readonly string root;
    private readonly CampaignIdentity identity;
    private readonly FileStream lease;
    private CampaignJournal(string root, CampaignIdentity identity, FileStream lease) => (this.root, this.identity, this.lease) = (root, identity, lease);
    public static CampaignJournal Open(string directory, CampaignIdentity identity, bool resume)
    {
        var root = Path.GetFullPath(directory);
        Directory.CreateDirectory(root);
        var lease = new FileStream(Path.Combine(root, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var manifest = Path.Combine(root, "campaign.json");
            if (resume)
            {
                if (CampaignJson.Read<CampaignIdentity>(manifest) != identity)
                    throw new InvalidDataException("Resume identity differs from immutable campaign manifest.");
            }
            else
            {
                if (Directory.EnumerateFileSystemEntries(root).Any(x => Path.GetFileName(x) != ".lock"))
                    throw new IOException("Campaign directory already exists; use --resume with the same inputs.");
                CampaignJson.WriteNew(manifest, identity);
            }
            var rows = ReadAttempts(root);
            if (rows.Any(x => x.Identity != identity)) throw new InvalidDataException("Attempt belongs to a different campaign identity.");
            return new(root, identity, lease);
        }
        catch { lease.Dispose(); throw; }
    }

    public static IReadOnlyList<AttemptResult> ReadAttempts(string directory)
    {
        var rows = new List<AttemptResult>();
        var attemptsRoot = Path.Combine(directory, "attempts");
        if (!Directory.Exists(attemptsRoot)) return rows;
        foreach (var keyDirectory in Directory.EnumerateDirectories(attemptsRoot))
        {
            var previous = 0;
            foreach (var attemptDirectory in Directory.EnumerateDirectories(keyDirectory).OrderBy(x => x, StringComparer.Ordinal))
            {
                var start = CampaignJson.Read<AttemptResult>(Path.Combine(attemptDirectory, "started.json"));
                if (start.Attempt != ++previous || start.Attempt > 3 || Path.GetFileName(keyDirectory) != start.Key.Key ||
                    Path.GetFileName(attemptDirectory) != $"attempt-{start.Attempt:00}" || start.Status != "started")
                    throw new InvalidDataException("Invalid or non-contiguous attempt history.");
                var resultPath = Path.Combine(attemptDirectory, "result.json");
                var sealPath = Path.Combine(attemptDirectory, "sha256.json");
                // A crash before sealing consumes an attempt and preserves every byte for investigation.
                if (!File.Exists(sealPath)) { rows.Add(start with { Status = "interrupted", Failure = "Attempt was not sealed." }); continue; }
                var expected = CampaignJson.Read<Dictionary<string, string>>(sealPath);
                var actual = CampaignJson.HashTree(attemptDirectory).Where(x => x.Key != "sha256.json").ToDictionary(x => x.Key, x => x.Value);
                if (expected.Count != actual.Count || expected.Any(x => !actual.TryGetValue(x.Key, out var value) || value != x.Value))
                    throw new InvalidDataException("Immutable attempt hash verification failed.");
                var result = CampaignJson.Read<AttemptResult>(resultPath);
                if (result.Identity != start.Identity || result.Key != start.Key || result.Attempt != start.Attempt ||
                    result.Status is not ("success" or "failed") || (result.Status == "success" && result.Observation == null))
                    throw new InvalidDataException("Attempt result does not match reserved identity.");
                rows.Add(result);
            }
        }
        return rows;
    }

    public async Task ExecuteAsync(IEnumerable<CampaignKey> keys, Func<CampaignKey, string, int, CancellationToken, Task<RunObservation>> execute,
        int? maxRuns = null, CancellationToken cancellationToken = default)
    {
        var existing = ReadAttempts(root).ToList();
        foreach (var exhausted in existing.GroupBy(x => x.Key).Where(x => x.Count() >= 3 && x.All(a => a.Status != "success")))
            throw new CampaignExhaustedException(exhausted.Key.Key);
        var completed = 0;
        foreach (var key in keys)
        {
            if (existing.Any(x => x.Key == key && x.Status == "success")) continue;
            if (maxRuns.HasValue && completed >= maxRuns.Value) break;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var number = existing.Count(x => x.Key == key) + 1;
                if (number > 3) throw new CampaignExhaustedException(key.Key);
                var path = Path.Combine(root, "attempts", key.Key, $"attempt-{number:00}");
                if (Directory.Exists(path)) throw new IOException("Attempt directory already exists.");
                Directory.CreateDirectory(path);
                var row = new AttemptResult(identity, key, number, "started", null, null);
                CampaignJson.WriteNew(Path.Combine(path, "started.json"), row);
                try
                {
                    var observation = await execute(key, path, number, cancellationToken);
                    ValidateObservation(key, observation);
                    row = row with { Status = "success", Observation = observation };
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) { row = row with { Status = "failed", Failure = error.GetType().Name + ": " + error.Message }; }
                CampaignJson.WriteNew(Path.Combine(path, "result.json"), row);
                CampaignJson.WriteNew(Path.Combine(path, "sha256.json"), CampaignJson.HashTree(path));
                existing.Add(row);
                if (row.Status == "success") { completed++; break; }
                if (number == 3) throw new CampaignExhaustedException(key.Key);
            }
        }
    }

    internal static void ValidateObservation(CampaignKey key, RunObservation observation)
    {
        var m = observation.Measurement;
        if (key != observation.Key || !double.IsFinite(m.ActualMeasurementSeconds) || m.ActualMeasurementSeconds <= 0 ||
            observation.MeasurementEndedUtc <= observation.MeasurementStartedUtc || m.AttemptedPublishes <= 0 ||
            m.CompletedPublishes < 0 || m.FailedPublishes < 0 || m.AttemptedPublishes != m.CompletedPublishes + m.FailedPublishes ||
            m.CompletedIdsDelivered < 0 || m.CompletedIdsDelivered > m.CompletedPublishes ||
            m.UniqueDeliveries < 0 || m.DuplicateDeliveries < 0 || m.UnexpectedDeliveries < 0 ||
            m.DeliveredAfterFailedPublish < 0 || m.DeliveredAfterFailedPublish > m.FailedPublishes ||
            m.ErrorReasons.Values.Sum() != m.FailedPublishes || !double.IsFinite(observation.RttBaselineMs) || observation.RttBaselineMs < 0)
            throw new InvalidDataException("Inconsistent measurement counts, identity, observed duration or RTT.");
        if ((key.Qos == 0 && (m.PublishLatencyMs != null || m.LatencyStatus != "notApplicable")) ||
            (key.Qos > 0 && m.CompletedPublishes > 0 && (m.PublishLatencyMs == null || m.LatencyStatus != "observed")))
            throw new InvalidDataException("Latency applicability differs from QoS/completion counts.");
        m.PublishLatencyMs?.Validate();
    }
    public void Dispose() => lease.Dispose();
}

public static class CampaignAggregator
{
    public static BrokerObservations Aggregate(CampaignDefinition config, IEnumerable<AttemptResult> attempts,
        IReadOnlyDictionary<string, string> hashes)
    {
        var expected = config.Expand().ToHashSet();
        var all = attempts.ToArray();
        if (all.Length == 0 || all.Select(x => x.Identity).Distinct().Count() != 1)
            throw new InvalidDataException("Aggregation requires exactly one campaign identity/deployment/CPU mode.");
        var identity = all[0].Identity;
        if (identity.CpuMode != config.CpuMode || identity.DeploymentMode != config.DeploymentMode)
            throw new InvalidDataException("Campaign modes differ from aggregation configuration.");
        if (all.Any(x => !expected.Contains(x.Key))) throw new InvalidDataException("Unknown campaign key.");
        var rows = all.Where(x => x.Status == "success").ToArray();
        if (rows.Length != 288 || rows.Select(x => x.Key).Distinct().Count() != 288 || !expected.SetEquals(rows.Select(x => x.Key)))
            throw new InvalidDataException("Aggregation requires 288/288 unique successful keys.");
        foreach (var row in rows) CampaignJournal.ValidateObservation(row.Key,
            row.Observation ?? throw new InvalidDataException("Successful attempt is missing observation."));
        var points = rows.GroupBy(x => (x.Key.Broker, x.Key.MessageBytes, x.Key.Qos, x.Key.Publishers))
            .OrderBy(g => g.Key.Broker, StringComparer.Ordinal).ThenBy(g => g.Key.MessageBytes).ThenBy(g => g.Key.Qos).ThenBy(g => g.Key.Publishers)
            .Select(g =>
            {
                var measurements = g.Select(x => x.Observation!.Measurement).ToArray();
                var latency = measurements.Select(x => x.PublishLatencyMs).ToArray();
                MetricStatistics Latency(Func<LatencyQuantiles, double> select) => MetricStatistics.Calculate(latency.Select(x => select(x!)));
                var stats = g.Key.Qos == 0 ? null : latency.Any(x => x == null)
                    ? throw new InvalidDataException("QoS 1/2 requires observed completion latency for every repeat.")
                    : new LatencyQuantileStatistics(Latency(x => x.MinMs), Latency(x => x.P50Ms), Latency(x => x.P75Ms),
                        Latency(x => x.P95Ms), Latency(x => x.P99Ms), Latency(x => x.MaxMs));
                return new ObservationPoint(g.Key.Broker, g.Key.MessageBytes, g.Key.Qos, g.Key.Publishers, 3,
                    MetricStatistics.Calculate(measurements.Select(x => x.AttemptedRps)),
                    MetricStatistics.Calculate(measurements.Select(x => x.CompletedRps)),
                    MetricStatistics.Calculate(measurements.Select(x => x.PublishFailureRate)),
                    MetricStatistics.Calculate(measurements.Select(x => x.DeliveryLossRate)),
                    stats == null ? null : new LatencyQuantiles(stats.MinMs.Mean, stats.P50Ms.Mean, stats.P75Ms.Mean, stats.P95Ms.Mean, stats.P99Ms.Mean, stats.MaxMs.Mean),
                    stats, g.Average(x => x.Observation!.RttBaselineMs));
            }).ToArray();
        return new(3, rows.Length, new(identity, hashes), points);
    }
}
