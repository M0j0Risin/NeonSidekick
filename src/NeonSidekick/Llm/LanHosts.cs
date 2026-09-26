using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Llm;

/// <summary>
/// The other machines on the local network, as addresses to probe (<see cref="ScanScope.Remote"/> /
/// <see cref="ScanScope.Both"/>): every host of the /24 around each active IPv4 address of this
/// machine, this machine's own addresses left out. <see cref="Expand"/> and <see cref="Describe"/>
/// are pure and pinned by tests; <see cref="Discover"/> is the one reader of
/// <see cref="NetworkInterface.GetAllNetworkInterfaces"/> and never throws.
/// </summary>
/// <param name="Hosts">The addresses to probe, ascending within each subnet, distinct.</param>
/// <param name="Subnets">The subnets they came from, as <see cref="Describe"/> writes them (<c>192.168.1.0/24</c>), for the log.</param>
public sealed record LanHosts(IReadOnlyList<IPAddress> Hosts, IReadOnlyList<string> Subnets)
{
    private const string Category = "Llm";

    /// <summary>
    /// The shortest prefix a subnet is scanned at: a /16 would be 65,534 hosts × six ports; the /24
    /// around the adapter's address (254 hosts) is what a home or office LAN is in practice.
    /// </summary>
    public const int MaxPrefix = 24;

    /// <summary>No network: nothing to probe.</summary>
    public static readonly LanHosts None = new(Array.Empty<IPAddress>(), Array.Empty<string>());

    /// <summary>
    /// Every other host address of the subnet holding <paramref name="address"/>: the mask clamped to
    /// /<see cref="MaxPrefix"/> when it is wider, the network and broadcast addresses and
    /// <paramref name="address"/> itself left out, ascending. Empty for anything that is not IPv4.
    /// </summary>
    public static IReadOnlyList<IPAddress> Expand(IPAddress address, IPAddress mask)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(mask);
        if (!TryClamp(address, mask, out uint self, out uint network, out uint broadcast))
        {
            return Array.Empty<IPAddress>();
        }

        var hosts = new List<IPAddress>();
        for (uint host = network + 1; host < broadcast; host++)
        {
            if (host != self)
            {
                hosts.Add(FromUInt32(host));
            }
        }

        return hosts;
    }

    /// <summary>The subnet <see cref="Expand"/> walks for <paramref name="address"/>, as <c>192.168.1.0/24</c>; empty for anything that is not IPv4.</summary>
    public static string Describe(IPAddress address, IPAddress mask)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(mask);
        if (!TryClamp(address, mask, out _, out uint network, out uint broadcast))
        {
            return "";
        }

        int prefix = 32 - System.Numerics.BitOperations.Log2(broadcast - network + 1);
        return FromUInt32(network) + "/" + prefix.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>One adapter as <see cref="From"/> reads it: whether its subnets are scanned (up, neither loopback nor a tunnel) and its unicast addresses with their masks.</summary>
    internal sealed record Adapter(bool Scanned, IReadOnlyList<(IPAddress Address, IPAddress Mask)> Addresses);

    /// <summary>
    /// The network as the adapters see it (<see cref="From"/> over <see cref="NetworkInterface.GetAllNetworkInterfaces"/>).
    /// A failure to read the interfaces is a warning and <see cref="None"/>.
    /// </summary>
    public static LanHosts Discover()
    {
        try
        {
            var adapters = NetworkInterface.GetAllNetworkInterfaces().Select(nic => new Adapter(
                nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel),
                // The mask read for IPv4 alone, as before: an IPv6 address's IPv4Mask is nothing to rely on.
                nic.GetIPProperties().UnicastAddresses.Select(u => (u.Address, u.Address.AddressFamily == AddressFamily.InterNetwork ? u.IPv4Mask : IPAddress.Any)).ToList())).ToList();
            var lan = From(adapters);
            DiagnosticLog.Debug(Category, lan.Subnets.Count == 0
                ? "No local network to scan: no adapter is up with an IPv4 address."
                : $"Local network: {string.Join(", ", lan.Subnets)} ({lan.Hosts.Count.ToString(CultureInfo.InvariantCulture)} hosts to probe).");
            return lan;
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            DiagnosticLog.Warn(Category, $"Could not read the network adapters: {ex.Message}");
            return None;
        }
    }

    /// <summary>
    /// The hosts to probe over <paramref name="adapters"/>: each scanned adapter's IPv4 addresses outside link-local
    /// (169.254/16), <see cref="Expand"/> over the adapter's mask; the union distinct, minus every address this machine
    /// holds on <em>any</em> adapter — a down one's too (2026-09-26: a disconnected Wi-Fi still holding 192.168.1.103
    /// inside the Ethernet's /24 was listed as a host, since only the scanned adapters' addresses were left out). Pure.
    /// </summary>
    internal static LanHosts From(IEnumerable<Adapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        var own = new HashSet<IPAddress>();
        var subnets = new List<(IPAddress Address, IPAddress Mask)>();
        foreach (var adapter in adapters)
        {
            foreach (var (address, mask) in adapter.Addresses)
            {
                own.Add(address);
                if (adapter.Scanned && address.AddressFamily == AddressFamily.InterNetwork && !IsLinkLocal(address))
                {
                    subnets.Add((address, mask));
                }
            }
        }

        var hosts = new List<IPAddress>();
        var seen = new HashSet<IPAddress>();
        var names = new List<string>();
        foreach (var (address, mask) in subnets)
        {
            string name = Describe(address, mask);
            if (name.Length == 0 || names.Contains(name))
            {
                continue;
            }

            names.Add(name);
            foreach (var host in Expand(address, mask))
            {
                if (!own.Contains(host) && seen.Add(host))
                {
                    hosts.Add(host);
                }
            }
        }

        return new LanHosts(hosts, names);
    }

    private static bool IsLinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }

    /// <summary>The subnet bounds for an IPv4 address under its mask clamped to /<see cref="MaxPrefix"/>; false for any other family or a mask that is not one.</summary>
    private static bool TryClamp(IPAddress address, IPAddress mask, out uint self, out uint network, out uint broadcast)
    {
        self = network = broadcast = 0;
        if (address.AddressFamily != AddressFamily.InterNetwork || mask.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        self = ToUInt32(address);
        uint bits = ToUInt32(mask);
        int prefix = System.Numerics.BitOperations.PopCount(bits);
        if (bits != (prefix == 0 ? 0u : uint.MaxValue << (32 - prefix)))
        {
            return false;   // not a contiguous mask
        }

        if (prefix < MaxPrefix)
        {
            prefix = MaxPrefix;
            bits = uint.MaxValue << (32 - prefix);
        }

        network = self & bits;
        broadcast = network | ~bits;
        return true;
    }

    private static uint ToUInt32(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    private static IPAddress FromUInt32(uint value) =>
        new(new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value });
}
