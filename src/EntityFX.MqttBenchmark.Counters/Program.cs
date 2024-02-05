using Microsoft.Extensions.Hosting;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Packets;
using MQTTnet.Protocol;
using System.Collections.Concurrent;
using System.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
app.MapSwagger();

app.UseSwagger();
app.UseSwaggerUI();

var rootConfig = app.Services.GetRequiredService<IConfiguration>();
ILogger<Program> logger = app.Services.GetRequiredService<ILogger<Program>>();
var settings = rootConfig.GetSection("Mqtt").Get<MqttSettings[]>();
Dictionary<string, bool> status = settings.ToDictionary(kv => kv.Broker.ToString(), kv => false);
Dictionary<string, int> wait = settings.ToDictionary(kv => kv.Broker.ToString(), kv => 0);

// Configure the HTTP request pipeline.

ConcurrentDictionary<(string Broker, string Topic), int> countersStore =
    new ConcurrentDictionary<(string, string), int>();




object _lock = new { };

var mqttClients = settings.Select(async s => await ConnectMqttAndSubscribe(s))
    .Where(mq => mq != null).ToArray();

MapApi(app, countersStore);



app.Run();

async Task<IMqttClient?> ConnectMqttAndSubscribe(MqttSettings mqttSettings)
{
    var mqttFactory = new MqttFactory();

    var mqttClient = mqttFactory.CreateMqttClient();



    var mqttClientOptions = new MqttClientOptionsBuilder()
        .WithTcpServer(mqttSettings.Broker.Host, mqttSettings.Broker.Port)
        .Build();

    var brokerName = mqttSettings.Broker.ToString();

    mqttClient.ConnectedAsync += async e =>
    {
        logger.LogInformation($"{DateTime.Now}: Connected to  {mqttSettings.Broker}");
        var mqttSubscribeBuider = mqttFactory.CreateSubscribeOptionsBuilder();
        foreach (var topic in mqttSettings.Topics)
        {
            countersStore.TryAdd((brokerName, topic.Topic), 0);
            mqttSubscribeBuider.WithTopicFilter(
                f =>
                {
                    f.WithTopic(topic.Topic).WithQualityOfServiceLevel((MqttQualityOfServiceLevel)topic.Qos);
                });
        }

        var mqttSubscribeOptions = mqttSubscribeBuider.Build();

        var res = await mqttClient.SubscribeAsync(mqttSubscribeOptions, CancellationToken.None);
    };

    mqttClient.DisconnectedAsync += async e =>
    {
        int reconnectDelay = 0;
        lock (_lock)
        {
            reconnectDelay = wait[brokerName];
            reconnectDelay = reconnectDelay == 0 ? 5 : (reconnectDelay >= 60 ? 60 : reconnectDelay * 2);
            wait[brokerName] = reconnectDelay;
        }

        await Task.Delay(TimeSpan.FromSeconds(reconnectDelay));
        logger.LogWarning($"{DateTime.Now}: Re-connect to {mqttSettings.Broker}");
        var result = ConnectAsync(mqttClient, mqttClient.Options, brokerName);
    };

    mqttClient.ApplicationMessageReceivedAsync += e =>
    {
        Increment(brokerName, e.ApplicationMessage.Topic);
        return Task.CompletedTask;
    };

    var result = ConnectAsync(mqttClient, mqttClientOptions, brokerName);

    return mqttClient;
}

async Task<MqttClientConnectResult?> ConnectAsync(
    IMqttClient mqttClient, MqttClientOptions mqttClientOptions, string brokerName)
{
    try
    {
        var connResult = await mqttClient.ConnectAsync(mqttClientOptions);
        status[brokerName] = true;

        return connResult;
    }
    catch (Exception ex)
    {
        status[brokerName] = false;
        logger.LogError(ex.Message);
    }
    return null;
}

int Increment(string broker, string topic)
{
    return countersStore.AddOrUpdate((broker, topic), 0, (k, v) => Interlocked.Increment(ref v));
}

void MapApi(WebApplication app, ConcurrentDictionary<(string Broker, string Topic), int> countersStore)
{
    app.MapGet("/counter/{broker}/{topic}", (string broker, string topic) =>
    {
        broker = HttpUtility.UrlDecode(broker);
        topic = HttpUtility.UrlDecode(topic);
        return countersStore.GetValueOrDefault((broker, topic), 0);
    });

    app.MapGet("/counter/{broker}", (string broker) =>
    {
        broker = HttpUtility.UrlDecode(broker);
        return countersStore.Where(cs => cs.Key.Broker == broker)
        .ToDictionary(kv => kv.Key.Topic, kv => kv.Value);
    });

    app.MapGet("/counter", () =>
    {
        return countersStore
        .GroupBy(kv => kv.Key.Broker)
        .ToDictionary(kv => kv.Key, kv => kv.ToDictionary(kv1 => kv1.Key.Topic, kv1 => kv1.Value));
    });

    app.MapGet("/status", () =>
    {
        return status;
    });

    app.MapDelete("/counter/{broker}/{topic}", (string broker, string topic) =>
    {
        broker = HttpUtility.UrlDecode(broker);
        topic = HttpUtility.UrlDecode(topic);
        return countersStore[(broker, topic)] = 0;
    });

    app.MapDelete("/counter/{broker}", (string broker) =>
    {
        broker = HttpUtility.UrlDecode(broker);
        var items =  countersStore.Where(cs => cs.Key.Broker == broker).Select(t => t.Key.Topic);
        foreach (var item in items)
        {
            countersStore[(broker, item)] = 0;
        }
    });

    app.MapDelete("/counter", () =>
    {
        countersStore.Clear();
    });
}