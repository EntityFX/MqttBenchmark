using System.Text;
using System.Text.RegularExpressions;

namespace EntityFX.MqttBenchmark.Campaign;

/// <summary>
/// Интерактивный монитор кампании: компактный дашборд в консоли (общий и по-брокерный прогресс,
/// сводка по брокерам, ETA, текущий ключ), перерисовываемый раз в секунду. Смысл статусов
/// и классификация отказов совпадают со скриптом scripts/ExtractRunStats.ps1:
/// success / in-progress / failed, где "warned" — failure из-за clock alignment
/// (предупреждение), a failure из-за NIC sampling gap — reject.
/// Разобранная result.json кэшируется в памяти по (путь, время записи), поэтому повторные
/// обходы перечитывают только изменившиеся файлы.
/// </summary>
public sealed class CampaignMonitor
{
    private static readonly Regex ClockFailure = new(
        "clock alignment is unready|Stand controller clock failed|clock-alignment", RegexOptions.Compiled);
    private static readonly Regex NicFailure = new(
        "sampling gap|counter reset/wrap|Load-generator guard", RegexOptions.Compiled);

    private readonly string campaignsRoot;
    private readonly string prefix;
    private readonly int totalPlanned;
    private readonly int keysPerBroker;
    private readonly DateTime runStart;
    private readonly Dictionary<string, (DateTime Time, AttemptResult? Result)> results = new();

    public CampaignMonitor(string campaignsRoot, string prefix, int totalPlanned, int keysPerBroker, DateTime runStart)
    {
        this.campaignsRoot = campaignsRoot;
        this.prefix = prefix;
        this.totalPlanned = totalPlanned;
        this.keysPerBroker = keysPerBroker;
        this.runStart = runStart;
    }

    public sealed record KeyRow(string Broker, string Key, int Attempts, string Status,
        int ClockFails, int NicFails, int OtherFails, double DurationSeconds,
        double? AttemptedRps, double? P50Ms, double? P95Ms, double? P99Ms, double? DeliveryLossRate)
    {
        public bool Warned => Status == "failed" && ClockFails > 0 && NicFails == 0 && OtherFails == 0;
    }

    public sealed record BrokerRow(string Broker, int Keys, int Success, int InProgress, int Failed, int Warned,
        int Attempts, int ClockFails, int NicFails,
        double? RpsAvg, double? P50Avg, double? P95Avg, double? P99Avg, double? LossAvg, string? CurrentKey)
    {
        public string DisplayStatus => $"{Success}/{InProgress}/{Failed - Warned}";
        public string WarnSuffix => Warned > 0 ? $"+{Warned}w" : "";
    }

    public sealed record MonitorSnapshot(string Prefix, int TotalPlanned, int KeysDone, int KeysStarted,
        DateTime RunStart, DateTime Now, double ElapsedMin, double EtaSec, DateTime EstFinish,
        IReadOnlyList<BrokerRow> Brokers, string? CurrentKey);

    public MonitorSnapshot Refresh()
    {
        var now = DateTime.Now;
        var brokers = new List<BrokerRow>();
        var rows = new List<KeyRow>();
        DateTime? earliestStart = null;
        foreach (var directory in Directory.EnumerateDirectories(campaignsRoot)
            .Where(x => Path.GetFileName(x).StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(x => x, StringComparer.Ordinal))
        {
            var broker = Path.GetFileName(directory)[prefix.Length..];
            var attemptsRoot = Path.Combine(directory, "attempts");
            var keyRows = new List<KeyRow>();
            if (Directory.Exists(attemptsRoot))
            {
                foreach (var keyDirectory in Directory.EnumerateDirectories(attemptsRoot).OrderBy(x => x, StringComparer.Ordinal))
                {
                    var keyName = Path.GetFileName(keyDirectory);
                    var attemptDirectories = Directory.EnumerateDirectories(keyDirectory).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                    int clock = 0, nic = 0, other = 0;
                    bool running = false;
                    var success = (AttemptResult?)null;
                    DateTime? firstStart = null;
                    foreach (var attempt in attemptDirectories)
                    {
                        var result = ReadResult(Path.Combine(attempt, "result.json"));
                        if (result == null) { running = true; continue; }
                        if (result.Status == "success")
                        {
                            success = result;
                            if (firstStart is null)
                                firstStart = FileTime(Path.Combine(attempt, "started.json")) ?? new DirectoryInfo(attempt).CreationTime;
                        }
                        else
                        {
                            var failure = result.Failure ?? string.Empty;
                            if (ClockFailure.IsMatch(failure)) clock++;
                            else if (NicFailure.IsMatch(failure)) nic++;
                            else other++;
                        }
                    }
                    if (firstStart is null && attemptDirectories.Length > 0)
                        firstStart = FileTime(Path.Combine(attemptDirectories[0], "started.json"))
                            ?? new DirectoryInfo(attemptDirectories[0]).CreationTime;
                    double duration = 0;
                    if (firstStart is not null)
                    {
                        earliestStart = earliestStart is null
                            ? firstStart.Value
                            : (earliestStart.Value < firstStart.Value ? earliestStart.Value : firstStart.Value);
                        duration = Math.Max(0d, (LatestTopLevelFileTime(attemptDirectories[^1]) - firstStart.Value).TotalSeconds);
                    }
                    var measurement = success?.Observation?.Measurement;
                    var latencies = measurement?.PublishLatencyMs;
                    keyRows.Add(new KeyRow(broker, keyName, attemptDirectories.Length,
                        success != null ? "success" : running ? "in-progress" : "failed",
                        clock, nic, other, duration,
                        measurement?.AttemptedRps, latencies?.P50Ms, latencies?.P95Ms, latencies?.P99Ms,
                        measurement?.DeliveryLossRate));
                }
            }
            rows.AddRange(keyRows);
            var successes = keyRows.Where(x => x.Status == "success").ToArray();
            double? Average(Func<KeyRow, double?> selector)
            {
                var values = successes.Select(selector).Where(x => x.HasValue).Select(x => x!.Value).ToArray();
                return values.Length > 0 ? values.Average() : null;
            }
            brokers.Add(new BrokerRow(broker, keyRows.Count, successes.Length,
                keyRows.Count(x => x.Status == "in-progress"),
                keyRows.Count(x => x.Status == "failed"),
                keyRows.Count(x => x.Warned),
                keyRows.Sum(x => x.Attempts), keyRows.Sum(x => x.ClockFails), keyRows.Sum(x => x.NicFails),
                Average(x => x.AttemptedRps), Average(x => x.P50Ms), Average(x => x.P95Ms), Average(x => x.P99Ms),
                Average(x => x.DeliveryLossRate),
                keyRows.FirstOrDefault(x => x.Status == "in-progress")?.Key));
        }
        var keysDone = rows.Count(x => x.Status == "success");
        var start = runStart == default ? (earliestStart ?? now) : runStart;
        var elapsed = Math.Max(0, (now - start).TotalSeconds);
        var perKey = rows.Count > 0 ? elapsed / rows.Count : 0;
        var eta = perKey * Math.Max(0, totalPlanned - keysDone);
        return new MonitorSnapshot(prefix, totalPlanned, keysDone, rows.Count, start, now, elapsed / 60, eta,
            start.AddSeconds(elapsed + eta), brokers,
            brokers.Select(x => x.CurrentKey).Where(x => x != null).Cast<string>().FirstOrDefault());
    }

    private AttemptResult? ReadResult(string path)
    {
        if (!File.Exists(path)) return null;
        var time = new FileInfo(path).LastWriteTime;
        if (results.TryGetValue(path, out var cached) && cached.Time == time) return cached.Result;
        AttemptResult? result;
        try { result = CampaignJson.Read<AttemptResult>(path); }
        catch { result = null; } // незапечатанный/чужой файл — попытка считается незавершённой
        results[path] = (time, result);
        return result;
    }

    private static DateTime? FileTime(string path) => File.Exists(path) ? new FileInfo(path).LastWriteTime : null;

    private static DateTime LatestTopLevelFileTime(string directory)
    {
        var latest = new DirectoryInfo(directory).CreationTime;
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var time = new FileInfo(file).LastWriteTime;
            if (time > latest) latest = time;
        }
        return latest;
    }

    public string Render(int width)
    {
        var snapshot = Refresh();
        width = Math.Clamp(width, 50, 100);
        var builder = new StringBuilder();
        builder.AppendLine($"MqttBenchmark monitor - {snapshot.Prefix} | {snapshot.Now:dd.MM.yyyy HH:mm:ss}");
        builder.AppendLine($"Run {snapshot.RunStart:dd.MM HH:mm} -> {snapshot.Now:HH:mm}   Elapsed {snapshot.ElapsedMin:F1} min   Keys {snapshot.KeysDone}/{snapshot.TotalPlanned} ({Percent(snapshot.KeysDone, snapshot.TotalPlanned)}%)");
        builder.AppendLine();
        builder.AppendLine(Bar(snapshot.KeysDone, snapshot.TotalPlanned, width - 14) + "  overall");
        foreach (var broker in snapshot.Brokers)
            builder.AppendLine(Bar(broker.Success, keysPerBroker, width - 34) + "  " + TrimTo(broker.Broker, width - 26));
        builder.AppendLine();
        var nameWidth = Math.Clamp(snapshot.Brokers.Select(x => x.Broker.Length).DefaultIfEmpty(12).Max(), 12, 24);
        builder.AppendLine("broker    succ/inf/failed        att clk/nic rps avg     p50/p95/p99 ms    loss");
        foreach (var broker in snapshot.Brokers)
        {
            builder.AppendLine(
                TrimTo(broker.Broker, nameWidth).PadRight(nameWidth) + "  " +
                (broker.DisplayStatus + broker.WarnSuffix).PadRight(11) + " " +
                broker.Attempts.ToString().PadLeft(3) + " " +
                broker.ClockFails + "/" + broker.NicFails.ToString().PadLeft(5) + " " +
                Rps(broker.RpsAvg).PadRight(11) + " " +
                Latency(broker.P50Avg, broker.P95Avg, broker.P99Avg).PadRight(16) + " " +
                Loss(broker.LossAvg));
        }
        builder.AppendLine();
        builder.AppendLine($"ETA ~{snapshot.EtaSec / 3600:F1} h (finish ~= {snapshot.EstFinish:HH:mm}, upper bound)");
        if (snapshot.CurrentKey != null)
            builder.AppendLine($"Now: {snapshot.CurrentKey} (in-progress)");
        return builder.ToString();
    }

    private static string Percent(int done, int total) => total <= 0 ? "0.0" : (100.0 * done / total).ToString("0.0");

    private static string Bar(int done, int total, int width)
    {
        width = Math.Max(6, width);
        if (total <= 0) return new string(' ', width + 8);
        var fraction = Math.Clamp((double)done / total, 0, 1);
        var filled = (int)Math.Round(fraction * width);
        return $"[{new string('#', filled)}{new string('-', width - filled)}] {done}/{total}  {fraction * 100:0.0}%";
    }

    private static string Rps(double? value) => value.HasValue ? value.Value.ToString("0.#") : "-";
    private static string Latency(double? p50, double? p95, double? p99) =>
        p50.HasValue && p95.HasValue && p99.HasValue ? $"{p50.Value:0.0}/{p95.Value:0.0}/{p99.Value:0.0}" : "-";
    private static string Loss(double? value) => value.HasValue ? value.Value.ToString("0.00") : "-";

    private static string TrimTo(string value, int maxWidth) =>
        value.Length <= maxWidth ? value : value[..Math.Max(0, maxWidth - 1)] + "...";

    /// <summary>
    /// Запуск: живая перерисовка только в интерактивной консоли; --once — разовый снапшот
    /// (работает и при перенаправленном выводе, пригоден для логирования/CI).
    /// </summary>
    public static async Task<int> RunAsync(IReadOnlyDictionary<string, string?> options, CancellationToken cancellationToken)
    {
        var campaigns = Required(options, "campaigns");
        if (!Directory.Exists(campaigns))
            throw new ArgumentException($"Campaigns root not found: '{campaigns}'.");
        var monitor = new CampaignMonitor(campaigns,
            Value(options, "prefix", "stand111-full-"),
            Positive(options, "total", 288),
            Positive(options, "per-broker", 72),
            options.TryGetValue("run-start", out var runStart) && !string.IsNullOrWhiteSpace(runStart)
                ? DateTime.Parse(runStart) : default);
        var interval = Positive(options, "interval", 1);
        var once = options.ContainsKey("once");
        var interactive = !Console.IsOutputRedirected;
        if (!interactive && !once)
            throw new ArgumentException("monitor live mode requires an interactive console (output is redirected). Use --once for a single snapshot.");
        if (once)
        {
            Console.WriteLine(monitor.Render(100));
            return 0;
        }
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        Console.CursorVisible = false;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int width;
                try { width = Console.WindowWidth; } catch { width = 80; }
                Console.Write("\u001b[2J\u001b[H");
                Console.Write(monitor.Render(width));
                await Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            Console.CursorVisible = true;
            Console.Write("\u001b[2J\u001b[H");
            Console.WriteLine("monitor stopped.");
        }
        return 0;
    }

    private static string Required(IReadOnlyDictionary<string, string?> options, string name) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value! : throw new ArgumentException($"Required option is missing: --{name}");

    private static string Value(IReadOnlyDictionary<string, string?> options, string name, string fallback) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value! : fallback;

    private static int Positive(IReadOnlyDictionary<string, string?> options, string name, int fallback)
    {
        if (!options.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value)) return fallback;
        if (!int.TryParse(value, out var parsed) || parsed <= 0)
            throw new ArgumentException($"Option --{name} must be a positive integer.");
        return parsed;
    }
}

