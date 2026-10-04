using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
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

    /// <summary>Why the entry cannot connect as written, or null.</summary>
    string? Problem { get; }

    /// <summary>The Credential Manager entry the password is under for connection <paramref name="name"/>.</summary>
    string CredentialTarget(string name);
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
    /// empty list does (2026-10-01, the user's call; it was "all" until then).
    /// </summary>
    public static List<TNamed> Offered<TNamed>(IReadOnlyList<TNamed> connections, IReadOnlyList<string>? names)
        where TNamed : INamedConnection
    {
        ArgumentNullException.ThrowIfNull(connections);
        var wanted = new HashSet<string>((names ?? []).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
        return connections.Where(c => wanted.Contains(c.Name)).ToList();
    }

    /// <summary>
    /// The connection a call means: <paramref name="name"/> when given (case-insensitive), else <paramref name="defaultName"/>
    /// when that names one, else the first. Null (default) when nothing matches.
    /// </summary>
    public static TNamed? Find<TNamed>(IReadOnlyList<TNamed> connections, string? name, string? defaultName)
        where TNamed : class, INamedConnection
    {
        ArgumentNullException.ThrowIfNull(connections);
        if (!string.IsNullOrWhiteSpace(name))
        {
            return connections.FirstOrDefault(c => string.Equals(c.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(defaultName)
            && connections.FirstOrDefault(c => string.Equals(c.Name, defaultName.Trim(), StringComparison.OrdinalIgnoreCase)) is { } named)
        {
            return named;
        }

        return connections.Count > 0 ? connections[0] : null;
    }
}

/// <summary>
/// One connections-file family that signs in with a password — <c>oracle.json</c>, <c>mysql.json</c>, <c>postgres.json</c> — as one
/// piece of code over the family's facts (2026-10-04, the review's call: the three were copies with the names changed, and a fix to
/// the secrets or the loading had to be made three times). <c>{ "connections": { "&lt;name&gt;": { … } } }</c>, one in the profile's
/// folder and one in the home, the profile's winning by name (<see cref="SqlConfigFile"/>'s pair). A missing file is empty; a corrupt
/// or unreadable one is a problem and one warning, never a crash. A plain <c>password</c> is DPAPI-encrypted in place at the first read
/// that sees it; the byte-level edits are <see cref="ConnectionsFileEdit"/>'s. <c>sql.json</c> (Windows sign-in, <c>runas</c>),
/// <c>sqlite.json</c> (no password) and <c>unc.json</c> (shares) differ enough to keep their own.
/// </summary>
public sealed class ConnectionFamily<TConfig, TNamed>
    where TConfig : class, ISignInConfig
    where TNamed : class, INamedConnection<TConfig>
{
    /// <summary>The file's name in a profile's folder and in the home (<c>mysql.json</c>).</summary>
    public required string FileName { get; init; }

    /// <summary>The log category of everything in the family (<c>MySql</c>).</summary>
    public required string Category { get; init; }

    /// <summary>What a fresh file holds.</summary>
    public required string EmptyText { get; init; }

    /// <summary>The tab of <c>/tools</c> whose rows fix a password (<c>the MySQL tab</c>), put in for <see cref="WindowsCredentials"/>' <c>the SQL tab</c>.</summary>
    public required string Tab { get; init; }

    /// <summary>Why a <c>null</c> entry cannot connect (<c>MySqlText.NoHost</c>).</summary>
    public required string NullEntry { get; init; }

    /// <summary>The file's text as its connections by name, in file order; throws what <see cref="JsonSerializer"/> throws.</summary>
    public required Func<string, IReadOnlyDictionary<string, TConfig?>?> Deserialize { get; init; }

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

    /// <summary>The profile's file: <c>&lt;profile&gt;\&lt;FileName&gt;</c>.</summary>
    public string ProfilePath(string profileDirectory) => Path.Combine(profileDirectory, FileName);

    /// <summary>The home's file: <c>&lt;home&gt;\&lt;FileName&gt;</c>, every profile's.</summary>
    public string GlobalPath(string home) => Path.Combine(home, FileName);

    /// <summary>
    /// Reads <paramref name="path"/>: the usable connections in file order and a problem per entry that cannot connect
    /// (<see cref="ISignInConfig.Problem"/>) or has a blank name — for a file that is not JSON or cannot be read, one problem for
    /// the file and one warning. A missing file is empty. Never throws.
    /// </summary>
    public (List<TNamed> Connections, List<SqlConfigProblem> Problems) Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return ([], []);
        }

        IReadOnlyDictionary<string, TConfig?>? entries;
        try
        {
            entries = Deserialize(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            string detail = LogText.Excerpt(ex.Message);
            DiagnosticLog.Warn(Category, SqlText.ConfigProblemLogLine(path, detail));
            return ([], [new SqlConfigProblem(path, SqlText.UnreadableFile(detail), WholeFile: true)]);
        }

        if (entries is null || entries.Count == 0)
        {
            return ([], []);
        }

        var connections = new List<TNamed>(entries.Count);
        var problems = new List<SqlConfigProblem>();
        foreach (var (name, config) in entries)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                problems.Add(new SqlConfigProblem(path, SqlText.BlankName));
                continue;
            }

            string? reason = config is null ? NullEntry : config.Problem;
            if (reason is not null)
            {
                problems.Add(new SqlConfigProblem(SqlText.ConnectionSource(path, name), reason, Name: name.Trim()));
                continue;
            }

            EncryptInPlace(path, name.Trim(), config!);
            connections.Add(Named(name.Trim(), config!, path));
        }

        return (connections, problems);
    }

    /// <summary>The profile's file over the home's (<paramref name="home"/> null = the profile's alone): a name in both is the profile's.</summary>
    public (List<TNamed> Connections, List<SqlConfigProblem> Problems) LoadCatalog(string profileDirectory, string? home)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        var profile = Load(ProfilePath(profileDirectory));
        if (home is null || string.Equals(Path.GetFullPath(ProfilePath(profileDirectory)), Path.GetFullPath(GlobalPath(home)), StringComparison.OrdinalIgnoreCase))
        {
            return profile;
        }

        var global = Load(GlobalPath(home));
        var names = new HashSet<string>(profile.Connections.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        return ([.. profile.Connections, .. global.Connections.Where(c => !names.Contains(c.Name))], [.. profile.Problems, .. global.Problems]);
    }

    /// <summary>
    /// The safety net under the tab's masked prompt, <see cref="SqlConfigFile"/>'s: a plain-text <c>password</c> under the <c>file</c>
    /// store is DPAPI-encrypted and written back over that one value at the first read that sees it, the file's comments and layout
    /// kept. A file that cannot be written keeps working with the plain value, and says so once per read in the log.
    /// </summary>
    private void EncryptInPlace(string path, string name, TConfig config)
    {
        if (config.InCredentialManager || string.IsNullOrEmpty(config.Password) || WindowsCredentials.IsProtected(config.Password) || !OperatingSystem.IsWindows())
        {
            return;
        }

        var encrypted = WindowsCredentials.Protect(config.Password);
        string? error = encrypted.Error ?? ConnectionsFileEdit.WritePassword(path, name, encrypted.Value!);
        if (error is null)
        {
            config.Password = encrypted.Value;
            DiagnosticLog.Info(Category, SqlText.EncryptedLogLine(name, path));
        }
        else
        {
            DiagnosticLog.Warn(Category, SqlText.EncryptFailedLogLine(name, path, error));
        }
    }

    /// <summary>
    /// Adds connection <paramref name="name"/> to <paramref name="path"/> (the tab's add-connection wizard), the file's comments and
    /// layout kept (<see cref="ConnectionsFileEdit.AddConnection"/>; a missing file is made with <see cref="EmptyText"/> first). The
    /// entry is <paramref name="config"/> through <see cref="EntryInfo"/>, its <c>password</c> left out: the caller stores it after
    /// (<see cref="Save"/>). Null on success, else why not.
    /// </summary>
    public string? AddConnection(string path, string name, TConfig config)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(config);
        string? password = config.Password;
        config.Password = null;
        try
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                JsonSerializer.Serialize(writer, config, EntryInfo);
            }

            return ConnectionsFileEdit.AddConnection(path, name, Encoding.UTF8.GetString(stream.ToArray()), EmptyText);
        }
        finally
        {
            config.Password = password;
        }
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
    /// entry <see cref="ISignInConfig.CredentialTarget"/>; under <c>file</c> (the default) the <c>password</c> decrypted — or, while it
    /// is still plain text (a file the app could not rewrite), as it stands. A missing or undecryptable password is the sentence that
    /// names the fix, never a throw.
    /// </summary>
    public CredentialResult Resolve(TNamed connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var config = connection.Config;
        if (config.InCredentialManager)
        {
            string target = config.CredentialTarget(connection.Name);
            var read = WindowsCredentials.ReadGeneric(target);
            return read.NotFound ? CredentialResult.Failed(NoCredential(target)) : read;
        }

        string password = config.Password ?? "";
        if (password.Length == 0)
        {
            return CredentialResult.Failed(NoPassword(connection.Name));
        }

        if (!WindowsCredentials.IsProtected(password))
        {
            return CredentialResult.Ok(password);
        }

        // WindowsCredentials words its failure for sql.json; the fix it names is on this family's tab.
        var decrypted = WindowsCredentials.Unprotect(password);
        return decrypted.Error is { } error ? CredentialResult.Failed(error.Replace("the SQL tab", Tab, StringComparison.Ordinal)) : decrypted;
    }

    /// <summary>
    /// Saves <paramref name="password"/> for <paramref name="connection"/> in its store (the tab's masked prompt and the wizard): under
    /// <c>credman</c> the Credential Manager entry, written with the connection's user; under <c>file</c> DPAPI-encrypted into the file
    /// the connection came from. The status line's sentence either way.
    /// </summary>
    public (bool Saved, string Notice) Save(TNamed connection, string password)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(password);
        var config = connection.Config;
        if (config.InCredentialManager)
        {
            string target = config.CredentialTarget(connection.Name);
            var written = WindowsCredentials.WriteGeneric(target, config.User?.Trim() ?? "", password);
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
