using System.Text.Json.Nodes;
using EntityFX.MqttBenchmark.Campaign;

namespace EntityFX.MqttBenchmark.Tests;

[TestClass]
public class StandTelemetryScopeTests
{
    private static JsonNode Sample(string cpuScope, string networkScope) =>
        JsonNode.Parse($"{{\"cpuScope\":\"{cpuScope}\",\"networkScope\":\"{networkScope}\"}}")!;

    private static void Pass(Action action) => action();

    [TestMethod]
    public void ContainerTelemetryRequiresContainerCgroupCpuScope()
    {
        var samples = new[] { Sample(StandTelemetry.ContainerCpuScope, StandTelemetry.NetworkScope) };
        Pass(() => StandTelemetry.ValidateSamples(samples, 1, StandTelemetry.ContainerCpuScope));
        Assert.ThrowsException<InvalidDataException>(() => StandTelemetry.ValidateSamples(
            new[] { Sample(StandTelemetry.NativeCpuScope, StandTelemetry.NetworkScope) }, 1, StandTelemetry.ContainerCpuScope));
    }

    [TestMethod]
    public void NativeTelemetryRequiresProcessPinnedCpuScope()
    {
        var samples = new[] { Sample(StandTelemetry.NativeCpuScope, StandTelemetry.NetworkScope) };
        Pass(() => StandTelemetry.ValidateSamples(samples, 1, StandTelemetry.NativeCpuScope));
        Assert.ThrowsException<InvalidDataException>(() => StandTelemetry.ValidateSamples(
            new[] { Sample(StandTelemetry.ContainerCpuScope, StandTelemetry.NetworkScope) }, 1, StandTelemetry.NativeCpuScope));
        Assert.ThrowsException<InvalidDataException>(() => StandTelemetry.ValidateSamples(
            new[] { Sample(StandTelemetry.NativeCpuScope, "container-cgroup") }, 1, StandTelemetry.NativeCpuScope));
    }

    [TestMethod]
    public void ScopesAreMappedFromTheBrokerRuntime()
    {
        Assert.AreEqual(StandTelemetry.ContainerCpuScope, StandTelemetry.CpuScopeForRuntime(null));
        Assert.AreEqual(StandTelemetry.ContainerCpuScope, StandTelemetry.CpuScopeForRuntime("container"));
        Assert.AreEqual(StandTelemetry.NativeCpuScope, StandTelemetry.CpuScopeForRuntime("native"));
        Assert.AreEqual(StandTelemetry.NativeCpuScope, StandTelemetry.CpuScopeForRuntime("NATIVE"));
    }

    [TestMethod]
    public void IncompleteCaptureFailsClosed()
    {
        var samples = new[] { Sample(StandTelemetry.NativeCpuScope, StandTelemetry.NetworkScope) };
        Assert.ThrowsException<InvalidDataException>(() => StandTelemetry.ValidateSamples(samples, 2, StandTelemetry.NativeCpuScope));
    }
}