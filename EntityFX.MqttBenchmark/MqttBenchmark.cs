using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using EntityFX.MqttBenchmark.Helpers;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using TimeSpan = System.TimeSpan;

namespace EntityFX.MqttBenchmark;

class MqttBenchmark
{
    private readonly Settings _settings;

    public MqttBenchmark(Settings settings)
    {
        _settings = settings;
    }

    public async Task<BenchmarkResults> Run(string testName)
    {
        var clients = await BuildClients(testName);
        var clientsCount = clients.Count();

        var testTimeSw = new Stopwatch();
        testTimeSw.Start();

        var clientTasks = new List<Task<RunResults>>();
        for (var i = 0; i < clients.Count; i++)
        {
            if (clients.TryPeek(out var client))
            {
                clientTasks.Add(Task.Run(async () => await SendMessages(client)));
            }
        }
        
        var results = await Task.WhenAll(clientTasks);

        var totalResults = CalculateTotalResults(results, testTimeSw.Elapsed);

        return new BenchmarkResults(testName, clientsCount, totalResults, results, _settings);
    }

    private TotalResults CalculateTotalResults(IEnumerable<RunResults> runResults, TimeSpan testTime)
    {
        var runResultsArray = runResults.ToArray() ?? Array.Empty<RunResults>();

        if (!runResultsArray.Any())
        {
            return new TotalResults(
                1, 0, 0,
                TimeSpan.Zero,
                TimeSpan.Zero,
                TimeSpan.Zero,
                TimeSpan.Zero,
                TimeSpan.Zero,
                TimeSpan.Zero,
                0,
                0,
                0,
                0);
        }

        var successes = runResultsArray.Sum(r => r.Seccesses);
        var failures = runResultsArray.Sum(r => r.Failures);
        var ratio = successes > 0 ? successes / (decimal)(successes + failures) : 0;
        var totalBytes = runResultsArray.Sum(r => r.BytesSent);

        return new TotalResults(
            ratio, successes, failures,
            runResultsArray.Max(r => r.RunTime),
            testTime,
            TimeSpan.FromMilliseconds(runResultsArray.Average(r => r.RunTime.TotalMilliseconds)),
            runResultsArray.Min(r => r.MessageTimeMin),
            runResultsArray.Max(r => r.MessageTimeMax),
            TimeSpan.FromMilliseconds(
                runResultsArray.Average(r => r.MessageTimeMean.TotalMilliseconds)),
            (decimal)runResultsArray.Select(s => s.MessageTimeMean.TotalMilliseconds).StandardDeviation(),
            runResultsArray.Sum(r => r.MessagesPerSecond),
            runResultsArray.Average(r => r.MessagesPerSecond),
            totalBytes);
    }

    private async Task<RunResults> SendMessages(IMqttClient mqttClient)
    {
        TimeSpan duration = TimeSpan.Zero;
        var msgSw = new Stopwatch();

        var msgTimings = new List<TimeSpan>();

        msgSw.Start();

        int succeed = 0;
        int failed = 0;
        int total = 0;
        bool end = false;
        int totalBytes = 0;

        while (!end)
        {
            var message = BuildMessage();

            msgSw.Restart();

            try
            {
                var reasonCode = (await mqttClient.PublishAsync(message)).ReasonCode;

                if (reasonCode == MqttClientPublishReasonCode.Success)
                {
                    succeed++;
                    totalBytes += message.Payload.Length;
                    msgTimings.Add(msgSw.Elapsed);
                }
                else
                {
                    failed++;
                }
            }
            catch (Exception)
            {
                failed++;
            }
            finally
            {
                duration += msgSw.Elapsed;
            }

            if ((_settings.MessageCount != null && total >= _settings.MessageCount - 1)
                || (_settings.TestMaxTime != null && duration >= _settings.TestMaxTime))
            {
                end = true;
            }

            total++;
        }

        return GetResults(msgTimings, total,
            mqttClient.Options.ClientId, duration, succeed, failed, totalBytes);
    }

    private RunResults GetResults(List<TimeSpan> msgTimings, int count,
        string clientId, TimeSpan duration, int succeed, int failed, int totalBytes)
    {
        if (msgTimings?.Any() != true)
        {
            return new RunResults(clientId, succeed, failed, duration,
                TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero,
                0, 0, totalBytes);
        }


        var standardDeviation = msgTimings.Select(s => s.TotalMilliseconds).StandardDeviation();

        return new RunResults(
            clientId, succeed, failed, duration, msgTimings.Min(),
            msgTimings.Max(),
            TimeSpan.FromMilliseconds(msgTimings.Average(s => s.TotalMilliseconds)),
            (decimal)standardDeviation,
            (decimal)(count / duration.TotalSeconds), totalBytes
        );
    }

    private async Task<ConcurrentBag<IMqttClient>> BuildClients(string test)
    {
        var mqttFactory = new MqttFactory();

        var clients = Enumerable.Range(0, _settings.Clients!.Value).Select(_ =>
            mqttFactory.CreateMqttClient());

        var clientsBag = new ConcurrentBag<IMqttClient>();

        foreach (var client in clients)
        {
            var mqttClientOptions = new MqttClientOptionsBuilder()
                .WithTcpServer(options =>
                {
                    options.Server = _settings.Broker!.Host;
                    options.Port = _settings.Broker.Port;
                })
                .WithClientId($"{_settings.ClientPrefix}-{test}")
                .WithCleanSession(false)
                .WithTimeout(_settings.PublishTimeout!.Value)
                .Build();

            if (await TryConnect(client, mqttClientOptions))
            {
                clientsBag.Add(client);
            }
        }

        return clientsBag;
    }

    private async Task<bool> TryConnect(IMqttClient mqttClient, MqttClientOptions mqttClientOptions)
    {
        int attempts = _settings?.ConnectAttempts ?? 5;

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
            catch
            {
                //ignore here
            }
            finally
            {
                attempts--;
            }

            Thread.Sleep(1);
        }

        return false;
    }

    private MqttApplicationMessage BuildMessage()
    {
        var payload = !string.IsNullOrEmpty(_settings.Payload)
            ? _settings.Payload
            : new string('a', _settings.MessageSize!.Value);


        var applicationMessage = new MqttApplicationMessageBuilder()
            .WithTopic(_settings.Topic)
            .WithQualityOfServiceLevel((MqttQualityOfServiceLevel)(_settings.Qos ?? 1))
            .WithPayload(payload)
            .Build();

        return applicationMessage;
    }
}