using EntityFX.MqttBenchmark.Calibration;
using System.Text.Json;

namespace EntityFX.MqttBenchmark.Campaign;

/// <summary>
/// Оркестратор CLI-команд кампании (aggregate / matrix / preflight). Координирует выделенные
/// сервисы — разбор опций (<see cref="CampaignCommandOptions"/>), идентичность
/// (<see cref="CampaignIdentityFactory"/>), preflight-отчёт (<see cref="PreflightReportBuilder"/>),
/// стенд (<see cref="StandSession"/>) и раннер (<see cref="IMqttCampaignRunner"/>). Зависимости
/// внедряются через фабрики.
/// </summary>
public static class CampaignCommands
{
    /// <summary>
    /// Точка входа CLI-команд кампании. Три команды:
    /// <list type="bullet">
    /// <item><description><c>preflight</c> — разворачивает каждый брокер, выполняет протокольную пробу
    /// (RTT-эхо и <c>$SYS/broker/version</c>) и снимает телеметрию без нагрузки;
    /// результат — <c>preflight.json</c>.</description></item>
    /// <item><description><c>matrix</c> — полный прогон: для каждого ключа матрицы поднимается стенд,
    /// выполняется preflight, затем измерение под нагрузкой с телеметрией и load-guard;
    /// попытки записываются в журнал кампании.</description></item>
    /// <item><description><c>aggregate</c> — сводит успешные попытки в
    /// <c>broker-observations.v3.json</c>; требует полного покрытия матрицы.</description></item>
    /// </list>
    /// </summary>
    /// <param name="command">Имя команды: <c>aggregate</c>, <c>matrix</c> или <c>preflight</c>.</param>
    /// <param name="options">Опции командной строки (см. <see cref="CampaignCommandOptions.Parse"/>).</param>
    /// <param name="cancellationToken">Токен отмены; прерывает текущую попытку и останавливает стенд.</param>
    /// <param name="loadCountersFactory">Фабрика счётчиков нагрузки генератора; по умолчанию
    /// <see cref="WindowsLoadGeneratorCounters"/>.</param>
    /// <param name="runnerFactory">Фабрика MQTT-раннера; по умолчанию <see cref="DefaultMqttCampaignRunnerFactory"/>.</param>
    /// <param name="standFactory">Фабрика сессии стенда; по умолчанию <see cref="DefaultStandSessionFactory"/>.</param>
    /// <param name="guardFactory">Фабрика load-guard; по умолчанию <see cref="DefaultLoadGeneratorGuardFactory"/>.</param>
    /// <returns>0 при успехе (включая успешный preflight); 2 при неуспешном preflight
    /// (<c>preflight.json</c> с <c>Success = false</c>).</returns>
    /// <exception cref="ArgumentException">Неизвестная команда, неизвестный брокер (<c>--broker</c>),
    /// либо отсутствуют обязательные опции (<c>--raw</c>/<c>--output</c> для aggregate).</exception>
    /// <exception cref="InvalidDataException">Конфигурация не прошла <see cref="CampaignDefinition.Validate"/>,
    /// либо хеш конфига не совпал с идентичностью журнала/кампании (входы изменились между прогонами).</exception>
    /// <exception cref="CampaignExhaustedException">Ключ исчерпал лимит попыток (<see cref="CampaignJournal.ExecuteAsync"/>).</exception>
    public static async Task<int> ExecuteAsync(string command, IReadOnlyDictionary<string, string?> options,
        CancellationToken cancellationToken = default, Func<Uri, ILoadGeneratorCounters>? loadCountersFactory = null,
        IMqttCampaignRunnerFactory? runnerFactory = null, IStandSessionFactory? standFactory = null,
        ILoadGeneratorGuardFactory? guardFactory = null)
    {
        var parsed = CampaignCommandOptions.Parse(command, options);
        var configBytes = File.ReadAllBytes(parsed.ConfigPath);
        var configHash = CampaignJson.HashBytes(configBytes);
        var config = JsonSerializer.Deserialize<CampaignDefinition>(configBytes, CampaignJson.Options)
            ?? throw new InvalidDataException("Missing campaign configuration.");
        config.Validate();
        var keys = config.Expand();
        if (parsed.Broker != null)
        {
            if (!config.Brokers.Contains(parsed.Broker, StringComparer.Ordinal)) throw new ArgumentException("Unknown campaign broker.");
            keys = keys.Where(x => x.Broker == parsed.Broker).ToArray();
        }
        runnerFactory ??= new DefaultMqttCampaignRunnerFactory();
        standFactory ??= new DefaultStandSessionFactory();
        guardFactory ??= new DefaultLoadGeneratorGuardFactory();
        if (command == "aggregate")
        {
            if (parsed.RawDirectory == null) throw new ArgumentException("Missing --raw.");
            if (parsed.OutputDirectory == null) throw new ArgumentException("Missing --output.");
            var identity = CampaignJson.Read<CampaignIdentity>(Path.Combine(parsed.RawDirectory, "campaign.json"));
            if (identity.ConfigSha256 != configHash) throw new InvalidDataException("Aggregation configuration hash differs from campaign input.");
            using var journal = CampaignJournal.Open(parsed.RawDirectory, identity, true);
            var result = CampaignAggregator.Aggregate(config, CampaignJournal.ReadAttempts(parsed.RawDirectory), CampaignJson.HashTree(parsed.RawDirectory));
            CampaignJson.WriteNew(Path.Combine(parsed.OutputDirectory, "broker-observations.v3.json"), result);
            Console.WriteLine($"Aggregated {result.RunCount}/{config.Expand().Length} successful keys into {parsed.OutputDirectory}.");
            return 0;
        }
        if (command == "matrix" && options.ContainsKey("dry-run"))
        {
            Console.WriteLine($"Campaign v3: {keys.Length}/{config.Expand().Length} keys, seed {config.Seed}; selected run limit {parsed.MaxRuns?.ToString() ?? "none"}.");
            return 0;
        }
        if (command is not ("matrix" or "preflight")) throw new ArgumentException("Unknown campaign command.");
        var campaignIdentity = CampaignIdentityFactory.Create(parsed.Directory, configHash,
            CampaignJson.HashFile(parsed.StandPath), config, parsed.BenchmarkRepository);
        // Transport timeout comes from the campaign configuration (seconds); the factory keeps
        // its optional constructor parameters for direct/test callers.
        var transportTimeout = TimeSpan.FromSeconds(config.TransportTimeoutSeconds);
        var runner = runnerFactory.Create(transportTimeout, TimeSpan.FromSeconds(config.PreflightSysWaitSeconds));
        if (command == "preflight")
        {
            // Reservation is never replaced, including failed/partial preflight executions.
            CampaignJson.WriteNew(Path.Combine(parsed.Directory, "preflight-started.json"), campaignIdentity);
            var reports = new List<BrokerPreflight>();
            var standInventory = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(parsed.StandPath))!;
            foreach (var name in keys.Select(x => x.Broker).Distinct())
            {
                cancellationToken.ThrowIfCancellationRequested();
                Console.WriteLine($"Preflight: {name}");
                try
                {
                    await using var stand = await standFactory.StartAsync(parsed.StandScript, parsed.StandPath, config, name,
                        Path.Combine(parsed.Directory, name, "stand"), parsed.DockerExecutable, cancellationToken,
                        expectedStandSha256: campaignIdentity.StandSha256,
                        trustedBuildProvenanceDirectory: parsed.TrustedBuildProvenanceDirectory);
                    var protocol = await runner.PreflightAsync(name, stand.Endpoint, config.PreflightRttProbes, cancellationToken);
                    CampaignJson.WriteNew(Path.Combine(parsed.Directory, name, "protocol.json"), protocol);
                    var telemetry = await stand.CaptureAsync(1, cancellationToken);
                    reports.Add(new(protocol, telemetry));
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                {
                    var failure = new ProtocolPreflight(name, false, Array.Empty<ProtocolProbe>(), Array.Empty<double>(),
                        null, null, "unavailable", "Stand/preflight failure: " + error.Message);
                    var brokerNode = standInventory["brokers"]!.AsArray().Single(x => x!["name"]!.GetValue<string>() == name)!;
                    reports.Add(new(failure, new TelemetryManifest(0,
                        StandTelemetry.CpuScopeForRuntime(brokerNode["runtime"]?.GetValue<string>()),
                        StandTelemetry.NetworkScope, new Dictionary<string, string>())));
                }
            }
            var report = PreflightReportBuilder.Build(campaignIdentity, reports);
            CampaignJson.WriteNew(Path.Combine(parsed.Directory, "preflight.json"), report);
            CampaignJson.WriteNew(Path.Combine(parsed.Directory, "sha256.json"), CampaignJson.HashTree(parsed.Directory));
            Console.WriteLine($"Preflight {(report.Success ? "passed" : "failed")}: {parsed.Directory}");
            return report.Success ? 0 : 2;
        }

        // CLI --max-attempts overrides the campaign configuration value.
        var maxAttempts = parsed.MaxAttempts ?? config.MaxAttempts;
        using (var journal = CampaignJournal.Open(parsed.Directory, campaignIdentity, options.ContainsKey("resume")))
        {
            await journal.ExecuteAsync(keys, async (key, attemptPath, number, ct) =>
            {
                Console.WriteLine($"{key.Key}, attempt {number}/{maxAttempts}");
                await using var stand = await standFactory.StartAsync(parsed.StandScript, parsed.StandPath, config, key.Broker,
                    Path.Combine(attemptPath, "stand"), parsed.DockerExecutable, ct,
                    expectedStandSha256: campaignIdentity.StandSha256,
                    trustedBuildProvenanceDirectory: parsed.TrustedBuildProvenanceDirectory);
                var protocol = await runner.PreflightAsync(key.Broker, stand.Endpoint, config.PreflightRttProbes, ct);
                CampaignJson.WriteNew(Path.Combine(attemptPath, "protocol.json"), protocol);
                if (!protocol.Success || protocol.RttBaselineMs == null) throw new IOException("Protocol preflight failed; see protocol.json.");
                // The single remote sampler covers warmup, measurement, bounded in-flight completion,
                // drain and cooldown. The configured reserve covers controller/writer latency; awaiting
                // the capture also prevents writes after an attempt is sealed.
                var captureSeconds = (int)Math.Ceiling(config.WarmupSeconds + config.MeasurementSeconds +
                    config.CooldownSeconds + config.DrainTimeoutSeconds + config.TelemetryCaptureReserveSeconds);
                RunObservation? observation = null;
                var telemetry = await stand.RunWithTelemetryAsync(captureSeconds, async measurementToken =>
                {
                    await using var loadGuard = guardFactory.Create(stand.Endpoint, attemptPath,
                        loadCountersFactory ?? LoadGeneratorGuard.CreateDefaultCounters,
                        cancellationToken: measurementToken,
                        networkThresholdPercent: config.NetworkThresholdPercentFor(key.Broker),
                        cpuThresholdPercent: config.CpuThresholdPercent ?? LoadGeneratorAssessment.CpuThresholdPercent);
                    observation = await runner.RunAsync(config, key, stand.Endpoint, campaignIdentity.CampaignId,
                        key.Key + $".attempt-{number:00}", attemptPath, protocol.RttBaselineMs.Value, measurementToken, loadGuard);
                    await loadGuard.CompleteAsync();
                }, ct);
                var report = PreflightReportBuilder.Build(campaignIdentity, new[] { new BrokerPreflight(protocol, telemetry) });
                CampaignJson.WriteNew(Path.Combine(attemptPath, "preflight.json"), report);
                return observation!;
            }, parsed.MaxRuns, cancellationToken, maxAttempts);
        }
        var expectedKeyCount = config.Expand().Length;
        Console.WriteLine($"Campaign invocation finished: {parsed.Directory}. Aggregate enforces full {expectedKeyCount}/{expectedKeyCount} coverage.");
        return 0;
    }
}