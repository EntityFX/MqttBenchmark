namespace EntityFX.MqttBenchmark.Bomber.Settings
{
    internal class Settings
    {
        public Dictionary<string, Dictionary<string, object>> TestParams { get; set; } = new();

        public Dictionary<string, ScenarioNameTemplate> ScenarioNameTemplates { get; set; } = new();

        public Dictionary<string, string> TemplateFiles { get; set; } = new();

        public bool InParallel { get; set; } = true;

        public string Name { get; set; } = string.Empty;
    }
}
