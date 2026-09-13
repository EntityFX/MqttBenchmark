using System.Net.NetworkInformation;

namespace EntityFX.MqttBenchmark.Campaign;

/// <summary>
/// Счётчики load-generator guard'а для Linux (remote broker, небродкастный хост):
/// CPU из <c>/proc/stat</c>, сеть из <c>/proc/net/dev</c>, скорость линка из sysfs.
/// Маршрут резолвится по таблице <c>/proc/net/route</c> (наиболее специфичная маска).
/// </summary>
public sealed class UnixLoadGeneratorCounters : ILoadGeneratorCounters
{
    private readonly Uri endpoint;
    public LoadGeneratorInterface Network { get; }
    private readonly string destination;

    /// <param name="endpoint">Адрес брокера (IPv4-хост, не loopback).</param>
    /// <exception cref="PlatformNotSupportedException">Платформа не Linux.</exception>
    /// <exception cref="InvalidDataException">Невозможно однозначно выбрать маршрут/интерфейс или линк без заявленной скорости.</exception>
    public UnixLoadGeneratorCounters(Uri endpoint)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Unix load-generator counters require Linux (route table + procfs).");
        this.endpoint = endpoint;
        if (!System.Net.IPAddress.TryParse(endpoint.Host, out var literal) || literal.GetAddressBytes().Length != 4)
            throw new InvalidDataException("Unix load-generator counters require an IPv4 broker endpoint.");
        destination = literal.ToString();
        Network = Select(ResolveRouteInterface(literal), literal);
    }

    /// <inheritdoc cref="ILoadGeneratorCounters.Read"/>
    public LoadGeneratorCounters Read()
    {
        // Route stability: the same interface must still cover the broker address.
        if (ResolveRouteInterface(System.Net.IPAddress.Parse(destination)) != Network.Name)
            throw new IOException("Broker route changed during load-generator sampling.");
        var (rx, tx) = ReadInterfaceBytes(Network.Name);
        var (idle, kernel, user) = SameHostLoadGeneratorCounters.ReadUnixSystemCpu100Ns();
        return new LoadGeneratorCounters(idle, kernel, user, (ulong)rx, (ulong)tx, Network.LinkSpeedBitsPerSecond);
    }

    /// <summary>Имя интерфейса, по которому Linux маршрутизирует адрес (наиболее специфичная маска).</summary>
    internal static string ResolveRouteInterface(System.Net.IPAddress destination)
    {
        var target = System.BitConverter.ToUInt32(destination.GetAddressBytes(), 0); // /proc/net/route: little-endian hex.
        var bestMask = 0u;
        var bestInterface = "";
        foreach (var line in File.ReadLines("/proc/net/route").Skip(1))
        {
            var fields = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 7) continue;
            if (!uint.TryParse(fields[1], System.Globalization.NumberStyles.HexNumber, null, out var routeDestination) ||
                !uint.TryParse(fields[6], System.Globalization.NumberStyles.HexNumber, null, out var mask))
                continue;
            if ((routeDestination & mask) != (target & mask)) continue;
            if (mask > bestMask) { bestMask = mask; bestInterface = fields[0]; }
        }
        if (bestMask == 0) throw new InvalidDataException($"No Linux route covers broker address {destination}.");
        return bestInterface;
    }
    /// <summary>Счётчики RX/TX байтов интерфейса из <c>/proc/net/dev</c>.</summary>
    internal static (long rx, long tx) ReadInterfaceBytes(string interfaceName)
    {
        foreach (var line in File.ReadLines("/proc/net/dev").Skip(2))
        {
            var separator = line.IndexOf(':');
            if (separator < 0) continue;
            if (!string.Equals(line[..separator].Trim(), interfaceName, StringComparison.Ordinal)) continue;
            var fields = line[(separator + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 9 || !long.TryParse(fields[0], out var rx) || !long.TryParse(fields[8], out var tx))
                throw new InvalidDataException("Invalid /proc/net/dev row for " + interfaceName + ".");
            if (rx < 0 || tx < 0) throw new InvalidDataException("Negative network byte counters for " + interfaceName + ".");
            return (rx, tx);
        }
        throw new IOException("Routed network interface disappeared or is down: " + interfaceName);
    }

    /// <summary>Скорость линка (бит/с) из sysfs; -1/отсутствие — честное падение (нет заявленной полосы).</summary>
    internal static long ReadLinkSpeedBits(string interfaceName)
    {
        var value = File.ReadAllText($"/sys/class/net/{interfaceName}/speed").Trim();
        if (!long.TryParse(value, out var speed) || speed <= 0)
            throw new InvalidDataException($"Interface {interfaceName} does not expose a positive link speed (sysfs value: '{value}').");
        return speed * 8;
    }

    private static LoadGeneratorInterface Select(string interfaceName, System.Net.IPAddress destination)
    {
        var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(x =>
            string.Equals(x.Name, interfaceName, StringComparison.Ordinal) && x.OperationalStatus == OperationalStatus.Up)
            ?? throw new IOException("Routed network interface disappeared or is down: " + interfaceName);
        var ifindex = File.Exists($"/sys/class/net/{interfaceName}/ifindex")
            ? int.Parse(File.ReadAllText($"/sys/class/net/{interfaceName}/ifindex").Trim())
            : 0;
        var linkSpeed = ReadLinkSpeedBits(interfaceName);
        if (adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            throw new InvalidDataException("OS-selected route has no unique usable interface/link capacity.");
        var selected = new LoadGeneratorInterface(adapter.Id, adapter.Name, adapter.Description, ifindex, linkSpeed,
            destination.ToString(), adapter.NetworkInterfaceType.ToString());
        var hardware = new UnixAdapterMetadataProvider().Read(ifindex);
        if (hardware.HardwareInterface != true || hardware.Virtual != false || hardware.Error != null ||
            hardware.InterfaceIndex != ifindex || !System.Guid.TryParse(hardware.InterfaceId, out var metadataId) ||
            !System.Guid.TryParse(selected.Id, out var selectedId) || metadataId != selectedId)
            throw new LoadGeneratorHardwareException(selected);
        return selected with { HardwareEvidence = hardware };
    }
}

/// <summary>
/// Аппаратные метаданные Linux-адаптера по sysfs: наличие PCI-устройства (<c>device</c>)
/// отличает физический интерфейс от виртуальных (veth/tap/bridge/tunnel).
/// </summary>
public sealed class UnixAdapterMetadataProvider : IAdapterMetadataProvider
{
    public const string MetadataSource = "sysfs /sys/class/net/<iface> (device link + name pattern)";

    /// <inheritdoc cref="IAdapterMetadataProvider.Read"/>
    public AdapterHardwareEvidence Read(int interfaceIndex)
    {
        var interfaceName = ResolveNameByIndex(interfaceIndex);
        var isVirtual = System.Text.RegularExpressions.Regex.IsMatch(interfaceName,
            @"^(veth|tap|docker|br-|virbr|tun|lo|dummy|ifb|ip6tnl|ipip|gre|sit)");
        var isHardware = File.Exists($"/sys/class/net/{interfaceName}/device");
        var id = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(x => string.Equals(x.Name, interfaceName, StringComparison.Ordinal))?.Id ?? "";
        return new AdapterHardwareEvidence(interfaceIndex, id,
            isVirtual ? null : (bool?)isHardware, isVirtual ? true : (bool?)false,
            MetadataSource, DateTimeOffset.UtcNow);
    }

    private static string ResolveNameByIndex(int interfaceIndex)
    {
        foreach (var directory in Directory.GetDirectories("/sys/class/net"))
        {
            var name = Path.GetFileName(directory);
            if (File.Exists(Path.Combine(directory, "ifindex")) &&
                int.TryParse(File.ReadAllText(Path.Combine(directory, "ifindex")).Trim(), out var index) && index == interfaceIndex)
                return name;
        }
        throw new InvalidDataException($"No Linux interface found with ifindex {interfaceIndex}.");
    }
}