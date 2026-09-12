namespace EntityFX.MqttBenchmark.Campaign;

/// <summary>Итоговый отчёт агрегации кампании (<c>broker-observations.v3.json</c>): число успешных
/// прогонов, provenance входов и статистика по каждой точке матрицы.</summary>
public sealed record BrokerObservations(int SchemaVersion, int RunCount, ObservationProvenance Provenance,
    IReadOnlyList<ObservationPoint> Points);
