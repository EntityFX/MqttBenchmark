using Microsoft.Extensions.Configuration;
using NBomber.Contracts;
using NBomber.Contracts.Stats;

namespace EntityFX.MqttBenchmark.Bomber;

public class AggregatedReportSink : IReportingSink
{
    public Dictionary<string, NodeStats> AllNodeStats { get; } = new Dictionary<string, NodeStats>();

    public string SinkName => nameof(AggregatedReportSink);

    public void Dispose()
    {
    }

    public Task Init(IBaseContext context, IConfiguration infraConfig)
    {
        return Task.CompletedTask;
    }

    public Task SaveFinalStats(NodeStats stats)
    {
        AllNodeStats.Add(stats.TestInfo.SessionId, stats);
        return Task.CompletedTask;
    }

    public Task SaveRealtimeStats(ScenarioStats[] stats)
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
}