using System.Text;
using System.Text.Json;

namespace EntityFX.MqttBenchmark.Campaign;

/// <summary>
/// Построчная запись NDJSON-событий (attempt/completed/failed, deliveries) в неизменяемый файл.
/// Потокобезопасно; открывается только в режиме CreateNew.
/// </summary>
public sealed class CampaignEventLog : IDisposable
{
    private readonly StreamWriter writer;
    private readonly object gate = new();
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public CampaignEventLog(string path) =>
        writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));

    public void Write<T>(T value)
    {
        lock (gate) writer.WriteLine(JsonSerializer.Serialize(value, Json));
    }

    public void Dispose()
    {
        lock (gate) writer.Dispose();
    }
}