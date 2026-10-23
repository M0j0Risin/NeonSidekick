using System.Text.Json.Serialization;
using NeonSidekick.Sql;

namespace NeonSidekick.EventLog;

/// <summary>
/// One computer of <c>eventlog.json</c> (2026-10-15, the event-log plan, <c>Unc/UncShareConfig</c>'s shape): the host whose logs are read
/// over RPC, how (<c>windows</c>: as the user running the app; <c>runas</c>: as another account, its password kept as <c>unc.json</c>'s
/// are — <c>file</c> DPAPI-encrypted in <see cref="Password"/>, or <c>credman</c> — and passed straight into <c>EvtRpcLogin</c>, no
/// impersonation token), the auth method, what the model may read there (<see cref="Logs"/>, an allow-list) and in what detail
/// (<see cref="Detail"/>: <c>full</c> or <c>redacted</c>, the default). The name <c>local</c> is this PC, built in: its entry may carry
/// only <see cref="Logs"/>, <see cref="Detail"/> and <see cref="Description"/>. A password is never shown, logged or sent to the model.
/// Read through <see cref="EventLogJsonContext"/> alone.
/// </summary>
public sealed class EventLogComputerConfig
{
    public const string WindowsAuth = "windows";
    public const string RunAsAuth = "runas";
    public const string FullDetail = "full";
    public const string RedactedDetail = "redacted";
    public const string FileStore = SqlConnectionConfig.FileStore;
    public const string CredmanStore = SqlConnectionConfig.CredmanStore;

    /// <summary>The auth methods <c>EvtRpcLogin</c> takes, <c>default</c> first. Pinned.</summary>
    public static readonly IReadOnlyList<string> AuthMethods = ["default", "negotiate", "kerberos", "ntlm"];

    /// <summary>What <see cref="CredentialTarget"/> is for an entry that names no <see cref="Credential"/>: <c>NeonSidekick/eventlog/&lt;name&gt;</c>.</summary>
    public const string CredentialPrefix = "NeonSidekick/eventlog/";

    /// <summary>The computer's name or address (<c>fs01.corp.example</c>, <c>192.168.1.40</c>; <c>localhost</c> is a loopback session).</summary>
    public string? Host { get; set; }

    /// <summary><c>windows</c> (the default) or <c>runas</c>.</summary>
    public string? Auth { get; set; }

    /// <summary>The account under <c>runas</c>: <c>DOMAIN\name</c> or <c>name@domain</c> (<c>lab-box\admin</c> names the machine's own).</summary>
    public string? User { get; set; }

    /// <summary>The password under <c>runas</c> and the <c>file</c> store: <c>dpapi:…</c>, or plain text the next read encrypts. Never shown.</summary>
    public string? Password { get; set; }

    /// <summary><c>file</c> (the default) or <c>credman</c>.</summary>
    public string? PasswordStore { get; set; }

    /// <summary>The Credential Manager entry under <c>credman</c>; <see cref="CredentialPrefix"/> and the name when absent.</summary>
    public string? Credential { get; set; }

    /// <summary><c>default</c>, <c>negotiate</c>, <c>kerberos</c> or <c>ntlm</c>.</summary>
    public string? AuthMethod { get; set; }

    /// <summary>What the computer is, in the user's words; the model reads it to pick one.</summary>
    public string? Description { get; set; }

    /// <summary>The logs the model and <c>/eventlog</c> may read there (case ignored, <c>*</c> any run but <c>/</c>); absent, all.</summary>
    public List<string>? Logs { get; set; }

    /// <summary><c>full</c> or <c>redacted</c> (the default): what the main chat's model reads of this computer's events.</summary>
    public string? Detail { get; set; }

    /// <summary>Keys the file does not know (2026-10-23, the admin review's H5, DA1: <c>log</c> for <c>logs</c> read as "every log"); the loader refuses the entry.</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }

    [JsonIgnore]
    public bool IsRunAs => string.Equals(Auth?.Trim(), RunAsAuth, StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool NeedsPassword => IsRunAs;

    [JsonIgnore]
    public bool InCredentialManager => string.Equals(PasswordStore?.Trim(), CredmanStore, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the main chat reads this computer's events in full (<c>"detail": "full"</c>).</summary>
    [JsonIgnore]
    public bool Full => string.Equals(Detail?.Trim(), FullDetail, StringComparison.OrdinalIgnoreCase);

    /// <summary>The detail's word: <c>full</c> or <c>redacted</c>.</summary>
    [JsonIgnore]
    public string DetailWord => Full ? FullDetail : RedactedDetail;

    /// <summary>The auth method's word, <c>default</c> when unset.</summary>
    [JsonIgnore]
    public string AuthMethodWord => string.IsNullOrWhiteSpace(AuthMethod) ? AuthMethods[0] : AuthMethod.Trim().ToLowerInvariant();

    /// <summary>The Credential Manager entry this computer's password is read from under <c>credman</c>.</summary>
    public string CredentialTarget(string name) => string.IsNullOrWhiteSpace(Credential) ? CredentialPrefix + name : Credential.Trim();

    /// <summary>
    /// The reason this entry cannot be used under <paramref name="name"/>, or null: <c>local</c> with anything but the three keys; a remote
    /// entry without a host; an auth, detail, method or store word out of range; a <c>runas</c> without a <c>DOMAIN\name</c> or UPN
    /// account; a Credential Manager store under <c>windows</c>. Pure.
    /// </summary>
    public string? Problem(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string detail = Detail?.Trim() ?? "";
        if (detail.Length > 0 && !detail.Equals(FullDetail, StringComparison.OrdinalIgnoreCase) && !detail.Equals(RedactedDetail, StringComparison.OrdinalIgnoreCase))
        {
            return EventLogText.BadDetail(detail);
        }

        if (string.Equals(name.Trim(), EventLogText.LocalName, StringComparison.OrdinalIgnoreCase))
        {
            return Host is not null || Auth is not null || User is not null || Password is not null || PasswordStore is not null || Credential is not null || AuthMethod is not null
                ? EventLogText.LocalTakesNoHost
                : null;
        }

        if (string.IsNullOrWhiteSpace(Host))
        {
            return EventLogText.NoHost;
        }

        string auth = Auth?.Trim() ?? "";
        if (auth.Length > 0 && !auth.Equals(WindowsAuth, StringComparison.OrdinalIgnoreCase) && !auth.Equals(RunAsAuth, StringComparison.OrdinalIgnoreCase))
        {
            return EventLogText.BadAuth(auth);
        }

        string method = AuthMethod?.Trim() ?? "";
        if (method.Length > 0 && !AuthMethods.Contains(method, StringComparer.OrdinalIgnoreCase))
        {
            return EventLogText.BadAuthMethod(method);
        }

        string store = PasswordStore?.Trim() ?? "";
        if (store.Length > 0 && !store.Equals(FileStore, StringComparison.OrdinalIgnoreCase) && !store.Equals(CredmanStore, StringComparison.OrdinalIgnoreCase))
        {
            return SqlText.BadPasswordStore(store);
        }

        if (Computers.ComputerConfig.CredentialProblem(Credential, CredentialPrefix) is { } credential)
        {
            return credential;
        }

        if (IsRunAs)
        {
            if (string.IsNullOrWhiteSpace(User))
            {
                return EventLogText.RunAsNoUser;
            }

            if (WindowsCredentials.SplitAccount(User) is null)
            {
                return SqlText.RunAsNeedsDomain(User.Trim());
            }
        }
        else if (InCredentialManager)
        {
            return EventLogText.CredmanWithWindows;
        }

        return null;
    }
}

/// <summary>
/// A computer by its name, and the file it came from (null for <c>local</c> with no entry). <see cref="IsLocal"/> for this PC.
/// </summary>
public sealed record EventLogComputer(string Name, EventLogComputerConfig Config, string? Source = null)
{
    public bool IsLocal => string.Equals(Name, EventLogText.LocalName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Where it is: <c>this PC</c>, or the host.</summary>
    public string Where => IsLocal ? EventLogText.ThisPc : Config.Host?.Trim() ?? "";

    /// <summary>Who it is read as: the <c>runas</c> account, else the user running the app.</summary>
    public string Account => Config.IsRunAs ? Config.User!.Trim() : OperatingSystem.IsWindows() ? Environment.UserDomainName + "\\" + Environment.UserName : Environment.UserName;
}
