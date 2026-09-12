namespace EntityFX.MqttBenchmark.Campaign;

/// <summary>Запись попытки журнала кампании. Статусы: <c>started</c> (зарезервирована, без результата),
/// <c>success</c> (с наблюдением), <c>failed</c> (с причиной), <c>interrupted</c> (обнаружена при чтении
/// незапечатанной). Успешная попытка обязана содержать валидное <see cref="Observation"/>.</summary>
public sealed record AttemptResult(CampaignIdentity Identity, CampaignKey Key, int Attempt,
    string Status, string? Failure, RunObservation? Observation);
