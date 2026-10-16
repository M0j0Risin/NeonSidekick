using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using NeonSidekick.Diagnostics;
using NeonSidekick.Sql;

namespace NeonSidekick.EventLog;

/// <summary>
/// What <see cref="EventLogConfigFile.LoadCatalog"/> found (2026-10-15, <c>UncCatalog</c>'s shape): <c>local</c> first — built in, with a
/// <c>"local"</c> entry's <c>logs</c>, <c>detail</c> and <c>description</c> over it — then the usable computers (the profile's first, then the
/// home's the profile does not shadow, each in file order), and the problems, never a throw.
/// </summary>
public sealed record EventLogCatalog(IReadOnlyList<EventLogComputer> Computers, IReadOnlyList<SqlConfigProblem> Problems, int Hidden = 0)
{
    /// <summary>A catalog of <c>local</c> alone, as it is with no file.</summary>
    public static readonly EventLogCatalog LocalOnly = new([new EventLogComputer(EventLogText.LocalName, new EventLogComputerConfig())], []);

    /// <summary>
    /// The names <c>Event log computers offered</c> ticks (2026-10-15): null (never set) is <c>local</c> alone, an empty list none — so a
    /// fresh profile reads this PC once the switch is on, and a remote computer is never reached until ticked.
    /// </summary>
    public static IReadOnlyList<string> OfferedNames(IReadOnlyList<string>? offered) => offered ?? [EventLogText.LocalName];

    /// <summary>The catalog narrowed to the ticked computers (case ignored), in its order, <see cref="Hidden"/> counting the rest; problems kept. Pure.</summary>
    public EventLogCatalog Offered(IReadOnlyList<string>? names)
    {
        var wanted = new HashSet<string>(OfferedNames(names).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
        var kept = Computers.Where(c => wanted.Contains(c.Name)).ToList();
        return this with { Computers = kept, Hidden = Hidden + Computers.Count - kept.Count };
    }

    /// <summary>The ticked computer named <paramref name="name"/> (case ignored); null for none.</summary>
    public EventLogComputer? Named(string name) =>
        Computers.FirstOrDefault(c => string.Equals(c.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The computer a call means (2026-10-15, the plan's pick): <paramref name="name"/> when given; omitted, <c>local</c> when ticked, else
    /// the only ticked one. Null with the refusal: an unknown or unticked name (naming the ticked ones), several and none named, none ticked.
    /// </summary>
    public (EventLogComputer? Computer, string? Error) Find(string? name)
    {
        string offered = global::NeonSidekick.Computers.ComputerText.Names(Computers.Select(c => c.Name));
        if (!string.IsNullOrWhiteSpace(name))
        {
            return Named(name) is { } found ? (found, null) : (null, EventLogText.UnknownComputer(name.Trim(), offered));
        }

        if (Computers.Count == 0)
        {
            return (null, EventLogText.NoComputerTicked);
        }

        if (Computers.FirstOrDefault(c => c.IsLocal) is { } local)
        {
            return (local, null);
        }

        return Computers.Count == 1 ? (Computers[0], null) : (null, EventLogText.ComputerNeeded(offered));
    }

    /// <summary>
    /// Whether <paramref name="logs"/> (an entry's allow-list) lets <paramref name="channel"/> be read: absent allows every log, an empty list
    /// none; each pattern is matched case ignored, <c>*</c> any run of characters but <c>/</c> (<c>Microsoft-Windows-*/Operational</c>,
    /// <c>*/*</c> for every slashed name). Pure.
    /// </summary>
    public static bool Allows(IReadOnlyList<string>? logs, string channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        // The pattern rule is Computers/NameGlob's since later on 2026-10-15 (the admin-tools plan), '/' the separator.
        return logs is null || global::NeonSidekick.Computers.NameGlob.Any(logs, channel, '/');
    }
}

/// <summary>
/// <c>eventlog.json</c> (2026-10-15, the event-log plan, <c>Unc/UncConfigFile</c>'s shape): <c>{ "computers": { "&lt;name&gt;": { … } } }</c>,
/// one in the profile's <c>configs</c> folder and one in the home, the profile's winning by name. A missing file is empty; a corrupt or unreadable one is a
/// problem and one warning, never a crash. Read afresh at every call, so an edit in the editor counts on the next one. A plain <c>password</c>
/// is DPAPI-encrypted in place at the first read that sees it (<see cref="ConnectionsFileEdit"/> under <c>computers</c>). Not carried by
/// <c>/profile push|pull</c>, not copied by <c>/profile add</c> (as <c>unc.json</c>).
/// </summary>
public sealed class EventLogConfigFile
{
    public const string FileName = "eventlog.json";

    /// <summary>The key the computers sit under.</summary>
    public const string Section = "computers";

    /// <summary>What a computer is called in the file edits' refusals.</summary>
    public const string Noun = "computer";

    /// <summary>
    /// What a fresh file holds (2026-10-15): no computers, and a commented example of each kind — each one valid once its <c>//</c> are
    /// removed (pinned by a test) — with the Credential Manager line for a runas password. Pinned.
    /// </summary>
    public static string EmptyText =>
        "{\n" +
        "  // One entry per computer. Its name is what the model passes as \"computer\", and what /eventlog computer:<name> reads.\n" +
        "  // \"local\" is this PC, built in: its entry may only narrow its \"logs\", set its \"detail\" or describe it.\n" +
        "  // A computer is read only once it is ticked in Event log computers offered (/tools › Admin › EventLog).\n" +
        "  // Remove the leading // from an example to use it, and put it inside \"computers\" below.\n" +
        "  //\n" +
        "  // This PC, narrowed to four logs:\n" +
        "  // \"local\": { \"logs\": [\"Application\", \"System\", \"Setup\", \"Microsoft-Windows-*/Operational\"] },\n" +
        "  //\n" +
        "  // A server read as you, its events in full for the main chat:\n" +
        "  // \"fs01\": { \"host\": \"fs01.corp.example\", \"description\": \"file server\", \"detail\": \"full\" },\n" +
        "  //\n" +
        "  // As another account (it needs the Event Log Readers group there), the password in " + SqlText.CredentialStore + " as\n" +
        "  // NeonSidekick/eventlog/<name> — set it with Event log set password on /tools › Admin › EventLog, or:\n" +
        "  // " + SqlText.CredentialCommand("NeonSidekick/eventlog/dc01", "CORP\\svc-logreader") + "\n" +
        "  // \"dc01\": { \"host\": \"dc01\", \"auth\": \"runas\", \"user\": \"CORP\\\\svc-logreader\", \"passwordStore\": \"credman\",\n" +
        "  //   \"logs\": [\"Security\", \"System\", \"Directory Service\"] },\n" +
        "  //\n" +
        "  // auth: windows (as you, the default) or runas; authMethod: default, negotiate, kerberos or ntlm; detail: redacted (the\n" +
        "  // default: secret-like values hidden from the model) or full; logs: the logs allowed, * any run but / (absent: all).\n" +
        "  // A runas password typed here in plain text is encrypted (\"dpapi:…\") at the next read. The remote computer needs the\n" +
        "  // \"Remote Event Log Management\" firewall rule on.\n" +
        "  \"computers\": {}\n" +
        "}\n";

    /// <summary>The computers by name, in file order.</summary>
    public Dictionary<string, EventLogComputerConfig?> Computers { get; set; } = new(StringComparer.Ordinal);

    public static string ProfilePath(string profileDirectory) => NeonSidekick.Settings.Profiles.ConfigPath(profileDirectory, FileName);

    public static string GlobalPath(string home) => Path.Combine(home, FileName);

    /// <summary>
    /// Reads <paramref name="path"/>: the usable entries in file order (a <c>local</c> entry among them, its overrides) and a problem per entry
    /// that cannot be used or has a blank name; for a file that is not JSON or cannot be read, one problem and one warning. Never throws.
    /// </summary>
    public static (IReadOnlyList<EventLogComputer> Computers, IReadOnlyList<SqlConfigProblem> Problems) Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return ([], []);
        }

        EventLogConfigFile? file;
        try
        {
            file = JsonSerializer.Deserialize(File.ReadAllText(path), EventLogJsonContext.Default.EventLogConfigFile);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            string detail = LogText.Excerpt(ex.Message);
            DiagnosticLog.Warn(EventLogText.Category, SqlText.ConfigProblemLogLine(path, detail));
            return ([], [new SqlConfigProblem(path, EventLogText.UnreadableFile(detail), WholeFile: true)]);
        }

        if (file is null || file.Computers.Count == 0)
        {
            return ([], []);
        }

        var computers = new List<EventLogComputer>(file.Computers.Count);
        var problems = new List<SqlConfigProblem>();
        foreach (var (name, config) in file.Computers)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                problems.Add(new SqlConfigProblem(path, EventLogText.BlankName));
                continue;
            }

            var entry = config ?? new EventLogComputerConfig();
            string trimmed = name.Trim();
            if (entry.Problem(trimmed) is { } reason)
            {
                problems.Add(new SqlConfigProblem(SqlText.ConnectionSource(path, trimmed), reason, Name: trimmed));
                continue;
            }

            EncryptInPlace(path, trimmed, entry);
            computers.Add(new EventLogComputer(trimmed, entry, path));
        }

        return (computers, problems);
    }

    /// <summary>
    /// The profile's file over the home's (<paramref name="home"/> null = the profile's alone), <c>local</c> first: a name in both is the
    /// profile's; <c>local</c>'s overrides likewise. Never throws.
    /// </summary>
    public static EventLogCatalog LoadCatalog(string profileDirectory, string? home)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        var (profile, profileProblems) = Load(ProfilePath(profileDirectory));
        IReadOnlyList<EventLogComputer> global = [];
        IReadOnlyList<SqlConfigProblem> globalProblems = [];
        if (home is not null && !string.Equals(Path.GetFullPath(ProfilePath(profileDirectory)), Path.GetFullPath(GlobalPath(home)), StringComparison.OrdinalIgnoreCase))
        {
            (global, globalProblems) = Load(GlobalPath(home));
        }

        var names = new HashSet<string>(profile.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        var all = profile.Concat(global.Where(c => !names.Contains(c.Name))).ToList();
        var local = all.FirstOrDefault(c => c.IsLocal) ?? EventLogCatalog.LocalOnly.Computers[0];
        return new EventLogCatalog([local with { Name = EventLogText.LocalName }, .. all.Where(c => !c.IsLocal)], [.. profileProblems, .. globalProblems]);
    }

    /// <summary>The safety net under the masked prompt (<c>UncConfigFile</c>'s): a plain-text <c>file</c> password encrypted and written back over that one value.</summary>
    private static void EncryptInPlace(string path, string name, EventLogComputerConfig config)
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
            DiagnosticLog.Info(EventLogText.Category, SqlText.EncryptedLogLine(name, path));
        }
        else
        {
            DiagnosticLog.Warn(EventLogText.Category, SqlText.EncryptFailedLogLine(name, path, error));
        }
    }

    public static string? WritePassword(string path, string name, string value) => ConnectionsFileEdit.WritePassword(path, name, value, Section, Noun);

    /// <summary>Adds computer <paramref name="name"/> to <paramref name="path"/> (the wizard), its <c>password</c> left out; null on success.</summary>
    public static string? AddComputer(string path, string name, EventLogComputerConfig config)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(config);
        return ConnectionsFileEdit.AddConnection(path, name, EntryText(config), EmptyText, Section, Noun);
    }

    /// <summary>Replaces computer <paramref name="oldName"/> with <paramref name="newName"/> and <paramref name="config"/>; null on success.</summary>
    public static string? ReplaceComputer(string path, string oldName, string newName, EventLogComputerConfig config)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(oldName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        ArgumentNullException.ThrowIfNull(config);
        return ConnectionsFileEdit.ReplaceConnection(path, oldName, newName, EntryText(config), Section, Noun);
    }

    private static string EntryText(EventLogComputerConfig config)
    {
        string? password = config.Password;
        config.Password = null;
        try
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                JsonSerializer.Serialize(writer, config, EventLogJsonContext.Default.EventLogComputerConfig);
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
        finally
        {
            config.Password = password;
        }
    }

    /// <summary>Every <c>eventlog.json</c> under <paramref name="home"/> read once, so the encryption runs over them all at startup. Never throws.</summary>
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
            DiagnosticLog.Warn(EventLogText.Category, EventLogText.ProfilesUnlistedLogLine(Settings.Profiles.Root(home), LogText.Excerpt(ex.Message)));
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
    public static bool EnsureExists(string path) => ConnectionsFileEdit.EnsureExists(path, EmptyText);
}

/// <summary>
/// The password a <c>runas</c> computer signs in with (2026-10-15, <c>UncSecrets</c>' twin): under <c>credman</c> the Credential Manager
/// entry, under <c>file</c> the decrypted <c>password</c> (or plain text the app could not rewrite). A missing or undecryptable password
/// is the sentence that names the fix, never a throw.
/// </summary>
public static class EventLogSecrets
{
    public static CredentialResult Resolve(EventLogComputer computer)
    {
        ArgumentNullException.ThrowIfNull(computer);
        var config = computer.Config;
        if (!config.NeedsPassword)
        {
            return CredentialResult.Ok("");
        }

        if (config.InCredentialManager)
        {
            string target = config.CredentialTarget(computer.Name);
            var read = WindowsCredentials.ReadGeneric(target);
            return read.NotFound ? CredentialResult.Failed(EventLogText.NoCredential(target)) : read;
        }

        string password = config.Password ?? "";
        if (password.Length == 0)
        {
            return CredentialResult.Failed(EventLogText.NoPassword(computer.Name));
        }

        if (!WindowsCredentials.IsProtected(password))
        {
            return CredentialResult.Ok(password);
        }

        var decrypted = WindowsCredentials.Unprotect(password);
        return decrypted.Error is { } error ? CredentialResult.Failed(error.Replace(SqlText.SqlMenuPath, "/tools › Admin › EventLog", StringComparison.Ordinal)) : decrypted;
    }

    /// <summary>Saves <paramref name="password"/> for <paramref name="computer"/> in its store; the status line's sentence either way.</summary>
    public static (bool Saved, string Notice) Save(EventLogComputer computer, string password)
    {
        ArgumentNullException.ThrowIfNull(computer);
        ArgumentNullException.ThrowIfNull(password);
        var config = computer.Config;
        if (config.InCredentialManager)
        {
            string target = config.CredentialTarget(computer.Name);
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
            return (false, SqlText.PasswordSaveFailed(computer.Name, "no eventlog.json holds it"));
        }

        return EventLogConfigFile.WritePassword(source, computer.Name, encrypted.Value!) is { } error
            ? (false, SqlText.PasswordSaveFailed(computer.Name, error))
            : (true, SqlText.PasswordSavedToFile(computer.Name, source));
    }
}
