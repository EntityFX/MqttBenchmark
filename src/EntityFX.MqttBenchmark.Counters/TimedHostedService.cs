using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

public class TimedHostedService : BackgroundService
{
    private readonly ILogger<TimedHostedService> _logger;
    private readonly ConcurrentDictionary<(string Broker, string Topic), int> countersStore;
    private int _executionCount;

    public TimedHostedService(ILogger<TimedHostedService> logger,
    System.Collections.Concurrent.ConcurrentDictionary<(string Broker, string Topic), int> countersStore)
    {
        _logger = logger;
        this.countersStore = countersStore;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Timed Hosted Service running.");

        // When the timer should have no due-time, then do the work once now.
        LogCounters();

        using PeriodicTimer timer = new(TimeSpan.FromSeconds(5));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                LogCounters();
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Timed Hosted Service is stopping.");
        }
    }

    // Could also be a async method, that can be awaited in ExecuteAsync above
    private void LogCounters()
    {
        var allCounters = countersStore
        .GroupBy(kv => kv.Key.Broker)
        .ToDictionary(kv => kv.Key, kv => kv.ToDictionary(kv1 => kv1.Key.Topic, kv1 => kv1.Value));

        var sb = new StringBuilder();
        foreach (var counters in allCounters)
        {
            sb.AppendLine($"Broker: {counters.Key}");

            foreach (var topicCounters in counters.Value)
            {
                sb.AppendLine($" Topic {topicCounters.Key}: {topicCounters.Value}");
            }
        }
        var countersText = sb.ToString();


        _logger.LogInformation(countersText);




    }
}