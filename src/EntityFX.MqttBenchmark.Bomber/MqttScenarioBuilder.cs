using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MQTTnet;
using NBomber;
using NBomber.CSharp;
using MQTTnet.Client;
using NBomber.Contracts;
using EntityFX.MqttBenchmark.Bomber.Settings;
using System.Xml.Linq;

namespace EntityFX.MqttBenchmark.Bomber;

class MqttScenarioBuilder
{
    //private ClientPool<(IMqttClient mqttClient, MqttScenarioSettings MqttScenarioSettings)> _clientPool;
    private Dictionary<int, 
        (IMqttClient mqttClient, MqttScenarioSettings MqttScenarioSettings, MqttApplicationMessage MqttApplicationMessage)> 
        _clients = new Dictionary<int, (IMqttClient mqttClient, MqttScenarioSettings MqttScenarioSettings, MqttApplicationMessage MqttApplicationMessage)>();
    
    private readonly ILogger _logger;
    private readonly IConfiguration configuration;
    private readonly Dictionary<string, long> scenarioCounters;
    private readonly Dictionary<string, ScenarioTimeStats> scenarioStats;
    private readonly MqttCounterClient mqttCounterClient;

    private long scenarioCounter = 0;

    public MqttScenarioBuilder(
        ILogger logger,
        IConfiguration configuration,
        Dictionary<string, long> scenarioCounters,
        Dictionary<string, ScenarioTimeStats> scenarioStats,
        MqttCounterClient mqttCounterClient)
    {
        _logger = logger;
        this.configuration = configuration;
        this.scenarioCounters = scenarioCounters;
        this.scenarioStats = scenarioStats;
        this.mqttCounterClient = mqttCounterClient;
    }

    public ScenarioProps Build(string name)
    {
        scenarioStats[name] = new ScenarioTimeStats()
        {
            BuildTime = DateTimeOffset.UtcNow
        };

        var scenario = Scenario.Create(name, async context =>
        {
            if (!_clients.Any())
            {
                return Response.Fail("No connection", "No connection", 0);
            }

            //var poolItem = _clientPool.GetClient(context.ScenarioInfo);

            //var index = context.ScenarioInfo.ThreadNumber % _clients.Count;
            var index = (int)(scenarioCounter % _clients.Count);      
            var poolItem = _clients[index];
            //context.Logger.Information("{0} - {1}", index, context.ScenarioInfo.ThreadNumber % _clients.Count);
            Interlocked.Increment(ref scenarioCounter);

            var sizeBytes = poolItem.MqttScenarioSettings.MessageSize;
            var applicationMessage = poolItem.MqttApplicationMessage;

            if (null == applicationMessage)
            {
                return Response.Fail();
            }
            
            if (scenarioCounter == 1 && applicationMessage!.PayloadSegment.Count <= 1024 ) {
                context.Logger.Information($"{name}: Message = {applicationMessage.ConvertPayloadToString()}");
            }

            var sendStep = await Step.Run("publish", context, async () =>
            {

                var result = await poolItem.mqttClient.PublishAsync(applicationMessage);
                return result.ReasonCode == MqttClientPublishReasonCode.Success ? Response.Ok(sizeBytes: sizeBytes) :
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
        scenarioStats[context.ScenarioInfo.ScenarioName].CleanStartTime = DateTimeOffset.UtcNow;
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

        await Task.Delay(10000);

        await mqttCounterClient.ClearCounter(broker.ToString(), settings.Topic);
        context.Logger.Information("{0}: Clear conter", context.ScenarioInfo.ScenarioName);

        context.Logger.Information("{0}: Scenario counter = {1}", context.ScenarioInfo.ScenarioName, scenarioCounter);

        await Task.Delay(10000);
        scenarioStats[context.ScenarioInfo.ScenarioName].CleanEndTime = DateTimeOffset.UtcNow;
    }

    private async Task Init(IScenarioInitContext context)
    {
        scenarioStats[context.ScenarioInfo.ScenarioName].InitStartTime = DateTimeOffset.UtcNow;
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
        _clients = await ScenarioHelper.BuildClients(context.Logger, context.ScenarioInfo.ScenarioName, settings);
        scenarioStats[context.ScenarioInfo.ScenarioName].InitEndTime = DateTimeOffset.UtcNow;
    }
}
