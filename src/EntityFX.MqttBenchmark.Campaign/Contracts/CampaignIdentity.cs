namespace EntityFX.MqttBenchmark.Campaign;

/// <summary>
/// Неизменяемая идентичность кампании: привязывает прогон к хешам входов (конфиг, стенд),
/// режимам развёртывания/CPU и коммиту инструментария. Журнал и агрегатор требуют точного
/// совпадения идентичности между попытками и с конфигурацией.
/// </summary>
public sealed record CampaignIdentity(string CampaignId, string ConfigSha256, string StandSha256,
    string DeploymentMode, string CpuMode, string MqttBenchmarkCommitSha);
