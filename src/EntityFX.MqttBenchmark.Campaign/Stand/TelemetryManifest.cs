namespace EntityFX.MqttBenchmark.Campaign;

/// <summary>Манифест телеметрии стенда: число сэмплов удалённого самплера и охват ресурсов.</summary>
public sealed record TelemetryManifest(int SampleCount, string CpuScope, string NetworkScope, IReadOnlyDictionary<string, string> InputSha256);
