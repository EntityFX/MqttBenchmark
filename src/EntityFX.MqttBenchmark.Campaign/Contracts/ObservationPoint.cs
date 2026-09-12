using EntityFX.MqttBenchmark.Calibration;

namespace EntityFX.MqttBenchmark.Campaign;

/// <summary>Статистика по одной точке матрицы (брокер × размер × QoS × издатели), агрегированная
/// по повторам: распределения RPS (attempted/completed), частот ошибок публикации и потерь доставки,
/// квантили латентности (для QoS 1/2) и средний RTT-базис.</summary>
public sealed record ObservationPoint(string Broker, int MessageBytes, int Qos, int Publishers,
    int Repeats, MetricStatistics AttemptedRps, MetricStatistics CompletedRps,
    MetricStatistics PublishFailureRate, MetricStatistics DeliveryLossRate,
    LatencyQuantiles? ObservedLatencyQuantiles, LatencyQuantileStatistics? ObservedLatencyStatistics,
    double RttBaselineMs);
