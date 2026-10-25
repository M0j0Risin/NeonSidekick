using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using NeonSidekick.Diagnostics;
using NeonSidekick.Sql;
using NeonSidekick.Sqlite;

namespace NeonSidekick.DuckDb;

/// <summary>
/// One named database of <c>duckdb.json</c> (2026-10-25): the file and what it holds, <see cref="SqliteDatabaseConfig"/>'s twin. A relative
/// <see cref="Path"/> is taken from the folder <c>duckdb.json</c> sits in, a <c>~</c> one from the user's home. No password: an encrypted
/// DuckDB file is refused, and a file is read with the user's own rights. Read through <see cref="DuckDbJsonContext"/> alone.
/// </summary>
public sealed class DuckDbDatabaseConfig
{
    /// <summary>The database file: a full path, or one relative to the folder of the <c>duckdb.json</c> that names it.</summary>
    public string? Path { get; set; }

    /// <summary>What the database holds, in the user's words; the model reads it to pick one.</summary>
    public string? Description { get; set; }

    /// <summary>Keys the entry does not know: the load refuses the entry (SQLite's K2). Never written.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>Why the entry cannot be opened as it stands, or null: no path, or one no file can have (SQLite's D8).</summary>
    [JsonIgnore]
    public string? Problem => string.IsNullOrWhiteSpace(Path) ? DuckDbText.NoPath : SqliteNamedDatabase.Expand(Path) is null ? DuckDbText.BadPath(Path.Trim()) : null;
}

/// <summary>A named database as the catalog holds it: its name, its entry, and the file that named it.</summary>
public sealed record DuckDbNamedDatabase(string Name, DuckDbDatabaseConfig Config, string Source)
{
    /// <summary>
    /// The database file's full path: <see cref="DuckDbDatabaseConfig.Path"/> as written, a <c>~</c> one from the user's home, a relative one
    /// from <see cref="Source"/>'s folder (<see cref="SqliteNamedDatabase.Expand"/>'s rules). A path no file can have stands as written; the
    /// load refuses such an entry.
    /// </summary>
    public string FullPath => SqliteNamedDatabase.Expand(Config.Path, System.IO.Path.GetDirectoryName(Source)) ?? Config.Path?.Trim() ?? "";
}

/// <summary>
/// What <see cref="DuckDbConfigFile.LoadCatalog"/> found: the usable databases (the profile's first, then the home's the profile does
/// not shadow, each in file order) and the problems, never a throw. <see cref="SqliteCatalog"/>'s shape.
/// </summary>
public sealed record DuckDbCatalog(IReadOnlyList<DuckDbNamedDatabase> Databases, IReadOnlyList<SqlConfigProblem> Problems, int Hidden = 0)
{
    public static readonly DuckDbCatalog Empty = new([], []);

    /// <summary>The catalog narrowed to the databases a profile offers (<c>DuckDB databases offered</c>): those named in <paramref name="names"/> (case-insensitive), <see cref="Hidden"/> counting the rest; null offers none. Pure.</summary>
    public DuckDbCatalog Offered(IReadOnlyList<string>? names)
    {
        var wanted = ConnectionCatalog.Wanted(names);
        var kept = Databases.Where(d => wanted.Contains(d.Name)).ToList();
        return this with { Databases = kept, Problems = ConnectionCatalog.OfferedProblems(Problems, names), Hidden = Hidden + Databases.Count - kept.Count };
    }

    /// <summary>The database a name means, case-insensitive; null when none has it.</summary>
    public DuckDbNamedDatabase? Named(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : Databases.FirstOrDefault(d => string.Equals(d.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// <c>duckdb.json</c> (2026-10-25): <c>{ "databases": { "&lt;name&gt;": { "path": …, "description": … } } }</c>, one in the profile's
/// <c>configs</c> folder and one in the home's, <c>sqlite.json</c>'s pair: the profile's wins by name. A missing file is empty; a corrupt or
/// unreadable one is a problem and one warning, never a crash. Read again only once the file changes (<see cref="Computers.ConfigCache"/>).
/// The byte-level edits are <see cref="ConnectionsFileEdit"/>'s, under the <c>databases</c> section.
/// </summary>
public sealed class DuckDbConfigFile
{
    /// <summary>The file's name in a profile's <c>configs</c> folder and in the home's.</summary>
    public const string FileName = "duckdb.json";

    /// <summary>The log category of everything DuckDB.</summary>
    public const string Category = "DuckDb";

    /// <summary>The section the databases sit under (<see cref="ConnectionsFileEdit"/>'s word).</summary>
    public const string Section = "databases";

    /// <summary>What a fresh file holds: no databases and a commented example, valid once its <c>//</c> are removed. Pinned.</summary>
    public const string EmptyText =
        "{\n" +
        "  // One entry per DuckDB database. Its name is what the model passes as \"database\", and what %name picks on the input\n" +
        "  // line. \"path\" is required: a full path, or one relative to this file's folder. Remove the leading // from the\n" +
        "  // example to use it, and put it inside \"databases\" below.\n" +
        "  //\n" +
        "  // \"sales\": { \"path\": \"D:\\\\data\\\\sales.duckdb\", \"description\": \"orders and customers since 2020\" },\n" +
        "  //\n" +
        "  // The files are opened read-only; with DuckDB sandbox files on, any DuckDB file in the working directory can be\n" +
        "  // named by its path too, and CSV, Parquet and JSON files there can be queried.\n" +
        "  \"databases\": {}\n" +
        "}\n";

    /// <summary>The databases by name, in file order.</summary>
    public Dictionary<string, DuckDbDatabaseConfig?> Databases { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Keys beside <c>databases</c>: listed as problems, never used.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public static string ProfilePath(string profileDirectory) => NeonSidekick.Settings.Profiles.ConfigPath(profileDirectory, FileName);

    /// <summary>The home's file: <c>&lt;home&gt;\configs\duckdb.json</c> (a relative <c>path</c> in it counts from there), every profile's.</summary>
    public static string GlobalPath(string home) => NeonSidekick.Settings.Profiles.HomeConfigPath(home, FileName);

    /// <summary>
    /// Reads <paramref name="path"/>: the usable databases in file order and a problem per entry with no path or a blank name; a file that is
    /// not JSON or cannot be read is one problem and one warning. An entry with a key it does not know, a name given twice or a key given
    /// twice is refused, and a file holding <c>databases</c> twice is refused whole (SQLite's rules). Never throws.
    /// </summary>
    public static DuckDbCatalog Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Computers.ConfigCache.Get(FileName, path, () => LoadUncached(path));
    }

    private static DuckDbCatalog LoadUncached(string path)
    {
        if (!File.Exists(path))
        {
            return DuckDbCatalog.Empty;
        }

        DuckDbConfigFile? file;
        IReadOnlySet<string> repeated;
        IReadOnlyDictionary<string, string> twice;
        try
        {
            byte[] json = Computers.ConfigChecks.WithoutBom(ConnectionsFileEdit.ForEdit(File.ReadAllBytes(path), out _)).ToArray();
            if (ConnectionsFileEdit.RepeatedSection(json, Section) is { } section)
            {
                return new DuckDbCatalog([], [new SqlConfigProblem(path, SqlText.SectionTwice(section), WholeFile: true)]);
            }

            repeated = ConnectionsFileEdit.RepeatedNames(json, Section);
            twice = ConnectionsFileEdit.RepeatedKeys(json, Section);
            file = JsonSerializer.Deserialize(json, DuckDbJsonContext.Default.DuckDbConfigFile);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            string detail = LogText.Excerpt(ex.Message);
            DiagnosticLog.Warn(Category, SqlText.ConfigProblemLogLine(path, detail));
            return new DuckDbCatalog([], [new SqlConfigProblem(path, SqlText.UnreadableFile(detail), WholeFile: true)]);
        }

        // "databases": null reads as null past the non-nullable type (SQLite's third 2026-10-04 review).
        var problems = Computers.ConfigChecks.UnknownFileKeys(file?.Extra, Section).Select(p => new SqlConfigProblem(path, p)).ToList();
        if (file?.Databases is not { Count: > 0 })
        {
            return problems.Count == 0 ? DuckDbCatalog.Empty : new DuckDbCatalog([], problems);
        }

        var databases = new List<DuckDbNamedDatabase>(file.Databases.Count);
        foreach (var (name, config) in file.Databases)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                problems.Add(new SqlConfigProblem(path, SqlText.BlankName));
                continue;
            }

            string trimmed = name.Trim();
            string? reason = config is null ? DuckDbText.NoPath
                : repeated.Contains(trimmed) ? Computers.ComputerText.NamedTwice
                : twice.TryGetValue(trimmed, out string? key) ? Computers.ComputerText.KeyTwice(key)
                : Computers.ConfigChecks.UnknownKey(config.Extra, DuckDbJsonContext.Default.DuckDbDatabaseConfig) ?? config.Problem;
            if (reason is not null)
            {
                // The entry kept beside the problem: the guard still watches its file.
                problems.Add(new SqlConfigProblem(SqlText.ConnectionSource(path, name), reason, Name: trimmed, Entry: config is null ? null : new DuckDbNamedDatabase(trimmed, config, path)));
                continue;
            }

            databases.Add(new DuckDbNamedDatabase(trimmed, config!, path));
        }

        return new DuckDbCatalog(databases, problems);
    }

    /// <summary>The profile's file over the home's (<paramref name="home"/> null = the profile's alone): a name in both is the profile's.</summary>
    public static DuckDbCatalog LoadCatalog(string profileDirectory, string? home)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        var profile = Load(ProfilePath(profileDirectory));
        if (home is null || string.Equals(Path.GetFullPath(ProfilePath(profileDirectory)), Path.GetFullPath(GlobalPath(home)), StringComparison.OrdinalIgnoreCase))
        {
            return profile;
        }

        // A profile entry that was refused still hides the home's of its name (SQLite's D1: the profile meant its own).
        var global = Load(GlobalPath(home));
        var names = new HashSet<string>(profile.Databases.Select(d => d.Name).Concat(profile.Problems.Where(p => p.Name is not null).Select(p => p.Name!)), StringComparer.OrdinalIgnoreCase);
        return new DuckDbCatalog(
            [.. profile.Databases, .. global.Databases.Where(d => !names.Contains(d.Name))],
            [.. profile.Problems, .. global.Problems]);
    }

    /// <summary>
    /// Every file both files name — usable, refused, and the home's a profile entry shadows alike (SQLite's H9). The guard's, never a tool's.
    /// </summary>
    public static IReadOnlyList<string> EveryFile(string profileDirectory, string? home)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        var files = new List<DuckDbCatalog> { Load(ProfilePath(profileDirectory)) };
        if (home is not null && !string.Equals(Path.GetFullPath(ProfilePath(profileDirectory)), Path.GetFullPath(GlobalPath(home)), StringComparison.OrdinalIgnoreCase))
        {
            files.Add(Load(GlobalPath(home)));
        }

        return files.SelectMany(f => f.Databases.Concat(f.Problems.Select(p => p.Entry).OfType<DuckDbNamedDatabase>()))
            .Select(d => SqliteNamedDatabase.Expand(d.Config.Path, Path.GetDirectoryName(d.Source)))
            .OfType<string>()
            .ToList();
    }

    /// <summary>Adds database <paramref name="name"/> to <paramref name="path"/> (the <c>DuckDB add database</c> wizard), the file's comments and layout kept (a missing file made with <see cref="EmptyText"/> first). Null on success, else why not.</summary>
    public static string? AddDatabase(string path, string name, DuckDbDatabaseConfig config)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(config);
        return ConnectionsFileEdit.AddConnection(path, name, EntryText(config), EmptyText, Section, Noun);
    }

    /// <summary>Replaces database <paramref name="oldName"/> in <paramref name="path"/> with <paramref name="newName"/> and <paramref name="config"/> (the wizard's edit and rename), comments and layout kept. Null on success, else why not.</summary>
    public static string? ReplaceDatabase(string path, string oldName, string newName, DuckDbDatabaseConfig config)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(oldName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        ArgumentNullException.ThrowIfNull(config);
        return ConnectionsFileEdit.ReplaceConnection(path, oldName, newName, EntryText(config), Section, Noun);
    }

    /// <summary>What a database is called in a refusal.</summary>
    private const string Noun = "database";

    /// <summary>One database as the file holds it: <see cref="DuckDbJsonContext"/>'s keys, indented, the relaxed escaping, its unknown keys left out.</summary>
    private static string EntryText(DuckDbDatabaseConfig config)
    {
        var copy = new DuckDbDatabaseConfig { Path = config.Path, Description = config.Description };
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            JsonSerializer.Serialize(writer, copy, DuckDbJsonContext.Default.DuckDbDatabaseConfig);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Writes <see cref="EmptyText"/> to <paramref name="path"/> when no file is there; true when it wrote. Throws on an IO failure.</summary>
    public static bool EnsureExists(string path) => ConnectionsFileEdit.EnsureExists(path, EmptyText);
}
