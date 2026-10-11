using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Advisors;
using NeonSidekick.App;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Plans;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>neon_advisor</c> (2026-10-10, the profile advisors) over <see cref="FakeAdvisorHost"/>: the advisor found by name or role, the
/// per-turn cap, the opt-in confirm, the persona and role framing, the read-only tools and the round-trip cap, the thread replayed
/// and trimmed, the failures as <c>Error:</c> sentences and a cancel that goes on up.
/// </summary>
public partial class NeonAdvisorToolTests
{
    private readonly FakeAdvisorHost _host = new();
    private readonly AdvisorThreads _threads = new();
    private readonly List<ChatMessage> _history = [];
    private readonly RecordingView _view = new();
    private readonly List<AIFunction> _offered = [new NamedTool("read_file"), new NamedTool("write_file"), new NamedTool(AskUserTool.ToolName), new NamedTool(NeonAdvisorTool.ToolName)];
    private AppSettingsData _settings = new()
    {
        Advisors = true,
        AdvisorList = [new AdvisorEntry("dba", "database architect"), new AdvisorEntry("ux", "UX designer")],
    };

    private NeonAdvisorTool Tool(Func<AdvisorEntry, string, CancellationToken, Task<bool?>>? confirm = null) =>
        new(_host, () => _settings, () => "neon", _threads, () => (_offered, "RULES"), () => _history, confirm, _view);

    private static Task<object?> Ask(NeonAdvisorTool tool, string advisor, string question, string? context = null, CancellationToken cancellationToken = default)
    {
        var arguments = new AIFunctionArguments { [NeonAdvisorTool.AdvisorArgument] = advisor, [NeonAdvisorTool.QuestionArgument] = question };
        if (context is not null)
        {
            arguments[NeonAdvisorTool.ContextArgument] = context;
        }

        return tool.InvokeAsync(arguments, cancellationToken).AsTask();
    }

    private sealed class RecordingView : IAdvisorView
    {
        public List<string> Lines { get; } = [];

        public void Began(AdvisorEntry advisor, string question) => Lines.Add("began " + advisor.Profile + " " + question);

        public void Tool(AdvisorEntry advisor, string name, string detail) => Lines.Add("tool " + advisor.Profile + " " + name);

        public void Answered(AdvisorEntry advisor, string answer, string model, TokenUsage usage, string? warning) =>
            Lines.Add("answered " + advisor.Profile + " " + answer + " on " + model + (warning is null ? "" : " (" + warning + ")"));
    }

    private sealed class NamedTool(string name) : AIFunction
    {
        private static readonly JsonElement Schema = JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();

        public List<int> Calls { get; } = [];

        public override string Name => name;

        public override string Description => name + " stand-in";

        public override JsonElement JsonSchema => Schema;

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            Calls.Add(1);
            return new ValueTask<object?>("result of " + name);
        }
    }

    [Fact]
    public void Shape_TheNameTheSchemaTheDescriptionAndPlanMode()
    {
        var tool = Tool();

        Assert.Equal("neon_advisor", tool.Name);
        var properties = tool.JsonSchema.GetProperty("properties");
        Assert.True(properties.TryGetProperty("advisor", out _) && properties.TryGetProperty("question", out _) && properties.TryGetProperty("context", out _));
        Assert.Equal(["advisor", "question"], tool.JsonSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.EndsWith("Advisors: dba (database architect); ux (UX designer).", tool.Description);
        _settings = new AppSettingsData();
        Assert.EndsWith("Advisors: none.", tool.Description);   // read at each turn
        Assert.True(PlanTools.Allowed(NeonAdvisorTool.ToolName));
        Assert.Contains(NeonAdvisorTool.ToolName, Assistant.NeonAdvisorRule);
        Assert.Contains(Assistant.NeonAdvisorRule, Assistant.DefaultRules(markdown: false, tools: true, advisors: true));
        Assert.DoesNotContain(Assistant.NeonAdvisorRule, Assistant.DefaultRules(markdown: false, tools: true));
    }

    [Fact]
    public async Task AnAdvisor_AnswersOnItsOwnLink_UnderItsPersonaAndRole_OverTheReadOnlyTools()
    {
        var client = _host.Add("dba", persona: "You are Dee, a careful DBA.");
        client.EnqueueText("Index the ", "foreign key.");
        _history.Add(new ChatMessage(ChatRole.User, "earlier words"));

        object? result = await Ask(Tool(), "architect", "Which index?", "orders table, slow joins");

        Assert.Equal("Index the foreign key.", result);
        Assert.Equal(["dba"], _host.Linked);
        string system = Assert.Single(_host.SystemPrompts);
        Assert.StartsWith("You are Dee, a careful DBA.\n\n" + AdvisorText.Framing("neon", "database architect", tools: true), system);
        Assert.EndsWith("\n\nRULES", system);
        var request = Assert.Single(client.Requests);
        Assert.Equal(AdvisorText.Prompt("Which index?", "orders table, slow joins", null), request.Last().Text);   // brief: no recent lines
        var tools = client.Options.Single()!.Tools!.Select(t => t.Name).ToList();
        Assert.Equal(["read_file"], tools);   // write_file, ask_user and neon_advisor are never an advisor's
        Assert.Equal(["began dba Which index?", "answered dba Index the foreign key. on dba-model"], _view.Lines);
        Assert.Equal([new AdvisorPair("Which index?", "Index the foreign key.")], _threads.Of("dba"));
    }

    [Fact]
    public async Task TheThread_IsReplayed_AndKeptToItsLength()
    {
        _settings.AdvisorThreadPairs = 2;
        var client = _host.Add("ux");
        client.EnqueueText("one").EnqueueText("two").EnqueueText("three");
        var tool = Tool();

        await Ask(tool, "ux", "q1");
        await Ask(tool, "UX DESIGNER", "q2");
        tool.BeginTurn();
        await Ask(tool, "ux", "q3");

        Assert.Equal(["q1", "one"], client.Requests[1].Where(m => m.Role != ChatRole.System).Take(2).Select(m => m.Text));
        Assert.Equal([new AdvisorPair("q2", "two"), new AdvisorPair("q3", "three")], _threads.Of("ux"));   // the oldest gone past two
        Assert.Equal(5, _threads.Messages("ux").Count + 1);
    }

    [Fact]
    public async Task ContextRecent_SendsTheLastMessages()
    {
        _settings.AdvisorContext = "recent";
        var client = _host.Add("dba");
        client.EnqueueText("ok");
        _history.Add(new ChatMessage(ChatRole.User, "the schema is in schema.sql"));

        await Ask(Tool(), "dba", "q");

        Assert.Contains("user: the schema is in schema.sql", client.Requests.Single().Last().Text);
    }

    [Fact]
    public async Task ItsToolCalls_RunAndShow_UpToTheCap_TheLastRoundAskedForWords()
    {
        _settings.AdvisorMaxToolIterations = 1;
        var client = _host.Add("dba");
        client.Enqueue(FakeChatClient.Call("c1", "read_file"));
        client.EnqueueText("Having read it: add the index.");

        object? result = await Ask(Tool(), "dba", "q");

        Assert.Equal("Having read it: add the index.", result);
        Assert.Single(((NamedTool)_offered[0]).Calls);
        Assert.Contains("tool dba read_file", _view.Lines);
        Assert.Null(client.Options[1]?.Tools);   // the last round: no tools

        _settings.AdvisorMaxToolIterations = 0;
        client.EnqueueText("from memory");
        await Ask(Tool(), "dba", "q2");
        Assert.Null(client.Options[2]?.Tools);   // 0: text only
    }

    /// <summary>What an answer used goes to the tally's seam (2026-10-10): its reported requests and their tokens summed, once per answer.</summary>
    [Fact]
    public async Task AnAnswersRequests_AreToldToTheTally()
    {
        var spent = new List<(TokenUsage Usage, int Requests)>();
        var client = _host.Add("dba");
        client.Enqueue(FakeChatClient.Call("c1", "read_file"), FakeChatClient.Usage(100, 10));
        client.Enqueue(FakeChatClient.Text("Add the index."), FakeChatClient.Usage(150, 5));
        var tool = new NeonAdvisorTool(_host, () => _settings, () => "neon", _threads, () => (_offered, "RULES"), () => _history, null, _view, (usage, requests) => spent.Add((usage, requests)));

        await Ask(tool, "dba", "q");

        var (usage, requests) = Assert.Single(spent);
        Assert.Equal(2, requests);
        Assert.Equal(250, usage.Input);
        Assert.Equal(15, usage.Output);
    }

    [Fact]
    public async Task TheCap_CountsEveryAdvisor_AndStartsOverWithTheTurn()
    {
        _settings.AdvisorCallsPerTurn = 1;
        _host.Add("dba").EnqueueText("a").EnqueueText("b");
        _host.Add("ux");
        var tool = Tool();

        await Ask(tool, "dba", "q");
        Assert.Equal(AdvisorText.CapError(1), await Ask(tool, "ux", "q"));
        tool.BeginTurn();
        Assert.Equal("b", await Ask(tool, "dba", "q"));
    }

    [Fact]
    public async Task Confirm_AsksWithTheAdvisor_NoIsDeclined_NothingToAskIsRefused()
    {
        _settings.AdvisorConfirm = true;
        _host.Add("dba").EnqueueText("yes answer");
        var asked = new List<string>();

        Assert.Equal(AdvisorText.NotAskedError, await Ask(Tool(), "dba", "q"));
        Assert.Equal(AdvisorText.DeclinedError("dba"), await Ask(Tool((a, q, _) => { asked.Add(a.Profile + ":" + q); return Task.FromResult<bool?>(false); }), "dba", "q1"));
        Assert.Equal("yes answer", await Ask(Tool((_, _, _) => Task.FromResult<bool?>(true)), "dba", "q2"));
        Assert.Equal(["dba:q1"], asked);
        Assert.Equal("Let the model ask dba (database architect): Which index to add?", AdvisorText.ConfirmQuestion(new AdvisorEntry("dba", "database architect"), "Which index to add"));
    }

    [Fact]
    public async Task Refusals_AreErrorSentences()
    {
        var tool = Tool();
        Assert.Equal(AdvisorText.NoQuestionError, await Ask(tool, "dba", " "));
        Assert.StartsWith("Error: \"nobody\" names no single advisor. Name one of: dba (database architect); ux (UX designer).", (string)(await Ask(tool, "nobody", "q"))!);
        Assert.Equal(AdvisorText.MissingProfileError("dba"), await Ask(tool, "dba", "q"));   // listed, but no such profile

        _host.Add("ux");
        _host.Problems["ux"] = "http://box:8000/v1 did not answer (refused)";
        Assert.Equal(AdvisorText.FailedError("ux", "http://box:8000/v1 did not answer (refused)"), await Ask(tool, "ux", "q"));

        _host.ThrowOnRead = true;
        Assert.StartsWith("Error: the advisor dba's profile could not be read:", (string)(await Ask(Tool(), "dba", "q"))!);

        _settings = new AppSettingsData { Advisors = true };
        Assert.Equal(AdvisorText.NoAdvisorsError, await Ask(Tool(), "dba", "q"));
        Assert.False(_threads.Any);
    }

    [Fact]
    public async Task AFailedTurn_ForgetsTheLink_AndKeepsNoPair()
    {
        var client = _host.Add("dba");   // nothing enqueued: the fake's request fails

        object? result = await Ask(Tool(), "dba", "q");

        Assert.StartsWith("Error: the advisor dba could not answer:", (string)result!);
        Assert.Equal(["dba"], _host.Forgotten);
        Assert.Empty(_threads.Of("dba"));
    }

    [Fact]
    public async Task TheWarning_ReachesTheView()
    {
        _host.Warning = AdvisorText.SharedEmbeddedWarning("Gemma 4 E4B", "Gemma 4 E2B");
        _host.Add("dba").EnqueueText("fine");

        await Ask(Tool(), "dba", "q");

        Assert.Contains("answered dba fine on dba-model (wanted Gemma 4 E4B, answered on the running Gemma 4 E2B)", _view.Lines);
        Assert.Equal("dba · m · 10 in · 2 out · w", AdvisorText.Footer("dba", "m", new TokenUsage(10, 2, 12, 1, TimeSpan.Zero, TimeSpan.Zero), "w"));
    }

    [Fact]
    public async Task ACancel_GoesOnUp()
    {
        _host.Add("dba").EnqueueText("never");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Ask(Tool(), "dba", "q", cancellationToken: cts.Token));
    }

    [Fact]
    public void ReadOnlyOf_KeepsPlanModesReads_LessTheExcluded()
    {
        IReadOnlyList<AIFunction> tools = [new NamedTool("read_file"), new NamedTool("run_command"), new NamedTool(NeonAdvisorTool.ToolName), new NamedTool(CameraCaptureTool.ToolName), new NamedTool("srv__thing"), new NamedTool("sql_query")];

        Assert.Equal(["read_file", "sql_query"], NeonAdvisorTool.ReadOnlyOf(tools).Select(t => t.Name));
        Assert.Equal(["neon_advisor", "ask_user", "camera_capture", "screen_capture", "screen_list"], NeonAdvisorTool.Excluded);   // claude_code_advisor too until it was retired (2026-10-10)
    }

    [Fact]
    public void TheThreads_StoreAndRestore_ThroughTheSessionDocument()
    {
        _threads.Add("dba", new AdvisorPair("q", "a"), keep: 10);
        string json = NeonSidekick.Sessions.SessionHistory.ToJson([new ChatMessage(ChatRole.User, "hi")], advisorThreads: _threads.Snapshot());
        var restored = new AdvisorThreads();

        restored.Restore(NeonSidekick.Sessions.SessionHistory.AdvisorThreadsOf(json));

        Assert.Equal([new AdvisorPair("q", "a")], restored.Of("DBA"));
        Assert.Null(NeonSidekick.Sessions.SessionHistory.AdvisorThreadsOf(NeonSidekick.Sessions.SessionHistory.ToJson([])));
        Assert.Null(new AdvisorThreads().Snapshot());
        restored.Clear();
        Assert.False(restored.Any);
    }

    [Fact]
    public void AdvisorTurnTools_ArePlanModesNarrowing_LessTheAskers()
    {
        var read = new NamedTool("read_file");
        var write = new NamedTool("write_file");
        var ask = new NamedTool(AskUserTool.ToolName);
        var inputs = new TurnToolInputs { Standing = [], Files = [read, write], FilesEnabled = true, Ask = [ask], NeonAdvisor = [Tool()], NeonAdvisorEnabled = true };

        var (tools, rules) = ChatScreen.AdvisorTurnTools(inputs, markdown: false);

        Assert.Equal(["read_file"], tools.Select(t => t.Name));
        Assert.DoesNotContain(Assistant.NeonAdvisorRule, rules);
        Assert.Equal(([], ""), ChatScreen.AdvisorTurnTools(inputs with { ToolsEnabled = false }, markdown: false));
        Assert.True(ChatScreen.AdvisorsOffered(_settings));
        Assert.False(ChatScreen.AdvisorsOffered(new AppSettingsData { Advisors = true }));
    }

    [Fact]
    public void TheTurn_OffersTheAdvisors_WithItsRule()
    {
        var tool = Tool();
        var inputs = new TurnToolInputs { Standing = [], NeonAdvisor = [tool], NeonAdvisorEnabled = true };

        var set = ChatScreen.ComposeTurnTools(inputs);

        Assert.Same(tool, Assert.Single(set.Tools));
        Assert.Same(tool, set.Advisors);
        Assert.True(set.Rules.Advisors);
        Assert.Contains(Assistant.NeonAdvisorRule, set.Rules.DefaultRules(markdown: false));
        Assert.Empty(ChatScreen.ComposeTurnTools(inputs with { NeonAdvisorEnabled = false }).Tools);
        Assert.Empty(ChatScreen.ComposeTurnTools(inputs with { Disabled = new HashSet<string> { NeonAdvisorTool.ToolName } }).Tools);
    }
}
