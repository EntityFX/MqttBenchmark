using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace EntityFX.MqttBenchmark.Campaign;

public sealed record AdapterHardwareEvidence(int InterfaceIndex, string InterfaceId, bool? HardwareInterface,
    bool? Virtual, string Source, DateTimeOffset ObservedUtc, string? Error = null);
public interface IWindowsAdapterMetadataProvider
{
    AdapterHardwareEvidence Read(int interfaceIndex);
}
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
public sealed class LoadGeneratorHardwareException : IOException
{
    public LoadGeneratorInterface Network { get; }
    public LoadGeneratorHardwareException(LoadGeneratorInterface network) : base("Routed adapter is not proven physical hardware.") => Network = network;
}

public sealed class WindowsLoadGeneratorCounters : ILoadGeneratorCounters
{
    private readonly IPAddress destination;
    public LoadGeneratorInterface Network { get; }

    public WindowsLoadGeneratorCounters(Uri endpoint, IWindowsAdapterMetadataProvider? metadataProvider = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Matrix load-generator guard requires Windows system counters.");
        // GetSystemTimes covers the calling processor group, not every group on a >64-CPU host.
        if (GetActiveProcessorGroupCount() != 1) throw new InvalidDataException("Whole-system CPU coverage requires a single Windows processor group.");
        var addresses = IPAddress.TryParse(endpoint.Host, out var literal) ? new[] { literal } : Dns.GetHostAddresses(endpoint.Host).Distinct().ToArray();
        if (addresses.Length != 1) throw new InvalidDataException("Load-generator guard requires an unambiguous broker address; use its IP endpoint.");
        destination = addresses[0];
        if (IPAddress.IsLoopback(destination)) throw new InvalidDataException("A loopback route cannot establish remote load-generator network capacity.");
        Network = SelectInterface(RouteIndex(), AvailableInterfaces().Select(Describe).ToArray(), metadataProvider ?? new WindowsAdapterMetadataProvider());
    }

    public LoadGeneratorCounters Read()
    {
        if (RouteIndex() != Network.Index) throw new IOException("Broker route changed during load-generator sampling.");
        var adapter = AvailableInterfaces().SingleOrDefault(x => x.Id == Network.Id)
            ?? throw new IOException("Routed network interface disappeared or is down.");
        if (adapter.Speed != Network.LinkSpeedBitsPerSecond) throw new IOException("Routed network link capacity changed.");
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) throw new Win32Exception(Marshal.GetLastWin32Error(), "GetSystemTimes failed.");
        var statistics = adapter.GetIPStatistics();
        if (statistics.BytesReceived < 0 || statistics.BytesSent < 0) throw new InvalidDataException("Negative network byte counters.");
        return new(ToUInt64(idle), ToUInt64(kernel), ToUInt64(user), (ulong)statistics.BytesReceived,
            (ulong)statistics.BytesSent, adapter.Speed);
    }

    public static LoadGeneratorInterface SelectInterface(int routeIndex, IReadOnlyList<LoadGeneratorInterface> interfaces,
        IWindowsAdapterMetadataProvider? metadataProvider = null)
    {
        var matches = interfaces.Where(x => x.Index == routeIndex).ToArray();
        if (matches.Length != 1 || matches[0].LinkSpeedBitsPerSecond <= 0 || matches[0].InterfaceType is "Loopback" or "Tunnel")
            throw new InvalidDataException("OS-selected route has no unique usable interface/link capacity.");
        var selected = matches[0];
        AdapterHardwareEvidence? hardware = selected.HardwareEvidence;
        try { if (metadataProvider != null) hardware = metadataProvider.Read(routeIndex); }
        catch (Exception error)
        {
            hardware = new(routeIndex, selected.Id, null, null, WindowsAdapterMetadataProvider.MetadataSource,
                DateTimeOffset.UtcNow, error.GetType().Name + ": " + error.Message);
        }
        hardware ??= new(routeIndex, selected.Id, null, null, "unavailable", DateTimeOffset.UtcNow, "Hardware metadata is missing.");
        selected = selected with { HardwareEvidence = hardware };
        if (hardware.HardwareInterface != true || hardware.Virtual != false || hardware.Error != null ||
            hardware.InterfaceIndex != selected.Index || !Guid.TryParse(hardware.InterfaceId, out var metadataId) ||
            !Guid.TryParse(selected.Id, out var selectedId) || metadataId != selectedId)
            throw new LoadGeneratorHardwareException(selected);
        return selected;
    }
    private IEnumerable<NetworkInterface> AvailableInterfaces() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(x => x.OperationalStatus == OperationalStatus.Up && x.Supports(destination.AddressFamily == AddressFamily.InterNetwork
            ? NetworkInterfaceComponent.IPv4 : NetworkInterfaceComponent.IPv6));
    private LoadGeneratorInterface Describe(NetworkInterface adapter)
    {
        var properties = adapter.GetIPProperties();
        var index = destination.AddressFamily == AddressFamily.InterNetwork ? properties.GetIPv4Properties().Index : properties.GetIPv6Properties().Index;
        return new(adapter.Id, adapter.Name, adapter.Description, index, adapter.Speed, destination.ToString(), adapter.NetworkInterfaceType.ToString());
    }
    private int RouteIndex()
    {
        var ipv4 = destination.AddressFamily == AddressFamily.InterNetwork;
        var address = new byte[ipv4 ? 16 : 28];
        BitConverter.GetBytes((ushort)(ipv4 ? 2 : 23)).CopyTo(address, 0); // Windows AF_INET / AF_INET6.
        destination.GetAddressBytes().CopyTo(address, ipv4 ? 4 : 8);
        if (!ipv4) BitConverter.GetBytes((uint)destination.ScopeId).CopyTo(address, 24);
        var error = GetBestInterfaceEx(address, out var index);
        if (error != 0) throw new Win32Exception((int)error, "Cannot resolve broker route.");
        return checked((int)index);
    }
    private static ulong ToUInt64(System.Runtime.InteropServices.ComTypes.FILETIME value) =>
        ((ulong)(uint)value.dwHighDateTime << 32) | (uint)value.dwLowDateTime;
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out System.Runtime.InteropServices.ComTypes.FILETIME idle,
        out System.Runtime.InteropServices.ComTypes.FILETIME kernel, out System.Runtime.InteropServices.ComTypes.FILETIME user);
    [DllImport("kernel32.dll")]
    private static extern ushort GetActiveProcessorGroupCount();
    [DllImport("iphlpapi.dll")]
    private static extern uint GetBestInterfaceEx([In] byte[] address, out uint interfaceIndex);
}
