namespace EntityFX.MqttBenchmark.Campaign;

public sealed record ProtocolPreflight(string Broker, bool Success, IReadOnlyList<ProtocolProbe> Probes,
    IReadOnlyList<double> RttSamplesMs, double? RttBaselineMs, string? SysBrokerVersion, string SysVersionStatus,
    string? RttError = null, string? SysVersionError = null);
