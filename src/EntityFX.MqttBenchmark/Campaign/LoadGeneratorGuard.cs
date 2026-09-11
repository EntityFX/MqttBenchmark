using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace EntityFX.MqttBenchmark.Campaign;

public interface ILoadGeneratorCounters
{
    LoadGeneratorInterface Network { get; }
    LoadGeneratorCounters Read();
}
public interface ILoadGeneratorClock
{
    long Timestamp { get; }
    long Frequency { get; }
    DateTimeOffset UtcNow { get; }
    Task DelayUntilAsync(long deadline, CancellationToken cancellationToken);
}
public interface IMeasurementWindowObserver
{
    void MeasurementStarted(long tick, DateTimeOffset utc);
    void MeasurementEnded(long tick, DateTimeOffset utc);
}
public sealed record LoadGeneratorReport(LoadGeneratorInterface? Network, MeasurementWindow? Measurement,
    long TimestampFrequency, double ThresholdPercent, string NetworkAcceptanceMode, double? NetworkThresholdPercent, double SamplingIntervalSeconds, double MaximumIntervalSeconds,
    string AcceptanceRule, string CpuSource, string NetworkSource, LoadGeneratorAssessment Assessment);

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
    private MeasurementWindow? measurement;
    private Exception? samplerFailure;
    private bool finished;

    public static LoadGeneratorGuard Start(Uri endpoint, string directory, Func<Uri, ILoadGeneratorCounters>? countersFactory = null,
        ILoadGeneratorClock? clock = null, CancellationToken cancellationToken = default,
        double? networkThresholdPercent = LoadGeneratorAssessment.NetworkThresholdPercent,
        double cpuThresholdPercent = LoadGeneratorAssessment.CpuThresholdPercent)
    {
        clock ??= new LoadGeneratorClock();
        CampaignJson.WriteNew(Path.Combine(directory, "load-generator-started.json"), new
        {
            endpoint = endpoint.GetComponents(UriComponents.HostAndPort, UriFormat.UriEscaped),
            startedUtc = clock.UtcNow, startedTick = clock.Timestamp, timestampFrequency = clock.Frequency,
            machine = Environment.MachineName, operatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            processId = Environment.ProcessId, counterImplementation = countersFactory == null ? typeof(WindowsLoadGeneratorCounters).FullName : "dependency-injected"
        });
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = (countersFactory ?? (uri => new WindowsLoadGeneratorCounters(uri)))(endpoint);
            return new(directory, source, clock, cancellationToken, networkThresholdPercent, cpuThresholdPercent);
        }
        catch (Exception error)
        {
            CampaignJson.WriteNew(Path.Combine(directory, "load-generator-summary.json"), Report((error as LoadGeneratorHardwareException)?.Network, null, clock.Frequency,
                networkThresholdPercent, cpuThresholdPercent, new(false, new[] { error.GetType().Name + ": " + error.Message }, Array.Empty<LoadGeneratorInterval>())));
            throw;
        }
    }

    private LoadGeneratorGuard(string directory, ILoadGeneratorCounters counters, ILoadGeneratorClock clock, CancellationToken cancellationToken,
        double? networkThresholdPercent, double cpuThresholdPercent)
    {
        (this.directory, this.counters, this.clock, externalCancellation) = (directory, counters, clock, cancellationToken);
        if (networkThresholdPercent is { } threshold && (!double.IsFinite(threshold) || threshold <= 0 || threshold > 100))
            throw new ArgumentOutOfRangeException(nameof(networkThresholdPercent));
        if (!double.IsFinite(cpuThresholdPercent) || cpuThresholdPercent <= 0 || cpuThresholdPercent > 100)
            throw new ArgumentOutOfRangeException(nameof(cpuThresholdPercent));
        this.networkThresholdPercent = networkThresholdPercent;
        this.cpuThresholdPercent = cpuThresholdPercent;
        CampaignJson.WriteNew(Path.Combine(directory, "load-generator-provenance.json"), Report(counters.Network, null, clock.Frequency,
            networkThresholdPercent, cpuThresholdPercent, new(false, new[] { "Sampling has not completed." }, Array.Empty<LoadGeneratorInterval>())));
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
            : LoadGeneratorAssessment.Evaluate(samples, measurement, clock.Frequency, counters.Network, networkThresholdPercent, cpuThresholdPercent);
        var failure = error ?? samplerFailure;
        if (failure != null) result = result with { Success = false, Failures = result.Failures.Append(failure.GetType().Name + ": " + failure.Message).ToArray() };
        if (externalCancellation.IsCancellationRequested) result = result with { Success = false, Failures = result.Failures.Append("Cancelled.").ToArray() };
        CampaignJson.WriteNew(Path.Combine(directory, "load-generator-summary.json"), Report(counters.Network, measurement, clock.Frequency, networkThresholdPercent, cpuThresholdPercent, result));
        return result;
    }
    public async ValueTask DisposeAsync()
    {
        if (!finished) await FinishAsync(new InvalidOperationException("Attempt did not complete the load-generator guard."));
    }
    private static LoadGeneratorReport Report(LoadGeneratorInterface? network, MeasurementWindow? window, long frequency,
        double? networkThresholdPercent, double cpuThresholdPercent, LoadGeneratorAssessment assessment) =>
        new(network, window, frequency, cpuThresholdPercent,
            networkThresholdPercent.HasValue ? "enforced" : "informational", networkThresholdPercent,
            1, LoadGeneratorAssessment.MaximumIntervalSeconds,
            "max-overlapping-interval; no prorating; network=(rx+tx)/link-speed; " +
                (networkThresholdPercent.HasValue ? "network threshold enforced" : "network informational"),
            "Windows GetSystemTimes (kernel includes idle)",
            "OS-routed NetworkInterface cumulative byte counters", assessment);
}

public sealed class LoadGeneratorClock : ILoadGeneratorClock
{
    public long Timestamp => Stopwatch.GetTimestamp();
    public long Frequency => Stopwatch.Frequency;
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public async Task DelayUntilAsync(long deadline, CancellationToken cancellationToken)
    {
        while (Timestamp < deadline)
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, (deadline - Timestamp) / (double)Frequency)), cancellationToken);
    }
}

public sealed record LoadGeneratorInterface(string Id, string Name, string Description, int Index,
    long LinkSpeedBitsPerSecond, string Destination, string InterfaceType, AdapterHardwareEvidence? HardwareEvidence = null);
public sealed record LoadGeneratorCounters(ulong Idle100ns, ulong Kernel100ns, ulong User100ns,
    ulong ReceivedBytes, ulong SentBytes, long LinkSpeedBitsPerSecond);
public sealed record LoadGeneratorSample(long ReadStartedTick, long ReadEndedTick,
    DateTimeOffset ReadStartedUtc, DateTimeOffset ReadEndedUtc, LoadGeneratorCounters Counters);
public sealed record MeasurementWindow(long StartedTick, long EndedTick, DateTimeOffset StartedUtc, DateTimeOffset EndedUtc);
public sealed record LoadGeneratorInterval(long StartedTick, long EndedTick, double CpuPercent,
    double RxBitsPerSecond, double TxBitsPerSecond, double NetworkPercent);
public sealed record LoadGeneratorAssessment(bool Success, IReadOnlyList<string> Failures, IReadOnlyList<LoadGeneratorInterval> Intervals)
{
    public const double CpuThresholdPercent = 70;
    public const double NetworkThresholdPercent = 80;
    public const double MaximumIntervalSeconds = 1.25;
    public static LoadGeneratorAssessment Evaluate(IReadOnlyList<LoadGeneratorSample> samples, MeasurementWindow window,
        long frequency, LoadGeneratorInterface network, double? networkThresholdPercent = NetworkThresholdPercent,
        double cpuThresholdPercent = CpuThresholdPercent)
    {
        var failures = new List<string>();
        var intervals = new List<LoadGeneratorInterval>();
        if (frequency <= 0 || network.LinkSpeedBitsPerSecond <= 0 ||
            networkThresholdPercent is { } threshold && (!double.IsFinite(threshold) || threshold <= 0 || threshold > 100) ||
            !double.IsFinite(cpuThresholdPercent) || cpuThresholdPercent <= 0 || cpuThresholdPercent > 100 ||
            window.EndedTick <= window.StartedTick ||
            window.EndedUtc <= window.StartedUtc || samples.Count < 2)
            return new(false, new[] { "Invalid clock, link capacity, measurement bounds or missing samples." }, intervals);
        if (samples[0].ReadEndedTick > window.StartedTick || samples[^1].ReadStartedTick < window.EndedTick)
            failures.Add("Counter samples do not bracket the entire measurement window.");
        for (var i = 1; i < samples.Count; i++)
        {
            var previous = samples[i - 1]; var current = samples[i];
            if (current.ReadEndedTick <= window.StartedTick || previous.ReadStartedTick >= window.EndedTick) continue;
            var before = previous.Counters; var after = current.Counters;
            var minSeconds = (current.ReadStartedTick - previous.ReadEndedTick) / (double)frequency;
            var maxSeconds = (current.ReadEndedTick - previous.ReadStartedTick) / (double)frequency;
            if (previous.ReadEndedTick < previous.ReadStartedTick || current.ReadEndedTick < current.ReadStartedTick ||
                minSeconds <= 0 || maxSeconds > MaximumIntervalSeconds ||
                before.LinkSpeedBitsPerSecond != network.LinkSpeedBitsPerSecond || after.LinkSpeedBitsPerSecond != network.LinkSpeedBitsPerSecond ||
                after.Idle100ns < before.Idle100ns || after.Kernel100ns < before.Kernel100ns || after.User100ns < before.User100ns ||
                after.ReceivedBytes < before.ReceivedBytes || after.SentBytes < before.SentBytes)
            {
                failures.Add($"Interval {i}: sampling gap, counter reset/wrap, read overlap or link capacity change.");
                continue;
            }
            var idle = (double)(after.Idle100ns - before.Idle100ns);
            var total = (double)(after.Kernel100ns - before.Kernel100ns) + (after.User100ns - before.User100ns);
            if (total <= 0 || idle > total)
            {
                failures.Add($"Interval {i}: invalid system CPU counters.");
                continue;
            }
            var cpu = (total - idle) / total * 100;
            // The shortest possible read-to-read time makes network utilization conservative.
            var rx = (after.ReceivedBytes - before.ReceivedBytes) * 8d / minSeconds;
            var tx = (after.SentBytes - before.SentBytes) * 8d / minSeconds;
            var networkPercent = (rx + tx) / network.LinkSpeedBitsPerSecond * 100;
            intervals.Add(new(previous.ReadStartedTick, current.ReadEndedTick, cpu, rx, tx, networkPercent));
            if (cpu > cpuThresholdPercent)
                failures.Add($"Interval {i}: system CPU exceeds {cpuThresholdPercent}%.");
            if (networkThresholdPercent is { } enforcedThreshold && networkPercent > enforcedThreshold)
                failures.Add($"Interval {i}: combined RX+TX exceeds {enforcedThreshold}%.");
        }
        if (intervals.Count == 0) failures.Add("No valid interval overlaps measurement.");
        return new(failures.Count == 0, failures, intervals);
    }
}
