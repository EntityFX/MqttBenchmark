using MQTTnet.Protocol;

namespace EntityFX.MqttBenchmark.Bomber;

public class MqttScenarioSettings
{
    public MqttScenarioSettings(
        string topic, MqttQualityOfServiceLevel qos, string server, int port, 
        int clientsCount, int messageSize)
    {
        Topic = topic;
        Server = server;
        Port = port;
        ClientsCount = clientsCount;
        MessageSize = messageSize;
    }

    public MqttQualityOfServiceLevel Qos { get; set; }

    public int MessageSize { get; set; }

    public string Topic { get; set; }

    public  string Server { get; set; }

    public  int Port { get; set; }

    public int ClientsCount { get; set; }
}
