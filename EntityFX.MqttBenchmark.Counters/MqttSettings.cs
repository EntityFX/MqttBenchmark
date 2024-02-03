class MqttSettings
{
    public Uri Broker { get; set; }

    public MqttTopicSettings[] Topics { get; set; }
}

class MqttTopicSettings
{
    public string Topic { get; set; }

    public int Qos { get; set; }
}