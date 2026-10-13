using System.Net;
using EntityFX.MqttBenchmark.Campaign;

namespace EntityFX.MqttBenchmark.Tests;

[TestClass]
public class UnixLoadGeneratorCountersTests
{
    [TestMethod]
    public void TabSeparatedRouteTable_SelectsSpecificRouteOrDefaultGateway()
    {
        var routes = new[] {
            "eth0\t00000000\t010200C0\t0003\t0\t0\t100\t00000000\t0\t0\t0",
            "eth1\t000AA8C0\t00000000\t0001\t0\t0\t50\t00FFFFFF\t0\t0\t0" };
        Assert.AreEqual("eth1", UnixLoadGeneratorCounters.ResolveRouteInterface(IPAddress.Parse("192.168.10.5"), routes));
        Assert.AreEqual("eth0", UnixLoadGeneratorCounters.ResolveRouteInterface(IPAddress.Parse("203.0.113.5"), routes));
        Assert.ThrowsException<InvalidDataException>(() => UnixLoadGeneratorCounters.ResolveRouteInterface(
            IPAddress.Parse("203.0.113.5"), routes.Skip(1)));
    }

    [TestMethod]
    public void SysfsLinkSpeed_IsMegabitsAndRejectsUnavailableCapacity()
    {
        Assert.AreEqual(100_000_000L, UnixLoadGeneratorCounters.ParseLinkSpeedBits("100", "eth0"));
        Assert.AreEqual(1_000_000_000L, UnixLoadGeneratorCounters.ParseLinkSpeedBits("1000", "eth0"));
        foreach (var invalid in new[] { "-1", "0", "unknown" })
            Assert.ThrowsException<InvalidDataException>(() => UnixLoadGeneratorCounters.ParseLinkSpeedBits(invalid, "eth0"));
    }
}
