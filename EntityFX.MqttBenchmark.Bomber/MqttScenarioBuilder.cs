using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MQTTnet;
using NBomber;
using NBomber.CSharp;
using MQTTnet.Client;
using NBomber.Contracts;
using EntityFX.IotSimulator.Stress;

class MqttScenarioBuilder
{
    private readonly ClientPool<(IMqttClient mqttClient, MqttScenarioSettings MqttScenarioSettings)> _clientPool;
    private readonly ILogger<MqttScenarioBuilder> _logger;

    public MqttScenarioBuilder(
        ILogger<MqttScenarioBuilder> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _clientPool = new ClientPool<(IMqttClient mqttClient, MqttScenarioSettings MqttScenarioSettings)>();
    }

    public ScenarioProps Build(string name)
    {
        var message = StringHelper.GetString(256);

        var scenario = Scenario.Create(name, async context =>
        {
            var poolItem = _clientPool.GetClient(context.ScenarioInfo);
            
            var applicationMessage = new MqttApplicationMessageBuilder()
                .WithTopic(poolItem.MqttScenarioSettings.Topic)
                .WithQualityOfServiceLevel(poolItem.MqttScenarioSettings.Qos)
                .WithPayload(message)
                .Build();
            var length = applicationMessage!.Payload.Length;


            var sendStep = await Step.Run("publish", context, async () =>
            {

                await poolItem.mqttClient.PublishAsync(applicationMessage, CancellationToken.None);
                return Response.Ok(sizeBytes: length);
            });

            return sendStep;
        })
        .WithoutWarmUp()
        .WithInit(Init);

        return scenario;
    }
    
    private Task Init(IScenarioInitContext arg)
    {
        var settings = arg.CustomSettings.Get<MqttScenarioSettings>()
            ?? new MqttScenarioSettings("test", MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce, 
            "localhost", 1883, 50
            );

        return ScenarioHelper.BuildMqttClientPool(_clientPool, settings);
    }
}
