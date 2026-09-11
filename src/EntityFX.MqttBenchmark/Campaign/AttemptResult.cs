namespace EntityFX.MqttBenchmark.Campaign;

public sealed record AttemptResult(CampaignIdentity Identity, CampaignKey Key, int Attempt,
    string Status, string? Failure, RunObservation? Observation);
