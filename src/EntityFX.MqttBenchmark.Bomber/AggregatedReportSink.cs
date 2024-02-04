using EntityFX.MqttBenchmark.Bomber.Settings;
using Microsoft.Extensions.Configuration;
using NBomber.Contracts;
using NBomber.Contracts.Stats;
using System.Data;
using System.Globalization;
using System.Text;

namespace EntityFX.MqttBenchmark.Bomber;

public class AggregatedReportSink : IReportingSink
{
    private ScenarioParamsTemplate? scenarioParamsTemplate;
    private readonly MqttCounterClient mqttCounterClient;

    public AggregatedReportSink(MqttCounterClient mqttCounterClient)
    {
        this.mqttCounterClient = mqttCounterClient;
    }

    public Dictionary<string, (NodeStats NodeStats, ScenarioParamsTemplate? Params)> AllNodeStats { get; } = new Dictionary<string, (NodeStats NodeStats, ScenarioParamsTemplate Params)>();

    public string SinkName => nameof(AggregatedReportSink);

    public void Dispose()
    {
    }

    public Task Init(IBaseContext context, IConfiguration infraConfig)
    {
        //var brokerUrl = (new UriBuilder("mqtt", settings.Server, settings.Port)).Uri;

        // var counter = await _mqttCounterClient.GetCounter(brokerUrl.ToString(), settings.Topic);

        return Task.CompletedTask;
    }

    public async Task SaveFinalStats(NodeStats stats)
    {
        var counter = await mqttCounterClient
            .GetCounter(scenarioParamsTemplate!.Server, scenarioParamsTemplate!.Topic);


        var dataSet = new System.Data.DataSet("Counters");
        var dt = new System.Data.DataTable("Receive");
        var dc = new DataColumn("ReceiveCounter", typeof(int));
        dt.Columns.Add(dc);
        dataSet.Tables.Add(dt);

        var row = dt.NewRow();
        row["ReceiveCounter"] = counter;

        dt.Rows.Add(row);
        dataSet.AcceptChanges();

        stats.PluginStats = new DataSet[] { dataSet };

        AllNodeStats.Add(stats.TestInfo.SessionId, (stats, scenarioParamsTemplate));
    }

    public Task SaveRealtimeStats(ScenarioStats[] stats)
    {
        return Task.CompletedTask;
    }

    public async Task Start()
    {
        await mqttCounterClient
            .ClearCounter(scenarioParamsTemplate!.Server, scenarioParamsTemplate!.Topic);
    }

    public Task Stop()
    {
        return Task.CompletedTask;
    }

    public string AsCsv()
    {
        var allStats = AllNodeStats.SelectMany(
            nodeStat => nodeStat.Value.NodeStats.ScenarioStats.Select(
                ss => ss.StepStats.Where(sts => sts.StepName == "publish")?.Select(sts => new Dictionary<string, object>()
                {
                    ["scenario"] = ss.ScenarioName,
                    ["params_qos"] = nodeStat.Value.Params?.Params?.GetValueOrDefault("qos", -1) ?? -1,
                    ["params_clients"] = nodeStat.Value.Params?.Params?.GetValueOrDefault("clients", -1) ?? -1,
                    ["params_message_size"] = nodeStat.Value.Params?.Params?.GetValueOrDefault("message", -1) ?? -1,
                    ["duration"] = ss.Duration,
                    ["step_name"] = sts.StepName,
                    ["request_count"] = sts.Ok.Request.Count + sts.Fail.Request.Count,
                    ["ok"] = sts.Ok.Request.Count,
                    ["failed"] = sts.Fail.Request.Count,
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
                    ["data_transfer_all"] = sts.Ok.DataTransfer.AllBytes
                })?.FirstOrDefault() ?? new Dictionary<string, object>())).ToArray();

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
            nodeStat => nodeStat.Value.NodeStats.ScenarioStats.Select(
                ss => ss.StepStats.Where(sts => sts.StepName == "publish")?.Select(sts => new Dictionary<string, object>()
                {
                    ["scenario"] = ss.ScenarioName,
                    ["params_qos"] = nodeStat.Value.Params?.Params?.GetValueOrDefault("qos", -1) ?? -1,
                    ["params_clients"] = nodeStat.Value.Params?.Params?.GetValueOrDefault("clients", -1) ?? -1,
                    ["params_message_size"] = nodeStat.Value.Params?.Params?.GetValueOrDefault("message", -1) ?? -1,
                    ["duration"] = ss.Duration,
                    ["step_name"] = sts.StepName,
                    ["request_count"] = sts.Ok.Request.Count + sts.Fail.Request.Count,
                    ["ok"] = sts.Ok.Request.Count,
                    ["failed"] = sts.Fail.Request.Count,
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
                    ["data_transfer_all"] = sts.Ok.DataTransfer.AllBytes
                })?.FirstOrDefault() ?? new Dictionary<string, object>())).ToArray();

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
}