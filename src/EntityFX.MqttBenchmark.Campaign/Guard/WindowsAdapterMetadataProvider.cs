using System.Diagnostics;
using System.Text.Json;

namespace EntityFX.MqttBenchmark.Campaign;

public sealed class WindowsAdapterMetadataProvider : IWindowsAdapterMetadataProvider
{
    public const string MetadataSource = "MSFT_NetAdapter via Get-NetAdapter -IncludeHidden";
    private readonly Func<int, string> query;
    public WindowsAdapterMetadataProvider(Func<int, string>? query = null) => this.query = query ?? QueryWindows;
    public AdapterHardwareEvidence Read(int interfaceIndex)
    {
        var json = query(interfaceIndex);
        try
        {
            using var document = JsonDocument.Parse(json);
            var rows = document.RootElement;
            if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() != 1)
                throw new InvalidDataException("Expected exactly one MSFT_NetAdapter metadata record.");
            var row = rows[0];
            bool? Boolean(string key) => row.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean() : null;
            return new(row.GetProperty("InterfaceIndex").GetInt32(), row.GetProperty("InterfaceGuid").GetString() ?? "",
                Boolean("HardwareInterface"), Boolean("Virtual"), MetadataSource, DateTimeOffset.UtcNow);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new InvalidDataException("Invalid MSFT_NetAdapter metadata response.", error); }
    }
    private static string QueryWindows(int interfaceIndex)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Authoritative adapter hardware metadata requires Windows.");
        if (interfaceIndex <= 0) throw new InvalidDataException("Invalid routed interface index.");
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
            "$ErrorActionPreference = 'Stop'; ConvertTo-Json -Compress -InputObject @(Get-NetAdapter -IncludeHidden | " +
            "Where-Object InterfaceIndex -eq " + interfaceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            " | Select-Object InterfaceIndex,InterfaceGuid,HardwareInterface,Virtual)" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start Windows adapter metadata query.");
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10_000))
        {
            process.Kill(true); process.WaitForExit();
            Task.WhenAll(output, errors).GetAwaiter().GetResult();
            throw new TimeoutException("MSFT_NetAdapter query exceeded ten seconds.");
        }
        Task.WhenAll(output, errors).GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new IOException("MSFT_NetAdapter query failed: " + errors.Result.Trim());
        return output.Result;
    }
}
