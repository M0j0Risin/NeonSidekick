using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using NeonSidekick.Diagnostics;
using NeonSidekick.Sql;

namespace NeonSidekick.Computers;

/// <summary>A family's file: <c>{ "computers": { "&lt;name&gt;": { … } } }</c>.</summary>
public sealed class ComputersFile<TConfig> where TConfig : ComputerConfig
{
    /// <summary>The computers by name, in file order.</summary>
    public Dictionary<string, TConfig?> Computers { get; set; } = new(StringComparer.Ordinal);
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
/// A family's file and secrets (2026-10-15, the admin-tools plan: <c>EventLogConfigFile</c> and <c>EventLogSecrets</c> made generic): one file
/// in the profile's folder and one in the home, the profile's winning by name; a missing file is empty, a corrupt one a problem and one
/// warning; a plain <c>password</c> DPAPI-encrypted in place at the first read that sees it (<see cref="ConnectionsFileEdit"/> under
/// <c>computers</c>). Each family hands in its source-generated <see cref="JsonTypeInfo{T}"/>s, so there is no reflection path. Read afresh at
/// every call. Not carried by <c>/profile push|pull</c>, not copied by <c>/profile add</c>.
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

    public string ProfilePath(string profileDirectory) => Path.Combine(profileDirectory, Info.FileName);

    public string GlobalPath(string home) => Path.Combine(home, Info.FileName);

    /// <summary>A catalog of <c>local</c> alone, as it is with no file.</summary>
    public ComputerCatalog<TConfig> LocalOnly => new([new Computer<TConfig>(ComputerConfig.LocalName, new TConfig())], [], Info);

    /// <summary>Reads <paramref name="path"/>: the usable entries in file order and a problem per entry that cannot be used. Never throws.</summary>
    public (IReadOnlyList<Computer<TConfig>> Computers, IReadOnlyList<SqlConfigProblem> Problems) Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return ([], []);
        }

        ComputersFile<TConfig>? file;
        try
        {
            file = JsonSerializer.Deserialize(File.ReadAllText(path), _file);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            string detail = LogText.Excerpt(ex.Message);
            DiagnosticLog.Warn(Info.Category, SqlText.ConfigProblemLogLine(path, detail));
            return ([], [new SqlConfigProblem(path, ComputerText.UnreadableFile(detail), WholeFile: true)]);
        }

        if (file is null || file.Computers.Count == 0)
        {
            return ([], []);
        }

        var computers = new List<Computer<TConfig>>(file.Computers.Count);
        var problems = new List<SqlConfigProblem>();
        foreach (var (name, config) in file.Computers)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                problems.Add(new SqlConfigProblem(path, ComputerText.BlankName));
                continue;
            }

            var entry = config ?? new TConfig();
            string trimmed = name.Trim();
            if (entry.Problem(trimmed, Info) is { } reason)
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

        var names = new HashSet<string>(profile.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
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

    /// <summary>Adds computer <paramref name="name"/> to <paramref name="path"/> (the wizard), its <c>password</c> left out; null on success.</summary>
    public string? AddComputer(string path, string name, TConfig config)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(config);
        return ConnectionsFileEdit.AddConnection(path, name, EntryText(config), EmptyText, Section, Noun);
    }

    /// <summary>Replaces computer <paramref name="oldName"/> with <paramref name="newName"/> and <paramref name="config"/>; null on success.</summary>
    public string? ReplaceComputer(string path, string oldName, string newName, TConfig config)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(oldName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        ArgumentNullException.ThrowIfNull(config);
        return ConnectionsFileEdit.ReplaceConnection(path, oldName, newName, EntryText(config), Section, Noun);
    }

    /// <summary>An entry as the file holds it, its password left out.</summary>
    public string EntryText(TConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        string? password = config.Password;
        config.Password = null;
        try
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                JsonSerializer.Serialize(writer, config, _entry);
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
        finally
        {
            config.Password = password;
        }
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
            return read.NotFound ? CredentialResult.Failed(ComputerText.NoCredential(target, Info)) : read;
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
