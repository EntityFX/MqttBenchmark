namespace EntityFX.MqttBenchmark.Calibration;

public sealed record BrokerProfileEntry(string Name, IReadOnlyList<MessageSizeProfileEntry> MessageSizes);
