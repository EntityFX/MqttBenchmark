namespace EntityFX.MqttBenchmark.Calibration;

public sealed record LatencyQuantiles(
    double MinMs,
    double P50Ms,
    double P75Ms,
    double P95Ms,
    double P99Ms,
    double MaxMs)
{
    public static LatencyQuantiles FromSamples(IEnumerable<double> samples)
    {
        var sorted = samples.OrderBy(value => value).ToArray();
        if (sorted.Length == 0 || sorted.Any(value => !double.IsFinite(value) || value < 0))
            throw new InvalidDataException("Latency samples must be finite, non-negative and non-empty.");
        double Rank(double probability) =>
            sorted[Math.Clamp((int)Math.Ceiling(probability * sorted.Length) - 1, 0, sorted.Length - 1)];
        return new LatencyQuantiles(sorted[0], Rank(0.50), Rank(0.75), Rank(0.95),
            Rank(0.99), sorted[^1]);
    }

    public void Validate()
    {
        var values = new[] { MinMs, P50Ms, P75Ms, P95Ms, P99Ms, MaxMs };
        if (values.Any(value => !double.IsFinite(value) || value < 0) ||
            !values.SequenceEqual(values.OrderBy(value => value)))
            throw new InvalidDataException(
                "Latency quantiles must be finite and ordered min <= p50 <= p75 <= p95 <= p99 <= max.");
    }
}
