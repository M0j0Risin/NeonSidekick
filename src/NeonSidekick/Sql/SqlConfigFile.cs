using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Sql;

/// <summary>
/// One thing a load could not use: the file itself, or one connection in it. <see cref="Source"/> is the path, or the path and the
/// connection's name. <see cref="Name"/> is that connection's name (trimmed) and <see cref="WholeFile"/> marks a file that could not be
/// read at all (2026-10-04): the offered checklists never prune a name against a load that lost a whole file, and a name a problem
/// names still counts as there (<see cref="App.OfferedNames"/>).
/// </summary>
public sealed record SqlConfigProblem(string Source, string Reason, string? Name = null, bool WholeFile = false);

/// <summary>
/// What <see cref="SqlConfigFile.LoadCatalog"/> found: the usable connections (the profile's first, then the
/// home's the profile does not shadow, each in file order) and the problems, never a throw.
/// </summary>
public sealed record SqlCatalog(IReadOnlyList<SqlNamedConnection> Connections, IReadOnlyList<SqlConfigProblem> Problems, int Hidden = 0)
{
    public static readonly SqlCatalog Empty = new([], []);

    /// <summary>
    /// The catalog narrowed to the connections a profile offers (later on 2026-09-23, <c>SQL connections offered</c>):
    /// only those whose name is in <paramref name="names"/> (case-insensitive), in file order, <see cref="Hidden"/> counting
    /// the rest. Null offers none, as an empty list does (2026-10-01, the user's call: nothing is offered until ticked; until
    /// then null was "not narrowed", every connection). The problems are kept. Pure.
    /// </summary>
    public SqlCatalog Offered(IReadOnlyList<string>? names)
    {
        var wanted = new HashSet<string>((names ?? []).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
        var kept = Connections.Where(c => wanted.Contains(c.Name)).ToList();
        return this with { Connections = kept, Hidden = Hidden + Connections.Count - kept.Count };
    }

    /// <summary>
    /// The connection a call means: <paramref name="name"/> when given (case-insensitive), else
    /// <paramref name="defaultName"/> when that names one, else the first. Null when nothing matches.
    /// </summary>
    public SqlNamedConnection? Find(string? name, string? defaultName)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            return Connections.FirstOrDefault(c => string.Equals(c.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(defaultName)
            && Connections.FirstOrDefault(c => string.Equals(c.Name, defaultName.Trim(), StringComparison.OrdinalIgnoreCase)) is { } named)
        {
            return named;
        }

        return Connections.Count > 0 ? Connections[0] : null;
    }
}

/// <summary>
/// <c>sql.json</c> (2026-09-23): <c>{ "connections": { "&lt;name&gt;": { … } } }</c>, one in the profile's folder
/// and one in the home, the <c>mcp.json</c> pair (<see cref="Mcp.McpConfigFile"/>): the profile's wins by name.
/// A missing file is empty; a corrupt or unreadable one is a problem and one warning, never a crash. Read
/// afresh at every call (<see cref="SqlAccess"/>), so an edit in the editor counts on the next tool call with no
/// reload. The <c>/tools</c> SQL tab's edit rows create the file with <see cref="EmptyText"/> when it is missing.
/// </summary>
public sealed class SqlConfigFile
{
    /// <summary>The file's name in a profile's folder and in the home.</summary>
    public const string FileName = "sql.json";

    /// <summary>The log category of everything SQL.</summary>
    public const string Category = "Sql";

    /// <summary>
    /// What a fresh file holds: no connections, and a commented example of each kind — a SQL login, Windows sign-in as
    /// the user, and <c>runas</c> with its password in Credential Manager or in the file (later on 2026-09-23, the
    /// user's ask) — each one valid once its <c>//</c> are removed (pinned by a test). Pinned.
    /// </summary>
    public const string EmptyText =
        "{\n" +
        "  // One entry per connection. Its name is what the model passes as \"connection\", and what %name picks on the input\n" +
        "  // line. Every key but \"server\" is optional, and a JSON backslash is doubled: \"CONTOSO\\\\svc-reader\". Remove the\n" +
        "  // leading // from an example to use it, and put it inside \"connections\" below.\n" +
        "  //\n" +
        "  // A SQL login (auth sql), the password kept in this file: typed in plain text, it is encrypted (\"dpapi:…\") when\n" +
        "  // the app next starts or reads this file.\n" +
        "  // \"adventureworks\": {\n" +
        "  //   \"server\": \"127.0.0.1,1433\", \"database\": \"AdventureWorks2022\",\n" +
        "  //   \"auth\": \"sql\", \"user\": \"reader\", \"password\": \"type-it-here-once\",\n" +
        "  //   \"encrypt\": \"mandatory\", \"trustServerCertificate\": true,\n" +
        "  //   \"description\": \"the sample sales database\"\n" +
        "  // },\n" +
        "  //\n" +
        "  // Windows sign-in as you (auth windows: the account running NeonSidekick, no password).\n" +
        "  // \"reports-me\": {\n" +
        "  //   \"server\": \"sqlhost01.example.com,1453\", \"database\": \"Reports\",\n" +
        "  //   \"auth\": \"windows\"\n" +
        "  // },\n" +
        "  //\n" +
        "  // Windows sign-in as another account (auth runas: runas /netonly's way, remote servers only), the password in\n" +
        "  // Windows Credential Manager as NeonSidekick/sql/<name> — set it with SQL set password on the SQL tab of /tools,\n" +
        "  // or: cmdkey /generic:NeonSidekick/sql/reports-admin /user:CONTOSO\\svc-reader /pass\n" +
        "  // \"reports-admin\": {\n" +
        "  //   \"server\": \"sqlhost01.example.com,1453\", \"database\": \"Reports\",\n" +
        "  //   \"auth\": \"runas\", \"user\": \"CONTOSO\\\\svc-reader\", \"passwordStore\": \"credman\"\n" +
        "  // },\n" +
        "  //\n" +
        "  // The same account with the password kept (encrypted) in this file instead of Credential Manager.\n" +
        "  // \"reports-admin-file\": {\n" +
        "  //   \"server\": \"sqlhost01.example.com,1453\", \"database\": \"Reports\",\n" +
        "  //   \"auth\": \"runas\", \"user\": \"svc-reader@contoso.com\", \"password\": \"type-it-here-once\"\n" +
        "  // },\n" +
        "  //\n" +
        "  // \"passwordStore\": \"credman\" works for a SQL login too; \"credential\" names another Credential Manager entry.\n" +
        "  // encrypt: strict, mandatory (the default) or optional; trustServerCertificate only for a self-signed certificate;\n" +
        "  // connectTimeoutSeconds: 1 to 120 (15 by default). access: read (the default) or readwrite — sql_execute may change a\n" +
        "  // readwrite connection's databases while SQL mode is read-write, each change allowed by you. A read-only login is the\n" +
        "  // real guard for the rest.\n" +
        "  \"connections\": {}\n" +
        "}\n";

    /// <summary>The connections by name, in file order (the deserializer keeps it).</summary>
    public Dictionary<string, SqlConnectionConfig?> Connections { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The profile's file: <c>&lt;profile&gt;\sql.json</c>.</summary>
    public static string ProfilePath(string profileDirectory) => Path.Combine(profileDirectory, FileName);

    /// <summary>The home's file: <c>&lt;home&gt;\sql.json</c>, every profile's.</summary>
    public static string GlobalPath(string home) => Path.Combine(home, FileName);

    /// <summary>
    /// Reads <paramref name="path"/>: the usable connections in file order and a problem per entry that cannot
    /// connect (<see cref="SqlConnectionConfig.Problem"/>) or has a blank name — for a file that is not JSON or
    /// cannot be read, one problem for the file and one <c>Sql</c> warning. A missing file is empty. Never throws.
    /// </summary>
    public static SqlCatalog Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return SqlCatalog.Empty;
        }

        SqlConfigFile? file;
        try
        {
            file = JsonSerializer.Deserialize(File.ReadAllText(path), SqlJsonContext.Default.SqlConfigFile);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            string detail = LogText.Excerpt(ex.Message);
            DiagnosticLog.Warn(Category, SqlText.ConfigProblemLogLine(path, detail));
            return new SqlCatalog([], [new SqlConfigProblem(path, SqlText.UnreadableFile(detail), WholeFile: true)]);
        }

        if (file is null || file.Connections.Count == 0)
        {
            return SqlCatalog.Empty;
        }

        var connections = new List<SqlNamedConnection>(file.Connections.Count);
        var problems = new List<SqlConfigProblem>();
        foreach (var (name, config) in file.Connections)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                problems.Add(new SqlConfigProblem(path, SqlText.BlankName));
                continue;
            }

            string? reason = config is null ? SqlText.NoServer : config.Problem;
            if (reason is not null)
            {
                problems.Add(new SqlConfigProblem(SqlText.ConnectionSource(path, name), reason, Name: name.Trim()));
                continue;
            }

            EncryptInPlace(path, name.Trim(), config!);
            connections.Add(new SqlNamedConnection(name.Trim(), config!, path));
        }

        return new SqlCatalog(connections, problems);
    }

    /// <summary>
    /// The safety net under the SQL tab's masked prompt (later on 2026-09-23, the user's call): a plain-text
    /// <c>password</c> under the <c>file</c> store is DPAPI-encrypted and written back over that one value
    /// (<see cref="WritePassword"/>) at the first read that sees it, the file's comments and layout kept. A file that
    /// cannot be written keeps working with the plain value, and says so once per read in the log.
    /// </summary>
    private static void EncryptInPlace(string path, string name, SqlConnectionConfig config)
    {
        if (!config.NeedsPassword || config.InCredentialManager || string.IsNullOrEmpty(config.Password)
            || WindowsCredentials.IsProtected(config.Password) || !WindowsCredentials.CanProtect)
        {
            return;
        }

        var encrypted = WindowsCredentials.Protect(config.Password);
        string? error = encrypted.Error ?? WritePassword(path, name, encrypted.Value!);
        if (error is null)
        {
            config.Password = encrypted.Value;
            DiagnosticLog.Info(Category, SqlText.EncryptedLogLine(name, path));
        }
        else
        {
            DiagnosticLog.Warn(Category, SqlText.EncryptFailedLogLine(name, path, error));
        }
    }

    /// <summary>
    /// Writes <paramref name="value"/> as the <c>password</c> of connection <paramref name="name"/> in <paramref name="path"/>,
    /// touching nothing else (<see cref="ConnectionsFileEdit.WritePassword"/>: the token replaced, or the key inserted after
    /// the connection's <c>user</c>). Null on success, else why not.
    /// </summary>
    public static string? WritePassword(string path, string name, string value) => ConnectionsFileEdit.WritePassword(path, name, value);

    /// <summary>
    /// Adds connection <paramref name="name"/> to <paramref name="path"/> (2026-09-23, the SQL tab's <c>SQL add connection</c>
    /// wizard, the user's ask: step through every choice rather than hand-write the entry), the file's comments and layout
    /// kept (<see cref="ConnectionsFileEdit.AddConnection"/>; a missing file is made with <see cref="EmptyText"/> first). The
    /// entry is <paramref name="config"/> through <see cref="SqlJsonContext"/>, its <c>password</c> left out: the caller stores
    /// it after (<see cref="SqlSecrets.Save"/>), so a plain password never reaches the disk. A name already in the file is
    /// refused. Null on success, else why not.
    /// </summary>
    public static string? AddConnection(string path, string name, SqlConnectionConfig config)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(config);
        return ConnectionsFileEdit.AddConnection(path, name, EntryText(config), EmptyText);
    }

    /// <summary>
    /// Replaces connection <paramref name="oldName"/> in <paramref name="path"/> with <paramref name="newName"/> and
    /// <paramref name="config"/> (2026-10-05, the user's ask: <c>SQL add/edit connection</c> edits an entry too), comments and
    /// layout kept (<see cref="ConnectionsFileEdit.ReplaceConnection"/>), its <c>password</c> left out as for an add. Null on
    /// success, else why not.
    /// </summary>
    public static string? ReplaceConnection(string path, string oldName, string newName, SqlConnectionConfig config)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(oldName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        ArgumentNullException.ThrowIfNull(config);
        return ConnectionsFileEdit.ReplaceConnection(path, oldName, newName, EntryText(config));
    }

    /// <summary>
    /// One connection object as <see cref="AddConnection"/> writes it: <see cref="SqlJsonContext"/>'s keys in its order,
    /// indented two spaces a level, no <c>password</c>, the relaxed escaping (a <c>DOMAIN\name</c> reads as typed, the
    /// backslash doubled); the splice indents it under the entry's name and puts in the file's line ends.
    /// </summary>
    private static string EntryText(SqlConnectionConfig config)
    {
        string? password = config.Password;
        config.Password = null;
        try
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                JsonSerializer.Serialize(writer, config, SqlJsonContext.Default.SqlConnectionConfig);
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
        finally
        {
            config.Password = password;
        }
    }

    /// <summary>
    /// Every <c>sql.json</c> under <paramref name="home"/> read once — the home's and each profile's, not only the
    /// loaded one's (later on 2026-09-23, the user's ask: a password typed into any of them is encrypted when the app
    /// starts, not at the first read that happens to need that file) — so <see cref="Load"/>'s encryption runs over
    /// them all. The interactive screen and headless mode call it at startup; the check modes do not (a diagnostic
    /// run leaves the user's files alone). The files read, in order; a profiles folder that cannot be listed is a
    /// warning and the home's file alone. Never throws.
    /// </summary>
    public static IReadOnlyList<string> EncryptAll(string home)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        var paths = new List<string> { GlobalPath(home) };
        try
        {
            paths.AddRange(Settings.Profiles.List(home).Select(name => ProfilePath(Settings.Profiles.Directory(home, name))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(Category, SqlText.ProfilesUnlistedLogLine(Settings.Profiles.Root(home), LogText.Excerpt(ex.Message)));
        }

        var read = new List<string>();
        foreach (string path in paths.Where(File.Exists))
        {
            Load(path);
            read.Add(path);
        }

        return read;
    }

    /// <summary>The profile's file over the home's (<paramref name="home"/> null = the profile's alone): a name in both is the profile's.</summary>
    public static SqlCatalog LoadCatalog(string profileDirectory, string? home)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        var profile = Load(ProfilePath(profileDirectory));
        if (home is null || string.Equals(Path.GetFullPath(ProfilePath(profileDirectory)), Path.GetFullPath(GlobalPath(home)), StringComparison.OrdinalIgnoreCase))
        {
            return profile;
        }

        var global = Load(GlobalPath(home));
        var names = new HashSet<string>(profile.Connections.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        return new SqlCatalog(
            [.. profile.Connections, .. global.Connections.Where(c => !names.Contains(c.Name))],
            [.. profile.Problems, .. global.Problems]);
    }

    /// <summary>Writes <see cref="EmptyText"/> to <paramref name="path"/> when no file is there (the folder made first); true when it wrote. Throws on an IO failure — the caller's notice.</summary>
    public static bool EnsureExists(string path) => ConnectionsFileEdit.EnsureExists(path, EmptyText);
}
