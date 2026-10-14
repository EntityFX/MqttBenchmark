using System.Diagnostics;
using EntityFX.MqttBenchmark.Campaign;

namespace EntityFX.MqttBenchmark.Tests;

[TestClass]
public class ExternalGuardSamplerTests
{
    private static string CliPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName,"EntityFX.MqttBenchmark.Cli"))) directory=directory.Parent;
        var configuration = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.Name;
        return Path.Combine(directory!.FullName,"EntityFX.MqttBenchmark.Cli","bin",configuration,"net6.0","EntityFX.MqttBenchmark.Cli.dll");
    }

    [TestMethod]
    public async Task SeparateProcessBracketsTheControllerMonotonicWindow()
    {
        var directory=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));
        try
        {
            await using var guard=LoadGeneratorGuard.Start(new Uri("mqtt://127.0.0.1:1883"),directory,
                networkThresholdPercent:null,cpuThresholdPercent:100,maximumIntervalSeconds:5,externalSamplerCli:CliPath());
            var ready=CampaignJson.Read<ExternalSamplerReady>(Path.Combine(directory,"external-sampler-ready.json"));
            Assert.AreNotEqual(Environment.ProcessId,ready.ProcessId);
            Assert.AreEqual(Environment.ProcessId,ready.ParentProcessId);
            Assert.AreEqual(Stopwatch.Frequency,ready.TimestampFrequency);
            guard.MeasurementStarted(Stopwatch.GetTimestamp(),DateTimeOffset.UtcNow);
            await Task.Delay(2200);
            guard.MeasurementEnded(Stopwatch.GetTimestamp(),DateTimeOffset.UtcNow);
            await guard.CompleteAsync();
            var report=CampaignJson.Read<LoadGeneratorReport>(Path.Combine(directory,"load-generator-summary.json"));
            Assert.IsTrue(report.Assessment.Success);
            Assert.IsTrue(report.Assessment.Intervals.Count>=2);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory,true); }
    }

    [TestMethod]
    public async Task KilledSamplerFailsGuardAndPreservesPartialSamples()
    {
        var directory=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));
        try
        {
            await using var guard=LoadGeneratorGuard.Start(new Uri("mqtt://127.0.0.1:1883"),directory,
                networkThresholdPercent:null,cpuThresholdPercent:100,maximumIntervalSeconds:5,externalSamplerCli:CliPath());
            guard.MeasurementStarted(Stopwatch.GetTimestamp(),DateTimeOffset.UtcNow);
            var ready=CampaignJson.Read<ExternalSamplerReady>(Path.Combine(directory,"external-sampler-ready.json"));
            using var worker=Process.GetProcessById(ready.ProcessId);worker.Kill();worker.WaitForExit();
            guard.MeasurementEnded(Stopwatch.GetTimestamp(),DateTimeOffset.UtcNow);
            await Assert.ThrowsExceptionAsync<InvalidDataException>(()=>guard.CompleteAsync());
            var report=CampaignJson.Read<LoadGeneratorReport>(Path.Combine(directory,"load-generator-summary.json"));
            Assert.IsFalse(report.Assessment.Success);
            Assert.IsTrue(File.Exists(Path.Combine(directory,"load-generator-samples.ndjson")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory,true); }
    }
}
