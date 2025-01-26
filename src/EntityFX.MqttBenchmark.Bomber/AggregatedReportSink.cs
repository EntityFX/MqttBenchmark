using EntityFX.MqttBenchmark.Bomber.Settings;
using Microsoft.Extensions.Configuration;
using NATS.Client.JetStream;
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
    private readonly Dictionary<string, long> recievedCounters;
    private readonly Dictionary<string, ScenarioTimeStats> scenarioTimeStats;
    private readonly string countersPath;

    private IConfiguration? infraConfig;
    private IBaseContext? context;

    public AggregatedReportSink(
        Dictionary<string, long> recievedCounters,
        Dictionary<string, ScenarioTimeStats> scenarioTimeStats,
        string countersPath)
    {
        this.recievedCounters = recievedCounters;
        this.scenarioTimeStats = scenarioTimeStats;
        this.countersPath = countersPath;
    }

    public Dictionary<string, (NodeStats NodeStats, Dictionary<string, ScenarioParamsTemplate> ScenarioSubSubGroup)>
        AllNodeStats
    { get; }
        = new Dictionary<string, (NodeStats NodeStats, Dictionary<string, ScenarioParamsTemplate> ScenarioSubSubGroup)>();

    public string SinkName => nameof(AggregatedReportSink);

    public void Dispose()
    {
    }

    public Task Init(IBaseContext context, IConfiguration infraConfig)
    {
        this.infraConfig = infraConfig;
        this.context = context;

        return Task.CompletedTask;
    }

    public async Task SaveFinalStats(NodeStats stats)
    {
        // var counters = scenarioSubSubGroup.ToDictionary(sg => sg.Key, sg => mqttCounterClient
        //     .GetCounterAndValidate(sg.Value.Server, sg.Value.Topic).Result)
        //     .ToDictionary(sg => sg.Key, sg => sg.Value);

        AllNodeStats.Add(stats.TestInfo.SessionId, (stats, scenarioSubSubGroup));


        var localStats = ToCsv(stats, scenarioSubSubGroup);
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", localStats.FirstOrDefault()?.Keys.ToArray() ?? Array.Empty<string>()));

        foreach (var statItem in localStats)
        {
            sb.AppendLine(string.Join(",",
                statItem.Values.Select(si => Convert.ToString(si, CultureInfo.InvariantCulture))));
        }

        var scenario =  scenarioSubSubGroup.FirstOrDefault().Key ?? stats.TestInfo.SessionId;

        await File.WriteAllTextAsync(Path.Combine(countersPath, scenario, "report.csv"), sb.ToString());

        var all = AsCsv();
        await File.WriteAllTextAsync(Path.Combine(countersPath, scenario, "all.csv"), all);
    }

    public Task SaveRealtimeStats(NBomber.Contracts.Stats.ScenarioStats[] stats)
    {
        return Task.CompletedTask;
    }

    public Task Start()
    {
        return Task.CompletedTask;
    }

    public Task Stop()
    {
        return Task.CompletedTask;
    }

    private Dictionary<string, object>[] ToCsv(NodeStats nodeStats, Dictionary<string, ScenarioParamsTemplate> scenarioSubSubGroup)
    {
        var scenarioStats = nodeStats.ScenarioStats.Select(
            ss =>
            {
                var ssg = scenarioSubSubGroup?.GetValueOrDefault(ss.ScenarioName);

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
                    ["received"] = recievedCounters.GetValueOrDefault(ss.ScenarioName),
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
                    ["start_date_time"] = nodeStats.TestInfo.Created.ToString("s", CultureInfo.InvariantCulture),
                    ["test_build_time"] = scenarioTimeStats.GetValueOrDefault(ss.ScenarioName)?.BuildTime.ToString("s", CultureInfo.InvariantCulture) ?? string.Empty,
                    ["test_init_start_time"] = scenarioTimeStats.GetValueOrDefault(ss.ScenarioName)?.InitStartTime.ToString("s", CultureInfo.InvariantCulture) ?? string.Empty,
                    ["test_init_end_time"] = scenarioTimeStats.GetValueOrDefault(ss.ScenarioName)?.InitEndTime.ToString("s", CultureInfo.InvariantCulture) ?? string.Empty,
                    ["test_clean_start_time"] = scenarioTimeStats.GetValueOrDefault(ss.ScenarioName)?.CleanStartTime.ToString("s", CultureInfo.InvariantCulture) ?? string.Empty,
                    ["test_clean_end_time"] = scenarioTimeStats.GetValueOrDefault(ss.ScenarioName)?.CleanEndTime.ToString("s", CultureInfo.InvariantCulture) ?? string.Empty,
                })?.FirstOrDefault() ?? new Dictionary<string, object>();
        });

        return scenarioStats.ToArray();
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
                        ["received"] = recievedCounters.GetValueOrDefault(ss.ScenarioName),
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
                    //var receiveCounter = nodeStat.Value.Counters?.GetValueOrDefault(ss.ScenarioName) ?? 0;

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
                        ["received"] = recievedCounters.GetValueOrDefault(ss.ScenarioName),
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