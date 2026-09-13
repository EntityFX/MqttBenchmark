using System.Text;
using System.Text.Json;

namespace EntityFX.MqttBenchmark.Campaign;

public sealed class LoadGeneratorGuard : IAsyncDisposable, IMeasurementWindowObserver
{
    private readonly string directory;
    private readonly ILoadGeneratorCounters counters;
    private readonly ILoadGeneratorClock clock;
    private readonly CancellationToken externalCancellation;
    private readonly CancellationTokenSource stop;
    private readonly StreamWriter writer;
    private readonly List<LoadGeneratorSample> samples = new();
    private readonly object gate = new();
    private readonly Task sampler;
    private readonly double? networkThresholdPercent;
    private readonly double cpuThresholdPercent;
    private readonly double maximumIntervalSeconds;
    private MeasurementWindow? measurement;
    private Exception? samplerFailure;
    private bool finished;

    /// <summary>
    /// Старт guard'а. При наличии фабрики счётчиков (<paramref name="countersFactory"/>) используется она;
    /// при отсутствии — кроссплатформенная <see cref="CreateDefaultCounters"/>.
    /// </summary>
    public static LoadGeneratorGuard Start(Uri endpoint, string directory, Func<Uri, ILoadGeneratorCounters>? countersFactory = null,
        ILoadGeneratorClock? clock = null, CancellationToken cancellationToken = default,
        double? networkThresholdPercent = LoadGeneratorAssessment.NetworkThresholdPercent,
        double cpuThresholdPercent = LoadGeneratorAssessment.CpuThresholdPercent,
        double maximumIntervalSeconds = LoadGeneratorAssessment.MaximumIntervalSeconds)
    {
        clock ??= new LoadGeneratorClock();
        CampaignJson.WriteNew(Path.Combine(directory, "load-generator-started.json"), new
        {
            endpoint = endpoint.GetComponents(UriComponents.HostAndPort, UriFormat.UriEscaped),
            startedUtc = clock.UtcNow, startedTick = clock.Timestamp, timestampFrequency = clock.Frequency,
            machine = Environment.MachineName, operatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            processId = Environment.ProcessId, counterImplementation = countersFactory == null ? "default" : "dependency-injected"
        });
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // При отсутствии фабрики используется кроссплатформенная реализация по умолчанию.
            var source = (countersFactory ?? CreateDefaultCounters)(endpoint);
            return new(directory, source, clock, cancellationToken, networkThresholdPercent, cpuThresholdPercent, maximumIntervalSeconds);
        }
        catch (Exception error)
        {
            CampaignJson.WriteNew(Path.Combine(directory, "load-generator-summary.json"), LoadGeneratorReportFactory.Create(
                (error as LoadGeneratorHardwareException)?.Network, null, clock.Frequency,
                networkThresholdPercent, cpuThresholdPercent, new(false, new[] { error.GetType().Name + ": " + error.Message }, Array.Empty<LoadGeneratorInterval>()),
                maximumIntervalSeconds));
            throw;
        }
    }

    /// <summary>
    /// Кроссплатформенный выбор реализации счётчиков по адресу брокера:
    /// loopback → <see cref="SameHostLoadGeneratorCounters"/> (информационный сетевой гейт),
    /// иначе Windows → <see cref="WindowsLoadGeneratorCounters"/>, Linux → <see cref="UnixLoadGeneratorCounters"/>.
    /// </summary>
    /// <param name="endpoint">MQTT-адрес брокера (хост + порт).</param>
    public static ILoadGeneratorCounters CreateDefaultCounters(Uri endpoint)
    {
        var isLoopback = System.Net.IPAddress.TryParse(endpoint.Host, out var literal)
            ? System.Net.IPAddress.IsLoopback(literal)
            : string.Equals(endpoint.Host, "localhost", StringComparison.OrdinalIgnoreCase);
        if (isLoopback) return new SameHostLoadGeneratorCounters(endpoint);
        if (OperatingSystem.IsWindows()) return new WindowsLoadGeneratorCounters(endpoint);
        if (OperatingSystem.IsLinux()) return new UnixLoadGeneratorCounters(endpoint);
        throw new PlatformNotSupportedException("Load-generator guard counters are not implemented for " +
            System.Runtime.InteropServices.RuntimeInformation.OSDescription + ".");
    }

    private LoadGeneratorGuard(string directory, ILoadGeneratorCounters counters, ILoadGeneratorClock clock, CancellationToken cancellationToken,
        double? networkThresholdPercent, double cpuThresholdPercent, double maximumIntervalSeconds)
    {
        (this.directory, this.counters, this.clock, externalCancellation) = (directory, counters, clock, cancellationToken);
        if (networkThresholdPercent is { } threshold && (!double.IsFinite(threshold) || threshold <= 0 || threshold > 100))
            throw new ArgumentOutOfRangeException(nameof(networkThresholdPercent));
        if (!double.IsFinite(cpuThresholdPercent) || cpuThresholdPercent <= 0 || cpuThresholdPercent > 100)
            throw new ArgumentOutOfRangeException(nameof(cpuThresholdPercent));
        if (!double.IsFinite(maximumIntervalSeconds) || maximumIntervalSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumIntervalSeconds));
        this.networkThresholdPercent = networkThresholdPercent;
        this.cpuThresholdPercent = cpuThresholdPercent;
        this.maximumIntervalSeconds = maximumIntervalSeconds;
        CampaignJson.WriteNew(Path.Combine(directory, "load-generator-provenance.json"), LoadGeneratorReportFactory.Create(
            counters.Network, null, clock.Frequency, networkThresholdPercent, cpuThresholdPercent,
            new(false, new[] { "Sampling has not completed." }, Array.Empty<LoadGeneratorInterval>()),
            maximumIntervalSeconds));
        stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        writer = new(new FileStream(Path.Combine(directory, "load-generator-samples.ndjson"), FileMode.CreateNew, FileAccess.Write,
            FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        try { ReadSample(); }
        catch { writer.Dispose(); stop.Dispose(); throw; }
        sampler = SampleAsync();
    }

    public void MeasurementStarted(long tick, DateTimeOffset utc)
    {
        lock (gate)
        {
            if (finished || sampler.IsCompleted || measurement != null) throw new InvalidOperationException("Load-generator sampler is not ready for measurement.");
            measurement = new(tick, 0, utc, default);
            CampaignJson.WriteNew(Path.Combine(directory, "load-generator-measurement-started.json"), new { tick, utc });
        }
    }
    public void MeasurementEnded(long tick, DateTimeOffset utc)
    {
        lock (gate)
        {
            if (finished || measurement == null || measurement.EndedTick != 0) throw new InvalidOperationException("Invalid measurement lifecycle.");
            measurement = measurement with { EndedTick = tick, EndedUtc = utc };
            CampaignJson.WriteNew(Path.Combine(directory, "load-generator-measurement-ended.json"), new { tick, utc });
        }
    }
    private void ReadSample()
    {
        var start = clock.Timestamp; var startUtc = clock.UtcNow;
        var value = counters.Read();
        var sample = new LoadGeneratorSample(start, clock.Timestamp, startUtc, clock.UtcNow, value);
        samples.Add(sample);
        writer.WriteLine(JsonSerializer.Serialize(sample, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }
    private async Task SampleAsync()
    {
        try
        {
            while (true)
            {
                await clock.DelayUntilAsync(samples[^1].ReadStartedTick + clock.Frequency, stop.Token);
                stop.Token.ThrowIfCancellationRequested();
                ReadSample();
                lock (gate)
                    if (measurement?.EndedTick > 0 && samples[^1].ReadStartedTick >= measurement.EndedTick) return;
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) { samplerFailure = error; }
    }
    public async Task CompleteAsync()
    {
        if (finished) throw new InvalidOperationException("Load-generator guard already finalized.");
        Exception? completionError = null;
        try { await sampler.WaitAsync(TimeSpan.FromSeconds(5), externalCancellation); }
        catch (Exception error) { completionError = error; }
        var result = await FinishAsync(completionError);
        externalCancellation.ThrowIfCancellationRequested();
        if (!result.Success) throw new InvalidDataException("Load-generator guard failed: " + string.Join(" ", result.Failures));
    }
    private async Task<LoadGeneratorAssessment> FinishAsync(Exception? error)
    {
        stop.Cancel();
        await sampler;
        writer.Dispose();
        stop.Dispose();
        finished = true;
        var result = measurement == null ? new LoadGeneratorAssessment(false, new[] { "Missing measurement window." }, Array.Empty<LoadGeneratorInterval>())
            : LoadGeneratorAssessment.Evaluate(samples, measurement, clock.Frequency, counters.Network, networkThresholdPercent, cpuThresholdPercent, maximumIntervalSeconds);
        var failure = error ?? samplerFailure;
        if (failure != null) result = result with { Success = false, Failures = result.Failures.Append(failure.GetType().Name + ": " + failure.Message).ToArray() };
        if (externalCancellation.IsCancellationRequested) result = result with { Success = false, Failures = result.Failures.Append("Cancelled.").ToArray() };
        CampaignJson.WriteNew(Path.Combine(directory, "load-generator-summary.json"), LoadGeneratorReportFactory.Create(
            counters.Network, measurement, clock.Frequency, networkThresholdPercent, cpuThresholdPercent, result, maximumIntervalSeconds));
        return result;
    }
    public async ValueTask DisposeAsync()
    {
        if (!finished) await FinishAsync(new InvalidOperationException("Attempt did not complete the load-generator guard."));
    }
}