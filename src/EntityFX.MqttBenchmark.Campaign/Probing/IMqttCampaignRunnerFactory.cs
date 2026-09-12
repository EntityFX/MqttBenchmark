namespace EntityFX.MqttBenchmark.Campaign;

/// <summary>
/// Фабрика раннера кампании. Потребители зависят от этой абстракции, а не от конкретного
/// <see cref="MqttCampaignRunner"/>; по умолчанию создаётся реальный фасад.
/// </summary>
public interface IMqttCampaignRunnerFactory
{
    /// <summary>
    /// Создаёт раннер кампании.
    /// </summary>
    /// <param name="timeout">Единый таймаут транспортных операций MQTT; <c>null</c> — по умолчанию.</param>
    /// <param name="sysWait">Окно ожидания первого ответа <c>$SYS/broker/version</c>; <c>null</c> — по умолчанию.</param>
    /// <returns>Готовый к использованию раннер (реализует <see cref="IMqttCampaignRunner"/>).</returns>
    IMqttCampaignRunner Create(TimeSpan? timeout = null, TimeSpan? sysWait = null);
}

/// <summary>Реализация по умолчанию — фасад <see cref="MqttCampaignRunner"/>.</summary>
public sealed class DefaultMqttCampaignRunnerFactory : IMqttCampaignRunnerFactory
{
    /// <inheritdoc cref="IMqttCampaignRunnerFactory.Create"/>
    public IMqttCampaignRunner Create(TimeSpan? timeout = null, TimeSpan? sysWait = null) =>
        new MqttCampaignRunner(timeout, sysWait);
}