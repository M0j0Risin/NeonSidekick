using System.Text.Json.Serialization;
using Oracle.ManagedDataAccess.Client;

namespace NeonSidekick.Oracle;

/// <summary>
/// One named connection of <c>oracle.json</c> (2026-09-30, the user's ask: "mirror what we did for SQL server, but for
/// Oracle … just normal authentication"): the <see cref="Sql.SqlConnectionConfig"/> shape less its Windows sign-ins and TLS
/// pair — the data source (EZConnect <c>host:port/service</c>, or a whole <c>(DESCRIPTION=…)</c>), the schema a call
/// works in when it names none, the database user and its password, kept as a SQL login's is: <see cref="PasswordStore"/>
/// <c>file</c> DPAPI-encrypted in <see cref="Password"/> (a plain value typed there is encrypted in place at the next
/// read, <see cref="OracleConfigFile"/>), or <c>credman</c> in the Windows Credential Manager entry
/// <see cref="CredentialTarget"/>. A password is never shown, logged or sent to the model. <c>SYS</c> is refused
/// (<see cref="Problem"/>): Oracle does not hold SYS to a read-only transaction, the tools' third guard; the connect
/// never asks for a <c>DBA Privilege</c> either. Read through <see cref="OracleJsonContext"/> alone.
/// </summary>
public sealed class OracleConnectionConfig : Sql.ISignInConfig
{
    /// <summary>The <see cref="PasswordStore"/> word for a DPAPI value in <see cref="Password"/> (the default).</summary>
    public const string FileStore = Sql.SqlConnectionConfig.FileStore;

    /// <summary>The <see cref="PasswordStore"/> word for a Windows Credential Manager entry.</summary>
    public const string CredmanStore = Sql.SqlConnectionConfig.CredmanStore;

    /// <summary>What <see cref="CredentialTarget"/> is for a connection that names no <see cref="Credential"/>: <c>NeonSidekick/oracle/&lt;name&gt;</c>.</summary>
    public const string CredentialPrefix = "NeonSidekick/oracle/";

    /// <summary>The connect timeout when the entry names none.</summary>
    public const int DefaultConnectTimeoutSeconds = 15;

    /// <summary>The <see cref="ConnectTimeoutSeconds"/> ceiling.</summary>
    public const int MaxConnectTimeoutSeconds = 120;

    /// <summary>The account read-only transactions do not hold (Oracle's own rule); refused as a <see cref="User"/>.</summary>
    public const string SysUser = "SYS";

    /// <summary>The data source: EZConnect <c>host:port/service</c> (<c>//host/service</c>, the port 1521 when left out), or a <c>(DESCRIPTION=…)</c>.</summary>
    public string? DataSource { get; set; }

    /// <summary>
    /// The schema the connection is held to (<c>ALTER SESSION SET CURRENT_SCHEMA</c>); the user's own when blank. Since 2026-10-09 (the
    /// user's pick) no call names another and <see cref="OracleBoundary"/> refuses a statement that reaches one.
    /// </summary>
    public string? Schema { get; set; }

    /// <summary>
    /// The schemas the connection is held to (2026-11-14, feature idea #18: <c>HR.EMPLOYEES</c> joined with <c>OE.ORDERS</c> needed two
    /// connections): <see cref="Schema"/>'s list form, never both. The first is the session's <c>CURRENT_SCHEMA</c>; the others' tables
    /// are named <c>OE.ORDERS</c>. The listings keep to the list, and a statement reaching a schema off it is refused.
    /// </summary>
    public List<string>? Schemas { get; set; }

    /// <summary>The most schemas one entry's <see cref="Schemas"/> may list.</summary>
    public const int MaxSchemas = 16;

    /// <summary>
    /// Oracle's own schemas, refused in <see cref="Schema"/> and <see cref="Schemas"/> as written (#18); the session's check reads
    /// <c>ALL_USERS.ORACLE_MAINTAINED</c> for the rest (<see cref="OracleText.SchemaCheck"/>).
    /// </summary>
    public static readonly IReadOnlySet<string> SystemSchemas = new HashSet<string>(StringComparer.Ordinal)
    {
        "SYS", "SYSTEM", "XDB", "MDSYS", "CTXSYS", "OUTLN", "DBSNMP", "AUDSYS", "LBACSYS", "GSMADMIN_INTERNAL", "ORDSYS", "ORDDATA", "ORDPLUGINS",
        "WMSYS", "OJVMSYS", "DVSYS", "DVF", "OLAPSYS", "APPQOSSYS", "SI_INFORMTN_SCHEMA", "GSMCATUSER", "GSMUSER", "GSMROOTUSER", "SYSBACKUP",
        "SYSDG", "SYSKM", "SYSRAC", "REMOTE_SCHEDULER_AGENT", "DBSFWUSER", "GGSYS", "ANONYMOUS", "XS$NULL", "DIP", "ORACLE_OCM", "SYS$UMF",
    };

    /// <summary>The database user.</summary>
    public string? User { get; set; }

    /// <summary>The password under the <c>file</c> store: <c>dpapi:…</c>, or plain text the next read encrypts. Never shown.</summary>
    public string? Password { get; set; }

    /// <summary><c>file</c> (the default) or <c>credman</c>: where the password is kept.</summary>
    public string? PasswordStore { get; set; }

    /// <summary>The Credential Manager entry under <c>credman</c>; <see cref="CredentialPrefix"/> and the connection's name when absent.</summary>
    public string? Credential { get; set; }

    /// <summary>Seconds a connect may take, 1 to <see cref="MaxConnectTimeoutSeconds"/>; <see cref="DefaultConnectTimeoutSeconds"/> when absent.</summary>
    public int? ConnectTimeoutSeconds { get; set; }

    /// <summary>What the database holds, in the user's words; the model reads it to pick a connection.</summary>
    /// <summary><c>read</c> (the default) or <c>readwrite</c>: whether <c>oracle_execute</c> may change this connection's databases while
    /// <c>Oracle mode</c> is <c>read-write</c> too (2026-10-05, the user's two-key call, <c>unc.json</c>'s shape; <see cref="Sql.ConnectionAccess"/>).</summary>
    public string? Access { get; set; }

    public string? Description { get; set; }


    /// <summary>Keys the entry does not know (2026-10-24, the database review's K2): the load refuses the entry naming the first. Never written.</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }

    /// <summary>Where the password is sent (2026-10-24, decision DB9: a stored credential records it, and a read for another is refused).</summary>
    [JsonIgnore]
    public string SignInHost => DataSource?.Trim() ?? "";

    /// <summary>Whether the password lives in Windows Credential Manager rather than in the file.</summary>
    [JsonIgnore]
    public bool InCredentialManager => string.Equals(PasswordStore?.Trim(), CredmanStore, StringComparison.OrdinalIgnoreCase);

    /// <summary>The Credential Manager entry this connection's password is read from under <c>credman</c>.</summary>
    public string CredentialTarget(string name) => string.IsNullOrWhiteSpace(Credential) ? CredentialPrefix + name : Credential.Trim();

    /// <summary>Whether the entry allows changes (<c>readwrite</c>); the tools still need the family's mode <c>read-write</c>.</summary>
    [JsonIgnore]
    public bool IsReadWrite => Sql.ConnectionAccess.IsReadWrite(Access);

    /// <summary>
    /// The reason this entry cannot connect, or null: no data source, no user, <c>SYS</c> (or a user that asks for a
    /// <c>… AS SYSDBA</c>-style privilege), a store word outside the list, a connect timeout out of range, a
    /// <see cref="Schema"/> that is no identifier. Pure.
    /// </summary>
    [JsonIgnore]
    public string? Problem
    {
        get
        {
            if (string.IsNullOrWhiteSpace(DataSource))
            {
                return OracleText.NoDataSource;
            }

            if (string.IsNullOrWhiteSpace(User))
            {
                return OracleText.NoUser;
            }

            string user = User.Trim();
            if (user.Trim('"').Equals(SysUser, StringComparison.OrdinalIgnoreCase) || user.Contains(" as ", StringComparison.OrdinalIgnoreCase))
            {
                return OracleText.SysRefused(user);
            }

            string store = PasswordStore?.Trim() ?? "";
            if (store.Length > 0 && !store.Equals(FileStore, StringComparison.OrdinalIgnoreCase) && !store.Equals(CredmanStore, StringComparison.OrdinalIgnoreCase))
            {
                return Sql.SqlText.BadPasswordStore(store);
            }

            if (Sql.ConnectionAccess.Problem(Access) is { } access)
            {
                return access;
            }

            if (ConnectTimeoutSeconds is { } seconds && (seconds < 1 || seconds > MaxConnectTimeoutSeconds))
            {
                return Sql.SqlText.BadConnectTimeout(seconds, MaxConnectTimeoutSeconds);
            }

            return LockProblem(Schema, Schemas);
        }
    }

    /// <summary>
    /// The reason the lock's keys cannot hold the connection, or null (#18): both keys, an empty list, more than <see cref="MaxSchemas"/>,
    /// a name that is no identifier, one of <see cref="SystemSchemas"/>, or a name listed twice (as the dictionary spells it: <c>hr</c>
    /// and <c>HR</c> are one). The wizard's typed answer goes through it too.
    /// </summary>
    public static string? LockProblem(string? schema, IReadOnlyList<string?>? schemas)
    {
        if (schemas is null)
        {
            return string.IsNullOrWhiteSpace(schema) ? null : NameProblem(schema.Trim(), "schema");
        }

        if (!string.IsNullOrWhiteSpace(schema))
        {
            return OracleText.BothSchemaKeys;
        }

        if (schemas.Count == 0)
        {
            return OracleText.EmptySchemas;
        }

        if (schemas.Count > MaxSchemas)
        {
            return OracleText.TooManySchemas(MaxSchemas);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string? name in schemas)
        {
            if (NameProblem(name?.Trim() ?? "", "schemas") is { } problem)
            {
                return problem;
            }

            if (!seen.Add(OracleIdentifier.Normalize(name)!))
            {
                return OracleText.RepeatedSchema(name!.Trim());
            }
        }

        return null;
    }

    private static string? NameProblem(string name, string key) =>
        OracleIdentifier.Quote(name) is null ? OracleText.BadSchemaKey(name, key)
        : SystemSchemas.Contains(OracleIdentifier.Normalize(name)!) ? OracleText.SystemSchema(name, key)
        : null;

    /// <summary>The wizard's lock (#18): none clears both keys, one is <see cref="Schema"/>, several <see cref="Schemas"/> (a one-schema file stays as it was).</summary>
    public void HoldTo(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        Schema = names.Count == 1 ? names[0] : null;
        Schemas = names.Count > 1 ? [.. names] : null;
    }

    /// <summary>
    /// The ODP.NET connection string for this entry, <paramref name="password"/> the resolved one
    /// (<see cref="OracleSecrets.Resolve"/>). Pooling on (a pooled session keeps its <c>READ_ONLY</c>, and each call sets
    /// its schema afresh); no <c>DBA Privilege</c>, ever. Call only on an entry without a <see cref="Problem"/>. <paramref name="pooling"/>
    /// false (2026-10-05, <c>oracle_execute</c>): a write's session is its own, never a pooled one a read left <c>READ_ONLY</c>.
    /// </summary>
    public OracleConnectionStringBuilder Builder(string? password = null, bool pooling = true) => new()
    {
        DataSource = DataSource!.Trim(),
        UserID = User!.Trim(),
        Password = password ?? "",
        ConnectionTimeout = ConnectTimeoutSeconds ?? DefaultConnectTimeoutSeconds,
        Pooling = pooling,
    };
}

/// <summary>A connection by its name, and the file it came from.</summary>
public sealed record OracleNamedConnection(string Name, OracleConnectionConfig Config, string Source) : Sql.INamedConnection<OracleConnectionConfig>;
