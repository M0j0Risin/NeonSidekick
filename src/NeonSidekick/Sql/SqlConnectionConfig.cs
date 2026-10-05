using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;

namespace NeonSidekick.Sql;

/// <summary>
/// One named connection of <c>sql.json</c> (2026-09-23, the user's ask: "tools for connecting to and querying
/// MSSQL server", after the Python <c>mcp-mssql-read</c> server — its <c>MSSQL_*</c> variables are these keys):
/// the server (<c>host</c>, <c>host,port</c> or <c>host\instance</c>), the database a call opens when it names
/// none, the login (<see cref="Auth"/> <c>sql</c> with <see cref="User"/>/<see cref="Password"/>, or
/// <c>windows</c> — integrated, as the app's own Windows identity), the TLS pair, and a free-text
/// <see cref="Description"/> the model sees in <c>sql_connections</c> and the rules. Later that day (the user's ask:
/// integrated auth "as another user", their PROD launcher's <c>runas /savecred</c>) <see cref="Auth"/> gained
/// <c>runas</c> — Windows sign-in as <see cref="User"/> (<c>DOMAIN\name</c>), the <c>runas /netonly</c> way, for this
/// connection alone (<see cref="WindowsCredentials.LogonNetOnly"/>) — and every password became a stored secret:
/// <see cref="PasswordStore"/> <c>file</c> keeps it DPAPI-encrypted in <see cref="Password"/> (a plain value typed
/// there is encrypted in place at the next read, <see cref="SqlConfigFile"/>), <c>credman</c> reads it from the
/// Windows Credential Manager entry <see cref="CredentialTarget"/> (plain text until then, the user's call of the
/// morning). A password is never shown, logged or sent to the model (<see cref="SqlText.ConnectionLine"/> leaves
/// it out). Entra sign-in is not offered: it would pull in Azure.Identity. Read through <see cref="SqlJsonContext"/> alone.
/// </summary>
public sealed class SqlConnectionConfig
{
    /// <summary>The <see cref="Auth"/> word for a SQL login (the default).</summary>
    public const string SqlAuth = "sql";

    /// <summary>The <see cref="Auth"/> word for integrated Windows sign-in.</summary>
    public const string WindowsAuth = "windows";

    /// <summary>The <see cref="Auth"/> word for Windows sign-in as another account, <c>runas /netonly</c>'s way (later on 2026-09-23).</summary>
    public const string RunAsAuth = "runas";

    /// <summary>The <see cref="PasswordStore"/> word for a DPAPI value in <see cref="Password"/> (the default).</summary>
    public const string FileStore = "file";

    /// <summary>The <see cref="PasswordStore"/> word for a Windows Credential Manager entry.</summary>
    public const string CredmanStore = "credman";

    /// <summary>What <see cref="CredentialTarget"/> is for a connection that names no <see cref="Credential"/>: <c>NeonSidekick/sql/&lt;name&gt;</c>.</summary>
    public const string CredentialPrefix = "NeonSidekick/sql/";

    /// <summary>The <see cref="Encrypt"/> words, the <see cref="SqlConnectionEncryptOption"/> names in lower case.</summary>
    public static readonly IReadOnlyList<string> EncryptWords = ["strict", "mandatory", "optional"];

    /// <summary>The connect timeout when the entry names none.</summary>
    public const int DefaultConnectTimeoutSeconds = 15;

    /// <summary>The <see cref="ConnectTimeoutSeconds"/> ceiling.</summary>
    public const int MaxConnectTimeoutSeconds = 120;

    /// <summary>The server: <c>host</c>, <c>host,port</c>, <c>host\instance</c> (SqlClient's <c>Data Source</c>).</summary>
    public string? Server { get; set; }

    /// <summary>The database a call opens when it names none; the login's default database when blank.</summary>
    public string? Database { get; set; }

    /// <summary><c>sql</c> (the default), <c>windows</c> or <c>runas</c>.</summary>
    public string? Auth { get; set; }

    /// <summary>The SQL login, for <c>sql</c>; the Windows account (<c>DOMAIN\name</c> or <c>name@domain</c>), for <c>runas</c>.</summary>
    public string? User { get; set; }

    /// <summary>The password under the <c>file</c> store: <c>dpapi:…</c>, or plain text the next read encrypts. Never shown.</summary>
    public string? Password { get; set; }

    /// <summary><c>file</c> (the default) or <c>credman</c>: where the password of a <c>sql</c> or <c>runas</c> connection is kept.</summary>
    public string? PasswordStore { get; set; }

    /// <summary>The Credential Manager entry under <c>credman</c>; <see cref="CredentialPrefix"/> and the connection's name when absent.</summary>
    public string? Credential { get; set; }

    /// <summary><c>strict</c>, <c>mandatory</c> (the default) or <c>optional</c>.</summary>
    public string? Encrypt { get; set; }

    /// <summary>Whether a certificate no authority vouches for is accepted — a dev container's self-signed one.</summary>
    public bool TrustServerCertificate { get; set; }

    /// <summary>Seconds a connect may take, 1 to <see cref="MaxConnectTimeoutSeconds"/>; <see cref="DefaultConnectTimeoutSeconds"/> when absent.</summary>
    public int? ConnectTimeoutSeconds { get; set; }

    /// <summary>What the database holds, in the user's words; the model reads it to pick a connection.</summary>
    /// <summary><c>read</c> (the default) or <c>readwrite</c>: whether <c>sql_execute</c> may change this connection's databases while
    /// <c>SQL mode</c> is <c>read-write</c> too (2026-10-05, the user's two-key call, <c>unc.json</c>'s shape; <see cref="Sql.ConnectionAccess"/>).</summary>
    public string? Access { get; set; }

    public string? Description { get; set; }

    /// <summary>Whether the entry signs in as the app's Windows identity.</summary>
    [JsonIgnore]
    public bool IsWindows => string.Equals(Auth?.Trim(), WindowsAuth, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the entry signs in as another Windows account (<c>runas</c>).</summary>
    [JsonIgnore]
    public bool IsRunAs => string.Equals(Auth?.Trim(), RunAsAuth, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the entry needs a password: a SQL login or a <c>runas</c> account.</summary>
    [JsonIgnore]
    public bool NeedsPassword => !IsWindows;

    /// <summary>Whether the password lives in Windows Credential Manager rather than in the file.</summary>
    [JsonIgnore]
    public bool InCredentialManager => string.Equals(PasswordStore?.Trim(), CredmanStore, StringComparison.OrdinalIgnoreCase);

    /// <summary>The Credential Manager entry this connection's password is read from under <c>credman</c>.</summary>
    public string CredentialTarget(string name) => string.IsNullOrWhiteSpace(Credential) ? CredentialPrefix + name : Credential.Trim();

    /// <summary>Whether the entry allows changes (<c>readwrite</c>); the tools still need the family's mode <c>read-write</c>.</summary>
    [JsonIgnore]
    public bool IsReadWrite => Sql.ConnectionAccess.IsReadWrite(Access);

    /// <summary>
    /// The reason this entry cannot connect, or null: no server, an <see cref="Auth"/> or <see cref="Encrypt"/>
    /// word outside the lists, a SQL login without a user, a connect timeout out of range. Pure.
    /// </summary>
    [JsonIgnore]
    public string? Problem
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Server))
            {
                return SqlText.NoServer;
            }

            string auth = Auth?.Trim() ?? "";
            if (auth.Length > 0 && !auth.Equals(SqlAuth, StringComparison.OrdinalIgnoreCase) && !auth.Equals(WindowsAuth, StringComparison.OrdinalIgnoreCase) && !auth.Equals(RunAsAuth, StringComparison.OrdinalIgnoreCase))
            {
                return SqlText.BadAuth(auth);
            }

            if (NeedsPassword && string.IsNullOrWhiteSpace(User))
            {
                return IsRunAs ? SqlText.RunAsNeedsDomain("") : SqlText.NoUser;
            }

            if (IsRunAs && WindowsCredentials.SplitAccount(User) is null)
            {
                return SqlText.RunAsNeedsDomain(User!.Trim());
            }

            string store = PasswordStore?.Trim() ?? "";
            if (store.Length > 0 && !store.Equals(FileStore, StringComparison.OrdinalIgnoreCase) && !store.Equals(CredmanStore, StringComparison.OrdinalIgnoreCase))
            {
                return SqlText.BadPasswordStore(store);
            }

            if (IsWindows && InCredentialManager)
            {
                return SqlText.CredmanWithWindows;
            }

            string encrypt = Encrypt?.Trim() ?? "";
            if (encrypt.Length > 0 && !EncryptWords.Contains(encrypt.ToLowerInvariant()))
            {
                return SqlText.BadEncrypt(encrypt);
            }

            if (Sql.ConnectionAccess.Problem(Access) is { } access)
            {
                return access;
            }

            if (ConnectTimeoutSeconds is { } seconds && (seconds < 1 || seconds > MaxConnectTimeoutSeconds))
            {
                return SqlText.BadConnectTimeout(seconds, MaxConnectTimeoutSeconds);
            }

            return null;
        }
    }

    /// <summary>
    /// The SqlClient connection string for this entry, <paramref name="database"/> (when not blank) in place of
    /// <see cref="Database"/>, <paramref name="password"/> the resolved one (<see cref="SqlSecrets.Resolve"/>) for a SQL
    /// login. Always read-only intent (a replica takes it; a primary ignores it), the app's name in
    /// <c>sys.dm_exec_sessions</c>, pooling on — except for <c>runas</c>: integrated security under the other account's
    /// token, with pooling <b>off</b> and its own application name, since SqlClient keys an integrated pool by the
    /// process's SID, which a <c>NEW_CREDENTIALS</c> token keeps, and a socket signed in as the other account must never
    /// be handed to a plain <c>windows</c> connection to the same server. Call only on an entry without a <see cref="Problem"/>.
    /// <paramref name="readOnlyIntent"/> false (2026-10-05, <c>sql_execute</c> on a <c>readwrite</c> connection) asks for the primary:
    /// read-write intent, a pool of its own.
    /// </summary>
    public SqlConnectionStringBuilder Builder(string? database = null, string? password = null, bool readOnlyIntent = true)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = Server!.Trim(),
            ApplicationIntent = readOnlyIntent ? ApplicationIntent.ReadOnly : ApplicationIntent.ReadWrite,
            ApplicationName = "NeonSidekick",
            ConnectTimeout = ConnectTimeoutSeconds ?? DefaultConnectTimeoutSeconds,
            TrustServerCertificate = TrustServerCertificate,
            Encrypt = (Encrypt?.Trim().ToLowerInvariant() ?? "") switch
            {
                "strict" => SqlConnectionEncryptOption.Strict,
                "optional" => SqlConnectionEncryptOption.Optional,
                _ => SqlConnectionEncryptOption.Mandatory,
            },
        };

        string catalog = !string.IsNullOrWhiteSpace(database) ? database.Trim() : Database?.Trim() ?? "";
        if (catalog.Length > 0)
        {
            builder.InitialCatalog = catalog;
        }

        if (IsWindows)
        {
            builder.IntegratedSecurity = true;
        }
        else if (IsRunAs)
        {
            builder.IntegratedSecurity = true;
            builder.Pooling = false;
            builder.ApplicationName = "NeonSidekick (runas)";
        }
        else
        {
            builder.UserID = User!.Trim();
            builder.Password = password ?? "";
        }

        return builder;
    }
}

/// <summary>A connection by its name, and the file it came from.</summary>
public sealed record SqlNamedConnection(string Name, SqlConnectionConfig Config, string Source);
