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

    /// <summary>Why the entry cannot be opened as it stands (no path), or null.</summary>
    [JsonIgnore]
    public string? Problem => string.IsNullOrWhiteSpace(Path) ? SqliteText.NoPath : null;
}

/// <summary>A named database as the catalog holds it: its name, its entry, and the file that named it.</summary>
public sealed record SqliteNamedDatabase(string Name, SqliteDatabaseConfig Config, string Source)
{
    /// <summary>The database file's full path: <see cref="SqliteDatabaseConfig.Path"/> as written, a relative one from <see cref="Source"/>'s folder.</summary>
    public string FullPath
    {
        get
        {
            string path = Config.Path?.Trim() ?? "";
            return System.IO.Path.IsPathRooted(path) ? System.IO.Path.GetFullPath(path) : System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Source) ?? "", path));
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
        var wanted = new HashSet<string>((names ?? []).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
        var kept = Databases.Where(d => wanted.Contains(d.Name)).ToList();
        return this with { Databases = kept, Hidden = Hidden + Databases.Count - kept.Count };
    }

    /// <summary>The database a name means, case-insensitive; null when none has it.</summary>
    public SqliteNamedDatabase? Named(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : Databases.FirstOrDefault(d => string.Equals(d.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// <c>sqlite.json</c> (2026-10-04): <c>{ "databases": { "&lt;name&gt;": { "path": …, "description": … } } }</c>, one in the profile's
/// folder and one in the home, <c>mysql.json</c>'s pair: the profile's wins by name. A missing file is empty; a corrupt or unreadable
/// one is a problem and one warning, never a crash. Read afresh at every call. The byte-level edits are
/// <see cref="ConnectionsFileEdit"/>'s, under the <c>databases</c> section.
/// </summary>
public sealed class SqliteConfigFile
{
    /// <summary>The file's name in a profile's folder and in the home.</summary>
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

    public static string ProfilePath(string profileDirectory) => Path.Combine(profileDirectory, FileName);

    public static string GlobalPath(string home) => Path.Combine(home, FileName);

    /// <summary>Reads <paramref name="path"/>: the usable databases in file order and a problem per entry with no path or a blank name; a file that is not JSON or cannot be read is one problem and one warning. Never throws.</summary>
    public static SqliteCatalog Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return SqliteCatalog.Empty;
        }

        SqliteConfigFile? file;
        try
        {
            file = JsonSerializer.Deserialize(File.ReadAllText(path), SqliteJsonContext.Default.SqliteConfigFile);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            string detail = LogText.Excerpt(ex.Message);
            DiagnosticLog.Warn(Category, SqlText.ConfigProblemLogLine(path, detail));
            return new SqliteCatalog([], [new SqlConfigProblem(path, SqlText.UnreadableFile(detail))]);
        }

        // "databases": null reads as null past the non-nullable type (the third 2026-10-04 review: it threw at every turn's tool
        // build, ConnectionFamily.Load's null check being the Oracle/MySQL/Postgres files' alone).
        if (file?.Databases is not { Count: > 0 })
        {
            return SqliteCatalog.Empty;
        }

        var databases = new List<SqliteNamedDatabase>(file.Databases.Count);
        var problems = new List<SqlConfigProblem>();
        foreach (var (name, config) in file.Databases)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                problems.Add(new SqlConfigProblem(path, SqlText.BlankName));
                continue;
            }

            if ((config is null ? SqliteText.NoPath : config.Problem) is { } reason)
            {
                problems.Add(new SqlConfigProblem(SqlText.ConnectionSource(path, name), reason));
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

        var global = Load(GlobalPath(home));
        var names = new HashSet<string>(profile.Databases.Select(d => d.Name), StringComparer.OrdinalIgnoreCase);
        return new SqliteCatalog(
            [.. profile.Databases, .. global.Databases.Where(d => !names.Contains(d.Name))],
            [.. profile.Problems, .. global.Problems]);
    }

    /// <summary>Adds database <paramref name="name"/> to <paramref name="path"/> (the <c>SQLite add database</c> wizard), the file's comments and layout kept (a missing file made with <see cref="EmptyText"/> first). Null on success, else why not.</summary>
    public static string? AddDatabase(string path, string name, SqliteDatabaseConfig config)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(config);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            JsonSerializer.Serialize(writer, config, SqliteJsonContext.Default.SqliteDatabaseConfig);
        }

        return ConnectionsFileEdit.AddConnection(path, name, Encoding.UTF8.GetString(stream.ToArray()), EmptyText, Section, "database");
    }

    /// <summary>Writes <see cref="EmptyText"/> to <paramref name="path"/> when no file is there; true when it wrote. Throws on an IO failure.</summary>
    public static bool EnsureExists(string path) => ConnectionsFileEdit.EnsureExists(path, EmptyText);
}
