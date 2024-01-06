namespace EntityFX.MqttBenchmark;

class TestSettings
{
    public Settings Settings { get; set; } = new ();

    public Dictionary<string, Dictionary<string, Settings>> Tests { get; set; } = new ();

    public bool InParallel { get; set; } = true;

    public string OutputPath { get; set; } = "results";
}