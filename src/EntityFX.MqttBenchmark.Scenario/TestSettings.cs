namespace EntityFX.MqttBenchmark.Scenario
{

    public class TestSettings
    {
        public Settings Settings { get; set; } = new();

        public Dictionary<string, Dictionary<string, Settings>> Scenarios { get; set; } = new();

        public bool InParallel { get; set; } = true;

        public string OutputPath { get; set; } = "results";

        public string Name { get; set; } = string.Empty;
    }
}