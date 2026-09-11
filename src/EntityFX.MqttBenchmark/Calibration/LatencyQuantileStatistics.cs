namespace EntityFX.MqttBenchmark.Calibration;

public sealed record LatencyQuantileStatistics(
    MetricStatistics MinMs,
    MetricStatistics P50Ms,
    MetricStatistics P75Ms,
    MetricStatistics P95Ms,
    MetricStatistics P99Ms,
    MetricStatistics MaxMs);
