namespace EntityFX.MqttBenchmark.Campaign;

public sealed record CampaignIdentity(string CampaignId, string ConfigSha256, string StandSha256,
    string DeploymentMode, string CpuMode, string MqttBenchmarkCommitSha);
