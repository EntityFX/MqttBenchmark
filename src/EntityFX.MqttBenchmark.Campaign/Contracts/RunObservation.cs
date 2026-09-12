namespace EntityFX.MqttBenchmark.Campaign;

/// <summary>Результат одного успешного прогона нагрузки: сводка измерения, границы окна,
/// причина и длительность drain, RTT-базис preflight. Проверяется <see cref="CampaignJournal.ValidateObservation"/>.</summary>
public sealed record RunObservation(CampaignKey Key, MeasurementSummary Measurement,
    DateTimeOffset MeasurementStartedUtc, DateTimeOffset MeasurementEndedUtc,
    string DrainReason, double DrainSeconds, double RttBaselineMs);
