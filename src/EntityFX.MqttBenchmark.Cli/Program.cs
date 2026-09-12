using EntityFX.MqttBenchmark.Calibration;
using EntityFX.MqttBenchmark.Campaign;
using System.Text.Json;

return await Cli.RunAsync(args);

internal static class Cli
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            PrintHelp();
            return args.Length == 0 ? 1 : 0;
        }

        try
        {
            var options = Parse(args.Skip(1).ToArray());
            if (args[0] == "preflight" || (options.TryGetValue("config", out var configPath) &&
                !string.IsNullOrWhiteSpace(configPath) && IsV3(configPath)))
            {
                using var cancellation = new CancellationTokenSource();
                Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
                return await CampaignCommands.ExecuteAsync(args[0], options, cancellation.Token);
            }
            return args[0] switch
            {
                "matrix" => await RunMatrixAsync(options),
                "aggregate" => RunAggregate(options),
                _ => throw new ArgumentException($"Unknown command '{args[0]}'.")
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }

    private static async Task<int> RunMatrixAsync(IReadOnlyDictionary<string, string?> options)
    {
        var config = Required(options, "config");
        var output = Required(options, "output");
        var matrix = BenchmarkMatrixStore.Read(config);
        var runs = matrix.Expand();
        if (options.TryGetValue("broker", out var broker) && !string.IsNullOrWhiteSpace(broker))
            runs = runs.Where(run => string.Equals(run.Broker.Name, broker, StringComparison.Ordinal));
        if (options.TryGetValue("max-runs", out var maxRunsValue) &&
            int.TryParse(maxRunsValue, out var maxRuns) && maxRuns > 0)
            runs = runs.Take(maxRuns);
        var expanded = runs.ToArray();
        Console.WriteLine($"Expanded runs: {expanded.Length}");
        if (options.ContainsKey("dry-run")) return 0;

        var campaign = options.TryGetValue("campaign", out var campaignValue) &&
            !string.IsNullOrWhiteSpace(campaignValue)
                ? campaignValue
                : $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}";
        var rawDirectory = Path.Combine(output, campaign!);
        Directory.CreateDirectory(rawDirectory);
        var runner = new MqttMatrixRunner();
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        for (var index = 0; index < expanded.Length; index++)
        {
            var spec = expanded[index];
            Console.WriteLine($"[{index + 1}/{expanded.Length}] {spec.Broker.Name} " +
                $"m={spec.MessageBytes} q={spec.Qos} c={spec.Clients} r={spec.Repeat}");
            var result = await runner.RunAsync(spec, cancellation.Token);
            var path = RawArtifactStore.WriteUnique(rawDirectory, result);
            Console.WriteLine($"  raw: {path}");
        }
        return 0;
    }

    private static int RunAggregate(IReadOnlyDictionary<string, string?> options)
    {
        var config = Required(options, "config");
        var raw = Required(options, "raw");
        var output = Required(options, "output");
        var mqttYRepository = Required(options, "mqtt-y-repo");
        var benchmarkRepository = options.TryGetValue("benchmark-repo", out var benchmarkPath) &&
            !string.IsNullOrWhiteSpace(benchmarkPath) ? benchmarkPath! : Directory.GetCurrentDirectory();

        var matrix = BenchmarkMatrixStore.Read(config);
        var rows = RawArtifactStore.ReadTree(raw);
        var aggregates = BenchmarkAggregator.Aggregate(rows, matrix.Repeats);
        BenchmarkAggregator.ValidateCoverage(aggregates, matrix);
        Directory.CreateDirectory(output);
        AggregatedArtifactStore.WriteCsvNew(Path.Combine(output, "aggregated.csv"), aggregates);
        AggregatedArtifactStore.WriteJsonNew(Path.Combine(output, "aggregated.json"), aggregates);
        var provenance = new ProfileProvenance(
            GitRevisionReader.ReadHead(mqttYRepository),
            GitRevisionReader.ReadHead(benchmarkRepository),
            RawArtifactStore.HashTree(raw), DateTimeOffset.UtcNow,
            matrix.WarmupSeconds, matrix.MeasurementSeconds, matrix.Repeats);
        var profile = BrokerProfileGenerator.Generate(aggregates, provenance);
        BrokerProfileGenerator.WriteNew(Path.Combine(output, "broker-benchmark.v2.json"), profile);
        Console.WriteLine($"Aggregated {rows.Count} immutable raw runs into {output}.");
        return 0;
    }

    private static Dictionary<string, string?> Parse(string[] args)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Expected an option, got '{argument}'.");
            var key = argument[2..];
            string? value = null;
            if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
                value = args[++index];
            if (!result.TryAdd(key, value)) throw new ArgumentException($"Duplicate option '--{key}'.");
        }
        return result;
    }

    private static string Required(IReadOnlyDictionary<string, string?> options, string name) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Required option '--{name}' is missing.");

    private static void PrintHelp()
    {
        Console.WriteLine("MqttBenchmark reproducible calibration pipeline");
        Console.WriteLine("  preflight --stand <broker-stand.v1.json> --config <benchmark-campaign.v3.json> --output <new-dir> [--broker <name>]");
        Console.WriteLine("  matrix --stand <json> --config <v3-json> --campaign <directory> [--resume] [--broker <name>] [--max-runs <n>] [--max-attempts <n>] [--dry-run]");
        Console.WriteLine("  aggregate --config <v3-json> --raw <campaign-dir> --output <new-dir>");
        Console.WriteLine("  v3 stand operations also accept --docker-executable <path>, --benchmark-repo <dir>, --stand-script <path>, --trusted-build-provenance <dir>.");
        Console.WriteLine("Legacy v2:");
        Console.WriteLine("  matrix --config <json> --output <raw-root> [--campaign <id>] [--broker <name>] [--max-runs <n>] [--dry-run]");
        Console.WriteLine("  aggregate --config <json> --raw <campaign-dir> --output <dir> --mqtt-y-repo <dir> [--benchmark-repo <dir>]");
    }

    private static bool IsV3(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        return document.RootElement.TryGetProperty("schemaVersion", out var version) &&
            version.ValueKind == JsonValueKind.Number && version.GetInt32() == 3;
    }
}
