using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace EntityFX.MqttBenchmark.Campaign;

/// <summary>
/// Низкоуровневый MQTT-транспорт (CONNECT, SUBSCRIBE, PUBLISH, DISCONNECT) поверх MQTTnet
/// с едиными таймаутами и строгой валидацией результата. Бизнес-логики кампании
/// (preflight, нагрузка, учёт доставок) здесь нет.
/// </summary>
public sealed class MqttTransportClient
{
    private readonly MqttFactory factory;
    private readonly TimeSpan timeout;

    public MqttTransportClient(MqttFactory? factory = null, TimeSpan? timeout = null)
    {
        this.factory = factory ?? new MqttFactory();
        this.timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    public IMqttClient CreateClient() => factory.CreateMqttClient();

    public MqttClientSubscribeOptionsBuilder CreateSubscribeOptionsBuilder() =>
        factory.CreateSubscribeOptionsBuilder();

    public async Task ConnectAsync(IMqttClient client, Uri endpoint, CancellationToken ct)
    {
        if (endpoint.Scheme != "mqtt") throw new InvalidDataException("Stand requires plain MQTT TCP endpoints.");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct); cancel.CancelAfter(timeout);
        var result = await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer(endpoint.Host, endpoint.Port)
            .WithClientId("mb-" + Guid.NewGuid().ToString("N")).WithProtocolVersion(MqttProtocolVersion.V311)
            .WithCleanSession().WithTimeout(timeout).Build(), cancel.Token);
        if (result.ResultCode != MqttClientConnectResultCode.Success) throw new IOException("CONNECT: " + result.ResultCode);
    }

    public async Task SubscribeAsync(IMqttClient client, string topic, int qos, CancellationToken ct)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct); cancel.CancelAfter(timeout);
        var result = await client.SubscribeAsync(factory.CreateSubscribeOptionsBuilder().WithTopicFilter(topic,
            (MqttQualityOfServiceLevel)qos).Build(), cancel.Token);
        if (result.Items.Count != 1 || (int)result.Items.Single().ResultCode != qos)
            throw new IOException("SUBSCRIBE did not grant requested QoS " + qos);
    }

    public async Task PublishAsync(IMqttClient client, string topic, byte[] payload, int qos, CancellationToken ct)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct); cancel.CancelAfter(timeout);
        var result = await client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload)
            .WithQualityOfServiceLevel((MqttQualityOfServiceLevel)qos).Build(), cancel.Token);
        if (result.ReasonCode != MqttClientPublishReasonCode.Success) throw new IOException("PUBLISH: " + result.ReasonCode);
    }

    public async Task DisconnectAsync(IMqttClient client)
    {
        try { if (client.IsConnected) await client.DisconnectAsync().WaitAsync(timeout); }
        catch { /* Preserve the original protocol outcome. */ }
    }
}