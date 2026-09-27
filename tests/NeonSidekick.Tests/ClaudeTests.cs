using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Claude;
using NeonSidekick.Diagnostics;
using NeonSidekick.Llm;
using NeonSidekick.Sessions;
using NeonSidekick.Settings;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/claude</c>'s pure parts (2026-09-27): the command line, the stream parser over lines captured from Claude Code
/// 2.1.283 (<c>Fixtures/claude</c>, paths scrubbed), the CLI lookup, the settings words and the stored id.
/// </summary>
public class ClaudeTests
{
    private static IReadOnlyList<ClaudeEvent> ParseFixture(string name)
    {
        var parser = new ClaudeStreamParser();
        var events = new List<ClaudeEvent>();
        foreach (string line in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "claude", name)))
        {
            events.AddRange(parser.Read(line));
        }

        Assert.True(parser.SawResult);
        return events;
    }

    // ── the stream ──────────────────────────────────────────────────────────

    [Fact]
    public void Parser_ATextReply_IsItsDeltas_ThenAnOkResult()
    {
        var events = ParseFixture("text-reply.jsonl");

        Assert.Equal("hello there", string.Concat(events.OfType<ClaudeEvent.TextDelta>().Select(d => d.Text)));
        var result = Assert.IsType<ClaudeEvent.Result>(events[^1]);
        Assert.False(result.IsError);
        Assert.Equal("7eca3077-3c08-48ea-b771-18bca4c4b4eb", result.SessionId);
        Assert.Equal(0.0256008m, result.CostUsd);
        Assert.Equal(2 + 3108 + 3144, result.Usage.Input);   // the input with both cache figures: what the request carried
        Assert.Equal(5, result.Usage.Output);
        Assert.Equal("hello there", result.Text);
        Assert.Empty(result.Denied);
    }

    [Fact]
    public void Parser_AToolReply_SaysTheToolWithItsFile_ThenTheText()
    {
        var events = ParseFixture("tool-reply.jsonl");

        var tool = Assert.Single(events.OfType<ClaudeEvent.ToolActivity>());
        Assert.Equal("Read", tool.Name);
        Assert.Equal(@"C:\work\note.txt", tool.Detail);
        Assert.True(events.ToList().IndexOf(tool) < events.ToList().FindIndex(e => e is ClaudeEvent.TextDelta));
        Assert.Equal("The file says: **note**", string.Concat(events.OfType<ClaudeEvent.TextDelta>().Select(d => d.Text)));
        Assert.IsType<ClaudeEvent.Result>(events[^1]);
    }

    [Fact]
    public void Parser_AMissingResume_IsAnErrorResult_WithTheCliWords()
    {
        var result = Assert.IsType<ClaudeEvent.Result>(Assert.Single(ParseFixture("resume-missing.jsonl")));

        Assert.True(result.IsError);
        Assert.Equal("No conversation found with session ID: 11111111-1111-1111-1111-111111111111", result.Error);
        Assert.Equal(0m, result.CostUsd);
    }

    [Fact]
    public void Parser_ASecondTextBlock_StartsAParagraph_ASubagentsLinesAreSkipped()
    {
        var parser = new ClaudeStreamParser();
        string Start() => """{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}},"parent_tool_use_id":null}""";
        string Delta(string text, string parent = "null") => $$$"""{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"{{{text}}}"}},"parent_tool_use_id":{{{parent}}}}""";

        var events = new[] { Start(), Delta("Before."), Delta("inside", "\"toolu_1\""), Start(), Delta("After.") }.SelectMany(parser.Read).ToList();

        Assert.Equal(["Before.", "\n\nAfter."], events.OfType<ClaudeEvent.TextDelta>().Select(d => d.Text));
        Assert.False(parser.SawResult);
    }

    [Fact]
    public void Parser_NoiseIsSkipped_AndALineThatIsNotJson_IsLogged()
    {
        var parser = new ClaudeStreamParser();
        var logged = new List<DiagnosticEvent>();
        Action<DiagnosticEvent> handler = logged.Add;
        DiagnosticLog.Emitted += handler;
        try
        {
            Assert.Empty(parser.Read(""));
            Assert.Empty(parser.Read("""{"type":"rate_limit_event"}"""));
            Assert.Empty(parser.Read("""{"type":"a_kind_from_a_newer_cli","x":1}"""));
            Assert.Empty(parser.Read("[1,2]"));
            Assert.Empty(parser.Read("not json"));
        }
        finally
        {
            DiagnosticLog.Emitted -= handler;
        }

        Assert.Contains(logged, e => e.Category == ClaudeText.Category && e.Message.StartsWith("A stream line that is not JSON", StringComparison.Ordinal));
    }

    [Fact]
    public void Parser_TheDeniedTools_AndAFailureWithoutWords()
    {
        var parser = new ClaudeStreamParser();

        var denied = Assert.IsType<ClaudeEvent.Result>(Assert.Single(parser.Read("""{"type":"result","is_error":false,"session_id":"s","result":"no","permission_denials":[{"tool_name":"Bash","tool_use_id":"t"},{"x":1}]}""")));
        var failed = Assert.IsType<ClaudeEvent.Result>(Assert.Single(parser.Read("""{"type":"result","is_error":true,"subtype":"error_max_turns"}""")));
        var bare = Assert.IsType<ClaudeEvent.Result>(Assert.Single(parser.Read("""{"type":"result","is_error":true}""")));

        Assert.Equal(["Bash"], denied.Denied);
        Assert.Equal("error_max_turns", failed.Error);
        Assert.Equal(ClaudeText.UnknownFailure, bare.Error);
    }

    [Fact]
    public void Detail_TheFirstOfTheUsualFields()
    {
        using var input = System.Text.Json.JsonDocument.Parse("""{"pattern":"*.cs","command":"dir"}""");
        using var none = System.Text.Json.JsonDocument.Parse("""{"limit":3}""");

        Assert.Equal("*.cs", ClaudeStreamParser.Detail(input.RootElement));
        Assert.Equal("", ClaudeStreamParser.Detail(none.RootElement));
        Assert.Equal(["file_path", "path", "pattern", "command", "url", "query", "description", "prompt"], ClaudeStreamParser.DetailKeys);
    }

    // ── the command line ────────────────────────────────────────────────────

    [Fact]
    public void Arguments_ANewReadOnlyThread()
    {
        var request = new ClaudeRequest("hi", "id-1", Resume: false, @"C:\work", ClaudePermissionLevel.ReadOnly);

        Assert.Equal(
            ["-p", "--output-format", "stream-json", "--include-partial-messages", "--verbose", "--permission-prompts", "none", "--session-id", "id-1", "--tools", "Read,Grep,Glob,WebSearch,WebFetch"],
            ClaudeArguments.Build(request));
    }

    [Fact]
    public void Arguments_AResumedEditThread_WithModelAndEffort()
    {
        var request = new ClaudeRequest("hi", "id-1", Resume: true, @"C:\work", ClaudePermissionLevel.Edit, Model: " opus ", Effort: "high");

        var arguments = ClaudeArguments.Build(request);

        Assert.Equal(["--resume", "id-1"], arguments.Skip(7).Take(2));
        Assert.Equal(["--permission-mode", "acceptEdits", "--model", "opus", "--effort", "high"], arguments.Skip(9));
        Assert.DoesNotContain("hi", arguments);   // the prompt goes on stdin
    }

    [Fact]
    public void Arguments_Full_BypassesThePermissions()
    {
        var arguments = ClaudeArguments.Build(new ClaudeRequest("hi", "id", false, "C:\\", ClaudePermissionLevel.Full));

        Assert.Equal(["--permission-mode", "bypassPermissions"], arguments.Skip(9));
    }

    // ── the lookup ──────────────────────────────────────────────────────────

    [Fact]
    public void Locate_TheSetting_ThenThePath_ThenTheShim_ThenTheInstallersFolder()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"D:\tools\claude.exe", @"C:\npm\claude.cmd", @"C:\Users\u\.local\bin\claude.exe", @"C:\bin\claude.exe" };
        Func<string, string?> Env(string path) => name => name switch { "PATH" => path, "PATHEXT" => ".EXE;.CMD", "USERPROFILE" => @"C:\Users\u", _ => null };

        Assert.Equal(@"D:\tools\claude.exe", ClaudeExecutable.Locate(@"""D:\tools\claude.exe""", Env(@"C:\bin"), files.Contains));
        Assert.Null(ClaudeExecutable.Locate(@"D:\missing.exe", Env(@"C:\bin"), files.Contains));   // a set path is never a fall-through
        Assert.Equal(@"C:\bin\claude.exe", ClaudeExecutable.Locate("", Env(@"C:\npm;C:\bin"), files.Contains));   // the native exe first
        Assert.Equal(@"C:\npm\claude.cmd", ClaudeExecutable.Locate("", Env(@"C:\npm"), files.Contains));
        Assert.Equal(@"C:\Users\u\.local\bin\claude.exe", ClaudeExecutable.Locate(null, Env(@"C:\nothing"), files.Contains));
        Assert.Null(ClaudeExecutable.Locate(null, _ => null, files.Contains));
    }

    [Fact]
    public async Task Process_NoCli_ThrowsBeforeAnyEvent_WithTheInstallHint()
    {
        var cli = new ClaudeProcess(_ => null, _ => false);

        var ex = await Assert.ThrowsAsync<ClaudeStartException>(async () =>
        {
            await foreach (var _ in cli.RunAsync(new ClaudeRequest("hi", "id", false, Path.GetTempPath(), ClaudePermissionLevel.ReadOnly), CancellationToken.None))
            {
            }
        });
        Assert.Equal(ClaudeText.NotFound, ex.Message);
    }

    [Fact]
    public async Task Process_ASetPathThatIsNoFile_SaysSo()
    {
        var cli = new ClaudeProcess(_ => null, _ => false);

        var ex = await Assert.ThrowsAsync<ClaudeStartException>(async () =>
        {
            await foreach (var _ in cli.RunAsync(new ClaudeRequest("hi", "id", false, Path.GetTempPath(), ClaudePermissionLevel.ReadOnly, Executable: @"D:\nope.exe"), CancellationToken.None))
            {
            }
        });
        Assert.Equal(ClaudeText.ConfiguredNotFound(@"D:\nope.exe"), ex.Message);
    }

    [Fact]
    public async Task Process_AChildThatWritesNoResult_IsAnErrorResult_WithItsExitCode()
    {
        // cmd.exe stands in for the CLI: it ignores the flags it does not know, reads stdin and exits without a result line.
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var cli = new ClaudeProcess(_ => null);
        var events = new List<ClaudeEvent>();

        await foreach (var evt in cli.RunAsync(new ClaudeRequest("exit 3", "id", false, Path.GetTempPath(), ClaudePermissionLevel.ReadOnly, Executable: cmd), CancellationToken.None))
        {
            events.Add(evt);
        }

        var result = Assert.IsType<ClaudeEvent.Result>(Assert.Single(events));
        Assert.True(result.IsError);
        Assert.StartsWith("exit code ", result.Error, StringComparison.Ordinal);
    }

    // ── the settings words ──────────────────────────────────────────────────

    [Fact]
    public void Permission_TheWords_AndAnUnknownOneFallsBackToReadOnly()
    {
        Assert.Equal(["read-only", "edit", "full"], ClaudePermission.Names);
        Assert.Equal("read-only", ClaudePermission.Default);
        Assert.Equal("read-only", new AppSettingsData().ClaudePermissions);
        Assert.True(ClaudePermission.TryParse(" EDIT ", out var edit));
        Assert.Equal(ClaudePermissionLevel.Edit, edit);
        Assert.Equal(ClaudePermissionLevel.ReadOnly, ClaudePermission.Resolve(new AppSettingsData { ClaudePermissions = "yolo" }));
        Assert.Equal(ClaudePermissionLevel.Full, ClaudePermission.Resolve(new AppSettingsData { ClaudePermissions = "full" }));
        Assert.All(ClaudePermission.Names, name => Assert.Equal(name, ClaudePermission.Name(ClaudePermission.TryParse(name, out var level) ? level : throw new InvalidOperationException())));
        Assert.All(ClaudePermission.Names, name => Assert.NotEmpty(ClaudePermission.Describe(name)));
        Assert.Equal("", ClaudePermission.Describe("x"));
    }

    [Fact]
    public void Effort_BlankOrAWordOfTheCli()
    {
        Assert.Equal(["", "low", "medium", "high", "xhigh", "max"], ClaudeEffort.Names);
        Assert.Null(ClaudeEffort.Resolve(""));
        Assert.Null(ClaudeEffort.Resolve(null));
        Assert.Equal("high", ClaudeEffort.Resolve(" High "));
        Assert.Null(ClaudeEffort.Resolve("turbo"));
    }

    [Fact]
    public void ToolsMenu_TheClaudeTab_ItsTenRowsAndHowTheyRead()
    {
        var data = new AppSettingsData();
        int tab = ToolsText.TabTitles.ToList().IndexOf(ToolsText.ClaudeTabTitle);

        // On /tools since later on 2026-09-27 (the user's call), after ComfyUI, before Options; gone from /settings.
        Assert.Equal(ToolsText.TabTitles.Count - 2, tab);
        Assert.DoesNotContain("Claude", SettingsMenu.TabTitles);
        Assert.Equal(
            [SettingsField.ClaudeExecutable, SettingsField.ClaudePermissions, SettingsField.ClaudeModel, SettingsField.ClaudeEffort,
             SettingsField.ClaudeAdvisor, SettingsField.ClaudeAdvisorContext, SettingsField.ClaudeAdvisorCallsPerTurn, SettingsField.ClaudeAdvisorModel, SettingsField.ClaudeAdvisorEffort, SettingsField.ClaudeAdvisorConfirm],
            SettingsMenu.ToolsTabFields[tab - 1]);
        Assert.Equal(
            ["Claude command executable", "Claude command permissions", "Claude command model", "Claude command effort",
             "Claude advisor", "Claude advisor context", "Claude advisor calls per turn", "Claude advisor model", "Claude advisor effort", "Claude advisor confirm"],
            SettingsMenu.ToolsTabFields[tab - 1].Select(SettingsMenu.FieldName));
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.ClaudeAdvisor, data, "C:\\p"));
        Assert.Equal("brief", SettingsMenu.FieldValue(SettingsField.ClaudeAdvisorContext, data, "C:\\p"));
        Assert.Equal("2 calls", SettingsMenu.FieldValue(SettingsField.ClaudeAdvisorCallsPerTurn, data, "C:\\p"));
        Assert.Equal("1 call", SettingsMenu.ClaudeAdvisorCalls(1));
        Assert.Equal("(as Claude command model)", SettingsMenu.FieldValue(SettingsField.ClaudeAdvisorModel, data, "C:\\p"));
        Assert.Equal("(as Claude command effort)", SettingsMenu.FieldValue(SettingsField.ClaudeAdvisorEffort, data, "C:\\p"));
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.ClaudeAdvisorConfirm, data, "C:\\p"));
        Assert.True(SettingsMenu.IsToggle(SettingsField.ClaudeAdvisor));
        Assert.True(SettingsMenu.IsToggle(SettingsField.ClaudeAdvisorConfirm));
        Assert.Equal("(as Claude command effort)", SettingsMenu.ClaudeEffortLabel("", SettingsMenu.ClaudeAdvisorEffortLabel));
        Assert.StartsWith("recent  ", SettingsMenu.ClaudeAdvisorContextLabel("recent"), StringComparison.Ordinal);   // padded to eight
        Assert.Contains("the last 10 messages", SettingsMenu.ClaudeAdvisorContextLabel("recent"), StringComparison.Ordinal);
        var advisorCopy = AppSettings.Copy(new AppSettingsData { ClaudeAdvisor = true, ClaudeAdvisorContext = "recent", ClaudeAdvisorCallsPerTurn = 5, ClaudeAdvisorModel = "opus", ClaudeAdvisorEffort = "high", ClaudeAdvisorConfirm = true });
        Assert.Equal((true, "recent", 5, "opus", "high", true), (advisorCopy.ClaudeAdvisor, advisorCopy.ClaudeAdvisorContext, advisorCopy.ClaudeAdvisorCallsPerTurn, advisorCopy.ClaudeAdvisorModel, advisorCopy.ClaudeAdvisorEffort, advisorCopy.ClaudeAdvisorConfirm));
        Assert.Equal("(looked up)", SettingsMenu.FieldValue(SettingsField.ClaudeExecutable, data, "C:\\p"));
        Assert.Equal("read-only", SettingsMenu.FieldValue(SettingsField.ClaudePermissions, data, "C:\\p"));
        Assert.Equal("(Claude Code's default)", SettingsMenu.FieldValue(SettingsField.ClaudeModel, data, "C:\\p"));
        Assert.Equal("(Claude Code's default)", SettingsMenu.FieldValue(SettingsField.ClaudeEffort, data, "C:\\p"));
        Assert.Equal("opus", SettingsMenu.FieldValue(SettingsField.ClaudeModel, new AppSettingsData { ClaudeModel = "opus" }, "C:\\p"));
        Assert.StartsWith("edit       ", SettingsMenu.ClaudePermissionLabel("edit"), StringComparison.Ordinal);   // padded to eleven
        Assert.Contains("edit files without asking; commands denied", SettingsMenu.ClaudePermissionLabel("edit"), StringComparison.Ordinal);
        Assert.Equal("(Claude Code's default)", SettingsMenu.ClaudeEffortLabel(""));
        Assert.Equal("max", SettingsMenu.ClaudeEffortLabel("max"));
        Assert.False(SettingsMenu.RefusedMidTurn(SettingsField.ClaudePermissions));   // read at each /claude: nothing to reconnect
        var copy = AppSettings.Copy(new AppSettingsData { ClaudeExecutable = "x", ClaudePermissions = "full", ClaudeModel = "m", ClaudeEffort = "low" });
        Assert.Equal(("x", "full", "m", "low"), (copy.ClaudeExecutable, copy.ClaudePermissions, copy.ClaudeModel, copy.ClaudeEffort));
    }

    [Fact]
    public void EnvironmentOverrides_TheTwoClaudeVariables()
    {
        var env = new EnvironmentOverrides(name => name switch
        {
            EnvironmentOverrides.ClaudeExeVariable => @"D:\claude.exe",
            EnvironmentOverrides.ClaudePermissionsVariable => "EDIT",
            _ => null,
        });

        var effective = env.ApplyTo(new AppSettingsData());

        Assert.Equal(@"D:\claude.exe", effective.ClaudeExecutable);
        Assert.Equal("edit", effective.ClaudePermissions);
        Assert.Null(new EnvironmentOverrides(name => name == EnvironmentOverrides.ClaudePermissionsVariable ? "yolo" : null).ClaudePermissions);
        Assert.Contains(EnvironmentOverrides.ClaudeExeVariable, EnvironmentOverrides.AllVariables);
        Assert.Contains(EnvironmentOverrides.ClaudePermissionsVariable, EnvironmentOverrides.AllVariables);
        // The advisor's switch (2026-09-27): a switch word, as NEONSIDEKICK_SHELL_NATIVE's.
        var advisor = new EnvironmentOverrides(name => name == EnvironmentOverrides.ClaudeAdvisorVariable ? "on" : null);
        Assert.True(advisor.ApplyTo(new AppSettingsData()).ClaudeAdvisor);
        Assert.Contains(EnvironmentOverrides.ClaudeAdvisorVariable, advisor.ActiveVariables());
        Assert.Null(new EnvironmentOverrides(name => name == EnvironmentOverrides.ClaudeAdvisorVariable ? "maybe" : null).ClaudeAdvisor);
        Assert.Contains(EnvironmentOverrides.ClaudeAdvisorVariable, EnvironmentOverrides.AllVariables);
    }

    // ── the words ───────────────────────────────────────────────────────────

    [Fact]
    public void Text_TheLinesUsersRead()
    {
        Assert.Equal("$0.0256", ClaudeText.Dollars(0.0256008m));
        Assert.Equal("$1.50", ClaudeText.Dollars(1.5m));
        Assert.Equal("Claude › Read a.cs", ClaudeText.ToolNote("Read", "a.cs"));
        Assert.Equal("Claude › Glob", ClaudeText.ToolNote("Glob", " "));
        Assert.Equal("Claude · $0.0200 · 1,234 in · 5 out", ClaudeText.Footer(0.02m, new TokenUsage(1234, 5, 1239, 1, default, default)));
        Assert.Equal("Claude was denied Bash, Edit (Claude command permissions: read-only).", ClaudeText.DeniedNotice(["Bash", "Edit", "Bash"], "read-only"));
        Assert.Equal("exit code 1: bad", ClaudeText.NoResult(1, "bad\n"));
        Assert.Equal("exit code 2, nothing on stderr", ClaudeText.NoResult(2, ""));
        Assert.Equal("[to Claude] hi", ClaudeText.HistoryUser("hi"));
        Assert.Equal("[Claude] yo", ClaudeText.HistoryReply("yo"));
        Assert.True(ClaudeText.IsClaudeLine("/claude what"));
        Assert.False(ClaudeText.IsClaudeLine("/claude new"));
        Assert.False(ClaudeText.IsClaudeLine("/claudeish"));
    }

    // ── the command, the store, the tally ───────────────────────────────────

    [Fact]
    public void SlashCommands_ClaudeTakesItsMessage()
    {
        Assert.Equal((SlashCommand.Claude, "what is this?"), SlashCommands.Parse("/claude what is this?"));
        Assert.Equal((SlashCommand.Claude, ""), SlashCommands.Parse("/claude"));
        Assert.True(SlashCommands.TakesArgument(SlashCommand.Claude));
        Assert.Contains("/claude", SlashCommands.Words);
        Assert.Contains(SlashCommands.HelpEntries, e => e.Command == "/claude");
    }

    [Fact]
    public void SessionHistory_TheClaudeIdRoundTrips_AndAnOlderRowHasNone()
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "[to Claude] hi"), new(ChatRole.Assistant, "[Claude] yo") };

        string json = SessionHistory.ToJson(messages, claudeSessionId: "s-1");
        string older = SessionHistory.ToJson(messages);

        Assert.Equal(2, SessionHistory.FromJson(json, out _, out _, out string? id).Count);
        Assert.Equal("s-1", id);
        SessionHistory.FromJson(older, out _, out _, out string? none);
        Assert.Null(none);
        Assert.DoesNotContain("ClaudeSessionId", older, StringComparison.Ordinal);
    }

    [Fact]
    public void Tally_ClaudeIsItsOwnBucket_AndUsageShowsIt()
    {
        var tally = new TokenTally();

        tally.AddClaude(new TokenUsage(1000, 50, 1050, 1, default, default), 0.01m);
        tally.AddClaude(new TokenUsage(2000, 25, 2025, 1, default, default), 0.02m);

        Assert.Equal(2, tally.ClaudeRuns);
        Assert.Equal(3000, tally.Claude.Input);
        Assert.Equal(0.03m, tally.ClaudeCostUsd);
        Assert.True(tally.Conversation.IsEmpty);   // another model's tokens: never the local context's
        Assert.Equal("2 runs · 3,000 in · 75 out · $0.0300", UsageText.ClaudeValue(tally));
        Assert.Contains("claude: 2 runs · 3,000 in · 75 out · $0.0300", UsageText.Lines(tally, null));
    }
}
