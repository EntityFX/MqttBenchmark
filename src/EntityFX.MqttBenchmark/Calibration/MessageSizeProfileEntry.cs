namespace EntityFX.MqttBenchmark.Calibration;

public sealed record MessageSizeProfileEntry(int MessageBytes, IReadOnlyList<QosProfileEntry> Qos);
