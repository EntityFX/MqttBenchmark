namespace EntityFX.MqttBenchmark.Campaign;

public sealed record BrokerObservations(int SchemaVersion, int RunCount, ObservationProvenance Provenance,
    IReadOnlyList<ObservationPoint> Points);
