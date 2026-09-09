using System.Text.Json;
using EntityFX.MqttBenchmark.Calibration;

namespace EntityFX.MqttBenchmark.Tests;

[TestClass]
public class CalibrationPipelineTests
{
    private static BenchmarkMatrixDefinition Matrix() => new()
    {
        Brokers = new[]
        {
            new BenchmarkBrokerEndpoint("Mosquitto", new Uri("mqtt://localhost:1883")),
            new BenchmarkBrokerEndpoint("ActiveMQ", new Uri("mqtt://localhost:1884")),
            new BenchmarkBrokerEndpoint("Aedes", new Uri("mqtt://localhost:1885")),
            new BenchmarkBrokerEndpoint("EMQX", new Uri("mqtt://localhost:1886"))
        },
        MessageBytes = new[] { 16, 256 },
        Qos = new[] { 0, 1, 2 },
        Clients = new[] { 1, 2, 4, 8, 16, 32, 64, 128 },
        WarmupSeconds = 5,
        MeasurementSeconds = 30,
        Repeats = 10
    };

    private static RawBenchmarkResult Raw(
        string broker = "Mosquitto", int messageBytes = 16, int qos = 1,
        int clients = 1, int repeat = 1, long requestCount = 100,
        long ok = 80, long failed = 20, long received = 72,
        LatencyQuantiles? latency = null) => new(
            $"run-{repeat}", broker, new Uri("mqtt://localhost:1883"), messageBytes,
            qos, clients, repeat, 5, 30, requestCount, ok, failed, received,
            latency ?? (qos == 0 ? null : new LatencyQuantiles(1, 2, 3, 4, 5, 6)),
            DateTimeOffset.Parse("2026-09-09T00:00:00Z"),
            DateTimeOffset.Parse("2026-09-09T00:00:30Z"));

    [TestMethod]
    public void Matrix_ExpandsApprovedDissertationDimensions()
    {
        var runs = Matrix().Expand().ToArray();

        Assert.AreEqual(4 * 2 * 3 * 8 * 10, runs.Length);
        Assert.AreEqual(10, runs.Count(run => run.Broker.Name == "Mosquitto" &&
            run.MessageBytes == 16 && run.Qos == 0 && run.Clients == 1));
        CollectionAssert.AreEqual(Enumerable.Range(1, 10).ToArray(),
            runs.Where(run => run.Broker.Name == "EMQX" && run.MessageBytes == 256 &&
                run.Qos == 2 && run.Clients == 128).Select(run => run.Repeat).ToArray());
    }

    [TestMethod]
    public void Matrix_FailsFastOnInvalidOrDuplicateDimensions()
    {
        var matrix = Matrix();
        matrix.Clients = new[] { 1, 1 };
        Assert.ThrowsException<InvalidDataException>(() => matrix.Validate());

        matrix = Matrix();
        matrix.MessageBytes = new[] { 0 };
        Assert.ThrowsException<InvalidDataException>(() => matrix.Validate());

        matrix = Matrix();
        matrix.Qos = new[] { 3 };
        Assert.ThrowsException<InvalidDataException>(() => matrix.Validate());
    }

    [TestMethod]
    public void RawResult_UsesFixedMeasurementWindowAndSeparateRates()
    {
        var result = Raw();
        Assert.AreEqual(80.0 / 30.0, result.CompletedRps, 1e-12);
        Assert.AreEqual(0.2, result.PublishFailureRate, 1e-12);
        Assert.AreEqual(0.1, result.DeliveryLossRate, 1e-12);

        var totalFailure = Raw(ok: 0, failed: 100, received: 0);
        Assert.AreEqual(1, totalFailure.PublishFailureRate);
        Assert.AreEqual(0, totalFailure.DeliveryLossRate,
            "Conditional delivery loss is unobservable when no publish completed");
    }

    [TestMethod]
    public void Statistics_UsesSampleDeviationAndStudentCi95()
    {
        var stats = MetricStatistics.Calculate(new[] { 1.0, 2.0, 3.0 });
        Assert.AreEqual(2, stats.Mean, 1e-12);
        Assert.AreEqual(1, stats.StandardDeviation, 1e-12);
        Assert.AreEqual(2.4841377117, stats.Ci95HalfWidth, 1e-6);
        Assert.AreEqual(1, stats.Min);
        Assert.AreEqual(3, stats.Max);
    }

    [TestMethod]
    public void Aggregator_RequiresEveryRepeatAndExcludesQos0Latency()
    {
        var qos1 = Enumerable.Range(1, 3).Select(repeat => Raw(repeat: repeat)).ToArray();
        var aggregate = BenchmarkAggregator.Aggregate(qos1, expectedRepeats: 3).Single();
        Assert.AreEqual(3, aggregate.Repeats);
        Assert.IsNotNull(aggregate.ProcessingLatencyQuantiles);

        var qos0 = Enumerable.Range(1, 3)
            .Select(repeat => Raw(qos: 0, repeat: repeat, latency: null)).ToArray();
        Assert.IsNull(BenchmarkAggregator.Aggregate(qos0, 3).Single().ProcessingLatencyQuantiles);
        Assert.ThrowsException<InvalidDataException>(() =>
            BenchmarkAggregator.Aggregate(qos1.Take(2), expectedRepeats: 3));
    }

    [TestMethod]
    public void RawCsv_RoundTripsAndNeverOverwrites()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mqttbenchmark-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "run.csv");
            RawArtifactStore.WriteNew(path, Raw());
            var loaded = RawArtifactStore.Read(path);
            Assert.AreEqual(Raw().RunId, loaded.RunId);
            Assert.AreEqual(Raw().PublishLatencyMs, loaded.PublishLatencyMs);
            Assert.ThrowsException<IOException>(() => RawArtifactStore.WriteNew(path, Raw()));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void ProfileGenerator_WritesCompatibleV2SchemaAndInputHashes()
    {
        var rows = new List<RawBenchmarkResult>();
        foreach (var broker in new[] { "Mosquitto", "ActiveMQ", "Aedes", "EMQX" })
        foreach (var bytes in new[] { 16, 256 })
        foreach (var qos in new[] { 0, 1, 2 })
        foreach (var clients in new[] { 1, 2 })
        foreach (var repeat in new[] { 1, 2 })
            rows.Add(Raw(broker, bytes, qos, clients, repeat));

        var aggregates = BenchmarkAggregator.Aggregate(rows, 2);
        var document = BrokerProfileGenerator.Generate(aggregates, new ProfileProvenance(
            "mqtt-y-sha", "benchmark-sha",
            new Dictionary<string, string> { ["raw/a.csv"] = new string('a', 64) },
            DateTimeOffset.Parse("2026-09-09T01:00:00Z"), 5, 30, 2));
        var json = BrokerProfileGenerator.Serialize(document);
        using var parsed = JsonDocument.Parse(json);

        Assert.AreEqual(2, parsed.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.AreEqual(96, parsed.RootElement.GetProperty("runCount").GetInt32());
        Assert.AreEqual("mqtt-y-sha", parsed.RootElement.GetProperty("mqttYCommitSha").GetString());
        Assert.AreEqual(4, parsed.RootElement.GetProperty("brokers").GetArrayLength());
        var qos0 = parsed.RootElement.GetProperty("brokers")[0]
            .GetProperty("messageSizes")[0].GetProperty("qos")[0]
            .GetProperty("samples")[0];
        Assert.AreEqual(JsonValueKind.Null,
            qos0.GetProperty("processingLatencyQuantiles").ValueKind);
    }

    [TestMethod]
    public void LatencyQuantiles_UsesNearestRankKnots()
    {
        var quantiles = LatencyQuantiles.FromSamples(Enumerable.Range(1, 100)
            .Select(value => (double)value));
        Assert.AreEqual(1, quantiles.MinMs);
        Assert.AreEqual(50, quantiles.P50Ms);
        Assert.AreEqual(75, quantiles.P75Ms);
        Assert.AreEqual(95, quantiles.P95Ms);
        Assert.AreEqual(99, quantiles.P99Ms);
        Assert.AreEqual(100, quantiles.MaxMs);
    }

    [TestMethod]
    public void Payload_IsExactLengthAndDeterministic()
    {
        var first = BenchmarkPayload.Create(16, 42);
        var repeated = BenchmarkPayload.Create(16, 42);
        CollectionAssert.AreEqual(first, repeated);
        Assert.AreEqual(16, first.Length);
        CollectionAssert.AreNotEqual(first, BenchmarkPayload.Create(16, 43));
    }

    [TestMethod]
    public void Coverage_RejectsMissingMatrixPoint()
    {
        var matrix = Matrix();
        matrix.Brokers = matrix.Brokers.Take(1).ToArray();
        matrix.MessageBytes = new[] { 16 };
        matrix.Qos = new[] { 0 };
        matrix.Clients = new[] { 1, 2 };
        matrix.Repeats = 2;
        var rows = new[] { Raw(qos: 0, repeat: 1), Raw(qos: 0, repeat: 2) };
        var aggregates = BenchmarkAggregator.Aggregate(rows, 2);
        Assert.ThrowsException<InvalidDataException>(() =>
            BenchmarkAggregator.ValidateCoverage(aggregates, matrix));
    }
}
