namespace NeonSidekick.ActiveDirectory;

/// <summary>One directory object: its distinguished name and its attributes' raw values (an attribute's options, <c>;range=…</c>, kept in its name).</summary>
public sealed record DirectoryEntry(string Dn, IReadOnlyDictionary<string, IReadOnlyList<byte[]>> Attributes)
{
    /// <summary>An attribute's values (name case ignored, a range option ignored).</summary>
    public IReadOnlyList<byte[]> Values(string name)
    {
        foreach (var (key, values) in Attributes)
        {
            int semi = key.IndexOf(';', StringComparison.Ordinal);
            if (string.Equals(semi < 0 ? key : key[..semi], name, StringComparison.OrdinalIgnoreCase))
            {
                return values;
            }
        }

        return [];
    }

    /// <summary>An attribute's first value as UTF-8 text; null when absent.</summary>
    public string? Text(string name) => Values(name) is { Count: > 0 } values ? System.Text.Encoding.UTF8.GetString(values[0]) : null;

    /// <summary>Every value as UTF-8 text.</summary>
    public IReadOnlyList<string> Texts(string name) => Values(name).Select(v => System.Text.Encoding.UTF8.GetString(v)).ToList();

    /// <summary>The returned attribute name carrying a range option for <paramref name="name"/> (<c>member;range=0-1499</c>); null when none.</summary>
    public string? RangedName(string name) =>
        Attributes.Keys.FirstOrDefault(k => k.StartsWith(name + ";range=", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A many-valued attribute's values as text, and whether only its first range came back (2026-10-15, the user's ask, the AD gaps plan): past
    /// the server's <c>MaxValRange</c> (1,500 by default, up to 5,000 on 2008 and later) a value list comes back as <c>memberOf;range=0-1499</c>,
    /// which <see cref="Values"/> already reads; <c>Partial</c> says the range named an end, so "via", direct reports and an entry's groups never
    /// come back silently short. No further rounds are read here.
    /// </summary>
    public (IReadOnlyList<string> Values, bool Partial) ValuesOrFirstRange(string name)
    {
        string? ranged = RangedName(name);
        return (Texts(name), ranged is not null && !ranged.EndsWith("-*", StringComparison.Ordinal));
    }
}

/// <summary>How a search goes: base, one level or the subtree.</summary>
public enum DirectoryScope
{
    Base = 0,
    OneLevel = 1,
    Subtree = 2,
}

/// <summary>
/// What a search gave: the entries, whether a limit cut it short, and whether that limit was time (2026-10-15, the AD gaps plan: a note then says
/// "stopped at the time limit", not the size cap).
/// </summary>
public sealed record DirectoryResults(IReadOnlyList<DirectoryEntry> Entries, bool Truncated, bool TimedOut = false);

/// <summary>Who a session binds as: the account and its password (never logged), handed to Negotiate as an explicit identity.</summary>
public sealed record DirectoryLogin(string User, string Password)
{
    /// <summary>The account alone (2026-10-23, the admin review's K9: the generated ToString printed the password, into a log or an exception).</summary>
    public override string ToString() => $"DirectoryLogin {{ User = {User} }}";
}

/// <summary>
/// Where a session goes: the server (a DC, or the domain's name), its port, LDAPS or not, and the sign-in; and the domain's DNS name
/// (2026-10-18, the Mac AD plan: the managed client finds a domain's DCs by it and trusts a DC's own name only inside it; wldap32 ignores it).
/// </summary>
public sealed record DirectoryTarget(string Server, int Port, bool Tls, DirectoryLogin? Login, string? Domain = null);

/// <summary>A failure from the LDAP layer: the LDAP result code (or a Win32 code) and where.</summary>
public sealed class DirectoryException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>One bound session: the searches the tools and <c>/ad</c> make. Nothing here writes.</summary>
public interface IDirectorySession
{
    /// <summary>The RootDSE (no bind needed, but read on the bound session).</summary>
    DirectoryEntry RootDse();

    /// <summary>
    /// A search: <paramref name="attributes"/> only (never <c>*</c>), at most <paramref name="sizeLimit"/> entries; with <paramref name="paged"/>
    /// the paged-results control in pages of <see cref="DirectoryLimits.PageSize"/>; <paramref name="timeLimit"/> the most it may take on the server
    /// and the client (null: <see cref="DirectoryLimits.SearchTime"/>; 2026-10-15, the AD gaps plan: a walk's searches get what its deadline has left).
    /// </summary>
    DirectoryResults Search(string baseDn, DirectoryScope scope, string filter, IReadOnlyList<string> attributes, int sizeLimit, bool paged, TimeSpan? timeLimit = null);

    /// <summary>
    /// How the session is connected, for <c>--ad-check</c> (2026-10-18, the Mac AD plan: "389, Kerberos as ldap/dc1.corp.example, sealed");
    /// null where the backend does not say (wldap32's, the fakes).
    /// </summary>
    string? Transport => null;
}

/// <summary>
/// The directory (2026-10-15, the admin-tools plan): <see cref="WindowsDirectorySystem"/> over wldap32 in-process, read-only by construction;
/// tests pass <c>FakeDirectorySystem</c>; null off Windows, where every call says there is no directory here. A session is opened, bound,
/// worked and unbound inside <see cref="Run{T}"/> on the caller's thread.
/// </summary>
public interface IDirectorySystem
{
    T Run<T>(DirectoryTarget target, Func<IDirectorySession, T> work);
}

/// <summary>The searches' bounds (2026-10-15, the plan's item 11).</summary>
public static class DirectoryLimits
{
    public const int PageSize = 500;

    /// <summary>Pages past <c>sizeLimit / PageSize</c> a paged walk may take (short pages) before it stops as cut (2026-10-23, the admin review's N1).</summary>
    public const int PageSlack = 8;

    /// <summary>Empty pages in a row a paged walk takes before it stops as cut (N1: RFC 2696 allows one; a server answering only those spun the walk).</summary>
    public const int MaxEmptyPages = 3;

    /// <summary>The most entries one call reads, the client-side refining included.</summary>
    public const int MaxEntries = 1000;

    /// <summary>
    /// The objects one redacted free-text match reads (2026-10-23, the admin review's E2, decision DA5): their descriptions matched here, the
    /// server never asked about them; a <c>scan_from</c> goes on past them.
    /// </summary>
    public const int RedactedScan = 5000;

    /// <summary>
    /// The furthest a redacted scan starts (2026-10-23, the branch review of DP7): each page reads from the first object, so a start of a
    /// million held a million entries in memory; twenty pages' worth.
    /// </summary>
    public const int MaxScanFrom = 20 * RedactedScan;

    /// <summary>The time one search may take, on the server and on the client.</summary>
    public static readonly TimeSpan SearchTime = TimeSpan.FromSeconds(30);

    /// <summary>How many SIDs one search resolves.</summary>
    public const int SidBatch = 50;

    /// <summary>The range a multi-valued attribute is read in past which it is read again.</summary>
    public const int RangeStep = 1500;

    /// <summary>
    /// The most entries a group's members, or a <c>to</c> save, read (2026-10-15, the AD gaps plan): the sort and the total need them all, where
    /// <see cref="MaxEntries"/> stays the most an answer lists.
    /// </summary>
    public const int MaxCounted = 10_000;

    /// <summary>The most groups a nested walk reads the members of (cycles end on a visited set before this).</summary>
    public const int MaxWalkedGroups = 200;

    /// <summary>The time a walk leaves itself: no search starts with less.</summary>
    public static readonly TimeSpan LeastSearchTime = TimeSpan.FromSeconds(2);

    /// <summary>A call's own budget from its start (<see cref="DirectoryAccess.Timeout"/>'s 45 s covers the bind too).</summary>
    public static readonly TimeSpan CallBudget = TimeSpan.FromSeconds(40);
}

/// <summary>
/// When a call's work must stop starting searches (2026-10-15, the AD gaps plan): the moment its budget ends on <see cref="Time"/>, and the
/// caller's token, so ESC (or a web Stop) stops a walk too — an abandoned call's work no longer runs to its end. <see cref="None"/> never ends.
/// </summary>
public readonly record struct DirectoryDeadline(DateTimeOffset? Ends, TimeProvider? Time, CancellationToken Token)
{
    public static DirectoryDeadline None => default;

    public static DirectoryDeadline From(TimeProvider time, TimeSpan budget, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(time);
        return new DirectoryDeadline(time.GetUtcNow() + budget, time, token);
    }

    /// <summary>
    /// The call's clock now (2026-10-23, the admin review's E9: the group, computer, OU and domain cards read the machine's clock, not the
    /// app's <see cref="TimeProvider"/>); the machine's only with no deadline.
    /// </summary>
    public DateTimeOffset Now => Time?.GetUtcNow() ?? DateTimeOffset.UtcNow;

    /// <summary>The time left; null with no deadline.</summary>
    public TimeSpan? Left => Ends is { } ends && Time is { } time ? ends - time.GetUtcNow() : null;

    /// <summary>Whether another search may start: not cancelled, and at least <see cref="DirectoryLimits.LeastSearchTime"/> left.</summary>
    public bool CanStart => !Token.IsCancellationRequested && (Left is not { } left || left >= DirectoryLimits.LeastSearchTime);

    /// <summary>The next search's time limit: the time left, at most <see cref="DirectoryLimits.SearchTime"/>; null (the default) with no deadline.</summary>
    public TimeSpan? Limit => Left is { } left ? (left < DirectoryLimits.SearchTime ? left : DirectoryLimits.SearchTime) : null;
}
