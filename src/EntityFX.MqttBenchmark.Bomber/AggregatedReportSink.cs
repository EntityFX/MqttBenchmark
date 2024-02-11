using EntityFX.MqttBenchmark.Bomber.Settings;
using Microsoft.Extensions.Configuration;
using NBomber.Contracts;
using NBomber.Contracts.Stats;
using System.Data;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace EntityFX.MqttBenchmark.Bomber;

public class AggregatedReportSink : IReportingSink
{
    private ScenarioParamsTemplate? scenarioParamsTemplate;
    private Dictionary<string, ScenarioParamsTemplate> scenarioSubSubGroup = new Dictionary<string, ScenarioParamsTemplate>();
    private readonly MqttCounterClient mqttCounterClient;
    private readonly string countersPath;

    public AggregatedReportSink(MqttCounterClient mqttCounterClient, string countersPath)
    {
        this.mqttCounterClient = mqttCounterClient;
        this.countersPath = countersPath;
    }

    public Dictionary<string, (NodeStats NodeStats, Dictionary<string, ScenarioParamsTemplate> ScenarioSubSubGroup, Dictionary<string, int> Counters)>
        AllNodeStats { get; } 
        = new Dictionary<string, (NodeStats NodeStats, Dictionary<string, ScenarioParamsTemplate> ScenarioSubSubGroup, Dictionary<string, int> Counters)>();

    public string SinkName => nameof(AggregatedReportSink);

    public void Dispose()
    {
    }

    public Task Init(IBaseContext context, IConfiguration infraConfig)
    {
        return Task.CompletedTask;
    }

    public async Task SaveFinalStats(NodeStats stats)
    {
        var counters = scenarioSubSubGroup.ToDictionary(sg => sg.Key, async sg => await mqttCounterClient
            .GetCounter(sg.Value.Server, sg.Value.Topic))
            .ToDictionary(sg => sg.Key, sg => sg.Value.Result);

        //var dataSetJson = JsonSerializer.Serialize(counters, new JsonSerializerOptions(new JsonSerializerOptions() { WriteIndented = true }));
        //await File.WriteAllTextAsync(countersPath, dataSetJson);

        AllNodeStats.Add(stats.TestInfo.SessionId, (stats, scenarioSubSubGroup, counters));
    }

    public Task SaveRealtimeStats(ScenarioStats[] stats)
    {
        return Task.CompletedTask;
    }

    public async Task Start()
    {
        foreach (var sg in scenarioSubSubGroup)
        {
            await mqttCounterClient.ClearCounter(sg.Value!.Server, sg.Value!.Topic);
        }
    }

    public Task Stop()
    {
        return Task.CompletedTask;
    }

    public string AsCsv()
    {
        var allStats = AllNodeStats.SelectMany(
            nodeStat =>
            {

                var scenarioStats = nodeStat.Value.NodeStats.ScenarioStats.Select(

                ss =>
                {
                    var ssg = nodeStat.Value.ScenarioSubSubGroup?.GetValueOrDefault(ss.ScenarioName);
                    var receiveCounter = nodeStat.Value.Counters?.GetValueOrDefault(ss.ScenarioName) ?? 0;

                    return ss.StepStats.Where(sts => sts.StepName == "publish")?.Select(sts => new Dictionary<string, object>()
                    {
                        ["scenario"] = ss.ScenarioName,
                        ["params_qos"] = ssg?.Params?.GetValueOrDefault("qos", -1) ?? -1,
                        ["params_clients"] = ssg?.Params?.GetValueOrDefault("clients", -1) ?? -1,
                        ["params_message_size"] = ssg?.Params?.GetValueOrDefault("message", -1) ?? -1,
                        ["duration"] = ss.Duration,
                        ["step_name"] = sts.StepName,
                        ["request_count"] = sts.Ok.Request.Count + sts.Fail.Request.Count,
                        ["ok"] = sts.Ok.Request.Count,
                        ["failed"] = sts.Fail.Request.Count,
                        ["received"] = receiveCounter,
                        ["rps"] = sts.Ok.Request.RPS,
                        ["min"] = sts.Ok.Latency.MinMs,
                        ["mean"] = sts.Ok.Latency.MeanMs,
                        ["max"] = sts.Ok.Latency.MaxMs,
                        ["50_percent"] = sts.Ok.Latency.Percent50,
                        ["75_percent"] = sts.Ok.Latency.Percent75,
                        ["95_percent"] = sts.Ok.Latency.Percent95,
                        ["99_percent"] = sts.Ok.Latency.Percent99,
                        ["std_dev"] = sts.Ok.Latency.StdDev,
                        ["data_transfer_min"] = sts.Ok.DataTransfer.MinBytes,
                        ["data_transfer_mean"] = sts.Ok.DataTransfer.MeanBytes,
                        ["data_transfer_max"] = sts.Ok.DataTransfer.MaxBytes,
                        ["data_transfer_all"] = sts.Ok.DataTransfer.AllBytes,
                        ["start_date_time"] = nodeStat.Value.NodeStats.TestInfo.Created.ToString("s", CultureInfo.InvariantCulture)
                    })?.FirstOrDefault() ?? new Dictionary<string, object>();


                });

                return scenarioStats;
            }).ToArray();

        //var allStats = new Dictionary<string, object>[0];

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", allStats.FirstOrDefault()?.Keys.ToArray() ?? Array.Empty<string>()));

        foreach (var statItem in allStats)
        {
            sb.AppendLine(string.Join(",",
                statItem.Values.Select(si => Convert.ToString(si, CultureInfo.InvariantCulture))));
        }

        return sb.ToString();
    }

    public string AsMd()
    {
        var allStats = AllNodeStats.SelectMany(
            nodeStat =>
            {

                var scenarioStats = nodeStat.Value.NodeStats.ScenarioStats.Select(

                ss =>
                {
                    var ssg = nodeStat.Value.ScenarioSubSubGroup?.GetValueOrDefault(ss.ScenarioName);
                    var receiveCounter = nodeStat.Value.Counters?.GetValueOrDefault(ss.ScenarioName) ?? 0;

                    return ss.StepStats.Where(sts => sts.StepName == "publish")?.Select(sts => new Dictionary<string, object>()
                    {
                        ["scenario"] = ss.ScenarioName,
                        ["params_qos"] = ssg?.Params?.GetValueOrDefault("qos", -1) ?? -1,
                        ["params_clients"] = ssg?.Params?.GetValueOrDefault("clients", -1) ?? -1,
                        ["params_message_size"] = ssg?.Params?.GetValueOrDefault("message", -1) ?? -1,
                        ["duration"] = ss.Duration,
                        ["step_name"] = sts.StepName,
                        ["request_count"] = sts.Ok.Request.Count + sts.Fail.Request.Count,
                        ["ok"] = sts.Ok.Request.Count,
                        ["failed"] = sts.Fail.Request.Count,
                        ["received"] = receiveCounter,
                        ["rps"] = sts.Ok.Request.RPS,
                        ["min"] = sts.Ok.Latency.MinMs,
                        ["mean"] = sts.Ok.Latency.MeanMs,
                        ["max"] = sts.Ok.Latency.MaxMs,
                        ["50_percent"] = sts.Ok.Latency.Percent50,
                        ["75_percent"] = sts.Ok.Latency.Percent75,
                        ["95_percent"] = sts.Ok.Latency.Percent95,
                        ["99_percent"] = sts.Ok.Latency.Percent99,
                        ["std_dev"] = sts.Ok.Latency.StdDev,
                        ["data_transfer_min"] = sts.Ok.DataTransfer.MinBytes,
                        ["data_transfer_mean"] = sts.Ok.DataTransfer.MeanBytes,
                        ["data_transfer_max"] = sts.Ok.DataTransfer.MaxBytes,
                        ["data_transfer_all"] = sts.Ok.DataTransfer.AllBytes,
                        ["start_date_time"] = nodeStat.Value.NodeStats.TestInfo.Created.ToString("s", CultureInfo.InvariantCulture)
                    })?.FirstOrDefault() ?? new Dictionary<string, object>();
                });

                return scenarioStats;
            }).ToArray();

        var sb = new StringBuilder();
        sb.AppendLine($"|{string.Join("|", allStats.FirstOrDefault()?.Keys.ToArray() ?? Array.Empty<string>())}|");
        sb.AppendLine($"|{string.Join("|", Enumerable.Range(0, allStats.FirstOrDefault()?.Count ?? 1).Select(i => "-"))}|");

        foreach (var statItem in allStats)
        {
            sb.AppendLine($"|{string.Join("|", statItem.Values.Select(si => Convert.ToString(si, CultureInfo.InvariantCulture)))}");
        }

        return sb.ToString();
    }

    internal IReportingSink WithScenarioParams(ScenarioParamsTemplate scenarioParamsTemplate)
    {
        this.scenarioParamsTemplate = scenarioParamsTemplate;

        return this;
    }

    internal void AddScenarioSubGroup(Dictionary<string, ScenarioParamsTemplate> scenarioSubSubGroup)
    {
        this.scenarioSubSubGroup = scenarioSubSubGroup;
    }
}