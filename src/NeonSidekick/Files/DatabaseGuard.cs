namespace NeonSidekick.Files;

/// <summary>
/// What counts as a SQLite database file while the SQLite tools are on (2026-10-05, the user's call after a model, under Shell
/// command policy yolo, wrote a script that inserted into a database SQLite mode kept read-only): a name ending one of
/// <see cref="Extensions"/>, or one of those with a journal's <see cref="Siblings"/> suffix, and the files <c>sqlite.json</c>
/// names whatever they end in (<see cref="NamedFiles"/>, full paths, their siblings too). Two rules read it:
/// <see cref="WorkingDirectory"/> refuses a file tool's change to such a file (<see cref="FileOutcome.DatabaseProtected"/>), and
/// the shell police refuses a line or script that reaches one (<c>Shell.SqlitePolice</c>). Pure.
/// </summary>
public sealed record DatabaseGuard(IReadOnlyList<string> NamedFiles)
{
    /// <summary>
    /// A SQLite database's usual file extensions, compared case-blind; <c>.s3db</c>, <c>.sl3</c>, <c>.sl2</c> and <c>.sqlitedb</c> since
    /// 2026-10-22 (review I9: the SQLite tools open any file, so those were databases left open to the file and shell tools).
    /// </summary>
    public static readonly IReadOnlyList<string> Extensions = [".db", ".db3", ".sqlite", ".sqlite3", ".s3db", ".sl3", ".sl2", ".sqlitedb"];

    /// <summary>The 16 bytes every SQLite database file starts with.</summary>
    private static ReadOnlySpan<byte> Header => "SQLite format 3\0"u8;

    /// <summary>
    /// Whether the existing file at <paramref name="fullPath"/> starts with SQLite's header (2026-10-22, review I9): a database by its content,
    /// whatever its name. One 16-byte read; false for a missing, unreadable or short file and for a folder.
    /// </summary>
    public static bool HasSqliteHeader(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        try
        {
            if (!File.Exists(fullPath))
            {
                return false;
            }

            Span<byte> start = stackalloc byte[16];
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
            return stream.ReadAtLeast(start, 16, throwOnEndOfStream: false) == 16 && start.SequenceEqual(Header);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>The suffixes of the files SQLite keeps beside a database: its rollback journal and its write-ahead log and index.</summary>
    public static readonly IReadOnlyList<string> Siblings = ["-journal", "-wal", "-shm"];

    /// <summary>A guard over no named file: the extensions alone (tests, and a catalog that cannot be read).</summary>
    public static readonly DatabaseGuard ByExtension = new([]);

    /// <summary><paramref name="name"/> without a journal sibling's suffix: <c>app.db-wal</c> is <c>app.db</c>.</summary>
    public static string WithoutSibling(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (string sibling in Siblings)
        {
            if (name.EndsWith(sibling, StringComparison.OrdinalIgnoreCase))
            {
                return name[..^sibling.Length];
            }
        }

        return name;
    }

    /// <summary>Whether a file name (or a path's last part) ends a database extension, a sibling's suffix allowed after it.</summary>
    public static bool HasDatabaseName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string bare = WithoutSibling(name);
        return Extensions.Any(e => bare.Length > e.Length && bare.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether <paramref name="fullPath"/> is a database file: by its name, or as one of <see cref="NamedFiles"/> (a sibling of one too).</summary>
    public bool IsDatabaseFile(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        if (HasDatabaseName(Path.GetFileName(fullPath)))
        {
            return true;
        }

        string bare = WithoutSibling(fullPath);
        return NamedFiles.Any(named => string.Equals(Full(named), bare, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The file names of <see cref="NamedFiles"/>, for the shell police to find in a line.</summary>
    public IEnumerable<string> NamedFileNames => NamedFiles.Select(Path.GetFileName).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!);

    private static string Full(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return path;
        }
    }
}
