using System.Security.Cryptography;
using System.Text;

namespace EntityFX.MqttBenchmark.Campaign;

/// <summary>
/// The shape of a benchmark campaign (schema) and its structural invariants.
/// Carries no specific experiment of its own: the standard definition (broker set,
/// matrix, timing, network gates) lives in <see cref="CampaignDefaults"/> and is normally
/// overridden by <c>benchmark-campaign.v3.json</c>.
///
/// Contract: a definition is inert until <see cref="Validate"/> succeeds; every consumer
/// (<see cref="Expand"/>, <see cref="NetworkThresholdPercentFor"/>, commands, aggregator)
/// validates before use. The identity of a run is bound to the SHA-256 hash of the raw
/// config bytes (see <see cref="CampaignIdentityFactory"/>), so the schema must stay
/// byte-stable between runs that are expected to resume.
/// </summary>
public sealed class CampaignDefinition
{
    public int SchemaVersion { get; set; } = CampaignDefaults.SchemaVersion;
    public int Seed { get; set; } = CampaignDefaults.Seed;
    public string[] Brokers { get; set; } = (string[])CampaignDefaults.Brokers.Clone();
    public int[] MessageBytes { get; set; } = (int[])CampaignDefaults.MessageBytes.Clone();
    public int[] Qos { get; set; } = (int[])CampaignDefaults.Qos.Clone();
    public int[] Publishers { get; set; } = (int[])CampaignDefaults.Publishers.Clone();
    public int Repeats { get; set; } = CampaignDefaults.Repeats;
    public double WarmupSeconds { get; set; } = CampaignDefaults.WarmupSeconds;
    public double MeasurementSeconds { get; set; } = CampaignDefaults.MeasurementSeconds;
    public double CooldownSeconds { get; set; } = CampaignDefaults.CooldownSeconds;
    public double DrainTimeoutSeconds { get; set; } = CampaignDefaults.DrainTimeoutSeconds;
    public double QuietPeriodSeconds { get; set; } = CampaignDefaults.QuietPeriodSeconds;
    public string DeploymentMode { get; set; } = CampaignDefaults.DeploymentMode;
    public string CpuMode { get; set; } = CampaignDefaults.CpuMode;

    /// <summary>Максимум попыток на один ключ матрицы (используется журналом кампании).</summary>
    public int MaxAttempts { get; set; } = CampaignDefaults.MaxAttempts;
    /// <summary>Число RTT-эхо проб в протокольном preflight.</summary>
    public int PreflightRttProbes { get; set; } = CampaignDefaults.PreflightRttProbes;
    /// <summary>Сколько секунд ждать первый ответ <c>$SYS/broker/version</c> в preflight.</summary>
    public double PreflightSysWaitSeconds { get; set; } = CampaignDefaults.PreflightSysWaitSeconds;
    /// <summary>Единый таймаут транспортных операций MQTT (CONNECT/SUBSCRIBE/PUBLISH/DISCONNECT), секунды.</summary>
    public double TransportTimeoutSeconds { get; set; } = CampaignDefaults.TransportTimeoutSeconds;
    /// <summary>Резерв сверх суммы фаз кампании для запроса длительности захвата телеметрии, секунды.</summary>
    public double TelemetryCaptureReserveSeconds { get; set; } = CampaignDefaults.TelemetryCaptureReserveSeconds;
    /// <summary>Максимум ожидания готовности телеметрии перед запуском warmup, секунды.</summary>
    public double TelemetryReadinessTimeoutSeconds { get; set; } = CampaignDefaults.TelemetryReadinessTimeoutSeconds;
    /// <summary>Брокеры, которые собираются из исходников контроллером (custom-образ) и требуют pull/validate до старта.</summary>
    public string[] CustomImageBrokers { get; set; } = (string[])CampaignDefaults.CustomImageBrokers.Clone();

    public Dictionary<string, double?> NetworkThresholdPercentByBroker { get; set; } = CampaignDefaults.CreateNetworkThresholds(CampaignDefaults.Brokers);
    public double? CpuThresholdPercent { get; set; }
    /// <summary>Максимально допустимый интервал между сэмплами load-generator guard'а, секунды; null сохраняет дефолт 1.25 с.</summary>
    public double? MaximumGuardIntervalSeconds { get; set; }

    /// <summary>Возвращает сетевой порог (в процентах) для брокера; <c>null</c> означает
    /// информационный режим (без принудительного ограничения). Требует валидной конфигурации.</summary>
    /// <exception cref="InvalidDataException">Конфигурация не прошла <see cref="Validate"/>.</exception>
    /// <exception cref="KeyNotFoundException">Брокер отсутствует в <see cref="NetworkThresholdPercentByBroker"/>.</exception>
    public double? NetworkThresholdPercentFor(string broker)
    {
        Validate();
        return NetworkThresholdPercentByBroker[broker];
    }
    /// <summary>
    /// Разворачивает матрицу кампании в упорядоченный список ключей:
    /// broker × message size × QoS × publishers × repeat. Порядок детерминирован SHA-256
    /// хешем <c>"seed|ключ"</c> (стабилен между версиями рантайма); брокеры группируются,
    /// чтобы стенд поднимался последовательно по одному.
    /// </summary>
    /// <returns>Массив ключей матрицы; длина — ожидаемое покрытие для агрегации.</returns>
    /// <exception cref="InvalidDataException">Конфигурация не прошла <see cref="Validate"/>.</exception>
    public CampaignKey[] Expand()
    {
        Validate();
        // SHA-256 ordering is stable across runtime versions. Group brokers to keep the stand sequential.
        return (from broker in Brokers.OrderBy(Order, StringComparer.Ordinal)
                from key in (from bytes in MessageBytes from qos in Qos from publishers in Publishers
                             from repeat in Enumerable.Range(1, Repeats)
                             select new CampaignKey(broker, bytes, qos, publishers, repeat))
                            .OrderBy(x => Order(x.Key), StringComparer.Ordinal)
                select key).ToArray();
    }

    /// <summary>
    /// Проверяет структурные инварианты кампании: схема v3, непустые и уникальные списки
    /// брокеров/размеров/QoS/издателей, положительные длительности и таймауты, лимиты попыток
    /// и RTT-проб, ссылки <see cref="CustomImageBrokers"/> на сконфигурированных брокеров,
    /// корректные пороги сети/CPU и поддерживаемые режимы развёртывания.
    /// </summary>
    /// <exception cref="InvalidDataException">Любой инвариант нарушен; сообщение описывает нарушение.</exception>
    public void Validate()
    {
        if (SchemaVersion != 3)
            throw new InvalidDataException("Campaign v3 is required (schemaVersion must be 3).");
        if (Brokers is not { Length: > 0 } || Brokers.Any(string.IsNullOrWhiteSpace) ||
            Brokers.Distinct(StringComparer.Ordinal).Count() != Brokers.Length)
            throw new InvalidDataException("Brokers must be a non-empty, unique, non-blank list.");
        if (MessageBytes is not { Length: > 0 } || MessageBytes.Any(x => x <= 0))
            throw new InvalidDataException("MessageBytes must be a non-empty list of positive sizes.");
        if (Qos is not { Length: > 0 } || Qos.Any(x => x is not (0 or 1 or 2)))
            throw new InvalidDataException("Qos must be a non-empty list of values in {0, 1, 2}.");
        if (Publishers is not { Length: > 0 } || Publishers.Any(x => x <= 0) ||
            Publishers.Distinct().Count() != Publishers.Length)
            throw new InvalidDataException("Publishers must be a non-empty, unique list of positive client counts.");
        if (Repeats < 1)
            throw new InvalidDataException("Repeats must be at least 1.");
        if (MaxAttempts < 1)
            throw new InvalidDataException("MaxAttempts must be at least 1.");
        if (PreflightRttProbes < 1)
            throw new InvalidDataException("PreflightRttProbes must be at least 1.");
        if (new[] { WarmupSeconds, MeasurementSeconds, CooldownSeconds, DrainTimeoutSeconds, QuietPeriodSeconds,
            PreflightSysWaitSeconds, TransportTimeoutSeconds, TelemetryCaptureReserveSeconds, TelemetryReadinessTimeoutSeconds }
            .Any(x => !double.IsFinite(x) || x <= 0))
            throw new InvalidDataException("Campaign durations and timeouts must be finite and positive.");
        if (MeasurementSeconds <= 0 || QuietPeriodSeconds <= 0 || DrainTimeoutSeconds < QuietPeriodSeconds)
            throw new InvalidDataException("Campaign durations must be finite and the drain timeout must cover the quiet period.");
        if (CustomImageBrokers == null || CustomImageBrokers.Any(string.IsNullOrWhiteSpace) ||
            CustomImageBrokers.Any(broker => !Brokers.Contains(broker, StringComparer.Ordinal)))
            throw new InvalidDataException("CustomImageBrokers must reference only configured campaign brokers.");
        if (DeploymentMode is not ("singleHostSequential" or "distributedHosts") ||
            CpuMode is not ("singleCorePinned" or "hostAllCores"))
            throw new InvalidDataException("Unsupported campaign deployment/CPU mode.");
        if (NetworkThresholdPercentByBroker == null || NetworkThresholdPercentByBroker.Count != Brokers.Length ||
            Brokers.Any(broker => !NetworkThresholdPercentByBroker.TryGetValue(broker, out var threshold) ||
                threshold != null && (!double.IsFinite(threshold.Value) || threshold <= 0 || threshold > 100)) ||
            NetworkThresholdPercentByBroker.Keys.Any(broker => !Brokers.Contains(broker, StringComparer.Ordinal)))
            throw new InvalidDataException("Every broker must declare either a null (informational) network gate or a finite gate in (0, 100].");
        if (CpuThresholdPercent is { } cpuThreshold && (!double.IsFinite(cpuThreshold) || cpuThreshold <= 0 || cpuThreshold > 100))
            throw new InvalidDataException("CpuThresholdPercent must be in (0, 100] when set; null keeps the 70% default.");
        if (MaximumGuardIntervalSeconds is { } maxInterval && (!double.IsFinite(maxInterval) || maxInterval <= 0))
            throw new InvalidDataException("MaximumGuardIntervalSeconds must be finite and positive when set; null keeps the 1.25 s default.");
    }

    private string Order(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Seed}|{key}")));
}
