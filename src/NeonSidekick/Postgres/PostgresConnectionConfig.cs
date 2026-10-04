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

    /// <summary>The database a connection without one works in: every server has it.</summary>
    public const string DefaultDatabase = "postgres";

    public const int DefaultConnectTimeoutSeconds = 15;

    public const int MaxConnectTimeoutSeconds = 120;

    /// <summary>The <see cref="SslMode"/> words, libpq's own spelling; <c>prefer</c> is the default.</summary>
    public static readonly IReadOnlyList<string> SslModeWords = ["prefer", "require", "verify-ca", "verify-full", "disable"];

    public string? Host { get; set; }

    public int? Port { get; set; }

    /// <summary>The database a call works in when it names none; <see cref="DefaultDatabase"/> when absent.</summary>
    public string? Database { get; set; }

    public string? User { get; set; }

    /// <summary>The password under the <c>file</c> store: <c>dpapi:…</c>, or plain text the next read encrypts. Never shown.</summary>
    public string? Password { get; set; }

    public string? PasswordStore { get; set; }

    public string? Credential { get; set; }

    public string? SslMode { get; set; }

    public int? ConnectTimeoutSeconds { get; set; }

    public string? Description { get; set; }

    [JsonIgnore]
    public bool InCredentialManager => string.Equals(PasswordStore?.Trim(), CredmanStore, StringComparison.OrdinalIgnoreCase);

    public string CredentialTarget(string name) => string.IsNullOrWhiteSpace(Credential) ? CredentialPrefix + name : Credential.Trim();

    [JsonIgnore]
    public string Endpoint => Host?.Trim() + ":" + (Port ?? DefaultPort).ToString(CultureInfo.InvariantCulture);

    /// <summary>The reason this entry cannot connect, or null: no host, no user, a port, store, TLS mode or connect timeout out of range. Pure.</summary>
    [JsonIgnore]
    public string? Problem
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Host))
            {
                return PostgresText.NoHost;
            }

            if (string.IsNullOrWhiteSpace(User))
            {
                return PostgresText.NoUser;
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

            if (ConnectTimeoutSeconds is { } seconds && (seconds < 1 || seconds > MaxConnectTimeoutSeconds))
            {
                return Sql.SqlText.BadConnectTimeout(seconds, MaxConnectTimeoutSeconds);
            }

            return null;
        }
    }

    /// <summary>
    /// The Npgsql connection string for this entry in <paramref name="database"/> (the entry's own, else <see cref="DefaultDatabase"/>,
    /// when blank), <paramref name="password"/> the resolved one. The session is set at startup through <c>Options</c> — every
    /// transaction read-only by default, and <c>statement_timeout</c> and <c>lock_timeout</c> at <paramref name="timeoutSeconds"/> — so the
    /// server enforces both even for a statement the gate let through; a connection per call (<see cref="PostgresAccess"/> builds a data
    /// source per call, unpooled, disposed after). <c>standard_conforming_strings</c> is pinned on (the 2026-10-04 review, MySQL's
    /// <c>NO_BACKSLASH_ESCAPES</c> strip in reverse): the gate reads a backslash in a plain <c>'…'</c> string as a character, and a
    /// server, database or role with it off would read <c>\'</c> as an escaped quote and split the text where the gate did not.
    /// Call only on an entry without a <see cref="Problem"/>.
    /// </summary>
    public NpgsqlConnectionStringBuilder Builder(string? password, string? database, int timeoutSeconds)
    {
        long millis = Math.Max(1, timeoutSeconds) * 1000L;
        return new NpgsqlConnectionStringBuilder
        {
            Host = Host!.Trim(),
            Port = Port ?? DefaultPort,
            Database = !string.IsNullOrWhiteSpace(database) ? database.Trim() : !string.IsNullOrWhiteSpace(Database) ? Database.Trim() : DefaultDatabase,
            Username = User!.Trim(),
            Password = password ?? "",
            Timeout = ConnectTimeoutSeconds ?? DefaultConnectTimeoutSeconds,
            CommandTimeout = Math.Max(1, timeoutSeconds) + 5,
            ApplicationName = "NeonSidekick",
            Pooling = false,
            Options = "-c default_transaction_read_only=on -c standard_conforming_strings=on -c statement_timeout=" + millis.ToString(CultureInfo.InvariantCulture)
                + " -c lock_timeout=" + millis.ToString(CultureInfo.InvariantCulture) + " -c idle_in_transaction_session_timeout=" + (millis * 2).ToString(CultureInfo.InvariantCulture),
            SslMode = (SslMode?.Trim().ToLowerInvariant() ?? "") switch
            {
                "require" => Npgsql.SslMode.Require,
                "verify-ca" => Npgsql.SslMode.VerifyCA,
                "verify-full" => Npgsql.SslMode.VerifyFull,
                "disable" => Npgsql.SslMode.Disable,
                _ => Npgsql.SslMode.Prefer,
            },
        };
    }
}

/// <summary>A connection by its name, and the file it came from.</summary>
public sealed record PostgresNamedConnection(string Name, PostgresConnectionConfig Config, string Source) : Sql.INamedConnection<PostgresConnectionConfig>;
