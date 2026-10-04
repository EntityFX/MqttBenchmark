using System.Diagnostics;
using System.Text.Json;
using EntityFX.MqttBenchmark.Campaign;

namespace EntityFX.MqttBenchmark.Tests;

/// <summary>
/// Регрессия: обработчик доставки писал NDJSON синхронно под глобальным замком прямо в
/// колбэке MQTT-клиента. Пропускная способность приёмника упиралась в ~1 тыс. записей/с
/// (константа, не зависящая от числа издателей), из-за чего <c>deliveryLossRate</c>
/// измерял пропускную способность стенда, а не потери брокера.
/// </summary>
[TestClass]
public class BufferedEventLogTests
{
    private sealed class CampaignTemp : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "mqb-buffered-" + Guid.NewGuid().ToString("N"));
        public CampaignTemp() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }

    [TestMethod]
    public async Task QueuedRecordsReachTheFileInOrder_AndFlushWaitsForAllOfThem()
    {
        using var temp = new CampaignTemp();
        var path = System.IO.Path.Combine(temp.Path, "deliveries.ndjson");
        await using var log = new BufferedEventLog(path);
        for (var i = 0; i < 1000; i++) log.Write(new { id = i, elapsedSeconds = i * 0.001, payloadBytes = 16 });
        await log.FlushAsync();
        var lines = File.ReadAllLines(path);
        Assert.AreEqual(1000, lines.Length, "Every queued record must reach the file exactly once.");
        Assert.AreEqual(0, JsonDocument.Parse(lines[0]).RootElement.GetProperty("id").GetInt32());
        Assert.AreEqual(999, JsonDocument.Parse(lines[999]).RootElement.GetProperty("id").GetInt32(),
            "Records must keep submission order so the evidence stays replayable.");
        Assert.AreEqual("16", JsonDocument.Parse(lines[500]).RootElement.GetProperty("payloadBytes").GetInt32().ToString());
    }

    [TestMethod]
    public async Task EnqueueIsNotBoundedByDiskThroughput_AndFlushPreservesEveryRecord()
    {
        using var temp = new CampaignTemp();
        var path = System.IO.Path.Combine(temp.Path, "deliveries.ndjson");
        const int records = 50_000;
        var clock = Stopwatch.StartNew();
        await using (var log = new BufferedEventLog(path))
        {
            for (var i = 0; i < records; i++) log.Write(new { id = i, elapsedSeconds = 0, payloadBytes = 16 });
            var enqueue = clock.Elapsed;
            await log.FlushAsync();
            // The producer must not have waited for the disk: at ~1k synchronous writes/second the
            // synchronous implementation needed ~50 s here and blocked the MQTT receive callback.
            Assert.IsTrue(enqueue.TotalSeconds < 5,
                $"Enqueuing {records} records took {enqueue.TotalSeconds:F1}s; the producer is still blocked on I/O.");
        }
        Assert.AreEqual(records, File.ReadAllLines(path).Length);
    }

    [TestMethod]
    public async Task DisposeFlushesPendingRecordsSoTheAttemptSealSeesThem()
    {
        using var temp = new CampaignTemp();
        var path = System.IO.Path.Combine(temp.Path, "deliveries.ndjson");
        var log = new BufferedEventLog(path);
        log.Write(new { id = "a", elapsedSeconds = 0, payloadBytes = 16 });
        log.Write(new { id = "b", elapsedSeconds = 1, payloadBytes = 16 });
        await log.DisposeAsync();
        // The attempt directory is SHA-256 sealed right after the run, so pending records must be on
        // disk before the log is disposed; otherwise the seal would not cover the evidence.
        Assert.AreEqual(2, File.ReadAllLines(path).Length);
        await Assert.ThrowsExceptionAsync<ObjectDisposedException>(async () => await log.FlushAsync());
    }
}