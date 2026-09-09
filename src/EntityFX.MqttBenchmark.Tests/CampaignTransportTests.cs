using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using EntityFX.MqttBenchmark.Campaign;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;

namespace EntityFX.MqttBenchmark.Tests;

[TestClass]
public class CampaignTransportTests
{
    [DataTestMethod]
    [DataRow(0)] [DataRow(1)] [DataRow(2)]
    public async Task RealMqtt_RunMeasuresCompletionsExactPayloadsAndUniqueDelivery(int qos)
    {
        await using var broker = await LocalBroker.StartAsync();
        using var temp = new CampaignTemp();
        var key = new CampaignKey("Mosquitto", 16, qos, 2, 1);
        var runner = new MqttCampaignRunner(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(30));
        var result = await runner.RunAsync(new CampaignDefinition { WarmupSeconds = .02, MeasurementSeconds = .12,
            CooldownSeconds = 0, QuietPeriodSeconds = .03, DrainTimeoutSeconds = .2 }, key, broker.Uri, "test", "attempt-01", temp.Path, .5);
        Assert.IsTrue(result.Measurement.ActualMeasurementSeconds >= .12);
        Assert.IsTrue(result.Measurement.CompletedPublishes > 0);
        Assert.AreEqual(result.Measurement.CompletedPublishes, result.Measurement.CompletedIdsDelivered);
        Assert.AreEqual(0L, result.Measurement.UnexpectedDeliveries);
        Assert.AreEqual(0.0, result.Measurement.DeliveryLossRate);
        Assert.AreEqual("allCompletedDelivered", result.DrainReason);
        if (qos == 0) Assert.IsNull(result.Measurement.PublishLatencyMs);
        else Assert.IsTrue(result.Measurement.PublishLatencyMs!.MinMs > 0);
        var publishes = File.ReadAllLines(System.IO.Path.Combine(temp.Path, "publishes.ndjson"))
            .Select(x => JsonDocument.Parse(x)).Where(x => x.RootElement.GetProperty("event").GetString() == "attempt").ToArray();
        Assert.AreEqual(result.Measurement.AttemptedPublishes, (long)publishes.Length);
        Assert.AreEqual(publishes.Length, publishes.Select(x => x.RootElement.GetProperty("id").GetString()).Distinct().Count());
        Assert.IsTrue(publishes.All(x => x.RootElement.GetProperty("payloadBytes").GetInt32() == 16));
        await Assert.ThrowsExceptionAsync<IOException>(() => runner.RunAsync(new CampaignDefinition(), key, broker.Uri, "test", "same", temp.Path, .5));
    }

    [TestMethod]
    public async Task Preflight_ProbesEachQosAnd20EchoRttsWithoutInventingHiddenSysVersion()
    {
        await using var broker = await LocalBroker.StartAsync();
        var runner = new MqttCampaignRunner(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(30));
        var result = await runner.PreflightAsync("Mosquitto", broker.Uri);
        Assert.IsTrue(result.Success);
        Assert.AreEqual(3, result.Probes.Count);
        Assert.IsTrue(result.Probes.All(x => x.Connect && x.Subscribe && x.Publish && x.Delivered));
        CollectionAssert.AreEqual(new[] { 0, 1, 2 }, result.Probes.Select(x => x.Qos).ToArray());
        Assert.AreEqual(20, result.RttSamplesMs.Count);
        Assert.IsTrue(result.RttSamplesMs.All(x => x > 0));
        Assert.IsNull(result.SysBrokerVersion);
        Assert.AreEqual("unavailable", result.SysVersionStatus);
    }

    [TestMethod]
    public async Task Preflight_ReportsRejectedQosInsteadOfCallingItSuccessful()
    {
        await using var broker = await LocalBroker.StartAsync();
        broker.Server.InterceptingSubscriptionAsync += args =>
        {
            if (args.TopicFilter.QualityOfServiceLevel == MqttQualityOfServiceLevel.ExactlyOnce)
                args.Response.ReasonCode = MqttSubscribeReasonCode.UnspecifiedError;
            return Task.CompletedTask;
        };
        var result = await new MqttCampaignRunner(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10))
            .PreflightAsync("Mosquitto", broker.Uri);
        Assert.IsFalse(result.Success);
        Assert.IsFalse(result.Probes.Single(x => x.Qos == 2).Subscribe);
        Assert.IsNotNull(result.Probes.Single(x => x.Qos == 2).Error);
    }
}

internal sealed class LocalBroker : IAsyncDisposable
{
    public MqttServer Server { get; }
    public Uri Uri { get; }
    private LocalBroker(MqttServer server, int port) => (Server, Uri) = (server, new Uri($"mqtt://127.0.0.1:{port}"));
    public static async Task<LocalBroker> StartAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var server = new MqttFactory().CreateMqttServer(new MqttServerOptionsBuilder().WithDefaultEndpoint()
            .WithDefaultEndpointBoundIPAddress(IPAddress.Loopback).WithDefaultEndpointPort(port).Build());
        await server.StartAsync();
        return new(server, port);
    }
    public async ValueTask DisposeAsync() { await Server.StopAsync(); Server.Dispose(); }
}
