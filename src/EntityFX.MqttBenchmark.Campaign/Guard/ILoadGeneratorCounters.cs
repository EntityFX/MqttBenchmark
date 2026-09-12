namespace EntityFX.MqttBenchmark.Campaign;

public interface ILoadGeneratorCounters
{
    LoadGeneratorInterface Network { get; }
    LoadGeneratorCounters Read();
}
