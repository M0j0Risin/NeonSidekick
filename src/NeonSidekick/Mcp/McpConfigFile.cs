using System.Text.Json;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Mcp;

/// <summary>One thing a load could not use: the file itself, or one server in it. <see cref="Source"/> is the path, or the path and the server's name.</summary>
public sealed record McpConfigProblem(string Source, string Reason);

/// <summary>What <see cref="McpConfigFile.Load"/> found: the usable servers in file order and the problems, never a throw.</summary>
public sealed record McpConfigLoad(string Path, IReadOnlyList<KeyValuePair<string, McpServerConfig>> Servers, IReadOnlyList<McpConfigProblem> Problems)
{
    /// <summary>A file that does not exist, or one with nothing in it.</summary>
    public static McpConfigLoad Empty(string path) => new(path, [], []);
}

/// <summary>
/// <c>mcp.json</c> (2026-09-20): <c>{ "mcpServers": { "&lt;name&gt;": { … } } }</c>, the shape every MCP client
/// reads, one in the profile's <c>configs</c> folder and one in the home (<see cref="McpCatalog"/> merges them, the
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

    /// <summary>What a fresh file holds: no servers, the key ready to fill. Pinned.</summary>
    public const string EmptyText = "{\n  \"mcpServers\": {}\n}\n";

    /// <summary>The servers by name, in file order (the deserializer keeps it).</summary>
    public Dictionary<string, McpServerConfig> McpServers { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The profile's file: <c>&lt;profile&gt;\configs\mcp.json</c> (<c>configs</c> since 2026-10-16).</summary>
    public static string ProfilePath(string profileDirectory) => NeonSidekick.Settings.Profiles.ConfigPath(profileDirectory, FileName);

    /// <summary>The home's file: <c>&lt;home&gt;\mcp.json</c>, every profile's.</summary>
    public static string GlobalPath(string home) => Path.Combine(home, FileName);

    /// <summary>
    /// Reads <paramref name="path"/>: the usable servers in file order, a problem per server that names
    /// neither or both transports or has a blank name, and — for a file that is not JSON or cannot be
    /// read — one problem for the file and one <c>Mcp</c> warning. A missing file is
    /// <see cref="McpConfigLoad.Empty"/>. Never throws.
    /// </summary>
    public static McpConfigLoad Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            return McpConfigLoad.Empty(path);
        }

        McpConfigFile? file;
        try
        {
            file = JsonSerializer.Deserialize(File.ReadAllText(path), McpJsonContext.Default.McpConfigFile);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            string detail = LogText.Excerpt(ex.Message);
            DiagnosticLog.Warn(Category, McpText.ConfigProblemLogLine(path, detail));
            return new McpConfigLoad(path, [], [new McpConfigProblem(path, McpText.UnreadableFile(detail))]);
        }

        if (file is null || file.McpServers.Count == 0)
        {
            return McpConfigLoad.Empty(path);
        }

        var servers = new List<KeyValuePair<string, McpServerConfig>>(file.McpServers.Count);
        var problems = new List<McpConfigProblem>();
        foreach (var (name, config) in file.McpServers)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                problems.Add(new McpConfigProblem(path, McpText.BlankName));
                continue;
            }

            if (config is null)
            {
                problems.Add(new McpConfigProblem(McpText.ServerSource(path, name), McpText.NeitherCommandNorUrl));
                continue;
            }

            if (config.Problem is { } reason)
            {
                problems.Add(new McpConfigProblem(McpText.ServerSource(path, name), reason));
                continue;
            }

            servers.Add(new KeyValuePair<string, McpServerConfig>(name, config));
        }

        return new McpConfigLoad(path, servers, problems);
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
