using System.Text.Json.Serialization;
using NeonSidekick.Sql;

namespace NeonSidekick.Computers;

/// <summary>
/// One family's words (2026-10-15, the admin-tools plan): what its file, settings and refusals call things, so the shared computers layer
/// (<see cref="ComputerConfig"/>, <see cref="ComputerCatalog{TConfig}"/>, <see cref="ComputerFamily{TConfig}"/>) speaks for services and tasks
/// alike. <see cref="ActionWords"/> are the words a computer's <c>controlActions</c> takes; <see cref="VisibleKey"/> the allow-list's key
/// (<c>services</c>, <c>folders</c>); <see cref="Separator"/> the patterns' (none for a service's name, <c>\</c> for a task's path).
/// </summary>
public sealed record ComputerFamilyInfo(
    string Category,
    string FileName,
    string OfferedSetting,
    string MenuPath,
    string SetPasswordRow,
    string CredentialPrefix,
    IReadOnlyList<string> ActionWords,
    string VisibleKey,
    string Things,
    char Separator,
    string Glyph);

/// <summary>
/// What every computer of <c>services.json</c> and <c>tasks.json</c> holds (2026-10-15, the admin-tools plan: <c>EventLogComputerConfig</c>
/// made a base): the host reached (<c>windows</c> as the user running the app; <c>runas</c> as another account, its password kept as the other
/// files' are — <c>file</c> DPAPI-encrypted in <see cref="Password"/>, or <c>credman</c>), what the model may change there (<see cref="Access"/>:
/// <c>read</c>, the default, or <c>readwrite</c>; <see cref="Control"/>, the patterns it may change — absent, nothing; <c>controlActions</c> on
/// each family's subclass) and in what detail it reads (<see cref="Detail"/>: <c>full</c> or <c>redacted</c>, the default). The name
/// <c>local</c> is this PC, built in, read-only by default; its entry may carry everything but the host and the sign-in. A password is never
/// shown, logged or sent to the model.
/// </summary>
public abstract class ComputerConfig
{
    public const string WindowsAuth = "windows";
    public const string RunAsAuth = "runas";
    public const string FullDetail = "full";
    public const string RedactedDetail = "redacted";
    public const string FileStore = SqlConnectionConfig.FileStore;
    public const string CredmanStore = SqlConnectionConfig.CredmanStore;

    /// <summary>The name of this PC among the computers: built in, reserved in each file.</summary>
    public const string LocalName = "local";

    /// <summary>The computer's name or address.</summary>
    public string? Host { get; set; }

    /// <summary><c>windows</c> (the default) or <c>runas</c>.</summary>
    public string? Auth { get; set; }

    /// <summary>The account under <c>runas</c>: <c>DOMAIN\name</c> or <c>name@domain</c>.</summary>
    public string? User { get; set; }

    /// <summary>The password under <c>runas</c> and the <c>file</c> store: <c>dpapi:…</c>, or plain text the next read encrypts. Never shown.</summary>
    public string? Password { get; set; }

    /// <summary><c>file</c> (the default) or <c>credman</c>.</summary>
    public string? PasswordStore { get; set; }

    /// <summary>The Credential Manager entry under <c>credman</c>; the family's prefix and the name when absent.</summary>
    public string? Credential { get; set; }

    /// <summary>What the computer is, in the user's words; the model reads it to pick one.</summary>
    public string? Description { get; set; }

    /// <summary><c>read</c> (the default) or <c>readwrite</c>: whether anything may be changed here at all.</summary>
    public string? Access { get; set; }

    /// <summary><c>full</c> or <c>redacted</c> (the default): what the main chat's model reads of this computer.</summary>
    public string? Detail { get; set; }

    /// <summary>The patterns of what may be changed here (case ignored, <c>*</c> a run); absent, nothing — unlike the visible list, where absent is everything.</summary>
    public List<string>? Control { get; set; }

    /// <summary>The actions allowed here (the family's words); absent, all; empty, none.</summary>
    public List<string>? ControlActions { get; set; }

    /// <summary>
    /// Keys the family does not know (2026-10-23, the admin review's H5, decision DA1): <c>controlAction</c> for <c>controlActions</c> read as
    /// "every action" and <c>protect</c> for <c>protected</c> as "nothing protected". The loader refuses an entry holding one, naming the key.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }

    [JsonIgnore]
    public bool IsRunAs => string.Equals(Auth?.Trim(), RunAsAuth, StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool NeedsPassword => IsRunAs;

    [JsonIgnore]
    public bool InCredentialManager => string.Equals(PasswordStore?.Trim(), CredmanStore, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the main chat reads this computer in full.</summary>
    [JsonIgnore]
    public bool Full => string.Equals(Detail?.Trim(), FullDetail, StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public string DetailWord => Full ? FullDetail : RedactedDetail;

    /// <summary>Whether this computer's entry says <c>access: readwrite</c>.</summary>
    [JsonIgnore]
    public bool ReadWrite => ConnectionAccess.IsReadWrite(Access);

    /// <summary>The Credential Manager entry this computer's password is read from under <c>credman</c>.</summary>
    public string CredentialTarget(ComputerFamilyInfo family, string name)
    {
        ArgumentNullException.ThrowIfNull(family);
        return string.IsNullOrWhiteSpace(Credential) ? family.CredentialPrefix + name : Credential.Trim();
    }

    /// <summary>Whether <paramref name="action"/> is allowed here: absent <c>controlActions</c> allows every action, an empty list none.</summary>
    public bool AllowsAction(string action) => ControlActions is null || ControlActions.Any(a => string.Equals(a.Trim(), action, StringComparison.OrdinalIgnoreCase));

    /// <summary>The actions allowed here, in the family's order.</summary>
    public IReadOnlyList<string> AllowedActions(ComputerFamilyInfo family)
    {
        ArgumentNullException.ThrowIfNull(family);
        return family.ActionWords.Where(AllowsAction).ToList();
    }

    /// <summary>Whether <paramref name="name"/> matches a <see cref="Control"/> pattern; absent, nothing does.</summary>
    public bool InControl(string name, char separator) => Control is { } patterns && NameGlob.Any(patterns, name, separator);

    /// <summary>
    /// The reason this entry cannot be used under <paramref name="name"/>, or null: <c>local</c> with a host or a sign-in; a remote entry
    /// without a host; an auth, detail, access or store word out of range; an action word the family does not know; a <c>runas</c> without a
    /// <c>DOMAIN\name</c> or UPN account; a Credential Manager store under <c>windows</c>; then the family's own (<see cref="ExtraProblem"/>). Pure.
    /// </summary>
    public string? Problem(string name, ComputerFamilyInfo family)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(family);
        string detail = Detail?.Trim() ?? "";
        if (detail.Length > 0 && !detail.Equals(FullDetail, StringComparison.OrdinalIgnoreCase) && !detail.Equals(RedactedDetail, StringComparison.OrdinalIgnoreCase))
        {
            return ComputerText.BadDetail(detail);
        }

        if (ConnectionAccess.Problem(Access) is { } access)
        {
            return access;
        }

        if (ControlActions is { } actions && actions.FirstOrDefault(a => !family.ActionWords.Contains(a.Trim(), StringComparer.OrdinalIgnoreCase)) is { } unknown)
        {
            return ComputerText.BadAction(unknown, family);
        }

        if (string.Equals(name.Trim(), LocalName, StringComparison.OrdinalIgnoreCase))
        {
            return Host is not null || Auth is not null || User is not null || Password is not null || PasswordStore is not null || Credential is not null
                ? ComputerText.LocalTakesNoHost
                : ExtraProblem(family);
        }

        if (string.IsNullOrWhiteSpace(Host))
        {
            return ComputerText.NoHost;
        }

        string auth = Auth?.Trim() ?? "";
        if (auth.Length > 0 && !auth.Equals(WindowsAuth, StringComparison.OrdinalIgnoreCase) && !auth.Equals(RunAsAuth, StringComparison.OrdinalIgnoreCase))
        {
            return ComputerText.BadAuth(auth);
        }

        string store = PasswordStore?.Trim() ?? "";
        if (store.Length > 0 && !store.Equals(FileStore, StringComparison.OrdinalIgnoreCase) && !store.Equals(CredmanStore, StringComparison.OrdinalIgnoreCase))
        {
            return SqlText.BadPasswordStore(store);
        }

        if (CredentialProblem(Credential, family.CredentialPrefix) is { } credential)
        {
            return credential;
        }

        if (IsRunAs && IsThisPc(Host))
        {
            // Another account's sign-in does not apply to this PC (2026-10-23, the admin review's J6): a NEW_CREDENTIALS token changes only
            // what goes over the network, so the change ran as the app's own user while the question and the audit named the runas account.
            return ComputerText.RunAsOnThisPc;
        }

        if (IsRunAs)
        {
            if (string.IsNullOrWhiteSpace(User))
            {
                return ComputerText.RunAsNoUser;
            }

            if (WindowsCredentials.SplitAccount(User) is null)
            {
                return SqlText.RunAsNeedsDomain(User.Trim());
            }
        }
        else if (InCredentialManager)
        {
            return ComputerText.CredmanWithWindows;
        }

        return ExtraProblem(family);
    }

    /// <summary>
    /// Whether <paramref name="host"/> names this PC: <c>localhost</c>, a loopback address, <c>.</c>, or this PC's name (its NetBIOS or DNS
    /// name, bare or with a domain after it).
    /// </summary>
    public static bool IsThisPc(string? host)
    {
        string name = host?.Trim().TrimStart('\\').TrimEnd('.').ToLowerInvariant() ?? "";
        if (name.Length == 0)
        {
            return false;
        }

        if (name is "localhost" or "." or "::1" or "[::1]" || name.StartsWith("127.", StringComparison.Ordinal))
        {
            return true;
        }

        string first = name.Split('.')[0];
        string own = Environment.MachineName.ToLowerInvariant();
        string dns;
        try
        {
            dns = System.Net.Dns.GetHostName().ToLowerInvariant();
        }
        catch (System.Net.Sockets.SocketException)
        {
            dns = own;
        }

        return first == own || name == dns || first == dns.Split('.')[0];
    }

    /// <summary>The family's own reason this entry cannot be used, or null.</summary>
    protected virtual string? ExtraProblem(ComputerFamilyInfo family) => null;

    /// <summary>A <c>credential</c> that does not sit under <paramref name="prefix"/> (DA2), or null. Shared by the event logs' and AD's entries.</summary>
    public static string? CredentialProblem(string? credential, string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        return string.IsNullOrWhiteSpace(credential) || (credential.Trim().StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && credential.Trim().Length > prefix.Length)
            ? null
            : ComputerText.CredentialOutsidePrefix(prefix);
    }

    /// <summary>
    /// <paramref name="read"/> when its stored user is empty or is <paramref name="user"/> (case ignored), else the refusal naming both (DA2).
    /// An empty stored user (<c>cmdkey /generic:… /pass</c> without <c>/user</c>) is accepted, as before.
    /// </summary>
    public static CredentialResult CheckedUser(CredentialResult read, string target, string? user)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (read.Error is not null || string.IsNullOrWhiteSpace(read.User) || user is null
            || string.Equals(read.User.Trim(), user.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return read;
        }

        return CredentialResult.Failed(ComputerText.CredentialUserMismatch(target, read.User.Trim(), user.Trim()));
    }
}

/// <summary>A computer by its name, and the file it came from (null for <c>local</c> with no entry). <see cref="IsLocal"/> for this PC.</summary>
public sealed record Computer<TConfig>(string Name, TConfig Config, string? Source = null) where TConfig : ComputerConfig
{
    public bool IsLocal => string.Equals(Name, ComputerConfig.LocalName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Where it is: <c>this PC</c>, or the host.</summary>
    public string Where => IsLocal ? ComputerText.ThisPc : Config.Host?.Trim() ?? "";

    /// <summary>The host a native call opens: null for this PC.</summary>
    public string? RemoteHost => IsLocal ? null : Config.Host?.Trim();

    /// <summary>Who it is reached as: the <c>runas</c> account, else the user running the app.</summary>
    public string Account => Config.IsRunAs ? Config.User!.Trim() : OperatingSystem.IsWindows() ? Environment.UserDomainName + "\\" + Environment.UserName : Environment.UserName;
}
