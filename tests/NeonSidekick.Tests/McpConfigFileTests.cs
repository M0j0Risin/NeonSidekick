using NeonSidekick.Diagnostics;
using NeonSidekick.Mcp;

namespace NeonSidekick.Tests;

/// <summary>The <c>mcp.json</c> reader (2026-09-20): the ecosystem's shape, the tolerance, the per-server problems, the merge.</summary>
public class McpConfigFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));

    public McpConfigFileTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Write(string name, string json)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Strings_ArePinned()
    {
        Assert.Equal("mcp.json", McpConfigFile.FileName);
        Assert.Equal("Mcp", McpConfigFile.Category);
        Assert.Equal(Path.Combine(@"D:\home\profiles\work", "mcp.json"), McpConfigFile.ProfilePath(@"D:\home\profiles\work"));
        Assert.Equal(Path.Combine(@"D:\home", "mcp.json"), McpConfigFile.GlobalPath(@"D:\home"));
    }

    [Fact]
    public void MissingFile_IsEmpty()
    {
        var load = McpConfigFile.Load(Path.Combine(_dir, "none.json"));
        Assert.Empty(load.Servers);
        Assert.Empty(load.Problems);
    }

    /// <summary>A Claude Desktop block, pasted as it is: a stdio server with args and env.</summary>
    [Fact]
    public void ClaudeDesktopShape_LoadsAStdioServer_InFileOrder()
    {
        string path = Write("mcp.json", """
            {
              "mcpServers": {
                "docker": { "command": "docker", "args": ["mcp", "gateway", "run"], "env": { "DOCKER_HOST": "npipe" }, "cwd": "C:\\work" },
                "fs": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", "C:\\data"] }
              }
            }
            """);

        var load = McpConfigFile.Load(path);

        Assert.Empty(load.Problems);
        Assert.Equal(["docker", "fs"], load.Servers.Select(s => s.Key));
        var docker = load.Servers[0].Value;
        Assert.True(docker.IsStdio);
        Assert.False(docker.IsHttp);
        Assert.Equal("docker", docker.Command);
        Assert.Equal(["mcp", "gateway", "run"], docker.Args);
        Assert.Equal("npipe", docker.Env!["DOCKER_HOST"]);
        Assert.Equal(@"C:\work", docker.Cwd);
        Assert.Null(docker.Problem);
        Assert.Equal("stdio: docker mcp gateway run", docker.Describe());
    }

    /// <summary>A Claude Code block: the http type with a url and headers; comments and a trailing comma tolerated.</summary>
    [Fact]
    public void ClaudeCodeShape_LoadsAnHttpServer_CommentsAndTrailingCommasTolerated()
    {
        string path = Write("mcp.json", """
            {
              // the gateway
              "mcpServers": {
                "gateway": { "type": "http", "url": "http://localhost:8811/mcp", "headers": { "Authorization": "Bearer x" }, },
              },
            }
            """);

        var load = McpConfigFile.Load(path);

        Assert.Empty(load.Problems);
        var gateway = Assert.Single(load.Servers).Value;
        Assert.True(gateway.IsHttp);
        Assert.Equal("http", gateway.Type);
        Assert.Equal("Bearer x", gateway.Headers!["Authorization"]);
        Assert.Equal("http: http://localhost:8811/mcp", gateway.Describe());
    }

    [Fact]
    public void Fingerprint_IsCanonical_SoSpacingAndKeyOrderNeverCount()
    {
        var a = McpConfigFile.Load(Write("a.json", """{ "mcpServers": { "x": { "args": ["1"], "command": "c" } } }""")).Servers[0].Value;
        var b = McpConfigFile.Load(Write("b.json", """{"mcpServers":{"x":{"command":"c","args":["1"]}}}""")).Servers[0].Value;
        var c = McpConfigFile.Load(Write("c.json", """{"mcpServers":{"x":{"command":"c","args":["2"]}}}""")).Servers[0].Value;
        Assert.Equal(a.Fingerprint(), b.Fingerprint());
        Assert.NotEqual(a.Fingerprint(), c.Fingerprint());
    }

    [Fact]
    public void AServerWithNeitherOrBoth_OrABadUrl_OrABlankName_IsAProblem_TheOthersLoad()
    {
        string path = Write("mcp.json", """
            {
              "mcpServers": {
                "ok": { "command": "docker" },
                "neither": { "type": "stdio" },
                "both": { "command": "x", "url": "http://a/" },
                "bad": { "url": "not a url" },
                "": { "command": "x" }
              }
            }
            """);

        var load = McpConfigFile.Load(path);

        Assert.Equal(["ok"], load.Servers.Select(s => s.Key));
        Assert.Equal(
            [
                (path + " › neither", McpText.NeitherCommandNorUrl),
                (path + " › both", McpText.BothCommandAndUrl),
                (path + " › bad", McpText.BadUrl("not a url")),
                (path, McpText.BlankName),
            ],
            load.Problems.Select(p => (p.Source, p.Reason)));
        Assert.Equal("the url 'not a url' is not an absolute http(s) address", McpText.BadUrl("not a url"));
        Assert.Equal(path + " › neither: names neither a command nor a url", McpText.ProblemLine(load.Problems[0]));
    }

    [Fact]
    public void ACorruptFile_IsOneProblem_AndOneWarning_NeverAThrow()
    {
        string path = Write("mcp.json", "{ not json");
        var warnings = new List<DiagnosticEvent>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == McpConfigFile.Category) warnings.Add(e); };
        DiagnosticLog.Emitted += capture;
        try
        {
            var load = McpConfigFile.Load(path);

            Assert.Empty(load.Servers);
            var problem = Assert.Single(load.Problems);
            Assert.Equal(path, problem.Source);
            Assert.StartsWith("could not be read: ", problem.Reason);
            var warning = Assert.Single(warnings);
            Assert.Equal(DiagnosticLevel.Warning, warning.Level);
            Assert.StartsWith("Could not read " + path + ": ", warning.Message);
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }
    }

    [Fact]
    public void AnEmptyObject_OrNull_IsEmpty()
    {
        Assert.Empty(McpConfigFile.Load(Write("a.json", McpConfigFile.EmptyText)).Servers);
        Assert.Empty(McpConfigFile.Load(Write("b.json", "null")).Servers);
        Assert.Empty(McpConfigFile.Load(Write("b.json", "null")).Problems);
    }

    [Fact]
    public void EnsureExists_WritesTheEmptyShapeOnce_AndLeavesAFileAlone()
    {
        string path = Path.Combine(_dir, "sub", "mcp.json");
        Assert.True(McpConfigFile.EnsureExists(path));
        Assert.Equal(McpConfigFile.EmptyText, File.ReadAllText(path));
        File.WriteAllText(path, "{ \"mcpServers\": { \"x\": { \"command\": \"c\" } } }");
        Assert.False(McpConfigFile.EnsureExists(path));
        Assert.Contains("\"x\"", File.ReadAllText(path));
        Assert.Empty(McpConfigFile.Load(path).Problems);
    }

    // ── The merge ───────────────────────────────────────────────────────────

    [Fact]
    public void Merge_ProfileFirst_ThenGlobal_AGlobalNameTheProfileHasIsShadowed()
    {
        var profile = McpConfigFile.Load(Write("p.json", """{ "mcpServers": { "docker": { "command": "p-docker" }, "chrome": { "command": "chrome" } } }"""));
        var global = McpConfigFile.Load(Write("g.json", """{ "mcpServers": { "gateway": { "url": "http://h/mcp" }, "docker": { "command": "g-docker" } } }"""));

        var merged = McpCatalog.Merge(profile, global);

        Assert.Equal(["docker", "chrome", "gateway", "docker"], merged.Entries.Select(e => e.Name));
        Assert.Equal([McpScope.Profile, McpScope.Profile, McpScope.Global, McpScope.Global], merged.Entries.Select(e => e.Scope));
        Assert.Equal([null, null, null, McpScope.Profile], merged.Entries.Select(e => e.ShadowedBy));
        Assert.Equal([true, true, true, false], merged.Entries.Select(e => e.Startable));
        Assert.Equal("p-docker", merged.Entries[0].Config.Command);
        Assert.Equal("g-docker", merged.Entries[3].Config.Command);
        Assert.True(merged.Configured);
        Assert.Empty(merged.Problems);
    }

    [Fact]
    public void Merge_CarriesBothFilesProblems_ProfileFirst_AndTwoEmptyLoadsAreNotConfigured()
    {
        var profile = McpConfigFile.Load(Write("p.json", """{ "mcpServers": { "a": {} } }"""));
        var global = McpConfigFile.Load(Write("g.json", "{ broken"));

        var merged = McpCatalog.Merge(profile, global);

        Assert.Empty(merged.Entries);
        Assert.Equal(2, merged.Problems.Count);
        Assert.EndsWith(" › a", merged.Problems[0].Source);
        Assert.StartsWith("could not be read: ", merged.Problems[1].Reason);
        Assert.True(merged.Configured);
        Assert.False(McpCatalog.Merge(McpConfigLoad.Empty("x"), McpConfigLoad.Empty("y")).Configured);
        Assert.False(McpMerged.Empty.Configured);
    }
}
