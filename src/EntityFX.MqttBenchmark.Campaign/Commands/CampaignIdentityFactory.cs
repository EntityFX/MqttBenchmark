using EntityFX.MqttBenchmark.Calibration;

namespace EntityFX.MqttBenchmark.Campaign;

/// <summary>
/// Формирование неизменяемой идентичности кампании (<see cref="CampaignIdentity"/>) из
/// директории, хэшей конфига/стенда и ревизии git.
/// </summary>
public static class CampaignIdentityFactory
{
    public static CampaignIdentity Create(string directory, string configSha256, string standSha256,
        CampaignDefinition config, string repository)
    {
        var name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return new(name, configSha256, standSha256, config.DeploymentMode, config.CpuMode,
            GitRevisionReader.ReadHead(repository));
    }
}