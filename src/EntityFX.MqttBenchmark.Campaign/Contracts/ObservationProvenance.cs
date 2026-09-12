namespace EntityFX.MqttBenchmark.Campaign;

/// <summary>Provenance агрегации: идентичность кампании и SHA-256 всех входных файлов
/// (конфиг, инвентарь стенда, дерево результатов, коммит).</summary>
public sealed record ObservationProvenance(CampaignIdentity Identity, IReadOnlyDictionary<string, string> InputSha256);
