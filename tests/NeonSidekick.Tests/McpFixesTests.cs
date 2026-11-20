using System.Text.Json;
using NeonSidekick.App;
using NeonSidekick.Computers;
using NeonSidekick.Diagnostics;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Mcp;
using NeonSidekick.Shell;
using NeonSidekick.Sql;

namespace NeonSidekick.Tests;

/// <summary>
/// The mcp-ask review's fixes (2026-11-20) that need no server: <c>${VAR}</c> expansion (#72), one entry at a time (#5, #64), the cached
/// load's one Warn (#17), one write for several values (#42), the <c>--allow</c> entry (#8, #71, #83), the view's and the audit's cuts
/// (#123), <c>--allow</c> beside <c>--help</c> (#26) and a server's own <c>/usage</c> share (#97).
/// </summary>
public sealed class McpFixesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly IDisposable _cache = ConfigCache.Scope();

    public McpFixesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _cache.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Write(string json)
    {
        string path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    // ── #72: ${VAR} ───────────────────────────────────────────────────────────

    [Fact]
    public void Expand_FillsTheFiveFields_TakesADefault_AndLeavesAnUnsetNameAsWritten()
    {
        var config = new McpServerConfig
        {
            Command = "${TOOL_HOME}/run",
            Args = ["--token=${MY_TOKEN}", "${MISSING}", "${MISSING:-fallback}"],
            Env = new() { ["A"] = "${MY_TOKEN}" },
            Url = null,
            Cwd = "${TOOL_HOME}",
        };
        string? Lookup(string name) => name switch { "TOOL_HOME" => "C:/tools", "MY_TOKEN" => "abc", _ => null };

        var (expanded, unset) = McpReferences.Expand(config, Lookup);

        Assert.Equal("C:/tools/run", expanded.Command);
        Assert.Equal(["--token=abc", "${MISSING}", "fallback"], expanded.Args);
        Assert.Equal("abc", expanded.Env!["A"]);
        Assert.Equal("${TOOL_HOME}", expanded.Cwd);   // cwd isn't expanded, as Claude Code's isn't
        Assert.Equal(new McpUnsetReference("args", "MISSING"), Assert.Single(unset));
        // The catalog's config is never changed: the load cache shares it.
        Assert.Equal(["--token=${MY_TOKEN}", "${MISSING}", "${MISSING:-fallback}"], config.Args);
        Assert.Equal("${MY_TOKEN}", config.Env["A"]);
    }

    [Fact]
    public void Expand_WithNothingToExpand_IsTheSameConfig_AndUrlAndHeadersExpand()
    {
        var plain = new McpServerConfig { Command = "x", Args = ["a"] };
        Assert.Same(plain, McpReferences.Expand(plain, _ => "never").Config);

        var remote = new McpServerConfig { Url = "https://${HOST}/mcp", Headers = new() { ["Authorization"] = "Bearer ${TOKEN}" } };
        var (expanded, unset) = McpReferences.Expand(remote, n => n == "HOST" ? "example.com" : n == "TOKEN" ? "t0k" : null);
        Assert.Equal("https://example.com/mcp", expanded.Url);
        Assert.Equal("Bearer t0k", expanded.Headers!["Authorization"]);
        Assert.Empty(unset);
    }

    [Fact]
    public void AReference_NeverReachesTheAppsOwnKeys()
    {
        Assert.Null(ChildEnvironment.ForReference("NEONSIDEKICK_OPENAI_API_KEY"));
        Assert.Null(ChildEnvironment.ForReference("NEONSIDEKICK_TEST_LLM_URL"));
    }

    [Fact]
    public void AUrlWithAReference_IsNoProblemAtLoad()
    {
        var load = McpConfigFile.Load(Write("""{ "mcpServers": { "r": { "url": "${MCP_URL}" } } }"""), seal: false);
        Assert.Empty(load.Problems);
        Assert.Equal("${MCP_URL}", Assert.Single(load.Servers).Value.Url);
    }

    // ── #5, #64, #17: one entry at a time, cached ────────────────────────────────

    [Fact]
    public void AMistypedEntry_IsThatServersProblem_TheOthersLoad_AndItStillShadowsTheHomes()
    {
        string profile = Write("""{ "mcpServers": { "bad": { "command": "x", "args": 5 }, "good": { "command": "y" } } }""");
        string global = Write("""{ "mcpServers": { "bad": { "command": "z" } } }""");

        var load = McpConfigFile.Load(profile, seal: false);
        Assert.Equal("good", Assert.Single(load.Servers).Key);
        var problem = Assert.Single(load.Problems);
        Assert.Equal("bad", problem.Name);
        Assert.Equal(McpText.ServerSource(profile, "bad"), problem.Source);
        Assert.StartsWith(McpText.BadEntry(""), problem.Reason);

        var merged = McpCatalog.Merge(load, McpConfigFile.Load(global, seal: false));
        Assert.DoesNotContain(merged.Entries, e => e.Name == "bad" && e.Startable);
    }

    [Fact]
    public void AFileThatIsntJson_IsStillTheWholeFilesProblem()
    {
        var load = McpConfigFile.Load(Write("""{ "mcpServers": [1, 2] }"""), seal: false);
        Assert.Empty(load.Servers);
        Assert.Null(Assert.Single(load.Problems).Name);
    }

    [Fact]
    public void ABrokenEntry_WarnsOnce_OverTwoSealingLoads_AndALookingLoadIsQuiet()
    {
        string path = Write("""{ "mcpServers": { "bad": { "command": "x", "args": 5 }, "good": { "command": "y" } } }""");
        var warns = new List<string>();
        Action<DiagnosticEvent> capture = e =>
        {
            if (e.Level == DiagnosticLevel.Warning && e.Category == McpConfigFile.Category)
            {
                lock (warns)
                {
                    warns.Add(e.Message);
                }
            }
        };
        DiagnosticLog.Emitted += capture;
        try
        {
            McpConfigFile.Load(path, seal: false);
            McpConfigFile.Load(path);
            McpConfigFile.Load(path);
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        lock (warns)
        {
            Assert.Single(warns, w => w.Contains(" › bad", StringComparison.Ordinal));
        }
    }

    // ── #42: one write ───────────────────────────────────────────────────────

    [Fact]
    public void WriteValues_WritesEveryCurrentValueInOneGo_AndLeavesAStaleOneOut()
    {
        string path = Write("""
            { "mcpServers": { "a": { "command": "x", "env": { "A_TOKEN": "one", "B_TOKEN": "two", "C_TOKEN": "three" } } } }
            """);
        string[] Key(string name) => ["mcpServers", "a", "env", name];

        var errors = ConnectionsFileEdit.WriteValues(path,
        [
            new ConnectionsFileEdit.ValueWrite(Key("A_TOKEN"), "sealed-one", "one"),
            new ConnectionsFileEdit.ValueWrite(Key("B_TOKEN"), "sealed-two", "changed"),   // the file holds "two": left out
            new ConnectionsFileEdit.ValueWrite(Key("C_TOKEN"), "sealed-three", "three"),
        ]);

        Assert.Equal(1, Assert.Single(errors).Index);
        string after = File.ReadAllText(path);
        Assert.Contains("\"sealed-one\"", after);
        Assert.Contains("\"two\"", after);
        Assert.Contains("\"sealed-three\"", after);
        Assert.Contains("\"mcpServers\": { \"a\"", after);   // every other byte kept
    }

    // ── #8, #71, #83: the --allow entry ──────────────────────────────────────

    [Theory]
    [InlineData("github", "mcp:github")]
    [InlineData("my server", "\"mcp:my server\"")]
    [InlineData("a&b", "\"mcp:a&b\"")]
    [InlineData("a,b", "mcp")]
    [InlineData(" padded", "mcp")]
    [InlineData("say\"hi", "mcp")]
    [InlineData("", "mcp")]
    public void AllowEntry_QuotesWhatAShellReads_AndFallsBackToMcpForAnUntypableName(string server, string entry)
    {
        Assert.Equal(entry, McpText.AllowEntry(server));
        Assert.Equal(" A headless run allows it with --allow " + entry + ".", McpText.HeadlessAllowTail(server));
    }

    // ── #123: escapes after the cut ───────────────────────────────────────────

    [Fact]
    public void TheView_CutsTheRawLine_ThenEscapes_SoNoEscapeIsSplit()
    {
        // The value puts U+202E just before the cut: escaping first would have made it six characters and cut inside them.
        string text = new string('a', McpText.ViewLineChars - 20) + "\u202E" + new string('b', 50);
        using var doc = JsonDocument.Parse("\"" + text + "\"");
        var args = new Dictionary<string, JsonElement> { ["text"] = doc.RootElement.Clone() };
        int raw = McpText.ArgumentsJson(args, indented: true).Split('\n').Single(l => l.Contains("aaaa", StringComparison.Ordinal)).TrimEnd('\r').Length;

        var (lines, cut) = McpText.ArgumentLines(args);

        Assert.True(cut);
        string line = Assert.Single(lines, l => l.Contains("aaaa", StringComparison.Ordinal));
        Assert.DoesNotContain("\u202E", line, StringComparison.Ordinal);   // ordinal: a culture comparison ignores U+202E
        Assert.Contains(Files.NameScrub.Display("\u202E") + "bbbb", line, StringComparison.Ordinal);   // escaped whole, the raw b's after it
        Assert.EndsWith(McpText.MoreCharacters(raw - McpText.ViewLineChars), line);
    }

    [Fact]
    public void AuditText_RedactsCutsThenEscapes()
    {
        string text = new string('x', 15) + "\u202E" + "yyyy";
        string kept = ToolRedaction.AuditText(text, 16);
        Assert.Equal(new string('x', 15) + Files.NameScrub.Display("\u202E") + "…", kept);
        Assert.DoesNotContain("hunter2", ToolRedaction.AuditText("password=hunter2 and more", 12));
    }

    // ── #26: --allow beside --help ────────────────────────────────────────────

    [Fact]
    public void AllowWithHelpOrVersion_IsNoError()
    {
        Assert.Null(SidekickOptions.Parse(["--allow", "mcp", "--help"]).Error);
        Assert.Null(SidekickOptions.Parse(["--allow", "mcp", "--version"]).Error);
        Assert.Equal(SidekickOptions.AllowNeedsHeadless, SidekickOptions.Parse(["--allow", "mcp"]).Error);
    }

    // ── #97: a server's own share ─────────────────────────────────────────────

    [Fact]
    public void WithoutMcpServer_DropsThatServersNotesAlone_InOrder()
    {
        var facts = new SystemPromptFacts(null, null, null, false, [], false, false, McpEnabled: true, McpTools: 3,
            McpNotes: [new McpServerNote("a", "A's notes."), new McpServerNote("b", "B's notes."), new McpServerNote("c", "C's notes.")]);

        var without = facts.WithoutMcpServer("b");

        Assert.Equal(["a", "c"], without.McpNotes!.Select(n => n.Server));
        Assert.True(without.McpEnabled);   // the rule and the other servers' notes stay
        var none = facts with { McpNotes = null };
        Assert.Same(none, none.WithoutMcpServer("b"));
    }
}
