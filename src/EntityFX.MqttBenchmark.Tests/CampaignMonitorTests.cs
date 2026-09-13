using EntityFX.MqttBenchmark.Campaign;

namespace EntityFX.MqttBenchmark.Tests;

[TestClass]
public class CampaignMonitorTests
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "campaign-monitor-test-" + Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(root))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }

    private void WriteAttempt(string broker, string keyName, string status, string? failure)
    {
        var attempt = Path.Combine(root, broker, "attempts", keyName, "attempt-01");
        Directory.CreateDirectory(attempt);
        var key = new CampaignKey("Broker", 16, 1, 1, 1);
        CampaignJson.WriteNew(Path.Combine(attempt, "started.json"), new AttemptResult(
            CampaignArtifactTests.Identity(), key, 1, "started", null, null));
        CampaignJson.WriteNew(Path.Combine(attempt, "result.json"), new AttemptResult(
            CampaignArtifactTests.Identity(), key, 1, status, failure,
            status == "success" ? CampaignArtifactTests.Observation(key) : null));
    }

    [TestMethod]
    public void Refresh_CountsSuccessWarnedAndInProgressAcrossBrokers()
    {
        WriteAttempt("stand111-full-mosquitto", "Broker.m16.q0.p1.r1", "success", null);
        WriteAttempt("stand111-full-mosquitto", "Broker.m16.q0.p1.r2", "failed", "Stand controller clock failed (unready).");
        Directory.CreateDirectory(Path.Combine(root, "stand111-full-emqx", "attempts", "Broker.m16.q0.p1.r1", "attempt-01"));
        var monitor = new CampaignMonitor(root, "stand111-full-", 4, 2, DateTime.Now.AddSeconds(-60));
        var snapshot = monitor.Refresh();
        Assert.AreEqual(1, snapshot.KeysDone);
        Assert.AreEqual(3, snapshot.KeysStarted);
        var mosquitto = snapshot.Brokers.Single(x => x.Broker == "mosquitto");
        Assert.AreEqual(1, mosquitto.Success);
        Assert.AreEqual(1, mosquitto.Warned);
        Assert.AreEqual(50.0, mosquitto.RpsAvg);
        Assert.AreEqual(2.0, mosquitto.P50Avg);
        var emqx = snapshot.Brokers.Single(x => x.Broker == "emqx");
        Assert.AreEqual(1, emqx.InProgress);
        Assert.AreEqual("Broker.m16.q0.p1.r1", snapshot.CurrentKey);
        Assert.IsTrue(snapshot.EtaSec > 0);
        var rendered = monitor.Render(90);
        StringAssert.Contains(rendered, "1/4");
        StringAssert.Contains(rendered, "mosquitto");
        StringAssert.Contains(rendered, "in-progress");
        // повторный обход идёт из кэша и даёт тот же результат
        Assert.AreEqual(1, monitor.Refresh().KeysDone);
    }

    [TestMethod]
    public void RunAsync_RejectsLiveModeWithoutInteractiveConsole_AndOnceRequiresCampaigns()
    {
        var redirected = new Dictionary<string, string?> { ["campaigns"] = root };
        if (Console.IsOutputRedirected)
        {
            Directory.CreateDirectory(root);
            Assert.ThrowsException<ArgumentException>(() => CampaignMonitor.RunAsync(redirected, CancellationToken.None).GetAwaiter().GetResult());
        }
        Assert.ThrowsException<ArgumentException>(() =>
            CampaignMonitor.RunAsync(new Dictionary<string, string?>(), CancellationToken.None).GetAwaiter().GetResult());
    }
}
