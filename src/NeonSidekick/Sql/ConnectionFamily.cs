using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using NeonSidekick.Computers;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Sql;

/// <summary>
/// A connection entry that signs in with a user and a password kept as a SQL login's is: <see cref="Password"/> DPAPI-encrypted
/// in the file, or in Windows Credential Manager under <see cref="CredentialTarget"/>. What <see cref="ConnectionFamily{TConfig, TNamed}"/>
/// needs of an entry (2026-10-04).
/// </summary>
public interface ISignInConfig
{
    /// <summary>The account the connection signs in as.</summary>
    string? User { get; }

    /// <summary>The password under the <c>file</c> store: DPAPI (<c>dpapi:…</c>), or plain text until the first read encrypts it.</summary>
    string? Password { get; set; }

    /// <summary>Whether the password lives in Windows Credential Manager (<c>passwordStore: credman</c>).</summary>
    bool InCredentialManager { get; }

    /// <summary>
    /// Whether the entry signs in with a password at all (2026-10-24, the database review's Q11: <c>sql.json</c> joined the family, and its
    /// <c>windows</c> entries take none); every other family's always does.
    /// </summary>
    bool NeedsPassword => true;

    /// <summary>Why the entry cannot connect as written, or null.</summary>
    string? Problem { get; }

    /// <summary>The Credential Manager entry the password is under for connection <paramref name="name"/>.</summary>
    string CredentialTarget(string name);

    /// <summary>The entry's own <c>credential</c>, or null for the default target (DB10's prefix rule reads it).</summary>
    string? Credential { get; }

    /// <summary>
    /// Where the password is sent: the host (and port, or a data source) as the entry names it (2026-10-24, decision DB9: a stored credential
    /// records it, and a read for another is refused).
    /// </summary>
    string SignInHost { get; }

    /// <summary>The entry's keys the family does not know (2026-10-24, the database review's K2): any one refuses the entry.</summary>
    Dictionary<string, JsonElement>? Extra { get; set; }
}

/// <summary>A connection by its name and the file it came from, whatever its family.</summary>
public interface INamedConnection
{
    /// <summary>The name the model passes as <c>connection</c>.</summary>
    string Name { get; }

    /// <summary>The file the entry was read from.</summary>
    string Source { get; }
}

/// <summary>A connection by its name, its entry and the file it came from.</summary>
public interface INamedConnection<out TConfig> : INamedConnection
{
    /// <summary>The entry as the file has it.</summary>
    TConfig Config { get; }
}

/// <summary>The catalog operations every connections file shares: narrowing to the offered names, finding the one a call means. Pure.</summary>
public static class ConnectionCatalog
{
    /// <summary>
    /// The connections whose name is in <paramref name="names"/> (case-insensitive), in file order; null offers none, as an
    /// empty list does (2026-10-01, the user's call; it was "all" until then). A null or blank name in the list is skipped (2026-10-24, the
    /// database review's N11: a null threw in every tool build).
    /// </summary>
    public static List<TNamed> Offered<TNamed>(IReadOnlyList<TNamed> connections, IReadOnlyList<string>? names)
        where TNamed : INamedConnection
    {
        ArgumentNullException.ThrowIfNull(connections);
        var wanted = Wanted(names);
        return connections.Where(c => wanted.Contains(c.Name)).ToList();
    }

    /// <summary>The ticked names, trimmed, case ignored; null and blank ones skipped (N11).</summary>
    public static HashSet<string> Wanted(IReadOnlyList<string>? names) =>
        new((names ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The problems an offered catalog keeps (2026-10-24, the database review's J5: <c>*_connections</c> listed every entry's problem, so the
    /// names, file paths and sign-in details of entries the user had not ticked reached the model): those of a ticked name, and those of a
    /// whole file or of no entry. The menus read the whole catalog's.
    /// </summary>
    public static List<SqlConfigProblem> OfferedProblems(IReadOnlyList<SqlConfigProblem> problems, IReadOnlyList<string>? names)
    {
        ArgumentNullException.ThrowIfNull(problems);
        var wanted = Wanted(names);
        return problems.Where(p => p.Name is null || wanted.Contains(p.Name)).ToList();
    }

    /// <summary>
    /// The connection a call means: <paramref name="name"/> when given (case-insensitive), else the only one when just one is
    /// offered. Null (default) when nothing matches, or when several are offered and the call names none (2026-10-09, the
    /// user's call: the default-connection settings are gone, and a call must name its connection unless there is one).
    /// </summary>
    public static TNamed? Find<TNamed>(IReadOnlyList<TNamed> connections, string? name)
        where TNamed : class, INamedConnection
    {
        ArgumentNullException.ThrowIfNull(connections);
        if (!string.IsNullOrWhiteSpace(name))
        {
            return connections.FirstOrDefault(c => string.Equals(c.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        return connections.Count == 1 ? connections[0] : null;
    }
}

/// <summary>
/// One connections-file family that signs in with a password — <c>oracle.json</c>, <c>mysql.json</c>, <c>postgres.json</c>, and since
/// 2026-10-24 <c>sql.json</c> (the database review's Q11: its own loader and <c>SqlSecrets</c> were a fourth copy, already worded apart) — as one
/// piece of code over the family's facts (2026-10-04, the review's call: the three were copies with the names changed, and a fix to
/// the secrets or the loading had to be made three times). <c>{ "connections": { "&lt;name&gt;": { … } } }</c>, one in the profile's
/// folder and one in the home's (configs since 2026-10-16), the profile's winning by name (<see cref="SqlConfigFile"/>'s pair). A missing file is empty; a corrupt
/// or unreadable one is a problem and one warning, never a crash. A plain <c>password</c> is DPAPI-encrypted in place at the first read
/// that sees it; the byte-level edits are <see cref="ConnectionsFileEdit"/>'s. <c>sql.json</c>'s Windows sign-in and <c>runas</c> stay in
/// its entry's <c>Problem</c> and its access class; <c>sqlite.json</c> (no password) and <c>unc.json</c> (shares) differ enough to keep their own.
/// </summary>
/// <remarks>
/// Since 2026-10-24 (the database review's mechanism 1, the admin families' <see cref="ComputerFamily{TConfig}"/> carried over): a file is
/// read once per change (<see cref="ConfigCache"/>: K6, B15 — every catalog read parsed both files and ran the encryption again); an entry is
/// refused for a key it does not know (K2: a misspelled <c>schema</c> dropped the lock silently), a name given twice (D2), a key given twice, or a
/// <c>credential</c> outside <see cref="CredentialPrefix"/> under the <c>credman</c> store, the only one that reads it (D7, DB10); a file whose section is given twice is refused whole (N5); a profile's
/// refused entry hides the home's of the same name (D1: a broken read-only lock-down fell back to the home's readwrite one); every plain
/// password in the file is encrypted, an entry with a problem's or a windows/credman entry's leftover too (N1, N2), and only over the very
/// value read (A16); a typed value that merely starts <c>dpapi:</c> is plain text (D10); the entry text is written from a copy (B14) and a
/// sealed password rides the same write (B13).
/// </remarks>
public sealed class ConnectionFamily<TConfig, TNamed>
    where TConfig : class, ISignInConfig
    where TNamed : class, INamedConnection<TConfig>
{
    /// <summary>The key the connections sit under.</summary>
    public const string Section = ConnectionsFileEdit.DefaultSection;

    /// <summary>The file's name in a profile's <c>configs</c> folder and in the home (<c>mysql.json</c>).</summary>
    public required string FileName { get; init; }

    /// <summary>The log category of everything in the family (<c>MySql</c>).</summary>
    public required string Category { get; init; }

    /// <summary>What a fresh file holds.</summary>
    public required string EmptyText { get; init; }

    /// <summary>Where on <c>/tools</c> the rows that fix a password are (<c>/tools › Databases › MySQL</c>; <c>the MySQL tab</c> until 2026-10-09), put in for <see cref="SqlText.SqlMenuPath"/> in <see cref="WindowsCredentials"/>' wording.</summary>
    public required string Tab { get; init; }

    /// <summary>Why a <c>null</c> entry cannot connect (<c>MySqlText.NoHost</c>).</summary>
    public required string NullEntry { get; init; }

    /// <summary>
    /// The file's UTF-8 text as its connections by name, in file order, and the keys beside <c>connections</c>; throws what
    /// <see cref="JsonSerializer"/> throws.
    /// </summary>
    public required Func<byte[], (IReadOnlyDictionary<string, TConfig?>? Entries, IReadOnlyDictionary<string, JsonElement>? Extra)> Deserialize { get; init; }

    /// <summary>The source-generated metadata an added entry is written with.</summary>
    public required JsonTypeInfo<TConfig> EntryInfo { get; init; }

    /// <summary>A named connection from its name, entry and file.</summary>
    public required Func<string, TConfig, string, TNamed> Named { get; init; }

    /// <summary>The sentence for a connection with no password in its file (<c>MySqlText.NoPassword</c>).</summary>
    public required Func<string, string> NoPassword { get; init; }

    /// <summary>The sentence for a Credential Manager entry that is not there (<c>MySqlText.NoCredential</c>).</summary>
    public required Func<string, string> NoCredential { get; init; }

    /// <summary>The log line for a profiles folder that could not be listed (<c>MySqlText.ProfilesUnlistedLogLine</c>).</summary>
    public required Func<string, string, string> ProfilesUnlistedLogLine { get; init; }

    /// <summary>Where every <c>credential</c> of the family sits (<c>NeonSidekick/mysql/</c>; DB10).</summary>
    public required string CredentialPrefix { get; init; }

    /// <summary>The profile's file: <c>&lt;profile&gt;\configs\&lt;FileName&gt;</c> (<c>configs</c> since 2026-10-16).</summary>
    public string ProfilePath(string profileDirectory) => NeonSidekick.Settings.Profiles.ConfigPath(profileDirectory, FileName);

    /// <summary>The home's file: <c>&lt;home&gt;\configs\&lt;FileName&gt;</c> (configs since 2026-10-16), every profile's.</summary>
    public string GlobalPath(string home) => NeonSidekick.Settings.Profiles.HomeConfigPath(home, FileName);

    /// <summary>
    /// Reads <paramref name="path"/>: the usable connections in file order and a problem per entry that cannot be used — for a file that is not
    /// JSON or cannot be read, one problem for the file and one warning. A missing file is empty. Read again only once the file changes
    /// (<see cref="ConfigCache"/>); the lists are the caller's own. Never throws.
    /// </summary>
    public (List<TNamed> Connections, List<SqlConfigProblem> Problems) Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var (connections, problems) = ConfigCache.Get(FileName, path, () => LoadUncached(path));
        return ([.. connections], [.. problems]);
    }

    private (IReadOnlyList<TNamed> Connections, IReadOnlyList<SqlConfigProblem> Problems) LoadUncached(string path)
    {
        if (!File.Exists(path))
        {
            return ([], []);
        }

        IReadOnlyDictionary<string, TConfig?>? entries;
        IReadOnlyDictionary<string, JsonElement>? extra;
        IReadOnlySet<string> repeated;
        IReadOnlyDictionary<string, string> twice;
        string? section;
        try
        {
            byte[] utf8 = ConnectionsFileEdit.ForEdit(File.ReadAllBytes(path), out _);
            byte[] json = ConfigChecks.WithoutBom(utf8).ToArray();
            repeated = ConnectionsFileEdit.RepeatedNames(json, Section);
            twice = ConnectionsFileEdit.RepeatedKeys(json, Section);
            section = ConnectionsFileEdit.RepeatedSection(json, Section);
            (entries, extra) = section is null ? Deserialize(json) : (null, null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            string detail = LogText.Excerpt(ex.Message);
            DiagnosticLog.Warn(Category, SqlText.ConfigProblemLogLine(path, detail));
            return ([], [new SqlConfigProblem(path, SqlText.UnreadableFile(detail), WholeFile: true)]);
        }

        if (section is not null)
        {
            return ([], [new SqlConfigProblem(path, SqlText.SectionTwice(section), WholeFile: true)]);
        }

        var problems = ConfigChecks.UnknownFileKeys(extra, Section).Select(p => new SqlConfigProblem(path, p)).ToList();
        if (entries is null || entries.Count == 0)
        {
            return ([], problems);
        }

        var connections = new List<TNamed>(entries.Count);
        foreach (var (name, config) in entries)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                problems.Add(new SqlConfigProblem(path, SqlText.BlankName));
                continue;
            }

            string trimmed = name.Trim();
            bool named = repeated.Contains(trimmed);
            string? key = twice.TryGetValue(trimmed, out string? k) ? k : null;
            string? reason = config is null ? NullEntry
                : named ? ComputerText.NamedTwice
                : key is not null ? ComputerText.KeyTwice(key)
                : ConfigChecks.UnknownKey(config.Extra, EntryInfo)
                ?? config.Problem
                ?? (config.InCredentialManager ? ComputerConfig.CredentialProblem(config.Credential, CredentialPrefix) : null);

            // Every plain password is encrypted, a refused entry's too (N1, N2) — but not one given twice, whose splice could land in the twin.
            if (config is not null && !named && key is null)
            {
                EncryptInPlace(path, trimmed, config);
            }

            if (reason is not null)
            {
                problems.Add(new SqlConfigProblem(SqlText.ConnectionSource(path, trimmed), reason, Name: trimmed, Entry: config));
                continue;
            }

            connections.Add(Named(trimmed, config!, path));
        }

        return (connections, problems);
    }

    /// <summary>
    /// The profile's file over the home's (<paramref name="home"/> null = the profile's alone): a name in both is the profile's, and a profile
    /// entry that was refused still hides the home's of its name (D1: the profile meant its own).
    /// </summary>
    public (List<TNamed> Connections, List<SqlConfigProblem> Problems) LoadCatalog(string profileDirectory, string? home)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        var profile = Load(ProfilePath(profileDirectory));
        if (home is null || SamePath(profileDirectory, home))
        {
            return profile;
        }

        var global = Load(GlobalPath(home));
        var names = new HashSet<string>(profile.Connections.Select(c => c.Name).Concat(profile.Problems.Where(p => p.Name is not null).Select(p => p.Name!)), StringComparer.OrdinalIgnoreCase);
        return ([.. profile.Connections, .. global.Connections.Where(c => !names.Contains(c.Name))], [.. profile.Problems, .. global.Problems]);
    }

    /// <summary>
    /// Every entry both files name, as read — usable, refused, and the home's a profile entry shadows alike (2026-10-24, the database review's
    /// H9: the shell police and the SQLite guard built from <see cref="LoadCatalog"/>, which drops a shadowed home entry, so its host or file
    /// went unguarded). The police's and the guard's, never a tool's.
    /// </summary>
    public IReadOnlyList<TConfig> Everything(string profileDirectory, string? home)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        var files = new List<(List<TNamed> Connections, List<SqlConfigProblem> Problems)> { Load(ProfilePath(profileDirectory)) };
        if (home is not null && !SamePath(profileDirectory, home))
        {
            files.Add(Load(GlobalPath(home)));
        }

        return files.SelectMany(f => f.Connections.Select(c => c.Config).Concat(f.Problems.Select(p => p.Entry).OfType<TConfig>())).ToList();
    }

    private bool SamePath(string profileDirectory, string home) =>
        string.Equals(Path.GetFullPath(ProfilePath(profileDirectory)), Path.GetFullPath(GlobalPath(home)), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The safety net under the tab's masked prompt, <see cref="SqlConfigFile"/>'s: a plain-text <c>password</c> is DPAPI-encrypted and written
    /// back over that one value at the first read that sees it, the file's comments and layout kept — only while the file still holds that
    /// value (A16). A file that cannot be written keeps working with the plain value, and says so once per change of the file in the log.
    /// </summary>
    private void EncryptInPlace(string path, string name, TConfig config)
    {
        string? plain = config.Password;
        if (string.IsNullOrEmpty(plain) || WindowsCredentials.IsSealed(plain) || !WindowsCredentials.CanProtect)
        {
            return;
        }

        var encrypted = WindowsCredentials.Protect(plain);
        string? error = encrypted.Error ?? ConnectionsFileEdit.WritePassword(path, name, encrypted.Value!, expected: plain);
        if (error is null)
        {
            config.Password = encrypted.Value;
            DiagnosticLog.Info(Category, SqlText.EncryptedLogLine(name, path));
        }
        else if (error != ConnectionsFileEdit.PasswordChanged)
        {
            DiagnosticLog.Warn(Category, SqlText.EncryptFailedLogLine(name, path, error));
        }
    }

    /// <summary>
    /// Adds connection <paramref name="name"/> to <paramref name="path"/> (the tab's add-connection wizard), the file's comments and
    /// layout kept (<see cref="ConnectionsFileEdit.AddConnection"/>; a missing file is made with <see cref="EmptyText"/> first). The
    /// entry is <paramref name="config"/> through <see cref="EntryInfo"/>, its <c>password</c> <paramref name="sealedPassword"/> (a <c>dpapi:</c>
    /// value, B13: in the same write) or left out. Null on success, else why not.
    /// </summary>
    public string? AddConnection(string path, string name, TConfig config, string? sealedPassword = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(config);
        return ConnectionsFileEdit.AddConnection(path, name, EntryText(config, sealedPassword), EmptyText);
    }

    /// <summary>
    /// Replaces connection <paramref name="oldName"/> in <paramref name="path"/> with <paramref name="newName"/> and
    /// <paramref name="config"/> (2026-10-05, the tab's wizard editing an entry; <see cref="ConnectionsFileEdit.ReplaceConnection"/>), the
    /// <c>password</c> <paramref name="sealedPassword"/> in the same write (B13: written after, a failed save left the entry with none) or left
    /// out. Null on success, else why not.
    /// </summary>
    public string? ReplaceConnection(string path, string oldName, string newName, TConfig config, string? sealedPassword = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(oldName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        ArgumentNullException.ThrowIfNull(config);
        return ConnectionsFileEdit.ReplaceConnection(path, oldName, newName, EntryText(config, sealedPassword));
    }

    /// <summary>
    /// One entry as the file holds it: <see cref="EntryInfo"/>'s keys, indented, the relaxed escaping, its unknown keys left out and its
    /// <c>password</c> <paramref name="sealedPassword"/> or none. Written from a copy (2026-10-24, the database review's B14: the loaded entries
    /// are shared through <see cref="ConfigCache"/>, and blanking then restoring the caller's <c>Password</c> raced a reader).
    /// </summary>
    public string EntryText(TConfig config, string? sealedPassword = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        var copy = JsonSerializer.Deserialize(JsonSerializer.Serialize(config, EntryInfo), EntryInfo)!;
        copy.Password = sealedPassword;
        copy.Extra = null;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            JsonSerializer.Serialize(writer, copy, EntryInfo);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Every file of the family under <paramref name="home"/> read once — the home's and each profile's — so <see cref="Load"/>'s
    /// encryption runs over them all at startup (<see cref="SqlConfigFile.EncryptAll"/>'s part). The files read, in order; a profiles
    /// folder that cannot be listed is a warning and the home's file alone. Never throws.
    /// </summary>
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
            DiagnosticLog.Warn(Category, ProfilesUnlistedLogLine(Settings.Profiles.Root(home), LogText.Excerpt(ex.Message)));
        }

        var read = new List<string>();
        foreach (string path in paths.Where(File.Exists))
        {
            Load(path);
            read.Add(path);
        }

        return read;
    }

    /// <summary>
    /// The password <paramref name="connection"/> signs in with: under <c>passwordStore: credman</c> the Windows Credential Manager
    /// entry <see cref="ISignInConfig.CredentialTarget"/> — its stored user the entry's (I14), its recorded host the entry's (DB9); under
    /// <c>file</c> (the default) the <c>password</c> decrypted — or, while it is still plain text (a file the app could not rewrite), as it
    /// stands. A missing or undecryptable password is the sentence that names the fix, never a throw.
    /// </summary>
    public CredentialResult Resolve(TNamed connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var config = connection.Config;
        if (!config.NeedsPassword)
        {
            return CredentialResult.Ok("");
        }

        if (config.InCredentialManager)
        {
            string target = config.CredentialTarget(connection.Name);
            var read = WindowsCredentials.ReadGeneric(target);
            if (read.NotFound)
            {
                return CredentialResult.Failed(NoCredential(target));
            }

            // The stored account must be the entry's (2026-10-24, review I14: a changed user signed in with the old account's password, the
            // admin families' DA2 rule), and a password saved for one host is never sent to another (DB9).
            read = ComputerConfig.CheckedUser(read, target, config.User);
            // A custom credential several entries may share (two replicas' one service account) is tied to no host (2026-10-24, the review of
            // these fixes: one save's mark refused every other entry using it); the app's own per-entry target is.
            bool shared = !string.IsNullOrWhiteSpace(config.Credential);
            if (!shared && read.Error is null && read.Host is { } recorded && !CredentialMark.SameHost(recorded, config.SignInHost))
            {
                return CredentialResult.Failed(SqlText.CredentialHostMismatch(target, recorded, config.SignInHost, Tab));
            }

            return read;
        }

        string password = config.Password ?? "";
        if (password.Length == 0)
        {
            return CredentialResult.Failed(NoPassword(connection.Name));
        }

        if (!WindowsCredentials.IsSealed(password))
        {
            return CredentialResult.Ok(password);
        }

        // WindowsCredentials words its failure for sql.json; the fix it names is on this family's tab.
        var decrypted = WindowsCredentials.Unprotect(password);
        return decrypted.Error is { } error ? CredentialResult.Failed(error.Replace(SqlText.SqlMenuPath, Tab, StringComparison.Ordinal)) : decrypted;
    }

    /// <summary>
    /// Saves <paramref name="password"/> for <paramref name="connection"/> in its store (the tab's masked prompt and the wizard): under
    /// <c>credman</c> the Credential Manager entry, written with the connection's user and its host (DB9); under <c>file</c> DPAPI-encrypted
    /// into the file the connection came from. The status line's sentence either way.
    /// </summary>
    public (bool Saved, string Notice) Save(TNamed connection, string password)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(password);
        var config = connection.Config;
        if (config.InCredentialManager)
        {
            string target = config.CredentialTarget(connection.Name);
            var written = WindowsCredentials.WriteGeneric(target, config.User?.Trim() ?? "", password, string.IsNullOrWhiteSpace(config.Credential) ? config.SignInHost : null);
            return written.Error is { } refused ? (false, SqlText.PasswordSaveFailed(connection.Name, refused)) : (true, SqlText.PasswordSavedToCredman(connection.Name, target));
        }

        var encrypted = WindowsCredentials.Protect(password);
        if (encrypted.Error is { } failed)
        {
            return (false, SqlText.PasswordSaveFailed(connection.Name, failed));
        }

        return ConnectionsFileEdit.WritePassword(connection.Source, connection.Name, encrypted.Value!) is { } error
            ? (false, SqlText.PasswordSaveFailed(connection.Name, error))
            : (true, SqlText.PasswordSavedToFile(connection.Name, connection.Source));
    }
}
