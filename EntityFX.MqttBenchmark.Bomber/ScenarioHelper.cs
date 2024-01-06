using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MQTTnet;
using MQTTnet.Client;
using NBomber;

namespace EntityFX.IotSimulator.Stress;

static class ScenarioHelper
{
    public static IConfiguration InitConfiguration(IHost host, string[] args)
    {
        var rootConfig = host.Services.GetRequiredService<IConfiguration>();
        var profile = rootConfig.GetSection("profile");


        var configBuilder = new ConfigurationBuilder()
            .AddJsonFile(profile.Value);


        var config = configBuilder.Build();

        return config;
    }

    internal static async Task BuildMqttClientPool(
        ClientPool<(IMqttClient mqttClient, MqttScenarioSettings MqttScenarioSettings)> clientPool, 
        MqttScenarioSettings settings)
    {
        var mqttFactory = new MqttFactory();

        for (int i = 0; i < settings.ClientsCount; i++)
        {
            var mqttClient = mqttFactory.CreateMqttClient();

            var mqttClientOptions = new MqttClientOptionsBuilder()
                .WithTcpServer(settings.Server, settings.Port)
                .Build();

            await mqttClient.ConnectAsync(mqttClientOptions, CancellationToken.None);
            
            clientPool.AddClient((mqttClient, settings));
        }
    }
}