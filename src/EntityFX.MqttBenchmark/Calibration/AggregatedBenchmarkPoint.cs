namespace EntityFX.MqttBenchmark.Calibration;

public sealed record AggregatedBenchmarkPoint(
    string Broker,
    int MessageBytes,
    int Qos,
    int Clients,
    int Repeats,
    MetricStatistics CompletedRps,
    MetricStatistics PublishFailureRate,
    MetricStatistics DeliveryLossRate,
    LatencyQuantileStatistics? ProcessingLatencyQuantiles);
