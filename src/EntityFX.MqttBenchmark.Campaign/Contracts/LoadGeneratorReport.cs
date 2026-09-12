namespace EntityFX.MqttBenchmark.Campaign;

public sealed record LoadGeneratorReport(LoadGeneratorInterface? Network, MeasurementWindow? Measurement,
    long TimestampFrequency, double ThresholdPercent, string NetworkAcceptanceMode, double? NetworkThresholdPercent, double SamplingIntervalSeconds, double MaximumIntervalSeconds,
    string AcceptanceRule, string CpuSource, string NetworkSource, LoadGeneratorAssessment Assessment);
