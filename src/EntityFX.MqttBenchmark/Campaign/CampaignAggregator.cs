using EntityFX.MqttBenchmark.Calibration;

namespace EntityFX.MqttBenchmark.Campaign;

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
