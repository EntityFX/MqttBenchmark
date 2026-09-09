using System.Text.Json;
using EntityFX.MqttBenchmark.Calibration;
using EntityFX.MqttBenchmark.Campaign;

namespace EntityFX.MqttBenchmark.Tests;

[TestClass]
public class CampaignArtifactTests
{
    internal static CampaignIdentity Identity(string id = "test") => new(id, new string('a', 64),
        new string('b', 64), "singleHostSequential", "singleCorePinned", new string('c', 40));
    internal static RunObservation Observation(CampaignKey key, double seconds = 2) => new(key,
        new MeasurementSummary(100, 90, 10, 81, 1, 2, 0, 81, seconds,
            key.Qos == 0 ? null : new LatencyQuantiles(1, 2, 3, 4, 5, 6),
            key.Qos == 0 ? "notApplicable" : "observed", new Dictionary<string, long> { ["rejected"] = 10 }),
        DateTimeOffset.Parse("2026-09-09T00:00:00Z"), DateTimeOffset.Parse("2026-09-09T00:00:00Z").AddSeconds(seconds),
        "quietPeriod", 2, 0.5);

    [TestMethod]
    public async Task Resume_SkipsOnlySuccessKeepsAttemptsAndStopsEntireCampaignAtThirdFailure()
    {
        using var temp = new CampaignTemp();
        var keys = new CampaignDefinition().Expand().Take(3).ToArray();
        string successfulBytes;
        using (var campaign = CampaignJournal.Open(temp.Path, Identity(), false))
        {
            await campaign.ExecuteAsync(keys.Take(1), (key, path, number, ct) => Task.FromResult(Observation(key)));
            successfulBytes = File.ReadAllText(Directory.GetFiles(temp.Path, "result.json", SearchOption.AllDirectories).Single());
        }
        using (var campaign = CampaignJournal.Open(temp.Path, Identity(), true))
        {
            await Assert.ThrowsExceptionAsync<CampaignExhaustedException>(() => campaign.ExecuteAsync(keys,
                (key, path, number, ct) => throw new IOException("broker unavailable")));
        }
        Assert.AreEqual(4, Directory.GetFiles(temp.Path, "result.json", SearchOption.AllDirectories).Length);
        var rows = CampaignJournal.ReadAttempts(temp.Path);
        Assert.AreEqual(1, rows.Count(x => x.Status == "success"));
        Assert.AreEqual(3, rows.Count(x => x.Status == "failed" && x.Key == keys[1]));
        Assert.AreEqual(successfulBytes, File.ReadAllText(Directory.GetFiles(temp.Path, "result.json", SearchOption.AllDirectories)
            .Single(x => File.ReadAllText(x).Contains("\"status\": \"success\""))));
        using var exhausted = CampaignJournal.Open(temp.Path, Identity(), true);
        await Assert.ThrowsExceptionAsync<CampaignExhaustedException>(() => exhausted.ExecuteAsync(keys,
            (key, path, number, ct) => Task.FromResult(Observation(key))));
    }

    [TestMethod]
    public async Task Campaign_FailsClosedOnConcurrentOpenChangedIdentityAndTamperedArtifact()
    {
        using var temp = new CampaignTemp();
        using (var campaign = CampaignJournal.Open(temp.Path, Identity(), false))
        {
            Assert.ThrowsException<IOException>(() => CampaignJournal.Open(temp.Path, Identity(), true));
            await campaign.ExecuteAsync(new CampaignDefinition().Expand().Take(1), (key, path, number, ct) => Task.FromResult(Observation(key)));
        }
        Assert.ThrowsException<IOException>(() => CampaignJournal.Open(temp.Path, Identity(), false));
        Assert.ThrowsException<InvalidDataException>(() => CampaignJournal.Open(temp.Path, Identity("other"), true));
        var result = Directory.GetFiles(temp.Path, "result.json", SearchOption.AllDirectories).Single();
        File.AppendAllText(result, " ");
        Assert.ThrowsException<InvalidDataException>(() => CampaignJournal.Open(temp.Path, Identity(), true));
    }

    [TestMethod]
    public async Task InterruptedAttempt_ConsumesRetryWithoutOverwritingPartialEvidence()
    {
        using var temp = new CampaignTemp();
        var key = new CampaignDefinition().Expand()[0];
        using (var campaign = CampaignJournal.Open(temp.Path, Identity(), false))
        {
            using var cancel = new CancellationTokenSource();
            await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => campaign.ExecuteAsync(new[] { key },
                (k, path, number, ct) =>
                {
                    File.WriteAllText(Path.Combine(path, "partial.ndjson"), "partial evidence");
                    cancel.Cancel();
                    throw new OperationCanceledException();
                }, cancellationToken: cancel.Token));
        }
        var interrupted = CampaignJournal.ReadAttempts(temp.Path).Single();
        Assert.AreEqual("interrupted", interrupted.Status);
        using var resumed = CampaignJournal.Open(temp.Path, Identity(), true);
        await resumed.ExecuteAsync(new[] { key }, (k, path, number, ct) => Task.FromResult(Observation(k)));
        Assert.AreEqual(2, CampaignJournal.ReadAttempts(temp.Path).Single(x => x.Status == "success").Attempt);
        Assert.AreEqual("partial evidence", File.ReadAllText(Directory.GetFiles(temp.Path, "partial.ndjson", SearchOption.AllDirectories).Single()));
    }

    [TestMethod]
    public void Aggregate_Requires288UniqueCompatibleSuccessfulKeysAndUsesStudentTObservedRates()
    {
        var config = new CampaignDefinition();
        var rows = config.Expand().Select(key => new AttemptResult(Identity(), key, 1, "success", null, Observation(key, key.Repeat))).ToArray();
        var doc = CampaignAggregator.Aggregate(config, rows, new Dictionary<string, string> { ["raw/result.json"] = new string('d', 64) });
        Assert.AreEqual(288, doc.RunCount);
        Assert.AreEqual(96, doc.Points.Count);
        var point = doc.Points.First(x => x.Qos == 0);
        Assert.AreEqual(61.1111111111111, point.AttemptedRps.Mean, 1e-10);
        Assert.AreEqual(55, point.CompletedRps.Mean, 1e-10);
        Assert.AreEqual(31.224989991992, point.CompletedRps.StandardDeviation, 1e-10);
        Assert.AreEqual(77.5671751881437, point.CompletedRps.Ci95HalfWidth, 1e-8);
        Assert.IsNull(point.ObservedLatencyQuantiles);
        Assert.AreEqual(0.5, point.RttBaselineMs);
        var json = JsonSerializer.Serialize(doc, CampaignJson.Options);
        using var parsed = JsonDocument.Parse(json);
        Assert.AreEqual(3, parsed.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.AreEqual("test", parsed.RootElement.GetProperty("provenance").GetProperty("identity").GetProperty("campaignId").GetString());
        Assert.ThrowsException<InvalidDataException>(() => CampaignAggregator.Aggregate(config, rows.Take(287), new Dictionary<string, string>()));
        Assert.ThrowsException<InvalidDataException>(() => CampaignAggregator.Aggregate(config, rows.Append(rows[0]), new Dictionary<string, string>()));
        foreach (var changed in new[] { Identity() with { CpuMode = "hostAllCores" }, Identity() with { DeploymentMode = "distributedHosts" }, Identity("mixed") })
        {
            var mixed = rows.ToArray(); mixed[0] = mixed[0] with { Identity = changed };
            Assert.ThrowsException<InvalidDataException>(() => CampaignAggregator.Aggregate(config, mixed, new Dictionary<string, string>()));
        }
    }

    [DataTestMethod]
    [DataRow(1)] [DataRow(2)]
    public async Task ZeroCompletions_AreFailedAndResumeCanReplaceThemWithAggregatableSuccess(int qos)
    {
        using var temp = new CampaignTemp();
        var config = new CampaignDefinition();
        var key = config.Expand().First(x => x.Qos == qos);
        var zero = ZeroCompletions(key);
        using (var journal = CampaignJournal.Open(temp.Path, Identity(), false))
        using (var cancel = new CancellationTokenSource())
        {
            await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => journal.ExecuteAsync(new[] { key },
                (k, path, number, ct) => { cancel.Cancel(); return Task.FromResult(zero); }, cancellationToken: cancel.Token));
        }
        var failed = CampaignJournal.ReadAttempts(temp.Path).Single();
        Assert.AreEqual("failed", failed.Status);
        Assert.IsNull(failed.Observation);
        StringAssert.Contains(failed.Failure!, "completion latency");
        using (var resumed = CampaignJournal.Open(temp.Path, Identity(), true))
            await resumed.ExecuteAsync(new[] { key }, (k, path, number, ct) => Task.FromResult(Observation(k)));
        var history = CampaignJournal.ReadAttempts(temp.Path);
        Assert.AreEqual(2, history.Single(x => x.Status == "success").Attempt);
        var remaining = config.Expand().Where(x => x != key).Select(x => new AttemptResult(Identity(), x, 1, "success", null, Observation(x)));
        Assert.AreEqual(288, CampaignAggregator.Aggregate(config, remaining.Concat(history), new Dictionary<string, string>()).RunCount);
        var invalidSuccess = failed with { Status = "success", Failure = null, Observation = zero };
        Assert.ThrowsException<InvalidDataException>(() => CampaignAggregator.Aggregate(config, remaining.Append(invalidSuccess), new Dictionary<string, string>()));
    }

    [DataTestMethod]
    [DataRow(1)] [DataRow(2)]
    public async Task ZeroCompletions_ThirdFailureStopsLaterKeysAndFutureResume(int qos)
    {
        using var temp = new CampaignTemp();
        var keys = new CampaignDefinition().Expand().Where(x => x.Qos == qos).Take(2).ToArray();
        using (var journal = CampaignJournal.Open(temp.Path, Identity(), false))
            await Assert.ThrowsExceptionAsync<CampaignExhaustedException>(() => journal.ExecuteAsync(keys,
                (key, path, number, ct) => Task.FromResult(ZeroCompletions(key))));
        var history = CampaignJournal.ReadAttempts(temp.Path);
        Assert.AreEqual(3, history.Count);
        Assert.IsTrue(history.All(x => x.Key == keys[0] && x.Status == "failed"));
        using var resumed = CampaignJournal.Open(temp.Path, Identity(), true);
        await Assert.ThrowsExceptionAsync<CampaignExhaustedException>(() => resumed.ExecuteAsync(keys,
            (key, path, number, ct) => Task.FromResult(Observation(key))));
    }

    [TestMethod]
    public void Resume_RejectsHistoricallySealedSuccessWithoutQosCompletionLatency()
    {
        using var temp = new CampaignTemp();
        var key = new CampaignDefinition().Expand().First(x => x.Qos == 1);
        using (CampaignJournal.Open(temp.Path, Identity(), false)) { }
        var directory = Path.Combine(temp.Path, "attempts", key.Key, "attempt-01");
        CampaignJson.WriteNew(Path.Combine(directory, "started.json"), new AttemptResult(Identity(), key, 1, "started", null, null));
        CampaignJson.WriteNew(Path.Combine(directory, "result.json"), new AttemptResult(Identity(), key, 1, "success", null, ZeroCompletions(key)));
        CampaignJson.WriteNew(Path.Combine(directory, "sha256.json"), CampaignJson.HashTree(directory));
        Assert.ThrowsException<InvalidDataException>(() => { using var journal = CampaignJournal.Open(temp.Path, Identity(), true); });
    }

    private static RunObservation ZeroCompletions(CampaignKey key) => Observation(key) with
    {
        Measurement = new MeasurementSummary(100, 0, 100, 0, 0, 0, 0, 0, 2, null, "unobserved",
            new Dictionary<string, long> { ["transport"] = 100 })
    };
}

internal sealed class CampaignTemp : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "campaign-test-" + Guid.NewGuid().ToString("N"));
    public void Dispose()
    {
        if (!Directory.Exists(Path)) return;
        // Git fixture object files can be read-only on Windows; every file here belongs to this fixture.
        foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(Path, true);
    }
}
