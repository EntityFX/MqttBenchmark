class MqttSettings
{
    public Uri Broker { get; set; } = new Uri("localhost:1883");

    public MqttTopicSettings[] Topics { get; set; } = Array.Empty<MqttTopicSettings>();
}
