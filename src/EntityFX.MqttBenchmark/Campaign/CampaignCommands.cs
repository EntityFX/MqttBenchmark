using EntityFX.MqttBenchmark.Calibration;
using System.Text.Json;

namespace EntityFX.MqttBenchmark.Campaign;

public static class CampaignCommands
{
    public static async Task<int> ExecuteAsync(string command, IReadOnlyDictionary<string, string?> options,
        CancellationToken cancellationToken = default, Func<Uri, ILoadGeneratorCounters>? loadCountersFactory = null)
    {
        var configPath = Required(options, "config");
        var configBytes = File.ReadAllBytes(configPath);
        var configHash = CampaignJson.HashBytes(configBytes);
        var config = JsonSerializer.Deserialize<CampaignDefinition>(configBytes, CampaignJson.Options)
            ?? throw new InvalidDataException("Missing campaign configuration.");
        config.Validate();
        var keys = config.Expand();
        if (options.TryGetValue("broker", out var broker))
        {
            if (!config.Brokers.Contains(broker, StringComparer.Ordinal)) throw new ArgumentException("Unknown campaign broker.");
            keys = keys.Where(x => x.Broker == broker).ToArray();
        }
        int? maxRuns = null;
        if (options.TryGetValue("max-runs", out var maxValue))
            maxRuns = int.TryParse(maxValue, out var max) && max > 0 ? max : throw new ArgumentException("--max-runs must be positive.");
        if (command == "aggregate")
        {
            var raw = Required(options, "raw");
            var identity = CampaignJson.Read<CampaignIdentity>(Path.Combine(raw, "campaign.json"));
            if (identity.ConfigSha256 != configHash) throw new InvalidDataException("Aggregation configuration hash differs from campaign input.");
            using var journal = CampaignJournal.Open(raw, identity, true);
            var result = CampaignAggregator.Aggregate(config, CampaignJournal.ReadAttempts(raw), CampaignJson.HashTree(raw));
            var output = Required(options, "output");
            CampaignJson.WriteNew(Path.Combine(output, "broker-observations.v3.json"), result);
            Console.WriteLine($"Aggregated {result.RunCount}/288 successful keys into {output}.");
            return 0;
        }
        if (command == "matrix" && options.ContainsKey("dry-run"))
        {
            Console.WriteLine($"Campaign v3: {keys.Length}/288 keys, seed {config.Seed}; selected run limit {maxRuns?.ToString() ?? "none"}.");
            return 0;
        }
        if (command is not ("matrix" or "preflight")) throw new ArgumentException("Unknown campaign command.");
        var standPath = Required(options, "stand");
        var repository = Value(options, "benchmark-repo", Directory.GetCurrentDirectory());
        var script = Value(options, "stand-script", Path.Combine(repository, "scripts", "BrokerStand.ps1"));
        var docker = Value(options, "docker-executable", "docker");
        var trustedBuildProvenance = options.TryGetValue("trusted-build-provenance", out var trustedValue) &&
            !string.IsNullOrWhiteSpace(trustedValue) ? trustedValue : null;
        var directory = Path.GetFullPath(Required(options, command == "matrix" ? "campaign" : "output"));
        var campaignIdentity = new CampaignIdentity(Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            configHash, CampaignJson.HashFile(standPath), config.DeploymentMode, config.CpuMode, GitRevisionReader.ReadHead(repository));
        var runner = new MqttCampaignRunner();
        if (command == "preflight")
        {
            // Reservation is never replaced, including failed/partial preflight executions.
            CampaignJson.WriteNew(Path.Combine(directory, "preflight-started.json"), campaignIdentity);
            var reports = new List<BrokerPreflight>();
            foreach (var name in keys.Select(x => x.Broker).Distinct())
            {
                cancellationToken.ThrowIfCancellationRequested();
                Console.WriteLine($"Preflight: {name}");
                try
                {
                    await using var stand = await StandSession.StartAsync(script, standPath, config, name,
                        Path.Combine(directory, name, "stand"), docker, cancellationToken,
                        expectedStandSha256: campaignIdentity.StandSha256,
                        trustedBuildProvenanceDirectory: trustedBuildProvenance);
                    var protocol = await runner.PreflightAsync(name, stand.Endpoint, cancellationToken);
                    CampaignJson.WriteNew(Path.Combine(directory, name, "protocol.json"), protocol);
                    var telemetry = await stand.CaptureAsync(1, cancellationToken);
                    reports.Add(new(protocol, telemetry));
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                {
                    var failure = new ProtocolPreflight(name, false, Array.Empty<ProtocolProbe>(), Array.Empty<double>(),
                        null, null, "unavailable", "Stand/preflight failure: " + error.Message);
                    reports.Add(new(failure, new(0, "container-cgroup", "host-shared", new Dictionary<string, string>())));
                }
            }
            var report = PreflightReport.Create(campaignIdentity, reports);
            CampaignJson.WriteNew(Path.Combine(directory, "preflight.json"), report);
            CampaignJson.WriteNew(Path.Combine(directory, "sha256.json"), CampaignJson.HashTree(directory));
            Console.WriteLine($"Preflight {(report.Success ? "passed" : "failed")}: {directory}");
            return report.Success ? 0 : 2;
        }

        using (var journal = CampaignJournal.Open(directory, campaignIdentity, options.ContainsKey("resume")))
        {
            await journal.ExecuteAsync(keys, async (key, attemptPath, number, ct) =>
            {
                Console.WriteLine($"{key.Key}, attempt {number}/3");
                await using var stand = await StandSession.StartAsync(script, standPath, config, key.Broker,
                    Path.Combine(attemptPath, "stand"), docker, ct,
                    expectedStandSha256: campaignIdentity.StandSha256,
                    trustedBuildProvenanceDirectory: trustedBuildProvenance);
                var protocol = await runner.PreflightAsync(key.Broker, stand.Endpoint, ct);
                CampaignJson.WriteNew(Path.Combine(attemptPath, "protocol.json"), protocol);
                if (!protocol.Success || protocol.RttBaselineMs == null) throw new IOException("Protocol preflight failed; see protocol.json.");
                // The single remote sampler covers warmup, measurement, bounded in-flight completion,
                // drain and cooldown. Awaiting it also prevents capture writes after an attempt is sealed.
                var captureSeconds = (int)Math.Ceiling(config.WarmupSeconds + config.MeasurementSeconds +
                    config.CooldownSeconds + config.DrainTimeoutSeconds + 20);
                RunObservation? observation = null;
                var telemetry = await stand.RunWithTelemetryAsync(captureSeconds, async measurementToken =>
                {
                    await using var loadGuard = LoadGeneratorGuard.Start(stand.Endpoint, attemptPath, loadCountersFactory,
                        cancellationToken: measurementToken,
                        networkThresholdPercent: config.NetworkThresholdPercentFor(key.Broker),
                        cpuThresholdPercent: config.CpuThresholdPercent ?? LoadGeneratorAssessment.CpuThresholdPercent);
                    observation = await runner.RunAsync(config, key, stand.Endpoint, campaignIdentity.CampaignId,
                        key.Key + $".attempt-{number:00}", attemptPath, protocol.RttBaselineMs.Value, measurementToken, loadGuard);
                    await loadGuard.CompleteAsync();
                }, ct);
                var report = PreflightReport.Create(campaignIdentity, new[] { new BrokerPreflight(protocol, telemetry) });
                CampaignJson.WriteNew(Path.Combine(attemptPath, "preflight.json"), report);
                return observation!;
            }, maxRuns, cancellationToken);
        }
        Console.WriteLine($"Campaign invocation finished: {directory}. Aggregate enforces full 288/288 coverage.");
        return 0;
    }

    private static string Required(IReadOnlyDictionary<string, string?> options, string key) =>
        options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException($"Missing --{key}.");
    private static string Value(IReadOnlyDictionary<string, string?> options, string key, string fallback) =>
        options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;
}
