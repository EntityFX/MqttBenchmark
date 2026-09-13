using System.Diagnostics;
using System.Runtime.InteropServices;

namespace EntityFX.MqttBenchmark.Campaign;

/// <summary>Сводный preflight-отчёт кампании: успешен, только если все брокеры прошли протокол
/// и сняли телеметрию.</summary>
public sealed record PreflightReport(int SchemaVersion, CampaignIdentity Identity, bool Success, BenchmarkEnvironment Environment,
    IReadOnlyList<BrokerPreflight> Brokers)
{
    /// <summary>Собирает отчёт по списку брокеров; <c>Success</c> — все протоколы успешны
    /// и все манифесты содержат хотя бы один сэмпл.</summary>
    public static PreflightReport Create(CampaignIdentity identity, IReadOnlyList<BrokerPreflight> brokers) =>
        new(CampaignDefaults.SchemaVersion, identity, brokers.Count > 0 && brokers.All(x => x.Protocol.Success && x.Telemetry.SampleCount > 0),
            new(RuntimeInformation.OSDescription, System.Environment.ProcessorCount, RuntimeInformation.FrameworkDescription,
                RuntimeInformation.ProcessArchitecture.ToString(), System.Environment.MachineName, Stopwatch.Frequency, DateTimeOffset.UtcNow), brokers);
}
