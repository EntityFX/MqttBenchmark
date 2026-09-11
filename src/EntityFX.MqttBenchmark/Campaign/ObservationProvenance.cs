namespace EntityFX.MqttBenchmark.Campaign;

public sealed record ObservationProvenance(CampaignIdentity Identity, IReadOnlyDictionary<string, string> InputSha256);
