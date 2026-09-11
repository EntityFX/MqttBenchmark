using EntityFX.MqttBenchmark.Calibration;

namespace EntityFX.MqttBenchmark.Campaign;

public sealed record ObservationPoint(string Broker, int MessageBytes, int Qos, int Publishers,
    int Repeats, MetricStatistics AttemptedRps, MetricStatistics CompletedRps,
    MetricStatistics PublishFailureRate, MetricStatistics DeliveryLossRate,
    LatencyQuantiles? ObservedLatencyQuantiles, LatencyQuantileStatistics? ObservedLatencyStatistics,
    double RttBaselineMs);
