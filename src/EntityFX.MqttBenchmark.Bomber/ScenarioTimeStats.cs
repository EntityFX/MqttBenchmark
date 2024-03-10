namespace EntityFX.MqttBenchmark.Bomber;

public class ScenarioTimeStats
{
    public DateTimeOffset BuildTime { get; set; }

    public DateTimeOffset InitStartTime { get; set; }
    public DateTimeOffset InitEndTime { get; set; }

    public DateTimeOffset CleanStartTime { get; set; }
    public DateTimeOffset CleanEndTime { get; set; }
}
