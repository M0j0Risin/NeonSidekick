using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;
using NeonSidekick.Web;

namespace NeonSidekick.Network;

/// <summary>What one network step came to: its value, or the sentence saying why not (never both).</summary>
public readonly record struct NetworkResult<T>(T? Value, string? Error)
{
    public static NetworkResult<T> Ok(T value) => new(value, null);

    public static NetworkResult<T> Failed(string error) => new(default, error);
}

/// <summary>A host a check may reach: its name as asked, the addresses admitted, and (for <c>/net</c>) the note when the model would be refused.</summary>
public sealed record NetworkTarget(string Host, IReadOnlyList<IPAddress> Addresses, string? ModelNote = null);

/// <summary>
/// The network tools' one way in (2026-10-15, the admin-tools plan), the tools' and <c>/net</c>'s alike: one host (no ranges, blocks or
/// wildcards), resolved through the injected resolver (<c>WebFetcher</c>'s shape), every address judged by <see cref="LanPolicy.Admit"/> under
/// <c>Network tools reach</c> (<see cref="NetworkToolsReach"/>), then by the denied and allowed lists (<see cref="HostRules"/>: deny wins; an
/// empty allow list admits what reach admits), then counted in the turn's <see cref="NetworkBudget"/>. <c>/net</c> passes no budget: it is the
/// user's own hand, judged by nothing, the model's refusal shown as a dim note. Configured hosts of the other admin families never come here.
/// </summary>
public sealed class NetworkAccess
{
    private static readonly ConcurrentDictionary<string, byte> s_warned = new(StringComparer.Ordinal);
    private readonly Func<AppSettingsData> _effective;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
    private readonly Func<IReadOnlyList<IPEndPoint>> _systemDns;

    /// <param name="effective">The settings in force, read at each call.</param>
    /// <param name="pinger">The echo; null where there is none (the ping and trace tools are then not offered).</param>
    /// <param name="resolve">The resolver (<see cref="Dns.GetHostAddressesAsync(string, CancellationToken)"/> when null).</param>
    /// <param name="connect">The TCP connect <c>net_port</c> uses (<see cref="NetworkProbes.ConnectAsync"/> when null).</param>
    /// <param name="systemDns">The system's resolvers (<see cref="DnsClient.SystemServers"/> when null).</param>
    /// <param name="time">The clock: certificates' days left, the zone an answer shows.</param>
    public NetworkAccess(Func<AppSettingsData> effective, IPinger? pinger, TimeProvider time, Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null,
        Func<IPAddress, int, CancellationToken, Task<PortState>>? connect = null, Func<IReadOnlyList<IPEndPoint>>? systemDns = null)
    {
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
        Pinger = pinger;
        Time = time ?? throw new ArgumentNullException(nameof(time));
        _resolve = resolve ?? ((host, ct) => Dns.GetHostAddressesAsync(host, ct));
        Connect = connect ?? NetworkProbes.ConnectAsync;
        _systemDns = systemDns ?? DnsClient.SystemServers;
    }

    public IPinger? Pinger { get; }

    public TimeProvider Time { get; }

    public Func<IPAddress, int, CancellationToken, Task<PortState>> Connect { get; }

    /// <summary>The settings in force now.</summary>
    public AppSettingsData Effective => _effective();

    /// <summary>The system's resolvers, read now.</summary>
    public IReadOnlyList<IPEndPoint> SystemDns() => _systemDns();

    /// <summary>The most distinct hosts a turn may check, clamped.</summary>
    public int MaxHosts => Math.Clamp(_effective().NetworkMaxHostsPerTurn, AppSettingsData.MinNetworkMaxHosts, AppSettingsData.MaxNetworkMaxHosts);

    /// <summary>The most distinct host:port pairs a turn may check, clamped.</summary>
    public int MaxPorts => Math.Clamp(_effective().NetworkMaxPortsPerTurn, AppSettingsData.MinNetworkMaxPorts, AppSettingsData.MaxNetworkMaxPorts);

    /// <summary>
    /// The text of a target judged as one host: not a range (<c>10.0.0.1-20</c>), a block (<c>/24</c>), a wildcard, a list or carrying a port or a
    /// scheme. The refusal's sentence, or null when it is one host.
    /// </summary>
    public static string? OneHostProblem(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        string text = host.Trim();
        if (text.Length == 0)
        {
            return NetworkText.Missing("host");
        }

        if (text.Contains("://", StringComparison.Ordinal))
        {
            return NetworkText.PortInHost(text);
        }

        if (text.IndexOfAny(['/', '*', ',', ';', ' ', '\t', '?']) >= 0)
        {
            return NetworkText.NotOneHost(text);
        }

        string bare = text.Trim('[', ']');
        if (HostRules.TryParseAddress(bare, out _))
        {
            return null;
        }

        // 10.1, 167772161, 0x0a.0.0.1: the parser's legacy spellings of an address, refused rather than read as one the model never wrote.
        if (IPAddress.TryParse(bare, out _))
        {
            return NetworkText.BadName(text);
        }

        if (text.Contains(':', StringComparison.Ordinal))
        {
            return NetworkText.PortInHost(text);
        }

        // 10.0.0.1-20: a range by its spelling.
        int dash = text.LastIndexOf('-');
        if (dash > 0 && IPAddress.TryParse(text[..dash], out _))
        {
            return NetworkText.NotOneHost(text);
        }

        return HostRules.NormalizeName(text) is { Length: > 0 } name && HostRules.IsHostName(name) ? null : NetworkText.BadName(text);
    }

    /// <summary>
    /// The host a check reaches: one host, resolved, judged and counted. <paramref name="budget"/> null is the user's hand (<c>/net</c>): every
    /// resolved address used, the model's refusal (if any) as <see cref="NetworkTarget.ModelNote"/>. <paramref name="ports"/> are counted
    /// against the port budget with the host.
    /// </summary>
    public async Task<NetworkResult<NetworkTarget>> TargetAsync(string host, NetworkBudget? budget, IReadOnlyCollection<int> ports, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(ports);
        if (OneHostProblem(host) is { } problem)
        {
            return NetworkResult<NetworkTarget>.Failed(problem);
        }

        string text = host.Trim();
        var resolved = await ResolveAsync(text, cancellationToken).ConfigureAwait(false);
        if (resolved.Error is { } unresolved)
        {
            return NetworkResult<NetworkTarget>.Failed(unresolved);
        }

        var addresses = resolved.Value!;
        var aliases = await DeniedAddressesAsync(cancellationToken).ConfigureAwait(false);
        var (admitted, refusal) = Judge(text, addresses, aliases);
        if (budget is null)
        {
            return NetworkResult<NetworkTarget>.Ok(new NetworkTarget(text, addresses, refusal is null ? null : NetworkText.ModelWouldBeRefused(refusal)));
        }

        if (refusal is not null)
        {
            DiagnosticLog.Info(NetworkText.Category, "Refused " + text + ": " + refusal);
            return NetworkResult<NetworkTarget>.Failed(refusal);
        }

        if (budget.Take(text, admitted, ports, MaxHosts, MaxPorts) is { } spent)
        {
            DiagnosticLog.Info(NetworkText.Category, "Refused " + text + ": " + spent);
            return NetworkResult<NetworkTarget>.Failed(spent);
        }

        return NetworkResult<NetworkTarget>.Ok(new NetworkTarget(text, admitted));
    }

    /// <summary>A host's addresses: a literal as itself, a name through the resolver; the refusal's sentence when it resolves to none.</summary>
    public async Task<NetworkResult<IReadOnlyList<IPAddress>>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        string bare = host.Trim().Trim('[', ']');
        if (HostRules.TryParseAddress(bare, out var literal))
        {
            return NetworkResult<IReadOnlyList<IPAddress>>.Ok([literal]);
        }

        string name = HostRules.NormalizeName(bare);
        try
        {
            var addresses = await _resolve(name, cancellationToken).ConfigureAwait(false);
            return addresses.Length == 0
                ? NetworkResult<IReadOnlyList<IPAddress>>.Failed(NetworkText.NoAddresses(host))
                : NetworkResult<IReadOnlyList<IPAddress>>.Ok(addresses.Distinct().ToList());
        }
        catch (SocketException ex)
        {
            return NetworkResult<IReadOnlyList<IPAddress>>.Failed(NetworkText.NotResolved(host, ex.Message));
        }
        catch (ArgumentException ex)
        {
            return NetworkResult<IReadOnlyList<IPAddress>>.Failed(NetworkText.NotResolved(host, ex.Message));
        }
    }

    /// <summary>
    /// The model's judgement of <paramref name="host"/> at <paramref name="addresses"/>: reach first (<see cref="LanPolicy.Admit"/>), then the
    /// denied list (the name, then each address — by the list's literals and blocks, and by <paramref name="deniedAliases"/>, the addresses
    /// the list's names resolve to; a denied address dropped, none left a refusal), then the allowed list (the name matching admits every
    /// address; else only the matching addresses). The addresses kept, or the refusal. Pure over the settings.
    /// </summary>
    public (IReadOnlyList<IPAddress> Addresses, string? Refusal) Judge(string host, IReadOnlyList<IPAddress> addresses, IReadOnlyDictionary<IPAddress, HostRule>? deniedAliases = null)
    {
        var effective = _effective();
        var admission = LanPolicy.Admit(host, addresses, NetworkToolsReach.Resolve(effective));
        if (!admission.Admitted)
        {
            return ([], NetworkText.ReachRefused(host, admission.Refusal));
        }

        var denied = Rules(effective.NetworkDeniedHosts, NetworkText.DeniedSetting);
        var allowed = Rules(effective.NetworkAllowedHosts, NetworkText.AllowedSetting);
        var kept = admission.Addresses.Count > 0 ? admission.Addresses : addresses;
        if (HostRules.Match(denied, host, null) is { } byName)
        {
            return ([], NetworkText.Denied(host, byName.Text));
        }

        HostRule? firstDenied = null;
        var notDenied = new List<IPAddress>(kept.Count);
        foreach (var address in kept)
        {
            // An address carrying an IPv4 one (NAT64, 6to4) is denied by that one's entries too.
            if ((HostRules.Match(denied, null, address) ?? (LanPolicy.EmbeddedIPv4(address) is { } carried ? HostRules.Match(denied, null, carried) : null)) is { } entry)
            {
                firstDenied ??= entry;
            }
            else if (deniedAliases is not null && deniedAliases.TryGetValue(HostRules.Canonical(address), out var alias))
            {
                firstDenied ??= alias;
            }
            else
            {
                notDenied.Add(address);
            }
        }

        if (notDenied.Count == 0 && firstDenied is not null)
        {
            return ([], NetworkText.Denied(host, firstDenied.Text));
        }

        if (allowed.Count == 0 || HostRules.Match(allowed, host, null) is not null)
        {
            return (notDenied, null);
        }

        var allowedAddresses = notDenied.Where(a => HostRules.Match(allowed, null, a) is not null).ToList();
        return allowedAddresses.Count == 0 ? ([], NetworkText.NotAllowed(host)) : (allowedAddresses, null);
    }

    /// <summary>How long the denied list's names may take to resolve, all of them together.</summary>
    public static readonly TimeSpan DeniedNamesTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The addresses the denied list's names resolve to now, each with its entry (2026-10-19, the network review: <c>fs01.corp.example</c>
    /// denied was still reached by its address or by any alias name, a CNAME included, since a name entry matched only the name typed).
    /// Resolved side by side under <see cref="DeniedNamesTimeout"/>; a name that does not resolve in time denies nothing more than itself.
    /// Globs cannot be resolved and still match names alone. Empty when the list holds no name.
    /// </summary>
    public async Task<IReadOnlyDictionary<IPAddress, HostRule>> DeniedAddressesAsync(CancellationToken cancellationToken)
    {
        var names = Rules(_effective().NetworkDeniedHosts, NetworkText.DeniedSetting)
            .Where(r => r.Kind == HostRuleKind.Name && !IPAddress.TryParse(r.Name, out _)).ToList();
        if (names.Count == 0)
        {
            return new Dictionary<IPAddress, HostRule>();
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(DeniedNamesTimeout);
        var lookups = names.Select(async rule =>
        {
            try
            {
                return (rule, await _resolve(rule.Name!, budget.Token).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is SocketException or ArgumentException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                return (rule, Array.Empty<IPAddress>());
            }
        }).ToList();
        var found = new Dictionary<IPAddress, HostRule>();
        foreach (var (rule, addresses) in await Task.WhenAll(lookups).ConfigureAwait(false))
        {
            foreach (var address in addresses)
            {
                found.TryAdd(HostRules.Canonical(address), rule);
            }
        }

        return found;
    }

    /// <summary>
    /// The model's judgement of a DNS question (2026-10-19, the network review: a lookup through the system's resolvers was judged by nothing
    /// and counted by nothing, so PTRs of 192.168.1.1–254 or A lookups of internal names mapped a LAN the reach refused). The question's
    /// <paramref name="addresses"/> — the reverse name's address for a PTR, what an A/AAAA resolved to — judged as a host's are
    /// (<see cref="Judge"/>, the denied names' addresses too); with none (another type of a name) the name alone: a LAN spelling under a
    /// reach that refuses the LAN, then the lists by name. The addresses kept, or the refusal.
    /// </summary>
    public async Task<(IReadOnlyList<IPAddress> Addresses, string? Refusal)> JudgeLookupAsync(string name, IReadOnlyList<IPAddress> addresses, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(addresses);
        if (addresses.Count > 0)
        {
            var aliases = await DeniedAddressesAsync(cancellationToken).ConfigureAwait(false);
            return Judge(name, addresses, aliases);
        }

        var effective = _effective();
        if (LanPolicy.IsPrivateHost(name) && LanPolicy.Judge(NetworkToolsReach.Resolve(effective), lan: true) is var refusal and not NetworkRefusal.None)
        {
            return ([], NetworkText.ReachRefused(name, refusal));
        }

        if (HostRules.Match(Rules(effective.NetworkDeniedHosts, NetworkText.DeniedSetting), name, null) is { } denied)
        {
            return ([], NetworkText.Denied(name, denied.Text));
        }

        var allowed = Rules(effective.NetworkAllowedHosts, NetworkText.AllowedSetting);
        return allowed.Count == 0 || HostRules.Match(allowed, name, null) is not null ? ([], null) : ([], NetworkText.NotAllowed(name));
    }

    /// <summary>A saved list's usable entries; each unreadable one warned once per run.</summary>
    private static IReadOnlyList<HostRule> Rules(IReadOnlyList<string>? saved, string setting)
    {
        foreach (string problem in HostRules.Problems(saved))
        {
            if (s_warned.TryAdd(setting + "|" + problem, 0))
            {
                DiagnosticLog.Warn(NetworkText.Category, NetworkText.HostListProblemLogLine(setting, problem));
            }
        }

        return HostRules.ReadList(saved);
    }
}
