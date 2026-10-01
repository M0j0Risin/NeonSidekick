using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Sql;

namespace NeonSidekick.Unc;

/// <summary>
/// What <see cref="UncConfigFile.LoadCatalog"/> found: the usable shares (the profile's first, then the home's the profile does
/// not shadow, each in file order) and the problems, never a throw. The <see cref="SqlCatalog"/> shape.
/// </summary>
public sealed record UncCatalog(IReadOnlyList<UncNamedShare> Shares, IReadOnlyList<SqlConfigProblem> Problems, int Hidden = 0)
{
    public static readonly UncCatalog Empty = new([], []);

    /// <summary>
    /// The catalog narrowed to the shares a profile offers (<c>UNC shares offered</c>): <paramref name="names"/> null = all of
    /// them; else only those whose name is in it (case-insensitive), in file order, <see cref="Hidden"/> counting the rest. The
    /// problems are kept. Pure.
    /// </summary>
    public UncCatalog Offered(IReadOnlyList<string>? names)
    {
        if (names is null)
        {
            return this;
        }

        var wanted = new HashSet<string>(names.Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
        var kept = Shares.Where(s => wanted.Contains(s.Name)).ToList();
        return this with { Shares = kept, Hidden = Hidden + Shares.Count - kept.Count };
    }

    /// <summary>
    /// The share a call means: <paramref name="name"/> when given (case-insensitive), else <paramref name="defaultName"/> when that
    /// names one, else the first. Null when nothing matches.
    /// </summary>
    public UncNamedShare? Find(string? name, string? defaultName)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            return Shares.FirstOrDefault(s => string.Equals(s.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(defaultName)
            && Shares.FirstOrDefault(s => string.Equals(s.Name, defaultName.Trim(), StringComparison.OrdinalIgnoreCase)) is { } named)
        {
            return named;
        }

        return Shares.Count > 0 ? Shares[0] : null;
    }

    /// <summary>
    /// The share a full path lies in (2026-09-30: the model may pass <c>\\fs01\eng\specs\a.md</c> rather than a share's name and a
    /// relative path): the one whose root is the longest prefix of <paramref name="fullPath"/> by spelling, case-insensitive, a
    /// separator after it (<c>\\fs01\eng2</c> is not under <c>\\fs01\eng</c>); <c>/</c> reads as <c>\</c>. Null for a path that is
    /// not absolute or lies under none. Pure.
    /// </summary>
    public UncNamedShare? Locate(string? fullPath)
    {
        string text = (fullPath ?? "").Trim().Replace('/', '\\');
        if (!IsAbsolute(text))
        {
            return null;
        }

        string candidate;
        try
        {
            candidate = UncShareConfig.NormalizeRoot(text);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }

        return Shares
            .Where(s => WorkingDirectory.IsInside(s.Config.Root, candidate))
            .OrderByDescending(s => s.Config.Root.Length)
            .FirstOrDefault();
    }

    /// <summary>Whether <paramref name="text"/> (already <c>\</c>-spelled) is a full path a share could hold: <c>\\…</c> or <c>X:\…</c>.</summary>
    public static bool IsAbsolute(string text) =>
        text.StartsWith(@"\\", StringComparison.Ordinal)
        || (text.Length >= 3 && char.IsAsciiLetter(text[0]) && text[1] == ':' && text[2] == '\\');
}

/// <summary>
/// <c>unc.json</c> (2026-09-30): <c>{ "shares": { "&lt;name&gt;": { … } } }</c>, one in the profile's folder and one in the home,
/// the <see cref="SqlConfigFile"/> pair: the profile's wins by name. A missing file is empty; a corrupt or unreadable one is a
/// problem and one warning, never a crash. Read afresh at every call (<see cref="UncAccess"/>), so an edit in the editor counts
/// on the next tool call. A plain <c>password</c> is DPAPI-encrypted in place at the first read that sees it; the byte-level
/// edits are <see cref="ConnectionsFileEdit"/>'s, under the <c>shares</c> key.
/// </summary>
public sealed class UncConfigFile
{
    /// <summary>The file's name in a profile's folder and in the home.</summary>
    public const string FileName = "unc.json";

    /// <summary>The log category of everything UNC.</summary>
    public const string Category = "Unc";

    /// <summary>The key the shares sit under (<see cref="ConnectionsFileEdit"/>'s section).</summary>
    public const string Section = "shares";

    /// <summary>What a share is called in the file edits' refusals.</summary>
    public const string Noun = "share";

    /// <summary>
    /// What a fresh file holds: no shares, and a commented example of each way in — each one valid once its <c>//</c> are removed
    /// (pinned by a test). Pinned.
    /// </summary>
    public const string EmptyText =
        "{\n" +
        "  // One entry per share. Its name is what the model passes as \"share\", and what %name picks on the input line.\n" +
        "  // \"path\" is required: \\\\server\\share, a folder under it, or a local folder. In JSON a backslash is doubled\n" +
        "  // (\"\\\\\\\\fs01\\\\eng\") or written as / (\"//fs01/eng\"). Remove the leading // from an example to use it, and put it\n" +
        "  // inside \"shares\" below.\n" +
        "  //\n" +
        "  // A share you can open yourself, read only (the default):\n" +
        "  // \"eng\": { \"path\": \"//fs01/eng\", \"description\": \"engineering specs and drawings\" },\n" +
        "  //\n" +
        "  // As another Windows account (like runas /netonly), the password in Windows Credential Manager as NeonSidekick/unc/<name> —\n" +
        "  // set it with UNC set password on the UNC tab of /tools, or: cmdkey /generic:NeonSidekick/unc/finance /user:CORP\\svc_reader /pass\n" +
        "  // \"finance\": { \"path\": \"//fs02.corp.local/finance/reports\", \"auth\": \"runas\", \"user\": \"CORP\\\\svc_reader\",\n" +
        "  //   \"passwordStore\": \"credman\" },\n" +
        "  //\n" +
        "  // A local folder outside the working directory that the model may also change (UNC writes must be on too):\n" +
        "  // \"data\": { \"path\": \"D:/Data\", \"access\": \"readwrite\" },\n" +
        "  //\n" +
        "  // auth: windows (the default) or runas; access: read (the default) or readwrite — changes and deletes on a share are\n" +
        "  // permanent, nothing is kept; a runas password typed here in plain text is encrypted (\"dpapi:…\") at the next read.\n" +
        "  \"shares\": {}\n" +
        "}\n";

    /// <summary>The shares by name, in file order (the deserializer keeps it).</summary>
    public Dictionary<string, UncShareConfig?> Shares { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The profile's file: <c>&lt;profile&gt;\unc.json</c>.</summary>
    public static string ProfilePath(string profileDirectory) => Path.Combine(profileDirectory, FileName);

    /// <summary>The home's file: <c>&lt;home&gt;\unc.json</c>, every profile's.</summary>
    public static string GlobalPath(string home) => Path.Combine(home, FileName);

    /// <summary>
    /// Reads <paramref name="path"/>: the usable shares in file order and a problem per entry that cannot be used
    /// (<see cref="UncShareConfig.Problem"/>) or has a blank name — for a file that is not JSON or cannot be read, one problem for
    /// the file and one <c>Unc</c> warning. A missing file is empty. Never throws.
    /// </summary>
    public static UncCatalog Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return UncCatalog.Empty;
        }

        UncConfigFile? file;
        try
        {
            file = JsonSerializer.Deserialize(File.ReadAllText(path), UncJsonContext.Default.UncConfigFile);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            string detail = LogText.Excerpt(ex.Message);
            DiagnosticLog.Warn(Category, SqlText.ConfigProblemLogLine(path, detail));
            return new UncCatalog([], [new SqlConfigProblem(path, UncText.UnreadableFile(detail))]);
        }

        if (file is null || file.Shares.Count == 0)
        {
            return UncCatalog.Empty;
        }

        var shares = new List<UncNamedShare>(file.Shares.Count);
        var problems = new List<SqlConfigProblem>();
        foreach (var (name, config) in file.Shares)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                problems.Add(new SqlConfigProblem(path, UncText.BlankName));
                continue;
            }

            string? reason = config is null ? UncText.NoPath : config.Problem;
            if (reason is not null)
            {
                problems.Add(new SqlConfigProblem(SqlText.ConnectionSource(path, name), reason));
                continue;
            }

            EncryptInPlace(path, name.Trim(), config!);
            shares.Add(new UncNamedShare(name.Trim(), config!, path));
        }

        return new UncCatalog(shares, problems);
    }

    /// <summary>
    /// The safety net under the UNC tab's masked prompt, <see cref="SqlConfigFile"/>'s: a plain-text <c>password</c> under the
    /// <c>file</c> store is DPAPI-encrypted and written back over that one value at the first read that sees it, the file's
    /// comments and layout kept. A file that cannot be written keeps working with the plain value, and says so in the log.
    /// </summary>
    private static void EncryptInPlace(string path, string name, UncShareConfig config)
    {
        if (!config.IsRunAs || config.InCredentialManager || string.IsNullOrEmpty(config.Password) || WindowsCredentials.IsProtected(config.Password) || !OperatingSystem.IsWindows())
        {
            return;
        }

        var encrypted = WindowsCredentials.Protect(config.Password);
        string? error = encrypted.Error ?? WritePassword(path, name, encrypted.Value!);
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

    /// <summary>Writes <paramref name="value"/> as the <c>password</c> of share <paramref name="name"/> in <paramref name="path"/>, touching nothing else. Null on success, else why not.</summary>
    public static string? WritePassword(string path, string name, string value) => ConnectionsFileEdit.WritePassword(path, name, value, Section, Noun);

    /// <summary>
    /// Adds share <paramref name="name"/> to <paramref name="path"/> (the UNC tab's <c>UNC add share</c> wizard), the file's comments
    /// and layout kept (<see cref="ConnectionsFileEdit.AddConnection"/> under <see cref="Section"/>; a missing file is made with
    /// <see cref="EmptyText"/> first). The entry is <paramref name="config"/> through <see cref="UncJsonContext"/>, its
    /// <c>password</c> left out: the caller stores it after (<see cref="UncSecrets.Save"/>). Null on success, else why not.
    /// </summary>
    public static string? AddShare(string path, string name, UncShareConfig config)
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
                JsonSerializer.Serialize(writer, config, UncJsonContext.Default.UncShareConfig);
            }

            return ConnectionsFileEdit.AddConnection(path, name, Encoding.UTF8.GetString(stream.ToArray()), EmptyText, Section, Noun);
        }
        finally
        {
            config.Password = password;
        }
    }

    /// <summary>
    /// Every <c>unc.json</c> under <paramref name="home"/> read once — the home's and each profile's — so <see cref="Load"/>'s
    /// encryption runs over them all at startup (<see cref="SqlConfigFile.EncryptAll"/>'s part). The files read, in order; a
    /// profiles folder that cannot be listed is a warning and the home's file alone. Never throws.
    /// </summary>
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
            DiagnosticLog.Warn(Category, UncText.ProfilesUnlistedLogLine(Settings.Profiles.Root(home), LogText.Excerpt(ex.Message)));
        }

        var read = new List<string>();
        foreach (string path in paths.Where(File.Exists))
        {
            Load(path);
            read.Add(path);
        }

        return read;
    }

    /// <summary>The profile's file over the home's (<paramref name="home"/> null = the profile's alone): a name in both is the profile's.</summary>
    public static UncCatalog LoadCatalog(string profileDirectory, string? home)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        var profile = Load(ProfilePath(profileDirectory));
        if (home is null || string.Equals(Path.GetFullPath(ProfilePath(profileDirectory)), Path.GetFullPath(GlobalPath(home)), StringComparison.OrdinalIgnoreCase))
        {
            return profile;
        }

        var global = Load(GlobalPath(home));
        var names = new HashSet<string>(profile.Shares.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        return new UncCatalog(
            [.. profile.Shares, .. global.Shares.Where(s => !names.Contains(s.Name))],
            [.. profile.Problems, .. global.Problems]);
    }

    /// <summary>Writes <see cref="EmptyText"/> to <paramref name="path"/> when no file is there (the folder made first); true when it wrote. Throws on an IO failure — the caller's notice.</summary>
    public static bool EnsureExists(string path) => ConnectionsFileEdit.EnsureExists(path, EmptyText);
}
