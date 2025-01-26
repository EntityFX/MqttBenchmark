using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using TimeSpan = System.TimeSpan;
using System.Security.Cryptography;
using EntityFX.MqttBenchmark.Helper;

namespace EntityFX.MqttBenchmark;

public class MqttBenchmark
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
        var startDateTime = DateTimeOffset.UtcNow;
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
        var endDateTime = DateTimeOffset.UtcNow;
        return new BenchmarkResults(
            testName, clientsCount, totalResults, results,
            startDateTime, endDateTime, _settings);
    }

    private TotalResults CalculateTotalResults(IEnumerable<RunResults> runResults, TimeSpan testTime)
    {
        var runResultsArray = runResults.ToArray() ?? Array.Empty<RunResults>();

        if (!runResultsArray.Any())
        {
            return new TotalResults(
                1, 0, 0, 0,
                TimeSpan.Zero,
                TimeSpan.Zero,
                TimeSpan.Zero,
                TimeSpan.Zero,
                TimeSpan.Zero,
                TimeSpan.Zero,
                0,
                0,
                0,
                0, 0, string.Empty);
        }

        var successes = runResultsArray.Sum(r => r.Seccesses);
        var failures = runResultsArray.Sum(r => r.Failures);
        var ratio = successes > 0 ? successes / (decimal)(successes + failures) : 0;
        var totalBytes = runResultsArray.Sum(r => r.BytesSent);
        var first = runResultsArray.FirstOrDefault();

        return new TotalResults(
            ratio, successes, failures, 0,
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
            totalBytes, first?.Qos ?? 0, first?.Topic ?? string.Empty);
    }

    private async Task<RunResults> SendMessages(IMqttClient mqttClient)
    {
        TimeSpan duration = TimeSpan.Zero;
        TimeSpan successDuration = TimeSpan.Zero;
        var msgSw = new Stopwatch();

        var msgTimings = new List<TimeSpan>();

        msgSw.Start();

        long succeed = 0;
        long failed = 0;
        long total = 0;
        bool end = false;
        long totalBytes = 0;

        TimeSpan elapsed = TimeSpan.Zero;

        while (!end)
        {
            var message = BuildMessage();

            try
            {
                msgSw.Restart();
                var result = await mqttClient.PublishAsync(message);
                elapsed = msgSw.Elapsed;
                successDuration += elapsed;
                if (result.ReasonCode == MqttClientPublishReasonCode.Success)
                {
                    succeed++;
                    totalBytes += message.PayloadSegment.Count;
                }
                else
                {
                    failed++;
                }
            }
            catch (Exception)
            {
                elapsed = msgSw.Elapsed;
                failed++;
            }
            finally
            {
                duration += elapsed;
                msgTimings.Add(elapsed);
            }

            if (_settings.MessageCount != null && total >= _settings.MessageCount - 1
                || _settings.TestMaxTime != null && duration >= _settings.TestMaxTime
                || _settings.MaxFailure != null && failed >= _settings.MaxFailure)
            {
                end = true;
            }

            total++;
        }

        return GetResults(msgTimings, total,
            mqttClient.Options.ClientId, successDuration, succeed, failed, totalBytes,
            _settings.Qos ?? 0, _settings.Topic ?? string.Empty);
    }

    private RunResults GetResults(List<TimeSpan> msgTimings, long count,
        string clientId, TimeSpan duration, long succeed, long failed, long totalBytes,
        int qos, string topic)
    {
        if (msgTimings?.Any() != true)
        {
            return new RunResults(clientId, succeed, failed, duration,
                TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero,
                0, 0, totalBytes, qos, topic);
        }


        var standardDeviation = msgTimings.Select(s => s.TotalMilliseconds).StandardDeviation();

        return new RunResults(
            clientId, succeed, failed, duration, msgTimings.Min(),
            msgTimings.Max(),
            TimeSpan.FromMilliseconds(msgTimings.Average(s => s.TotalMilliseconds)),
            (decimal)standardDeviation,
            (decimal)(duration > TimeSpan.Zero ? succeed / duration.TotalSeconds : 0), totalBytes,
            qos,
            topic
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
                .WithCleanSession(true)
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
            : GetRandomString(_settings.MessageSize!.Value);


        var applicationMessage = new MqttApplicationMessageBuilder()
            .WithTopic(_settings.Topic)
            .WithQualityOfServiceLevel((MqttQualityOfServiceLevel)(_settings.Qos ?? 0))
            .WithPayload(payload)
            .Build();

        return applicationMessage;
    }

    private static string GetRandomString(int length)
    {
        var bit_count = length * 6;
        var byte_count = (bit_count + 7) / 8; // rounded up
        var bytes = RandomNumberGenerator.GetBytes(byte_count);
        return Convert.ToBase64String(bytes);
    }
}