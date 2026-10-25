using System.Text.Json;

namespace NeonSidekick.Sql;

/// <summary>
/// One thing a load could not use: the file itself, or one connection in it. <see cref="Source"/> is the path, or the path and the
/// connection's name. <see cref="Name"/> is that connection's name (trimmed) and <see cref="WholeFile"/> marks a file that could not be
/// read at all (2026-10-04): the offered checklists never prune a name against a load that lost a whole file, and a name a problem
/// names still counts as there (<see cref="App.OfferedNames"/>).
/// </summary>
/// <remarks>
/// <paramref name="Entry"/> (2026-10-09) is the skipped entry as read, when one was: the tools never use it, but the shell's
/// <c>ServerDatabasePolice</c> still guards its host — an entry from before the database was required must not open a way round it.
/// </remarks>
public sealed record SqlConfigProblem(string Source, string Reason, string? Name = null, bool WholeFile = false, object? Entry = null);

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
        var kept = ConnectionCatalog.Offered(Connections, names);
        return this with { Connections = kept, Problems = ConnectionCatalog.OfferedProblems(Problems, names), Hidden = Hidden + Connections.Count - kept.Count };
    }

    /// <summary>
    /// The connection a call means: <paramref name="name"/> when given (case-insensitive), else the only one when just one is
    /// offered (2026-10-09, <see cref="ConnectionCatalog.Find"/>'s rule). Null when nothing matches, or when several are and none is named.
    /// </summary>
    public SqlNamedConnection? Find(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            return Connections.FirstOrDefault(c => string.Equals(c.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        return Connections.Count == 1 ? Connections[0] : null;
    }
}

/// <summary>
/// <c>sql.json</c> (2026-09-23): <c>{ "connections": { "&lt;name&gt;": { … } } }</c>, one in the profile's <c>configs</c> folder
/// and one in the home's (since 2026-10-16), the <c>mcp.json</c> pair (<see cref="Mcp.McpConfigFile"/>): the profile's wins by name.
/// A missing file is empty; a corrupt or unreadable one is a problem and one warning, never a crash. Read
/// afresh at every call (<see cref="SqlAccess"/>), so an edit in the editor counts on the next tool call with no
/// reload. The <c>/tools</c> SQL tab's edit rows create the file with <see cref="EmptyText"/> when it is missing.
/// </summary>
public sealed class SqlConfigFile
{
    /// <summary>The file's name in a profile's <c>configs</c> folder and in the home.</summary>
    public const string FileName = "sql.json";

    /// <summary>The log category of everything SQL.</summary>
    public const string Category = "Sql";

    /// <summary>
    /// What a fresh file holds: no connections, and a commented example of each kind — a SQL login, Windows sign-in as
    /// the user, and <c>runas</c> with its password in Credential Manager or in the file (later on 2026-09-23, the
    /// user's ask) — each one valid once its <c>//</c> are removed (pinned by a test). Pinned.
    /// </summary>
    public static string EmptyText => OperatingSystem.IsMacOS() ? MacEmptyText : WindowsEmptyText;

    /// <summary>
    /// <see cref="EmptyText"/> on macOS (2026-10-06, the tidy-up before the first Mac release: a Mac user never reads Windows
    /// wording): the SQL login and Windows sign-in examples kept — <c>auth: windows</c> works on a Mac through Kerberos (proven
    /// by the user, so it must never be dropped there) — and Windows' two <c>runas</c> examples gone. The <c>credman</c> example is a
    /// SQL login's, in the Keychain with <see cref="SqlText.CredentialCommand"/>'s <c>security</c> line, and a typed password becomes
    /// <c>keychain:…</c>. Since 2026-10-11 one <c>runas</c> example is back, a Mac's own: <c>name@REALM</c>, its password in the
    /// Keychain, the ticket it leaves in the user's credential cache said (<see cref="KerberosSignIn"/>).
    /// Built on each read, so no static-field order can catch it unset. Pinned on a Mac.
    /// </summary>
    private static string MacEmptyText =>
        "{\n" +
        "  // One entry per connection. Its name is what the model passes as \"connection\", and what %name picks on the input\n" +
        "  // line. \"server\" and \"database\" are required, every other key optional: a connection reaches its database alone,\n" +
        "  // and another database is another entry. Remove the leading // from an example to use it, and put it inside\n" +
        "  // \"connections\" below.\n" +
        "  //\n" +
        "  // A SQL login (auth sql), the password kept in this file: typed in plain text, it is encrypted (\"" + WindowsCredentials.KeychainPrefix + "…\")\n" +
        "  // when the app next starts or reads this file.\n" +
        "  // \"adventureworks\": {\n" +
        "  //   \"server\": \"127.0.0.1,1433\", \"database\": \"AdventureWorks2022\",\n" +
        "  //   \"auth\": \"sql\", \"user\": \"reader\", \"password\": \"type-it-here-once\",\n" +
        "  //   \"encrypt\": \"mandatory\", \"trustServerCertificate\": true,\n" +
        "  //   \"description\": \"the sample sales database\"\n" +
        "  // },\n" +
        "  //\n" +
        "  // Windows sign-in as you through Kerberos (auth windows: the ticket kinit gave you, no password).\n" +
        "  // \"reports-me\": {\n" +
        "  //   \"server\": \"sqlhost01.example.com,1453\", \"database\": \"Reports\",\n" +
        "  //   \"auth\": \"windows\"\n" +
        "  // },\n" +
        "  //\n" +
        "  // A SQL login with the password in " + SqlText.CredentialStore + " as NeonSidekick/sql/<name> — set it with SQL set password\n" +
        "  // on /tools › Databases › SQL, or: " + SqlText.CredentialCommand("NeonSidekick/sql/reports-reader", "reader") + "\n" +
        "  // \"reports-reader\": {\n" +
        "  //   \"server\": \"sqlhost01.example.com,1453\", \"database\": \"Reports\",\n" +
        "  //   \"auth\": \"sql\", \"user\": \"reader\", \"passwordStore\": \"credman\"\n" +
        "  // },\n" +
        "  //\n" +
        "  // Kerberos sign-in as another account (auth runas, the account as name@REALM): the app gets that account's ticket\n" +
        "  // with its password at each connect, and macOS keeps it in a credential cache of its own beside yours (klist -l).\n" +
        "  // The password in " + SqlText.CredentialStore + " as NeonSidekick/sql/<name> (SQL set password, as above), or:\n" +
        "  // " + SqlText.CredentialCommand("NeonSidekick/sql/reports-admin", "svc-reader@AD.EXAMPLE.EDU") + "\n" +
        "  // \"reports-admin\": {\n" +
        "  //   \"server\": \"sqlhost01.example.com,1453\", \"database\": \"Reports\",\n" +
        "  //   \"auth\": \"runas\", \"user\": \"svc-reader@AD.EXAMPLE.EDU\", \"passwordStore\": \"credman\"\n" +
        "  // },\n" +
        "  //\n" +
        "  // \"credential\" names another Keychain entry.\n" +
        "  // encrypt: strict, mandatory (the default) or optional; trustServerCertificate only for a self-signed certificate;\n" +
        "  // connectTimeoutSeconds: 1 to 120 (15 by default). access: read (the default) or readwrite — sql_execute may change a\n" +
        "  // readwrite connection's database while SQL mode is read-write, each change allowed by you. A read-only login is the\n" +
        "  // real guard for the rest.\n" +
        "  \"connections\": {}\n" +
        "}\n";

    /// <summary>What a fresh file holds on Windows (and off macOS): <see cref="EmptyText"/>'s text before the macOS build. Pinned.</summary>
    public const string WindowsEmptyText =
        "{\n" +
        "  // One entry per connection. Its name is what the model passes as \"connection\", and what %name picks on the input\n" +
        "  // line. \"server\" and \"database\" are required, every other key optional: a connection reaches its database alone,\n" +
        "  // and another database is another entry. A JSON backslash is doubled: \"CONTOSO\\\\svc-reader\". Remove the\n" +
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
        "  // Windows Credential Manager as NeonSidekick/sql/<name> — set it with SQL set password on /tools › Databases › SQL,\n" +
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
        "  // readwrite connection's database while SQL mode is read-write, each change allowed by you. A read-only login is the\n" +
        "  // real guard for the rest.\n" +
        "  \"connections\": {}\n" +
        "}\n";

    /// <summary>The connections by name, in file order (the deserializer keeps it).</summary>
    public Dictionary<string, SqlConnectionConfig?> Connections { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Keys beside <c>connections</c>: listed as problems, never used (2026-10-24, the database review's K2).</summary>
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>
    /// The family's code over this file's facts (2026-10-24, the database review's Q11: the load, the encrypt-in-place, the splices and the
    /// secrets were a copy of <see cref="ConnectionFamily{TConfig, TNamed}"/>'s, and its credman wording had already drifted from the other
    /// families'). The Windows sign-in kinds stay in <see cref="SqlConnectionConfig.Problem"/> and <see cref="SqlAccess"/>.
    /// </summary>
    public static readonly ConnectionFamily<SqlConnectionConfig, SqlNamedConnection> Family = new()
    {
        FileName = FileName,
        Category = Category,
        EmptyText = EmptyText,
        Tab = SqlText.SqlMenuPath,
        NullEntry = SqlText.NoServer,
        Deserialize = json => JsonSerializer.Deserialize(json, SqlJsonContext.Default.SqlConfigFile) is { } file ? (file.Connections, file.Extra) : (null, null),
        CredentialPrefix = SqlConnectionConfig.CredentialPrefix,
        EntryInfo = SqlJsonContext.Default.SqlConnectionConfig,
        Named = (name, config, source) => new SqlNamedConnection(name, config, source),
        NoPassword = SqlText.NoPassword,
        NoCredential = SqlText.NoCredential,
        ProfilesUnlistedLogLine = SqlText.ProfilesUnlistedLogLine,
    };

    /// <summary>The profile's file: <c>&lt;profile&gt;\configs\sql.json</c> (<c>configs</c> since 2026-10-16).</summary>
    public static string ProfilePath(string profileDirectory) => NeonSidekick.Settings.Profiles.ConfigPath(profileDirectory, FileName);

    /// <summary>The home's file: <c>&lt;home&gt;\configs\sql.json</c> (configs since 2026-10-16), every profile's.</summary>
    public static string GlobalPath(string home) => NeonSidekick.Settings.Profiles.HomeConfigPath(home, FileName);

    /// <summary>
    /// Reads <paramref name="path"/>: the usable connections in file order and a problem per entry that cannot connect
    /// (<see cref="SqlConnectionConfig.Problem"/>) or has a blank name; a file that is not JSON or cannot be read is one problem and one
    /// <c>Sql</c> warning. A missing file is empty. Never throws (<see cref="ConnectionFamily{TConfig, TNamed}.Load"/>).
    /// </summary>
    public static SqlCatalog Load(string path)
    {
        var (connections, problems) = Family.Load(path);
        return connections.Count == 0 && problems.Count == 0 ? SqlCatalog.Empty : new SqlCatalog(connections, problems);
    }

    /// <summary>
    /// Writes <paramref name="value"/> as the <c>password</c> of connection <paramref name="name"/> in <paramref name="path"/>,
    /// touching nothing else (<see cref="ConnectionsFileEdit.WritePassword"/>). Null on success, else why not.
    /// </summary>
    public static string? WritePassword(string path, string name, string value) => ConnectionsFileEdit.WritePassword(path, name, value);

    /// <summary>
    /// Adds connection <paramref name="name"/> to <paramref name="path"/> (2026-09-23, the SQL tab's <c>SQL add connection</c> wizard), the
    /// file's comments and layout kept, its <c>password</c> left out: the caller stores it after (<see cref="SqlSecrets.Save"/>). A name
    /// already in the file is refused. Null on success, else why not.
    /// </summary>
    public static string? AddConnection(string path, string name, SqlConnectionConfig config) => Family.AddConnection(path, name, config);

    /// <summary>Replaces connection <paramref name="oldName"/> with <paramref name="newName"/> and <paramref name="config"/> (2026-10-05, the wizard's edit), its <c>password</c> left out as for an add.</summary>
    public static string? ReplaceConnection(string path, string oldName, string newName, SqlConnectionConfig config) => Family.ReplaceConnection(path, oldName, newName, config);

    /// <summary>
    /// Every <c>sql.json</c> under <paramref name="home"/> read once — the home's and each profile's (later on 2026-09-23, the user's ask: a
    /// password typed into any of them is encrypted when the app starts). The interactive screen and headless mode call it at startup; the
    /// check modes do not. The files read, in order. Never throws.
    /// </summary>
    public static IReadOnlyList<string> EncryptAll(string home) => Family.EncryptAll(home);

    /// <summary>The profile's file over the home's (<paramref name="home"/> null = the profile's alone): a name in both is the profile's.</summary>
    public static SqlCatalog LoadCatalog(string profileDirectory, string? home)
    {
        var (connections, problems) = Family.LoadCatalog(profileDirectory, home);
        return new SqlCatalog(connections, problems);
    }

    /// <summary>Writes <see cref="EmptyText"/> to <paramref name="path"/> when no file is there (the folder made first); true when it wrote. Throws on an IO failure — the caller's notice.</summary>
    public static bool EnsureExists(string path) => ConnectionsFileEdit.EnsureExists(path, EmptyText);
}
