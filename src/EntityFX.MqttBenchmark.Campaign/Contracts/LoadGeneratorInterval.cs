namespace EntityFX.MqttBenchmark.Campaign;

public sealed record LoadGeneratorInterval(long StartedTick, long EndedTick, double CpuPercent,
    double RxBitsPerSecond, double TxBitsPerSecond, double NetworkPercent);
