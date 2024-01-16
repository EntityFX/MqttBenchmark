using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MQTTnet;
using NBomber;
using NBomber.CSharp;
using MQTTnet.Client;
using NBomber.Contracts;

namespace EntityFX.MqttBenchmark.Bomber;

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
        var scenario = Scenario.Create(name, async context =>
        {
            var poolItem = _clientPool.GetClient(context.ScenarioInfo);

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
        .WithInit(Init);

        return scenario;
    }

    private Task Init(IScenarioInitContext arg)
    {
        var settings = arg.CustomSettings.Get<MqttScenarioSettings>()
            ?? new MqttScenarioSettings("test", MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce,
            "localhost", 1883, 50, 1024
            );

        return ScenarioHelper.BuildMqttClientPool(_clientPool, settings);
    }
}
