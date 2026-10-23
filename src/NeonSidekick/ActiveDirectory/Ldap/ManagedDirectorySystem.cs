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
/// honours a VPN's split DNS (AD's domain name points at its DCs). Since 2026-10-20 that last one signs in as <c>ldap/&lt;domain&gt;</c>,
/// which AD does not register, so where the app's DNS finds no DCs the server must name one.</item>
/// <item>Bind with SASL GSSAPI (<see cref="SaslGssapiExchange"/>) as <c>ldap/&lt;the name connected to&gt;</c>: the server named, or the DC DNS
/// gave (2026-10-20, the code review: the RootDSE's <c>dnsHostName</c>, read unsigned before the bind, chose the name until then, so a
/// machine in the middle could name any computer of the domain holding an <c>ldap/</c> name and pass mutual authentication as it; wldap32
/// uses the name as given too). An alias, <c>localhost</c> or an IP therefore needs a name the DC's account holds: name the DC itself.
/// Sealed on 389, no layer under LDAPS; the bind under <see cref="BindTime"/>.</item>
/// <item>Run the work's searches, each under its own deadline and message cap, then unbind and close, whatever happened.</item>
/// </list>
/// A <c>runas</c> sign-in that cannot get its first ticket says no more on a Mac (Phase 0: a wrong password and a KDC that isn't there are
/// both <c>Unsupported</c>; <see cref="LdapResultCodes.TicketRefused"/>), so the DC's own KDC is asked about the account with no password
/// (<see cref="KdcProbe"/>; every AD DC is a KDC; the user's call, 2026-10-18, narrowed 2026-10-20 after the code review found a skewed clock or a
/// Mac's own KDC on <c>localhost</c>'s port 88 held a right password): one that knows the account with the clocks agreeing, or says it is locked
/// or disabled, makes the failure a refused password (<see cref="LdapResultCodes.InvalidCredentials"/>, which <see cref="DirectoryAccess"/>
/// holds); an account it does not know is <see cref="LdapResultCodes.UnknownAccount"/> (what a Mac's own KDC says of any domain account, and
/// nothing to lock); a clock off by more than five minutes is <see cref="LdapResultCodes.ClockSkew"/>; silence is
/// <see cref="LdapResultCodes.KdcUnreachable"/>; anything else <see cref="LdapResultCodes.KerberosFailed"/>. None but the first is held. Any other
/// Kerberos failure came after the password was taken and is never a refused one, nor is the server's own 0x31 to a GSSAPI bind. Read-only by
/// construction: the client has no write operation (<see cref="LdapMessages"/>).
/// </summary>
public sealed class ManagedDirectorySystem : IDirectorySystem
{
    /// <summary>How long the bind may take, every round trip together (wldap32's connect timeout, as <see cref="DirectoryAccess"/> counts it).</summary>
    public static readonly TimeSpan BindTime = TimeSpan.FromSeconds(10);

    /// <summary>How long a DC lookup in DNS may take.</summary>
    public static readonly TimeSpan LocateTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The most DCs tried in turn.</summary>
    public const int MaxCandidates = 3;

    private readonly Func<DirectoryLogin?, string, bool, ChannelBinding?, ISaslLayer> _layers;
    private readonly Func<string, string, KdcAnswer> _askKdc;
    private readonly Func<string, IReadOnlyList<string>> _locate;
    private readonly Func<string, int, bool, LdapConnection> _open;

    /// <summary>The app's: Kerberos through .NET, the DC's KDC asked on port 88, DNS for DCs, real sockets.</summary>
    public ManagedDirectorySystem()
        : this((login, host, tls, binding) => new NegotiateSaslLayer(login, host, tls, binding), KdcProbe.Ask, Locate, LdapConnection.Open)
    {
    }

    /// <summary>The tests' (internal: a source pin keeps the app on the public one).</summary>
    internal ManagedDirectorySystem(
        Func<DirectoryLogin?, string, bool, ChannelBinding?, ISaslLayer> layers,
        Func<string, string, KdcAnswer> askKdc,
        Func<string, IReadOnlyList<string>> locate,
        Func<string, int, bool, LdapConnection> open)
    {
        _layers = layers;
        _askKdc = askKdc;
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
            layer = _layers(target.Login, connectedTo, target.Tls, connection.Binding);
            SaslSecurity security;
            int serverMaximum;
            try
            {
                using (connection.Within(BindTime))
                {
                    (security, serverMaximum) = SaslGssapiExchange.Bind(connection, layer, target.Tls);
                }
            }
            catch (DirectoryException ex) when (ex.Code == LdapResultCodes.TicketRefused)
            {
                if (target.Login is not { } login)
                {
                    throw new DirectoryException(LdapResultCodes.KerberosFailed, ex.Message);
                }

                string address = connection.Remote ?? connectedTo;
                var answer = _askKdc(address, login.User);
                DiagnosticLog.Debug(DirectoryText.Category, $"Kerberos sign-in as {login.User} got no ticket; the KDC on {address} says {answer.Verdict}"
                    + (answer.ErrorCode is { } code ? " (" + code.ToString(CultureInfo.InvariantCulture) + ")" : ""));
                throw Refusal(answer, login.User, connectedTo);
            }
            catch (DirectoryException ex) when (ex.Code == LdapResultCodes.InvalidCredentials)
            {
                // The password was taken at the KDC before the bind: the DC's own 0x31 to a GSSAPI bind is a ticket it would not take (a clock
                // off, a key out of date), never a wrong password to hold (2026-10-20, the code review).
                throw new DirectoryException(LdapResultCodes.KerberosFailed, ex.Message);
            }

            connection.StartSecurity(layer, security, serverMaximum);
            string service = NegotiateSaslLayer.ServiceName(connectedTo);
            DiagnosticLog.Debug(DirectoryText.Category, $"bound to {connectedTo}:{target.Port.ToString(CultureInfo.InvariantCulture)} as {service}, {security} ({Stopwatch.GetElapsedTime(started).TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms)");
            string transport = DirectoryText.Transport(target.Port, target.Tls, service, DirectoryText.LayerWord(security.ToString()));
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
    /// What a ticket refused before the bind is, by the KDC's answer about the account (<see cref="KdcProbe"/>): a refused password when it knows
    /// the account and the clocks agree, or says it is locked or disabled; otherwise the account, the clock, the KDC or the sign-in, none of them
    /// held.
    /// </summary>
    public static DirectoryException Refusal(KdcAnswer answer, string user, string connectedTo)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(connectedTo);
        return answer.Verdict switch
        {
            KdcVerdict.KnowsTheAccount =>
                new DirectoryException(LdapResultCodes.InvalidCredentials, DirectoryText.NoTicketPrefix + " as " + user),
            KdcVerdict.Revoked => new DirectoryException(LdapResultCodes.AccountRevoked, "the KDC on " + connectedTo + " says " + user + " is locked, disabled or expired"),
            KdcVerdict.UnknownAccount => new DirectoryException(LdapResultCodes.UnknownAccount, "the KDC on " + connectedTo + " does not know " + user),
            KdcVerdict.ClockSkew when answer.Skew is { } skew => new DirectoryException(LdapResultCodes.ClockSkew, DirectoryText.ClockOff(skew)),
            KdcVerdict.Unreachable => new DirectoryException(LdapResultCodes.KdcUnreachable, "the KDC on " + connectedTo + " could not be reached"),
            _ => new DirectoryException(LdapResultCodes.KerberosFailed, "the Kerberos sign-in as " + user + " failed; the KDC on " + connectedTo + " answered "
                + (answer.ErrorCode is { } code ? "error " + code.ToString(CultureInfo.InvariantCulture) : "nothing this client reads")),
        };
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

    /// <summary>The session over a connection already bound (the tests').</summary>
    internal static IDirectorySession SessionOver(LdapConnection connection) => new Session(connection, null);

    /// <summary>
    /// One bound session: RootDSE and searches, sequential, each with its own time limit and read deadline, and (2026-10-20, the code review)
    /// a deadline over the whole search, every page and the abandon, and a cap on the replies one request may take, so a server trickling bytes or
    /// sending entries without end cannot hold the thread: past either the connection is closed. A paged search cut at its size limit with a
    /// cookie left is abandoned (a page size of 0 with the cookie, RFC 2696), as wldap32's <c>ldap_search_abandon_page</c>, so the server frees it.
    /// </summary>
    private sealed class Session(LdapConnection connection, string? transport) : IDirectorySession
    {
        public string? Transport => transport;

        /// <summary>The read deadline's margin past the server's own time limit.</summary>
        private static readonly TimeSpan Margin = TimeSpan.FromSeconds(5);

        /// <summary>The replies beyond the entries asked for (references, mostly) one request may take.</summary>
        public const int MaxExtraReplies = 1000;

        public DirectoryEntry RootDse() => RootDseOf(connection);

        private static DirectoryEntry RootDseOf(LdapConnection connection)
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
            using var deadline = connection.Within(TimeSpan.FromSeconds(seconds) + Margin);
            var entries = new List<DirectoryEntry>();
            byte[]? cookie = null;
            try
            {
                while (true)
                {
                    int? pageSize = paged ? Math.Max(1, Math.Min(DirectoryLimits.PageSize, sizeLimit - entries.Count)) : null;
                    int id = connection.Search(baseDn, scope, filter, attributes, sizeLimit, seconds, pageSize, cookie);
                    var done = Replies(connection, id, Math.Max(1, pageSize ?? sizeLimit), reply =>
                    {
                        if (entries.Count < sizeLimit)
                        {
                            entries.Add(ToEntry(reply));
                        }
                    });

                    if (done.ResultCode is LdapResultCodes.SizeLimitExceeded or LdapResultCodes.TimeLimitExceeded)
                    {
                        return new DirectoryResults(entries, true, done.ResultCode == LdapResultCodes.TimeLimitExceeded);
                    }

                    if (done.ResultCode != LdapResultCodes.Success)
                    {
                        // The server's text cut and scrubbed before it is anyone's message (2026-10-23, the admin review's F5: up to 16 MB, raw, into
                        // the exception, the log and the model's answer).
                        throw new DirectoryException(done.ResultCode, "the search failed" + (done.Diagnostic.Length > 0 ? ": " + Llm.Tools.UntrustedText.Cut(Llm.Tools.UntrustedText.OneLine(done.Diagnostic), 300) : ""));
                    }

                    cookie = done.Cookie;
                    if (!paged || cookie is not { Length: > 0 } || entries.Count >= sizeLimit)
                    {
                        bool cut = entries.Count >= sizeLimit && cookie is { Length: > 0 };
                        if (cut && paged)
                        {
                            Abandon(connection, baseDn, scope, filter, attributes, sizeLimit, seconds, cookie!);
                        }

                        return new DirectoryResults(entries, cut, false);
                    }
                }
            }
            catch (DirectoryException ex) when (ex.Code == LdapResultCodes.Timeout && connection.Expired && entries.Count > 0)
            {
                // The pages read stand, as wldap32's do (2026-10-23, the admin review's B5: the deadline's end threw them away with the
                // connection); the connection is gone, so a later search on the session fails rather than reading out of step.
                return new DirectoryResults(entries, true, true);
            }
        }

        /// <summary>
        /// The replies to request <paramref name="id"/> up to its result: each entry handed to <paramref name="entry"/>, references passed over,
        /// at most <paramref name="asked"/> entries and <see cref="MaxExtraReplies"/> more before the connection is closed as broken.
        /// </summary>
        private static LdapReply Replies(LdapConnection connection, int id, int asked, Action<LdapReply> entry)
        {
            int taken = 0;
            while (true)
            {
                var reply = connection.Receive();
                if (reply.MessageId != id)
                {
                    // Closed, as the too-many-replies branch is (2026-10-23, the admin review's O8): left open, every later search read the
                    // wrong replies.
                    connection.Dispose();
                    throw new DirectoryException(LdapResultCodes.DecodingError, "a reply to another request");
                }

                if (reply.Op is LdapOp.SearchResultEntry or LdapOp.SearchResultReference)
                {
                    if (++taken > asked + MaxExtraReplies)
                    {
                        connection.Dispose();
                        throw new DirectoryException(LdapResultCodes.DecodingError, "the server sent more replies than were asked for");
                    }

                    if (reply.Op == LdapOp.SearchResultEntry)
                    {
                        entry(reply);
                    }

                    continue;   // a reference: referrals are off, as on Windows
                }

                return reply;
            }
        }

        /// <summary>
        /// Ends a paged search before its last page (RFC 2696: the same search with a page size of 0 and the cookie), reading the server's answer
        /// so the next request starts clean. Best effort: a failure closes the connection, the results already read standing.
        /// </summary>
        private static void Abandon(LdapConnection connection, string baseDn, DirectoryScope scope, string filter, IReadOnlyList<string> attributes, int sizeLimit, int seconds, byte[] cookie)
        {
            try
            {
                int id = connection.Search(baseDn, scope, filter, attributes, sizeLimit, seconds, 0, cookie);
                Replies(connection, id, 0, _ => { });
            }
            catch (DirectoryException ex)
            {
                DiagnosticLog.Debug(DirectoryText.Category, $"abandoning a paged search failed ({ex.Message}); the connection is closed");
                connection.Dispose();
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
