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
}

/// <summary>How a search goes: base, one level or the subtree.</summary>
public enum DirectoryScope
{
    Base = 0,
    OneLevel = 1,
    Subtree = 2,
}

/// <summary>What a search gave: the entries, and whether a limit cut it short.</summary>
public sealed record DirectoryResults(IReadOnlyList<DirectoryEntry> Entries, bool Truncated);

/// <summary>Who a session binds as: the account and its password (never logged), handed to Negotiate as an explicit identity.</summary>
public sealed record DirectoryLogin(string User, string Password);

/// <summary>Where a session goes: the server (a DC, or the domain's name), its port, LDAPS or not, and the sign-in.</summary>
public sealed record DirectoryTarget(string Server, int Port, bool Tls, DirectoryLogin? Login);

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
    /// the paged-results control in pages of <see cref="DirectoryLimits.PageSize"/>.
    /// </summary>
    DirectoryResults Search(string baseDn, DirectoryScope scope, string filter, IReadOnlyList<string> attributes, int sizeLimit, bool paged);
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

    /// <summary>The most entries one call reads, the client-side refining included.</summary>
    public const int MaxEntries = 1000;

    /// <summary>The time one search may take, on the server and on the client.</summary>
    public static readonly TimeSpan SearchTime = TimeSpan.FromSeconds(30);

    /// <summary>How many SIDs one search resolves.</summary>
    public const int SidBatch = 50;

    /// <summary>The range a multi-valued attribute is read in past which it is read again.</summary>
    public const int RangeStep = 1500;
}
