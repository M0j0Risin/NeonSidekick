using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Authentication.ExtendedProtection;
using NeonSidekick.Diagnostics;
using NeonSidekick.Network;

namespace NeonSidekick.ActiveDirectory.Ldap;

/// <summary>
/// The directory over the managed LDAP client (2026-10-18, the Mac AD plan; a Mac's backend, where <see cref="WindowsDirectorySystem"/> is
/// Windows'). <see cref="Run{T}"/>, on the caller's thread:
/// <list type="number">
/// <item>Find a DC: the server named, or for the domain's own name its DCs from DNS (<c>_ldap._tcp.dc._msdcs.&lt;domain&gt;</c> through
/// the app's own <see cref="DnsClient"/>, best priority first), falling back to the domain's name through the system's resolver, which
/// honours a VPN's split DNS (AD's domain name points at its DCs).</item>
/// <item>Read the RootDSE anonymously, for the DC's <c>dnsHostName</c>: the Kerberos name, so an alias, a load-balanced name,
/// <c>localhost</c> or an IP all sign in to the DC actually answering. That read is unsigned, so the name is taken only inside the
/// configured domain; otherwise the name connected to stands.</item>
/// <item>Bind with SASL GSSAPI (<see cref="SaslGssapiExchange"/>): sealed on 389, no layer under LDAPS.</item>
/// <item>Run the work's searches, then unbind and close, whatever happened.</item>
/// </list>
/// A <c>runas</c> sign-in that fails before the bind cannot be told from a KDC that isn't there by its status on a Mac (Phase 0: both are
/// <c>Unsupported</c>), so the DC's own port 88 is tried (every AD DC is a KDC; the user's call, 2026-10-18): answering, the failure is a
/// refused password (<see cref="LdapResultCodes.InvalidCredentials"/>, which <see cref="DirectoryAccess"/> holds); silent, the KDC is
/// unreachable and nothing is held. A firewall that passes 389 but not 88 reads as unreachable. Read-only by construction: the client has
/// no write operation (<see cref="LdapMessages"/>).
/// </summary>
public sealed class ManagedDirectorySystem : IDirectorySystem
{
    /// <summary>How long the KDC check may take.</summary>
    public static readonly TimeSpan KdcProbeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>How long a DC lookup in DNS may take.</summary>
    public static readonly TimeSpan LocateTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The most DCs tried in turn.</summary>
    public const int MaxCandidates = 3;

    private readonly Func<DirectoryLogin?, string, bool, ChannelBinding?, ISaslLayer> _layers;
    private readonly Func<string, bool> _kdcAnswers;
    private readonly Func<string, IReadOnlyList<string>> _locate;
    private readonly Func<string, int, bool, LdapConnection> _open;

    /// <summary>The app's: Kerberos through .NET, the DC's port 88, DNS for DCs, real sockets.</summary>
    public ManagedDirectorySystem()
        : this((login, host, tls, binding) => new NegotiateSaslLayer(login, host, tls, binding), KdcAnswers, Locate, LdapConnection.Open)
    {
    }

    /// <summary>The tests' (internal: a source pin keeps the app on the public one).</summary>
    internal ManagedDirectorySystem(
        Func<DirectoryLogin?, string, bool, ChannelBinding?, ISaslLayer> layers,
        Func<string, bool> kdcAnswers,
        Func<string, IReadOnlyList<string>> locate,
        Func<string, int, bool, LdapConnection> open)
    {
        _layers = layers;
        _kdcAnswers = kdcAnswers;
        _locate = locate;
        _open = open;
    }

    public T Run<T>(DirectoryTarget target, Func<IDirectorySession, T> work)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(work);
        var started = Stopwatch.GetTimestamp();
        var (connection, connectedTo) = Connect(target);
        ISaslLayer? layer = null;
        try
        {
            string? dnsHostName = Session.RootDseOf(connection).Text("dnsHostName");
            string host = KerberosHost(dnsHostName, connectedTo, target.Domain);
            layer = _layers(target.Login, host, target.Tls, connection.Binding);
            SaslSecurity security;
            int serverMaximum;
            try
            {
                (security, serverMaximum) = SaslGssapiExchange.Bind(connection, layer, target.Tls);
            }
            catch (DirectoryException ex) when (ex.Code == LdapResultCodes.KerberosFailed && target.Login is not null)
            {
                bool kdc = _kdcAnswers(connection.Remote ?? connectedTo);
                DiagnosticLog.Debug(DirectoryText.Category, $"Kerberos sign-in as {target.Login.User} failed; the DC's KDC {(kdc ? "answers" : "does not answer")}");
                throw kdc
                    ? new DirectoryException(LdapResultCodes.InvalidCredentials, "the Kerberos sign-in as " + target.Login.User + " was refused")
                    : new DirectoryException(LdapResultCodes.KdcUnreachable, "the KDC on " + connectedTo + " could not be reached");
            }

            connection.StartSecurity(layer, security, serverMaximum);
            DiagnosticLog.Debug(DirectoryText.Category, $"bound to {connectedTo}:{target.Port.ToString(CultureInfo.InvariantCulture)} as {NegotiateSaslLayer.ServiceName(host)}, {security} ({Stopwatch.GetElapsedTime(started).TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms)");
            string transport = DirectoryText.Transport(target.Port, target.Tls, NegotiateSaslLayer.ServiceName(host), DirectoryText.LayerWord(security.ToString()));
            var result = work(new Session(connection, transport));
            connection.Unbind();
            return result;
        }
        finally
        {
            layer?.Dispose();
            connection.Dispose();
        }
    }

    /// <summary>
    /// The Kerberos host: the RootDSE's <c>dnsHostName</c> when it lies inside <paramref name="domain"/> (or no domain is known), else the name
    /// connected to.
    /// </summary>
    public static string KerberosHost(string? dnsHostName, string connectedTo, string? domain)
    {
        ArgumentNullException.ThrowIfNull(connectedTo);
        if (string.IsNullOrWhiteSpace(dnsHostName))
        {
            return connectedTo;
        }

        string name = dnsHostName.Trim().TrimEnd('.');
        if (domain is null)
        {
            return name;
        }

        string suffix = domain.Trim().TrimEnd('.');
        return name.Equals(suffix, StringComparison.OrdinalIgnoreCase) || name.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase) ? name : connectedTo;
    }

    /// <summary>The DCs to try: the server named, or for the domain's own name the DCs DNS gives, then the name itself.</summary>
    public static IReadOnlyList<string> Candidates(DirectoryTarget target, Func<string, IReadOnlyList<string>> locate)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(locate);
        if (target.Domain is not { } domain || !string.Equals(target.Server.Trim().TrimEnd('.'), domain.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase))
        {
            return [target.Server];
        }

        return [.. locate(domain).Take(MaxCandidates), target.Server];
    }

    private (LdapConnection Connection, string Host) Connect(DirectoryTarget target)
    {
        DirectoryException? last = null;
        foreach (string host in Candidates(target, _locate).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                return (_open(host, target.Port, target.Tls), host);
            }
            catch (DirectoryException ex)
            {
                last = ex;
            }
        }

        throw last ?? new DirectoryException(LdapResultCodes.ConnectError, "no domain controller to connect to");
    }

    /// <summary>A domain's DCs from DNS, best priority (then weight) first; none when DNS has none or does not answer.</summary>
    public static IReadOnlyList<string> Locate(string domain)
    {
        ArgumentNullException.ThrowIfNull(domain);
        try
        {
            using var timeout = new CancellationTokenSource(LocateTimeout);
            var outcome = DnsClient.QueryAsync(DnsClient.SystemServers(), "_ldap._tcp.dc._msdcs." + domain.Trim().TrimEnd('.'), DnsWire.Srv, timeout.Token).GetAwaiter().GetResult();
            return SrvTargets(outcome.Reply?.Answers ?? []);
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException)
        {
            return [];
        }
    }

    /// <summary>SRV records' targets, by priority then weight (higher first).</summary>
    public static IReadOnlyList<string> SrvTargets(IReadOnlyList<DnsRecord> answers)
    {
        ArgumentNullException.ThrowIfNull(answers);
        var found = new List<(int Priority, int Weight, string Host)>();
        foreach (var record in answers.Where(a => a.Type == DnsWire.Srv))
        {
            string[] parts = record.Data.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 4 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int priority)
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int weight) && parts[3] is not "." and { Length: > 0 })
            {
                found.Add((priority, weight, parts[3].TrimEnd('.')));
            }
        }

        return found.OrderBy(f => f.Priority).ThenByDescending(f => f.Weight).Select(f => f.Host).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Whether something answers on <paramref name="address"/>'s port 88, in <see cref="KdcProbeTimeout"/>.</summary>
    public static bool KdcAnswers(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        try
        {
            using var client = new TcpClient();
            using var timeout = new CancellationTokenSource(KdcProbeTimeout);
            client.ConnectAsync(address, 88, timeout.Token).AsTask().GetAwaiter().GetResult();
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException)
        {
            return false;
        }
    }

    /// <summary>The session over a connection already bound (the tests').</summary>
    internal static IDirectorySession SessionOver(LdapConnection connection) => new Session(connection, null);

    /// <summary>One bound session: RootDSE and searches, sequential, each with its own time limit and read deadline.</summary>
    private sealed class Session(LdapConnection connection, string? transport) : IDirectorySession
    {
        public string? Transport => transport;

        /// <summary>The read deadline's margin past the server's own time limit.</summary>
        private static readonly TimeSpan Margin = TimeSpan.FromSeconds(5);

        public DirectoryEntry RootDse() => RootDseOf(connection);

        public static DirectoryEntry RootDseOf(LdapConnection connection)
        {
            var results = SearchOn(connection, "", DirectoryScope.Base, "(objectClass=*)", AdAttributes.RootDse, 1, paged: false, null);
            return results.Entries.Count > 0 ? results.Entries[0] : throw new DirectoryException(LdapResultCodes.NoSuchObject, "RootDSE answered nothing");
        }

        public DirectoryResults Search(string baseDn, DirectoryScope scope, string filter, IReadOnlyList<string> attributes, int sizeLimit, bool paged, TimeSpan? timeLimit = null) =>
            SearchOn(connection, baseDn, scope, filter, attributes, sizeLimit, paged, timeLimit);

        private static DirectoryResults SearchOn(LdapConnection connection, string baseDn, DirectoryScope scope, string filter, IReadOnlyList<string> attributes, int sizeLimit, bool paged, TimeSpan? timeLimit)
        {
            ArgumentNullException.ThrowIfNull(baseDn);
            ArgumentNullException.ThrowIfNull(filter);
            ArgumentNullException.ThrowIfNull(attributes);
            int seconds = Math.Max(1, (int)Math.Ceiling((timeLimit ?? DirectoryLimits.SearchTime).TotalSeconds));
            connection.ReadTimeout = TimeSpan.FromSeconds(seconds) + Margin;
            var entries = new List<DirectoryEntry>();
            byte[]? cookie = null;
            while (true)
            {
                int? pageSize = paged ? Math.Max(1, Math.Min(DirectoryLimits.PageSize, sizeLimit - entries.Count)) : null;
                int id = connection.NextId();
                connection.Send(LdapMessages.Search(id, baseDn, scope, filter, attributes, sizeLimit, seconds, pageSize, cookie));
                LdapReply done;
                while (true)
                {
                    var reply = connection.Receive();
                    if (reply.MessageId != id)
                    {
                        throw new DirectoryException(LdapResultCodes.DecodingError, "a reply to another request");
                    }

                    if (reply.Op == LdapOp.SearchResultEntry)
                    {
                        if (entries.Count < sizeLimit)
                        {
                            entries.Add(ToEntry(reply));
                        }

                        continue;
                    }

                    if (reply.Op == LdapOp.SearchResultReference)
                    {
                        continue;   // referrals are off, as on Windows
                    }

                    done = reply;
                    break;
                }

                if (done.ResultCode is LdapResultCodes.SizeLimitExceeded or LdapResultCodes.TimeLimitExceeded)
                {
                    return new DirectoryResults(entries, true, done.ResultCode == LdapResultCodes.TimeLimitExceeded);
                }

                if (done.ResultCode != LdapResultCodes.Success)
                {
                    throw new DirectoryException(done.ResultCode, "the search failed" + (done.Diagnostic.Length > 0 ? ": " + done.Diagnostic : ""));
                }

                cookie = done.Cookie;
                if (!paged || cookie is not { Length: > 0 } || entries.Count >= sizeLimit)
                {
                    return new DirectoryResults(entries, entries.Count >= sizeLimit && cookie is { Length: > 0 }, false);
                }
            }
        }

        private static DirectoryEntry ToEntry(LdapReply reply)
        {
            var attributes = new Dictionary<string, IReadOnlyList<byte[]>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, values) in reply.Attributes ?? [])
            {
                if (!AdAttributes.NeverReadName(name))
                {
                    attributes[name] = values;
                }
            }

            return new DirectoryEntry(reply.Dn ?? "", attributes);
        }
    }
}
