using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using NeonSidekick.Diagnostics;
using NeonSidekick.Sql;

namespace NeonSidekick.Postgres;

/// <summary>
/// What <see cref="PostgresConfigFile.LoadCatalog"/> found: the usable connections (the profile's first, then the home's the
/// profile does not shadow, each in file order) and the problems, never a throw. The <see cref="SqlCatalog"/> shape.
/// </summary>
public sealed record PostgresCatalog(IReadOnlyList<PostgresNamedConnection> Connections, IReadOnlyList<SqlConfigProblem> Problems, int Hidden = 0)
{
    public static readonly PostgresCatalog Empty = new([], []);

    /// <summary>
    /// The catalog narrowed to the connections a profile offers (<c>PostgreSQL connections offered</c>): only those whose name is in
    /// <paramref name="names"/> (case-insensitive), in file order, <see cref="Hidden"/> counting the rest; null offers none, as
    /// an empty list does (2026-10-01, the user's call; it was "all" until then). The problems are kept. Pure.
    /// </summary>
    public PostgresCatalog Offered(IReadOnlyList<string>? names)
    {
        var wanted = new HashSet<string>((names ?? []).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
        var kept = Connections.Where(c => wanted.Contains(c.Name)).ToList();
        return this with { Connections = kept, Hidden = Hidden + Connections.Count - kept.Count };
    }

    /// <summary>
    /// The connection a call means: <paramref name="name"/> when given (case-insensitive), else <paramref name="defaultName"/>
    /// when that names one, else the first. Null when nothing matches.
    /// </summary>
    public PostgresNamedConnection? Find(string? name, string? defaultName)
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
/// <c>postgres.json</c> (2026-10-04): <c>{ "connections": { "&lt;name&gt;": { … } } }</c>, one in the profile's folder and one in
/// the home, the <see cref="SqlConfigFile"/> pair: the profile's wins by name. A missing file is empty; a corrupt or
/// unreadable one is a problem and one warning, never a crash. Read afresh at every call (<see cref="PostgresAccess"/>), so an
/// edit in the editor counts on the next tool call. A plain <c>password</c> is DPAPI-encrypted in place at the first read
/// that sees it; the byte-level edits are <see cref="ConnectionsFileEdit"/>'s.
/// </summary>
public sealed class PostgresConfigFile
{
    /// <summary>The file's name in a profile's folder and in the home.</summary>
    public const string FileName = "postgres.json";

    /// <summary>The log category of everything PostgreSQL.</summary>
    public const string Category = "Postgres";

    /// <summary>
    /// What a fresh file holds: no connections, and a commented example of each way to keep the password and to name the
    /// database — each one valid once its <c>//</c> are removed (pinned by a test). Pinned.
    /// </summary>
    public const string EmptyText =
        "{\n" +
        "  // One entry per connection. Its name is what the model passes as \"connection\", and what %name picks on the input\n" +
        "  // line. \"host\" and \"user\" are required. Remove the leading // from an example to use it, and put it inside\n" +
        "  // \"connections\" below.\n" +
        "  //\n" +
        "  // PostgreSQL, the password kept in this file: typed in plain text, it is encrypted (\"dpapi:…\") when the app next\n" +
        "  // starts or reads this file.\n" +
        "  // \"shop\": {\n" +
        "  //   \"host\": \"localhost\", \"port\": 5432, \"database\": \"shop\", \"user\": \"shop_reader\", \"password\": \"type-it-here-once\",\n" +
        "  //   \"description\": \"the sample retail database\"\n" +
        "  // },\n" +
        "  //\n" +
        "  // TLS checked against the host name, the password in Windows Credential Manager as NeonSidekick/postgres/<name> — set it\n" +
        "  // with PostgreSQL set password on the PostgreSQL tab of /tools, or: cmdkey /generic:NeonSidekick/postgres/billing /user:billing_ro /pass\n" +
        "  // \"billing\": {\n" +
        "  //   \"host\": \"db01.example.com\", \"database\": \"billing\", \"user\": \"billing_ro\", \"passwordStore\": \"credman\",\n" +
        "  //   \"sslMode\": \"verify-full\"\n" +
        "  // },\n" +
        "  //\n" +
        "  // port: 5432 by default; database: the one a call works in when it names none (postgres when absent); sslMode: prefer\n" +
        "  // (the default), require, verify-ca, verify-full or disable; connectTimeoutSeconds: 1 to 120 (15 by default). The tools\n" +
        "  // only read, but an account with SELECT grants alone is the real guard.\n" +
        "  \"connections\": {}\n" +
        "}\n";

    /// <summary>The connections by name, in file order (the deserializer keeps it).</summary>
    public Dictionary<string, PostgresConnectionConfig?> Connections { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The profile's file: <c>&lt;profile&gt;\postgres.json</c>.</summary>
    public static string ProfilePath(string profileDirectory) => Path.Combine(profileDirectory, FileName);

    /// <summary>The home's file: <c>&lt;home&gt;\postgres.json</c>, every profile's.</summary>
    public static string GlobalPath(string home) => Path.Combine(home, FileName);

    /// <summary>
    /// Reads <paramref name="path"/>: the usable connections in file order and a problem per entry that cannot connect
    /// (<see cref="PostgresConnectionConfig.Problem"/>) or has a blank name — for a file that is not JSON or cannot be read,
    /// one problem for the file and one <c>Postgres</c> warning. A missing file is empty. Never throws.
    /// </summary>
    public static PostgresCatalog Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return PostgresCatalog.Empty;
        }

        PostgresConfigFile? file;
        try
        {
            file = JsonSerializer.Deserialize(File.ReadAllText(path), PostgresJsonContext.Default.PostgresConfigFile);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            string detail = LogText.Excerpt(ex.Message);
            DiagnosticLog.Warn(Category, SqlText.ConfigProblemLogLine(path, detail));
            return new PostgresCatalog([], [new SqlConfigProblem(path, SqlText.UnreadableFile(detail))]);
        }

        if (file is null || file.Connections.Count == 0)
        {
            return PostgresCatalog.Empty;
        }

        var connections = new List<PostgresNamedConnection>(file.Connections.Count);
        var problems = new List<SqlConfigProblem>();
        foreach (var (name, config) in file.Connections)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                problems.Add(new SqlConfigProblem(path, SqlText.BlankName));
                continue;
            }

            string? reason = config is null ? PostgresText.NoHost : config.Problem;
            if (reason is not null)
            {
                problems.Add(new SqlConfigProblem(SqlText.ConnectionSource(path, name), reason));
                continue;
            }

            EncryptInPlace(path, name.Trim(), config!);
            connections.Add(new PostgresNamedConnection(name.Trim(), config!, path));
        }

        return new PostgresCatalog(connections, problems);
    }

    /// <summary>
    /// The safety net under the PostgreSQL tab's masked prompt, <see cref="SqlConfigFile"/>'s: a plain-text <c>password</c> under
    /// the <c>file</c> store is DPAPI-encrypted and written back over that one value at the first read that sees it, the
    /// file's comments and layout kept. A file that cannot be written keeps working with the plain value, and says so once
    /// per read in the log.
    /// </summary>
    private static void EncryptInPlace(string path, string name, PostgresConnectionConfig config)
    {
        if (config.InCredentialManager || string.IsNullOrEmpty(config.Password) || WindowsCredentials.IsProtected(config.Password) || !OperatingSystem.IsWindows())
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

    /// <summary>Writes <paramref name="value"/> as the <c>password</c> of connection <paramref name="name"/> in <paramref name="path"/>, touching nothing else (<see cref="ConnectionsFileEdit.WritePassword"/>). Null on success, else why not.</summary>
    public static string? WritePassword(string path, string name, string value) => ConnectionsFileEdit.WritePassword(path, name, value);

    /// <summary>
    /// Adds connection <paramref name="name"/> to <paramref name="path"/> (the PostgreSQL tab's <c>PostgreSQL add connection</c> wizard),
    /// the file's comments and layout kept (<see cref="ConnectionsFileEdit.AddConnection"/>; a missing file is made with
    /// <see cref="EmptyText"/> first). The entry is <paramref name="config"/> through <see cref="PostgresJsonContext"/>, its
    /// <c>password</c> left out: the caller stores it after (<see cref="PostgresSecrets.Save"/>). Null on success, else why not.
    /// </summary>
    public static string? AddConnection(string path, string name, PostgresConnectionConfig config)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(config);
        string? password = config.Password;
        config.Password = null;
        try
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                JsonSerializer.Serialize(writer, config, PostgresJsonContext.Default.PostgresConnectionConfig);
            }

            return ConnectionsFileEdit.AddConnection(path, name, Encoding.UTF8.GetString(stream.ToArray()), EmptyText);
        }
        finally
        {
            config.Password = password;
        }
    }

    /// <summary>
    /// Every <c>postgres.json</c> under <paramref name="home"/> read once — the home's and each profile's — so <see cref="Load"/>'s
    /// encryption runs over them all at startup (<see cref="SqlConfigFile.EncryptAll"/>'s part). The files read, in order;
    /// a profiles folder that cannot be listed is a warning and the home's file alone. Never throws.
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
            DiagnosticLog.Warn(Category, PostgresText.ProfilesUnlistedLogLine(Settings.Profiles.Root(home), LogText.Excerpt(ex.Message)));
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
    public static PostgresCatalog LoadCatalog(string profileDirectory, string? home)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        var profile = Load(ProfilePath(profileDirectory));
        if (home is null || string.Equals(Path.GetFullPath(ProfilePath(profileDirectory)), Path.GetFullPath(GlobalPath(home)), StringComparison.OrdinalIgnoreCase))
        {
            return profile;
        }

        var global = Load(GlobalPath(home));
        var names = new HashSet<string>(profile.Connections.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        return new PostgresCatalog(
            [.. profile.Connections, .. global.Connections.Where(c => !names.Contains(c.Name))],
            [.. profile.Problems, .. global.Problems]);
    }

    /// <summary>Writes <see cref="EmptyText"/> to <paramref name="path"/> when no file is there (the folder made first); true when it wrote. Throws on an IO failure — the caller's notice.</summary>
    public static bool EnsureExists(string path) => ConnectionsFileEdit.EnsureExists(path, EmptyText);
}
