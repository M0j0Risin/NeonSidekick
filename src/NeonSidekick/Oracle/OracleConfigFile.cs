using System.Text.Json;
using NeonSidekick.Sql;

namespace NeonSidekick.Oracle;

/// <summary>
/// What <see cref="OracleConfigFile.LoadCatalog"/> found: the usable connections (the profile's first, then the home's the
/// profile does not shadow, each in file order) and the problems, never a throw. The <see cref="SqlCatalog"/> shape.
/// </summary>
public sealed record OracleCatalog(IReadOnlyList<OracleNamedConnection> Connections, IReadOnlyList<SqlConfigProblem> Problems, int Hidden = 0)
{
    public static readonly OracleCatalog Empty = new([], []);

    /// <summary>
    /// The catalog narrowed to the connections a profile offers (<c>Oracle connections offered</c>): only those whose name is in
    /// <paramref name="names"/> (case-insensitive), in file order, <see cref="Hidden"/> counting the rest; null offers none, as
    /// an empty list does (2026-10-01, the user's call; it was "all" until then). The problems are kept. Pure.
    /// </summary>
    public OracleCatalog Offered(IReadOnlyList<string>? names)
    {
        var kept = ConnectionCatalog.Offered(Connections, names);
        return this with { Connections = kept, Hidden = Hidden + Connections.Count - kept.Count };
    }

    /// <summary>
    /// The connection a call means: <paramref name="name"/> when given (case-insensitive), else <paramref name="defaultName"/>
    /// when that names one, else the first. Null when nothing matches.
    /// </summary>
    public OracleNamedConnection? Find(string? name, string? defaultName) => ConnectionCatalog.Find(Connections, name, defaultName);
}

/// <summary>
/// <c>oracle.json</c> (2026-09-30): <c>{ "connections": { "&lt;name&gt;": { … } } }</c>, one in the profile's folder and one in
/// the home, the <see cref="SqlConfigFile"/> pair: the profile's wins by name. A missing file is empty; a corrupt or
/// unreadable one is a problem and one warning, never a crash. Read afresh at every call (<see cref="OracleAccess"/>), so an
/// edit in the editor counts on the next tool call. A plain <c>password</c> is DPAPI-encrypted in place at the first read
/// that sees it; the byte-level edits are <see cref="ConnectionsFileEdit"/>'s. The work is <see cref="Family"/>'s, shared with the
/// other password families since the 2026-10-04 review; this class is the file's shape and the family's facts.
/// </summary>
public sealed class OracleConfigFile
{
    /// <summary>The file's name in a profile's folder and in the home.</summary>
    public const string FileName = "oracle.json";

    /// <summary>The log category of everything Oracle.</summary>
    public const string Category = "Oracle";

    /// <summary>
    /// What a fresh file holds: no connections, and a commented example of each way to keep the password and to name the
    /// database — each one valid once its <c>//</c> are removed (pinned by a test). Pinned.
    /// </summary>
    public static string EmptyText => OperatingSystem.IsMacOS() ? MacEmptyText : WindowsEmptyText;

    /// <summary>
    /// <see cref="EmptyText"/> on macOS (2026-10-06, the tidy-up before the first Mac release: a Mac user never reads Windows
    /// wording): <see cref="WindowsEmptyText"/> with the Keychain where Credential Manager stood, <c>security add-generic-password</c>
    /// for <c>cmdkey</c> (<see cref="Sql.SqlText.CredentialCommand"/>) and <c>keychain:</c> for <c>dpapi:</c>, the prefix
    /// <see cref="Sql.MacKeychain"/> writes. Built on each read, so no static-field order can catch it unset. Pinned on a Mac.
    /// </summary>
    private static string MacEmptyText => WindowsEmptyText
        .Replace("(\"dpapi:…\")", "(\"" + Sql.WindowsCredentials.KeychainPrefix + "…\")", StringComparison.Ordinal)
        .Replace("Windows Credential Manager", Sql.SqlText.CredentialStore, StringComparison.Ordinal)
        .Replace("cmdkey /generic:NeonSidekick/oracle/ledger /user:ledger_ro /pass", Sql.SqlText.CredentialCommand("NeonSidekick/oracle/ledger", "ledger_ro"), StringComparison.Ordinal)
        .Replace("Credential Manager entry", "Keychain entry", StringComparison.Ordinal);

    /// <summary>What a fresh file holds on Windows (and off macOS): <see cref="EmptyText"/>'s text before the macOS build. Pinned.</summary>
    public const string WindowsEmptyText =
        "{\n" +
        "  // One entry per connection. Its name is what the model passes as \"connection\", and what %name picks on the input\n" +
        "  // line. \"dataSource\" and \"user\" are required. Remove the leading // from an example to use it, and put it inside\n" +
        "  // \"connections\" below.\n" +
        "  //\n" +
        "  // EZConnect (host:port/service), the password kept in this file: typed in plain text, it is encrypted (\"dpapi:…\")\n" +
        "  // when the app next starts or reads this file.\n" +
        "  // \"hr\": {\n" +
        "  //   \"dataSource\": \"localhost:1521/FREEPDB1\", \"user\": \"hr_reader\", \"password\": \"type-it-here-once\",\n" +
        "  //   \"schema\": \"HR\", \"description\": \"the sample human-resources schema\"\n" +
        "  // },\n" +
        "  //\n" +
        "  // A full descriptor, the password in Windows Credential Manager as NeonSidekick/oracle/<name> — set it with Oracle set\n" +
        "  // password on the Oracle tab of /tools, or: cmdkey /generic:NeonSidekick/oracle/ledger /user:ledger_ro /pass\n" +
        "  // \"ledger\": {\n" +
        "  //   \"dataSource\": \"(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=dbhost01.example.com)(PORT=1521))(CONNECT_DATA=(SERVICE_NAME=LEDGER)))\",\n" +
        "  //   \"user\": \"ledger_ro\", \"passwordStore\": \"credman\"\n" +
        "  // },\n" +
        "  //\n" +
        "  // \"schema\": the schema a call works in when it names none (the user's own by default); \"credential\" names another\n" +
        "  // Credential Manager entry; connectTimeoutSeconds: 1 to 120 (15 by default). SYS is refused. access: read (the\n" +
        "  // default) or readwrite — oracle_execute may change a readwrite connection's schemas while Oracle mode is read-write,\n" +
        "  // each change allowed by you. A read-only account (ALTER USER … READ ONLY on 23ai, or SELECT grants alone) is the\n" +
        "  // real guard for the rest.\n" +
        "  \"connections\": {}\n" +
        "}\n";

    /// <summary>The connections by name, in file order (the deserializer keeps it).</summary>
    public Dictionary<string, OracleConnectionConfig?> Connections { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The family's code over this file's facts.</summary>
    public static readonly ConnectionFamily<OracleConnectionConfig, OracleNamedConnection> Family = new()
    {
        FileName = FileName,
        Category = Category,
        EmptyText = EmptyText,
        Tab = "the Oracle tab",
        NullEntry = OracleText.NoDataSource,
        Deserialize = json => JsonSerializer.Deserialize(json, OracleJsonContext.Default.OracleConfigFile)?.Connections,
        EntryInfo = OracleJsonContext.Default.OracleConnectionConfig,
        Named = (name, config, source) => new OracleNamedConnection(name, config, source),
        NoPassword = OracleText.NoPassword,
        NoCredential = OracleText.NoCredential,
        ProfilesUnlistedLogLine = OracleText.ProfilesUnlistedLogLine,
    };

    /// <summary>The profile's file: <c>&lt;profile&gt;\oracle.json</c>.</summary>
    public static string ProfilePath(string profileDirectory) => Family.ProfilePath(profileDirectory);

    /// <summary>The home's file: <c>&lt;home&gt;\oracle.json</c>, every profile's.</summary>
    public static string GlobalPath(string home) => Family.GlobalPath(home);

    /// <summary>Reads <paramref name="path"/> (<see cref="ConnectionFamily{TConfig, TNamed}.Load"/>): the usable connections and the problems. Never throws.</summary>
    public static OracleCatalog Load(string path)
    {
        var (connections, problems) = Family.Load(path);
        return connections.Count == 0 && problems.Count == 0 ? OracleCatalog.Empty : new OracleCatalog(connections, problems);
    }

    /// <summary>Writes <paramref name="value"/> as the <c>password</c> of connection <paramref name="name"/> in <paramref name="path"/>, touching nothing else (<see cref="ConnectionsFileEdit.WritePassword"/>). Null on success, else why not.</summary>
    public static string? WritePassword(string path, string name, string value) => ConnectionsFileEdit.WritePassword(path, name, value);

    /// <summary>Adds connection <paramref name="name"/> to <paramref name="path"/> (the Oracle tab's wizard), its <c>password</c> left out (<see cref="ConnectionFamily{TConfig, TNamed}.AddConnection"/>). Null on success, else why not.</summary>
    public static string? AddConnection(string path, string name, OracleConnectionConfig config) => Family.AddConnection(path, name, config);

    /// <summary>Replaces connection <paramref name="oldName"/> with <paramref name="newName"/> and <paramref name="config"/> (2026-10-05, the wizard's edit; <see cref="Sql.ConnectionFamily{TConfig, TNamed}.ReplaceConnection"/>).</summary>
    public static string? ReplaceConnection(string path, string oldName, string newName, OracleConnectionConfig config) => Family.ReplaceConnection(path, oldName, newName, config);

    /// <summary>Every <c>oracle.json</c> under <paramref name="home"/> read once, so a plain password is encrypted at startup (<see cref="ConnectionFamily{TConfig, TNamed}.EncryptAll"/>). The files read. Never throws.</summary>
    public static IReadOnlyList<string> EncryptAll(string home) => Family.EncryptAll(home);

    /// <summary>The profile's file over the home's (<paramref name="home"/> null = the profile's alone): a name in both is the profile's.</summary>
    public static OracleCatalog LoadCatalog(string profileDirectory, string? home)
    {
        var (connections, problems) = Family.LoadCatalog(profileDirectory, home);
        return new OracleCatalog(connections, problems);
    }

    /// <summary>Writes <see cref="EmptyText"/> to <paramref name="path"/> when no file is there (the folder made first); true when it wrote. Throws on an IO failure — the caller's notice.</summary>
    public static bool EnsureExists(string path) => ConnectionsFileEdit.EnsureExists(path, EmptyText);
}
