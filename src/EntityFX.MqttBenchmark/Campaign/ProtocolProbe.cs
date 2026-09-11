namespace EntityFX.MqttBenchmark.Campaign;

public sealed record ProtocolProbe(int Qos, bool Connect, bool Subscribe, bool Publish, bool Delivered, string? Error);
