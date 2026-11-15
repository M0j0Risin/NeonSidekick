using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Mcp;
using ToolSchema = NeonSidekick.Llm.Tools.ToolSchema;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// Misnamed arguments answered (2026-11-15, feature idea #42, <see cref="ToolArgumentCheck"/>): a count synonym read as the tool's one member of
/// the group, any other unknown key refused with the nearest name and the list, null ignored, a tool with no arguments run with a note, and each
/// MCP skip rule.
/// </summary>
public class ToolArgumentCheckTests
{
    private static readonly JsonElement Search = ToolSchema.Parse(
        """{"type":"object","properties":{"query":{"type":"string"},"max_results":{"type":"integer"}},"required":["query"]}""");

    private static readonly JsonElement NoArguments = ToolSchema.Parse("""{"type":"object","properties":{}}""");

    private static Dictionary<string, object?> Args(params (string Name, object? Value)[] values) =>
        values.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);

    private static ArgumentCheck Check(JsonElement schema, Dictionary<string, object?> args, bool mcp = false, ToolExtras? extras = null) =>
        ToolArgumentCheck.Check(schema, mcp, extras ?? ToolExtras.None, "web_search", args);

    private static JsonElement Json(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    [Fact]
    public void DeclaredKeys_Pass()
    {
        Assert.Same(ArgumentCheck.Passed, Check(Search, Args(("query", "x"), ("max_results", 5))));
        Assert.Same(ArgumentCheck.Passed, Check(Search, Args()));
    }

    [Fact]
    public void ACountSynonym_IsReadAsTheToolsOneMember()
    {
        var result = Check(Search, Args(("query", "x"), ("limit", 5)));
        Assert.Equal(ArgumentVerdict.Renamed, result.Verdict);
        Assert.Equal(5, result.Arguments!["max_results"]);
        Assert.False(result.Arguments.ContainsKey("limit"));
        Assert.Equal("x", result.Arguments["query"]);
        Assert.Equal([("limit", "max_results")], result.Renames);
        Assert.Equal("Tool web_search: read limit as max_results", TurnText.RenameLogLine("web_search", "limit", "max_results"));
    }

    [Fact]
    public void ACountSynonym_WithTheTargetSentToo_IsRefused()
    {
        var result = Check(Search, Args(("query", "x"), ("limit", 5), ("max_results", 10)));
        Assert.Equal(ArgumentVerdict.Refused, result.Verdict);
        Assert.Equal("Error: web_search takes no argument 'limit'; send only 'max_results'. It takes: query, max_results. Nothing ran.", result.Text);
    }

    [Fact]
    public void ACountSynonym_WithTheTargetSentAsNull_IsStillRenamed()
    {
        var result = Check(Search, Args(("query", "x"), ("limit", 5), ("max_results", null)));
        Assert.Equal(ArgumentVerdict.Renamed, result.Verdict);
        Assert.Equal(5, result.Arguments!["max_results"]);
    }

    [Fact]
    public void ACountSynonym_ForAToolWithNoneOrTwoMembers_IsRefused()
    {
        var none = ToolSchema.Parse("""{"type":"object","properties":{"query":{"type":"string"}}}""");
        Assert.Equal("Error: web_search takes no argument 'limit'. It takes: query. Nothing ran.", Check(none, Args(("limit", 5))).Text);

        var two = ToolSchema.Parse("""{"type":"object","properties":{"max":{"type":"integer"},"tail":{"type":"integer"}}}""");
        var result = Check(two, Args(("limit", 5)));
        Assert.Equal(ArgumentVerdict.Refused, result.Verdict);
        Assert.Equal("Error: web_search takes no argument 'limit'. It takes: max, tail. Nothing ran.", result.Text);
    }

    [Fact]
    public void AMisnamedKey_IsRefused_WithTheNearestName()
    {
        var result = Check(Search, Args(("qurey", "x")));
        Assert.Equal(ArgumentVerdict.Refused, result.Verdict);
        Assert.Equal("Error: web_search takes no argument 'qurey'; did you mean 'query'? It takes: query, max_results. Nothing ran.", result.Text);
    }

    [Fact]
    public void ANearMissOfACountSynonym_IsAnsweredWithTheToolsMember()
    {
        Assert.Equal(
            "Error: web_search takes no argument 'limt'; did you mean 'max_results'? It takes: query, max_results. Nothing ran.",
            Check(Search, Args(("query", "x"), ("limt", 5))).Text);
    }

    [Fact]
    public void AKeyNearNothing_GetsTheListAlone()
    {
        Assert.Equal("Error: web_search takes no argument 'zzz'. It takes: query, max_results. Nothing ran.", Check(Search, Args(("zzz", 1))).Text);
    }

    [Fact]
    public void ALongKey_SkipsTheSearch_AndIsEchoedShort()
    {
        string key = new('q', 100_000);
        var result = Check(Search, Args((key, 1)));
        Assert.Equal(ArgumentVerdict.Refused, result.Verdict);
        Assert.True(result.Text!.Length < 200, result.Text);
        Assert.DoesNotContain("did you mean", result.Text, StringComparison.Ordinal);
        Assert.Contains(ArgumentText.Echo(key), result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void SeveralUnknownKeys_AreNamedInOneAnswer_TheSynonymWithItsRealName()
    {
        var result = Check(Search, Args(("qurey", "x"), ("limit", 5), ("zzz", 1)));
        Assert.Equal(
            "Error: web_search takes no arguments 'qurey' (did you mean 'query'?), 'limit' (it is 'max_results') and 'zzz'. It takes: query, max_results. Nothing ran.",
            result.Text);
    }

    [Fact]
    public void MoreThanFiveUnknownKeys_AreCounted()
    {
        var result = Check(Search, Args(("a1", 1), ("a2", 1), ("a3", 1), ("a4", 1), ("a5", 1), ("a6", 1), ("a7", 1)));
        Assert.Equal(
            "Error: web_search takes no arguments 'a1', 'a2', 'a3', 'a4', 'a5', and 2 more. It takes: query, max_results. Nothing ran.",
            result.Text);
    }

    [Fact]
    public void AnUnknownKeySentAsNull_IsNotGiven()
    {
        Assert.Same(ArgumentCheck.Passed, Check(Search, Args(("query", "x"), ("qurey", null))));
        Assert.Same(ArgumentCheck.Passed, Check(Search, Args(("query", "x"), ("qurey", Json("null")))));
    }

    [Fact]
    public void AToolWithNoArguments_Runs_WithANote()
    {
        var result = ToolArgumentCheck.Check(NoArguments, false, ToolExtras.None, "recall_memory", Args(("query", "x")));
        Assert.Equal(ArgumentVerdict.Pass, result.Verdict);
        Assert.Equal("(ignored: query; recall_memory takes no arguments)", result.Note);
    }

    [Fact]
    public void ExtraArguments_AreKnown_AndAnyPassesAll()
    {
        Assert.Same(ArgumentCheck.Passed, Check(Search, Args(("query", "x"), ("reason", "y")), extras: new ToolExtras(["reason"], false)));
        Assert.Same(ArgumentCheck.Passed, Check(Search, Args(("whatever", "y")), extras: new ToolExtras([], true)));
    }

    [Fact]
    public void ASchemaThatCantBeJudged_Passes()
    {
        Assert.Same(ArgumentCheck.Passed, Check(Json("{}"), Args(("x", 1))));
        Assert.Same(ArgumentCheck.Passed, Check(Json("""{"type":"object","properties":{"a":{}},"additionalProperties":true}"""), Args(("x", 1))));
        Assert.Same(ArgumentCheck.Passed, Check(Json("""{"type":"object","properties":{"a":{}},"additionalProperties":{"type":"string"}}"""), Args(("x", 1))));
        foreach (string combinator in (string[])["oneOf", "anyOf", "allOf"])
        {
            Assert.Same(ArgumentCheck.Passed, Check(Json($$$"""{"type":"object","properties":{"a":{}},"{{{combinator}}}":[]}"""), Args(("x", 1))));
        }

        Assert.Same(ArgumentCheck.Passed, Check(Json("""{"type":"object","properties":{"a":{}},"$ref":"#/x"}"""), Args(("x", 1))));
        Assert.Equal(ArgumentVerdict.Refused, Check(Json("""{"type":"object","properties":{"a":{}},"additionalProperties":false}"""), Args(("x", 1))).Verdict);
    }

    [Fact]
    public void AnMcpTool_IsJudgedByItsServersSchema_ButNeverWithNoProperties()
    {
        Assert.Equal(ArgumentVerdict.Refused, Check(Search, Args(("qurey", "x")), mcp: true).Verdict);
        Assert.Same(ArgumentCheck.Passed, Check(NoArguments, Args(("query", "x")), mcp: true));
        Assert.Same(ArgumentCheck.Passed, Check(Json("{}"), Args(("query", "x")), mcp: true));
        Assert.Same(ArgumentCheck.Passed, Check(Json("""{"type":"object","properties":{"a":{}},"additionalProperties":true}"""), Args(("x", 1)), mcp: true));
    }

    /// <summary>Every tool that reads undeclared names on purpose lets them through (the §1 scan's findings).</summary>
    [Fact]
    public void TheToolsThatReadAliases_PassThem()
    {
        Assert.Equal(CameraCaptureTool.PromptAliases, ((IAcceptsArguments)new CameraCaptureTool(null)).ExtraArguments);
        Assert.Equal([.. ScreenCaptureTool.TargetAliases, .. CameraCaptureTool.PromptAliases], ((IAcceptsArguments)new ScreenCaptureTool(null)).ExtraArguments);
        Assert.Equal(["old_text", "new_text", "edits"], WriteFileTool.EditArguments);
        Assert.True(((IAcceptsArguments)new AskUserTool((_, _) => Task.FromResult<IReadOnlyList<AskAnswer>?>(null), () => new NeonSidekick.Settings.AppSettingsData())).AcceptsAnyArgument);
    }

    /// <summary>A refused call never reaches the tool, and is marked refused; a renamed one is rewritten on the call, so the history holds it as it ran.</summary>
    [Fact]
    public async Task TheInvokePath_RefusesBeforeTheToolRuns_AndRewritesARename()
    {
        var echo = new EchoTool();
        var refused = await Assistant.InvokeToolWithDiffAsync([echo], new FunctionCallContent("c1", "echo", Args(("text", "hi"), ("txet", "x"))), CancellationToken.None);
        Assert.True(refused.Refused);
        Assert.Equal("Error: echo takes no argument 'txet'; did you mean 'text'? It takes: text. Nothing ran.", refused.Text);
        Assert.Empty(echo.Received);

        var counted = new CountTool();
        var call = new FunctionCallContent("c2", "count", Args(("limit", 3)));
        var renamed = await Assistant.InvokeToolWithDiffAsync([counted], call, CancellationToken.None);
        Assert.False(renamed.Refused);
        Assert.Equal("max_rows=3", renamed.Text);
        Assert.Equal(3, call.Arguments!["max_rows"]);
        Assert.False(call.Arguments.ContainsKey("limit"));
    }

    /// <summary>A no-argument tool runs, and its result ends with the note as the last line.</summary>
    [Fact]
    public async Task TheInvokePath_NotesIgnoredKeys_AfterTheResult()
    {
        var fixedTool = new NoArgumentTool();
        var result = await Assistant.InvokeToolWithDiffAsync([fixedTool], new FunctionCallContent("c1", "none", Args(("query", "x"))), CancellationToken.None);
        Assert.False(result.Refused);
        Assert.Equal("done\n(ignored: query; none takes no arguments)", result.Text);
    }

    /// <summary>A real server's tool is judged by the schema it sent; the key it declares reaches it as sent.</summary>
    [Fact]
    public async Task AnMcpToolsUndeclaredKey_IsRefused_AndItsDeclaredOnesReachTheServer()
    {
        await using var server = McpPipeServer.Start([("echo", "Echoes.")]);
        await using var client = await McpClient.CreateAsync(server.ClientTransport);
        var listed = await client.ListToolsAsync((RequestOptions?)null);
        AIFunction tool = new McpTool("pipe", client, listed[0], "pipe__echo");

        var refused = await Assistant.InvokeToolWithDiffAsync([tool], new FunctionCallContent("c1", "pipe__echo", Args(("txt", "ping"))), CancellationToken.None);
        Assert.True(refused.Refused);
        Assert.Empty(server.Calls);

        var (text, _) = await Assistant.InvokeToolAsync([tool], new FunctionCallContent("c2", "pipe__echo", Args(("text", "ping"))), CancellationToken.None);
        Assert.Equal("echo: ping", text);
    }

    private sealed class CountTool : AIFunction
    {
        private static readonly JsonElement Schema = ToolSchema.Parse("""{"type":"object","properties":{"max_rows":{"type":"integer"}}}""");

        public override string Name => "count";

        public override JsonElement JsonSchema => Schema;

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            new("max_rows=" + ToolArguments.ReadString(arguments, "max_rows"));
    }

    private sealed class NoArgumentTool : AIFunction
    {
        public override string Name => "none";

        public override JsonElement JsonSchema => NoArguments;

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) => new("done");
    }
}
