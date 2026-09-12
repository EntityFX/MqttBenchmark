namespace EntityFX.MqttBenchmark.Campaign;

/// <summary>
/// Фасад кампании-раннера: сохраняет прежний публичный API и сигнатуры, но делегирует
/// работу узкоспециализированным классам — транспорту (<see cref="MqttTransportClient"/>),
/// preflight (<see cref="MqttPreflightProbe"/>) и нагрузочному циклу
/// (<see cref="CampaignWorkloadRunner"/>). Расширение выполняется через
/// <see cref="IMqttCampaignRunner"/>.
/// </summary>
public sealed class MqttCampaignRunner : IMqttCampaignRunner
{
    private readonly MqttTransportClient transport;
    private readonly MqttPreflightProbe preflight;
    private readonly CampaignWorkloadRunner workload;

    /// <summary>
    /// Создаёт фасад с настроенными таймаутами.
    /// </summary>
    /// <param name="timeout">Единый таймаут транспортных операций MQTT; <c>null</c> — значение по умолчанию
    /// (<see cref="CampaignDefaults.TransportTimeoutSeconds"/>).</param>
    /// <param name="sysWait">Окно ожидания первого ответа <c>$SYS/broker/version</c>; <c>null</c> —
    /// значение по умолчанию (<see cref="CampaignDefaults.PreflightSysWaitSeconds"/>).</param>
    public MqttCampaignRunner(TimeSpan? timeout = null, TimeSpan? sysWait = null)
    {
        transport = new MqttTransportClient(timeout: timeout);
        preflight = new MqttPreflightProbe(transport, timeout, sysWait);
        workload = new CampaignWorkloadRunner(transport);
    }

    /// <inheritdoc cref="IMqttCampaignRunner.PreflightAsync(string,Uri,int,CancellationToken)"/>
    public Task<ProtocolPreflight> PreflightAsync(string broker, Uri endpoint, int rttProbes,
        CancellationToken cancellationToken = default) =>
        preflight.ProbeAsync(broker, endpoint, rttProbes, cancellationToken);

    /// <summary>Обратно совместимая перегрузка без числа RTT-проб — использует
    /// <see cref="CampaignDefaults.PreflightRttProbes"/>.</summary>
    public Task<ProtocolPreflight> PreflightAsync(string broker, Uri endpoint, CancellationToken cancellationToken = default) =>
        PreflightAsync(broker, endpoint, CampaignDefaults.PreflightRttProbes, cancellationToken);

    /// <inheritdoc cref="IMqttCampaignRunner.RunAsync"/>
    public Task<RunObservation> RunAsync(CampaignDefinition config, CampaignKey key, Uri endpoint, string campaignId,
        string runId, string directory, double rttBaselineMs, CancellationToken cancellationToken = default,
        IMeasurementWindowObserver? measurementObserver = null) =>
        workload.RunAsync(config, key, endpoint, campaignId, runId, directory, rttBaselineMs,
            cancellationToken, measurementObserver);
}