using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using NeonSidekick.Diagnostics;
using NeonSidekick.Sql;

namespace NeonSidekick.Sqlite;

/// <summary>
/// One named database of <c>sqlite.json</c> (2026-10-04): the file and what it holds. A relative <see cref="Path"/> is taken from
/// the folder <c>sqlite.json</c> sits in. No password: the bundled e_sqlite3 has no encryption, and a SQLite file is read with
/// the user's own rights. Read through <see cref="SqliteJsonContext"/> alone.
/// </summary>
public sealed class SqliteDatabaseConfig
{
    /// <summary>The database file: a full path, or one relative to the folder of the <c>sqlite.json</c> that names it.</summary>
    public string? Path { get; set; }

    /// <summary>What the database holds, in the user's words; the model reads it to pick one.</summary>
    public string? Description { get; set; }

    /// <summary>Keys the entry does not know (2026-10-24, the database review's K2, mechanism 1): the load refuses the entry. Never written.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>
    /// Why the entry cannot be opened as it stands, or null: no path, or one no file can have (2026-10-24, the database review's D8: a NUL or
    /// another character a path cannot hold threw from every full-path read, and the SQLite guard, read at every write and shell call,
    /// threw with it).
    /// </summary>
    [JsonIgnore]
    public string? Problem => string.IsNullOrWhiteSpace(Path) ? SqliteText.NoPath : SqliteNamedDatabase.Expand(Path) is null ? SqliteText.BadPath(Path.Trim()) : null;
}

/// <summary>A named database as the catalog holds it: its name, its entry, and the file that named it.</summary>
public sealed record SqliteNamedDatabase(string Name, SqliteDatabaseConfig Config, string Source)
{
    /// <summary>
    /// The database file's full path: <see cref="SqliteDatabaseConfig.Path"/> as written, a <c>~</c> one from the user's home (2026-10-24, the
    /// database review's I12: <c>~/data/app.db</c> was joined to the configs folder, and the guard watched the wrong file), a relative one from
    /// <see cref="Source"/>'s folder. A path no file can have stands as written (D8: it threw); the load refuses such an entry.
    /// </summary>
    public string FullPath => Expand(Config.Path, System.IO.Path.GetDirectoryName(Source)) ?? Config.Path?.Trim() ?? "";

    /// <summary><paramref name="path"/> as a full path (a <c>~</c> from the home, a relative one from <paramref name="folder"/>), or null when it cannot be one.</summary>
    public static string? Expand(string? path, string? folder = null)
    {
        string text = path?.Trim() ?? "";
        if (text.Length == 0 || text.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0)
        {
            return null;
        }

        if (text == "~" || text.StartsWith("~/", StringComparison.Ordinal) || text.StartsWith("~\\", StringComparison.Ordinal))
        {
            text = System.IO.Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), text[1..].TrimStart('/', '\\'));
        }

        try
        {
            return System.IO.Path.IsPathRooted(text) ? System.IO.Path.GetFullPath(text) : System.IO.Path.GetFullPath(System.IO.Path.Combine(folder ?? "", text));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}

/// <summary>
/// What <see cref="SqliteConfigFile.LoadCatalog"/> found: the usable databases (the profile's first, then the home's the profile does
/// not shadow, each in file order) and the problems, never a throw. <c>MySqlCatalog</c>'s shape.
/// </summary>
public sealed record SqliteCatalog(IReadOnlyList<SqliteNamedDatabase> Databases, IReadOnlyList<SqlConfigProblem> Problems, int Hidden = 0)
{
    public static readonly SqliteCatalog Empty = new([], []);

    /// <summary>The catalog narrowed to the databases a profile offers (<c>SQLite databases offered</c>): those named in <paramref name="names"/> (case-insensitive), <see cref="Hidden"/> counting the rest; null offers none. Pure.</summary>
    public SqliteCatalog Offered(IReadOnlyList<string>? names)
    {
        var wanted = ConnectionCatalog.Wanted(names);
        var kept = Databases.Where(d => wanted.Contains(d.Name)).ToList();
        return this with { Databases = kept, Problems = ConnectionCatalog.OfferedProblems(Problems, names), Hidden = Hidden + Databases.Count - kept.Count };
    }

    /// <summary>The database a name means, case-insensitive; null when none has it.</summary>
    public SqliteNamedDatabase? Named(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : Databases.FirstOrDefault(d => string.Equals(d.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// <c>sqlite.json</c> (2026-10-04): <c>{ "databases": { "&lt;name&gt;": { "path": …, "description": … } } }</c>, one in the profile's
/// folder and one in the home's (since 2026-10-16), <c>mysql.json</c>'s pair: the profile's wins by name. A missing file is empty; a corrupt or unreadable
/// one is a problem and one warning, never a crash. Read afresh at every call. The byte-level edits are
/// <see cref="ConnectionsFileEdit"/>'s, under the <c>databases</c> section.
/// </summary>
public sealed class SqliteConfigFile
{
    /// <summary>The file's name in a profile's <c>configs</c> folder and in the home.</summary>
    public const string FileName = "sqlite.json";

    /// <summary>The log category of everything SQLite.</summary>
    public const string Category = "Sqlite";

    /// <summary>The section the databases sit under (<see cref="ConnectionsFileEdit"/>'s word).</summary>
    public const string Section = "databases";

    /// <summary>What a fresh file holds: no databases and a commented example, valid once its <c>//</c> are removed. Pinned.</summary>
    public const string EmptyText =
        "{\n" +
        "  // One entry per SQLite database. Its name is what the model passes as \"database\", and what %name picks on the input\n" +
        "  // line. \"path\" is required: a full path, or one relative to this file's folder. Remove the leading // from the\n" +
        "  // example to use it, and put it inside \"databases\" below.\n" +
        "  //\n" +
        "  // \"chinook\": { \"path\": \"D:\\\\data\\\\chinook.db\", \"description\": \"the music store sample\" },\n" +
        "  //\n" +
        "  // The files are opened read-only; with SQLite sandbox files on, any database file in the working directory can be\n" +
        "  // named by its path too.\n" +
        "  \"databases\": {}\n" +
        "}\n";

    /// <summary>The databases by name, in file order.</summary>
    public Dictionary<string, SqliteDatabaseConfig?> Databases { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Keys beside <c>databases</c>: listed as problems, never used (2026-10-24, review K2).</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public static string ProfilePath(string profileDirectory) => NeonSidekick.Settings.Profiles.ConfigPath(profileDirectory, FileName);

    /// <summary>The home's file: <c>&lt;home&gt;\configs\sqlite.json</c> (configs since 2026-10-16, so a relative <c>path</c> in it counts from there), every profile's.</summary>
    public static string GlobalPath(string home) => NeonSidekick.Settings.Profiles.HomeConfigPath(home, FileName);

    /// <summary>
    /// Reads <paramref name="path"/>: the usable databases in file order and a problem per entry with no path or a blank name; a file that is
    /// not JSON or cannot be read is one problem and one warning. Read again only once the file changes (2026-10-24, the database review's
    /// mechanism 1, <see cref="Computers.ConfigCache"/>); an entry with a key it does not know, a name given twice or a key given twice is
    /// refused, and a file holding <c>databases</c> twice is refused whole (K2, D2, N5). Never throws.
    /// </summary>
    public static SqliteCatalog Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Computers.ConfigCache.Get(FileName, path, () => LoadUncached(path));
    }

    private static SqliteCatalog LoadUncached(string path)
    {
        if (!File.Exists(path))
        {
            return SqliteCatalog.Empty;
        }

        SqliteConfigFile? file;
        IReadOnlySet<string> repeated;
        IReadOnlyDictionary<string, string> twice;
        try
        {
            byte[] json = Computers.ConfigChecks.WithoutBom(ConnectionsFileEdit.ForEdit(File.ReadAllBytes(path), out _)).ToArray();
            if (ConnectionsFileEdit.RepeatedSection(json, Section) is { } section)
            {
                return new SqliteCatalog([], [new SqlConfigProblem(path, SqlText.SectionTwice(section), WholeFile: true)]);
            }

            repeated = ConnectionsFileEdit.RepeatedNames(json, Section);
            twice = ConnectionsFileEdit.RepeatedKeys(json, Section);
            file = JsonSerializer.Deserialize(json, SqliteJsonContext.Default.SqliteConfigFile);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            string detail = LogText.Excerpt(ex.Message);
            DiagnosticLog.Warn(Category, SqlText.ConfigProblemLogLine(path, detail));
            return new SqliteCatalog([], [new SqlConfigProblem(path, SqlText.UnreadableFile(detail), WholeFile: true)]);
        }

        // "databases": null reads as null past the non-nullable type (the third 2026-10-04 review: it threw at every turn's tool
        // build, ConnectionFamily.Load's null check being the Oracle/MySQL/Postgres files' alone).
        var problems = Computers.ConfigChecks.UnknownFileKeys(file?.Extra, Section).Select(p => new SqlConfigProblem(path, p)).ToList();
        if (file?.Databases is not { Count: > 0 })
        {
            return problems.Count == 0 ? SqliteCatalog.Empty : new SqliteCatalog([], problems);
        }

        var databases = new List<SqliteNamedDatabase>(file.Databases.Count);
        foreach (var (name, config) in file.Databases)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                problems.Add(new SqlConfigProblem(path, SqlText.BlankName));
                continue;
            }

            string trimmed = name.Trim();
            string? reason = config is null ? SqliteText.NoPath
                : repeated.Contains(trimmed) ? Computers.ComputerText.NamedTwice
                : twice.TryGetValue(trimmed, out string? key) ? Computers.ComputerText.KeyTwice(key)
                : Computers.ConfigChecks.UnknownKey(config.Extra, SqliteJsonContext.Default.SqliteDatabaseConfig) ?? config.Problem;
            if (reason is not null)
            {
                // The entry kept beside the problem (H9): the guard still watches its file.
                problems.Add(new SqlConfigProblem(SqlText.ConnectionSource(path, name), reason, Name: trimmed, Entry: config is null ? null : new SqliteNamedDatabase(trimmed, config, path)));
                continue;
            }

            databases.Add(new SqliteNamedDatabase(name.Trim(), config!, path));
        }

        return new SqliteCatalog(databases, problems);
    }

    /// <summary>The profile's file over the home's (<paramref name="home"/> null = the profile's alone): a name in both is the profile's.</summary>
    public static SqliteCatalog LoadCatalog(string profileDirectory, string? home)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        var profile = Load(ProfilePath(profileDirectory));
        if (home is null || string.Equals(Path.GetFullPath(ProfilePath(profileDirectory)), Path.GetFullPath(GlobalPath(home)), StringComparison.OrdinalIgnoreCase))
        {
            return profile;
        }

        // A profile entry that was refused still hides the home's of its name (2026-10-24, review D1: the profile meant its own).
        var global = Load(GlobalPath(home));
        var names = new HashSet<string>(profile.Databases.Select(d => d.Name).Concat(profile.Problems.Where(p => p.Name is not null).Select(p => p.Name!)), StringComparer.OrdinalIgnoreCase);
        return new SqliteCatalog(
            [.. profile.Databases, .. global.Databases.Where(d => !names.Contains(d.Name))],
            [.. profile.Problems, .. global.Problems]);
    }

    /// <summary>
    /// Every file both files name — usable, refused, and the home's a profile entry shadows alike (2026-10-24, the database review's H9: the
    /// guard built from <see cref="LoadCatalog"/>, which drops a shadowed entry, so its file went unguarded). The guard's, never a tool's.
    /// </summary>
    public static IReadOnlyList<string> EveryFile(string profileDirectory, string? home)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        var files = new List<SqliteCatalog> { Load(ProfilePath(profileDirectory)) };
        if (home is not null && !string.Equals(Path.GetFullPath(ProfilePath(profileDirectory)), Path.GetFullPath(GlobalPath(home)), StringComparison.OrdinalIgnoreCase))
        {
            files.Add(Load(GlobalPath(home)));
        }

        return files.SelectMany(f => f.Databases.Concat(f.Problems.Select(p => p.Entry).OfType<SqliteNamedDatabase>()))
            .Select(d => SqliteNamedDatabase.Expand(d.Config.Path, Path.GetDirectoryName(d.Source)))
            .OfType<string>()
            .ToList();
    }

    /// <summary>Adds database <paramref name="name"/> to <paramref name="path"/> (the <c>SQLite add database</c> wizard), the file's comments and layout kept (a missing file made with <see cref="EmptyText"/> first). Null on success, else why not.</summary>
    public static string? AddDatabase(string path, string name, SqliteDatabaseConfig config)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(config);
        return ConnectionsFileEdit.AddConnection(path, name, EntryText(config), EmptyText, Section, Noun);
    }

    /// <summary>Replaces database <paramref name="oldName"/> in <paramref name="path"/> with <paramref name="newName"/> and <paramref name="config"/> (2026-10-05, the user's ask: <c>SQLite add/edit database</c> edits an entry too), comments and layout kept. Null on success, else why not.</summary>
    public static string? ReplaceDatabase(string path, string oldName, string newName, SqliteDatabaseConfig config)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(oldName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        ArgumentNullException.ThrowIfNull(config);
        return ConnectionsFileEdit.ReplaceConnection(path, oldName, newName, EntryText(config), Section, Noun);
    }

    /// <summary>What a database is called in a refusal.</summary>
    private const string Noun = "database";

    /// <summary>One database as the file holds it: <see cref="SqliteJsonContext"/>'s keys, indented, the relaxed escaping, its unknown keys left out.</summary>
    private static string EntryText(SqliteDatabaseConfig config)
    {
        var copy = new SqliteDatabaseConfig { Path = config.Path, Description = config.Description };
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            JsonSerializer.Serialize(writer, copy, SqliteJsonContext.Default.SqliteDatabaseConfig);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Writes <see cref="EmptyText"/> to <paramref name="path"/> when no file is there; true when it wrote. Throws on an IO failure.</summary>
    public static bool EnsureExists(string path) => ConnectionsFileEdit.EnsureExists(path, EmptyText);
}
