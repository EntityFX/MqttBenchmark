using EntityFX.MqttBenchmark.Calibration;

namespace EntityFX.MqttBenchmark.Campaign;

/// <summary>
/// Итоговая сводка измерения одного прогона: счётчики публикаций/доставок, наблюдаемая
/// длительность и (для QoS 1/2) распределение латентности завершения. Производные метрики —
/// RPS, частота ошибок публикации и потерь доставки.
/// </summary>
public sealed record MeasurementSummary(long AttemptedPublishes, long CompletedPublishes,
    long FailedPublishes, long UniqueDeliveries, long DuplicateDeliveries, long UnexpectedDeliveries,
    long DeliveredAfterFailedPublish, long CompletedIdsDelivered, double ActualMeasurementSeconds,
    LatencyQuantiles? PublishLatencyMs, string LatencyStatus, IReadOnlyDictionary<string, long> ErrorReasons)
{
    public double AttemptedRps => AttemptedPublishes / ActualMeasurementSeconds;
    public double CompletedRps => CompletedPublishes / ActualMeasurementSeconds;
    public double PublishFailureRate => AttemptedPublishes == 0 ? 0 : FailedPublishes / (double)AttemptedPublishes;
    public double DeliveryLossRate => CompletedPublishes == 0 ? 0 : 1 - CompletedIdsDelivered / (double)CompletedPublishes;
}
