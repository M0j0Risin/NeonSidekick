using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using NeonSidekick.Diagnostics;
using NeonSidekick.Sql;

namespace NeonSidekick.Computers;

/// <summary>A family's file: <c>{ "computers": { "&lt;name&gt;": { … } } }</c>.</summary>
public sealed class ComputersFile<TConfig> where TConfig : ComputerConfig
{
    /// <summary>The computers by name, in file order.</summary>
    public Dictionary<string, TConfig?> Computers { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Keys beside <c>computers</c>: listed as problems, never used (2026-10-23, DA1).</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>
/// What a family's files hold (2026-10-15, the admin-tools plan: <c>EventLogCatalog</c> made generic): <c>local</c> first — built in, with a
/// <c>"local"</c> entry's keys over it — then the usable computers (the profile's first, then the home's the profile does not shadow, each in
/// file order), and the problems, never a throw.
/// </summary>
public sealed record ComputerCatalog<TConfig>(IReadOnlyList<Computer<TConfig>> Computers, IReadOnlyList<SqlConfigProblem> Problems, ComputerFamilyInfo Family, int Hidden = 0)
    where TConfig : ComputerConfig
{
    /// <summary>The names an <c>…computers offered</c> setting ticks: null (never set) is <c>local</c> alone, an empty list none.</summary>
    public static IReadOnlyList<string> OfferedNames(IReadOnlyList<string>? offered) => offered ?? [ComputerConfig.LocalName];

    /// <summary>The catalog narrowed to the ticked computers (case ignored), in its order, <see cref="Hidden"/> counting the rest; problems kept. Pure.</summary>
    public ComputerCatalog<TConfig> Offered(IReadOnlyList<string>? names)
    {
        var wanted = new HashSet<string>(OfferedNames(names).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
        var kept = Computers.Where(c => wanted.Contains(c.Name)).ToList();
        return this with { Computers = kept, Hidden = Hidden + Computers.Count - kept.Count };
    }

    /// <summary>
    /// The actions of <paramref name="all"/> every <c>readwrite</c> computer here allows, when they agree; all of them when they disagree or none
    /// is readwrite (each call is checked anyway): a write tool's <c>action</c> enum (2026-10-15; shared by the services and tasks since
    /// 2026-10-20). Pure.
    /// </summary>
    public IReadOnlyList<string> AgreedActions(IReadOnlyList<string> all)
    {
        ArgumentNullException.ThrowIfNull(all);
        var sets = Computers.Where(c => c.Config.ReadWrite).Select(c => all.Where(c.Config.AllowsAction).ToList()).ToList();
        return sets.Count > 0 && sets.All(s => s.SequenceEqual(sets[0])) ? sets[0] : all;
    }

    /// <summary>The ticked computer named <paramref name="name"/> (case ignored); null for none.</summary>
    public Computer<TConfig>? Named(string name) =>
        Computers.FirstOrDefault(c => string.Equals(c.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The computer a call means: <paramref name="name"/> when given; omitted, <c>local</c> when ticked, else the only ticked one. Null with the
    /// refusal: an unknown or unticked name (naming the ticked ones), several and none named, none ticked.
    /// </summary>
    public (Computer<TConfig>? Computer, string? Error) Find(string? name)
    {
        string offered = ComputerText.Names(Computers.Select(c => c.Name));
        if (!string.IsNullOrWhiteSpace(name))
        {
            return Named(name) is { } found ? (found, null) : (null, ComputerText.UnknownComputer(name.Trim(), offered, Family));
        }

        if (Computers.Count == 0)
        {
            return (null, ComputerText.NoComputerTicked(Family));
        }

        if (Computers.FirstOrDefault(c => c.IsLocal) is { } local)
        {
            return (local, null);
        }

        return Computers.Count == 1 ? (Computers[0], null) : (null, ComputerText.ComputerNeeded(offered));
    }
}

/// <summary>
/// The ticks of an <c>…computers offered</c> (or domains offered) setting kept in step with the files (2026-10-23, the admin review's H6,
/// decision DP4): ticks are names, so a tick left after its entry went attached itself to a later entry of that name (another profile's
/// home-file <c>readwrite</c> one). A load that finds a ticked name gone drops it; only a clean load does (no problem anywhere: a file that
/// failed to read, or an entry refused by a typo, must not untick a computer). The built-in names stay. Pure.
/// </summary>
public static class OfferedTicks
{
    /// <summary>
    /// The ticks with every name not in <paramref name="present"/> dropped, or null when nothing changes (or the load was not clean, or nothing
    /// was ever ticked). A load with no entries of its own (only <paramref name="builtIn"/>) never prunes (2026-10-23, the branch review of DP7):
    /// a missing file reads as an empty one, so an editor's save by rename or a home briefly out of reach would have saved the ticks away.
    /// </summary>
    public static List<string>? Pruned(IReadOnlyList<string>? offered, IEnumerable<string> present, bool clean, params string[] builtIn)
    {
        ArgumentNullException.ThrowIfNull(present);
        ArgumentNullException.ThrowIfNull(builtIn);
        var listed = present.ToList();
        if (offered is null || !clean || !listed.Any(n => !builtIn.Contains(n.Trim(), StringComparer.OrdinalIgnoreCase)))
        {
            return null;
        }

        var names = new HashSet<string>(listed.Concat(builtIn), StringComparer.OrdinalIgnoreCase);
        var kept = offered.Where(n => names.Contains(n.Trim())).ToList();
        return kept.Count == offered.Count ? null : kept;
    }
}

/// <summary>
/// A family's file and secrets (2026-10-15, the admin-tools plan: <c>EventLogConfigFile</c> and <c>EventLogSecrets</c> made generic): one file
/// in the profile's <c>configs</c> folder and one in the home's (since 2026-10-16), the profile's winning by name; a missing file is empty, a corrupt one a problem and one
/// warning; a plain <c>password</c> DPAPI-encrypted in place at the first read that sees it (<see cref="ConnectionsFileEdit"/> under
/// <c>computers</c>). Each family hands in its source-generated <see cref="JsonTypeInfo{T}"/>s, so there is no reflection path. Read again
/// once a file changes (<see cref="ConfigCache"/>, 2026-10-23; afresh at every call before). Not carried by <c>/profile push|pull</c>, not
/// copied by <c>/profile add</c>. Since 2026-10-23 (the admin review's mechanism 1) an entry is refused for an unknown key (DA1), a name
/// given twice (Q1) or a key given twice (C7), and a profile's refused entry hides the home's of the same name (D2): a broken read-only
/// lock-down no longer falls back to a readwrite one.
/// </summary>
public sealed class ComputerFamily<TConfig> where TConfig : ComputerConfig, new()
{
    /// <summary>The key the computers sit under.</summary>
    public const string Section = "computers";

    /// <summary>What a computer is called in the file edits' refusals.</summary>
    public const string Noun = "computer";

    private readonly JsonTypeInfo<ComputersFile<TConfig>> _file;
    private readonly JsonTypeInfo<TConfig> _entry;

    public ComputerFamily(ComputerFamilyInfo info, JsonTypeInfo<ComputersFile<TConfig>> file, JsonTypeInfo<TConfig> entry, string emptyText)
    {
        Info = info ?? throw new ArgumentNullException(nameof(info));
        _file = file ?? throw new ArgumentNullException(nameof(file));
        _entry = entry ?? throw new ArgumentNullException(nameof(entry));
        EmptyText = emptyText ?? throw new ArgumentNullException(nameof(emptyText));
    }

    public ComputerFamilyInfo Info { get; }

    /// <summary>What a fresh file holds: no computers, a commented example of each kind.</summary>
    public string EmptyText { get; }

    public string ProfilePath(string profileDirectory) => NeonSidekick.Settings.Profiles.ConfigPath(profileDirectory, Info.FileName);

    /// <summary>The home's file: <c>&lt;home&gt;\configs\&lt;FileName&gt;</c> (configs since 2026-10-16), every profile's.</summary>
    public string GlobalPath(string home) => NeonSidekick.Settings.Profiles.HomeConfigPath(home, Info.FileName);

    /// <summary>A catalog of <c>local</c> alone, as it is with no file.</summary>
    public ComputerCatalog<TConfig> LocalOnly => new([new Computer<TConfig>(ComputerConfig.LocalName, new TConfig())], [], Info);

    /// <summary>Reads <paramref name="path"/>: the usable entries in file order and a problem per entry that cannot be used. Never throws.</summary>
    public (IReadOnlyList<Computer<TConfig>> Computers, IReadOnlyList<SqlConfigProblem> Problems) Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return ConfigCache.Get(Info.FileName, path, () => LoadUncached(path));
    }

    private (IReadOnlyList<Computer<TConfig>> Computers, IReadOnlyList<SqlConfigProblem> Problems) LoadUncached(string path)
    {
        if (!File.Exists(path))
        {
            return ([], []);
        }

        ComputersFile<TConfig>? file;
        IReadOnlySet<string> repeated;
        IReadOnlyDictionary<string, string> twice;
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            var json = ConfigChecks.WithoutBom(bytes);
            repeated = ConnectionsFileEdit.RepeatedNames(json, Section);
            twice = ConnectionsFileEdit.RepeatedKeys(json, Section);
            file = JsonSerializer.Deserialize(json, _file);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            string detail = LogText.Excerpt(ex.Message);
            DiagnosticLog.Warn(Info.Category, SqlText.ConfigProblemLogLine(path, detail));
            return ([], [new SqlConfigProblem(path, ComputerText.UnreadableFile(detail), WholeFile: true)]);
        }

        // "computers": null reads as null past the non-nullable type (2026-10-22, review K1: it threw here, and the
        // startup encryption pass and every turn's tool build with it). SqliteConfigFile has had this check since 2026-10-04.
        if (file?.Computers is not { Count: > 0 })
        {
            return ([], []);
        }

        var computers = new List<Computer<TConfig>>(file.Computers.Count);
        var problems = ConfigChecks.UnknownFileKeys(file.Extra, Section).Select(p => new SqlConfigProblem(path, p)).ToList();
        foreach (var (name, config) in file.Computers)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                problems.Add(new SqlConfigProblem(path, ComputerText.BlankName));
                continue;
            }

            var entry = config ?? new TConfig();
            string trimmed = name.Trim();
            string? reason = repeated.Contains(trimmed) ? ComputerText.NamedTwice
                : twice.TryGetValue(trimmed, out string? key) ? ComputerText.KeyTwice(key)
                : ConfigChecks.UnknownKey(entry.Extra, _entry) ?? entry.Problem(trimmed, Info);
            if (reason is not null)
            {
                problems.Add(new SqlConfigProblem(SqlText.ConnectionSource(path, trimmed), reason, Name: trimmed));
                continue;
            }

            EncryptInPlace(path, trimmed, entry);
            computers.Add(new Computer<TConfig>(trimmed, entry, path));
        }

        return (computers, problems);
    }

    /// <summary>The profile's file over the home's (<paramref name="home"/> null = the profile's alone), <c>local</c> first. Never throws.</summary>
    public ComputerCatalog<TConfig> LoadCatalog(string profileDirectory, string? home)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        var (profile, profileProblems) = Load(ProfilePath(profileDirectory));
        IReadOnlyList<Computer<TConfig>> global = [];
        IReadOnlyList<SqlConfigProblem> globalProblems = [];
        if (home is not null && !string.Equals(Path.GetFullPath(ProfilePath(profileDirectory)), Path.GetFullPath(GlobalPath(home)), StringComparison.OrdinalIgnoreCase))
        {
            (global, globalProblems) = Load(GlobalPath(home));
        }

        // A profile entry that was refused still hides the home's of its name (D2): the profile meant its own.
        var names = new HashSet<string>(profile.Select(c => c.Name).Concat(profileProblems.Where(p => p.Name is not null).Select(p => p.Name!)), StringComparer.OrdinalIgnoreCase);
        var all = profile.Concat(global.Where(c => !names.Contains(c.Name))).ToList();
        var local = all.FirstOrDefault(c => c.IsLocal) ?? LocalOnly.Computers[0];
        return new ComputerCatalog<TConfig>([local with { Name = ComputerConfig.LocalName }, .. all.Where(c => !c.IsLocal)], [.. profileProblems, .. globalProblems], Info);
    }

    /// <summary>The safety net under the masked prompt: a plain-text <c>file</c> password encrypted and written back over that one value.</summary>
    private void EncryptInPlace(string path, string name, TConfig config)
    {
        if (!config.IsRunAs || config.InCredentialManager || string.IsNullOrEmpty(config.Password) || WindowsCredentials.IsProtected(config.Password))
        {
            return;
        }

        var encrypted = WindowsCredentials.Protect(config.Password);
        string? error = encrypted.Error ?? WritePassword(path, name, encrypted.Value!);
        if (error is null)
        {
            config.Password = encrypted.Value;
            DiagnosticLog.Info(Info.Category, SqlText.EncryptedLogLine(name, path));
        }
        else
        {
            DiagnosticLog.Warn(Info.Category, SqlText.EncryptFailedLogLine(name, path, error));
        }
    }

    public static string? WritePassword(string path, string name, string value) => ConnectionsFileEdit.WritePassword(path, name, value, Section, Noun);

    /// <summary>
    /// Adds computer <paramref name="name"/> to <paramref name="path"/> (the wizard); null on success. <paramref name="sealedPassword"/> (a
    /// <c>dpapi:</c> value) goes into the entry in the same write (2026-10-23, the admin review's H9: written after, a failed password save left
    /// the entry with none, the old one lost); null leaves <c>password</c> out.
    /// </summary>
    public string? AddComputer(string path, string name, TConfig config, string? sealedPassword = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(config);
        return ConnectionsFileEdit.AddConnection(path, name, EntryText(config, sealedPassword), EmptyText, Section, Noun);
    }

    /// <summary>Replaces computer <paramref name="oldName"/> with <paramref name="newName"/> and <paramref name="config"/> (and <paramref name="sealedPassword"/>, H9); null on success.</summary>
    public string? ReplaceComputer(string path, string oldName, string newName, TConfig config, string? sealedPassword = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(oldName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        ArgumentNullException.ThrowIfNull(config);
        return ConnectionsFileEdit.ReplaceConnection(path, oldName, newName, EntryText(config, sealedPassword), Section, Noun);
    }

    /// <summary>
    /// An entry as the file holds it, its password and unknown keys left out. Written from a copy (2026-10-23: the loaded entries are shared
    /// through <see cref="ConfigCache"/>, so the old blank-then-restore of <c>Password</c> on the caller's object raced a reader).
    /// </summary>
    public string EntryText(TConfig config, string? sealedPassword = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        var copy = JsonSerializer.Deserialize(JsonSerializer.Serialize(config, _entry), _entry)!;
        copy.Password = sealedPassword;
        copy.Extra = null;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            JsonSerializer.Serialize(writer, copy, _entry);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Every file of the family under <paramref name="home"/> read once, so the encryption runs over them all at startup. Never throws.</summary>
    public IReadOnlyList<string> EncryptAll(string home)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        var paths = new List<string> { GlobalPath(home) };
        try
        {
            paths.AddRange(Settings.Profiles.List(home).Select(name => ProfilePath(Settings.Profiles.Directory(home, name))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(Info.Category, ComputerText.ProfilesUnlistedLogLine(Info.FileName, Settings.Profiles.Root(home), LogText.Excerpt(ex.Message)));
        }

        var read = new List<string>();
        foreach (string path in paths.Where(File.Exists))
        {
            Load(path);
            read.Add(path);
        }

        return read;
    }

    /// <summary>Writes <see cref="EmptyText"/> to <paramref name="path"/> when no file is there; true when it wrote.</summary>
    public bool EnsureExists(string path) => ConnectionsFileEdit.EnsureExists(path, EmptyText);

    /// <summary>The password a <c>runas</c> computer signs in with; a missing or undecryptable one is the sentence that names the fix.</summary>
    public CredentialResult ResolvePassword(Computer<TConfig> computer)
    {
        ArgumentNullException.ThrowIfNull(computer);
        var config = computer.Config;
        if (!config.NeedsPassword)
        {
            return CredentialResult.Ok("");
        }

        if (config.InCredentialManager)
        {
            string target = config.CredentialTarget(Info, computer.Name);
            var read = WindowsCredentials.ReadGeneric(target);
            return read.NotFound ? CredentialResult.Failed(ComputerText.NoCredential(target, Info)) : ComputerConfig.CheckedUser(read, target, config.User);
        }

        string password = config.Password ?? "";
        if (password.Length == 0)
        {
            return CredentialResult.Failed(ComputerText.NoPassword(computer.Name, Info));
        }

        if (!WindowsCredentials.IsProtected(password))
        {
            return CredentialResult.Ok(password);
        }

        var decrypted = WindowsCredentials.Unprotect(password);
        return decrypted.Error is { } error ? CredentialResult.Failed(error.Replace(SqlText.SqlMenuPath, Info.MenuPath, StringComparison.Ordinal)) : decrypted;
    }

    /// <summary>Saves <paramref name="password"/> for <paramref name="computer"/> in its store; the status line's sentence either way.</summary>
    public (bool Saved, string Notice) SavePassword(Computer<TConfig> computer, string password)
    {
        ArgumentNullException.ThrowIfNull(computer);
        ArgumentNullException.ThrowIfNull(password);
        var config = computer.Config;
        if (config.InCredentialManager)
        {
            string target = config.CredentialTarget(Info, computer.Name);
            var written = WindowsCredentials.WriteGeneric(target, config.User?.Trim() ?? "", password);
            return written.Error is { } refused ? (false, SqlText.PasswordSaveFailed(computer.Name, refused)) : (true, SqlText.PasswordSavedToCredman(computer.Name, target));
        }

        var encrypted = WindowsCredentials.Protect(password);
        if (encrypted.Error is { } failed)
        {
            return (false, SqlText.PasswordSaveFailed(computer.Name, failed));
        }

        if (computer.Source is not { Length: > 0 } source)
        {
            return (false, SqlText.PasswordSaveFailed(computer.Name, "no " + Info.FileName + " holds it"));
        }

        return WritePassword(source, computer.Name, encrypted.Value!) is { } error
            ? (false, SqlText.PasswordSaveFailed(computer.Name, error))
            : (true, SqlText.PasswordSavedToFile(computer.Name, source));
    }
}
