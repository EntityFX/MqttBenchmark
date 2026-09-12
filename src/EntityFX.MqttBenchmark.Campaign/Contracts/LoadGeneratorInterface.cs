namespace EntityFX.MqttBenchmark.Campaign;

public sealed record LoadGeneratorInterface(string Id, string Name, string Description, int Index,
    long LinkSpeedBitsPerSecond, string Destination, string InterfaceType, AdapterHardwareEvidence? HardwareEvidence = null);
