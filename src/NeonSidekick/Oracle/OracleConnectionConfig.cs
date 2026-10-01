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
public sealed class OracleConnectionConfig
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

    /// <summary>The schema a call works in when it names none (<c>ALTER SESSION SET CURRENT_SCHEMA</c>); the user's own when blank.</summary>
    public string? Schema { get; set; }

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
    public string? Description { get; set; }

    /// <summary>Whether the password lives in Windows Credential Manager rather than in the file.</summary>
    [JsonIgnore]
    public bool InCredentialManager => string.Equals(PasswordStore?.Trim(), CredmanStore, StringComparison.OrdinalIgnoreCase);

    /// <summary>The Credential Manager entry this connection's password is read from under <c>credman</c>.</summary>
    public string CredentialTarget(string name) => string.IsNullOrWhiteSpace(Credential) ? CredentialPrefix + name : Credential.Trim();

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

            if (ConnectTimeoutSeconds is { } seconds && (seconds < 1 || seconds > MaxConnectTimeoutSeconds))
            {
                return Sql.SqlText.BadConnectTimeout(seconds, MaxConnectTimeoutSeconds);
            }

            if (!string.IsNullOrWhiteSpace(Schema) && OracleIdentifier.Quote(Schema) is null)
            {
                return OracleText.BadSchemaKey(Schema.Trim());
            }

            return null;
        }
    }

    /// <summary>
    /// The ODP.NET connection string for this entry, <paramref name="password"/> the resolved one
    /// (<see cref="OracleSecrets.Resolve"/>). Pooling on (a pooled session keeps its <c>READ_ONLY</c>, and each call sets
    /// its schema afresh); no <c>DBA Privilege</c>, ever. Call only on an entry without a <see cref="Problem"/>.
    /// </summary>
    public OracleConnectionStringBuilder Builder(string? password = null) => new()
    {
        DataSource = DataSource!.Trim(),
        UserID = User!.Trim(),
        Password = password ?? "",
        ConnectionTimeout = ConnectTimeoutSeconds ?? DefaultConnectTimeoutSeconds,
        Pooling = true,
    };
}

/// <summary>A connection by its name, and the file it came from.</summary>
public sealed record OracleNamedConnection(string Name, OracleConnectionConfig Config, string Source);
