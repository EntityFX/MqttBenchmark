namespace EntityFX.MqttBenchmark.Campaign;

public sealed class CampaignExhaustedException : IOException
{
    public CampaignExhaustedException(string key) : this(key, 3) { }
    public CampaignExhaustedException(string key, int maxAttempts)
        : base($"Campaign stopped: {key} exhausted {maxAttempts} attempts.") { }
}
