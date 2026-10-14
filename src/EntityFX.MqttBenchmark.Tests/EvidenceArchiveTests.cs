using EntityFX.MqttBenchmark.Campaign;

namespace EntityFX.MqttBenchmark.Tests;

[TestClass]
public class EvidenceArchiveTests
{
    private static string SealedAttempt(string root)
    {
        var key = new CampaignKey("Aedes", 16, 1, 1, 1);
        var path = Path.Combine(root, "attempts", key.Key, "attempt-01");
        Directory.CreateDirectory(path);
        var identity = new CampaignIdentity("test-archive", new string('a', 64), new string('b', 64),
            "singleHostSequential", "singleCorePinned", new string('c', 40));
        var started = new AttemptResult(identity, key, 1, "started", null, null);
        CampaignJson.WriteNew(Path.Combine(path, "started.json"), started);
        CampaignJson.WriteNew(Path.Combine(path, "result.json"), started with { Status = "failed", Failure = "synthetic failure" });
        File.WriteAllText(Path.Combine(path, "publishes.ndjson"), "synthetic evidence, never real measurements\n");
        CampaignJson.WriteNew(Path.Combine(path, "sha256.json"), CampaignJson.HashTree(path));
        return path;
    }

    [TestMethod]
    public void ColdAttempt_PreservesImmutableHistoryAndDetectsCorruption()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var attempt = SealedAttempt(root);
            EvidenceArchive.Archive(attempt, Path.Combine(root, "disk"));
            Assert.IsFalse(File.Exists(Path.Combine(attempt, "publishes.ndjson")));
            Assert.AreEqual("failed", CampaignJournal.ReadAttempts(root).Single().Status);
            var receipt = CampaignJson.Read<ArchivedAttempt>(Path.Combine(attempt, EvidenceArchive.ReceiptName));
            File.AppendAllText(receipt.ArchivePath, "corrupt");
            Assert.ThrowsException<InvalidDataException>(() => CampaignJournal.ReadAttempts(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void FailedTransferOrBadSeal_NeverRemovesOriginalEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var attempt = SealedAttempt(root);
            File.AppendAllText(Path.Combine(attempt, "publishes.ndjson"), "tampered");
            Assert.ThrowsException<InvalidDataException>(() => EvidenceArchive.Archive(attempt, Path.Combine(root, "disk")));
            Assert.IsTrue(File.Exists(Path.Combine(attempt, "publishes.ndjson")));
            Assert.IsFalse(File.Exists(Path.Combine(attempt, EvidenceArchive.ReceiptName)));
            Assert.ThrowsException<IOException>(() => EvidenceArchive.Archive(attempt, Path.Combine(attempt, "disk")));
            var inaccessible = Path.Combine(root, "not-a-directory");
            File.WriteAllText(inaccessible, "occupied");
            Assert.ThrowsException<IOException>(() => EvidenceArchive.Archive(attempt, inaccessible));
            Assert.IsTrue(File.Exists(Path.Combine(attempt, "publishes.ndjson")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
