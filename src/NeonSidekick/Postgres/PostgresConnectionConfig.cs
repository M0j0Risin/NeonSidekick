using System.Globalization;
using System.Text.Json.Serialization;
using Npgsql;

namespace NeonSidekick.Postgres;

/// <summary>
/// One named connection of <c>postgres.json</c> (2026-10-04, the user's pick of the integration ideas), <c>MySqlConnectionConfig</c>'s
/// shape for PostgreSQL: the host and port, the database a call works in when it names none, the user and its password (kept as
/// <c>mysql.json</c>'s are: <see cref="PasswordStore"/> <c>file</c> DPAPI-encrypted in <see cref="Password"/>, or <c>credman</c> in Windows
/// Credential Manager), the TLS mode and the connect timeout. A password is never shown, logged or sent to the model. Read through
/// <see cref="PostgresJsonContext"/> alone.
/// </summary>
public sealed class PostgresConnectionConfig : Sql.ISignInConfig
{
    public const string FileStore = Sql.SqlConnectionConfig.FileStore;

    public const string CredmanStore = Sql.SqlConnectionConfig.CredmanStore;

    /// <summary>What <see cref="CredentialTarget"/> is for a connection that names no <see cref="Credential"/>: <c>NeonSidekick/postgres/&lt;name&gt;</c>.</summary>
    public const string CredentialPrefix = "NeonSidekick/postgres/";

    public const int DefaultPort = 5432;

    public const int DefaultConnectTimeoutSeconds = 15;

    public const int MaxConnectTimeoutSeconds = 120;

    /// <summary>The JSON keys of the lock, as its refusals name them.</summary>
    public const string SchemaKey = "schema";

    public const string SchemasKey = "schemas";

    /// <summary>What <see cref="Builder"/> sets Npgsql's <c>RequireAuth</c> to while the certificate is unchecked (E3, DB1). Pinned.</summary>
    public const string NoCleartextPassword = "!Password";

    /// <summary>The TLS word a new connection's wizard starts on: the certificate and host name checked (2026-10-24, decision DB1).</summary>
    public const string NewConnectionSslMode = "verify-full";

    /// <summary>The <see cref="SslMode"/> words, libpq's own spelling; <c>prefer</c> is the default of an entry that names none.</summary>
    public static readonly IReadOnlyList<string> SslModeWords = ["prefer", "require", "verify-ca", "verify-full", "disable"];

    public string? Host { get; set; }

    public int? Port { get; set; }

    /// <summary>
    /// The one database the connection reaches (required since 2026-10-09, the user's call, SQL Server's lock carried over; <c>postgres</c>
    /// was the default before): no tool takes another, and another database is another connection.
    /// </summary>
    public string? Database { get; set; }

    /// <summary>
    /// The schema the connection is held to, or none (2026-10-09, the user's pick: the schemas stay open unless one is named). Named, it
    /// is the session's whole <c>search_path</c> (<see cref="SearchPath"/>), the listings keep to it, a tool's <c>schema</c> naming another
    /// is refused, and so is a statement qualifying a name with another (<see cref="PostgresBoundary"/>).
    /// </summary>
    public string? Schema { get; set; }

    /// <summary>
    /// The schemas the connection is held to, in search-path order (2026-11-14, feature idea #18: <c>HR.EMPLOYEES</c> joined with
    /// <c>OE.ORDERS</c> needed two connections): <see cref="Schema"/>'s list form, never both. The session's <c>search_path</c> is the
    /// list, so an unqualified name resolves across it; the listings keep to the list, and a qualifier naming a schema off it is refused.
    /// </summary>
    public List<string>? Schemas { get; set; }

    public string? User { get; set; }

    /// <summary>The password under the <c>file</c> store: <c>dpapi:…</c>, or plain text the next read encrypts. Never shown.</summary>
    public string? Password { get; set; }

    public string? PasswordStore { get; set; }

    public string? Credential { get; set; }

    public string? SslMode { get; set; }

    public int? ConnectTimeoutSeconds { get; set; }

    /// <summary><c>read</c> (the default) or <c>readwrite</c>: whether <c>postgres_execute</c> may change this connection's databases while
    /// <c>PostgreSQL mode</c> is <c>read-write</c> too (2026-10-05, the user's two-key call, <c>unc.json</c>'s shape; <see cref="Sql.ConnectionAccess"/>).</summary>
    public string? Access { get; set; }

    public string? Description { get; set; }


    /// <summary>Keys the entry does not know (2026-10-24, the database review's K2): the load refuses the entry naming the first. Never written.</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }

    /// <summary>
    /// Whether the server's certificate and host name are checked (<c>verify-full</c>; 2026-10-24, the database review's E3, decision DB1): the
    /// other modes encrypt (or not) but accept any certificate, so a man in the middle could read the password. An absent key is
    /// <c>prefer</c>, unchecked, as before.
    /// </summary>
    [JsonIgnore]
    public bool TlsChecked => string.Equals(SslMode?.Trim(), "verify-full", StringComparison.OrdinalIgnoreCase);

    /// <summary>Where the password is sent (2026-10-24, decision DB9: a stored credential records it, and a read for another is refused).</summary>
    [JsonIgnore]
    public string SignInHost => Endpoint;

    [JsonIgnore]
    public bool InCredentialManager => string.Equals(PasswordStore?.Trim(), CredmanStore, StringComparison.OrdinalIgnoreCase);

    public string CredentialTarget(string name) => string.IsNullOrWhiteSpace(Credential) ? CredentialPrefix + name : Credential.Trim();

    [JsonIgnore]
    public string Endpoint => Host?.Trim() + ":" + (Port ?? DefaultPort).ToString(CultureInfo.InvariantCulture);

    /// <summary>Whether the entry allows changes (<c>readwrite</c>); the tools still need the family's mode <c>read-write</c>.</summary>
    [JsonIgnore]
    public bool IsReadWrite => Sql.ConnectionAccess.IsReadWrite(Access);

    /// <summary>
    /// The schemas the connection is held to, in order: <see cref="Schemas"/>, else <see cref="Schema"/>, else none (every schema). Each
    /// trimmed; read only on an entry without a <see cref="Problem"/>.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> Locks =>
        Schemas is { Count: > 0 } list ? list.Select(s => s?.Trim() ?? "").ToArray() : string.IsNullOrWhiteSpace(Schema) ? [] : [Schema.Trim()];

    /// <summary>The lock as messages, the listing and the audit write it (<c>sales</c>, <c>sales, ns_other</c>), or null for none.</summary>
    [JsonIgnore]
    public string? LockText => Locks is { Count: > 0 } locks ? string.Join(", ", locks) : null;

    /// <summary>The wizard's lock (#18), kept in the entry's form (#29, <see cref="Sql.SchemaLock.Hold"/>).</summary>
    public void HoldTo(IReadOnlyList<string> names) => (Schema, Schemas) = Sql.SchemaLock.Hold(names, asList: Schemas is not null);

    /// <summary>The reason this entry cannot connect, or null: no host, no database, no user, a schema it cannot set, a port, store, TLS mode or connect timeout out of range. Pure.</summary>
    [JsonIgnore]
    public string? Problem
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Host))
            {
                return PostgresText.NoHost;
            }

            if (string.IsNullOrWhiteSpace(Database))
            {
                return PostgresText.NoDatabase;
            }

            if (string.IsNullOrWhiteSpace(User))
            {
                return PostgresText.NoUser;
            }

            if (SchemasProblem() is { } schema)
            {
                return schema;
            }

            if (Port is { } port && (port < 1 || port > 65535))
            {
                return PostgresText.BadPort(port);
            }

            string store = PasswordStore?.Trim() ?? "";
            if (store.Length > 0 && !store.Equals(FileStore, StringComparison.OrdinalIgnoreCase) && !store.Equals(CredmanStore, StringComparison.OrdinalIgnoreCase))
            {
                return Sql.SqlText.BadPasswordStore(store);
            }

            string ssl = SslMode?.Trim() ?? "";
            if (ssl.Length > 0 && !SslModeWords.Contains(ssl.ToLowerInvariant()))
            {
                return PostgresText.BadSslMode(ssl);
            }

            if (Sql.ConnectionAccess.Problem(Access) is { } access)
            {
                return access;
            }

            if (ConnectTimeoutSeconds is { } seconds && (seconds < 1 || seconds > MaxConnectTimeoutSeconds))
            {
                return Sql.SqlText.BadConnectTimeout(seconds, MaxConnectTimeoutSeconds);
            }

            return null;
        }
    }

    /// <summary>
    /// The reason <paramref name="schema"/> cannot be on the session's search path, or null (none is fine): a space, a comma, a quote or a
    /// backslash, which the session's options would split. <paramref name="key"/> is the key it came from. One of PostgreSQL's own schemas
    /// passes here, as a single <c>schema</c> always did (2026-11-15, #160, the user's call: <c>"$user"</c> still loads); a <c>schemas</c>
    /// element and the wizard's pick refuse them (<see cref="ListedProblem"/>).
    /// </summary>
    public static string? SchemaProblem(string? schema, string key = SchemaKey)
    {
        if (string.IsNullOrWhiteSpace(schema))
        {
            return null;
        }

        string s = schema.Trim();
        return s.Any(c => char.IsWhiteSpace(c) || c is ',' or '"' or '\\' or '\'') ? PostgresText.BadSchemaKey(s, key) : null;
    }

    /// <summary><see cref="SchemaProblem"/>, then one of PostgreSQL's own schemas (2026-11-14, #18: <c>pg_*</c>, <c>information_schema</c>, a <c>$</c>-led name).</summary>
    private static string? ListedProblem(string schema, string key) =>
        SchemaProblem(schema, key) ?? (IsSystemSchema(schema.Trim()) ? PostgresText.SystemSchema(schema.Trim(), key) : null);

    /// <summary>
    /// The reason the wizard's picked <paramref name="names"/> cannot hold the connection, or null (none is no lock): one name is judged as a
    /// <c>schema</c>, several as a <c>schemas</c> list, and one of PostgreSQL's own schemas is refused in both (2026-11-15, #160).
    /// </summary>
    public static string? PickProblem(IReadOnlyList<string?> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return names.Count == 0 ? null : names.Count == 1 ? ListedProblem(names[0] ?? "", SchemaKey) : LockProblem(null, names);
    }

    /// <summary>Whether <paramref name="schema"/> is one of PostgreSQL's own: <c>pg_*</c>, <c>information_schema</c>, or <c>$</c>-led (<c>$user</c>).</summary>
    public static bool IsSystemSchema(string schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return schema.StartsWith("pg_", StringComparison.Ordinal) || schema == "information_schema" || schema.StartsWith('$');
    }

    /// <summary>
    /// The reason the lock's keys cannot hold the connection, or null: both keys, an empty list, more than <see cref="Sql.SchemaLock.MaxSchemas"/>, a
    /// blank or bad name (<see cref="SchemaProblem"/>), a system name in the list (<see cref="ListedProblem"/>), or one listed twice
    /// (exactly: <c>Sales</c> and <c>sales</c> are two).
    /// </summary>
    private string? SchemasProblem() => LockProblem(Schema, Schemas);

    /// <summary><see cref="SchemasProblem"/> for the two keys' values (the wizard's typed answer goes through it too).</summary>
    public static string? LockProblem(string? schema, IReadOnlyList<string?>? schemas)
    {
        if (schemas is not { } list)
        {
            return SchemaProblem(schema);
        }

        return Sql.SchemaLock.ListProblem(schema, list, LockWords, static name => string.IsNullOrWhiteSpace(name) ? PostgresText.BadSchemaKey(name?.Trim() ?? "", SchemasKey) : ListedProblem(name, SchemasKey), static name => name);
    }

    private static readonly Sql.SchemaLock.Words LockWords = new(PostgresText.BothSchemaKeys, PostgresText.EmptySchemas, PostgresText.TooManySchemas, PostgresText.RepeatedSchema);

    /// <summary>One name of the <c>search_path</c> a schema lock sets: a plain lower-case name as it is, any other in double quotes (its case kept).</summary>
    public static string SearchPath(string schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        string s = schema.Trim();
        return s.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '$') && !char.IsDigit(s[0]) ? s : "\"" + s + "\"";
    }

    /// <summary>
    /// The Npgsql connection string for this entry, in its own <see cref="Database"/> alone (the per-call one went 2026-10-09, with the
    /// lock) and, under a lock (<see cref="Locks"/>), its schemas as the whole <c>search_path</c>, in order, joined by a bare comma (the
    /// options split on spaces; <see cref="SchemaProblem"/> keeps both out of a name); <paramref name="password"/> the resolved one. The session is set at startup through <c>Options</c> — every
    /// transaction read-only by default, and <c>statement_timeout</c> and <c>lock_timeout</c> at <paramref name="timeoutSeconds"/> — so the
    /// server enforces both even for a statement the gate let through; a connection per call (<see cref="PostgresAccess"/> builds a data
    /// source per call, unpooled, disposed after). <c>standard_conforming_strings</c> is pinned on (the 2026-10-04 review, MySQL's
    /// <c>NO_BACKSLASH_ESCAPES</c> strip in reverse): the gate reads a backslash in a plain <c>'…'</c> string as a character, and a
    /// server, database or role with it off would read <c>\'</c> as an escaped quote and split the text where the gate did not.
    /// Call only on an entry without a <see cref="Problem"/>. <paramref name="readOnly"/> false (2026-10-05, <c>postgres_execute</c>
    /// on a <c>readwrite</c> connection) leaves <c>default_transaction_read_only</c> out and nothing else: the strings and the timeouts
    /// stay as the gate and the reads have them.
    /// </summary>
    public NpgsqlConnectionStringBuilder Builder(string? password, int timeoutSeconds, bool readOnly = true)
    {
        long millis = Math.Max(1, timeoutSeconds) * 1000L;
        return new NpgsqlConnectionStringBuilder
        {
            Host = Host!.Trim(),
            Port = Port ?? DefaultPort,
            Database = Database?.Trim() ?? "",
            Username = User!.Trim(),
            Password = password ?? "",
            Timeout = ConnectTimeoutSeconds ?? DefaultConnectTimeoutSeconds,
            CommandTimeout = Math.Max(1, timeoutSeconds) + 5,
            ApplicationName = "NeonSidekick",
            Pooling = false,
            Options = (readOnly ? "-c default_transaction_read_only=on " : "") + "-c standard_conforming_strings=on -c statement_timeout=" + millis.ToString(CultureInfo.InvariantCulture)
                + " -c lock_timeout=" + millis.ToString(CultureInfo.InvariantCulture) + " -c idle_in_transaction_session_timeout=" + (millis * 2).ToString(CultureInfo.InvariantCulture)
                + (Locks is { Count: > 0 } locks ? " -c search_path=" + string.Join(",", locks.Select(SearchPath)) : ""),
            SslMode = (SslMode?.Trim().ToLowerInvariant() ?? "") switch
            {
                "require" => Npgsql.SslMode.Require,
                "verify-ca" => Npgsql.SslMode.VerifyCA,
                "verify-full" => Npgsql.SslMode.VerifyFull,
                "disable" => Npgsql.SslMode.Disable,
                _ => Npgsql.SslMode.Prefer,
            },

            // A server's request for the password in clear refused while the certificate is unchecked (2026-10-24, the database review's E3,
            // decision DB1: Npgsql 10's RequireAuth, libpq's require_auth): a man in the middle could ask for it and read it. SCRAM and MD5
            // still sign in; under verify-full the link itself is the server's, and cleartext is let through as before.
            RequireAuth = TlsChecked ? null : NoCleartextPassword,
        };
    }
}

/// <summary>A connection by its name, and the file it came from.</summary>
public sealed record PostgresNamedConnection(string Name, PostgresConnectionConfig Config, string Source) : Sql.INamedConnection<PostgresConnectionConfig>;
