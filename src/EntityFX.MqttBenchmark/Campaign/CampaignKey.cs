namespace EntityFX.MqttBenchmark.Campaign;

public sealed record CampaignKey(string Broker, int MessageBytes, int Qos, int Publishers, int Repeat)
{
    public string Key => $"{Broker}.m{MessageBytes}.q{Qos}.p{Publishers}.r{Repeat}";
}
