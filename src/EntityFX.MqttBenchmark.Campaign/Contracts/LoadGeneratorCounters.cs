namespace EntityFX.MqttBenchmark.Campaign;

public sealed record LoadGeneratorCounters(ulong Idle100ns, ulong Kernel100ns, ulong User100ns,
    ulong ReceivedBytes, ulong SentBytes, long LinkSpeedBitsPerSecond);
