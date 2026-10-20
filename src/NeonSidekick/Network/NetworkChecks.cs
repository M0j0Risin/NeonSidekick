using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using NeonSidekick.Diagnostics;

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
    /// counted as a host; the system's resolvers are exempt, but the question is not (2026-10-19, the network review): a PTR is judged by
    /// its address, an A/AAAA by what it resolved to, any other by its name, and through the system's resolvers each counts as a host. A PTR
    /// of an address asks for its reverse name.
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

        IPAddress? reverse = null;
        if (type == DnsWire.Ptr && IPAddress.TryParse(asked.Trim('[', ']'), out var address))
        {
            reverse = HostRules.Canonical(address);
            asked = DnsWire.ReverseName(address);
        }
        else if (HostRules.NormalizeName(asked) is { Length: > 0 } normalized)
        {
            asked = normalized;

            // A reverse name typed out stands for its address, whatever the type.
            reverse = DnsWire.ReverseAddress(asked) is { } typed ? HostRules.Canonical(typed) : null;
        }
        else
        {
            return new NetworkAnswer(NetworkText.BadName(name));
        }

        IReadOnlyList<IPEndPoint> servers;
        bool system = string.IsNullOrWhiteSpace(server);
        if (system && (type is DnsWire.A or DnsWire.Aaaa) && reverse is null)
        {
            var resolved = await access.ResolveAsync(asked, cancellationToken).ConfigureAwait(false);
            if (resolved.Error is { } failed)
            {
                return new NetworkAnswer(failed);
            }

            var judged = await QuestionAsync(access, budget, asked, resolved.Value!, cancellationToken).ConfigureAwait(false);
            if (judged.Refusal is { } refusal)
            {
                return new NetworkAnswer(refusal);
            }

            var family = type == DnsWire.A ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6;
            return new NetworkAnswer(NetworkRender.SystemLookup(asked, type, judged.Shown.Where(a => a.AddressFamily == family).ToList()), judged.Note);
        }

        // The question itself, before any server is asked: a PTR by its address, any other by its name; counted when the system's
        // resolvers carry it (an explicit server is counted as the host it is).
        var question = await QuestionAsync(access, budget, reverse?.ToString() ?? asked, reverse is null ? [] : [reverse], cancellationToken, count: system).ConfigureAwait(false);
        if (question.Refusal is { } questionRefused)
        {
            return new NetworkAnswer(questionRefused);
        }

        string? note = question.Note;
        if (system)
        {
            servers = access.SystemDns();
        }
        else
        {
            var target = await access.TargetAsync(server!, budget, [], cancellationToken).ConfigureAwait(false);
            if (target.Error is { } refused)
            {
                return new NetworkAnswer(refused);
            }

            note ??= target.Value!.ModelNote;
            servers = target.Value!.Addresses.Select(a => new IPEndPoint(a, DnsClient.Port)).ToList();
        }

        var outcome = await DnsClient.QueryAsync(servers, asked, type, cancellationToken).ConfigureAwait(false);
        return new NetworkAnswer(NetworkRender.Lookup(asked, type, outcome, system), note);
    }

    /// <summary>
    /// A DNS question judged (<see cref="NetworkAccess.JudgeLookupAsync"/>) and, under a <paramref name="budget"/>, counted as a host
    /// (2026-10-19, the network review: a lookup through the system's resolvers was neither, so a PTR sweep of the LAN went unchecked). The
    /// model's: <see cref="QuestionJudgement.Refusal"/> when refused or the budget is spent, the kept addresses shown. The user's hand (no
    /// budget): every address shown, the model's refusal as <see cref="QuestionJudgement.Note"/>.
    /// </summary>
    private static async Task<QuestionJudgement> QuestionAsync(NetworkAccess access, NetworkBudget? budget, string host, IReadOnlyList<IPAddress> addresses, CancellationToken cancellationToken, bool count = true)
    {
        var (kept, refusal) = await access.JudgeLookupAsync(host, addresses, cancellationToken).ConfigureAwait(false);
        if (budget is null)
        {
            return new QuestionJudgement(addresses, null, refusal is null ? null : NetworkText.ModelWouldBeRefused(refusal));
        }

        if (refusal is null && count)
        {
            refusal = budget.Take(host, kept, [], access.MaxHosts, access.MaxPorts);
        }

        if (refusal is not null)
        {
            DiagnosticLog.Info(NetworkText.Category, "Refused " + host + ": " + refusal);
        }

        return new QuestionJudgement(kept, refusal, null);
    }

    /// <summary>What <see cref="QuestionAsync"/> came to: the addresses to show, the model's refusal, <c>/net</c>'s note.</summary>
    private readonly record struct QuestionJudgement(IReadOnlyList<IPAddress> Shown, string? Refusal, string? Note);

    /// <summary><c>net_ping</c>: <paramref name="count"/> echoes to the host's first admitted address that answers (<see cref="ChooseAsync"/>), half a second apart.</summary>
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

        var (address, first) = await ChooseAsync(target.Value!.Addresses, pinger, cancellationToken).ConfigureAwait(false);
        var echoes = new List<PingHop>(count);
        if (first is { } sent)
        {
            echoes.Add(sent);
        }

        for (int i = echoes.Count; i < count; i++)
        {
            if (i > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), access.Time, cancellationToken).ConfigureAwait(false);
            }

            echoes.Add(await pinger.PingAsync(address, 128, EchoTimeout, cancellationToken).ConfigureAwait(false));
        }

        return new NetworkAnswer(NetworkRender.Ping(target.Value.Host, address, echoes), target.Value.ModelNote);
    }

    /// <summary><c>net_trace</c>: the hops to the host's first admitted address that answers an echo (<see cref="ChooseAsync"/>), else its first.</summary>
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

        var (address, _) = await ChooseAsync(target.Value!.Addresses, pinger, cancellationToken).ConfigureAwait(false);
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

    /// <summary>The address an echo goes to first: the first IPv4 for a pinger without IPv6, else the first.</summary>
    private static IPAddress Preferred(IReadOnlyList<IPAddress> addresses, IPinger pinger) =>
        pinger is IcmpSocketPinger ? addresses.FirstOrDefault(a => HostRules.Canonical(a).AddressFamily == AddressFamily.InterNetwork) ?? addresses[0] : addresses[0];

    /// <summary>
    /// The address the echoes go to (2026-10-19, the network review: a dual-stack host's first address was often IPv6 on a network that routes
    /// it nowhere, and the host looked down though IPv4 answered — the web tools' Happy Eyeballs lesson): <see cref="Preferred"/>, unless one
    /// echo there gets no reply and one to the other family's first address does. The echo that settled it comes back with it (null when none
    /// was sent: a host of one family, or a pinger without IPv6).
    /// </summary>
    private static async Task<(IPAddress Address, PingHop? First)> ChooseAsync(IReadOnlyList<IPAddress> addresses, IPinger pinger, CancellationToken cancellationToken)
    {
        var address = Preferred(addresses, pinger);
        var family = HostRules.Canonical(address).AddressFamily;
        var other = addresses.FirstOrDefault(a => HostRules.Canonical(a).AddressFamily != family);
        if (other is null || pinger is IcmpSocketPinger)
        {
            return (address, null);
        }

        var first = await pinger.PingAsync(address, 128, EchoTimeout, cancellationToken).ConfigureAwait(false);
        if (first.Status == PingStatus.Reply)
        {
            return (address, first);
        }

        var fallback = await pinger.PingAsync(other, 128, EchoTimeout, cancellationToken).ConfigureAwait(false);
        return fallback.Status == PingStatus.Reply ? (other, fallback) : (address, first);
    }
}
