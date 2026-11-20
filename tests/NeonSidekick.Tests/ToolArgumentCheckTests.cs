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
/// Misnamed arguments answered (2026-11-15, feature idea #42, <see cref="ToolArgumentCheck"/>): an unknown key refused with its likely name (a
/// count synonym hinted toward the tool's own, never renamed; another agent's name; a case-only twin; the nearest declared name) and the list,
/// harmless values ignored, commentary noted, a tool with no arguments run with a note, and each skip rule (MCP judged only when closed).
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
    public void ACountSynonym_IsRefused_NamingTheToolsOwn()
    {
        var call = Args(("query", "x"), ("limit", 5));
        var result = Check(Search, call);
        Assert.Equal(ArgumentVerdict.Refused, result.Verdict);
        Assert.Equal("Error: web_search takes no argument 'limit'; it is 'max_results'. It takes: query, max_results. Nothing ran.", result.Text);
        Assert.Equal(5, call["limit"]);
        Assert.False(call.ContainsKey("max_results"));
    }

    [Fact]
    public void ACountSynonym_WithTheTargetSentToo_IsRefused()
    {
        var result = Check(Search, Args(("query", "x"), ("limit", 5), ("max_results", 10)));
        Assert.Equal(ArgumentVerdict.Refused, result.Verdict);
        Assert.Equal("Error: web_search takes no argument 'limit'; send only 'max_results'. It takes: query, max_results. Nothing ran.", result.Text);
    }

    [Fact]
    public void ACountSynonym_WithTheTargetSentAsNull_GetsTheNameAlone()
    {
        var result = Check(Search, Args(("query", "x"), ("limit", 5), ("max_results", null)));
        Assert.Equal("Error: web_search takes no argument 'limit'; it is 'max_results'. It takes: query, max_results. Nothing ran.", result.Text);
    }

    [Fact]
    public void ACountSynonym_ForAToolWithNoneOrTwoMembers_GetsNoCountHint()
    {
        var none = ToolSchema.Parse("""{"type":"object","properties":{"query":{"type":"string"}}}""");
        Assert.Equal("Error: web_search takes no argument 'limit'. It takes: query. Nothing ran.", Check(none, Args(("limit", 5))).Text);

        var two = ToolSchema.Parse("""{"type":"object","properties":{"max":{"type":"integer"},"tail":{"type":"integer"}}}""");
        var result = Check(two, Args(("limit", 5)));
        Assert.Equal(ArgumentVerdict.Refused, result.Verdict);
        Assert.Equal("Error: web_search takes no argument 'limit'. It takes: max, tail. Nothing ran.", result.Text);
    }

    /// <summary>A statement tool's max_rows limits only what is shown (#41): a count name is never pointed there.</summary>
    [Fact]
    public void ACountSynonym_OnAStatementTool_SaysMaxRowsOnlyLimitsWhatIsShown()
    {
        var execute = ToolSchema.Parse("""{"type":"object","properties":{"statement":{"type":"string"},"max_rows":{"type":"integer"}}}""");
        var result = ToolArgumentCheck.Check(execute, false, ToolExtras.None, SqlExecuteTool.ToolName, Args(("statement", "x"), ("limit", 5)));
        Assert.Equal(ArgumentVerdict.Refused, result.Verdict);
        Assert.Contains(TurnText.RowsShownHint("max_rows"), result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(TurnText.SynonymHint("max_rows"), result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AMisnamedKey_IsRefused_WithTheNearestName()
    {
        var result = Check(Search, Args(("qurey", "x")));
        Assert.Equal(ArgumentVerdict.Refused, result.Verdict);
        Assert.Equal("Error: web_search takes no argument 'qurey'; did you mean 'query'? It takes: query, max_results. Nothing ran.", result.Text);
    }

    /// <summary>The near-miss search runs over the declared names only (#80): <c>limt</c> finds a declared <c>limit</c>, never another count name.</summary>
    [Fact]
    public void ANearMiss_IsSearchedAmongTheDeclaredNamesOnly()
    {
        Assert.Equal("Error: web_search takes no argument 'limt'. It takes: query, max_results. Nothing ran.", Check(Search, Args(("query", "x"), ("limt", 5))).Text);
        Assert.Equal("Error: web_search takes no argument 'map'. It takes: query, max_results. Nothing ran.", Check(Search, Args(("query", "x"), ("map", 5))).Text);

        var limited = ToolSchema.Parse("""{"type":"object","properties":{"query":{"type":"string"},"limit":{"type":"integer"}}}""");
        Assert.Contains(TurnText.NearestHint("limit"), Check(limited, Args(("limt", 5))).Text, StringComparison.Ordinal);
    }

    /// <summary>Other agents' names for the app's own (#74, #118) are hinted, on an exact match.</summary>
    [Theory]
    [InlineData("offset", "start_line")]
    [InlineData("limit", "max_lines")]
    [InlineData("old_string", "old_text")]
    [InlineData("new_string", "new_text")]
    [InlineData("file_path", "path")]
    public void AnotherAgentsName_IsHintedTowardTheAppsOwn(string sent, string meant)
    {
        var file = ToolSchema.Parse(
            """{"type":"object","properties":{"path":{"type":"string"},"start_line":{"type":"integer"},"max_lines":{"type":"integer"},"old_text":{"type":"string"},"new_text":{"type":"string"}}}""");
        var result = ToolArgumentCheck.Check(file, false, ToolExtras.None, "read_file", Args((sent, 5)));
        Assert.Equal(ArgumentVerdict.Refused, result.Verdict);
        Assert.Contains("'" + sent + "'; " + TurnText.NearestHint(meant), result.Text, StringComparison.Ordinal);
    }

    /// <summary>A key that differs from a declared name only in case (#121) is answered with that name, or told to drop it when that name was sent.</summary>
    [Fact]
    public void ACaseOnlyTwin_IsAnsweredWithTheDeclaredName_OrDropped()
    {
        var read = ToolSchema.Parse("""{"type":"object","properties":{"path":{"type":"string"},"paths":{"type":"array"},"start_line":{"type":"integer"}}}""");
        var both = ToolArgumentCheck.Check(read, false, ToolExtras.None, "read_file", Args(("path", "a.txt"), ("Path", "b.txt")));
        Assert.Contains("'Path'; " + TurnText.CaseTwinHint("Path", "path"), both.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("paths'?", both.Text, StringComparison.Ordinal);

        var alone = ToolArgumentCheck.Check(read, false, ToolExtras.None, "read_file", Args(("Path", "b.txt")));
        Assert.Contains("'Path'; " + TurnText.NearestHint("path"), alone.Text, StringComparison.Ordinal);
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

    /// <summary>Several keys read "doesn't take … or …" (#108), each with its hint.</summary>
    [Fact]
    public void SeveralUnknownKeys_AreNamedInOneAnswer_EachWithItsHint()
    {
        var result = Check(Search, Args(("qurey", "x"), ("limit", 5), ("zzz", 1)));
        Assert.Equal(
            "Error: web_search doesn't take 'qurey' (did you mean 'query'?), 'limit' (it is 'max_results') or 'zzz'. It takes: query, max_results. Nothing ran.",
            result.Text);
    }

    [Fact]
    public void MoreThanFiveUnknownKeys_AreCounted()
    {
        var result = Check(Search, Args(("a1", 1), ("a2", 1), ("a3", 1), ("a4", 1), ("a5", 1), ("a6", 1), ("a7", 1)));
        Assert.Equal(
            "Error: web_search doesn't take 'a1', 'a2', 'a3', 'a4', 'a5', or 2 more. It takes: query, max_results. Nothing ran.",
            result.Text);
    }

    /// <summary>A server's 300 names aren't all listed (#76): the first twenty, then a count.</summary>
    [Fact]
    public void TheToolsOwnNames_AreCappedAtTwenty()
    {
        string properties = string.Join(",", Enumerable.Range(1, ToolArgumentCheck.MaxTakes + 5).Select(i => $"\"p{i}\":{{}}"));
        var wide = ToolSchema.Parse("{\"type\":\"object\",\"properties\":{" + properties + "}}");
        var result = Check(wide, Args(("zzz", 1)));
        Assert.Contains($"p{ToolArgumentCheck.MaxTakes}, and 5 more. Nothing ran.", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain($"p{ToolArgumentCheck.MaxTakes + 1}", result.Text, StringComparison.Ordinal);
    }

    /// <summary>An unknown key whose value would change nothing (#55) is left aside silently: null, "", false, [] and {}.</summary>
    [Fact]
    public void AnUnknownKeyWithAHarmlessValue_IsNotGiven()
    {
        foreach (object? harmless in (object?[])[null, Json("null"), "", false, Json("\"\""), Json("false"), Json("[]"), Json("{}"), new List<string>()])
        {
            Assert.Same(ArgumentCheck.Passed, Check(Search, Args(("query", "x"), ("qurey", harmless))));
        }

        foreach (object? given in (object?[])[0, true, "x", Json("0"), Json("true"), Json("[1]"), Json("""{"a":1}""")])
        {
            Assert.Equal(ArgumentVerdict.Refused, Check(Search, Args(("query", "x"), ("qurey", given))).Verdict);
        }
    }

    /// <summary>A commentary key (#55) runs the call, named in the note; beside a real unknown key the call is still refused.</summary>
    [Fact]
    public void ACommentaryKey_RunsTheCall_WithANote()
    {
        var result = Check(Search, Args(("query", "x"), ("reason", "the user asked")));
        Assert.Equal(ArgumentVerdict.Pass, result.Verdict);
        Assert.Equal("(ignored: reason; web_search doesn't read it)", result.Note);

        var two = Check(Search, Args(("query", "x"), ("reason", "a"), ("explanation", "b")));
        Assert.Equal("(ignored: reason, explanation; web_search doesn't read them)", two.Note);

        var mixed = Check(Search, Args(("query", "x"), ("reason", "a"), ("zzz", 1)));
        Assert.Equal(ArgumentVerdict.Refused, mixed.Verdict);
        Assert.DoesNotContain("reason", mixed.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AToolWithNoArguments_Runs_WithANote()
    {
        var result = ToolArgumentCheck.Check(NoArguments, false, ToolExtras.None, "recall_memory", Args(("query", "x")));
        Assert.Equal(ArgumentVerdict.Pass, result.Verdict);
        Assert.Equal("(ignored: query; recall_memory takes no arguments)", result.Note);
    }

    [Fact]
    public void ExtraArguments_AreKnown()
    {
        Assert.Same(ArgumentCheck.Passed, Check(Search, Args(("query", "x"), ("share", "y")), extras: new ToolExtras(["share"])));
        Assert.Equal(ArgumentVerdict.Refused, Check(Search, Args(("whatever", "y")), extras: new ToolExtras(["share"])).Verdict);
    }

    [Fact]
    public void ASchemaThatCantBeJudged_Passes()
    {
        Assert.Same(ArgumentCheck.Passed, Check(Json("{}"), Args(("x", 1))));
        Assert.Same(ArgumentCheck.Passed, Check(Json("""{"type":"object","properties":{"a":{}},"additionalProperties":true}"""), Args(("x", 1))));
        Assert.Same(ArgumentCheck.Passed, Check(Json("""{"type":"object","properties":{"a":{}},"additionalProperties":{"type":"string"}}"""), Args(("x", 1))));
        foreach (string combinator in (string[])["oneOf", "anyOf", "allOf", "dependentSchemas"])
        {
            Assert.Same(ArgumentCheck.Passed, Check(Json($$$"""{"type":"object","properties":{"a":{}},"{{{combinator}}}":[]}"""), Args(("x", 1))));
        }

        // #60: the keywords that admit or shape names beyond properties.
        foreach (string open in (string[])["patternProperties", "if", "then", "else", "unevaluatedProperties"])
        {
            Assert.Same(ArgumentCheck.Passed, Check(Json($$$"""{"type":"object","properties":{"a":{}},"{{{open}}}":{}}"""), Args(("x", 1))));
        }

        Assert.Same(ArgumentCheck.Passed, Check(Json("""{"type":"object","properties":{"a":{}},"$ref":"#/x"}"""), Args(("x", 1))));
        Assert.Equal(ArgumentVerdict.Refused, Check(Json("""{"type":"object","properties":{"a":{}},"additionalProperties":false}"""), Args(("x", 1))).Verdict);
        Assert.Equal(ArgumentVerdict.Refused, Check(Json("""{"type":"object","properties":{"a":{}},"propertyNames":{"maxLength":9}}"""), Args(("x", 1))).Verdict);
    }

    /// <summary>A server's tool is judged only when its schema is closed (#2: servers often read undeclared keys), and never with no properties.</summary>
    [Fact]
    public void AnMcpTool_IsJudgedOnlyByAClosedSchema_AndNeverWithNoProperties()
    {
        var closed = Json("""{"type":"object","properties":{"query":{"type":"string"}},"additionalProperties":false}""");
        Assert.Equal(ArgumentVerdict.Refused, Check(closed, Args(("qurey", "x")), mcp: true).Verdict);
        Assert.Same(ArgumentCheck.Passed, Check(Search, Args(("qurey", "x")), mcp: true));
        Assert.Same(ArgumentCheck.Passed, Check(NoArguments, Args(("query", "x")), mcp: true));
        Assert.Same(ArgumentCheck.Passed, Check(Json("""{"type":"object","properties":{},"additionalProperties":false}"""), Args(("query", "x")), mcp: true));
        Assert.Same(ArgumentCheck.Passed, Check(Json("{}"), Args(("query", "x")), mcp: true));
        Assert.Same(ArgumentCheck.Passed, Check(Json("""{"type":"object","properties":{"a":{}},"additionalProperties":true}"""), Args(("x", 1)), mcp: true));
    }

    /// <summary>Every tool that reads undeclared names on purpose lists them (the §1 scan's findings).</summary>
    [Fact]
    public void TheToolsThatReadAliases_ListThem()
    {
        Assert.Equal(CameraCaptureTool.PromptAliases, ((IAcceptsArguments)new CameraCaptureTool(null)).ExtraArguments);
        Assert.Equal([.. ScreenCaptureTool.TargetAliases, .. CameraCaptureTool.PromptAliases], ((IAcceptsArguments)new ScreenCaptureTool(null)).ExtraArguments);
        Assert.Equal(["old_text", "new_text", "replace_all", "edits"], WriteFileTool.EditArguments);
        var ask = new AskUserTool((_, _) => Task.FromResult<IReadOnlyList<AskAnswer>?>(null), () => new NeonSidekick.Settings.AppSettingsData());
        Assert.Equal(["question", "text", "title", "type", "options", "choices", "multi", "multiple"], ((IAcceptsArguments)ask).ExtraArguments);
    }

    /// <summary>
    /// Every tool that reads undeclared names (2026-11-15, the tool-arguments review's #11): each extra, sent alone, passes the check and is
    /// answered in the tool's own words, never the generic "It takes:" refusal. The type list must name every IAcceptsArguments tool, so a new
    /// one joins the table. (open_url's window extra is held in WebToolsTests, where its window hook lives.)
    /// </summary>
    [Fact]
    public async Task EveryExtraArgument_ReachesTheTool_AndIsAnsweredInItsOwnWords()
    {
        string root = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var time = new ManualTimeProvider();
            var files = new NeonSidekick.Files.WorkingDirectory(() => root, time);
            var settings = new NeonSidekick.Settings.AppSettingsData();
            var unc = new NeonSidekick.Unc.UncAccess(() => new NeonSidekick.Unc.UncCatalog([], []), time);
            var browser = new FakeHeadlessBrowser();
            var skillRoots = new NeonSidekick.Skills.SkillRoots(Path.Combine(root, "p"), Path.Combine(root, "g"), Path.Combine(root, "e"));
            var ask = new AskUserTool((_, _) => Task.FromResult<IReadOnlyList<AskAnswer>?>(null), () => settings);
            (AIFunction Tool, Dictionary<string, object?> Base)[] table =
            [
                (new CameraCaptureTool(null), Args()),
                (new ScreenCaptureTool(null), Args()),
                (ask, Args()),
                (new WriteFileTool(files), Args(("path", "a.txt"), ("content", "x"))),
                (new UncWriteTool(unc, () => settings), Args(("path", @"\server\share\a.txt"), ("content", "x"))),
                (new OpenTool(files, _ => { }), Args(("path", "a.txt"))),
                (new ReadPdfTool(files, new NeonSidekick.Pdf.PdfReading(browser, () => browser.Executable), () => settings), Args(("path", "a.pdf"))),
                (new ReadOfficeTool(files, () => settings), Args(("path", "a.docx"))),
                (new SqliteQueryTool(new NeonSidekick.Sqlite.SqliteAccess(() => NeonSidekick.Sqlite.SqliteCatalog.Empty, () => files), () => settings), Args(("sql", "SELECT 1"))),
                (new SkillEditorTool(() => skillRoots, () => false), Args(("action", "create"), ("name", "notes"))),
            ];

            var covered = table.Select(row => row.Tool.GetType()).ToHashSet();
            // The database tools share ServerTool's extras (the retired argument, and the export's three on a query tool): one family stands for all.
            var every = typeof(IAcceptsArguments).Assembly.GetTypes()
                .Where(type => typeof(IAcceptsArguments).IsAssignableFrom(type) && type is { IsAbstract: false, IsInterface: false })
                .Where(type => type != typeof(OpenUrlTool) && !IsServerTool(type));
            Assert.All(every, type => Assert.Contains(type, covered));

            foreach (var (tool, given) in table)
            {
                foreach (string extra in ((IAcceptsArguments)tool).ExtraArguments)
                {
                    var arguments = new Dictionary<string, object?>(given, StringComparer.Ordinal) { [extra] = extra == "replace_all" || extra == "overwrite" ? true : "x" };
                    var result = await Assistant.InvokeToolWithDiffAsync([tool], new FunctionCallContent("c1", tool.Name, arguments), CancellationToken.None);
                    Assert.False(result.ArgumentError, tool.Name + " " + extra + ": " + result.Text);
                    Assert.DoesNotContain("It takes:", result.Text, StringComparison.Ordinal);
                }
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// A refused call never reaches the tool, is marked refused and an argument error (#19), and is never rewritten: a count synonym is hinted,
    /// not renamed (the user's call, 2026-11-15).
    /// </summary>
    [Fact]
    public async Task TheInvokePath_RefusesBeforeTheToolRuns_AndNeverRenames()
    {
        var echo = new EchoTool();
        var refused = await Assistant.InvokeToolWithDiffAsync([echo], new FunctionCallContent("c1", "echo", Args(("txet", "x"))), CancellationToken.None);
        Assert.True(refused.Refused);
        Assert.True(refused.ArgumentError);
        Assert.Equal("Error: echo takes no argument 'txet'; did you mean 'text'? It takes: text. Nothing ran.", refused.Text);
        Assert.Empty(echo.Received);

        var counted = new CountTool();
        var call = new FunctionCallContent("c2", "count", Args(("limit", 3)));
        var hinted = await Assistant.InvokeToolWithDiffAsync([counted], call, CancellationToken.None);
        Assert.True(hinted.Refused);
        Assert.Equal("Error: count takes no argument 'limit'; it is 'max_rows'. It takes: max_rows. Nothing ran.", hinted.Text);
        Assert.Equal(3, call.Arguments!["limit"]);
        Assert.False(call.Arguments.ContainsKey("max_rows"));
        Assert.Equal(0, counted.Runs);
    }

    /// <summary>A no-argument tool runs, and its result ends with the note as the last line; never after an Error: answer (#112).</summary>
    [Fact]
    public async Task TheInvokePath_NotesIgnoredKeys_AfterAGoodResultOnly()
    {
        var fixedTool = new NoArgumentTool("done");
        var result = await Assistant.InvokeToolWithDiffAsync([fixedTool], new FunctionCallContent("c1", "none", Args(("query", "x"))), CancellationToken.None);
        Assert.False(result.Refused);
        Assert.False(result.ArgumentError);
        Assert.Equal("done\n(ignored: query; none takes no arguments)", result.Text);

        // A fenced answer keeps its quote note last (2026-11-16, the web-tools review's #70): the note the user's views unfence by.
        var fencedTool = new NoArgumentTool("⟪page⟫\n" + UntrustedText.QuoteNote);
        var fenced = await Assistant.InvokeToolWithDiffAsync([fencedTool], new FunctionCallContent("c3", "none", Args(("query", "x"))), CancellationToken.None);
        Assert.Equal("⟪page⟫\n(ignored: query; none takes no arguments)\n" + UntrustedText.QuoteNote, fenced.Text);

        var failing = new NoArgumentTool("Error: nothing to do.");
        var failed = await Assistant.InvokeToolWithDiffAsync([failing], new FunctionCallContent("c2", "none", Args(("query", "x"))), CancellationToken.None);
        Assert.Equal("Error: nothing to do.", failed.Text);
    }

    /// <summary>A real server's tool with a closed schema is judged by it; the key it declares reaches it as sent.</summary>
    [Fact]
    public async Task AnMcpToolsUndeclaredKey_IsRefused_AndItsDeclaredOnesReachTheServer()
    {
        await using var server = McpPipeServer.Start([("echo", "Echoes.")], options: new McpPipeServerOptions { Closed = true });
        await using var client = await McpClient.CreateAsync(server.ClientTransport);
        var listed = await client.ListToolsAsync((RequestOptions?)null);
        AIFunction tool = new McpTool("pipe", client, listed[0], "pipe__echo");

        var refused = await Assistant.InvokeToolWithDiffAsync([tool], new FunctionCallContent("c1", "pipe__echo", Args(("txt", "ping"))), CancellationToken.None);
        Assert.True(refused.Refused);
        Assert.Empty(server.Calls);

        var (text, _) = await Assistant.InvokeToolAsync([tool], new FunctionCallContent("c2", "pipe__echo", Args(("text", "ping"))), CancellationToken.None);
        Assert.Equal("⟪echo: ping⟫" + "\n" + NeonSidekick.Llm.Tools.UntrustedText.QuoteNote, text);
    }

    /// <summary>An open server schema (no additionalProperties) passes an undeclared key through to the server (#2).</summary>
    [Fact]
    public async Task AnOpenMcpSchema_PassesAnUndeclaredKeyToTheServer()
    {
        await using var server = McpPipeServer.Start([("echo", "Echoes.")]);
        await using var client = await McpClient.CreateAsync(server.ClientTransport);
        var listed = await client.ListToolsAsync((RequestOptions?)null);
        AIFunction tool = new McpTool("pipe", client, listed[0], "pipe__echo");

        var result = await Assistant.InvokeToolWithDiffAsync([tool], new FunctionCallContent("c1", "pipe__echo", Args(("text", "ping"), ("txt", "x"))), CancellationToken.None);
        Assert.False(result.Refused);
        Assert.Single(server.Calls);
    }

    private static bool IsServerTool(Type type)
    {
        for (var at = type.BaseType; at is not null; at = at.BaseType)
        {
            if (at.IsGenericType && at.GetGenericTypeDefinition() == typeof(ServerTool<>))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class CountTool : AIFunction
    {
        private static readonly JsonElement Schema = ToolSchema.Parse("""{"type":"object","properties":{"max_rows":{"type":"integer"}}}""");

        public int Runs { get; private set; }

        public override string Name => "count";

        public override JsonElement JsonSchema => Schema;

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            Runs++;
            return new("max_rows=" + ToolArguments.ReadString(arguments, "max_rows"));
        }
    }

    private sealed class NoArgumentTool(string answer) : AIFunction
    {
        public override string Name => "none";

        public override JsonElement JsonSchema => NoArguments;

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) => new(answer);
    }
}
