using System.Text.Json.Serialization;
using MySqlConnector;

namespace NeonSidekick.MySql;

/// <summary>
/// One named connection of <c>mysql.json</c> (2026-09-30, the user's ask: MySQL "very similar to Oracle without all the login
/// complexities of the SQL implementation"), the <see cref="Oracle.OracleConnectionConfig"/> shape for MySQL and MariaDB: the
/// host and port, the database a call works in when it names none, the user and its password (kept as <c>oracle.json</c>'s
/// are: <see cref="PasswordStore"/> <c>file</c> DPAPI-encrypted in <see cref="Password"/>, or <c>credman</c> in Windows Credential
/// Manager), the TLS mode, and — only when asked — the RSA public-key fetch a <c>caching_sha2_password</c> account needs over a
/// connection without TLS. Never a client-side <c>LOAD DATA LOCAL</c>, never user variables (<see cref="Builder"/>). A password is
/// never shown, logged or sent to the model. Read through <see cref="MySqlJsonContext"/> alone.
/// </summary>
public sealed class MySqlConnectionConfig : Sql.ISignInConfig
{
    /// <summary>The <see cref="PasswordStore"/> word for a DPAPI value in <see cref="Password"/> (the default).</summary>
    public const string FileStore = Sql.SqlConnectionConfig.FileStore;

    /// <summary>The <see cref="PasswordStore"/> word for a Windows Credential Manager entry.</summary>
    public const string CredmanStore = Sql.SqlConnectionConfig.CredmanStore;

    /// <summary>What <see cref="CredentialTarget"/> is for a connection that names no <see cref="Credential"/>: <c>NeonSidekick/mysql/&lt;name&gt;</c>.</summary>
    public const string CredentialPrefix = "NeonSidekick/mysql/";

    /// <summary>The port when the entry names none.</summary>
    public const int DefaultPort = 3306;

    /// <summary>The connect timeout when the entry names none.</summary>
    public const int DefaultConnectTimeoutSeconds = 15;

    /// <summary>The <see cref="ConnectTimeoutSeconds"/> ceiling.</summary>
    public const int MaxConnectTimeoutSeconds = 120;

    /// <summary>The <see cref="SslMode"/> words, MySqlConnector's <see cref="MySqlSslMode"/> in the server's own spelling; <c>preferred</c> is the default.</summary>
    public static readonly IReadOnlyList<string> SslModeWords = ["preferred", "required", "verify-ca", "verify-full", "none"];

    /// <summary>The host name or address.</summary>
    public string? Host { get; set; }

    /// <summary>The TCP port, 1 to 65535; <see cref="DefaultPort"/> when absent.</summary>
    public int? Port { get; set; }

    /// <summary>The database a call works in when it names none; the listings then cover every database the user can see.</summary>
    public string? Database { get; set; }

    /// <summary>The database user.</summary>
    public string? User { get; set; }

    /// <summary>The password under the <c>file</c> store: <c>dpapi:…</c>, or plain text the next read encrypts. Never shown.</summary>
    public string? Password { get; set; }

    /// <summary><c>file</c> (the default) or <c>credman</c>: where the password is kept.</summary>
    public string? PasswordStore { get; set; }

    /// <summary>The Credential Manager entry under <c>credman</c>; <see cref="CredentialPrefix"/> and the connection's name when absent.</summary>
    public string? Credential { get; set; }

    /// <summary>One of <see cref="SslModeWords"/>; <c>preferred</c> when absent.</summary>
    public string? SslMode { get; set; }

    /// <summary>Whether the client may ask the server for its RSA key (a <c>caching_sha2_password</c> account over a connection without TLS). Off: a man in the middle could hand over its own.</summary>
    public bool AllowPublicKeyRetrieval { get; set; }

    /// <summary>Seconds a connect may take, 1 to <see cref="MaxConnectTimeoutSeconds"/>; <see cref="DefaultConnectTimeoutSeconds"/> when absent.</summary>
    public int? ConnectTimeoutSeconds { get; set; }

    /// <summary>What the database holds, in the user's words; the model reads it to pick a connection.</summary>
    public string? Description { get; set; }

    /// <summary>Whether the password lives in Windows Credential Manager rather than in the file.</summary>
    [JsonIgnore]
    public bool InCredentialManager => string.Equals(PasswordStore?.Trim(), CredmanStore, StringComparison.OrdinalIgnoreCase);

    /// <summary>The Credential Manager entry this connection's password is read from under <c>credman</c>.</summary>
    public string CredentialTarget(string name) => string.IsNullOrWhiteSpace(Credential) ? CredentialPrefix + name : Credential.Trim();

    /// <summary>The host and port as one place: <c>host:port</c>.</summary>
    [JsonIgnore]
    public string Endpoint => Host?.Trim() + ":" + (Port ?? DefaultPort).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The reason this entry cannot connect, or null: no host, no user, a port, store, TLS mode or connect timeout out of range. Pure.</summary>
    [JsonIgnore]
    public string? Problem
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Host))
            {
                return MySqlText.NoHost;
            }

            if (string.IsNullOrWhiteSpace(User))
            {
                return MySqlText.NoUser;
            }

            if (Port is { } port && (port < 1 || port > 65535))
            {
                return MySqlText.BadPort(port);
            }

            string store = PasswordStore?.Trim() ?? "";
            if (store.Length > 0 && !store.Equals(FileStore, StringComparison.OrdinalIgnoreCase) && !store.Equals(CredmanStore, StringComparison.OrdinalIgnoreCase))
            {
                return Sql.SqlText.BadPasswordStore(store);
            }

            string ssl = SslMode?.Trim() ?? "";
            if (ssl.Length > 0 && !SslModeWords.Contains(ssl.ToLowerInvariant()))
            {
                return MySqlText.BadSslMode(ssl);
            }

            if (ConnectTimeoutSeconds is { } seconds && (seconds < 1 || seconds > MaxConnectTimeoutSeconds))
            {
                return Sql.SqlText.BadConnectTimeout(seconds, MaxConnectTimeoutSeconds);
            }

            return null;
        }
    }

    /// <summary>
    /// The MySqlConnector connection string for this entry, <paramref name="password"/> the resolved one. Pooling on (a pooled
    /// session is reset on its way back, its database and variables with it); the hardening always on: no <c>LOAD DATA LOCAL</c>,
    /// no user variables (an <c>@name</c> the call did not bind is an error, not a session variable), zero dates read as
    /// <see cref="DateTime.MinValue"/> rather than throwing. Call only on an entry without a <see cref="Problem"/>.
    /// </summary>
    public MySqlConnectionStringBuilder Builder(string? password = null)
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = Host!.Trim(),
            Port = (uint)(Port ?? DefaultPort),
            UserID = User!.Trim(),
            Password = password ?? "",
            ConnectionTimeout = (uint)(ConnectTimeoutSeconds ?? DefaultConnectTimeoutSeconds),
            Pooling = true,
            ApplicationName = "NeonSidekick",
            AllowLoadLocalInfile = false,
            AllowUserVariables = false,
            AllowPublicKeyRetrieval = AllowPublicKeyRetrieval,
            ConvertZeroDateTime = true,
            SslMode = (SslMode?.Trim().ToLowerInvariant() ?? "") switch
            {
                "required" => MySqlSslMode.Required,
                "verify-ca" => MySqlSslMode.VerifyCA,
                "verify-full" => MySqlSslMode.VerifyFull,
                "none" => MySqlSslMode.None,
                _ => MySqlSslMode.Preferred,
            },
        };
        if (!string.IsNullOrWhiteSpace(Database))
        {
            builder.Database = Database.Trim();
        }

        return builder;
    }
}

/// <summary>A connection by its name, and the file it came from.</summary>
public sealed record MySqlNamedConnection(string Name, MySqlConnectionConfig Config, string Source) : Sql.INamedConnection<MySqlConnectionConfig>;
