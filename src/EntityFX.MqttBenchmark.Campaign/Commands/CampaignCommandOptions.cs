namespace EntityFX.MqttBenchmark.Campaign;

/// <summary>
/// Разбор и валидация CLI-опций кампании (<c>--config</c>, <c>--stand</c>, <c>--max-runs</c>,
/// <c>--max-attempts</c> и т.д.) в типизированный неизменяемый объект.
/// Командной логики здесь нет.
/// </summary>
public sealed record CampaignCommandOptions(
    string ConfigPath,
    string? Broker,
    int? MaxRuns,
    int? MaxAttempts,
    string StandPath,
    string BenchmarkRepository,
    string StandScript,
    string DockerExecutable,
    string? TrustedBuildProvenanceDirectory,
    string Directory,
    string? RawDirectory,
    string? OutputDirectory,
    double? MaximumGuardInterval)
{
    public static CampaignCommandOptions Parse(string command, IReadOnlyDictionary<string, string?> options)
    {
        var configPath = Required(options, "config");
        string? broker = null;
        if (options.TryGetValue("broker", out var brokerValue)) broker = brokerValue;
        int? maxRuns = null;
        if (options.TryGetValue("max-runs", out var maxValue))
            maxRuns = int.TryParse(maxValue, out var max) && max > 0 ? max : throw new ArgumentException("--max-runs must be positive.");
        // Null means "use the campaign configuration"; a CLI value overrides it.
        int? maxAttempts = null;
        if (options.TryGetValue("max-attempts", out var attemptsValue) && !string.IsNullOrWhiteSpace(attemptsValue))
            maxAttempts = int.TryParse(attemptsValue, out var attempts) && attempts >= 1
                ? attempts : throw new ArgumentException("--max-attempts must be a positive integer.");
        // Null means "use the campaign configuration"; a CLI value overrides it. The override
        // keeps the config bytes (and therefore the journal identity hash) unchanged, so a
        // resumed campaign can be re-run with a different guard interval.
        double? maximumGuardInterval = null;
        if (options.TryGetValue("maximum-guard-interval", out var intervalValue) && !string.IsNullOrWhiteSpace(intervalValue))
            maximumGuardInterval = double.TryParse(intervalValue, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var interval) && interval > 0 && double.IsFinite(interval)
                ? interval : throw new ArgumentException("--maximum-guard-interval must be a positive number of seconds.");
        var directory = Path.GetFullPath(Required(options, command == "matrix" ? "campaign" : "output"));
        return new(configPath, broker, maxRuns, maxAttempts,
            Required(options, "stand"),
            Value(options, "benchmark-repo", System.IO.Directory.GetCurrentDirectory()),
            Value(options, "stand-script", Path.Combine(Value(options, "benchmark-repo", System.IO.Directory.GetCurrentDirectory()), "scripts", "BrokerStand.ps1")),
            Value(options, "docker-executable", "docker"),
            options.TryGetValue("trusted-build-provenance", out var trustedValue) && !string.IsNullOrWhiteSpace(trustedValue) ? trustedValue : null,
            directory,
            options.TryGetValue("raw", out var rawValue) ? rawValue : null,
            options.TryGetValue("output", out var outputValue) ? outputValue : null,
            maximumGuardInterval);
    }

    private static string Required(IReadOnlyDictionary<string, string?> options, string key) =>
        options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException($"Missing --{key}.");

    private static string Value(IReadOnlyDictionary<string, string?> options, string key, string fallback) =>
        options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;
}