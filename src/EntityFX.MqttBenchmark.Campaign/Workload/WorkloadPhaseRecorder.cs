using System.Text.Json;

namespace EntityFX.MqttBenchmark.Campaign;

/// <summary>Exact submission boundaries, independent of the last awaited publish.</summary>
internal sealed class WorkloadPhaseRecorder : IDisposable
{
    private readonly string path;
    private readonly CampaignEventLog connections;
    private readonly int qos;
    public double? Start { get; private set; }
    public double? End { get; private set; }
    public double? DrainEnd { get; private set; }
    public bool Completed { get; set; }

    public WorkloadPhaseRecorder(string directory, int qos)
    {
        path = Path.Combine(directory, "phases.json");
        this.qos = qos;
        connections = new CampaignEventLog(Path.Combine(directory, "connections.ndjson"));
    }

    public void Begin(double start, double seconds) { Start = start; End = start + seconds; }
    public void EndDrain(double seconds) => DrainEnd = seconds;
    public void Connection(string client, string state, double seconds, string? reason = null) =>
        connections.Write(new { client, state, elapsedSeconds = seconds, reason });

    public void Dispose()
    {
        connections.Dispose();
        using var output = new FileStream(path, FileMode.CreateNew);
        JsonSerializer.Serialize(output, new { timeOrigin = "sharedMonotonic", qos,
            measurementStartSeconds = Start, measurementEndSeconds = End,
            drainEndSeconds = DrainEnd, completed = Completed,
            policy = "closedLoopPerPublisher; fixed submission deadline; outcomes observed through drain" });
    }
}
