using EntityFX.MqttBenchmark.Calibration;

namespace EntityFX.MqttBenchmark.Campaign;

/// <summary>
/// Сводит успешные попытки кампании в итоговые наблюдения по точкам матрицы.
/// Требует ПОЛНОГО покрытия матрицы: ожидаемый набор ключей выводится из
/// <see cref="CampaignDefinition.Expand"/>, а не из фактических попыток.
/// </summary>
public static class CampaignAggregator
{
    /// <summary>
    /// Агрегирует попытки в <see cref="BrokerObservations"/>.
    /// </summary>
    /// <param name="config">Валидированная конфигурация кампании (матрица, повторы).</param>
    /// <param name="attempts">История попыток журнала кампании (все статусы; учитываются только успешные).</param>
    /// <param name="hashes">Хеши исходных входов (конфиг, стенд, репозиторий) для provenance.</param>
    /// <returns>Сводный отчёт: статистика по каждой точке (брокер × размер × QoS × издатели) по повторам.</returns>
    /// <exception cref="InvalidDataException">Несколько идентичностей кампании, неизвестные ключи, неполное
    /// покрытие, отсутствие наблюдения у успешной попытки, либо некорректная латентность QoS 1/2.</exception>
    public static BrokerObservations Aggregate(CampaignDefinition config, IEnumerable<AttemptResult> attempts,
        IReadOnlyDictionary<string, string> hashes)
    {
        var expected = config.Expand().ToHashSet();
        var expectedKeyCount = expected.Count;
        var all = attempts.ToArray();
        if (all.Length == 0 || all.Select(x => x.Identity).Distinct().Count() != 1)
            throw new InvalidDataException("Aggregation requires exactly one campaign identity/deployment/CPU mode.");
        var identity = all[0].Identity;
        if (identity.CpuMode != config.CpuMode || identity.DeploymentMode != config.DeploymentMode)
            throw new InvalidDataException("Campaign modes differ from aggregation configuration.");
        if (all.Any(x => !expected.Contains(x.Key))) throw new InvalidDataException("Unknown campaign key.");
        var rows = all.Where(x => x.Status == "success").ToArray();
        // The required coverage is derived from the campaign definition itself, never hard-coded.
        if (rows.Length != expectedKeyCount || rows.Select(x => x.Key).Distinct().Count() != expectedKeyCount ||
            !expected.SetEquals(rows.Select(x => x.Key)))
            throw new InvalidDataException($"Aggregation requires {expectedKeyCount}/{expectedKeyCount} unique successful keys.");
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
                return new ObservationPoint(g.Key.Broker, g.Key.MessageBytes, g.Key.Qos, g.Key.Publishers, config.Repeats,
                    MetricStatistics.Calculate(measurements.Select(x => x.AttemptedRps)),
                    MetricStatistics.Calculate(measurements.Select(x => x.CompletedRps)),
                    MetricStatistics.Calculate(measurements.Select(x => x.PublishFailureRate)),
                    MetricStatistics.Calculate(measurements.Select(x => x.DeliveryLossRate)),
                    stats == null ? null : new LatencyQuantiles(stats.MinMs.Mean, stats.P50Ms.Mean, stats.P75Ms.Mean, stats.P95Ms.Mean, stats.P99Ms.Mean, stats.MaxMs.Mean),
                    stats, g.Average(x => x.Observation!.RttBaselineMs));
            }).ToArray();
        return new(CampaignDefaults.SchemaVersion, rows.Length, new(identity, hashes), points);
    }
}
