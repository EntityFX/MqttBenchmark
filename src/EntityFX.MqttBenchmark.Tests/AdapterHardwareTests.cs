using EntityFX.MqttBenchmark.Campaign;

namespace EntityFX.MqttBenchmark.Tests;

[TestClass]
public class AdapterHardwareTests
{
    // Read-only Get-NetAdapter -IncludeHidden metadata captured on the controller, 2026-09-10.
    private static readonly LoadGeneratorInterface Physical = new("{D8909FF0-176D-4CC5-A372-3F3D376C37ED}", "Ethernet 9",
        "Intel(R) 82579LM Gigabit Network Connection", 39, 1_000_000_000, "10.10.157.111", "Ethernet");
    private static readonly LoadGeneratorInterface VirtualBox = new("{7FDD94A0-C712-470F-870B-38F5AB1E27D3}", "Ethernet 12",
        "VirtualBox Host-Only Ethernet Adapter", 26, 1_000_000_000, "192.168.56.2", "Ethernet");
    private static AdapterHardwareEvidence Evidence(LoadGeneratorInterface nic, bool? hardware, bool? virtualAdapter) =>
        new(nic.Index, nic.Id, hardware, virtualAdapter, "MSFT_NetAdapter/Get-NetAdapter", DateTimeOffset.Parse("2026-09-10T07:00:00Z"));

    [DataTestMethod]
    [DataRow("VirtualBox")] [DataRow("Hyper-V")] [DataRow("TAP")] [DataRow("unknown")]
    [DataRow("query-error")] [DataRow("wrong-index")] [DataRow("wrong-guid")] [DataRow("contradictory")]
    public void HardwareAdmissionRejectsVirtualUnknownOrUnmatchedMetadata(string defect)
    {
        var nic = defect == "VirtualBox" ? VirtualBox : Physical;
        var metadata = Evidence(nic, true, false);
        switch (defect)
        {
            case "VirtualBox": metadata = Evidence(nic, false, true); break;
            case "Hyper-V": nic = Physical with { Name = "vEthernet (Default Switch)", Description = "Hyper-V Virtual Ethernet Adapter" }; metadata = Evidence(nic, false, true); break;
            case "TAP": nic = Physical with { Name = "Ethernet", Description = "TAP-Windows Adapter V9" }; metadata = Evidence(nic, false, true); break;
            case "unknown": metadata = Evidence(nic, null, null); break;
            case "wrong-index": metadata = metadata with { InterfaceIndex = 26 }; break;
            case "wrong-guid": metadata = metadata with { InterfaceId = VirtualBox.Id }; break;
            case "contradictory": metadata = metadata with { Virtual = true }; break;
        }
        var provider = new MetadataProvider(metadata, defect == "query-error");
        var error = Assert.ThrowsException<LoadGeneratorHardwareException>(() => WindowsLoadGeneratorCounters.SelectInterface(nic.Index, new[] { nic }, provider));
        Assert.IsNotNull(error.Network.HardwareEvidence, "Rejected hardware provenance must survive admission failure.");
    }

    [TestMethod]
    public void PhysicalHardwarePassesWithEvidence_IndependentOfAdapterName()
    {
        var renamed = Physical with { Name = "vEthernet-looking custom name" };
        var evidence = Evidence(Physical, true, false);
        var selected = WindowsLoadGeneratorCounters.SelectInterface(39, new[] { VirtualBox, renamed }, new MetadataProvider(evidence));
        Assert.AreEqual(evidence, selected.HardwareEvidence);
        Assert.AreEqual(1_000_000_000L, selected.LinkSpeedBitsPerSecond);
    }

    [TestMethod]
    public void MissingHardwareEvidenceIsNotPhysicalByDefault()
    {
        Assert.ThrowsException<LoadGeneratorHardwareException>(() => WindowsLoadGeneratorCounters.SelectInterface(39, new[] { Physical }));
    }

    [TestMethod]
    public void RejectedHardwareIsPreservedInImmutableFailureSummary()
    {
        using var temp = new CampaignTemp();
        Assert.ThrowsException<LoadGeneratorHardwareException>(() => LoadGeneratorGuard.Start(new Uri("mqtt://192.168.56.2:1883"), temp.Path,
            _ => throw new LoadGeneratorHardwareException(VirtualBox with { HardwareEvidence = Evidence(VirtualBox, false, true) })));
        var summary = CampaignJson.Read<LoadGeneratorReport>(Path.Combine(temp.Path, "load-generator-summary.json"));
        Assert.IsFalse(summary.Assessment.Success);
        Assert.IsNotNull(summary.Network, "Rejected adapter evidence must be retained, not just an error string.");
        Assert.AreEqual(false, summary.Network.HardwareEvidence!.HardwareInterface);
        Assert.AreEqual(true, summary.Network.HardwareEvidence.Virtual);
    }

    [DataTestMethod]
    [DataRow(39, "{D8909FF0-176D-4CC5-A372-3F3D376C37ED}", true, false)]
    [DataRow(26, "{7FDD94A0-C712-470F-870B-38F5AB1E27D3}", false, true)]
    public void WindowsMetadataProviderReadsActualNetAdapterShape(int index, string id, bool hardware, bool virtualAdapter)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new[] { new { InterfaceIndex = index, InterfaceGuid = id,
            HardwareInterface = hardware, Virtual = virtualAdapter } });
        var evidence = new WindowsAdapterMetadataProvider(_ => json).Read(index);
        Assert.AreEqual(hardware, evidence.HardwareInterface);
        Assert.AreEqual(virtualAdapter, evidence.Virtual);
        Assert.AreEqual(id, evidence.InterfaceId);
        Assert.AreEqual(index, evidence.InterfaceIndex);
        StringAssert.Contains(evidence.Source, "MSFT_NetAdapter");
    }

    [DataTestMethod]
    [DataRow("[]")] [DataRow("[{},{}]")] [DataRow("{\"HardwareInterface\":true}")] [DataRow("not-json")]
    public void WindowsMetadataProviderRejectsMissingAmbiguousOrMalformedQuery(string json)
    {
        Assert.ThrowsException<InvalidDataException>(() => new WindowsAdapterMetadataProvider(_ => json).Read(39));
    }

    private sealed class MetadataProvider : IAdapterMetadataProvider
    {
        private readonly AdapterHardwareEvidence metadata;
        private readonly bool fail;
        public MetadataProvider(AdapterHardwareEvidence metadata, bool fail = false) => (this.metadata, this.fail) = (metadata, fail);
        public AdapterHardwareEvidence Read(int interfaceIndex) => fail ? throw new IOException("Adapter metadata query denied.") : metadata;
    }
}
