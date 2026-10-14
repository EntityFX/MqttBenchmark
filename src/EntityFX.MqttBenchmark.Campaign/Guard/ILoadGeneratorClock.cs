namespace EntityFX.MqttBenchmark.Campaign;

public interface ILoadGeneratorClock
{
    long Timestamp { get; }
    long Frequency { get; }
    DateTimeOffset UtcNow { get; }
    Task DelayUntilAsync(long deadline, CancellationToken cancellationToken);
    void WaitUntil(long deadline, CancellationToken cancellationToken) =>
        DelayUntilAsync(deadline, cancellationToken).GetAwaiter().GetResult();
}
