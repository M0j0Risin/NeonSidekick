namespace NeonSidekick.Unc;

/// <summary>
/// How a share's path is spelled, by platform (2026-10-11, UNC shares on a Mac): the one place the catalog, the wizard and the tools
/// read a path a share could hold, so one <c>unc.json</c> serves both machines. Pure: nothing is touched; <c>windows</c> is a
/// parameter so every rule is pinned on either OS.
///
/// <para><b>Windows</b> keeps its spelling: <c>\\server\share[\folder]</c> or <c>X:\folder</c>, <c>/</c> read as <c>\</c>; an
/// <c>smb://server/share</c> (a Mac's) reads as <c>\\server\share</c>.</para>
///
/// <para><b>A Mac</b> has no UNC paths and no drives: a network share is <c>smb://server/share[/folder]</c>, the form Finder's Connect
/// to Server and NetFS take, and <c>\\server\share\x</c> or <c>//server/share/x</c> (a Windows file's, the model's habit) reads as
/// it, every <c>\</c> a separator; a local folder is an absolute POSIX path. The network form is only a name there: the app mounts it
/// (<see cref="MacShareMounts"/>) and the files are reached under the mount point. A <c>:</c> is a plain character on a Mac (no
/// streams), and a port in the server (<c>smb://fs01:445/eng</c>) is the URL's own.</para>
/// </summary>
public static class UncPath
{
    /// <summary>The scheme a Mac's network share is spelled with. Pinned.</summary>
    public const string SmbScheme = "smb://";

    /// <summary>
    /// <paramref name="text"/> in the platform's spelling: on Windows <c>/</c> as <c>\</c> and <c>smb://</c> as <c>\\</c>; on a Mac a
    /// network form (<c>smb://</c>, <c>\\</c>, <c>//</c>) as <c>smb://server/share/…</c> with <c>/</c> separators, anything else as given.
    /// Trimmed; nothing folded.
    /// </summary>
    public static string Spell(string? text, bool windows)
    {
        string trimmed = (text ?? "").Trim();
        if (windows)
        {
            string slashed = trimmed.Replace('/', '\\');
            return slashed.StartsWith(@"smb:\\", StringComparison.OrdinalIgnoreCase) ? @"\\" + slashed[@"smb:\\".Length..] : slashed;
        }

        string? rest = trimmed.StartsWith(SmbScheme, StringComparison.OrdinalIgnoreCase) ? trimmed[SmbScheme.Length..]
            : trimmed.StartsWith(@"\\", StringComparison.Ordinal) || trimmed.StartsWith("//", StringComparison.Ordinal) ? trimmed[2..]
            : null;
        return rest is null ? trimmed : SmbScheme + rest.Replace('\\', '/');
    }

    /// <summary>Whether <paramref name="spelled"/> (<see cref="Spell"/>'s) names a share on the network rather than a local folder.</summary>
    public static bool IsNetwork(string spelled, bool windows) =>
        windows ? spelled.StartsWith(@"\\", StringComparison.Ordinal) : spelled.StartsWith(SmbScheme, StringComparison.Ordinal);

    /// <summary>Whether <paramref name="spelled"/> is a full path a share could hold: on Windows <c>\\…</c> or <c>X:\…</c>; on a Mac <c>smb://…</c> or <c>/…</c>.</summary>
    public static bool IsAbsolute(string spelled, bool windows) =>
        windows
            ? spelled.StartsWith(@"\\", StringComparison.Ordinal) || IsDrivePath(spelled)
            : spelled.StartsWith(SmbScheme, StringComparison.Ordinal) || spelled.StartsWith('/');

    /// <summary>Whether <paramref name="text"/> is a drive path, <c>X:\…</c> or <c>X:/…</c>.</summary>
    public static bool IsDrivePath(string text) =>
        text.Length >= 3 && char.IsAsciiLetter(text[0]) && text[1] == ':' && (text[2] == '\\' || text[2] == '/');

    /// <summary>Whether <paramref name="text"/> (as given) is a Windows device path: <c>\\?\</c>, <c>\\.\</c>, or the same with <c>/</c>.</summary>
    public static bool IsDevicePath(string text)
    {
        string slashed = text.Trim().Replace('/', '\\');
        return slashed.StartsWith(@"\\?\", StringComparison.Ordinal) || slashed.StartsWith(@"\\.\", StringComparison.Ordinal);
    }

    /// <summary>
    /// <paramref name="spelled"/> as a root: full, <c>..</c> and <c>.</c> folded, no trailing separator. A Mac's network form folds
    /// its own segments (a <c>..</c> above the share throws <see cref="ArgumentException"/>) and keeps server and share as written;
    /// everything else is <see cref="Path.GetFullPath(string)"/>'s.
    /// </summary>
    public static string Normalize(string spelled, bool windows)
    {
        if (!windows && IsNetwork(spelled, windows: false))
        {
            var (server, share, rest) = Split(spelled) ?? throw new ArgumentException("no share in the path", nameof(spelled));
            return SmbScheme + server + "/" + share + (rest.Length == 0 ? "" : "/" + rest);
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(spelled));
    }

    /// <summary>
    /// A Mac's network form cut into server, share and the folder under it (<c>/</c>-joined, <c>.</c> and <c>..</c> folded); null
    /// without a server and a share, and an <see cref="ArgumentException"/> for a <c>..</c> that climbs above the share.
    /// </summary>
    public static (string Server, string Share, string Folder)? Split(string spelled)
    {
        if (!spelled.StartsWith(SmbScheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string[] parts = spelled[SmbScheme.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] is "." or ".." || parts[1] is "." or "..")
        {
            return null;
        }

        var folded = new List<string>();
        foreach (string part in parts.Skip(2))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (folded.Count == 0)
                {
                    throw new ArgumentException("a '..' climbs above the share", nameof(spelled));
                }

                folded.RemoveAt(folded.Count - 1);
                continue;
            }

            folded.Add(part);
        }

        return (parts[0], parts[1], string.Join('/', folded));
    }
}
