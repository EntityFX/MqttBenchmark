namespace EntityFX.MqttBenchmark.Campaign;

/// <summary>
/// Сборка <see cref="PreflightReport"/> из идентичности кампании и списка
/// per-broker preflight/telemetry результатов.
/// </summary>
public static class PreflightReportBuilder
{
    public static PreflightReport Build(CampaignIdentity identity, IReadOnlyList<BrokerPreflight> brokers) =>
        PreflightReport.Create(identity, brokers);
}