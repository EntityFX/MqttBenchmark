using System.Diagnostics;

namespace EntityFX.MqttBenchmark.Campaign;

public sealed class LoadGeneratorClock : ILoadGeneratorClock
{
    public long Timestamp => Stopwatch.GetTimestamp();
    public long Frequency => Stopwatch.Frequency;
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public async Task DelayUntilAsync(long deadline, CancellationToken cancellationToken)
    {
        while (Timestamp < deadline)
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, (deadline - Timestamp) / (double)Frequency)), cancellationToken);
    }
}
