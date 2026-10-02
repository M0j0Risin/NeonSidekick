using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Sessions;
using NeonSidekick.Settings;
using NeonSidekick.Skills;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

public class SkillLearnerTests : IDisposable
{
    private static readonly LlmTimeouts Timeouts = new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly SkillRoots _roots;
    private readonly FakeChatClient _client = new();
    private readonly Assistant _assistant;

    public SkillLearnerTests()
    {
        _roots = new SkillRoots(Path.Combine(_dir, "profile", "skills"), Path.Combine(_dir, "skills"), Path.Combine(_dir, ".agents", "skills"));
        _assistant = new Assistant(_client, new ConversationHistory("sys"), Timeouts);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private void Put(string root, string name, string description, string body)
    {
        string folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, SkillCatalog.FileName), SkillFrontmatter.Write(name, description, [], body));
    }

    private static TurnTrace Trace(int calls, int errors = 0, bool recovered = false, bool wroteSkill = false)
    {
        var trace = new TurnTrace();
        int id = 0;
        for (int i = 0; i < errors; i++)
        {
            trace.Observe(new TurnEvent.ToolCall("read_file", "c" + id, "{}"));
            trace.Observe(new TurnEvent.ToolResult("read_file", "c" + id++, "Error: no"));
        }

        int successes = calls - errors;
        for (int i = 0; i < successes; i++)
        {
            bool last = i == successes - 1;
            string name = last && wroteSkill ? SkillEditorTool.ToolName : "read_file";
            trace.Observe(new TurnEvent.ToolCall(name, "c" + id, "{}"));
            trace.Observe(new TurnEvent.ToolResult(name, "c" + id++, recovered || errors == 0 ? "1: ok" : "Error: still no"));
        }

        return trace;
    }

    private static List<ChatMessage> Turn(params ChatMessage[] messages) => messages.ToList();

    private static ChatMessage ToolCall(string id, string name) =>
        new(ChatRole.Assistant, [new FunctionCallContent(id, name, new Dictionary<string, object?> { ["path"] = "a.txt" })]);

    private static ChatMessage ToolResult(string id, string text) =>
        new(ChatRole.Tool, [new FunctionResultContent(id, text)]);

    private static Dictionary<string, object?> CreateArgs(string name, string description = "When the user wants X deployed.", string instructions = "1. Do X.\n2. Check Y.") => new()
    {
        [SkillEditorTool.ActionArgument] = SkillEditorTool.CreateAction,
        [SkillEditorTool.ScopeArgument] = SkillScopes.ProfileName,
        [SkillEditorTool.NameArgument] = name,
        [SkillEditorTool.DescriptionArgument] = description,
        [SkillEditorTool.InstructionsArgument] = instructions,
    };

    private Task<SkillLearnResult> Run(IReadOnlyList<ChatMessage>? turn = null, string? focus = null, bool external = false, ReasoningEffort effort = ReasoningEffort.None, CancellationToken ct = default, int maxRequests = SkillLearner.DefaultMaxRequests) =>
        SkillLearner.RunAsync(_assistant, turn ?? Turn(new ChatMessage(ChatRole.User, "deploy x"), ToolCall("c1", "read_file"), ToolResult("c1", "1: text"), new ChatMessage(ChatRole.Assistant, "Done.")), _roots, external, focus, effort, ct, maxRequests);

    // ── The trigger ─────────────────────────────────────────────────────────

    [Fact]
    public void Constants_ArePinned()
    {
        Assert.Equal(4, SkillLearner.DefaultMinToolCalls);
        Assert.Equal(ReflectionMinToolCalls.Default, SkillLearner.DefaultMinToolCalls);
        Assert.Equal(4, SkillLearner.DefaultMaxRequests);
        Assert.Equal(ReflectionMaxRequests.Default, SkillLearner.DefaultMaxRequests);
        Assert.Equal(3_000, SkillLearner.MaxResultChars);
        Assert.Equal(300, SkillLearner.MaxLeadUpResultChars);
        Assert.Equal("nothing", SkillLearner.NothingWord);
    }

    [Theory]
    [InlineData(4, 0, false, false, 5, false)]   // a simple exchange
    [InlineData(5, 0, false, false, 5, true)]    // enough orchestration
    [InlineData(12, 0, false, false, 5, true)]
    [InlineData(2, 1, true, false, 5, true)]     // an error the turn got past
    [InlineData(2, 1, false, false, 5, false)]   // an error it ended on
    [InlineData(1, 1, false, false, 5, false)]
    [InlineData(6, 0, false, true, 5, false)]    // the turn kept its own lesson
    [InlineData(3, 1, true, true, 5, false)]
    [InlineData(3, 0, false, false, 3, true)]    // the threshold lowered (Reflection min tool calls)
    [InlineData(19, 0, false, false, 20, false)] // and raised
    [InlineData(2, 1, true, false, 20, true)]    // the error door is not the setting's
    public void ShouldLearn_IsTheTriggerRule(int calls, int errors, bool recovered, bool wroteSkill, int minCalls, bool expected)
    {
        Assert.Equal(expected, SkillLearner.ShouldLearn(Trace(calls, errors, recovered, wroteSkill), minCalls));
    }

    [Fact]
    public void ShouldLearn_RefusesAThresholdBelowOne()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SkillLearner.ShouldLearn(Trace(1), 0));
    }

    // ── The request ─────────────────────────────────────────────────────────

    [Fact]
    public void Request_IsPinned_WithAndWithoutAFocus()
    {
        Assert.Equal("Reflect on the turns above. Update or create a skill if they taught a reusable procedure; otherwise answer nothing.", SkillLearner.Request(null));
        Assert.Equal(SkillLearner.Request(null), SkillLearner.Request("  "));
        Assert.Equal("Reflect on the turns above. Update or create a skill if they taught a reusable procedure; otherwise answer nothing. The user asked to keep: the docker steps", SkillLearner.Request(" the docker steps "));
        Assert.StartsWith("You are reviewing the last turns of a conversation between a user and Neon", SkillLearner.Instruction, StringComparison.Ordinal);
        Assert.Contains("The last turn is the one to learn from; the turns before it are its lead-up with their tool results shortened — a mistake made there and put right later, or a correction the user gave, is a pitfall worth keeping. ", SkillLearner.Instruction, StringComparison.Ordinal);
        Assert.Contains("load_skill", SkillLearner.Instruction, StringComparison.Ordinal);
        Assert.Contains("skill_editor with action update", SkillLearner.Instruction, StringComparison.Ordinal);
        Assert.Contains("action create, scope profile", SkillLearner.Instruction, StringComparison.Ordinal);
        Assert.Contains("Call skill_editor at most once, with a summary: one or two sentences saying what you changed and why, for the user to read. ", SkillLearner.Instruction, StringComparison.Ordinal);
        Assert.EndsWith("answer with the single word nothing and no tool call.", SkillLearner.Instruction, StringComparison.Ordinal);
    }

    [Fact]
    public void Transcript_DropsPictures_CutsLongResults_AndCopiesTheRest()
    {
        var picture = new ChatMessage(ChatRole.User, [new TextContent("(attached by view_image, not typed by the user: a.png)"), new DataContent(new byte[] { 1, 2, 3 }, "image/png")])
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { [ConversationHistory.CarrierKey] = true },
        };
        var withPicture = new ChatMessage(ChatRole.User, [new TextContent("look"), new DataContent(new byte[] { 4 }, "image/png")]);
        string longText = new string('x', SkillLearner.MaxResultChars + 10);
        var turn = Turn(withPicture, ToolCall("c1", "view_image"), ToolResult("c1", longText), picture, new ChatMessage(ChatRole.Assistant, "A cat."));

        var copy = SkillLearner.Transcript(turn);

        Assert.Equal(4, copy.Count);
        Assert.Equal("look", Assert.IsType<TextContent>(Assert.Single(copy[0].Contents)).Text);
        Assert.NotSame(turn[0], copy[0]);
        Assert.Same(turn[1].Contents[0], copy[1].Contents[0]);   // the call content itself is shared; the message is new
        var result = Assert.IsType<FunctionResultContent>(Assert.Single(copy[2].Contents));
        Assert.Equal("c1", result.CallId);
        Assert.Equal(new string('x', SkillLearner.MaxResultChars) + "\n" + SkillLearner.CutMark, result.Result);
        Assert.Equal("A cat.", copy[3].Text);
        Assert.Equal(longText, ((FunctionResultContent)turn[2].Contents[0]).Result);   // the history's own untouched
    }

    [Fact]
    public void Transcript_SlimsTheLeadUp_AndKeepsTheLastTurnAsBefore()
    {
        string longText = new string('x', SkillLearner.MaxResultChars + 10);
        string mid = new string('y', SkillLearner.MaxLeadUpResultChars + 10);
        var window = Turn(
            new ChatMessage(ChatRole.User, "first ask"),
            ToolCall("a1", "read_file"), ToolResult("a1", mid),
            new ChatMessage(ChatRole.Assistant, "first reply"),
            new ChatMessage(ChatRole.User, "second ask"),
            ToolCall("b1", "read_file"), ToolResult("b1", "short"),
            ToolCall("b2", "read_file"), ToolResult("b2", mid),
            new ChatMessage(ChatRole.Assistant, "second reply"));

        var copy = SkillLearner.Transcript(window);

        Assert.Equal(10, copy.Count);
        Assert.Equal("first ask", copy[0].Text);
        // The lead-up: its result cut at the short cap; the call and the texts whole.
        Assert.Same(window[1].Contents[0], copy[1].Contents[0]);
        Assert.Equal(new string('y', SkillLearner.MaxLeadUpResultChars) + "\n" + SkillLearner.CutMark, ((FunctionResultContent)copy[2].Contents[0]).Result);
        Assert.Equal("first reply", copy[3].Text);
        // The last turn: the long cap, so a mid-sized result stays whole.
        Assert.Equal("short", ((FunctionResultContent)copy[6].Contents[0]).Result);
        Assert.Equal(mid, ((FunctionResultContent)copy[8].Contents[0]).Result);
        Assert.Equal("second reply", copy[9].Text);

        // A lone turn is the last turn: the long cap throughout, as before the window.
        var alone = SkillLearner.Transcript(Turn(new ChatMessage(ChatRole.User, "ask"), ToolCall("c1", "read_file"), ToolResult("c1", longText)));
        Assert.Equal(new string('x', SkillLearner.MaxResultChars) + "\n" + SkillLearner.CutMark, ((FunctionResultContent)alone[2].Contents[0]).Result);
    }

    [Fact]
    public void Build_IsTheInstructionAndTheCatalog_TheTurn_TheAsk()
    {
        var skills = new List<Skill> { new("docker-deploy", "Deploys a & b.", SkillScope.Profile, "d") };
        var turn = Turn(new ChatMessage(ChatRole.User, "hi"), new ChatMessage(ChatRole.Assistant, "hello"));

        var request = SkillLearner.Build(turn, skills, "keep it");

        Assert.Equal(4, request.Count);
        Assert.Equal(ChatRole.System, request[0].Role);
        Assert.Equal(SkillLearner.Instruction + "\n\n" + SkillsPrompt.Catalog(skills), request[0].Text);
        Assert.Contains("<name>docker-deploy</name>", request[0].Text, StringComparison.Ordinal);
        Assert.Equal("hi", request[1].Text);
        Assert.Equal("hello", request[2].Text);
        Assert.Equal(ChatRole.User, request[3].Role);
        Assert.Equal(SkillLearner.Request("keep it"), request[3].Text);

        var bare = SkillLearner.Build(turn, [], null);
        Assert.Equal(SkillLearner.Instruction + "\n\n" + SkillLearner.NoSkillsLine, bare[0].Text);
    }

    // ── The loop ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ACreateCall_WritesTheSkillUnderTheProfile_AndEndsTheLoop()
    {
        _client.Enqueue(FakeChatClient.Call("r1", SkillEditorTool.ToolName, CreateArgs("deploy-x")), FakeChatClient.Usage(300, 40));

        var result = await Run(focus: "the docker steps", effort: ReasoningEffort.High);

        Assert.Equal(SkillLearnOutcome.Learned, result.Outcome);
        Assert.Equal(SkillEditOutcome.Created, result.Edit!.Outcome);
        Assert.Equal("deploy-x", result.Edit.Name);
        Assert.Equal(SkillScope.Profile, result.Edit.Scope);
        Assert.StartsWith("created skill 'deploy-x' (profile, ", result.Detail, StringComparison.Ordinal);
        Assert.Equal(1, result.Requests);
        Assert.Equal(340, result.Usage.Total);
        Assert.True(File.Exists(Path.Combine(_roots.Profile, "deploy-x", SkillCatalog.FileName)));
        Assert.Contains("1. Do X.", File.ReadAllText(Path.Combine(_roots.Profile, "deploy-x", SkillCatalog.FileName)), StringComparison.Ordinal);

        // One request: the reflection's own system message, the turn, the ask; skill_editor alone offered (no skill to load); the effort passed.
        var request = Assert.Single(_client.Requests);
        Assert.Equal(ChatRole.System, request[0].Role);
        Assert.Equal(SkillLearner.Instruction + "\n\n" + SkillLearner.NoSkillsLine, request[0].Text);
        Assert.Equal("deploy x", request[1].Text);
        Assert.Equal(SkillLearner.Request("the docker steps"), request[^1].Text);
        var options = Assert.Single(_client.Options)!;
        Assert.Equal([SkillEditorTool.ToolName], options.Tools!.Select(t => t.Name));
        Assert.Equal(ReasoningEffort.High, options.Reasoning!.Effort);
    }

    [Fact]
    public async Task ALoadThenAnUpdate_ChangesTheSkillWhereItLives_TwoRequests()
    {
        Put(_roots.Global, "deploy-x", "Deploys X.", "1. Old step.");
        _client.Enqueue(FakeChatClient.Call("r1", LoadSkillTool.ToolName, new Dictionary<string, object?> { [LoadSkillTool.NameArgument] = "deploy-x" }), FakeChatClient.Usage(300, 20));
        _client.Enqueue(FakeChatClient.Call("r2", SkillEditorTool.ToolName, new Dictionary<string, object?>
        {
            [SkillEditorTool.ActionArgument] = SkillEditorTool.UpdateAction,
            [SkillEditorTool.ScopeArgument] = SkillScopes.ProfileName,   // the wrong scope: the editor redirects
            [SkillEditorTool.NameArgument] = "deploy-x",
            [SkillEditorTool.InstructionsArgument] = "1. Old step.\n2. New pitfall.",
        }), FakeChatClient.Usage(400, 30));

        var result = await Run();

        Assert.Equal(SkillLearnOutcome.Learned, result.Outcome);
        Assert.Equal(SkillEditOutcome.Updated, result.Edit!.Outcome);
        Assert.Equal(SkillScope.Global, result.Edit.Scope);
        Assert.Equal(2, result.Requests);
        Assert.Equal(750, result.Usage.Total);
        Assert.Contains("2. New pitfall.", File.ReadAllText(Path.Combine(_roots.Global, "deploy-x", SkillCatalog.FileName)), StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_roots.Profile, "deploy-x")));

        // Both tools offered with a skill installed; the second request carries the first's call and the loaded body.
        Assert.Equal([LoadSkillTool.ToolName, SkillEditorTool.ToolName], _client.Options[0]!.Tools!.Select(t => t.Name));
        Assert.Contains("<name>deploy-x</name>", _client.Requests[0][0].Text, StringComparison.Ordinal);
        var second = _client.Requests[1];
        Assert.Contains(second, m => m.Contents.OfType<FunctionCallContent>().Any(c => c.CallId == "r1"));
        var loaded = second.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single(r => r.CallId == "r1");
        Assert.Contains("1. Old step.", (string)loaded.Result!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATextReply_IsNothing_WithItsFirstLine()
    {
        _client.EnqueueText("nothing", "\nThe turn was a plain question.");

        var result = await Run();

        Assert.Equal(SkillLearnOutcome.Nothing, result.Outcome);
        Assert.Null(result.Edit);
        Assert.Equal("nothing", result.Detail);
        Assert.Equal(1, result.Requests);
        Assert.True(result.Usage.IsEmpty);
        Assert.False(Directory.Exists(_roots.Profile));
    }

    [Fact]
    public async Task ACreateOfATakenName_IsRefused_AndTheNextRequestMayUpdate()
    {
        Put(_roots.Profile, "deploy-x", "Deploys X.", "1. Old.");
        _client.Enqueue(FakeChatClient.Call("r1", SkillEditorTool.ToolName, CreateArgs("deploy-x")));
        _client.Enqueue(FakeChatClient.Call("r2", SkillEditorTool.ToolName, new Dictionary<string, object?>
        {
            [SkillEditorTool.ActionArgument] = SkillEditorTool.UpdateAction,
            [SkillEditorTool.ScopeArgument] = SkillScopes.ProfileName,
            [SkillEditorTool.NameArgument] = "deploy-x",
            [SkillEditorTool.DescriptionArgument] = "Deploys X, with the pitfalls.",
        }));

        var result = await Run();

        Assert.Equal(SkillLearnOutcome.Learned, result.Outcome);
        Assert.Equal(SkillEditOutcome.Updated, result.Edit!.Outcome);
        Assert.Equal(2, result.Requests);
        var refusal = _client.Requests[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single(r => r.CallId == "r1");
        Assert.Equal(SkillText.Exists("deploy-x", SkillScope.Profile), refusal.Result);
        Assert.Contains("Deploys X, with the pitfalls.", File.ReadAllText(Path.Combine(_roots.Profile, "deploy-x", SkillCatalog.FileName)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StillCallingToolsAtTheCap_IsExhausted_NothingWritten()
    {
        Put(_roots.Profile, "deploy-x", "Deploys X.", "1. Old.");
        for (int i = 0; i < SkillLearner.DefaultMaxRequests + 1; i++)
        {
            _client.Enqueue(FakeChatClient.Call("r" + i, LoadSkillTool.ToolName, new Dictionary<string, object?> { [LoadSkillTool.NameArgument] = "deploy-x" }), FakeChatClient.Usage(100, 10));
        }

        var result = await Run();

        Assert.Equal(SkillLearnOutcome.Exhausted, result.Outcome);
        Assert.Null(result.Edit);
        Assert.Equal(SkillLearner.DefaultMaxRequests, result.Requests);
        Assert.Equal(SkillLearner.DefaultMaxRequests * 110, result.Usage.Total);
        Assert.Equal(SkillLearner.DefaultMaxRequests, _client.Requests.Count);
    }

    [Fact]
    public async Task TheCapIsTheArgument_TheSettingsValue_NeverBelowOne()
    {
        // Reflection max requests (2026-09-17): the host passes the setting; two requests, then exhausted.
        Put(_roots.Profile, "deploy-x", "Deploys X.", "1. Old.");
        for (int i = 0; i < 3; i++)
        {
            _client.Enqueue(FakeChatClient.Call("r" + i, LoadSkillTool.ToolName, new Dictionary<string, object?> { [LoadSkillTool.NameArgument] = "deploy-x" }), FakeChatClient.Usage(100, 10));
        }

        var result = await Run(maxRequests: 2);

        Assert.Equal(SkillLearnOutcome.Exhausted, result.Outcome);
        Assert.Equal(2, result.Requests);
        Assert.Equal(2, _client.Requests.Count);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Run(maxRequests: 0));
    }

    [Fact]
    public async Task AnUnknownTool_GetsTheErrorSentence_AndTheLoopGoesOn()
    {
        _client.Enqueue(FakeChatClient.Call("r1", "read_file", new Dictionary<string, object?> { ["path"] = "a.txt" }));
        _client.EnqueueText("nothing");

        var result = await Run();

        Assert.Equal(SkillLearnOutcome.Nothing, result.Outcome);
        var answer = _client.Requests[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single(r => r.CallId == "r1");
        Assert.Equal("Error: unknown tool 'read_file'.", answer.Result);
    }

    [Fact]
    public async Task ATransportFailure_IsFailed_WithTheExplanation()
    {
        _client.Enqueue(FakeChatClient.Text("x"));
        _client.ThrowAt = 0;

        var result = await Run();

        Assert.Equal(SkillLearnOutcome.Failed, result.Outcome);
        Assert.Equal("HttpRequestException: scripted failure", result.Detail);
        Assert.Equal(0, result.Requests);
    }

    [Fact]
    public async Task Cancellation_IsCancelled_NeverThrows()
    {
        using var cts = new CancellationTokenSource();
        _client.Enqueue(FakeChatClient.Text("x"), FakeChatClient.Call("r1", SkillEditorTool.ToolName, CreateArgs("deploy-x")));
        _client.BeforeUpdate = (i, _) => { if (i == 1) cts.Cancel(); return Task.CompletedTask; };

        var result = await Run(ct: cts.Token);

        Assert.Equal(SkillLearnOutcome.Cancelled, result.Outcome);
        Assert.False(Directory.Exists(_roots.Profile));
    }

    [Fact]
    public async Task TheExternalFolder_BlocksANameOnlyWhileRead()
    {
        Put(_roots.External, "deploy-x", "Deploys X.", "1. Old.");
        _client.Enqueue(FakeChatClient.Call("r1", SkillEditorTool.ToolName, CreateArgs("deploy-x")));
        _client.EnqueueText("nothing");

        var result = await Run(external: true);

        Assert.Equal(SkillLearnOutcome.Nothing, result.Outcome);
        var refusal = _client.Requests[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single(r => r.CallId == "r1");
        Assert.Equal(SkillText.ExternalReadOnly("deploy-x"), refusal.Result);
        Assert.Contains("<name>deploy-x</name>", _client.Requests[0][0].Text, StringComparison.Ordinal);   // in the catalog while read
    }

    // ── The earlier sessions as evidence (2026-09-19) ───────────────────────

    private readonly ManualTimeProvider _time = new();

    private SessionStore Store()
    {
        var store = new SessionStore(Path.Combine(_dir, "profile"), _time);
        long a = store.Begin("earlier deploy", "m")!.Value;
        store.AppendTurn(a, "deploy the docker stack to staging", "Deployed; the compose file needed the network first.", 3, ["read_file", "edit_file"], ["docker-deploy"], 1, 10, 5, false);
        long current = store.Begin("this one", "m")!.Value;
        store.AppendTurn(current, "deploy the docker stack again", "Done.", 2, ["edit_file"], [], 0, 10, 5, false);
        return store;
    }

    private SessionEvidence Evidence(SessionStore store, long? current = 2) => new(store, () => new AppSettingsData(), current, _time);

    [Fact]
    public void Constants_OfTheEvidence_ArePinned()
    {
        Assert.Equal(6_000, SkillLearner.MaxSessionChars);
        Assert.Equal("neonsessn", SkillLearner.SessionsCallId);
        Assert.Equal(9, SkillLearner.SessionsCallId.Length);
        Assert.Equal(SkillLearner.TurnOpening + SkillLearner.InstructionBody, SkillLearner.Instruction);
        Assert.Equal(SkillLearner.SessionsOpening + SkillLearner.InstructionBody, SkillLearner.SessionsPassInstruction);
        Assert.StartsWith("You are reviewing several earlier conversations between a user and Neon", SkillLearner.SessionsOpening, StringComparison.Ordinal);
        Assert.StartsWith("The earlier sessions found for this turn close the transcript, as a session_manager search:", SkillLearner.SessionsInstruction, StringComparison.Ordinal);
        Assert.DoesNotContain("usage line", SkillLearner.SessionsInstruction, StringComparison.Ordinal);   // the usage sentence is its own since 2026-10-02
        Assert.StartsWith("A skill's usage line says what is known of it: a skill loaded often and still followed by errors needs its steps fixed", SkillLearner.UsageInstruction, StringComparison.Ordinal);
        Assert.Contains("A skill edited by hand carries the user's own wording", SkillLearner.UsageInstruction, StringComparison.Ordinal);
        Assert.Equal("Reflect on the sessions above. Find the procedure that recurs or the pitfall met more than once; update or create ONE skill for it, or answer nothing.", SkillLearner.SessionsRequest(null));
        Assert.EndsWith(" The sessions were found by searching for: docker", SkillLearner.SessionsRequest(" docker "), StringComparison.Ordinal);
    }

    [Fact]
    public void Evidence_IsTheOrSearchOverTheUserLine_TheSessionOnScreenLeftOut()
    {
        using var store = Store();
        var (query, result) = SkillLearner.Evidence(Evidence(store), "Deploy the Docker stack again, please!");

        Assert.Equal("deploy docker stack again please", query);
        Assert.NotNull(result);
        Assert.StartsWith(SessionText.SearchHeader(query!, 1), result, StringComparison.Ordinal);
        Assert.Contains("#1 · ", result, StringComparison.Ordinal);
        Assert.DoesNotContain("#2 · ", result, StringComparison.Ordinal);   // the one on screen
        Assert.Equal(SessionText.NoHits("pasta"), SkillLearner.Evidence(Evidence(store), "pasta").Result);
        Assert.Equal((null, null), SkillLearner.Evidence(Evidence(store), "hi, ok?"));   // no word of four letters
    }

    [Fact]
    public void Build_WithASeededSearch_AddsTheSessionsInstruction_ThePair_AndTheUsageLines()
    {
        var skills = new List<Skill> { new("docker-deploy", "Deploys the stack.", SkillScope.Profile, "d"), new("unused", "Nothing loads it.", SkillScope.Global, "u") };
        // The lines come from the skill records since 2026-10-02 (SkillRecordsTests reads them); here, one as they give it.
        var usage = new Dictionary<string, string> { ["docker-deploy"] = "loaded 1 time across 1 session, 1 followed by errors" };
        var turn = Turn(new ChatMessage(ChatRole.User, "deploy the docker stack again"), new ChatMessage(ChatRole.Assistant, "Done."));
        var material = new ReflectionMaterial.Turn(turn, null, "deploy docker stack again", SessionText.NoHits("deploy docker stack again"));

        var request = SkillLearner.Build(material, skills, usage, _time.LocalTimeZone);

        Assert.Equal(6, request.Count);
        Assert.Equal(SkillLearner.Instruction + "\n\n" + SkillLearner.SessionsInstruction + "\n\n" + SkillLearner.UsageInstruction + "\n\n" + SkillsPrompt.Catalog(skills, usage), request[0].Text);
        Assert.Contains("<usage>loaded 1 time across 1 session, 1 followed by errors</usage>", request[0].Text, StringComparison.Ordinal);
        Assert.Equal("deploy the docker stack again", request[1].Text);
        Assert.Equal("Done.", request[2].Text);
        var call = Assert.IsType<FunctionCallContent>(Assert.Single(request[3].Contents));
        Assert.Equal((ChatRole.Assistant, SkillLearner.SessionsCallId, SessionManagerTool.ToolName), (request[3].Role, call.CallId, call.Name));
        Assert.Equal("search", ((JsonElement)call.Arguments![SessionManagerTool.ActionArgument]!).GetString());
        Assert.Equal("deploy docker stack again", ((JsonElement)call.Arguments![SessionManagerTool.QueryArgument]!).GetString());
        var result = Assert.IsType<FunctionResultContent>(Assert.Single(request[4].Contents));
        Assert.Equal((ChatRole.Tool, SkillLearner.SessionsCallId, SessionText.NoHits("deploy docker stack again")), (request[4].Role, result.CallId, (string?)result.Result));
        Assert.Equal(SkillLearner.Request(null), request[5].Text);
        // The seeded call round-trips through the session store's JSON shape (the AOT wire shape).
        Assert.Equal("{\"action\":\"search\",\"query\":\"deploy docker stack again\"}", Assistant.SerializeArguments(call.Arguments));

        // Without a seeded search the bare shape stands; the usage sentence rides with the lines whatever the sessions say.
        var bare = SkillLearner.Build(new ReflectionMaterial.Turn(turn, null), skills, usage, _time.LocalTimeZone);
        Assert.Equal(4, bare.Count);
        Assert.Equal(SkillLearner.Instruction + "\n\n" + SkillLearner.UsageInstruction + "\n\n" + SkillsPrompt.Catalog(skills, usage), bare[0].Text);
        // No line at all: no usage sentence either.
        var plain = SkillLearner.Build(new ReflectionMaterial.Turn(turn, null), skills, new Dictionary<string, string>(), _time.LocalTimeZone);
        Assert.Equal(SkillLearner.Instruction + "\n\n" + SkillsPrompt.Catalog(skills), plain[0].Text);
    }

    [Fact]
    public void Build_APass_IsThePassInstruction_OneMessagePerSession_TheAsk()
    {
        using var store = Store();
        var records = new List<SessionRecord> { store.Load(1)!, store.Load(2)! };
        var request = SkillLearner.Build(new ReflectionMaterial.Sessions(records, "docker"), [], null, _time.LocalTimeZone);

        Assert.Equal(4, request.Count);
        Assert.Equal(SkillLearner.SessionsPassInstruction + "\n\n" + SkillLearner.NoSkillsLine, request[0].Text);
        Assert.Equal((ChatRole.User, SessionText.Read(records[0], 1, 0, _time.LocalTimeZone, SkillLearner.MaxSessionChars)), (request[1].Role, request[1].Text));
        Assert.StartsWith("Session #2 \"this one\"", request[2].Text, StringComparison.Ordinal);
        Assert.Equal(SkillLearner.SessionsRequest("docker"), request[3].Text);
    }

    [Fact]
    public async Task WithEvidence_SessionManagerIsTheThirdTool_AndItsReadAnswersFromTheStore()
    {
        using var store = Store();
        Put(_roots.Profile, "docker-deploy", "Deploys the stack.", "1. Old.");
        var turn = Turn(new ChatMessage(ChatRole.User, "deploy the docker stack again"), new ChatMessage(ChatRole.Assistant, "Done."));
        var (query, result) = SkillLearner.Evidence(Evidence(store), "deploy the docker stack again");
        _client.Enqueue(FakeChatClient.Call("r1", SessionManagerTool.ToolName, new Dictionary<string, object?> { [SessionManagerTool.ActionArgument] = SessionManagerTool.ReadAction, [SessionManagerTool.IdArgument] = 1 }));
        _client.EnqueueText("nothing");
        // The usage lines come from the skill records (2026-10-02): the store's history imported once, as the screen does at a profile's load.
        using var skillStore = new SkillRecordStore(_dir);
        var records = new SkillRecords(skillStore, () => _roots, _time);
        var catalog = new SkillCatalog(() => _roots);
        catalog.Scan(external: false);
        records.Reconcile(catalog.Skills);
        Assert.Equal(1, records.ImportFrom(store, catalog.Skills));
        Assert.Equal(0, records.ImportFrom(store, catalog.Skills));   // once per profile

        var outcome = await SkillLearner.RunAsync(_assistant, new ReflectionMaterial.Turn(turn, null, query, result), _roots, false, ReasoningEffort.None, CancellationToken.None, 4, Evidence(store), records: records);

        Assert.Equal(SkillLearnOutcome.Nothing, outcome.Outcome);
        Assert.Equal([LoadSkillTool.ToolName, SkillEditorTool.ToolName, SessionManagerTool.ToolName], _client.Options[0]!.Tools!.Select(t => t.Name));
        var read = _client.Requests[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single(r => r.CallId == "r1");
        Assert.StartsWith("Session #1 \"earlier deploy\"", (string)read.Result!, StringComparison.Ordinal);
        Assert.Contains("the compose file needed the network first", (string)read.Result!, StringComparison.Ordinal);
        Assert.Contains("<usage>loaded 1 time across 1 session, 1 followed by errors", _client.Requests[0][0].Text, StringComparison.Ordinal);
        Assert.Contains(SkillLearner.UsageInstruction, _client.Requests[0][0].Text, StringComparison.Ordinal);

        // Without evidence: the two tools, the bare instruction.
        _client.EnqueueText("nothing");
        await Run(turn);
        Assert.Equal([LoadSkillTool.ToolName, SkillEditorTool.ToolName], _client.Options[2]!.Tools!.Select(t => t.Name));
        Assert.DoesNotContain("<usage>", _client.Requests[2][0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APass_EndsAtTheFirstWrite_LikeATurn()
    {
        using var store = Store();
        var records = new List<SessionRecord> { store.Load(1)!, store.Load(2)! };
        _client.Enqueue(FakeChatClient.Call("r1", SkillEditorTool.ToolName, CreateArgs("docker-deploy")), FakeChatClient.Usage(300, 40));
        _client.Enqueue(FakeChatClient.Call("r2", SkillEditorTool.ToolName, CreateArgs("never-written")));

        var outcome = await SkillLearner.RunAsync(_assistant, new ReflectionMaterial.Sessions(records, null), _roots, false, ReasoningEffort.None, CancellationToken.None, 4, Evidence(store));

        Assert.Equal(SkillLearnOutcome.Learned, outcome.Outcome);
        Assert.Equal(1, outcome.Requests);
        Assert.True(File.Exists(Path.Combine(_roots.Profile, "docker-deploy", SkillCatalog.FileName)));
        Assert.False(Directory.Exists(Path.Combine(_roots.Profile, "never-written")));
        Assert.Equal(SkillLearner.SessionsRequest(null), _client.Requests[0][^1].Text);
        Assert.Equal([SkillEditorTool.ToolName, SessionManagerTool.ToolName], _client.Options[0]!.Tools!.Select(t => t.Name));
    }
}
