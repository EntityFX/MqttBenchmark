namespace EntityFX.MqttBenchmark.Campaign;

/// <summary>
/// Провайдер аппаратных метаданных сетевого адаптера для load-generator guard'а.
/// Реализации: Windows (MSFT_NetAdapter) и Unix (sysfs). Абстракция кроссплатформенная —
/// имя осознанно без привязки к ОС.
/// </summary>
public interface IAdapterMetadataProvider
{
    /// <summary>Возвращает аппаратное свидетельство (hardware/virtual, source) для интерфейса с данным индексом.</summary>
    AdapterHardwareEvidence Read(int interfaceIndex);
}
