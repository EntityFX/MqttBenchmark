using EntityFX.MqttBenchmark.Campaign;
using System.Threading.Channels;

namespace EntityFX.MqttBenchmark.Tests;

[TestClass]
public class LoadGeneratorGuardTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-09-10T00:00:00Z");
    internal static readonly LoadGeneratorInterface Nic = new("{D8909FF0-176D-4CC5-A372-3F3D376C37ED}", "Ethernet 9", "Intel 82579LM", 39, 1_000_000_000, "10.10.157.111", "Ethernet",
        new(39, "{D8909FF0-176D-4CC5-A372-3F3D376C37ED}", true, false, "deterministic-fixture", Epoch));
    private static LoadGeneratorSample Sample(long tick, ulong idle, ulong rx = 0, ulong tx = 0) =>
        new(tick, tick, Epoch.AddMilliseconds(tick), Epoch.AddMilliseconds(tick), new(idle, (ulong)tick, 0, rx, tx, Nic.LinkSpeedBitsPerSecond));
    private static LoadGeneratorAssessment Assess(params LoadGeneratorSample[] samples) =>
        LoadGeneratorAssessment.Evaluate(samples, new(500, 1500, Epoch.AddMilliseconds(500), Epoch.AddMilliseconds(1500)), 1000, Nic);

    [TestMethod]
    public void RouteSelectionUsesExactOsInterfaceIndex_NotAggregatedAdapters()
    {
        var interfaces = new[] { Nic with { Index = 1, Id = "loopback", InterfaceType = "Loopback" },
            Nic with { Index = 2, Id = "virtual", Name = "vEthernet" }, Nic };
        Assert.AreEqual(Nic, WindowsLoadGeneratorCounters.SelectInterface(39, interfaces));
        Assert.ThrowsException<InvalidDataException>(() => WindowsLoadGeneratorCounters.SelectInterface(1, interfaces));
        Assert.ThrowsException<InvalidDataException>(() => WindowsLoadGeneratorCounters.SelectInterface(100, interfaces));
        Assert.ThrowsException<InvalidDataException>(() => WindowsLoadGeneratorCounters.SelectInterface(39, new[] { Nic with { LinkSpeedBitsPerSecond = 0 } }));
    }

    [TestMethod]
    public void DefaultCounterSourceFailsClosedForLoopbackAndPreservesFailureProvenance()
    {
        using var temp = new CampaignTemp();
        // Route selection itself is read-only; no MQTT connection or live run is made.
        if (OperatingSystem.IsWindows())
            Assert.ThrowsException<InvalidDataException>(() => LoadGeneratorGuard.Start(new Uri("mqtt://127.0.0.1:1883"), temp.Path));
        else
            Assert.ThrowsException<PlatformNotSupportedException>(() => LoadGeneratorGuard.Start(new Uri("mqtt://127.0.0.1:1883"), temp.Path));
        Assert.IsFalse(CampaignJson.Read<LoadGeneratorReport>(Path.Combine(temp.Path, "load-generator-summary.json")).Assessment.Success);
    }

    [TestMethod]
    public void CpuUsesMaximumOverlappingInterval_NotWholeRunAverage()
    {
        var result = Assess(Sample(0, 0), Sample(1000, 290), Sample(2000, 1290));
        Assert.IsFalse(result.Success, "A 71% interval must fail even though the run average is below 70%.");
        Assert.AreEqual(71d, result.Intervals.Max(x => x.CpuPercent));
    }

    [TestMethod]
    public void NetworkUsesCombinedRxTxCapacity_AndHasIndependentEightyPercentLimit()
    {
        Assert.IsTrue(Assess(Sample(0, 0), Sample(1000, 300, 50_000_000, 50_000_000),
            Sample(2000, 600, 100_000_000, 100_000_000)).Success,
            "Network utilization at exactly 80% must pass while CPU remains below 70%.");
        var result = Assess(Sample(0, 0), Sample(1000, 300, 50_000_001, 50_000_000),
            Sample(2000, 600, 100_000_001, 100_000_000));
        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Intervals[0].NetworkPercent > 80);
    }

    [TestMethod]
    public void LinkLimitedPolicy_RecordsAedesUtilizationWithoutRejectingIt_AndKeepsOthersAtEighty()
    {
        var samples = new[] { Sample(0, 0), Sample(1000, 300, 57_500_000, 57_500_000),
            Sample(2000, 600, 115_000_000, 115_000_000) };
        var window = new MeasurementWindow(500, 1500, Epoch.AddMilliseconds(500), Epoch.AddMilliseconds(1500));
        var config = new CampaignDefinition();

        Assert.IsTrue(LoadGeneratorAssessment.Evaluate(samples, window, 1000, Nic,
            config.NetworkThresholdPercentFor("Aedes")).Success,
            "Aedes network utilization is measured but informational in the approved link-limited campaign.");
        Assert.AreEqual(92d, LoadGeneratorAssessment.Evaluate(samples, window, 1000, Nic,
            config.NetworkThresholdPercentFor("Aedes")).Intervals[0].NetworkPercent);
        Assert.IsFalse(LoadGeneratorAssessment.Evaluate(samples, window, 1000, Nic,
            config.NetworkThresholdPercentFor("Mosquitto")).Success,
            "The other brokers must retain the 80% network gate.");
    }

    [TestMethod]
    public void OutsideMeasurementIntervalsAreExcluded_BoundaryCrossingIsNotProrated()
    {
        var samples = new[] { Sample(0, 0), Sample(1000, 0), Sample(2000, 1000), Sample(3000, 2000) };
        Assert.IsTrue(LoadGeneratorAssessment.Evaluate(samples, new(1000, 2000, Epoch.AddSeconds(1), Epoch.AddSeconds(2)), 1000, Nic).Success);
        Assert.IsFalse(LoadGeneratorAssessment.Evaluate(samples, new(999, 2000, Epoch.AddMilliseconds(999), Epoch.AddSeconds(2)), 1000, Nic).Success);
    }

    [DataTestMethod]
    [DataRow("late-baseline")] [DataRow("early-end")] [DataRow("gap")] [DataRow("reset")]
    [DataRow("wrap")] [DataRow("invalid-idle")] [DataRow("zero-speed")] [DataRow("speed-change")]
    [DataRow("read-overlap")]
    public void InvalidOrInsufficientCountersFailClosed(string defect)
    {
        var samples = new[] { Sample(0, 0, 100), Sample(1000, 800, 200), Sample(2000, 1600, 300) };
        switch (defect)
        {
            case "late-baseline": samples[0] = Sample(501, 0, 100); break;
            case "early-end": samples[2] = Sample(1499, 1600, 300); break;
            case "gap": samples[2] = Sample(2501, 1600, 300); break;
            case "reset": samples[1] = Sample(1000, 800, 99); break;
            case "wrap": samples[0] = Sample(0, 0, ulong.MaxValue); break;
            case "invalid-idle": samples[1] = Sample(1000, 1001, 200); break;
            case "zero-speed": samples[1] = samples[1] with { Counters = samples[1].Counters with { LinkSpeedBitsPerSecond = 0 } }; break;
            case "speed-change": samples[1] = samples[1] with { Counters = samples[1].Counters with { LinkSpeedBitsPerSecond = 100_000_000 } }; break;
            case "read-overlap": samples[0] = samples[0] with { ReadEndedTick = 1001 }; break;
        }
        Assert.IsFalse(Assess(samples).Success, defect);
    }

    [TestMethod]
    public async Task SamplerWaitsForEndBracket_ThenSealsImmutableEvidence()
    {
        using var temp = new CampaignTemp();
        var clock = new ManualClock();
        await using var guard = LoadGeneratorGuard.Start(new Uri("mqtt://10.10.157.111:1883"), temp.Path,
            _ => new TestCounters(), clock, networkThresholdPercent: 85);
        await clock.NextDelayAsync();
        guard.MeasurementStarted(500, Epoch.AddMilliseconds(500));
        clock.Advance(1000);
        await clock.NextDelayAsync();
        guard.MeasurementEnded(1500, Epoch.AddMilliseconds(1500));
        var complete = guard.CompleteAsync();
        Assert.IsFalse(complete.IsCompleted, "The measurement end requires a later counter sample.");
        clock.Advance(2000);
        await complete;
        var summary = CampaignJson.Read<LoadGeneratorReport>(Path.Combine(temp.Path, "load-generator-summary.json"));
        Assert.IsTrue(summary.Assessment.Success);
        Assert.AreEqual(500L, summary.Measurement!.StartedTick);
        Assert.AreEqual(1500L, summary.Measurement.EndedTick);
        Assert.AreEqual(Nic, summary.Network);
        Assert.AreEqual(70d, summary.ThresholdPercent);
        Assert.AreEqual(85d, summary.NetworkThresholdPercent);
        Assert.AreEqual(3, File.ReadAllLines(Path.Combine(temp.Path, "load-generator-samples.ndjson")).Length);
        var hashes = CampaignJson.HashTree(temp.Path);
        await guard.DisposeAsync();
        CollectionAssert.AreEquivalent(hashes.ToArray(), CampaignJson.HashTree(temp.Path).ToArray());
        Assert.ThrowsException<IOException>(() => LoadGeneratorGuard.Start(new Uri("mqtt://10.10.157.111:1883"), temp.Path, _ => new TestCounters(), clock));
    }

    [TestMethod]
    public async Task InformationalNetworkPolicy_IsExplicitInSealedReport()
    {
        using var temp = new CampaignTemp();
        var clock = new ManualClock();
        await using var guard = LoadGeneratorGuard.Start(new Uri("mqtt://10.10.157.111:1883"), temp.Path,
            _ => new TestCounters(), clock, networkThresholdPercent: null);
        await clock.NextDelayAsync();
        guard.MeasurementStarted(500, Epoch.AddMilliseconds(500));
        clock.Advance(1000);
        await clock.NextDelayAsync();
        guard.MeasurementEnded(1500, Epoch.AddMilliseconds(1500));
        var complete = guard.CompleteAsync();
        clock.Advance(2000);
        await complete;

        var summary = CampaignJson.Read<LoadGeneratorReport>(Path.Combine(temp.Path, "load-generator-summary.json"));
        Assert.AreEqual("informational", summary.NetworkAcceptanceMode);
        Assert.IsNull(summary.NetworkThresholdPercent);
        StringAssert.Contains(summary.AcceptanceRule, "network informational");
        Assert.IsTrue(summary.Assessment.Success);
    }

    [TestMethod]
    public async Task CancelledSamplerFinishesWriters_AndCannotReportSuccess()
    {
        using var temp = new CampaignTemp();
        var clock = new ManualClock();
        using var cancellation = new CancellationTokenSource();
        await using var guard = LoadGeneratorGuard.Start(new Uri("mqtt://10.10.157.111:1883"), temp.Path,
            _ => new TestCounters(), clock, cancellation.Token);
        await clock.NextDelayAsync();
        guard.MeasurementStarted(500, Epoch.AddMilliseconds(500));
        cancellation.Cancel();
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => guard.CompleteAsync());
        Assert.IsFalse(CampaignJson.Read<LoadGeneratorReport>(Path.Combine(temp.Path, "load-generator-summary.json")).Assessment.Success);
        using var exclusive = new FileStream(Path.Combine(temp.Path, "load-generator-samples.ndjson"), FileMode.Open, FileAccess.Read, FileShare.None);
    }

    [TestMethod]
    public async Task CounterFailureBeforeMeasurementPreventsDispatchAndIsRecorded()
    {
        using var temp = new CampaignTemp();
        var clock = new ManualClock();
        await using var guard = LoadGeneratorGuard.Start(new Uri("mqtt://10.10.157.111:1883"), temp.Path,
            _ => new TestCounters(failAfter: 1), clock);
        await clock.NextDelayAsync();
        clock.Advance(1000);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => guard.CompleteAsync());
        Assert.ThrowsException<InvalidOperationException>(() => guard.MeasurementStarted(1500, Epoch.AddMilliseconds(1500)));
        StringAssert.Contains(File.ReadAllText(Path.Combine(temp.Path, "load-generator-summary.json")), "route changed");
    }

    internal sealed class TestCounters : ILoadGeneratorCounters
    {
        private readonly bool overloaded;
        private readonly int failAfter;
        private ulong reads;
        public TestCounters(bool overloaded = false, int failAfter = int.MaxValue) => (this.overloaded, this.failAfter) = (overloaded, failAfter);
        public LoadGeneratorInterface Network => Nic;
        public LoadGeneratorCounters Read()
        {
            if (reads >= (ulong)failAfter) throw new IOException("route changed");
            var count = reads++;
            return new(count * (overloaded ? 200UL : 800UL), count * 1000, 0, count * 100, count * 100, Nic.LinkSpeedBitsPerSecond);
        }
    }

    private sealed class ManualClock : ILoadGeneratorClock
    {
        private readonly Channel<bool> waiting = Channel.CreateUnbounded<bool>();
        private TaskCompletionSource<bool>? pending;
        public long Timestamp { get; private set; }
        public long Frequency => 1000;
        public DateTimeOffset UtcNow => Epoch.AddMilliseconds(Timestamp);
        public Task DelayUntilAsync(long deadline, CancellationToken cancellationToken)
        {
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            waiting.Writer.TryWrite(true);
            return pending.Task.WaitAsync(cancellationToken);
        }
        public async Task NextDelayAsync()
        {
            try { await waiting.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)); }
            catch (TimeoutException) { Assert.Fail("Sampler did not schedule its next one-second counter read."); }
        }
        public void Advance(long tick) { Timestamp = tick; pending!.SetResult(true); }
    }
}
