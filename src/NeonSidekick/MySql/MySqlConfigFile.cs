using System.Text.Json;
using NeonSidekick.Sql;

namespace NeonSidekick.MySql;

/// <summary>
/// What <see cref="MySqlConfigFile.LoadCatalog"/> found: the usable connections (the profile's first, then the home's the
/// profile does not shadow, each in file order) and the problems, never a throw. The <see cref="SqlCatalog"/> shape.
/// </summary>
public sealed record MySqlCatalog(IReadOnlyList<MySqlNamedConnection> Connections, IReadOnlyList<SqlConfigProblem> Problems, int Hidden = 0)
{
    public static readonly MySqlCatalog Empty = new([], []);

    /// <summary>
    /// The catalog narrowed to the connections a profile offers (<c>MySQL connections offered</c>): only those whose name is in
    /// <paramref name="names"/> (case-insensitive), in file order, <see cref="Hidden"/> counting the rest; null offers none, as
    /// an empty list does (2026-10-01, the user's call; it was "all" until then). The problems are kept. Pure.
    /// </summary>
    public MySqlCatalog Offered(IReadOnlyList<string>? names)
    {
        var kept = ConnectionCatalog.Offered(Connections, names);
        return this with { Connections = kept, Hidden = Hidden + Connections.Count - kept.Count };
    }

    /// <summary>
    /// The connection a call means: <paramref name="name"/> when given (case-insensitive), else <paramref name="defaultName"/>
    /// when that names one, else the first. Null when nothing matches.
    /// </summary>
    public MySqlNamedConnection? Find(string? name, string? defaultName) => ConnectionCatalog.Find(Connections, name, defaultName);
}

/// <summary>
/// <c>mysql.json</c> (2026-09-30): <c>{ "connections": { "&lt;name&gt;": { … } } }</c>, one in the profile's folder and one in
/// the home, the <see cref="SqlConfigFile"/> pair: the profile's wins by name. A missing file is empty; a corrupt or
/// unreadable one is a problem and one warning, never a crash. Read afresh at every call (<see cref="MySqlAccess"/>), so an
/// edit in the editor counts on the next tool call. A plain <c>password</c> is DPAPI-encrypted in place at the first read
/// that sees it; the byte-level edits are <see cref="ConnectionsFileEdit"/>'s. The work is <see cref="Family"/>'s, shared with the
/// other password families since the 2026-10-04 review; this class is the file's shape and the family's facts.
/// </summary>
public sealed class MySqlConfigFile
{
    /// <summary>The file's name in a profile's folder and in the home.</summary>
    public const string FileName = "mysql.json";

    /// <summary>The log category of everything MySQL.</summary>
    public const string Category = "MySql";

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
        "  // MySQL or MariaDB, the password kept in this file: typed in plain text, it is encrypted (\"dpapi:…\") when the app\n" +
        "  // next starts or reads this file.\n" +
        "  // \"shop\": {\n" +
        "  //   \"host\": \"localhost\", \"port\": 3306, \"database\": \"shop\", \"user\": \"shop_reader\", \"password\": \"type-it-here-once\",\n" +
        "  //   \"description\": \"the sample retail database\"\n" +
        "  // },\n" +
        "  //\n" +
        "  // TLS checked against the host name, the password in Windows Credential Manager as NeonSidekick/mysql/<name> — set it\n" +
        "  // with MySQL set password on the MySQL tab of /tools, or: cmdkey /generic:NeonSidekick/mysql/billing /user:billing_ro /pass\n" +
        "  // \"billing\": {\n" +
        "  //   \"host\": \"db01.example.com\", \"database\": \"billing\", \"user\": \"billing_ro\", \"passwordStore\": \"credman\",\n" +
        "  //   \"sslMode\": \"verify-full\"\n" +
        "  // },\n" +
        "  //\n" +
        "  // port: 3306 by default; database: the one a call works in when it names none (the listings then cover them all);\n" +
        "  // sslMode: preferred (the default), required, verify-ca, verify-full or none; allowPublicKeyRetrieval: true only for a\n" +
        "  // caching_sha2_password account over a connection without TLS; connectTimeoutSeconds: 1 to 120 (15 by default).\n" +
        "  // access: read (the default) or readwrite — mysql_execute may change a readwrite connection's databases while MySQL\n" +
        "  // mode is read-write, each change allowed by you. An account with SELECT grants alone is the real guard for the rest.\n" +
        "  \"connections\": {}\n" +
        "}\n";

    /// <summary>The connections by name, in file order (the deserializer keeps it).</summary>
    public Dictionary<string, MySqlConnectionConfig?> Connections { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The family's code over this file's facts.</summary>
    public static readonly ConnectionFamily<MySqlConnectionConfig, MySqlNamedConnection> Family = new()
    {
        FileName = FileName,
        Category = Category,
        EmptyText = EmptyText,
        Tab = "the MySQL tab",
        NullEntry = MySqlText.NoHost,
        Deserialize = json => JsonSerializer.Deserialize(json, MySqlJsonContext.Default.MySqlConfigFile)?.Connections,
        EntryInfo = MySqlJsonContext.Default.MySqlConnectionConfig,
        Named = (name, config, source) => new MySqlNamedConnection(name, config, source),
        NoPassword = MySqlText.NoPassword,
        NoCredential = MySqlText.NoCredential,
        ProfilesUnlistedLogLine = MySqlText.ProfilesUnlistedLogLine,
    };

    /// <summary>The profile's file: <c>&lt;profile&gt;\mysql.json</c>.</summary>
    public static string ProfilePath(string profileDirectory) => Family.ProfilePath(profileDirectory);

    /// <summary>The home's file: <c>&lt;home&gt;\mysql.json</c>, every profile's.</summary>
    public static string GlobalPath(string home) => Family.GlobalPath(home);

    /// <summary>Reads <paramref name="path"/> (<see cref="ConnectionFamily{TConfig, TNamed}.Load"/>): the usable connections and the problems. Never throws.</summary>
    public static MySqlCatalog Load(string path)
    {
        var (connections, problems) = Family.Load(path);
        return connections.Count == 0 && problems.Count == 0 ? MySqlCatalog.Empty : new MySqlCatalog(connections, problems);
    }

    /// <summary>Writes <paramref name="value"/> as the <c>password</c> of connection <paramref name="name"/> in <paramref name="path"/>, touching nothing else (<see cref="ConnectionsFileEdit.WritePassword"/>). Null on success, else why not.</summary>
    public static string? WritePassword(string path, string name, string value) => ConnectionsFileEdit.WritePassword(path, name, value);

    /// <summary>Adds connection <paramref name="name"/> to <paramref name="path"/> (the MySQL tab's wizard), its <c>password</c> left out (<see cref="ConnectionFamily{TConfig, TNamed}.AddConnection"/>). Null on success, else why not.</summary>
    public static string? AddConnection(string path, string name, MySqlConnectionConfig config) => Family.AddConnection(path, name, config);

    /// <summary>Every <c>mysql.json</c> under <paramref name="home"/> read once, so a plain password is encrypted at startup (<see cref="ConnectionFamily{TConfig, TNamed}.EncryptAll"/>). The files read. Never throws.</summary>
    public static IReadOnlyList<string> EncryptAll(string home) => Family.EncryptAll(home);

    /// <summary>The profile's file over the home's (<paramref name="home"/> null = the profile's alone): a name in both is the profile's.</summary>
    public static MySqlCatalog LoadCatalog(string profileDirectory, string? home)
    {
        var (connections, problems) = Family.LoadCatalog(profileDirectory, home);
        return new MySqlCatalog(connections, problems);
    }

    /// <summary>Writes <see cref="EmptyText"/> to <paramref name="path"/> when no file is there (the folder made first); true when it wrote. Throws on an IO failure — the caller's notice.</summary>
    public static bool EnsureExists(string path) => ConnectionsFileEdit.EnsureExists(path, EmptyText);
}
