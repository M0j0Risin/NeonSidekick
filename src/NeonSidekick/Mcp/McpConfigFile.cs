using System.Text.Json;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Mcp;

/// <summary>One thing a load could not use: the file itself, or one server in it. <see cref="Source"/> is the path, or the path and the server's name.</summary>
public sealed record McpConfigProblem(string Source, string Reason)
{
    /// <summary>
    /// The server's name for a problem of one server (2026-11-20, the mcp-ask review's #64 and #124), null for the whole file's or a blank
    /// name's: a broken profile entry still shadows the home's of that name, and headless <c>--allow mcp:&lt;name&gt;</c> knows it.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// The file a problem of one server sits in (2026-11-20, plan idempotent-mixing-ripple): the <c>/mcp</c> line of a server whose <c>ask</c>
    /// is a typo takes <c>a</c>, which writes there; null beside a null <see cref="Name"/>.
    /// </summary>
    public string? Path { get; init; }
}

/// <summary>What <see cref="McpConfigFile.Load"/> found: the usable servers in file order and the problems, never a throw.</summary>
public sealed record McpConfigLoad(string Path, IReadOnlyList<KeyValuePair<string, McpServerConfig>> Servers, IReadOnlyList<McpConfigProblem> Problems)
{
    /// <summary>A file that does not exist, or one with nothing in it.</summary>
    public static McpConfigLoad Empty(string path) => new(path, [], []);
}

/// <summary>
/// <c>mcp.json</c> (2026-09-20): <c>{ "mcpServers": { "&lt;name&gt;": { … } } }</c>, the shape every MCP client
/// reads, one in the profile's <c>configs</c> folder and one in the home's (since 2026-10-16; <see cref="McpCatalog"/> merges them, the
/// profile's winning by name). A missing file is empty; a corrupt or unreadable one is a problem and
/// one warning (the <c>MemoryStore</c> tolerance), never a crash. The pane's edit rows create the file
/// with <see cref="EmptyText"/> when it is missing (<see cref="EnsureExists"/>), so the editor opens on
/// the right shape.
/// </summary>
public sealed class McpConfigFile
{
    /// <summary>The file's name in a profile's <c>configs</c> folder and in the home.</summary>
    public const string FileName = "mcp.json";

    /// <summary>The log category of everything MCP.</summary>
    public const string Category = "Mcp";

    /// <summary>The key the servers sit under: what the byte-level edits (a sealed token, a server's <c>ask</c>) name as the first step.</summary>
    public const string ServersKey = "mcpServers";

    /// <summary>A server's <c>ask</c> key, as <see cref="McpServerConfig.Ask"/> binds it (camel-case, case-sensitive).</summary>
    public const string AskKey = "ask";

    /// <summary>What a fresh file holds: no servers, the key ready to fill. Pinned.</summary>
    public const string EmptyText = "{\n  \"mcpServers\": {}\n}\n";

    /// <summary>
    /// The servers by name, in file order (the deserializer keeps it), each as its raw JSON (2026-11-20, the mcp-ask review's #5): read one
    /// at a time (<see cref="Parse"/>), so a mistyped value is that server's problem and the others still load.
    /// </summary>
    public Dictionary<string, JsonElement>? McpServers { get; set; }

    /// <summary>The profile's file: <c>&lt;profile&gt;\configs\mcp.json</c> (<c>configs</c> since 2026-10-16).</summary>
    public static string ProfilePath(string profileDirectory) => NeonSidekick.Settings.Profiles.ConfigPath(profileDirectory, FileName);

    /// <summary>The home's file: <c>&lt;home&gt;\configs\mcp.json</c> (configs since 2026-10-16), every profile's.</summary>
    public static string GlobalPath(string home) => NeonSidekick.Settings.Profiles.HomeConfigPath(home, FileName);

    /// <summary>
    /// Reads <paramref name="path"/>: the usable servers in file order, a problem per server that names neither or both transports, has a
    /// blank name or won't read (2026-11-20, the mcp-ask review's #5: one entry at a time), and — for a file that is not JSON, cannot be
    /// read or whose <c>mcpServers</c> isn't an object — one problem for the file. A missing file is <see cref="McpConfigLoad.Empty"/>. Never
    /// throws.
    /// <para>With <paramref name="seal"/> (the default) the plain tokens are encrypted first (<see cref="SealFile"/>), and the read is cached
    /// per file stamp (<see cref="Computers.ConfigCache"/>, 2026-11-20, #17): a file's problems are logged once per change, not at every read.
    /// Sealing runs before the cached read, never inside it: the cache stamps the file before its loader, and a loader that wrote would store
    /// a stale stamp and read, and warn, again. Without <paramref name="seal"/> (#84, #122: the reads that only look, such as <c>/profile
    /// push|pull mcp</c> and whether any server is set up) nothing is written, nothing logged and nothing cached.</para>
    /// </summary>
    public static McpConfigLoad Load(string path, bool seal = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!seal)
        {
            return Parse(path, log: false);
        }

        Computers.ConfigCache.Get(SealFamily, path, () => SealFile(path));
        return Computers.ConfigCache.Get(Category, path, () => Parse(path, log: true));
    }

    /// <summary>The cache family of <see cref="SealFile"/>'s run, so a seal that can't write (a read-only file) is tried, and warned about, once per change.</summary>
    private const string SealFamily = "mcp-seal";

    /// <summary>
    /// Encrypts every plain token in <paramref name="path"/> (<see cref="McpSecrets.EncryptInPlace"/>), every server that reads at all
    /// included, one skipped over a typo too (the families' rule: no token stays plain for a typo). Quiet about the file's problems: the
    /// cached <see cref="Parse"/> after it logs them. True, for the cache.
    /// </summary>
    private static bool SealFile(string path)
    {
        if (!File.Exists(path) || ReadFile(path, out _) is not { } servers)
        {
            return true;
        }

        foreach (var (name, element) in servers)
        {
            if (string.IsNullOrWhiteSpace(name) || element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            McpServerConfig? config;
            try
            {
                config = element.Deserialize(McpJsonContext.Default.McpServerConfig);
            }
            catch (JsonException)
            {
                continue;
            }

            if (config is not null)
            {
                McpSecrets.EncryptInPlace(path, name, config);
            }
        }

        return true;
    }

    /// <summary>The file's servers as raw JSON, null when there are none to read; <paramref name="error"/> says why a file couldn't be read.</summary>
    private static Dictionary<string, JsonElement>? ReadFile(string path, out string? error)
    {
        error = null;
        try
        {
            var file = JsonSerializer.Deserialize(File.ReadAllText(path), McpJsonContext.Default.McpConfigFile);
            return file?.McpServers is { Count: > 0 } servers ? servers : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            error = LogText.Excerpt(ex.Message);
            return null;
        }
    }

    /// <summary>One read of the file, with no writes: <see cref="Load"/>'s body. <paramref name="log"/> writes each problem's Warn.</summary>
    private static McpConfigLoad Parse(string path, bool log)
    {
        if (!File.Exists(path))
        {
            return McpConfigLoad.Empty(path);
        }

        // "mcpServers": null reads as none (2026-10-22, review K1: it threw here, and the startup encryption pass and every turn's tool build
        // with it). SqliteConfigFile has had this check since 2026-10-04.
        var servers = ReadFile(path, out string? unreadable);
        if (unreadable is not null)
        {
            if (log)
            {
                DiagnosticLog.Warn(Category, McpText.ConfigProblemLogLine(path, unreadable));
            }

            return new McpConfigLoad(path, [], [new McpConfigProblem(path, McpText.UnreadableFile(unreadable))]);
        }

        if (servers is null)
        {
            return McpConfigLoad.Empty(path);
        }

        var usable = new List<KeyValuePair<string, McpServerConfig>>(servers.Count);
        var problems = new List<McpConfigProblem>();
        foreach (var (name, element) in servers)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                problems.Add(new McpConfigProblem(path, McpText.BlankName));
                continue;
            }

            string source = McpText.ServerSource(path, name);
            McpServerConfig? config;
            try
            {
                config = element.ValueKind == JsonValueKind.Null ? null : element.Deserialize(McpJsonContext.Default.McpServerConfig);
            }
            catch (JsonException ex)
            {
                string detail = LogText.Excerpt(ex.Message);
                if (log)
                {
                    DiagnosticLog.Warn(Category, McpText.ConfigProblemLogLine(source, detail));
                }

                problems.Add(new McpConfigProblem(source, McpText.BadEntry(detail)) { Name = name, Path = path });
                continue;
            }

            if (config is null)
            {
                problems.Add(new McpConfigProblem(source, McpText.NeitherCommandNorUrl) { Name = name, Path = path });
                continue;
            }

            if (config.Problem is { } reason)
            {
                problems.Add(new McpConfigProblem(source, reason) { Name = name, Path = path });
                continue;
            }

            usable.Add(new KeyValuePair<string, McpServerConfig>(name, config));
        }

        return new McpConfigLoad(path, usable, problems);
    }

    /// <summary>
    /// Reads the home's file and every profile's (2026-11-19, feature idea #29), so a plain token is encrypted before the first screen or turn,
    /// as the families' passes do. The paths read. Never throws: a profile folder that can't be listed is one Warn, the home's file still read.
    /// </summary>
    public static IReadOnlyList<string> EncryptAll(string home)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        var read = NeonSidekick.Settings.Profiles.ExistingConfigFiles(home, GlobalPath(home), ProfilePath, Category, (folder, detail) => Computers.ComputerText.ProfilesUnlistedLogLine(FileName, folder, detail));
        foreach (string path in read)
        {
            Load(path);
        }

        return read;
    }

    /// <summary>Writes <see cref="EmptyText"/> to <paramref name="path"/> when no file is there (the folder made first); true when it wrote. Throws on an IO failure — the caller's notice.</summary>
    public static bool EnsureExists(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (File.Exists(path))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, EmptyText);
        return true;
    }
}
