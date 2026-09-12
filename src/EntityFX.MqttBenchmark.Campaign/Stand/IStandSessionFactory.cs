namespace EntityFX.MqttBenchmark.Campaign;

/// <summary>
/// Фабрика сессий стенда. Потребители зависят от этой абстракции, а не от конкретного
/// <see cref="StandSession"/>; по умолчанию создаётся реальная сессия.
/// </summary>
public interface IStandSessionFactory
{
    /// <summary>
    /// Разворачивает стенд выбранного брокера и возвращает активную сессию.
    /// </summary>
    /// <param name="script">Путь к управляющему скрипту стенда (<c>BrokerStand.ps1</c>).</param>
    /// <param name="inventoryPath">Путь к инвентарю стенда (<c>broker-stand.local.json</c>).</param>
    /// <param name="config">Валидированная конфигурация кампании (custom-образы, режимы развёртывания).</param>
    /// <param name="broker">Имя брокера из инвентаря.</param>
    /// <param name="output">Каталог для артефактов стенда (логи контроллера и т.п.).</param>
    /// <param name="dockerExecutable">Исполняемый файл Docker (по умолчанию <c>docker</c>).</param>
    /// <param name="cancellationToken">Токен отмены; при отмене стенд останавливается корректно.</param>
    /// <param name="expectedStandSha256">Ожидаемый SHA-256 инвентаря стенда (контроль неизменности между прогонами);
    /// <c>null</c> — не проверять.</param>
    /// <param name="trustedBuildProvenanceDirectory">Каталог с доверенной provenance сборки для custom-образов;
    /// <c>null</c> — не проверять.</param>
    /// <returns>Активная сессия стенда: MQTT-адрес, телеметрия, управление жизненным циклом.</returns>
    /// <exception cref="IOException">Стенд не поднялся (скрипт вернул ошибку, образ не собран/не проверен).</exception>
    Task<StandSession> StartAsync(string script, string inventoryPath, CampaignDefinition config,
        string broker, string output, string dockerExecutable = "docker", CancellationToken cancellationToken = default,
        string? expectedStandSha256 = null, string? trustedBuildProvenanceDirectory = null);
}

/// <summary>Реализация по умолчанию — делегирует <see cref="StandSession.StartAsync"/>.</summary>
public sealed class DefaultStandSessionFactory : IStandSessionFactory
{
    /// <inheritdoc cref="IStandSessionFactory.StartAsync"/>
    public Task<StandSession> StartAsync(string script, string inventoryPath, CampaignDefinition config,
        string broker, string output, string dockerExecutable = "docker", CancellationToken cancellationToken = default,
        string? expectedStandSha256 = null, string? trustedBuildProvenanceDirectory = null) =>
        StandSession.StartAsync(script, inventoryPath, config, broker, output, dockerExecutable, cancellationToken,
            expectedStandSha256, trustedBuildProvenanceDirectory);
}