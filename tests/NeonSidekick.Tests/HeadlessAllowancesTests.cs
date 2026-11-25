using System.Text.Json;
using NeonSidekick.App;
using NeonSidekick.Diagnostics;
using NeonSidekick.Mcp;
using NeonSidekick.Settings;

namespace NeonSidekick.Tests;

/// <summary>A headless run's <c>--allow</c> (2026-11-19, feature idea #45): each kind allowed or refused, the MCP entries' matching, the summary lines and the log.</summary>
public class HeadlessAllowancesTests
{
    private static McpAsk Ask(string server, string name) =>
        new(server, "tool", name, new Dictionary<string, JsonElement>(), McpChangeNote.Unstated);

    [Fact]
    public async Task EachKind_IsAllowedOrRefused_AndOnlyTheRefusedAreCounted()
    {
        var allow = new HeadlessAllowances(["docker", "ha"]);

        Assert.True(await allow.Confirm(AllowKind.Docker)("Stop web?", CancellationToken.None));
        Assert.True(await allow.Confirm(AllowKind.Ha)("Turn on the fan?", CancellationToken.None));
        Assert.Null(await allow.Confirm(AllowKind.Print)("Print a.pdf?", CancellationToken.None));
        Assert.Null(await allow.Advisor(new AdvisorEntry("dba"), "Which index?", CancellationToken.None));

        Assert.Equal([AllowKind.Print, AllowKind.Advisor], allow.Refusals);
        Assert.Equal([AllowText.RefusedSummary(AllowKind.Print, 1), AllowText.RefusedSummary(AllowKind.Advisor, 1)], allow.SummaryLines());
        Assert.Equal("1 Docker change was refused (--allow docker allows it)", AllowText.RefusedSummary(AllowKind.Docker, 1));
        Assert.Equal("2 prints were refused (--allow print allows them)", AllowText.RefusedSummary(AllowKind.Print, 2));
    }

    [Fact]
    public async Task Mcp_AllowsEveryServer_AndMcpServer_OneMatchedWithoutRegardToCase()
    {
        var all = new HeadlessAllowances(["mcp"]);
        Assert.Equal(new McpVerdict.Allowed(AllowText.AllowedBy("mcp")), await all.Mcp(Ask("anything", "anything__x"), CancellationToken.None));

        var one = new HeadlessAllowances(["mcp:GitHub"]);
        one.KnowServers(["github", "notes"]);
        Assert.Equal(new McpVerdict.Allowed(AllowText.AllowedBy("mcp:GitHub")), await one.Mcp(Ask("github", "github__push"), CancellationToken.None));
        Assert.True(one.AllowsMcp("github"));
        Assert.False(one.AllowsMcp("notes"));
        Assert.Equal(new McpVerdict.NotAsked(McpText.HeadlessAllowTail("notes")), await one.Mcp(Ask("notes", "notes__add"), CancellationToken.None));
    }

    [Fact]
    public void AnExactName_PicksThatServerAlone()
    {
        var exact = new HeadlessAllowances(["mcp:github"]);
        exact.KnowServers(["GitHub", "github"]);
        Assert.True(exact.AllowsMcp("github"));
        Assert.False(exact.AllowsMcp("GitHub"));

        // #75 (2026-11-20): a case-blind name over two servers is an error, and allows neither.
        var blind = new HeadlessAllowances(["mcp:GITHUB"]);
        Assert.Equal([AllowText.Ambiguous("GITHUB", ["GitHub", "github"])], blind.KnowServers(["GitHub", "github"]));
        Assert.False(blind.AllowsMcp("github"));
        Assert.False(blind.AllowsMcp("GitHub"));
        Assert.Equal("--allow mcp:GITHUB matches GitHub and github; give the name as mcp.json spells it", AllowText.Ambiguous("GITHUB", ["GitHub", "github"]));
    }

    [Fact]
    public async Task TheMcpLine_NamesEachRefusedServer_InTheOrderFirstRefused()
    {
        var none = new HeadlessAllowances();
        await none.Mcp(Ask("github", "github__create_issue"), CancellationToken.None);
        await none.Mcp(Ask("notes", "notes__add"), CancellationToken.None);
        await none.Mcp(Ask("github", "github__push_files"), CancellationToken.None);

        Assert.Equal(["3 MCP calls were refused: github__create_issue, notes__add, github__push_files (--allow mcp:github,mcp:notes allows them)"], none.SummaryLines());
        Assert.Equal(3, none.Refusals.Count);
    }

    [Fact]
    public void AnEntryThatNamesNoServer_IsAnError()
    {
        var allow = new HeadlessAllowances(["mcp:typo", "mcp:GitHub"]);
        Assert.Equal([AllowText.NamesNoServer("typo")], allow.KnowServers(["github"]));
        Assert.Equal("--allow mcp:typo names no MCP server", AllowText.NamesNoServer("typo"));
    }

    [Fact]
    public async Task AnAllowedCall_IsLoggedRedacted_UnderItsFamilysCategory()
    {
        var lines = new List<(string Category, string Message)>();
        Action<DiagnosticEvent> capture = e => { lock (lines) { lines.Add((e.Category, e.Message)); } };
        DiagnosticLog.Emitted += capture;
        try
        {
            await new HeadlessAllowances(["docker"]).Confirm(AllowKind.Docker)("Run with password=hunter2?", CancellationToken.None);
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        lock (lines)
        {
            var line = Assert.Single(lines, l => l.Message.StartsWith(AllowText.AllowedBy("docker") + ": ", StringComparison.Ordinal));
            Assert.Equal(Docker.DockerText.Category, line.Category);
            Assert.DoesNotContain("hunter2", line.Message);
        }
    }

    [Fact]
    public void TryParse_TheWordsWithoutRegardToCase_TheServerAsTyped_ARepeatKeptOnce()
    {
        var entries = new List<string>();
        Assert.True(HeadlessAllowances.TryParse("Docker, MCP:GitHub ,mcp", entries, out var error));
        Assert.True(HeadlessAllowances.TryParse("docker", entries, out error));
        Assert.Null(error);
        Assert.Equal(["docker", "mcp:GitHub", "mcp"], entries);

        Assert.False(HeadlessAllowances.TryParse("mcp: ", entries, out error));
        Assert.Equal(SidekickOptions.AllowBlankServer, error);
        Assert.False(HeadlessAllowances.TryParse("shell", entries, out error));
        Assert.Equal(SidekickOptions.AllowUnknown("shell"), error);
        Assert.Contains("docker, print, ha, advisor, mcp, code, mcp:<server> or code:<language>", error);
    }

    /// <summary><c>--allow code</c> and <c>code:&lt;language&gt;</c> (2026-11-24, the user's choice): every language or one, as the tool's own prefix.</summary>
    [Fact]
    public void Code_AllowsEveryLanguage_OrOne_AsTheToolAsksForIt()
    {
        var entries = new List<string>();
        Assert.True(HeadlessAllowances.TryParse("code:python, CODE:Python ,Code", entries, out var error));
        Assert.Null(error);
        Assert.Equal(["code:python", "code"], entries);

        Assert.False(HeadlessAllowances.TryParse("code:ruby", entries, out error));
        Assert.Equal(SidekickOptions.AllowUnknownLanguage("ruby"), error);
        Assert.Equal("--allow code: doesn't know \"ruby\"; use powershell, python, node", error);
        Assert.False(HeadlessAllowances.TryParse("code: ", entries, out error));
        Assert.Equal(SidekickOptions.AllowUnknownLanguage(""), error);

        var every = new HeadlessAllowances(["code"]);
        Assert.True(every.Allows(AllowKind.Code));
        Assert.Equal(["code:powershell", "code:python", "code:node"], every.ScriptPrefixes);
        var one = new HeadlessAllowances(["code:python"]);
        Assert.False(one.Allows(AllowKind.Code));
        Assert.Equal(["python"], one.ScriptLanguages);
        Assert.Equal(["code:python"], one.ScriptPrefixes);
        Assert.Empty(new HeadlessAllowances(["docker"]).ScriptPrefixes);
    }

    /// <summary>A script the gate refused headless is summed up by the entry that allows it (2026-11-24); the start's log line and the model's sentence are pinned.</summary>
    [Fact]
    public void ARefusedScript_IsSummedUp_ByItsLanguagesEntry()
    {
        var allow = new HeadlessAllowances([]);
        allow.ScriptRefused("node");
        allow.ScriptRefused("python");
        allow.ScriptRefused("node");

        Assert.Equal([AllowText.ScriptsRefusedSummary(3, ["code:node", "code:python"])], allow.SummaryLines());
        Assert.Equal("1 script was refused (--allow code:python allows it)", AllowText.ScriptsRefusedSummary(1, ["code:python"]));
        Assert.Equal("3 scripts were refused (--allow code:node,code:python allows them)", AllowText.ScriptsRefusedSummary(3, ["code:node", "code:python"]));
        Assert.Equal("--allow code lets this run's scripts run: code:python, code:node", AllowText.ScriptsAllowedLogLine(["code:python", "code:node"]));
        Assert.Equal(
            "Error: the python script was not run: there was no screen to ask the user on (Shell command policy is ask; --allow code:python or --yolo would let it run). Allowed prefixes: none. Do not retry it or work around the refusal; tell the user what could not run",
            Shell.ShellText.ScriptNotAskable("python", []));
    }
}
