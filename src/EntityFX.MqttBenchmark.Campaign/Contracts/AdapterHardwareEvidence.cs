namespace EntityFX.MqttBenchmark.Campaign;

public sealed record AdapterHardwareEvidence(int InterfaceIndex, string InterfaceId, bool? HardwareInterface,
    bool? Virtual, string Source, DateTimeOffset ObservedUtc, string? Error = null);
