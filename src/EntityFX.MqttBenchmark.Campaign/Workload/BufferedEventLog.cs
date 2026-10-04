using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace EntityFX.MqttBenchmark.Campaign;

/// <summary>
/// NDJSON-журнал, сериализующий запись в вызывающем потоке, но выполняющий дисковую запись
/// в фоновом. Нужен там, где журналирование вызывается из callback'а сетевого приёмника:
/// синхронная запись на диск под замком ограничивает приёмник пропускной способностью
/// диска (~1 тыс. записей/с), и измеренная «потеря доставки» становится характеристикой
/// стенда, а не брокера.
///
/// Буфер неограничен: потеря записей недопустима, поскольку журнал — доказательство,
/// хешируемое при запечатывании попытки. Порядок записей сохраняется.
/// </summary>
public sealed class BufferedEventLog : IDisposable, IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly Channel<string> queue = Channel.CreateUnbounded<string>(new() { SingleReader = true });
    private readonly StreamWriter writer;
    private readonly Task pump;
    private int disposed;

    public BufferedEventLog(string path)
    {
        writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
        pump = Task.Run(DrainAsync);
    }

    /// <summary>
    /// Сериализует и ставит запись в очередь. Не выполняет дисковых операций и не блокируется
    /// на них, поэтому безопасен для вызова из сетевого callback'а.
    /// </summary>
    public void Write<T>(T value)
    {
        if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException(nameof(BufferedEventLog));
        queue.Writer.TryWrite(JsonSerializer.Serialize(value, Json));
    }

    /// <summary>Дожидается, пока все поставленные в очередь записи попадут в файл.</summary>
    public Task FlushAsync()
    {
        if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException(nameof(BufferedEventLog));
        queue.Writer.TryComplete();
        return pump;
    }

    private async Task DrainAsync()
    {
        try
        {
            await foreach (var line in queue.Reader.ReadAllAsync().ConfigureAwait(false))
                await writer.WriteLineAsync(line).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }
        finally { writer.Dispose(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        queue.Writer.TryComplete();
        await pump.ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        queue.Writer.TryComplete();
        try { pump.GetAwaiter().GetResult(); } catch (ObjectDisposedException) { }
    }
}