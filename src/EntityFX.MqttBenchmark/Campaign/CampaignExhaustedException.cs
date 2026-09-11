namespace EntityFX.MqttBenchmark.Campaign;

public sealed class CampaignExhaustedException : IOException
{
    public CampaignExhaustedException(string key) : base($"Campaign stopped: {key} exhausted three attempts.") { }
}
