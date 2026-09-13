using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;

namespace EntityFX.MqttBenchmark.Campaign;

/// <summary>
/// Счётчики load-generator guard'а для same-host стенда (брокер на loopback, например
/// нативный Mosquitto на 127.0.0.1). Кроссплатформенная реализация: CPU читается из
/// системных счётчиков ОС (Windows <c>GetSystemTimes</c> / Linux <c>/proc/stat</c>),
/// а сетевой гейт информационный: loopback не имеет физической полосы пропускания
/// (<see cref="Network"/>.LinkSpeedBitsPerSecond == 0), поэтому enforce-порог сети
/// должен быть отключён (<c>null</c>) — <see cref="LoadGeneratorAssessment"/> в этом
/// режиме отклоняет кампании с включённым сетевым гейтом.
/// </summary>
public sealed class SameHostLoadGeneratorCounters : ILoadGeneratorCounters
{
    /// <summary>Поразрядный множитель тиков Linux (USER_HZ=100) в единицах 100 нс: 10 мс = 100 000 × 100 нс.</summary>
    internal const long UnixTicksPer100Ns = 100_000;

    private readonly Uri endpoint;

    public LoadGeneratorInterface Network { get; }

    /// <summary>
    /// Создаёт same-host счётчики. Брокер обязан быть на loopback-адресе:
    /// любой небродкастный хост требует полноценной маршрутной реализации.
    /// </summary>
    /// <exception cref="InvalidDataException">Хост не является loopback.</exception>
    public SameHostLoadGeneratorCounters(Uri endpoint)
    {
        this.endpoint = endpoint;
        if (IPAddress.TryParse(endpoint.Host, out var literal)
            ? !IPAddress.IsLoopback(literal)
            : !string.Equals(endpoint.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Same-host counters require a loopback broker endpoint (127.0.0.1 / ::1 / localhost).");
        Network = new LoadGeneratorInterface(
            "same-host", "loopback",
            "Same-host broker over loopback; network gate is informational (no physical link capacity)",
            0, 0, endpoint.ToString(), "Loopback",
            new AdapterHardwareEvidence(0, "same-host", true, false, "same-host-loopback", DateTimeOffset.UtcNow));
    }

    /// <inheritdoc cref="ILoadGeneratorCounters.Read"/>
    public LoadGeneratorCounters Read()
    {
        var (idle, kernel, user) = OperatingSystem.IsWindows() ? ReadWindowsSystemCpu100Ns() : ReadUnixSystemCpu100Ns();
        // Loopback byte counters are not required for the assessment (link capacity is zero),
        // so a stable zero pair is honest and cross-platform.
        return new LoadGeneratorCounters(idle, kernel, user, 0, 0, 0);
    }

    /// <summary>
    /// Системный CPU Windows в единицах 100 нс (idle, kernel, user) через <c>GetSystemTimes</c>.
    /// Кросспроцессная монотонность гарантирована ядром.
    /// </summary>
    internal static (ulong idle, ulong kernel, ulong user) ReadWindowsSystemCpu100Ns()
    {
        if (GetActiveProcessorGroupCount() != 1)
            throw new InvalidDataException("Whole-system CPU coverage requires a single Windows processor group.");
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetSystemTimes failed.");
        return (ToUInt64(idle), ToUInt64(kernel), ToUInt64(user));
    }

    /// <summary>
    /// Системный CPU Linux в единицах 100 нс (idle, kernel, user) по агрегированной строке
    /// <c>/proc/stat</c>. Гостевое время (guest/guest_nice) уже учтено в user/system — двойной
    /// учёт исключён.
    /// </summary>
    internal static (ulong idle, ulong kernel, ulong user) ReadUnixSystemCpu100Ns()
    {
        var line = File.ReadLines("/proc/stat").FirstOrDefault(x => x.StartsWith("cpu ", StringComparison.Ordinal))
            ?? throw new InvalidDataException("Aggregated /proc/stat cpu line is missing.");
        return ParseProcStatCpu(line);
    }

    /// <summary>Чистый парсер строки <c>/proc/stat</c>: ticks → 100 нс (idle, kernel, user).</summary>
    public static (ulong idle, ulong kernel, ulong user) ParseProcStatCpu(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 8) throw new InvalidDataException("Invalid /proc/stat cpu line: " + line);
        long Value(int index)
        {
            if (!long.TryParse(parts[index], out var value)) throw new InvalidDataException("Invalid /proc/stat cpu field: " + parts[index]);
            return value;
        }
        var user = (Value(1) + Value(2)) * UnixTicksPer100Ns;                       // user + nice
        var system = (Value(3) + Value(6) + Value(7) + (parts.Length > 8 ? Value(8) : 0)) * UnixTicksPer100Ns; // system + irq + softirq + steal
        var idle = (Value(4) + Value(5)) * UnixTicksPer100Ns;                     // idle + iowait
        return ((ulong)idle, (ulong)system, (ulong)user);
    }

    private static ulong ToUInt64(System.Runtime.InteropServices.ComTypes.FILETIME value) =>
        ((ulong)(uint)value.dwHighDateTime << 32) | (uint)value.dwLowDateTime;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out System.Runtime.InteropServices.ComTypes.FILETIME idle,
        out System.Runtime.InteropServices.ComTypes.FILETIME kernel, out System.Runtime.InteropServices.ComTypes.FILETIME user);

    [DllImport("kernel32.dll")]
    private static extern ushort GetActiveProcessorGroupCount();
}