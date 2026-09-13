namespace EntityFX.MqttBenchmark.Campaign;

/// <summary>
/// Сборка неизменяемого <see cref="LoadGeneratorReport"/> (provenance, политики CPU/network,
/// применяемые правила и результат оценки) из наблюдаемых счётчиков, окна измерения
/// и итоговой оценки.
/// </summary>
public static class LoadGeneratorReportFactory
{
    public static LoadGeneratorReport Create(LoadGeneratorInterface? network, MeasurementWindow? window, long frequency,
        double? networkThresholdPercent, double cpuThresholdPercent, LoadGeneratorAssessment assessment,
        double maximumIntervalSeconds = LoadGeneratorAssessment.MaximumIntervalSeconds) =>
        new(network, window, frequency, cpuThresholdPercent,
            networkThresholdPercent.HasValue ? "enforced" : "informational", networkThresholdPercent,
            1, maximumIntervalSeconds,
            "max-overlapping-interval; no prorating; network=(rx+tx)/link-speed; " +
                (networkThresholdPercent.HasValue ? "network threshold enforced" : "network informational"),
            "Windows GetSystemTimes (kernel includes idle)",
            "OS-routed NetworkInterface cumulative byte counters", assessment);
}