using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Web;

/// <summary>One prefix this machine is on: an adapter's address and its prefix length.</summary>
public readonly record struct OnLinkPrefix(IPAddress Address, int Length);

/// <summary>
/// This machine's own addresses and the prefixes its adapters are on, which count as the LAN in <see cref="LanPolicy"/> (2026-10-23, the admin
/// review's U4, decision DA3): by class alone a LAN device's global IPv6 (SLAAC on the delegated prefix) and this machine's own public addresses
/// counted as the internet, so under <c>internet</c> reach they were probed. Every up adapter but loopback counts, tunnels too (a VPN's own
/// prefix is the VPN's LAN); a prefix shorter than /16 (IPv4) or /48 (IPv6) is left out, so an odd adapter cannot make a large share of the
/// internet "LAN". Read into a snapshot, never per request, and read again when the addresses change (<see cref="NetworkChange.NetworkAddressChanged"/>:
/// a VPN coming up). Each adapter is read alone, so one that throws is skipped, not the whole list. The tests set their own list
/// (<see cref="Use"/>), never this machine's adapters.
/// </summary>
public static class OnLinkNetworks
{
    /// <summary>The shortest IPv4 prefix counted.</summary>
    public const int MinIPv4Prefix = 16;

    /// <summary>The shortest IPv6 prefix counted.</summary>
    public const int MinIPv6Prefix = 48;

    private static volatile IReadOnlyList<OnLinkPrefix>? s_current;
    private static volatile IReadOnlyList<OnLinkPrefix>? s_override;
    private static int s_hooked;

    /// <summary>Whether <paramref name="address"/> is this machine's own or inside one of its on-link prefixes.</summary>
    public static bool Contains(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        byte[] bytes = address.GetAddressBytes();
        foreach (var prefix in Current)
        {
            if (prefix.Address.AddressFamily == address.AddressFamily && SamePrefix(bytes, prefix.Address.GetAddressBytes(), prefix.Length))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The prefixes in force: the tests' when set, else this machine's snapshot.</summary>
    public static IReadOnlyList<OnLinkPrefix> Current
    {
        get
        {
            if (s_override is { } set)
            {
                return set;
            }

            if (Interlocked.Exchange(ref s_hooked, 1) == 0)
            {
                NetworkChange.NetworkAddressChanged += (_, _) => s_current = null;
            }

            return s_current ??= Read();
        }
    }

    /// <summary>
    /// The tests' seam: <paramref name="prefixes"/> in force until the scope is disposed (an own address is a full-length prefix). The test
    /// assembly sets an empty list at start, so no test reads this machine's adapters.
    /// </summary>
    public static IDisposable Use(IReadOnlyList<OnLinkPrefix> prefixes)
    {
        ArgumentNullException.ThrowIfNull(prefixes);
        var before = s_override;
        s_override = Usable(prefixes);
        return new Scope(before);
    }

    /// <summary>The prefixes kept: those not too short, an own address always (as a full-length prefix). Pure.</summary>
    public static IReadOnlyList<OnLinkPrefix> Usable(IEnumerable<OnLinkPrefix> prefixes)
    {
        ArgumentNullException.ThrowIfNull(prefixes);
        var kept = new List<OnLinkPrefix>();
        foreach (var prefix in prefixes)
        {
            var address = prefix.Address.IsIPv4MappedToIPv6 ? prefix.Address.MapToIPv4() : prefix.Address;
            int full = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            int min = address.AddressFamily == AddressFamily.InterNetwork ? MinIPv4Prefix : MinIPv6Prefix;
            kept.Add(new OnLinkPrefix(address, full));   // the address itself is this machine's
            if (prefix.Length >= min && prefix.Length <= full)
            {
                kept.Add(new OnLinkPrefix(address, prefix.Length));
            }
        }

        return kept;
    }

    private static IReadOnlyList<OnLinkPrefix> Read()
    {
        var found = new List<OnLinkPrefix>();
        NetworkInterface[] adapters;
        try
        {
            adapters = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            DiagnosticLog.Warn("Web", "Could not read the network adapters for the LAN's prefixes: " + ex.Message);
            return [];
        }

        foreach (var adapter in adapters)
        {
            try
            {
                if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
                {
                    found.Add(new OnLinkPrefix(unicast.Address, unicast.PrefixLength));
                }
            }
            catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException)
            {
                // One adapter that will not be read is skipped, not the whole list (the U9 lesson).
            }
        }

        return Usable(found);
    }

    private static bool SamePrefix(byte[] address, byte[] network, int length)
    {
        if (address.Length != network.Length)
        {
            return false;
        }

        for (int i = 0; i < address.Length && length > 0; i++, length -= 8)
        {
            int bits = Math.Min(8, length);
            int mask = (0xFF << (8 - bits)) & 0xFF;
            if ((address[i] & mask) != (network[i] & mask))
            {
                return false;
            }
        }

        return true;
    }

    private sealed class Scope(IReadOnlyList<OnLinkPrefix>? before) : IDisposable
    {
        public void Dispose() => s_override = before;
    }
}
