using EntityFX.MqttBenchmark.Campaign;

namespace EntityFX.MqttBenchmark.Tests;

[TestClass]
public class CampaignCoreTests
{
    [TestMethod]
    public void Matrix_Contains288UniqueKeysAndStableSeededOrderRegardlessOfInputOrder()
    {
        var config = new CampaignDefinition();
        var first = config.Expand().Select(x => x.Key).ToArray();
        Assert.AreEqual(288, first.Length);
        Assert.AreEqual(288, first.Distinct().Count());
        config.Brokers = config.Brokers.Reverse().ToArray();
        config.Publishers = config.Publishers.Reverse().ToArray();
        CollectionAssert.AreEqual(first, config.Expand().Select(x => x.Key).ToArray());
        Assert.AreEqual(72, config.Expand().Count(x => x.Broker == "Aedes"));
        Assert.AreEqual(3, config.Expand().Count(x => x.Broker == "EMQX" && x.MessageBytes == 256 && x.Qos == 2 && x.Publishers == 128));
        config.Publishers = new[] { 1, 2, 64, 128 };
        Assert.ThrowsException<InvalidDataException>(() => config.Expand());
    }

    [TestMethod]
    public void Payload_RecoversDeterministicUniqueIdsEvenIn16Bytes()
    {
        var ids = new HashSet<string>();
        foreach (var campaign in new[] { "one", "two" })
        foreach (var run in new[] { "attempt-01", "attempt-02" })
        foreach (var publisher in new[] { 0, 1, 127 })
        foreach (var sequence in Enumerable.Range(0, 100))
        {
            var small = IdentifiedPayload.Create(16, campaign, run, publisher, sequence);
            var large = IdentifiedPayload.Create(256, campaign, run, publisher, sequence);
            Assert.AreEqual(16, small.Length);
            Assert.AreEqual(256, large.Length);
            CollectionAssert.AreEqual(small, IdentifiedPayload.Create(16, campaign, run, publisher, sequence));
            Assert.AreEqual(IdentifiedPayload.ReadId(small), IdentifiedPayload.ReadId(large));
            Assert.IsTrue(ids.Add(IdentifiedPayload.ReadId(small)));
        }
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => IdentifiedPayload.Create(15, "one", "run", 0, 1));
    }

    [TestMethod]
    public void Accounting_SeparatesDuplicatesUnknownAndFailedPublishDeliveriesIncludingReceiptRace()
    {
        var ledger = new DeliveryLedger();
        ledger.Attempt("a"); ledger.Attempt("b"); ledger.Attempt("c");
        ledger.Deliver("a", 1); ledger.Deliver("a", 2);
        ledger.Deliver("b", 3); // Receipt may precede PUBACK or a failed publish result.
        ledger.Deliver("unknown", 4); ledger.Deliver("unknown", 5);
        ledger.Complete("a", 8); ledger.Fail("b", "transport"); ledger.Complete("c", 12);
        var row = ledger.Snapshot(0, 2);
        Assert.AreEqual(3L, row.AttemptedPublishes);
        Assert.AreEqual(2L, row.CompletedPublishes);
        Assert.AreEqual(1L, row.FailedPublishes);
        Assert.AreEqual(2L, row.UniqueDeliveries);
        Assert.AreEqual(2L, row.DuplicateDeliveries);
        Assert.AreEqual(2L, row.UnexpectedDeliveries);
        Assert.AreEqual(1L, row.DeliveredAfterFailedPublish);
        Assert.AreEqual(1L, row.CompletedIdsDelivered);
        Assert.AreEqual(1.5, row.AttemptedRps);
        Assert.AreEqual(1.0, row.CompletedRps);
        Assert.AreEqual(1.0 / 3, row.PublishFailureRate, 1e-12);
        Assert.AreEqual(0.5, row.DeliveryLossRate);
        Assert.IsNull(row.PublishLatencyMs);
        Assert.AreEqual("notApplicable", row.LatencyStatus);
        Assert.AreEqual(1L, row.ErrorReasons["transport"]);
        var acknowledged = ledger.Snapshot(1, 4);
        Assert.AreEqual(0.5, acknowledged.CompletedRps);
        Assert.AreEqual(12.0, acknowledged.PublishLatencyMs!.MaxMs);
    }

    [TestMethod]
    public void Drain_StopsForAllCompletedIdsQuietPeriodOrTimeout()
    {
        var ledger = new DeliveryLedger();
        ledger.Attempt("id"); ledger.Complete("id", 1);
        Assert.IsNull(DrainPolicy.Decide(ledger, 0, 1.9, 2, 30));
        Assert.AreEqual("quietPeriod", DrainPolicy.Decide(ledger, 0, 2, 2, 30));
        ledger.Deliver("unknown", 29.9);
        Assert.AreEqual("timeout", DrainPolicy.Decide(ledger, 0, 30, 2, 30));
        ledger.Deliver("id", 30);
        Assert.AreEqual("allCompletedDelivered", DrainPolicy.Decide(ledger, 0, 30, 2, 30));
        Assert.ThrowsException<InvalidOperationException>(() => ledger.Complete("id", 1));
    }
}
