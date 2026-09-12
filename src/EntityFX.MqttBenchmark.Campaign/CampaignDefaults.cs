namespace EntityFX.MqttBenchmark.Campaign;

/// <summary>
/// Single source of truth for the STANDARD campaign: the broker set, the
/// broker × message-size × QoS × publisher × repeat matrix, the run timing, and the
/// default per-broker network-acceptance policy.
///
/// This is data, not logic. Production runs read the concrete values from
/// <c>benchmark-campaign.v3.json</c>; this type is the well-known fallback that
/// <see cref="CampaignDefinition"/> uses for its defaults and that tests build on.
/// Changing the standard experiment is a data change here (or in the config file),
/// never a change to the <see cref="CampaignDefinition"/> model itself.
/// </summary>
public static class CampaignDefaults
{
    public const int SchemaVersion = 3;
    public const int Seed = 20260909;

    public static readonly string[] Brokers = { "Aedes", "Mosquitto", "ActiveMQ", "EMQX" };
    public static readonly int[] MessageBytes = { 16, 256 };
    public static readonly int[] Qos = { 0, 1, 2 };
    public static readonly int[] Publishers = { 1, 16, 64, 128 };
    public const int Repeats = 3;

    public const double WarmupSeconds = 5;
    public const double MeasurementSeconds = 30;
    public const double CooldownSeconds = 5;
    public const double DrainTimeoutSeconds = 30;
    public const double QuietPeriodSeconds = 2;

    public const string DeploymentMode = "singleHostSequential";
    public const string CpuMode = "singleCorePinned";

    /// <summary>Максимум попыток на один ключ матрицы (по умолчанию для <c>--max-attempts</c>).</summary>
    public const int MaxAttempts = 3;
    /// <summary>Число RTT-эхо проб в протокольном preflight.</summary>
    public const int PreflightRttProbes = 20;
    /// <summary>Сколько секунд ждать первый ответ <c>$SYS/broker/version</c>.</summary>
    public const double PreflightSysWaitSeconds = 10;
    /// <summary>Единый таймаут транспортных операций MQTT (CONNECT/SUBSCRIBE/PUBLISH/DISCONNECT), секунды.</summary>
    public const double TransportTimeoutSeconds = 10;
    /// <summary>Резерв сверх суммы фаз кампании для запроса длительности захвата телеметрии, секунды.</summary>
    public const double TelemetryCaptureReserveSeconds = 20;
    /// <summary>Максимум ожидания готовности телеметрии перед запуском warmup, секунды.</summary>
    public const double TelemetryReadinessTimeoutSeconds = 60;

    /// <summary>Брокеры, которые собираются из исходников контроллером (custom-образ) и требуют pull/validate до старта.</summary>
    public static readonly string[] CustomImageBrokers = { "Aedes", "ActiveMQ" };

    /// <summary>
    /// Brokers whose network utilization is measured but NOT enforced (informational)
    /// in the standard campaign; every other broker is gated at
    /// <see cref="LoadGeneratorAssessment.NetworkThresholdPercent"/>.
    /// </summary>
    public static readonly string[] InformationalNetworkBrokers = { "Aedes" };

    private static bool IsInformationalNetwork(string broker) =>
        InformationalNetworkBrokers.Any(name => string.Equals(name, broker, StringComparison.OrdinalIgnoreCase));

    /// <summary>Builds the default per-broker network gates for the given broker set (null = informational).</summary>
    public static Dictionary<string, double?> CreateNetworkThresholds(IReadOnlyList<string> brokers) =>
        brokers.ToDictionary(
            broker => broker,
            broker => IsInformationalNetwork(broker) ? (double?)null : LoadGeneratorAssessment.NetworkThresholdPercent,
            StringComparer.Ordinal);

    /// <summary>A fresh, fully-populated standard <see cref="CampaignDefinition"/> (defaults from this provider).</summary>
    public static CampaignDefinition Standard() => new()
    {
        SchemaVersion = SchemaVersion,
        Seed = Seed,
        Brokers = (string[])Brokers.Clone(),
        MessageBytes = (int[])MessageBytes.Clone(),
        Qos = (int[])Qos.Clone(),
        Publishers = (int[])Publishers.Clone(),
        Repeats = Repeats,
        WarmupSeconds = WarmupSeconds,
        MeasurementSeconds = MeasurementSeconds,
        CooldownSeconds = CooldownSeconds,
        DrainTimeoutSeconds = DrainTimeoutSeconds,
        QuietPeriodSeconds = QuietPeriodSeconds,
        DeploymentMode = DeploymentMode,
        CpuMode = CpuMode,
        MaxAttempts = MaxAttempts,
        PreflightRttProbes = PreflightRttProbes,
        PreflightSysWaitSeconds = PreflightSysWaitSeconds,
        TransportTimeoutSeconds = TransportTimeoutSeconds,
        TelemetryCaptureReserveSeconds = TelemetryCaptureReserveSeconds,
        TelemetryReadinessTimeoutSeconds = TelemetryReadinessTimeoutSeconds,
        CustomImageBrokers = (string[])CustomImageBrokers.Clone(),
        NetworkThresholdPercentByBroker = CreateNetworkThresholds(Brokers),
    };
}