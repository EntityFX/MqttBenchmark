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
            var mqttClient = mqttFactory.CreateMqttClient();

            var mqttClientOptions = new MqttClientOptionsBuilder()
                .WithTcpServer(settings.Server, settings.Port)
                .Build();
            try
            {

                await mqttClient.ConnectAsync(mqttClientOptions, CancellationToken.None);

                clientPool.AddClient((mqttClient, settings));
            }
            catch (Exception)
            {
                Console.WriteLine($"Error connect {settings.Server}:{settings.Port}");
            }

        }
    }
}