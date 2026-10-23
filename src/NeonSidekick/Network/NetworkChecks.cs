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
            // A LAN spelling refused before the system's resolvers are asked (2026-10-23, the admin review's M7).
            if (budget is not null && access.SpelledLanRefusal(asked) is { } spelled)
            {
                return new NetworkAnswer(budget.Take(asked, [], [], access.MaxHosts, access.MaxPorts) ?? spelled);
            }

            var resolved = await access.ResolveAsync(asked, cancellationToken).ConfigureAwait(false);
            if (resolved.Error is { } failed)
            {
                // An unresolved name spends the budget too (U5).
                return new NetworkAnswer(budget?.Take(asked, [], [], access.MaxHosts, access.MaxPorts) ?? failed);
            }

            // Only the asked family's addresses judged and counted (2026-10-23, the admin review's U7: a private A refused an AAAA question under
            // internet, and under lan the AAAA read "no records").
            var family = type == DnsWire.A ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6;
            var judged = await QuestionAsync(access, budget, asked, resolved.Value!.Where(a => a.AddressFamily == family).ToList(), cancellationToken).ConfigureAwait(false);
            if (judged.Refusal is { } refusal)
            {
                return new NetworkAnswer(refusal);
            }

            return new NetworkAnswer(NetworkRender.SystemLookup(asked, type, judged.Shown.ToList()), judged.Note);
        }

        // The question itself, before any server is asked: a PTR by its address, any other by its name; counted always (2026-10-23, the admin
        // review's A1: through an explicit server the question was never counted, so one host's budget swept a /24's PTRs).
        var question = await QuestionAsync(access, budget, reverse?.ToString() ?? asked, reverse is null ? [] : [reverse], cancellationToken).ConfigureAwait(false);
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
        var (shown, withheld) = await AnswersAsync(access, budget, outcome, cancellationToken).ConfigureAwait(false);
        string text = NetworkRender.Lookup(asked, type, shown, system);
        return new NetworkAnswer(withheld > 0 ? text + "\n" + NetworkText.AnswersWithheld(withheld) : text, note);
    }

    /// <summary>
    /// A model's DNS answers judged (2026-10-23, the admin review's A1 and B9): an A or AAAA record by its address, as a host's are; a record
    /// that names a host (CNAME, NS, PTR, MX, SRV, SOA) by that name's spelling and the lists, as a lookup's name is. A refused record is withheld
    /// and counted. The user's hand (no budget) sees every record.
    /// </summary>
    private static async Task<(DnsOutcome Shown, int Withheld)> AnswersAsync(NetworkAccess access, NetworkBudget? budget, DnsOutcome outcome, CancellationToken cancellationToken)
    {
        if (budget is null || outcome.Reply is not { Rcode: 0 } reply || reply.Answers.Count == 0)
        {
            return (outcome, 0);
        }

        var aliases = await access.DeniedAddressesAsync(cancellationToken).ConfigureAwait(false);
        var kept = new List<DnsRecord>(reply.Answers.Count);
        int withheld = 0;
        foreach (var record in reply.Answers)
        {
            string? refusal = null;
            if (record.Type is DnsWire.A or DnsWire.Aaaa && IPAddress.TryParse(record.Data, out var address))
            {
                refusal = access.Judge(record.Name.TrimEnd('.'), [address], aliases).Refusal;
            }
            else if (TargetName(record) is { Length: > 0 } target)
            {
                refusal = (await access.JudgeLookupAsync(target, [], cancellationToken).ConfigureAwait(false)).Refusal;
            }

            if (refusal is null)
            {
                kept.Add(record);
            }
            else
            {
                withheld++;
            }
        }

        return withheld == 0 ? (outcome, 0) : (outcome with { Reply = reply with { Answers = kept } }, withheld);
    }

    /// <summary>The host a record names: a CNAME's, NS's or PTR's data, an MX's or SRV's last word, an SOA's first; null for any other.</summary>
    public static string? TargetName(DnsRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        string[] words = record.Data.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string? target = record.Type switch
        {
            DnsWire.Cname or DnsWire.Ns or DnsWire.Ptr => record.Data,
            DnsWire.Mx or DnsWire.Srv => words.Length > 0 ? words[^1] : null,
            DnsWire.Soa => words.Length > 0 ? words[0] : null,
            _ => null,
        };
        return target?.Trim().TrimEnd('.');
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

        if (count)
        {
            // A refused question spends the budget too (2026-10-23, the admin review's U5), counted by its name alone.
            refusal = refusal is null ? budget.Take(host, kept, [], access.MaxHosts, access.MaxPorts) : budget.Take(host, [], [], access.MaxHosts, access.MaxPorts) ?? refusal;
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
        if (HostRules.Canonical(address).AddressFamily == AddressFamily.InterNetworkV6 && !pinger.SupportsIPv6)
        {
            // Said, not echoed into 100% loss (2026-10-23, the admin review's I8).
            return new NetworkAnswer(NetworkText.NoIpv6Host(target.Value.Host));
        }

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
        if (HostRules.Canonical(address).AddressFamily == AddressFamily.InterNetworkV6 && !pinger.SupportsIPv6)
        {
            return new NetworkAnswer(NetworkText.NoIpv6Host(target.Value.Host));   // I8: never 30 hops of nothing
        }

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
        string text = NetworkRender.Ports(target.Value.Host, target.Value.Addresses, results);
        return new NetworkAnswer(target.Value.LeftOut > 0 ? text + "\n" + NetworkText.AddressesLeftOut(target.Value.LeftOut) : text, target.Value.ModelNote);
    }

    /// <summary><c>net_tls</c>: the handshake on <paramref name="port"/>, the preamble given or guessed from the port.</summary>
    public static async Task<NetworkAnswer> TlsAsync(NetworkAccess access, NetworkBudget? budget, string host, int port, string? serverName, StartTls? preamble, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);

        // The server name is a host too (2026-10-23, the admin review's U1): a denied virtual host's certificate could be read by naming it
        // through an allowed shared front end. Judged by name before anything is spent: one host name, then the reach's LAN spelling and the
        // denied and allowed lists, as a lookup of a name is. The user's own /net is told, not refused.
        string? sniNote = null;
        if (!string.IsNullOrWhiteSpace(serverName))
        {
            string sni = serverName.Trim();
            if (NetworkAccess.OneHostProblem(sni) is not null || HostRules.TryParseAddress(sni.Trim('[', ']'), out _))
            {
                return new NetworkAnswer(NetworkText.BadServerName(sni));
            }

            if (!string.Equals(HostRules.NormalizeName(sni), HostRules.NormalizeName(host), StringComparison.OrdinalIgnoreCase))
            {
                var (_, sniRefusal) = await access.JudgeLookupAsync(HostRules.NormalizeName(sni), [], cancellationToken).ConfigureAwait(false);
                if (sniRefusal is not null)
                {
                    if (budget is not null)
                    {
                        DiagnosticLog.Info(NetworkText.Category, "Refused the server name " + sni + ": " + sniRefusal);
                        return new NetworkAnswer(sniRefusal);
                    }

                    sniNote = NetworkText.ModelWouldBeRefused(sniRefusal);
                }
            }
        }

        var target = await access.TargetAsync(host, budget, [port], cancellationToken).ConfigureAwait(false);
        if (target.Error is { } refused)
        {
            return new NetworkAnswer(refused);
        }

        var value = target.Value!;
        if (sniNote is not null)
        {
            value = value with { ModelNote = value.ModelNote is null ? sniNote : value.ModelNote + " " + sniNote };
        }

        string name = string.IsNullOrWhiteSpace(serverName) ? HostRules.NormalizeName(value.Host) : HostRules.NormalizeName(serverName.Trim());
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
        !pinger.SupportsIPv6 ? addresses.FirstOrDefault(a => HostRules.Canonical(a).AddressFamily == AddressFamily.InterNetwork) ?? addresses[0] : addresses[0];

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
        if (other is null || !pinger.SupportsIPv6)
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
