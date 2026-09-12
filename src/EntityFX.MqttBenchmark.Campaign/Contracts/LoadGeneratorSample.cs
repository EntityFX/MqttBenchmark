namespace EntityFX.MqttBenchmark.Campaign;

public sealed record LoadGeneratorSample(long ReadStartedTick, long ReadEndedTick,
    DateTimeOffset ReadStartedUtc, DateTimeOffset ReadEndedUtc, LoadGeneratorCounters Counters);
