namespace EntityFX.MqttBenchmark.Campaign;

/// <summary>
/// Фабрика load-generator guard'а. Потребители зависят от этой абстракции, а не от конкретного
/// <see cref="LoadGeneratorGuard"/> и его реализации счётчиков; по умолчанию создаётся
/// реальный guard с Windows-счётчиками.
/// </summary>
public interface ILoadGeneratorGuardFactory
{
    /// <summary>
    /// Создаёт и запускает load-guard: фоновый сэмплер ресурсов генератора нагрузки
    /// (сеть/CPU), который оценивает окно измерения и завершается отчётом о помехах.
    /// </summary>
    /// <param name="endpoint">Адрес брокера — используется для выбора сетевого интерфейса.</param>
    /// <param name="directory">Каталог для артефактов отчёта (<c>load-generator.json</c>).</param>
    /// <param name="countersFactory">Фабрика счётчиков ресурсов (обычно
    /// <see cref="WindowsLoadGeneratorCounters"/>); выбирается по <paramref name="endpoint"/>.</param>
    /// <param name="clock">Часы для детерминированных тестов; <c>null</c> — реальные часы.</param>
    /// <param name="cancellationToken">Токен отмены; прерывает сэмплирование.</param>
    /// <param name="networkThresholdPercent">Порог утилизации сети в процентах; <c>null</c> — информационный режим.</param>
    /// <param name="cpuThresholdPercent">Порог утилизации CPU в процентах.</param>
    /// <param name="maximumIntervalSeconds">Максимально допустимый интервал между сэмплами счётчиков, секунды.</param>
    /// <returns>Активный guard (реализует <see cref="IMeasurementWindowObserver"/>); требует
    /// <see cref="LoadGeneratorGuard.CompleteAsync"/> и освобождения через <c>DisposeAsync</c>.</returns>
    LoadGeneratorGuard Create(Uri endpoint, string directory, Func<Uri, ILoadGeneratorCounters> countersFactory,
        ILoadGeneratorClock? clock = null, CancellationToken cancellationToken = default,
        double? networkThresholdPercent = LoadGeneratorAssessment.NetworkThresholdPercent,
        double cpuThresholdPercent = LoadGeneratorAssessment.CpuThresholdPercent,
        double maximumIntervalSeconds = LoadGeneratorAssessment.MaximumIntervalSeconds);
}

/// <summary>Реализация по умолчанию — делегирует <see cref="LoadGeneratorGuard.Start(Uri,string,Func{Uri,ILoadGeneratorCounters},ILoadGeneratorClock,CancellationToken,double?,double)"/>.</summary>
public sealed class DefaultLoadGeneratorGuardFactory : ILoadGeneratorGuardFactory
{
    /// <inheritdoc cref="ILoadGeneratorGuardFactory.Create"/>
    public LoadGeneratorGuard Create(Uri endpoint, string directory, Func<Uri, ILoadGeneratorCounters> countersFactory,
        ILoadGeneratorClock? clock = null, CancellationToken cancellationToken = default,
        double? networkThresholdPercent = LoadGeneratorAssessment.NetworkThresholdPercent,
        double cpuThresholdPercent = LoadGeneratorAssessment.CpuThresholdPercent,
        double maximumIntervalSeconds = LoadGeneratorAssessment.MaximumIntervalSeconds) =>
        LoadGeneratorGuard.Start(endpoint, directory, countersFactory, clock, cancellationToken,
            networkThresholdPercent, cpuThresholdPercent, maximumIntervalSeconds);
}