using System.Diagnostics;
using System.Text.Json.Nodes;

namespace EntityFX.MqttBenchmark.Campaign;

/// <summary>
/// Фасад сессии стенда: сохраняет прежний публичный API и сигнатуры, но делегирует
/// жизненный цикл контроллера — <see cref="StandLifecycle"/>, а телеметрию — <see cref="StandTelemetry"/>.
/// </summary>
public sealed class StandSession : IAsyncDisposable
{
    private readonly StandLifecycle lifecycle;
    private readonly StandTelemetry telemetry;
    private readonly string output;
    private readonly TimeSpan telemetryReadinessTimeout;
    public Uri Endpoint { get; }

    private StandSession(StandLifecycle lifecycle, StandTelemetry telemetry, string output, Uri endpoint,
        TimeSpan telemetryReadinessTimeout)
    {
        this.lifecycle = lifecycle;
        this.telemetry = telemetry;
        this.output = output;
        Endpoint = endpoint;
        this.telemetryReadinessTimeout = telemetryReadinessTimeout;
    }

    /// <summary>
    /// Разворачивает стенд брокера: проверяет инвентарь (и его SHA-256 против ожидаемого),
    /// подготавливает source/selected контексты, валидирует образ (для custom-образов — build-запись),
    /// запускает контроллер (<c>start</c>), проверяет здоровье и синхронизацию часов.
    /// Сессия удерживает аренду Docker-контекстов до <see cref="DisposeAsync"/>.
    /// </summary>
    /// <param name="script">Путь к управляющему скрипту стенда.</param>
    /// <param name="inventoryPath">Путь к инвентарю стенда (<c>broker-stand.local.json</c>).</param>
    /// <param name="config">Валидированная конфигурация кампании (режимы развёртывания/CPU, custom-образы).</param>
    /// <param name="broker">Имя брокера из инвентаря.</param>
    /// <param name="output">Каталог артефактов стенда (создаётся при необходимости).</param>
    /// <param name="dockerExecutable">Исполняемый файл Docker.</param>
    /// <param name="cancellationToken">Токен отмены; при отмене сессия останавливается и освобождает аренды.</param>
    /// <param name="expectedStandSha256">Ожидаемый SHA-256 исходных байт инвентаря; несовпадение —
    /// <see cref="InvalidDataException"/> (входы изменились между прогонами).</param>
    /// <param name="trustedBuildProvenanceDirectory">Каталог с доверенной build-записью для custom-образов;
    /// <c>null</c> — не проверять.</param>
    /// <returns>Активная сессия: <see cref="Endpoint"/> — MQTT-адрес выбранного брокера.</returns>
    /// <exception cref="InvalidDataException">Инвентарь не парсится, SHA-256 не совпал, либо брокер отсутствует.</exception>
    /// <exception cref="IOException">Контроллер стенда вернул ошибку на любом этапе (pull/validate/start/health/clock).</exception>
    /// <exception cref="OperationCanceledException">Отмена через <paramref name="cancellationToken"/>.</exception>
    public static async Task<StandSession> StartAsync(string script, string inventoryPath, CampaignDefinition config,
        string broker, string output, string dockerExecutable = "docker", CancellationToken cancellationToken = default,
        string? expectedStandSha256 = null, string? trustedBuildProvenanceDirectory = null)
    {
        config.Validate();
        var inputBytes = File.ReadAllBytes(inventoryPath);
        if (expectedStandSha256 != null && CampaignJson.HashBytes(inputBytes) != expectedStandSha256)
            throw new InvalidDataException("Stand input bytes do not match the campaign identity.");
        // Parse and later persist exactly the buffer whose identity was verified. A source-file change
        // after this read cannot change this deployment, and the next attempt must verify again.
        var inventory = JsonNode.Parse(inputBytes) ?? throw new InvalidDataException("Missing stand inventory.");
        var brokers = inventory["brokers"]!.AsArray();
        var selected = brokers.Single(x => x!["name"]!.GetValue<string>() == broker)!;
        output = Path.GetFullPath(output);
        var selectedPath = Path.Combine(output, "stand.selected.json");
        var trusted = string.IsNullOrWhiteSpace(trustedBuildProvenanceDirectory)
            ? null : Path.GetFullPath(trustedBuildProvenanceDirectory);
        var runtime = selected["runtime"]?.GetValue<string>();
        var lifecycle = new StandLifecycle(Path.GetFullPath(script), selectedPath, output, dockerExecutable, trusted);
        var session = new StandSession(lifecycle, new StandTelemetry(lifecycle, selectedPath, output, StandTelemetry.CpuScopeForRuntime(runtime)), output,
            new Uri(selected["mqttUri"]!.GetValue<string>()),
            TimeSpan.FromSeconds(config.TelemetryReadinessTimeoutSeconds));
        try
        {
            // A context lease spans preflight, measurement, capture and stop, also across different campaigns.
            lifecycle.LeaseContext(StandLifecycle.ControllerContext(inventory, selected));
            var sourcePath = Path.Combine(output, "inventory-check", "stand.source.json");
            CampaignJson.WriteBytesNew(sourcePath, inputBytes);
            // Custom-image brokers (from the campaign configuration) need the controller's build record
            // before validate. Registry images can be validated and started at their immutable digest
            // from cache, without contacting the registry.
            var activeBroker = inventory["activeBroker"]?.GetValue<string>();
            if (activeBroker != null && config.CustomImageBrokers.Contains(activeBroker, StringComparer.Ordinal))
            {
                // Source custom-image validation also builds/inspects that source's active context.
                // Lease this used context too; unrelated broker entries never influence the leases.
                var sourceActive = brokers.Single(x => x!["name"]!.GetValue<string>() == inventory["activeBroker"]!.GetValue<string>())!;
                lifecycle.LeaseContext(StandLifecycle.ControllerContext(inventory, sourceActive));
                await lifecycle.InvokeAsync("pull", sourcePath, Path.GetDirectoryName(sourcePath)!, 1, cancellationToken);
            }
            await lifecycle.InvokeAsync("validate", sourcePath, Path.GetDirectoryName(sourcePath)!, 1, cancellationToken);
            inventory["campaign"] = new JsonObject { ["deploymentMode"] = config.DeploymentMode, ["cpuMode"] = config.CpuMode };
            inventory["activeBroker"] = broker;
            foreach (var item in brokers) item!["loaded"] = item["name"]!.GetValue<string>() == broker;
            CampaignJson.WriteNew(selectedPath, inventory);
            if (config.CustomImageBrokers.Contains(broker, StringComparer.Ordinal))
                await lifecycle.InvokeAsync("pull", selectedPath, output, 1, cancellationToken);
            await lifecycle.InvokeAsync("validate", selectedPath, output, 1, cancellationToken);
            lifecycle.MarkStarted();
            await lifecycle.InvokeAsync("start", selectedPath, output, 1, cancellationToken);
            await lifecycle.InvokeAsync("health", selectedPath, output, 1, cancellationToken);
            await lifecycle.InvokeAsync("clock", selectedPath, output, 1, cancellationToken);
            return session;
        }
        catch
        {
            try { await session.DisposeAsync(); } catch { /* Original start/validation failure remains primary. */ }
            throw;
        }
    }

    /// <summary>
    /// Выполняет измерение под активным удалённым самплером телеметрии: ждёт готовности
    /// (<see cref="TelemetryReadinessTimeoutSeconds"/>), запускает <paramref name="measurement"/>,
    /// после завершения всех фаз workload ставит fence на удалённые монотонные часы и возвращает
    /// манифест с гарантией, что телеметрия покрывает завершение работы.
    /// </summary>
    /// <param name="seconds">Длительность захвата сэмплов (должна покрывать warmup + измерение + drain + cooldown + резерв).</param>
    /// <param name="measurement">Измеряемая работа (запуск load-guard, warmup, измерение, drain).</param>
    /// <param name="cancellationToken">Токен отмены; прерывает ожидание готовности и само измерение.</param>
    /// <returns>Манифест телеметрии после успешной проверки fence-покрытия.</returns>
    /// <exception cref="TimeoutException">Телеметрия не стала готовой в течение настроенного таймаута.</exception>
    /// <exception cref="InvalidDataException">Самплер завершился до готовности/измерения либо fence-покрытие
    /// удалённых часов не подтверждено.</exception>
    /// <exception cref="OperationCanceledException">Отмена через <paramref name="cancellationToken"/>.</exception>
    public async Task<TelemetryManifest> RunWithTelemetryAsync(int seconds, Func<CancellationToken, Task> measurement,
        CancellationToken cancellationToken = default)
    {
        using var captureCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var capture = telemetry.CaptureSamplesAsync(seconds, captureCancellation.Token);
        try
        {
            var startup = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(output, "telemetry-ready.json")))
            {
                if (capture.IsCompleted) { await capture; throw new InvalidDataException("Telemetry ended before measurement readiness."); }
                if (startup.Elapsed > telemetryReadinessTimeout) throw new TimeoutException(
                    $"Telemetry did not become ready within {telemetryReadinessTimeout.TotalSeconds:0} seconds.");
                await Task.Delay(25, cancellationToken);
            }
            if (capture.IsCompleted) { await capture; throw new InvalidDataException("Telemetry ended before measurement started."); }
            telemetry.RequireActiveSampler();
            cancellationToken.ThrowIfCancellationRequested();
            CampaignJson.WriteNew(Path.Combine(output, "measurement-started.json"), new { startedAtUtc = DateTimeOffset.UtcNow });
            await measurement(cancellationToken);
            telemetry.RequireActiveSampler();
            // Read the same remote monotonic clock only after all workload phases have finished.
            // A later sample proves end coverage even if the final sampler output was delayed in transit.
            await lifecycle.InvokeAsync("fence", GetSelectedPath(), output, 1, cancellationToken);
            var sampleCount = await capture;
            var fence = JsonNode.Parse(File.ReadAllText(Path.Combine(output, "workload-end-fence.json")))!;
            var end = JsonNode.Parse(File.ReadAllText(Path.Combine(output, "sampler-ended.json")))!;
            var ready = JsonNode.Parse(File.ReadAllText(Path.Combine(output, "telemetry-ready.json")))!;
            var endTime = end["lastSampleMonotonicSeconds"]!.GetValue<double>();
            var fenceTime = fence["monotonicSeconds"]!.GetValue<double>();
            if (end["unitId"] == null || fence["unitId"] == null || ready["unitId"] == null ||
                end["unitId"]!.GetValue<string>() != fence["unitId"]!.GetValue<string>() ||
                ready["unitId"]!.GetValue<string>() != fence["unitId"]!.GetValue<string>())
                throw new InvalidDataException("Sampler, workload fence and telemetry ready records do not belong to one broker unit.");
            if (!double.IsFinite(endTime) || !double.IsFinite(fenceTime) || endTime <= fenceTime)
                throw new InvalidDataException("Telemetry does not cover the remote workload completion fence.");
            // Fence and capture writers are both finished before hashing their immutable evidence.
            return telemetry.WriteTelemetryManifest(sampleCount);
        }
        catch
        {
            captureCancellation.Cancel();
            try { await capture; } catch { /* Preserve the readiness/measurement failure. */ }
            throw;
        }
    }

    /// <summary>Захватывает телеметрию стенда без измерения (используется в preflight).</summary>
    /// <param name="seconds">Длительность захвата в секундах.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Манифест телеметрии.</returns>
    /// <exception cref="OperationCanceledException">Отмена через <paramref name="cancellationToken"/>.</exception>
    public async Task<TelemetryManifest> CaptureAsync(int seconds, CancellationToken cancellationToken = default) =>
        await telemetry.CaptureAsync(seconds, cancellationToken);

    private string GetSelectedPath() => Path.Combine(output, "stand.selected.json");

    /// <summary>
    /// Останавливает стенд (<c>stop</c>, максимум 30 секунд), если он был запущен, и освобождает
    /// аренды Docker-контекстов. Повторный вызов безопасен: флаг запуска сбрасывается до вызова stop.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (lifecycle.Started)
            {
                // Сброс до вызова stop защищает от повторной остановки, даже если сам stop упадёт.
                lifecycle.MarkStopped();
                using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await lifecycle.InvokeAsync("stop", GetSelectedPath(), output, 1, cancel.Token);
            }
        }
        finally { lifecycle.Release(); }
    }
}