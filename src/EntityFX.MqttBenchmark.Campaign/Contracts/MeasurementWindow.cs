namespace EntityFX.MqttBenchmark.Campaign;

public sealed record MeasurementWindow(long StartedTick, long EndedTick, DateTimeOffset StartedUtc, DateTimeOffset EndedUtc);
