namespace EntityFX.MqttBenchmark.Campaign;

public sealed record RunObservation(CampaignKey Key, MeasurementSummary Measurement,
    DateTimeOffset MeasurementStartedUtc, DateTimeOffset MeasurementEndedUtc,
    string DrainReason, double DrainSeconds, double RttBaselineMs);
