using EntityFX.MqttBenchmark.Calibration;

namespace EntityFX.MqttBenchmark.Campaign;

public sealed record MeasurementSummary(long AttemptedPublishes, long CompletedPublishes,
    long FailedPublishes, long UniqueDeliveries, long DuplicateDeliveries, long UnexpectedDeliveries,
    long DeliveredAfterFailedPublish, long CompletedIdsDelivered, double ActualMeasurementSeconds,
    LatencyQuantiles? PublishLatencyMs, string LatencyStatus, IReadOnlyDictionary<string, long> ErrorReasons)
{
    public double AttemptedRps => AttemptedPublishes / ActualMeasurementSeconds;
    public double CompletedRps => CompletedPublishes / ActualMeasurementSeconds;
    public double PublishFailureRate => AttemptedPublishes == 0 ? 0 : FailedPublishes / (double)AttemptedPublishes;
    public double DeliveryLossRate => CompletedPublishes == 0 ? 0 : 1 - CompletedIdsDelivered / (double)CompletedPublishes;
}

public sealed class DeliveryLedger
{
    private readonly object gate = new();
    private readonly Dictionary<string, Outcome> publishes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> deliveries = new(StringComparer.Ordinal);
    private double lastDelivery;
    private sealed record Outcome(bool? Completed, double Latency, string? Error);

    public void Attempt(string id)
    {
        lock (gate) if (!publishes.TryAdd(id, new(null, 0, null)))
            throw new InvalidOperationException("Duplicate publish identity.");
    }
    public void Complete(string id, double latencyMs) => SetOutcome(id, new(true, latencyMs, null));
    public void Fail(string id, string reason) => SetOutcome(id, new(false, 0, reason));
    private void SetOutcome(string id, Outcome value)
    {
        lock (gate)
        {
            if (!publishes.TryGetValue(id, out var previous) || previous.Completed != null)
                throw new InvalidOperationException("Publish must have exactly one attempt and one outcome.");
            publishes[id] = value;
        }
    }
    public void Deliver(string id, double seconds)
    {
        lock (gate)
        {
            deliveries[id] = deliveries.GetValueOrDefault(id) + 1;
            lastDelivery = Math.Max(lastDelivery, seconds);
        }
    }
    public (bool AllDelivered, double LastDelivery) DrainState()
    {
        lock (gate) return (publishes.Values.All(x => x.Completed != null) &&
            publishes.Where(x => x.Value.Completed == true).All(x => deliveries.ContainsKey(x.Key)), lastDelivery);
    }
    public MeasurementSummary Snapshot(int qos, double actualSeconds)
    {
        if (qos is < 0 or > 2 || !double.IsFinite(actualSeconds) || actualSeconds <= 0)
            throw new InvalidDataException("A summary requires valid QoS and positive observed measurement duration.");
        lock (gate)
        {
            if (publishes.Values.Any(x => x.Completed == null)) throw new InvalidOperationException("Unsettled publishes.");
            var completed = publishes.Where(x => x.Value.Completed == true).ToArray();
            var failed = publishes.Where(x => x.Value.Completed == false).ToArray();
            return new(publishes.Count, completed.Length, failed.Length,
                deliveries.Keys.LongCount(publishes.ContainsKey), deliveries.Values.Sum(x => x - 1),
                deliveries.Where(x => !publishes.ContainsKey(x.Key)).Sum(x => x.Value),
                failed.LongCount(x => deliveries.ContainsKey(x.Key)), completed.LongCount(x => deliveries.ContainsKey(x.Key)),
                actualSeconds, qos == 0 || completed.Length == 0 ? null : LatencyQuantiles.FromSamples(completed.Select(x => x.Value.Latency)),
                qos == 0 ? "notApplicable" : completed.Length == 0 ? "unobserved" : "observed",
                failed.GroupBy(x => x.Value.Error ?? "unknown").ToDictionary(x => x.Key, x => x.LongCount()));
        }
    }
}

public static class DrainPolicy
{
    public static string? Decide(DeliveryLedger ledger, double start, double now, double quiet, double timeout)
    {
        var state = ledger.DrainState();
        if (state.AllDelivered) return "allCompletedDelivered";
        if (now - start >= timeout) return "timeout";
        if (now - Math.Max(start, state.LastDelivery) >= quiet) return "quietPeriod";
        return null;
    }
}
