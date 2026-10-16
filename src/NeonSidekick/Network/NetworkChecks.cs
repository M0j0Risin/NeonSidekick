using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;

namespace NeonSidekick.Network;

/// <summary>What one check came to: the answer's text, and for <c>/net</c> the note when the model's call would have been refused.</summary>
public readonly record struct NetworkAnswer(string Text, string? ModelNote = null);

/// <summary>
/// The six checks (2026-10-15, the admin-tools plan), the tools' and <c>/net</c>'s alike: each goes through <see cref="NetworkAccess"/> for its
/// host (judged and counted under a <paramref name="budget"/>, the user's hand under none) and answers with <see cref="NetworkRender"/>'s
/// text. A failure is its sentence, never a throw; a cancel by the caller is the caller's.
/// </summary>
public static class NetworkChecks
{
    public const int DefaultCount = 4;
    public const int MaxCount = 10;
    public const int DefaultHops = 30;
    public const int MaxHops = 30;
    public const int MaxPortsPerCall = 16;
    public const int DefaultCertLimit = 50;
    public const int MaxCertLimit = 500;

    /// <summary>The wait for one echo.</summary>
    public static readonly TimeSpan EchoTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// <c>net_dns</c>: A/AAAA with no server through the system resolver (the injected one); every other type, or any type with a
    /// <paramref name="server"/>, through the app's own client against the system's servers or that one. An explicit server is judged and
    /// counted as a host; the system's resolvers are exempt. A PTR of an address asks for its reverse name.
    /// </summary>
    public static async Task<NetworkAnswer> DnsAsync(NetworkAccess access, NetworkBudget? budget, string name, ushort type, string? server, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(name);
        string asked = name.Trim().TrimEnd('.');
        if (asked.Length == 0)
        {
            return new NetworkAnswer(NetworkText.Missing("name"));
        }

        if (type == DnsWire.Ptr && IPAddress.TryParse(asked.Trim('[', ']'), out var address))
        {
            asked = DnsWire.ReverseName(address);
        }
        else if (HostRules.NormalizeName(asked) is { Length: > 0 } normalized)
        {
            asked = normalized;
        }
        else
        {
            return new NetworkAnswer(NetworkText.BadName(name));
        }

        string? note = null;
        IReadOnlyList<IPEndPoint> servers;
        bool system = string.IsNullOrWhiteSpace(server);
        if (system)
        {
            if (type is DnsWire.A or DnsWire.Aaaa)
            {
                var resolved = await access.ResolveAsync(asked, cancellationToken).ConfigureAwait(false);
                if (resolved.Error is { } failed)
                {
                    return new NetworkAnswer(failed);
                }

                var family = type == DnsWire.A ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6;
                return new NetworkAnswer(NetworkRender.SystemLookup(asked, type, resolved.Value!.Where(a => a.AddressFamily == family).ToList()));
            }

            servers = access.SystemDns();
        }
        else
        {
            var target = await access.TargetAsync(server!, budget, [], cancellationToken).ConfigureAwait(false);
            if (target.Error is { } refused)
            {
                return new NetworkAnswer(refused);
            }

            note = target.Value!.ModelNote;
            servers = target.Value.Addresses.Select(a => new IPEndPoint(a, DnsClient.Port)).ToList();
        }

        var outcome = await DnsClient.QueryAsync(servers, asked, type, cancellationToken).ConfigureAwait(false);
        return new NetworkAnswer(NetworkRender.Lookup(asked, type, outcome, system), note);
    }

    /// <summary><c>net_ping</c>: <paramref name="count"/> echoes to the host's first admitted address, a second apart once the first has answered.</summary>
    public static async Task<NetworkAnswer> PingAsync(NetworkAccess access, NetworkBudget? budget, string host, int count, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (access.Pinger is not { } pinger)
        {
            return new NetworkAnswer(NetworkText.NoPinger);
        }

        var target = await access.TargetAsync(host, budget, [], cancellationToken).ConfigureAwait(false);
        if (target.Error is { } refused)
        {
            return new NetworkAnswer(refused);
        }

        var address = Preferred(target.Value!.Addresses, pinger);
        var echoes = new List<PingHop>(count);
        for (int i = 0; i < count; i++)
        {
            if (i > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), access.Time, cancellationToken).ConfigureAwait(false);
            }

            echoes.Add(await pinger.PingAsync(address, 128, EchoTimeout, cancellationToken).ConfigureAwait(false));
        }

        return new NetworkAnswer(NetworkRender.Ping(target.Value.Host, address, echoes), target.Value.ModelNote);
    }

    /// <summary><c>net_trace</c>: the hops to the host's first admitted address.</summary>
    public static async Task<NetworkAnswer> TraceAsync(NetworkAccess access, NetworkBudget? budget, string host, int maxHops, bool names, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (access.Pinger is not { } pinger)
        {
            return new NetworkAnswer(NetworkText.NoPinger);
        }

        if (!pinger.CanTrace)
        {
            return new NetworkAnswer(NetworkText.NoTrace);
        }

        var target = await access.TargetAsync(host, budget, [], cancellationToken).ConfigureAwait(false);
        if (target.Error is { } refused)
        {
            return new NetworkAnswer(refused);
        }

        var address = Preferred(target.Value!.Addresses, pinger);
        var hops = await NetworkProbes.TraceAsync(pinger, address, maxHops, names, NetworkProbes.ReverseAsync, cancellationToken).ConfigureAwait(false);
        return new NetworkAnswer(NetworkRender.Trace(target.Value.Host, address, maxHops, hops), target.Value.ModelNote);
    }

    /// <summary><c>net_port</c>: each port on every admitted address.</summary>
    public static async Task<NetworkAnswer> PortsAsync(NetworkAccess access, NetworkBudget? budget, string host, IReadOnlyList<int> ports, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(ports);
        var target = await access.TargetAsync(host, budget, ports, cancellationToken).ConfigureAwait(false);
        if (target.Error is { } refused)
        {
            return new NetworkAnswer(refused);
        }

        var results = await NetworkProbes.PortsAsync(target.Value!.Addresses, ports, access.Connect, cancellationToken).ConfigureAwait(false);
        return new NetworkAnswer(NetworkRender.Ports(target.Value.Host, target.Value.Addresses, results), target.Value.ModelNote);
    }

    /// <summary><c>net_tls</c>: the handshake on <paramref name="port"/>, the preamble given or guessed from the port.</summary>
    public static async Task<NetworkAnswer> TlsAsync(NetworkAccess access, NetworkBudget? budget, string host, int port, string? serverName, StartTls? preamble, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        var target = await access.TargetAsync(host, budget, [port], cancellationToken).ConfigureAwait(false);
        if (target.Error is { } refused)
        {
            return new NetworkAnswer(refused);
        }

        var value = target.Value!;
        string name = string.IsNullOrWhiteSpace(serverName) ? HostRules.NormalizeName(value.Host) : serverName.Trim();
        var kind = preamble ?? TlsPreamble.Guess(port);
        try
        {
            var report = await TlsProbe.RunAsync(value.Host, value.Addresses, port, name, kind, cancellationToken).ConfigureAwait(false);
            return new NetworkAnswer(NetworkRender.Tls(report, access.Time.GetUtcNow(), access.Time.LocalTimeZone), value.ModelNote);
        }
        catch (TlsPreambleException ex)
        {
            return new NetworkAnswer(ex.Message, value.ModelNote);
        }
        catch (AuthenticationException ex)
        {
            return new NetworkAnswer(NetworkText.HandshakeFailed(value.Host, port, ex.InnerException?.Message ?? ex.Message), value.ModelNote);
        }
        catch (IOException ex)
        {
            return new NetworkAnswer(NetworkText.HandshakeFailed(value.Host, port, ex.Message), value.ModelNote);
        }
        catch (SocketException ex)
        {
            return new NetworkAnswer(NetworkText.ConnectFailed(value.Host, port, ex.Message), value.ModelNote);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new NetworkAnswer(NetworkText.TimedOut("the TLS check of " + value.Host), value.ModelNote);
        }
    }

    /// <summary><c>net_certs</c>: a store read, narrowed and listed.</summary>
    public static NetworkAnswer Certificates(NetworkAccess access, string store, System.Security.Cryptography.X509Certificates.StoreLocation location, string? filter, int? expiring, int limit)
    {
        ArgumentNullException.ThrowIfNull(access);
        var listing = CertificateStores.Read(store, location);
        var now = access.Time.GetUtcNow();
        var shown = CertificateStores.Narrow(listing.Certificates, filter, expiring, now);
        return new NetworkAnswer(NetworkRender.Certificates(listing, shown, limit, filter, expiring, now, access.Time.LocalTimeZone));
    }

    /// <summary>The address an echo goes to: the first IPv4 for a pinger without IPv6, else the first.</summary>
    private static IPAddress Preferred(IReadOnlyList<IPAddress> addresses, IPinger pinger) =>
        pinger is IcmpSocketPinger ? addresses.FirstOrDefault(a => HostRules.Canonical(a).AddressFamily == AddressFamily.InterNetwork) ?? addresses[0] : addresses[0];
}
