namespace NeonSidekick.Files;

/// <summary>
/// What counts as a SQLite database file while the SQLite tools are on (2026-10-05, the user's call after a model, under Shell
/// command policy yolo, wrote a script that inserted into a database SQLite mode kept read-only): a name ending one of
/// <see cref="Extensions"/>, or one of those with a journal's <see cref="Siblings"/> suffix, and the files <c>sqlite.json</c>
/// names whatever they end in (<see cref="NamedFiles"/>, full paths, their siblings too). Two rules read it:
/// <see cref="WorkingDirectory"/> refuses a file tool's change to such a file (<see cref="FileOutcome.DatabaseProtected"/>), and
/// the shell police refuses a line or script that reaches one (<c>Shell.SqlitePolice</c>). Since 2026-10-25 (the DuckDB tools) it is built
/// from the file families whose tools are on (<see cref="Families"/>): SQLite's extensions, journals and header, DuckDB's
/// (<see cref="DuckDbExtensions"/>, its <c>.wal</c>, <c>DUCK</c> at offset 8), or both, and <see cref="NamedFiles"/> holds the named files
/// of each family that is on (<see cref="DuckDbNamedFiles"/> DuckDB's among them). Pure but for the header reads.
/// </summary>
public sealed record DatabaseGuard(IReadOnlyList<string> NamedFiles, DatabaseFamilies Families = DatabaseFamilies.Sqlite, IReadOnlyList<string>? DuckDbNamedFiles = null)
{
    /// <summary>
    /// <see cref="NamedFiles"/> as full paths, made once (2026-10-24, the database review's Q12: every check recomputed each one's full path,
    /// for each file of a folder walk too).
    /// </summary>
    private readonly HashSet<string> _named = new(NamedFiles.Select(Full).Select(Canonical), StringComparer.OrdinalIgnoreCase);

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

    /// <summary>A DuckDB database's usual file extensions (2026-10-25), compared case-blind; a DuckDB file named <c>.db</c> is found by its header.</summary>
    public static readonly IReadOnlyList<string> DuckDbExtensions = [".duckdb", ".ddb"];

    /// <summary>The suffix of the write-ahead log DuckDB keeps beside a database (<c>sales.duckdb.wal</c>).</summary>
    public static readonly IReadOnlyList<string> DuckDbSiblings = [".wal"];

    /// <summary>Whether <paramref name="family"/> is among <see cref="Families"/>.</summary>
    public bool Has(DatabaseFamilies family) => (Families & family) != 0;

    /// <summary>The extensions of the families that are on, SQLite's first.</summary>
    public IReadOnlyList<string> GuardedExtensions => Families switch
    {
        DatabaseFamilies.Sqlite => Extensions,
        DatabaseFamilies.DuckDb => DuckDbExtensions,
        _ => [.. Extensions, .. DuckDbExtensions],
    };

    /// <summary><paramref name="name"/> without a sibling's suffix of a family that is on: <c>app.db-wal</c> is <c>app.db</c>, <c>x.duckdb.wal</c> <c>x.duckdb</c>.</summary>
    public string Bare(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (Has(DatabaseFamilies.Sqlite) && WithoutSibling(name) is var sqlite && sqlite.Length != name.Length)
        {
            return sqlite;
        }

        if (Has(DatabaseFamilies.DuckDb))
        {
            foreach (string sibling in DuckDbSiblings)
            {
                if (name.EndsWith(sibling, StringComparison.OrdinalIgnoreCase))
                {
                    return name[..^sibling.Length];
                }
            }
        }

        return name;
    }

    /// <summary><see cref="HasDatabaseName"/> over the families that are on: a name ending one of <see cref="GuardedExtensions"/>, a sibling's suffix allowed.</summary>
    public bool IsDatabaseName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string bare = Bare(Canonical(name));
        return GuardedExtensions.Any(e => bare.Length > e.Length && bare.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether the existing file at <paramref name="fullPath"/> starts with the header of a family that is on. Never throws.</summary>
    public bool HasDatabaseHeader(string fullPath) =>
        (Has(DatabaseFamilies.Sqlite) && HasSqliteHeader(fullPath)) || (Has(DatabaseFamilies.DuckDb) && DuckDb.DuckDbAccess.HasDuckDbHeader(fullPath));

    /// <summary>
    /// <see cref="HoldsSqlite"/> over the families that are on: <paramref name="fullPath"/>, or the database its sibling's name points at, starts
    /// with a guarded header; a link read only when <paramref name="throughLinks"/>.
    /// </summary>
    public bool Holds(string fullPath, bool throughLinks = true)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        if (!throughLinks && IsLink(fullPath))
        {
            return false;
        }

        string database = Bare(fullPath);
        return HasDatabaseHeader(fullPath) || (database.Length != fullPath.Length && HasDatabaseHeader(database));
    }

    /// <summary>
    /// Which family a token the police found reaches (2026-10-25): DuckDB's word or extension, or a file only DuckDB names, is DuckDB's while
    /// its tools are on; anything else is SQLite's while SQLite's are, else DuckDB's.
    /// </summary>
    public DatabaseFamilies FamilyOf(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (Families != (DatabaseFamilies.Sqlite | DatabaseFamilies.DuckDb))
        {
            return Has(DatabaseFamilies.Sqlite) ? DatabaseFamilies.Sqlite : DatabaseFamilies.DuckDb;
        }

        string bare = Bare(Canonical(token));
        bool duck = token.Contains("duckdb", StringComparison.OrdinalIgnoreCase)
            || DuckDbExtensions.Any(e => bare.EndsWith(e, StringComparison.OrdinalIgnoreCase))
            || ((DuckDbNamedFiles ?? []).Any(f => string.Equals(Path.GetFileName(f), token, StringComparison.OrdinalIgnoreCase))
                && !NamedFiles.Except(DuckDbNamedFiles ?? []).Any(f => string.Equals(Path.GetFileName(f), token, StringComparison.OrdinalIgnoreCase)));
        return duck ? DatabaseFamilies.DuckDb : DatabaseFamilies.Sqlite;
    }

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

    /// <summary>
    /// Whether a file name (or a path's last part) ends a database extension, a sibling's suffix allowed after it — read as Windows opens it
    /// (2026-10-24, the database review's Q1: <c>app.db.</c> and <c>app.db </c> open <c>app.db</c>, and <c>app.db:x</c> is its stream).
    /// </summary>
    public static bool HasDatabaseName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string bare = WithoutSibling(Canonical(name));
        return Extensions.Any(e => bare.Length > e.Length && bare.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether <paramref name="fullPath"/> is a database file: by its name, or as one of <see cref="NamedFiles"/> (a sibling of one too).
    /// Both read as the file system opens them since 2026-10-24 (<see cref="Canonical"/>; review Q1, Q8); by the families that are on since
    /// 2026-10-25 (<see cref="IsDatabaseName"/>, <see cref="Bare"/>).
    /// </summary>
    public bool IsDatabaseFile(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        if (IsDatabaseName(Path.GetFileName(fullPath)))
        {
            return true;
        }

        return _named.Contains(Bare(Canonical(fullPath)));
    }

    /// <summary>
    /// A name or path as the file system opens it (2026-10-24, the database review's Q1, Q8): composed (NFC), so a Mac's decomposed spelling
    /// of a named file is that file; its last part cut at a <c>:</c> (an alternate data stream's) and without trailing dots and spaces,
    /// which Windows drops. Done on every system: a Mac file named so is only refused as well.
    /// </summary>
    public static string Canonical(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string text = path.IsNormalized(System.Text.NormalizationForm.FormC) ? path : path.Normalize(System.Text.NormalizationForm.FormC);
        int last = text.LastIndexOfAny(['\\', '/']) + 1;
        int stream = text.IndexOf(':', last);
        if (stream >= 0)
        {
            text = text[..stream];
        }

        return text[..last] + text[last..].TrimEnd('.', ' ');
    }

    /// <summary>
    /// Whether <paramref name="fullPath"/> is a database by its content: it, or the database a journal sibling's name points at, starts with
    /// SQLite's header (2026-10-24, the database review's J3: a content-found database's <c>-journal</c>, <c>-wal</c> and <c>-shm</c> were left
    /// open, and a hot journal changes the database). A link is read only when <paramref name="throughLinks"/> (Q7: removing a link leaves
    /// what it points at alone).
    /// </summary>
    public static bool HoldsSqlite(string fullPath, bool throughLinks = true)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        if (!throughLinks && IsLink(fullPath))
        {
            return false;
        }

        string database = WithoutSibling(fullPath);
        return HasSqliteHeader(fullPath) || (database.Length != fullPath.Length && HasSqliteHeader(database));
    }

    private static bool IsLink(string fullPath)
    {
        try
        {
            return new FileInfo(fullPath).LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
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
