using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MQTTnet;
using MQTTnet.Client;
using NBomber;

namespace EntityFX.MqttBenchmark.Bomber;

static class ScenarioHelper
{
    public static IConfiguration InitConfiguration(IHost host, string[] args)
    {
        var rootConfig = host.Services.GetRequiredService<IConfiguration>();
        var profile = rootConfig.GetSection("profile");

        return rootConfig;
    }

    internal static async Task BuildMqttClientPool(
        ClientPool<(IMqttClient mqttClient, MqttScenarioSettings MqttScenarioSettings)> clientPool,
        MqttScenarioSettings settings)
    {
        var mqttFactory = new MqttFactory();

        for (int i = 0; i < settings.ClientsCount; i++)
        {
            var mqttClientOptions = new MqttClientOptionsBuilder()
            .WithTcpServer(settings.Server, settings.Port)
            .WithCleanSession(true)
            .WithTimeout(TimeSpan.FromSeconds(45))
            .Build();
            try
            {
                var mqttClient = mqttFactory.CreateMqttClient();
                var result = await mqttClient.ConnectAsync(mqttClientOptions, CancellationToken.None);
                if (result?.ResultCode == MqttClientConnectResultCode.Success)
                {
                    clientPool.AddClient((mqttClient, settings));
                }
            }
            catch (Exception)
            {
                Console.WriteLine($"Error connect {settings.Server}:{settings.Port}");
            }

        }
    }
    
    internal static async Task<Dictionary<int, 
        (IMqttClient mqttClient, MqttScenarioSettings MqttScenarioSettings, MqttApplicationMessage MqttApplicationMessage)>> 
        BuildClients(Serilog.ILogger logger, string name, MqttScenarioSettings settings)
    {
        var mqttFactory = new MqttFactory();

        var clients = Enumerable.Range(0, settings.ClientsCount).Select(_ =>
            mqttFactory.CreateMqttClient());

        var clientsBag = new Dictionary<int, 
            (IMqttClient mqttClient, MqttScenarioSettings MqttScenarioSettings, MqttApplicationMessage)>();
        var id = 0;
        foreach (var client in clients)
        {
            var mqttClientOptions = new MqttClientOptionsBuilder()
                .WithTcpServer(settings.Server, settings.Port)
                .WithClientId($"{name}-{id}")
                .WithCleanSession(true)
                .WithTimeout(TimeSpan.FromSeconds(30))
                .Build();

            if (await TryConnect(logger, client, mqttClientOptions))
            {
                var message = StringHelper.GetString(settings.MessageSize);

                var applicationMessage = new MqttApplicationMessageBuilder()
                    .WithTopic(settings.Topic)
                    .WithQualityOfServiceLevel(settings.Qos)
                    .WithPayload(message)
                    .Build();

                clientsBag.TryAdd(id, (client, settings, applicationMessage));
                id++;
            }
        }

        return clientsBag;
    }

    private static async Task<bool> TryConnect(Serilog.ILogger logger, IMqttClient mqttClient, MqttClientOptions mqttClientOptions)
    {
        int attempts = 5;

        while (attempts > 0)
        {
            try
            {
                var result = await mqttClient.ConnectAsync(mqttClientOptions);
                if (result.ResultCode == MqttClientConnectResultCode.Success)
                {
                    return true;
                }
            }
            catch(Exception ex) 
            {
                logger.Error(ex, nameof(TryConnect));
            }
            finally
            {
                attempts--;
            }

            Thread.Sleep(1);
        }

        return false;
    }
}