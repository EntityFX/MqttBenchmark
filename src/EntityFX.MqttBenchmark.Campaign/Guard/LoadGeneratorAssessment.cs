namespace EntityFX.MqttBenchmark.Campaign;

public sealed record LoadGeneratorAssessment(bool Success, IReadOnlyList<string> Failures, IReadOnlyList<LoadGeneratorInterval> Intervals)
{
    public const double CpuThresholdPercent = 70;
    public const double NetworkThresholdPercent = 80;
    public const double MaximumIntervalSeconds = 1.25;
    public static LoadGeneratorAssessment Evaluate(IReadOnlyList<LoadGeneratorSample> samples, MeasurementWindow window,
        long frequency, LoadGeneratorInterface network, double? networkThresholdPercent = NetworkThresholdPercent,
        double cpuThresholdPercent = CpuThresholdPercent, double maximumIntervalSeconds = MaximumIntervalSeconds)
    {
        if (maximumIntervalSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumIntervalSeconds));
        var failures = new List<string>();
        var intervals = new List<LoadGeneratorInterval>();
        if (frequency <= 0 || network.LinkSpeedBitsPerSecond < 0 ||
            networkThresholdPercent is { } threshold && (!double.IsFinite(threshold) || threshold <= 0 || threshold > 100) ||
            !double.IsFinite(cpuThresholdPercent) || cpuThresholdPercent <= 0 || cpuThresholdPercent > 100 ||
            window.EndedTick <= window.StartedTick ||
            window.EndedUtc <= window.StartedUtc || samples.Count < 2)
            return new(false, new[] { "Invalid clock, link capacity, measurement bounds or missing samples." }, intervals);
        if (network.LinkSpeedBitsPerSecond == 0 && networkThresholdPercent is not null)
            return new(false, new[] { "Same-host loopback has no enforceable network gate; set the broker network threshold to null (informational mode)." }, intervals);
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
                minSeconds <= 0 || maxSeconds > maximumIntervalSeconds ||
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
            // A zero link capacity (same-host loopback) is informational: no gate is applied.
            var rx = (after.ReceivedBytes - before.ReceivedBytes) * 8d / minSeconds;
            var tx = (after.SentBytes - before.SentBytes) * 8d / minSeconds;
            var networkPercent = network.LinkSpeedBitsPerSecond > 0 ? (rx + tx) / network.LinkSpeedBitsPerSecond * 100 : 0;
            intervals.Add(new(previous.ReadStartedTick, current.ReadEndedTick, cpu, rx, tx, networkPercent));
            if (cpu > cpuThresholdPercent)
                failures.Add($"Interval {i}: system CPU exceeds {cpuThresholdPercent}%.");
            if (networkThresholdPercent is { } enforcedThreshold && network.LinkSpeedBitsPerSecond > 0 && networkPercent > enforcedThreshold)
                failures.Add($"Interval {i}: combined RX+TX exceeds {enforcedThreshold}%.");
        }
        if (intervals.Count == 0) failures.Add("No valid interval overlaps measurement.");
        return new(failures.Count == 0, failures, intervals);
    }
}
