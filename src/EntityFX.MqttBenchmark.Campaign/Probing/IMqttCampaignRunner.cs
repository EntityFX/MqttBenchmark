namespace EntityFX.MqttBenchmark.Campaign;

/// <summary>
/// Контракт кампании-раннера. Потребители зависят от этого интерфейса, а не от конкретного
/// <see cref="MqttCampaignRunner"/>, что позволяет подменять реализацию (например, в тестах)
/// без изменения потребителей.
/// </summary>
public interface IMqttCampaignRunner
{
    /// <summary>
    /// Выполняет протокольный preflight для брокера: серия RTT-эхо проб (CONNECT/PUBLISH/SUBSCRIBE/
    /// DISCONNECT) и ожидание первого ответа <c>$SYS/broker/version</c>.
    /// </summary>
    /// <param name="broker">Имя брокера из конфигурации кампании (для диагностики).</param>
    /// <param name="endpoint">MQTT-адрес стенда.</param>
    /// <param name="rttProbes">Число RTT-эхо проб; успех требует измерения всех проб.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Протокольный отчёт с медианным RTT-базисом (<c>RttBaselineMs</c>), если брокер доступен;
    /// иначе отчёт с <c>Success = false</c> и описанием причины.</returns>
    Task<ProtocolPreflight> PreflightAsync(string broker, Uri endpoint, int rttProbes,
        CancellationToken cancellationToken = default);

    /// <summary>Перегрузка без числа RTT-проб — использует <see cref="CampaignDefaults.PreflightRttProbes"/>
    /// (обратная совместимость для прямых тестовых вызовов).</summary>
    Task<ProtocolPreflight> PreflightAsync(string broker, Uri endpoint, CancellationToken cancellationToken = default) =>
        PreflightAsync(broker, endpoint, CampaignDefaults.PreflightRttProbes, cancellationToken);

    /// <summary>
    /// Выполняет один прогон нагрузки по ключу матрицы: warmup → измерение → bounded in-flight
    /// completion → drain → cooldown, с записью артефактов в <paramref name="directory"/>.
    /// </summary>
    /// <param name="config">Валидированная конфигурация кампании (тайминги фаз).</param>
    /// <param name="key">Ключ матрицы (брокер, размер, QoS, издатели, повтор).</param>
    /// <param name="endpoint">MQTT-адрес стенда.</param>
    /// <param name="campaignId">Идентификатор кампании (общий для всех попыток).</param>
    /// <param name="runId">Уникальный идентификатор прогона (ключ + номер попытки).</param>
    /// <param name="directory">Каталог артефактов попытки.</param>
    /// <param name="rttBaselineMs">Медианный RTT-базис из preflight (для фильтрации собственного времени).</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <param name="measurementObserver">Наблюдатель окна измерения (load-guard); <c>null</c>, если не требуется.</param>
    /// <returns>Валидированное наблюдение прогона (счётчики публикаций/доставок и латентность).</returns>
    /// <exception cref="OperationCanceledException">Отмена через <paramref name="cancellationToken"/>.</exception>
    Task<RunObservation> RunAsync(CampaignDefinition config, CampaignKey key, Uri endpoint, string campaignId,
        string runId, string directory, double rttBaselineMs, CancellationToken cancellationToken = default,
        IMeasurementWindowObserver? measurementObserver = null);
}