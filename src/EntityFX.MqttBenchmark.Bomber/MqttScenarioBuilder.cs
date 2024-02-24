using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MQTTnet;
using NBomber;
using NBomber.CSharp;
using MQTTnet.Client;
using NBomber.Contracts;
using EntityFX.MqttBenchmark.Bomber.Settings;

namespace EntityFX.MqttBenchmark.Bomber;

class MqttScenarioBuilder
{
    //private ClientPool<(IMqttClient mqttClient, MqttScenarioSettings MqttScenarioSettings)> _clientPool;
    private ConcurrentDictionary<int, (IMqttClient mqttClient, MqttScenarioSettings MqttScenarioSettings)> _clients;
    
    private readonly ILogger _logger;
    private readonly IConfiguration configuration;
    private readonly Dictionary<string, long> scenarioCounters;
    private readonly MqttCounterClient mqttCounterClient;

    public MqttScenarioBuilder(
        ILogger logger,
        IConfiguration configuration,
        Dictionary<string, long> scenarioCounters,
        MqttCounterClient mqttCounterClient)
    {
        _logger = logger;
        this.configuration = configuration;
        this.scenarioCounters = scenarioCounters;
        this.mqttCounterClient = mqttCounterClient;
    }

    public ScenarioProps Build(string name)
    {
        var scenario = Scenario.Create(name, async context =>
        {
            if (!_clients.Any())
            {
                return Response.Fail("No connection", "No connection", 0);
            }

            //var poolItem = _clientPool.GetClient(context.ScenarioInfo);

            var index = context.ScenarioInfo.ThreadNumber % _clients.Count;
            var poolItem = _clients[index];

            var message = StringHelper.GetString(poolItem.MqttScenarioSettings.MessageSize);

            var applicationMessage = new MqttApplicationMessageBuilder()
                .WithTopic(poolItem.MqttScenarioSettings.Topic)
                .WithQualityOfServiceLevel(poolItem.MqttScenarioSettings.Qos)
                .WithPayload(message)
                .Build();

            var sizeBytes = poolItem.MqttScenarioSettings.MessageSize;

            var sendStep = await Step.Run("publish", context, async () =>
            {

                var result = await poolItem.mqttClient.PublishAsync(applicationMessage, CancellationToken.None);
                return result.IsSuccess ? Response.Ok(sizeBytes: sizeBytes) :
                    Response.Fail(result.ReasonCode.ToString(), result.ReasonString, sizeBytes);
            });

            return sendStep;
        })
        .WithoutWarmUp()
        .WithInit(Init)
        .WithClean(Clean);

        return scenario;
    }

    private async Task Clean(IScenarioInitContext context)
    {
        var settings = context.CustomSettings.Get<MqttScenarioSettings>();
        if (settings == null) return;

        foreach (var client in _clients)
        {
            await client.Value.mqttClient.DisconnectAsync();
        }
        

        var broker = new Uri($"mqtt://{settings.Server}:{settings.Port}/");

        var counter = await mqttCounterClient.GetCounterAndValidate(broker.ToString(), settings.Topic);
        scenarioCounters[context.ScenarioInfo.ScenarioName] = counter;

        context.Logger.Information("{0}: Recieved conter={1}", context.ScenarioInfo.ScenarioName, counter);

        await Task.Delay(5000);

        await mqttCounterClient.ClearCounter(broker.ToString(), settings.Topic);
        context.Logger.Information("{0}: Clear conter", context.ScenarioInfo.ScenarioName);
    }

    private async Task Init(IScenarioInitContext context)
    {
        await Task.Delay(5000);
        var settings = context.CustomSettings.Get<MqttScenarioSettings>()
            ?? new MqttScenarioSettings("test", MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce,
            "localhost", 1883, 50, 1024
            );

        var broker = new Uri($"mqtt://{settings.Server}:{settings.Port}/");
        await mqttCounterClient.ClearCounter(broker.ToString(), settings.Topic);
        context.Logger.Information("{0}: Clear counter", context.ScenarioInfo.ScenarioName);
        //_clientPool = new ClientPool<(IMqttClient mqttClient, MqttScenarioSettings MqttScenarioSettings)>();
        
        //await ScenarioHelper.BuildMqttClientPool(_clientPool, settings);
        _clients = await ScenarioHelper.BuildClients(context.ScenarioInfo.ScenarioName, settings);
    }
}
