using MQTTnet.Protocol;

public class MqttScenarioSettings
{
    public string Topic { get; set; }

    public MqttScenarioSettings(string topic, MqttQualityOfServiceLevel qos, string server, int port, int clientsCount)
    {
        Server = server;
        Port = port;
        ClientsCount = clientsCount;
    }

    public MqttQualityOfServiceLevel Qos { get; set; }

    public  string Server { get; set; }

    public  int Port { get; set; }

    public int ClientsCount { get; set; }
}