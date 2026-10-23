using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using NeonSidekick.Diagnostics;
using NeonSidekick.Sql;

namespace NeonSidekick.ActiveDirectory;

/// <summary>
/// One domain of <c>ad.json</c> (2026-10-15, the admin-tools plan): the domain's DNS name, the server to ask (a DC; the domain's name when
/// absent), the base to search under (the domain's naming context when absent), LDAPS or not, the sign-in (<c>windows</c>, as the user running
/// the app — Kerberos from a joined PC; <c>runas</c>, an explicit Negotiate identity, its password kept as the other files' are) and the detail
/// (<c>redacted</c>, the default: phones, addresses and employee numbers left out). Not a computer: its own small loader on the shared
/// <see cref="ConnectionsFileEdit"/> and <see cref="WindowsCredentials"/> pieces.
/// </summary>
public sealed class DirectoryDomainConfig
{
    public const string WindowsAuth = "windows";
    public const string RunAsAuth = "runas";
    public const string FullDetail = "full";
    public const string RedactedDetail = "redacted";
    public const string CredentialPrefix = "NeonSidekick/ad/";

    /// <summary>The domain's DNS name: <c>corp.example</c>.</summary>
    public string? Domain { get; set; }

    /// <summary>The server to ask (a DC's name, or <c>localhost</c> for a test container); the domain's name when absent.</summary>
    public string? Server { get; set; }

    /// <summary>The base searched under; the domain's naming context when absent.</summary>
    public string? BaseDn { get; set; }

    /// <summary>LDAPS on 636 (the server's certificate checked against Windows' trust store); absent or false, 389 signed and sealed.</summary>
    public bool? Tls { get; set; }

    public string? Auth { get; set; }

    public string? User { get; set; }

    public string? Password { get; set; }

    public string? PasswordStore { get; set; }

    public string? Credential { get; set; }

    public string? Detail { get; set; }

    public string? Description { get; set; }

    /// <summary>Keys the file does not know (2026-10-23, the admin review's H5, DA1); the loader refuses the entry, naming the key.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore]
    public bool IsRunAs => string.Equals(Auth?.Trim(), RunAsAuth, StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool InCredentialManager => string.Equals(PasswordStore?.Trim(), SqlConnectionConfig.CredmanStore, StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool Full => string.Equals(Detail?.Trim(), FullDetail, StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool UsesTls => Tls == true;

    public string CredentialTarget(string name) => string.IsNullOrWhiteSpace(Credential) ? CredentialPrefix + name : Credential.Trim();

    /// <summary>The reason this entry cannot be used, or null. Pure.</summary>
    public string? Problem(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (string.Equals(name.Trim(), DirectoryText.CurrentName, StringComparison.OrdinalIgnoreCase))
        {
            return DirectoryText.CurrentReserved;
        }

        if (string.IsNullOrWhiteSpace(Domain))
        {
            return DirectoryText.NoDomain;
        }

        string detail = Detail?.Trim() ?? "";
        if (detail.Length > 0 && !detail.Equals(FullDetail, StringComparison.OrdinalIgnoreCase) && !detail.Equals(RedactedDetail, StringComparison.OrdinalIgnoreCase))
        {
            return Computers.ComputerText.BadDetail(detail);
        }

        string auth = Auth?.Trim() ?? "";
        if (auth.Length > 0 && !auth.Equals(WindowsAuth, StringComparison.OrdinalIgnoreCase) && !auth.Equals(RunAsAuth, StringComparison.OrdinalIgnoreCase))
        {
            return Computers.ComputerText.BadAuth(auth);
        }

        string store = PasswordStore?.Trim() ?? "";
        if (store.Length > 0 && !store.Equals(SqlConnectionConfig.FileStore, StringComparison.OrdinalIgnoreCase) && !store.Equals(SqlConnectionConfig.CredmanStore, StringComparison.OrdinalIgnoreCase))
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
                return Computers.ComputerText.RunAsNoUser;
            }

            if (WindowsCredentials.SplitAccount(User) is null)
            {
                return SqlText.RunAsNeedsDomain(User.Trim());
            }
        }
        else if (InCredentialManager)
        {
            return Computers.ComputerText.CredmanWithWindows;
        }

        if (BaseDn is { } baseDn && !baseDn.Contains('=', StringComparison.Ordinal))
        {
            return DirectoryText.BadBaseDn(baseDn);
        }

        return null;
    }
}

/// <summary>A domain by its name and the file it came from (null for <c>current</c>).</summary>
public sealed record DirectoryDomain(string Name, DirectoryDomainConfig Config, string? Source = null)
{
    public bool IsCurrent => string.Equals(Name, DirectoryText.CurrentName, StringComparison.OrdinalIgnoreCase);

    public string DomainName => Config.Domain!.Trim().TrimEnd('.');

    /// <summary>The server asked: the entry's, else the domain's name.</summary>
    public string Server => string.IsNullOrWhiteSpace(Config.Server) ? DomainName : Config.Server.Trim();

    public int Port => Config.UsesTls ? 636 : 389;

    /// <summary>The base searched under.</summary>
    public string BaseDn => string.IsNullOrWhiteSpace(Config.BaseDn) ? AdValues.DomainDn(DomainName) : Config.BaseDn.Trim();

    public string Account => Config.IsRunAs ? Config.User!.Trim()
        : OperatingSystem.IsWindows() ? Environment.UserDomainName + "\\" + Environment.UserName
        : OperatingSystem.IsMacOS() ? DirectoryText.MacTicketAccount   // a Mac signs in with the default Kerberos ticket (2026-10-18)
        : Environment.UserName;
}

/// <summary>The domains offered: the file's and <c>current</c>, with the problems.</summary>
public sealed record DirectoryCatalog(IReadOnlyList<DirectoryDomain> Domains, IReadOnlyList<SqlConfigProblem> Problems, int Hidden = 0)
{
    /// <summary>
    /// The names <c>Directory domains offered</c> ticks: null (never set) is <c>current</c> when this PC is joined, else none; an empty list none.
    /// </summary>
    public static IReadOnlyList<string> OfferedNames(IReadOnlyList<string>? offered, bool joined) => offered ?? (joined ? [DirectoryText.CurrentName] : []);

    public DirectoryCatalog Offered(IReadOnlyList<string>? names, bool joined)
    {
        var wanted = new HashSet<string>(OfferedNames(names, joined).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
        var kept = Domains.Where(d => wanted.Contains(d.Name)).ToList();
        return this with { Domains = kept, Hidden = Hidden + Domains.Count - kept.Count };
    }

    /// <summary>
    /// The domain a call means: the one named by its entry's name or, failing that, by its DNS name (2026-10-15, the user's report: the answers
    /// say "in lab.neon.test", so a model naturally passed that for the entry <c>lab</c>), a trailing dot ignored; omitted, <c>current</c> when
    /// ticked, else the only one. The refusal otherwise, naming each ticked one and its DNS name.
    /// </summary>
    public (DirectoryDomain? Domain, string? Error) Find(string? name)
    {
        string offered = string.Join(", ", Domains.Select(d => "'" + d.Name + "'" + (d.IsCurrent || string.Equals(d.Name, d.DomainName, StringComparison.OrdinalIgnoreCase) ? "" : " (" + d.DomainName + ")")));
        if (!string.IsNullOrWhiteSpace(name))
        {
            string wanted = name.Trim().TrimEnd('.');
            if (Domains.FirstOrDefault(d => string.Equals(d.Name, wanted, StringComparison.OrdinalIgnoreCase)) is { } named)
            {
                return (named, null);
            }

            var byDns = Domains.Where(d => string.Equals(d.DomainName, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
            return byDns.Count switch
            {
                1 => (byDns[0], null),
                0 => (null, DirectoryText.UnknownDomain(name.Trim(), offered)),
                _ => (null, DirectoryText.DomainNeeded(Computers.ComputerText.Names(byDns.Select(d => d.Name)))),
            };
        }

        if (Domains.Count == 0)
        {
            return (null, DirectoryText.NoDomainTicked);
        }

        return Domains.FirstOrDefault(d => d.IsCurrent) is { } current ? (current, null)
            : Domains.Count == 1 ? (Domains[0], null)
            : (null, DirectoryText.DomainNeeded(offered));
    }
}

/// <summary><c>ad.json</c>: <c>{ "domains": { "&lt;name&gt;": { … } } }</c>.</summary>
public sealed class DirectoryConfigFile
{
    public const string FileName = "ad.json";
    public const string Section = "domains";
    public const string Noun = "domain";

    public Dictionary<string, DirectoryDomainConfig?> Domains { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Keys beside <c>domains</c>: listed as problems, never used (2026-10-23, DA1).</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>What a fresh file holds: Windows' text, or on a Mac its own (2026-10-18: Kerberos, no joined PC, the Keychain). Pinned.</summary>
    public static string EmptyText => OperatingSystem.IsMacOS() ? MacEmptyText : WindowsEmptyText;

    /// <summary>A fresh file on a Mac (2026-10-18, the Mac AD plan). Pinned.</summary>
    public static string MacEmptyText =>
        "{\n" +
        "  // One entry per domain. Its name is what the model passes as \"domain\", and what /ad domain:<name> reads.\n" +
        "  // A Mac has no built-in \"current\" domain: name each one here. A domain is read only once it is ticked in\n" +
        "  // Directory domains offered (" + DirectoryText.MenuPath + "). Remove the leading // from an example to use it, and put it\n" +
        "  // inside \"domains\" below. Everything is read-only, signed and sealed on 389 (Kerberos).\n" +
        "  //\n" +
        "  // A domain read as you, with the Kerberos ticket kinit gives (kinit you@CORP.EXAMPLE); its DCs are found in DNS:\n" +
        "  // \"corp\": { \"domain\": \"corp.example\", \"description\": \"the office\" },\n" +
        "  //\n" +
        "  // As another account, name@REALM, the password in " + SqlText.CredentialStore + " as NeonSidekick/ad/<name> (set it with\n" +
        "  // Directory set password on " + DirectoryText.MenuPath + "):\n" +
        "  // \"lab\": { \"domain\": \"lab.example\", \"server\": \"dc1.lab.example\", \"auth\": \"runas\", \"user\": \"reader@LAB.EXAMPLE\", \"passwordStore\": \"credman\" },\n" +
        "  //\n" +
        "  // server: a DC (the domain's DCs from DNS when absent). baseDn: where to search (the domain when absent). tls: true for LDAPS on\n" +
        "  // 636 (the DC's certificate must be trusted in the Keychain). detail: redacted (the default) or full. auth: windows (your\n" +
        "  // ticket) or runas. A runas password typed here in plain text is encrypted at the next read.\n" +
        "  \"domains\": {}\n" +
        "}\n";

    /// <summary>A fresh file on Windows. Pinned.</summary>
    public static string WindowsEmptyText =>
        "{\n" +
        "  // One entry per domain. Its name is what the model passes as \"domain\", and what /ad domain:<name> reads.\n" +
        "  // \"current\" is this PC's own domain when it is joined, built in (signing in as you, Kerberos).\n" +
        "  // A domain is read only once it is ticked in Directory domains offered (" + DirectoryText.MenuPath + ").\n" +
        "  // Remove the leading // from an example to use it, and put it inside \"domains\" below. Everything is read-only.\n" +
        "  //\n" +
        "  // A domain read as you, through one DC:\n" +
        "  // \"corp\": { \"domain\": \"corp.example\", \"server\": \"dc01.corp.example\", \"description\": \"the office\" },\n" +
        "  //\n" +
        "  // As another account (from a PC outside the domain this is NTLM inside Negotiate), the password in " + SqlText.CredentialStore + "\n" +
        "  // as NeonSidekick/ad/<name> — set it with Directory set password on " + DirectoryText.MenuPath + ", or:\n" +
        "  // " + SqlText.CredentialCommand("NeonSidekick/ad/lab", "LAB\\reader") + "\n" +
        "  // \"lab\": { \"domain\": \"lab.example\", \"server\": \"localhost\", \"auth\": \"runas\", \"user\": \"LAB\\\\reader\", \"passwordStore\": \"credman\" },\n" +
        "  //\n" +
        "  // server: a DC (the domain's name when absent). baseDn: where to search (the domain when absent). tls: true for LDAPS on 636\n" +
        "  // (the certificate must be trusted here); otherwise 389, signed and sealed. detail: redacted (the default: phones, addresses\n" +
        "  // and employee numbers left out) or full. auth: windows (as you) or runas. A runas password typed here in plain text is\n" +
        "  // encrypted (\"dpapi:…\") at the next read.\n" +
        "  \"domains\": {}\n" +
        "}\n";

    public static string ProfilePath(string profileDirectory) => NeonSidekick.Settings.Profiles.ConfigPath(profileDirectory, FileName);

    /// <summary>The home's file: <c>&lt;home&gt;\configs\ad.json</c> (configs since 2026-10-16), every profile's.</summary>
    public static string GlobalPath(string home) => NeonSidekick.Settings.Profiles.HomeConfigPath(home, FileName);

    public static (IReadOnlyList<DirectoryDomain> Domains, IReadOnlyList<SqlConfigProblem> Problems) Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Computers.ConfigCache.Get(FileName, path, () => LoadUncached(path));
    }

    /// <summary>The read under <see cref="Load"/>'s cache (2026-10-23, the admin review's mechanism 1: DA1, Q1, C7 as the other families).</summary>
    private static (IReadOnlyList<DirectoryDomain> Domains, IReadOnlyList<SqlConfigProblem> Problems) LoadUncached(string path)
    {
        if (!File.Exists(path))
        {
            return ([], []);
        }

        DirectoryConfigFile? file;
        IReadOnlySet<string> repeated;
        IReadOnlyDictionary<string, string> twice;
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            var json = Computers.ConfigChecks.WithoutBom(bytes);
            repeated = ConnectionsFileEdit.RepeatedNames(json, Section);
            twice = ConnectionsFileEdit.RepeatedKeys(json, Section);
            file = JsonSerializer.Deserialize(json, DirectoryJsonContext.Default.DirectoryConfigFile);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            string detail = LogText.Excerpt(ex.Message);
            DiagnosticLog.Warn(DirectoryText.Category, SqlText.ConfigProblemLogLine(path, detail));
            return ([], [new SqlConfigProblem(path, Computers.ComputerText.UnreadableFile(detail), WholeFile: true)]);
        }

        // "domains": null reads as null past the non-nullable type (2026-10-22, review K1: it threw here, and the
        // startup encryption pass and every turn's tool build with it). SqliteConfigFile has had this check since 2026-10-04.
        if (file?.Domains is not { Count: > 0 })
        {
            return ([], []);
        }

        var domains = new List<DirectoryDomain>();
        var problems = Computers.ConfigChecks.UnknownFileKeys(file.Extra, Section).Select(p => new SqlConfigProblem(path, p)).ToList();
        foreach (var (name, config) in file.Domains)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                problems.Add(new SqlConfigProblem(path, Computers.ComputerText.BlankName));
                continue;
            }

            var entry = config ?? new DirectoryDomainConfig();
            string trimmed = name.Trim();
            string? reason = repeated.Contains(trimmed) ? Computers.ComputerText.NamedTwice
                : twice.TryGetValue(trimmed, out string? key) ? Computers.ComputerText.KeyTwice(key)
                : Computers.ConfigChecks.UnknownKey(entry.Extra, DirectoryJsonContext.Default.DirectoryDomainConfig) ?? entry.Problem(trimmed);
            if (reason is not null)
            {
                problems.Add(new SqlConfigProblem(SqlText.ConnectionSource(path, trimmed), reason, Name: trimmed));
                continue;
            }

            EncryptInPlace(path, trimmed, entry);
            domains.Add(new DirectoryDomain(trimmed, entry, path));
        }

        return (domains, problems);
    }

    /// <summary>The profile's file over the home's, <c>current</c> first when this PC is joined (<paramref name="joined"/>, its DNS name).</summary>
    public static DirectoryCatalog LoadCatalog(string profileDirectory, string? home, string? joined)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        var (profile, profileProblems) = Load(ProfilePath(profileDirectory));
        IReadOnlyList<DirectoryDomain> global = [];
        IReadOnlyList<SqlConfigProblem> globalProblems = [];
        if (home is not null && !string.Equals(Path.GetFullPath(ProfilePath(profileDirectory)), Path.GetFullPath(GlobalPath(home)), StringComparison.OrdinalIgnoreCase))
        {
            (global, globalProblems) = Load(GlobalPath(home));
        }

        // A profile entry that was refused still hides the home's of its name (2026-10-23, the admin review's D2).
        var names = new HashSet<string>(profile.Select(d => d.Name).Concat(profileProblems.Where(p => p.Name is not null).Select(p => p.Name!)), StringComparer.OrdinalIgnoreCase);
        var all = profile.Concat(global.Where(d => !names.Contains(d.Name))).ToList();
        if (joined is { Length: > 0 })
        {
            all.Insert(0, new DirectoryDomain(DirectoryText.CurrentName, new DirectoryDomainConfig { Domain = joined }));
        }

        return new DirectoryCatalog(all, [.. profileProblems, .. globalProblems]);
    }

    private static void EncryptInPlace(string path, string name, DirectoryDomainConfig config)
    {
        if (!config.IsRunAs || config.InCredentialManager || string.IsNullOrEmpty(config.Password) || WindowsCredentials.IsProtected(config.Password))
        {
            return;
        }

        var encrypted = WindowsCredentials.Protect(config.Password);
        string? error = encrypted.Error ?? ConnectionsFileEdit.WritePassword(path, name, encrypted.Value!, Section, Noun);
        if (error is null)
        {
            config.Password = encrypted.Value;
            DiagnosticLog.Info(DirectoryText.Category, SqlText.EncryptedLogLine(name, path));
        }
        else
        {
            DiagnosticLog.Warn(DirectoryText.Category, SqlText.EncryptFailedLogLine(name, path, error));
        }
    }

    /// <summary>Adds a domain; <paramref name="sealedPassword"/> written in the same splice (2026-10-23, the admin review's H9). Null on success.</summary>
    public static string? AddDomain(string path, string name, DirectoryDomainConfig config, string? sealedPassword = null) =>
        ConnectionsFileEdit.AddConnection(path, name, EntryText(config, sealedPassword), EmptyText, Section, Noun);

    public static string? ReplaceDomain(string path, string oldName, string newName, DirectoryDomainConfig config, string? sealedPassword = null) =>
        ConnectionsFileEdit.ReplaceConnection(path, oldName, newName, EntryText(config, sealedPassword), Section, Noun);

    /// <summary>An entry as the file holds it, from a copy with its password and unknown keys left out (2026-10-23: loaded entries are shared, <c>ConfigCache</c>).</summary>
    public static string EntryText(DirectoryDomainConfig config, string? sealedPassword = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        var info = DirectoryJsonContext.Default.DirectoryDomainConfig;
        var copy = JsonSerializer.Deserialize(JsonSerializer.Serialize(config, info), info)!;
        copy.Password = sealedPassword;
        copy.Extra = null;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            JsonSerializer.Serialize(writer, copy, info);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static IReadOnlyList<string> EncryptAll(string home)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        var paths = new List<string> { GlobalPath(home) };
        try
        {
            paths.AddRange(Settings.Profiles.List(home).Select(name => ProfilePath(Settings.Profiles.Directory(home, name))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(DirectoryText.Category, Computers.ComputerText.ProfilesUnlistedLogLine(FileName, Settings.Profiles.Root(home), LogText.Excerpt(ex.Message)));
        }

        var read = new List<string>();
        foreach (string path in paths.Where(File.Exists))
        {
            Load(path);
            read.Add(path);
        }

        return read;
    }

    public static bool EnsureExists(string path) => ConnectionsFileEdit.EnsureExists(path, EmptyText);

    /// <summary>The password a <c>runas</c> domain binds with; the sentence that names the fix when it cannot be had.</summary>
    public static CredentialResult ResolvePassword(DirectoryDomain domain)
    {
        ArgumentNullException.ThrowIfNull(domain);
        var config = domain.Config;
        if (!config.IsRunAs)
        {
            return CredentialResult.Ok("");
        }

        if (config.InCredentialManager)
        {
            string target = config.CredentialTarget(domain.Name);
            var read = WindowsCredentials.ReadGeneric(target);
            return read.NotFound ? CredentialResult.Failed(DirectoryText.NoCredential(target)) : Computers.ComputerConfig.CheckedUser(read, target, config.User);
        }

        string password = config.Password ?? "";
        if (password.Length == 0)
        {
            return CredentialResult.Failed(DirectoryText.NoPassword(domain.Name));
        }

        if (!WindowsCredentials.IsProtected(password))
        {
            return CredentialResult.Ok(password);
        }

        var decrypted = WindowsCredentials.Unprotect(password);
        return decrypted.Error is { } error ? CredentialResult.Failed(error.Replace(SqlText.SqlMenuPath, DirectoryText.MenuPath, StringComparison.Ordinal)) : decrypted;
    }

    public static (bool Saved, string Notice) SavePassword(DirectoryDomain domain, string password)
    {
        ArgumentNullException.ThrowIfNull(domain);
        ArgumentNullException.ThrowIfNull(password);
        var config = domain.Config;
        if (config.InCredentialManager)
        {
            string target = config.CredentialTarget(domain.Name);
            var written = WindowsCredentials.WriteGeneric(target, config.User?.Trim() ?? "", password);
            return written.Error is { } refused ? (false, SqlText.PasswordSaveFailed(domain.Name, refused)) : (true, SqlText.PasswordSavedToCredman(domain.Name, target));
        }

        var encrypted = WindowsCredentials.Protect(password);
        if (encrypted.Error is { } failed)
        {
            return (false, SqlText.PasswordSaveFailed(domain.Name, failed));
        }

        if (domain.Source is not { Length: > 0 } source)
        {
            return (false, SqlText.PasswordSaveFailed(domain.Name, "no ad.json holds it"));
        }

        return ConnectionsFileEdit.WritePassword(source, domain.Name, encrypted.Value!, Section, Noun) is { } error
            ? (false, SqlText.PasswordSaveFailed(domain.Name, error))
            : (true, SqlText.PasswordSavedToFile(domain.Name, source));
    }
}

/// <summary>The source-generated context for <c>ad.json</c> (2026-10-15, <c>EventLogJsonContext</c>'s twin).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DirectoryConfigFile))]
[JsonSerializable(typeof(DirectoryDomainConfig))]
public sealed partial class DirectoryJsonContext : JsonSerializerContext;
