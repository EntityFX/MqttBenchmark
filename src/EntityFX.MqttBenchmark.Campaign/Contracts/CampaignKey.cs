namespace EntityFX.MqttBenchmark.Campaign;

/// <summary>Ключ точки матрицы кампании: брокер, размер сообщения (байт), QoS, число издателей
/// и номер повтора. <see cref="Key"/> — каноническая строка для каталогов артефактов и логов.</summary>
public sealed record CampaignKey(string Broker, int MessageBytes, int Qos, int Publishers, int Repeat)
{
    public string Key => $"{Broker}.m{MessageBytes}.q{Qos}.p{Publishers}.r{Repeat}";
}
