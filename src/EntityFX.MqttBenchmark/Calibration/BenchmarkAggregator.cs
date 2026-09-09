namespace EntityFX.MqttBenchmark.Calibration;

public static class BenchmarkAggregator
{
    public static void ValidateCoverage(
        IEnumerable<AggregatedBenchmarkPoint> points, BenchmarkMatrixDefinition matrix)
    {
        var actual = points.Select(point =>
                (point.Broker, point.MessageBytes, point.Qos, point.Clients))
            .ToHashSet();
        var expected = matrix.Expand().Select(run =>
                (Broker: run.Broker.Name, run.MessageBytes, run.Qos, run.Clients))
            .ToHashSet();
        if (!actual.SetEquals(expected))
        {
            var missing = expected.Except(actual).Take(5)
                .Select(key => $"{key.Broker}/m{key.MessageBytes}/q{key.Qos}/c{key.Clients}");
            var extra = actual.Except(expected).Take(5)
                .Select(key => $"{key.Broker}/m{key.MessageBytes}/q{key.Qos}/c{key.Clients}");
            throw new InvalidDataException(
                $"Aggregate coverage differs from matrix. Missing: {string.Join(", ", missing)}; extra: {string.Join(", ", extra)}.");
        }
    }

    public static IReadOnlyList<AggregatedBenchmarkPoint> Aggregate(
        IEnumerable<RawBenchmarkResult> results, int expectedRepeats)
    {
        if (expectedRepeats <= 0) throw new ArgumentOutOfRangeException(nameof(expectedRepeats));
        var rows = results.ToArray();
        foreach (var row in rows) row.Validate();

        return rows.GroupBy(row => (row.Broker, row.MessageBytes, row.Qos, row.Clients))
            .OrderBy(group => group.Key.Broker, StringComparer.Ordinal)
            .ThenBy(group => group.Key.MessageBytes)
            .ThenBy(group => group.Key.Qos)
            .ThenBy(group => group.Key.Clients)
            .Select(group => AggregateGroup(group.ToArray(), expectedRepeats))
            .ToArray();
    }

    private static AggregatedBenchmarkPoint AggregateGroup(
        RawBenchmarkResult[] rows, int expectedRepeats)
    {
        var key = rows[0];
        var repeats = rows.Select(row => row.Repeat).OrderBy(value => value).ToArray();
        if (rows.Length != expectedRepeats || repeats.Distinct().Count() != expectedRepeats ||
            !repeats.SequenceEqual(Enumerable.Range(1, expectedRepeats)))
            throw new InvalidDataException(
                $"{key.Broker}/m{key.MessageBytes}/q{key.Qos}/c{key.Clients} requires repeats 1..{expectedRepeats}.");

        LatencyQuantileStatistics? latency = null;
        if (key.Qos > 0)
        {
            var values = rows.Select(row => row.PublishLatencyMs ??
                throw new InvalidDataException("QoS 1/2 aggregation requires latency quantiles.")).ToArray();
            latency = new LatencyQuantileStatistics(
                MetricStatistics.Calculate(values.Select(value => value.MinMs)),
                MetricStatistics.Calculate(values.Select(value => value.P50Ms)),
                MetricStatistics.Calculate(values.Select(value => value.P75Ms)),
                MetricStatistics.Calculate(values.Select(value => value.P95Ms)),
                MetricStatistics.Calculate(values.Select(value => value.P99Ms)),
                MetricStatistics.Calculate(values.Select(value => value.MaxMs)));
        }

        return new AggregatedBenchmarkPoint(key.Broker, key.MessageBytes, key.Qos,
            key.Clients, expectedRepeats,
            MetricStatistics.Calculate(rows.Select(row => row.CompletedRps)),
            MetricStatistics.Calculate(rows.Select(row => row.PublishFailureRate)),
            MetricStatistics.Calculate(rows.Select(row => row.DeliveryLossRate)),
            latency);
    }
}
