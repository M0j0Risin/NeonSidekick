using System.Net;
using System.Net.Sockets;

namespace NeonSidekick.Web;

/// <summary>Why <see cref="LanPolicy"/> refused a host: none, the LAN under <c>internet</c>, the internet under <c>local_area_network</c>.</summary>
public enum NetworkRefusal
{
    None,
    Lan,
    Internet,
}

/// <summary>What <see cref="LanPolicy.Admit"/> decided: the addresses to use, none under a refusal.</summary>
public readonly record struct Admission(IReadOnlyList<IPAddress> Addresses, NetworkRefusal Refusal)
{
    /// <summary>Whether the host may be reached.</summary>
    public bool Admitted => Refusal == NetworkRefusal.None;
}

/// <summary>Thrown by <see cref="LanPolicy.ConnectAsync"/> for a host the setting <c>Web browser network mode</c> keeps off limits (<c>LanRefusedException</c> until 2026-09-18).</summary>
public sealed class NetworkRefusedException : HttpRequestException
{
    public NetworkRefusedException(string host, NetworkRefusal reason)
        : base(reason == NetworkRefusal.Internet ? WebText.InternetRefused(host) : WebText.LanRefused(host))
    {
        Host = host;
        Reason = reason;
    }

    public string Host { get; }

    public NetworkRefusal Reason { get; }

    /// <summary>The fetch outcome the refusal is reported as.</summary>
    public FetchOutcome Outcome => Reason == NetworkRefusal.Internet ? FetchOutcome.InternetRefused : FetchOutcome.LanRefused;
}

/// <summary>
/// The setting <c>Web browser network mode</c> enforced where it counts: at the socket, over the addresses
/// the host actually resolves to, so a public name that points into the LAN and a redirect into it
/// are refused as surely as a literal <c>192.168.…</c>. Under <c>internet</c> (the default),
/// <c>web_fetch</c> reaches nothing on this machine or the local network; under
/// <c>local_area_network</c> nothing else (a name with both kinds of address is LAN, and is
/// connected to its private addresses alone); under <c>both</c> everything. The app's own SearXNG
/// request opts out through <see cref="ExemptKey"/>. The mode is read through a delegate on every
/// connect, so a pick in <c>/settings</c> applies at once. <see cref="IsPrivate"/> and
/// <see cref="Judge"/> are pure and pinned.
/// </summary>
public sealed class LanPolicy
{
    /// <summary>Set on a request the policy does not judge: the app's own calls (SearXNG), never a tool's.</summary>
    public static readonly HttpRequestOptionsKey<bool> ExemptKey = new("NeonSidekick.Web.LanExempt");

    private readonly Func<NetworkReach> _reach;

    /// <param name="reach">The live value of the setting <c>Web browser network mode</c>.</param>
    public LanPolicy(Func<NetworkReach> reach)
    {
        _reach = reach ?? throw new ArgumentNullException(nameof(reach));
    }

    /// <summary>Where the setting lets a fetch reach right now.</summary>
    public NetworkReach Reach => _reach();

    /// <summary>The one rule: what <paramref name="reach"/> refuses for a host that is (<paramref name="lan"/>) or is not this machine / the local network.</summary>
    public static NetworkRefusal Judge(NetworkReach reach, bool lan) => reach switch
    {
        NetworkReach.Both => NetworkRefusal.None,
        NetworkReach.LocalAreaNetwork => lan ? NetworkRefusal.None : NetworkRefusal.Internet,
        _ => lan ? NetworkRefusal.Lan : NetworkRefusal.None,
    };

    /// <summary>
    /// Whether <paramref name="address"/> is this machine or a local network: loopback, the private
    /// ranges (10/8, 172.16/12, 192.168/16), link-local (169.254/16), carrier-grade NAT (100.64/10),
    /// the unspecified and broadcast addresses, multicast; for IPv6 <c>::1</c>, <c>::</c>, <c>fc00::/7</c>,
    /// <c>fe80::/10</c>, <c>fec0::/10</c>, <c>64:ff9b:1::/48</c>, <c>ff00::/8</c>, and a v4-mapped address, or one carrying an IPv4 address
    /// (<see cref="EmbeddedIPv4"/>: NAT64, 6to4, v4-compatible, v4-translated), by its v4 rules.
    /// </summary>
    public static bool IsPrivate(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 0
                || b[0] == 10
                || b[0] == 127
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                // Not globally reachable (2026-10-23, the admin review's U8, IANA's special-purpose registry): 192.0.0.0/24, benchmarking's
                // 198.18.0.0/15 (lab and fake-IP networks), and the three documentation blocks.
                || (b[0] == 192 && b[1] == 0 && b[2] is 0 or 2)
                || (b[0] == 198 && b[1] is 18 or 19)
                || (b[0] == 198 && b[1] == 51 && b[2] == 100)
                || (b[0] == 203 && b[1] == 0 && b[2] == 113)
                || b[0] >= 224;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (IPAddress.IPv6Loopback.Equals(address) || IPAddress.IPv6Any.Equals(address))
            {
                return true;
            }

            var b = address.GetAddressBytes();
            if (EmbeddedIPv4(b) is { } embedded)
            {
                return IsPrivate(embedded);
            }

            return (b[0] & 0xFE) == 0xFC   // fc00::/7 unique local
                || (b[0] == 0xFE && (b[1] & 0xC0) == 0x80)   // fe80::/10 link-local
                || (b[0] == 0xFE && (b[1] & 0xC0) == 0xC0)   // fec0::/10 site-local (deprecated, still routed by some)
                || (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && b[4] == 0x00 && b[5] == 0x01)   // 64:ff9b:1::/48 local-use NAT64
                || (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8)   // 2001:db8::/32 documentation (U8)
                || b[0] == 0xFF;   // multicast
        }

        return true;
    }

    /// <summary>
    /// The IPv4 address an IPv6 one carries and reaches (2026-10-19, the network review: <c>64:ff9b::c0a8:101</c> reached 192.168.1.1 through
    /// a NAT64 gateway under <c>internet</c>): the well-known NAT64 prefix 64:ff9b::/96 (RFC 6052), 6to4's 2002::/16 (RFC 3056), an
    /// IPv4-compatible ::/96 (<c>::</c> and <c>::1</c> judged before), and an IPv4-translated ::ffff:0:0/96 (RFC 2765). Null for any other.
    /// </summary>
    public static IPAddress? EmbeddedIPv4(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return address.AddressFamily == AddressFamily.InterNetworkV6 && !address.IsIPv4MappedToIPv6
            && !IPAddress.IPv6Loopback.Equals(address) && !IPAddress.IPv6Any.Equals(address) ? EmbeddedIPv4(address.GetAddressBytes()) : null;
    }

    private static IPAddress? EmbeddedIPv4(byte[] b)
    {
        bool nat64 = b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && b.AsSpan(4, 8).IndexOfAnyExcept((byte)0) < 0;
        if (nat64)
        {
            return new IPAddress(b.AsSpan(12, 4));
        }

        if (b[0] == 0x20 && b[1] == 0x02)
        {
            return new IPAddress(b.AsSpan(2, 4));
        }

        // Teredo's 2001:0::/32 (RFC 4380) carries the client's address in its last 32 bits, each bit flipped; ISATAP (RFC 5214) an IPv4 address after
        // an interface id of 0000:5efe or 0200:5efe (2026-10-23, the admin review's J13: both were judged as public IPv6).
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00)
        {
            return new IPAddress([(byte)~b[12], (byte)~b[13], (byte)~b[14], (byte)~b[15]]);
        }

        if ((b[8] & 0xFD) == 0x00 && b[9] == 0x00 && b[10] == 0x5E && b[11] == 0xFE)
        {
            return new IPAddress(b.AsSpan(12, 4));
        }

        bool compatible = b.AsSpan(0, 12).IndexOfAnyExcept((byte)0) < 0;   // :: and ::1 judged before this is asked
        bool translated = b.AsSpan(0, 8).IndexOfAnyExcept((byte)0) < 0 && b[8] == 0xFF && b[9] == 0xFF && b[10] == 0 && b[11] == 0;
        return compatible || translated ? new IPAddress(b.AsSpan(12, 4)) : null;
    }

    /// <summary>
    /// Whether <paramref name="address"/> is the LAN: <see cref="IsPrivate"/>, or this machine's own or on one of its on-link prefixes
    /// (<see cref="OnLinkNetworks"/>, 2026-10-23, the admin review's U4, decision DA3).
    /// </summary>
    public static bool IsLan(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return IsPrivate(address) || OnLinkNetworks.Contains(address);
    }

    /// <summary>Whether <paramref name="host"/> is the name <c>localhost</c> (any case, a trailing dot allowed) or a LAN literal address.</summary>
    public static bool IsPrivateHost(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        string name = host.Trim().TrimEnd('.').Trim('[', ']');
        if (string.Equals(name, "localhost", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(name, out var address) && IsLan(address);
    }

    /// <summary>The suffixes only a local network answers (mDNS, the home and internal zones), for <see cref="SpelledLan"/>.</summary>
    public static readonly IReadOnlyList<string> LanSuffixes = [".local", ".lan", ".home.arpa", ".internal", ".localdomain"];

    /// <summary>
    /// Whether <paramref name="host"/> is the LAN by its spelling alone, before it is resolved (2026-10-23, the admin review's M7: under
    /// <c>internet</c> reach a single-label <c>fileserver</c> went out as LLMNR and NetBIOS broadcasts, and a spoofable reply was taken, before it
    /// was refused): <see cref="IsPrivateHost"/>, a single label, or one of <see cref="LanSuffixes"/>. Pure.
    /// </summary>
    public static bool SpelledLan(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        string name = host.Trim().TrimEnd('.').Trim('[', ']');
        if (IsPrivateHost(name))
        {
            return true;
        }

        if (IPAddress.TryParse(name, out _))
        {
            return false;
        }

        return name.Length > 0 && (!name.Contains('.', StringComparison.Ordinal) || LanSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Whether any of <paramref name="addresses"/> is the LAN (a name that resolves to one is judged by its worst address).</summary>
    public static bool AnyPrivate(IReadOnlyList<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        foreach (var address in addresses)
        {
            if (IsLan(address))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The one judgement over a host and what it resolved to (2026-10-15, the admin-tools plan: lifted out of <see cref="ConnectAsync"/> so
    /// <see cref="WebFetcher.PreflightAsync"/> and the network tools ask the same thing): the refusal <paramref name="reach"/> gives, and when
    /// none the addresses to use — under <c>lan</c> a public name's private addresses alone, otherwise all of them. A host is LAN when its
    /// spelling is (<see cref="IsPrivateHost"/>) or any address is. Pure.
    /// </summary>
    public static Admission Admit(string host, IReadOnlyList<IPAddress> addresses, NetworkReach reach)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(addresses);
        bool privateName = IsPrivateHost(host);
        var refusal = Judge(reach, privateName || AnyPrivate(addresses));
        if (refusal != NetworkRefusal.None)
        {
            return new Admission([], refusal);
        }

        if (reach != NetworkReach.LocalAreaNetwork)
        {
            return new Admission(addresses, NetworkRefusal.None);
        }

        // Under lan only the LAN's addresses, a LAN-spelled name's too (2026-10-23, the admin review's U3: a resolver answering x.localhost with a
        // public address reached the internet); none left is the internet refused.
        var lan = addresses.Where(IsLan).ToArray();
        return lan.Length > 0 || addresses.Count == 0 ? new Admission(lan, NetworkRefusal.None) : new Admission([], NetworkRefusal.Internet);
    }

    /// <summary>
    /// The <see cref="SocketsHttpHandler.ConnectCallback"/>: resolve, judge, connect. The judgement
    /// is skipped for a request carrying <see cref="ExemptKey"/> and under <c>both</c>; under
    /// <c>local_area_network</c> a resolved name is connected to its private addresses alone (<see cref="Admit"/>).
    /// </summary>
    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        string host = context.DnsEndPoint.Host;
        var resolved = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        bool exempt = context.InitialRequestMessage?.Options.TryGetValue(ExemptKey, out bool value) == true && value;
        var admission = Admit(host, resolved, exempt ? NetworkReach.Both : _reach());
        if (admission.Refusal != NetworkRefusal.None)
        {
            throw new NetworkRefusedException(host, admission.Refusal);
        }

        // Raced, not one after another (2026-10-19): a dead IPv6 route held the whole fetch on its first address (HappyEyeballs).
        var socket = await HappyEyeballs.ConnectAsync(admission.Addresses, context.DnsEndPoint.Port, HappyEyeballs.AttemptDelay, HappyEyeballs.ConnectOneAsync, cancellationToken).ConfigureAwait(false);
        return new NetworkStream(socket, ownsSocket: true);
    }
}
