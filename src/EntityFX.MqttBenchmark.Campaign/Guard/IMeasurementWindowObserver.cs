namespace EntityFX.MqttBenchmark.Campaign;

public interface IMeasurementWindowObserver
{
    void MeasurementStarted(long tick, DateTimeOffset utc);
    void MeasurementEnded(long tick, DateTimeOffset utc);
}
