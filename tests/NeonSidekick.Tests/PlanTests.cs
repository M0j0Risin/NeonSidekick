using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Memory;
using NeonSidekick.Plans;
using NeonSidekick.Sessions;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>Plan mode's pure parts and its tool (2026-09-26): the file name, the file, the allow-list, the state and <c>present_plan</c>.</summary>
public class PlanTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly WorkingDirectory _files;
    private readonly PlanSession _session = new();
    private readonly List<PlanPresentation> _presented = new();
    private PlanVerdict _verdict = new(PlanChoice.Refine);
    private readonly PresentPlanTool _tool;

    public PlanTests()
    {
        _files = new WorkingDirectory(() => _dir, _time);
        _tool = new PresentPlanTool(_session, _files, _time, (plan, _) =>
        {
            _presented.Add(plan);
            return Task.FromResult(_verdict);
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static AIFunctionArguments Args(string? title, string? markdown, string? name = null)
    {
        var args = new Dictionary<string, object?>();
        if (title is not null)
        {
            args[PresentPlanTool.TitleArgument] = title;
        }

        if (markdown is not null)
        {
            args[PresentPlanTool.MarkdownArgument] = markdown;
        }

        if (name is not null)
        {
            args[PresentPlanTool.NameArgument] = name;
        }

        return new AIFunctionArguments(args);
    }

    private string Read(string relative) => File.ReadAllText(Path.Combine(_dir, relative.Replace('/', Path.DirectorySeparatorChar)));

    private const string Plan = "# Contributing guide\n\n## Goal\nA guide.\n\n## Steps\n- [ ] Write it\n";

    // ── PlanSlug ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Add a CONTRIBUTING guide", "add-a-contributing-guide")]
    [InlineData("  add--contributing__guide!! ", "add-contributing-guide")]
    [InlineData("Café résumé naïve", "cafe-resume-naive")]
    [InlineData("v2.0 → v3.0 migration", "v2-0-v3-0-migration")]
    [InlineData("日本語", "plan")]
    [InlineData("", "plan")]
    [InlineData(null, "plan")]
    [InlineData("CON", "con-plan")]
    [InlineData("lpt1", "lpt1-plan")]
    public void Slug_IsKebabCase(string? text, string expected) => Assert.Equal(expected, PlanSlug.From(text));

    [Fact]
    public void Slug_IsCutBackToAWord_WithinTheCap()
    {
        string slug = PlanSlug.From(string.Join(' ', Enumerable.Repeat("migrate", 20)));

        Assert.True(slug.Length <= PlanSlug.MaxLength);
        Assert.False(slug.EndsWith('-'));
        Assert.EndsWith("migrate", slug);
    }

    [Fact]
    public void Slug_OneLongWord_IsCutAtTheCap()
    {
        Assert.Equal(new string('a', PlanSlug.MaxLength), PlanSlug.From(new string('a', 100)));
    }

    [Fact]
    public void Choose_NumbersPastTakenNames()
    {
        var taken = new HashSet<string> { ".neon/plans/x.md", ".neon/plans/x-2.md" };

        Assert.Equal(".neon/plans/x-3.md", PlanSlug.Choose("x", taken.Contains, "stamp"));
        Assert.Equal(".neon/plans/y.md", PlanSlug.Choose("y", taken.Contains, "stamp"));
        Assert.Equal(".neon/plans/z-stamp.md", PlanSlug.Choose("z", _ => true, "stamp"));
    }

    // ── PlanDocument ────────────────────────────────────────────────────────

    private static readonly DateTimeOffset T0 = new(2026, 9, 26, 10, 0, 0, TimeSpan.FromHours(1));

    [Fact]
    public void Render_HeaderThenBody_AndParsesBack()
    {
        var header = new PlanHeader(PlanStatus.Draft, 2, T0, T0.AddMinutes(5), "Add a \"guide\"\nplease");

        string text = PlanDocument.Render(header, "Guide", Plan);

        Assert.StartsWith("---\nstatus: draft\nrevision: 2\ncreated: 2026-09-26T10:00:00+01:00\nupdated: 2026-09-26T10:05:00+01:00\nrequirement: \"Add a \\\"guide\\\" please\"\n---\n\n# Contributing guide\n", text);
        Assert.EndsWith("- [ ] Write it\n", text);
        var back = PlanDocument.TryParse(text);
        Assert.Equal(header with { Requirement = "Add a \"guide\" please" }, back);
    }

    [Fact]
    public void Render_StripsTheModelsOwnFrontMatter_AndAddsATitleWhenTheBodyHasNone()
    {
        string text = PlanDocument.Render(new PlanHeader(PlanStatus.Draft, 1, T0, T0, "r"), "My plan", "---\nfoo: bar\n---\r\nJust steps.\r\n");

        Assert.Contains("---\n\n# My plan\n\nJust steps.\n", text);
        Assert.DoesNotContain("foo: bar", text);
        Assert.Equal("# My plan\n\nJust steps.", PlanDocument.Body(text));
    }

    [Fact]
    public void WithStatus_RewritesTheHeaderAlone()
    {
        string text = PlanDocument.Render(new PlanHeader(PlanStatus.Draft, 3, T0, T0, "r"), "T", Plan);

        string approved = PlanDocument.WithStatus(text, PlanStatus.Approved, T0.AddHours(1), "ignored");

        Assert.Equal(PlanDocument.Body(text), PlanDocument.Body(approved));
        Assert.Equal(new PlanHeader(PlanStatus.Approved, 3, T0, T0.AddHours(1), "r"), PlanDocument.TryParse(approved));
    }

    [Fact]
    public void WithStatus_OnAHandEditedFileWithoutAHeader_AddsOne()
    {
        string approved = PlanDocument.WithStatus("# Mine\n", PlanStatus.Cancelled, T0, "req");

        Assert.Equal(PlanStatus.Cancelled, PlanDocument.TryParse(approved)!.Status);
        Assert.EndsWith("\n# Mine\n", approved);
        Assert.Null(PlanDocument.TryParse("# no header"));
        Assert.Null(PlanDocument.TryParse("---\nstatus: sideways\n---\n"));
    }

    // ── PlanTools ───────────────────────────────────────────────────────────

    /// <summary>Every tool class the app ships, by its <c>ToolName</c>: the MCP wrapper aside (its names are the servers'), and present_plan (plan mode's own).</summary>
    private static IReadOnlyList<string> EveryToolName() =>
        typeof(SaveMemoryTool).Assembly.GetTypes()
            .Where(t => typeof(AIFunction).IsAssignableFrom(t) && !t.IsAbstract)
            .Select(t => t.GetField("ToolName", BindingFlags.Public | BindingFlags.Static)?.GetRawConstantValue() as string)
            .OfType<string>()
            .Where(name => name != PresentPlanTool.ToolName)
            .ToList();

    [Fact]
    public void EveryTool_IsClassifiedExactlyOnce()
    {
        var names = EveryToolName();

        Assert.True(names.Count > 50, string.Join(", ", names));
        Assert.All(names, name => Assert.True(PlanTools.ReadOnly.Contains(name) ^ PlanTools.Mutating.Contains(name), $"{name}: add it to PlanTools.ReadOnly or PlanTools.Mutating"));
        Assert.Empty(PlanTools.ReadOnly.Intersect(PlanTools.Mutating));
        Assert.Empty(PlanTools.ReadOnly.Concat(PlanTools.Mutating).Except(names));
    }

    [Fact]
    public void Widen_AddsWhatPlanModeDrops_ToTheToolsList()
    {
        var read = new StubTool("read_file");
        var write = new StubTool("write_file");
        var mcp = new StubTool("srv__read_file");

        var widened = PlanTools.Widen(new HashSet<string> { "zip" }, [read, write], null, [mcp]);

        Assert.Equal(["srv__read_file", "write_file", "zip"], widened.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void PrepareTurn_InPlanMode_OffersTheReadOnlyToolsAndPresentPlan_WithTheDirective()
    {
        var assistant = new Assistant(new FakeChatClient(), new ConversationHistory(""), new LlmTimeouts(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)));
        var memory = new MemoryStore(_dir);
        string home = Path.Combine(_dir, "profile");
        var fileTools = ChatScreen.FileTools(_files, () => true, _ => { }, () => new());
        _session.Enter("Add a guide");

        ChatScreen.PrepareTurn(assistant, memory, ChatScreen.MemoryTools(memory), ChatScreen.ClockTools(_time), new PersonaFile(home), new OperataFile(home), new VocaliaFile(home), memoryEnabled: true, speechOutput: false, fileTools: fileTools, filesEnabled: true, plan: _session.Turn(_tool));

        var names = assistant.Tools.Select(t => t.Name).ToList();
        Assert.Equal(PresentPlanTool.ToolName, names[^1]);
        Assert.Contains("read_file", names);
        Assert.Contains("recall_memory", names);
        Assert.DoesNotContain("write_file", names);
        Assert.DoesNotContain("delete", names);
        Assert.DoesNotContain("save_memory", names);
        Assert.All(names.Where(n => n != PresentPlanTool.ToolName), n => Assert.Contains(n, PlanTools.ReadOnly));
        Assert.Contains(PlanText.Directive("Add a guide", null, 0), assistant.History.SystemPrompt);

        // Plan mode off: the same call offers everything again, no directive.
        _session.Exit();
        ChatScreen.PrepareTurn(assistant, memory, ChatScreen.MemoryTools(memory), ChatScreen.ClockTools(_time), new PersonaFile(home), new OperataFile(home), new VocaliaFile(home), memoryEnabled: true, speechOutput: false, fileTools: fileTools, filesEnabled: true, plan: _session.Turn(_tool));
        Assert.Contains("write_file", assistant.Tools.Select(t => t.Name));
        Assert.DoesNotContain(PresentPlanTool.ToolName, assistant.Tools.Select(t => t.Name));
        Assert.DoesNotContain("PLAN MODE", assistant.History.SystemPrompt);
    }

    [Fact]
    public void Directive_RidesUnderACustomOperata_AheadOfTheVoice()
    {
        string prompt = Assistant.SystemPrompt(true, null, operatingRules: "My rules.", voiceDirective: "Speak.", plan: "PLAN MODE is on.");

        Assert.EndsWith("My rules.\n\nPLAN MODE is on.\n\nSpeak.", prompt);
    }

    // ── PlanSession and the stored copy ─────────────────────────────────────

    [Fact]
    public void Session_RoundTripsThroughTheStoredHistory_ApprovalNever()
    {
        _session.Enter("  Add a guide ");
        _session.Presented(".neon/plans/g.md", "Guide", 2, T0);
        _session.Approve(fresh: true);

        string json = SessionHistory.ToJson([new ChatMessage(ChatRole.User, "hi")], _session.ToStored());
        var messages = SessionHistory.FromJson(json, out var stored);
        var back = new PlanSession();
        back.Restore(stored);

        Assert.Single(messages);
        Assert.True(back.Active);
        Assert.Equal("Add a guide", back.Requirement);
        Assert.Equal(".neon/plans/g.md", back.Path);
        Assert.Equal(2, back.Revision);
        Assert.Equal(T0, back.Created);
        Assert.Null(back.Approved);
    }

    [Fact]
    public void Session_Off_StoresNothing_AndARowWithoutPlanReadsAsOff()
    {
        string json = SessionHistory.ToJson([new ChatMessage(ChatRole.User, "hi")], _session.ToStored());

        Assert.DoesNotContain("Plan", json);
        SessionHistory.FromJson(json, out var stored);
        Assert.Null(stored);
        _session.Enter("x");
        _session.Restore(stored);
        Assert.False(_session.Active);
    }

    [Theory]
    [InlineData("", false, "Usage")]
    [InlineData("", true, "Show")]
    [InlineData("show", true, "Show")]
    [InlineData("Approve", true, "Approve")]
    [InlineData("approve  --fresh", true, "ApproveFresh")]
    [InlineData("--fresh approve", true, "ApproveFresh")]
    [InlineData("cancel", true, "Cancel")]
    [InlineData("cancel the old one", true, "Detail")]
    [InlineData("approve", false, "NotPlanning")]
    [InlineData("cancel", false, "NotPlanning")]
    [InlineData("add a guide", false, "Enter")]
    [InlineData("show me the code", false, "Enter")]
    public void ParsePlanArgs_SubcommandsOnlyWhole(string args, bool planning, string expected) =>
        Assert.Equal(expected, ChatScreen.ParsePlanArgs(args, planning).ToString());

    [Fact]
    public void Plan_IsRefusedMidTurn_WithOrWithoutAnArgument()
    {
        Assert.Equal(MidTurnClass.Deferred, ChatScreen.MidTurnPolicy(SlashCommand.Plan, hasArgs: true));
        Assert.Equal(MidTurnClass.Deferred, ChatScreen.MidTurnPolicy(SlashCommand.Plan, hasArgs: false));
        Assert.Equal((SlashCommand.Plan, "approve --fresh"), SlashCommands.Parse("/PLAN  approve --fresh "));
    }

    [Fact]
    public void PlanStrip_PutsTheGlyphFirst_WhilePlanning()
    {
        Assert.Equal("🔊", ChatScreen.PlanStrip(false, "🔊"));
        Assert.Equal(PlanText.Glyph, ChatScreen.PlanStrip(true, ""));
        Assert.Equal(PlanText.Glyph + ChatScreen.GlyphSeparator + "🔊", ChatScreen.PlanStrip(true, "🔊"));
    }

    // ── present_plan ────────────────────────────────────────────────────────

    [Fact]
    public void Tool_Schema_DeclaresTheKeysRead()
    {
        var properties = _tool.JsonSchema.GetProperty("properties");

        Assert.Equal(PresentPlanTool.ToolName, _tool.Name);
        Assert.Equal([PresentPlanTool.TitleArgument, PresentPlanTool.NameArgument, PresentPlanTool.MarkdownArgument], properties.EnumerateObject().Select(p => p.Name));
        Assert.Equal([PresentPlanTool.TitleArgument, PresentPlanTool.MarkdownArgument], _tool.JsonSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Tool_OutsidePlanMode_Refuses()
    {
        Assert.Equal(PlanText.NotPlanningResult, await _tool.InvokeAsync(Args("T", Plan)));
        Assert.False(Directory.Exists(Path.Combine(_dir, PlanSlug.Folder)));
    }

    [Fact]
    public async Task Tool_MissingArguments_AreErrors()
    {
        _session.Enter("x");

        Assert.Equal(PlanText.MarkdownRequired, await _tool.InvokeAsync(Args("T", "  ")));
        Assert.Equal(PlanText.TitleRequired, await _tool.InvokeAsync(Args(null, Plan)));
        Assert.Empty(_presented);
    }

    [Fact]
    public async Task Tool_SavesUnderPlans_AsksTheUser_AndARevisionKeepsThePath()
    {
        _session.Enter("Add a guide");
        _verdict = new PlanVerdict(PlanChoice.Refine, "Mention tests");

        object? first = await _tool.InvokeAsync(Args("Contributing guide", Plan, name: "Add Contributing Guide"));

        Assert.Equal(PlanText.RefineResult(".neon/plans/add-contributing-guide.md", "Mention tests"), first);
        var presented = Assert.Single(_presented);
        Assert.Equal(new PlanPresentation("Contributing guide", ".neon/plans/add-contributing-guide.md", 1, Plan.TrimEnd()), presented);
        var header = PlanDocument.TryParse(Read(".neon/plans/add-contributing-guide.md"))!;
        Assert.Equal((PlanStatus.Draft, 1, "Add a guide"), (header.Status, header.Revision, header.Requirement));

        // The second revision: another name is ignored, the file is the same.
        _time.Advance(TimeSpan.FromMinutes(3));
        _verdict = new PlanVerdict(PlanChoice.Refine);
        object? second = await _tool.InvokeAsync(Args("Contributing guide", Plan + "- [ ] Test it\n", name: "other"));

        Assert.Equal(PlanText.RefineResult(".neon/plans/add-contributing-guide.md", null), second);
        Assert.False(File.Exists(Path.Combine(_dir, ".neon", "plans", "other.md")));
        var revised = PlanDocument.TryParse(Read(".neon/plans/add-contributing-guide.md"))!;
        Assert.Equal(2, revised.Revision);
        Assert.Equal(header.Created, revised.Created);
        Assert.True(revised.Updated > revised.Created);
        Assert.Contains("- [ ] Test it", Read(".neon/plans/add-contributing-guide.md"));
        Assert.Equal(2, _session.Revision);
    }

    [Fact]
    public async Task Tool_ANewPlan_DoesNotOverwriteAnOldOneOfTheSameName()
    {
        Directory.CreateDirectory(Path.Combine(_dir, ".neon", "plans"));
        File.WriteAllText(Path.Combine(_dir, ".neon", "plans", "guide.md"), "old");
        _session.Enter("x");

        await _tool.InvokeAsync(Args("Guide", Plan));

        Assert.Equal("old", Read(".neon/plans/guide.md"));
        Assert.Equal(".neon/plans/guide-2.md", _session.Path);
    }

    [Theory]
    [InlineData(PlanChoice.Approve)]
    [InlineData(PlanChoice.ApproveFresh)]
    public async Task Tool_Approved_IsNotedForTheTurnsEnd_AndASecondCallIsTurnedAway(PlanChoice choice)
    {
        _session.Enter("x");
        _verdict = new PlanVerdict(choice);

        Assert.Equal(PlanText.ApprovedResult(".neon/plans/guide.md"), await _tool.InvokeAsync(Args("Guide", Plan)));
        Assert.Equal(choice, _session.Approved);
        Assert.True(_session.Active);
        Assert.Equal(PlanText.AlreadyApprovedResult, await _tool.InvokeAsync(Args("Guide", Plan)));
        Assert.Single(_presented);
    }

    [Fact]
    public async Task Tool_Cancelled_MarksTheFile_AndEndsPlanMode()
    {
        _session.Enter("x");
        _verdict = new PlanVerdict(PlanChoice.Cancel);

        Assert.Equal(PlanText.CancelledResult(".neon/plans/guide.md"), await _tool.InvokeAsync(Args("Guide", Plan)));
        Assert.False(_session.Active);
        Assert.Equal(PlanStatus.Cancelled, PlanDocument.TryParse(Read(".neon/plans/guide.md"))!.Status);
    }

    [Fact]
    public async Task Tool_Saved_TellsTheModelToStop()
    {
        _session.Enter("x");
        _verdict = new PlanVerdict(PlanChoice.Saved);

        Assert.Equal(PlanText.SavedResult(".neon/plans/guide.md"), await _tool.InvokeAsync(Args("Guide", Plan)));
        Assert.Null(_session.Approved);
        Assert.True(_session.Active);
    }

    [Fact]
    public async Task Tool_AFolderInTheWay_IsNotWrittenOver()
    {
        Directory.CreateDirectory(Path.Combine(_dir, ".neon", "plans", "guide.md"));
        _session.Enter("x");

        await _tool.InvokeAsync(Args("Guide", Plan));

        Assert.Equal(".neon/plans/guide-2.md", _session.Path);
    }

    [Fact]
    public void MarkFile_AMissingFile_IsAnError()
    {
        Assert.Equal(FileText.Missing(".neon/plans/none.md"), PlanFiles.MarkFile(_files, ".neon/plans/none.md", PlanStatus.Approved, T0, "r"));
    }

    /// <summary>A named tool that does nothing, for the allow-list's pure parts.</summary>
    private sealed class StubTool(string name) : AIFunction
    {
        public override string Name => name;

        public override JsonElement JsonSchema => NeonSidekick.Llm.Tools.ToolSchema.Parse("""{"type":"object","properties":{}}""");

        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) => new((object?)null);
    }
}
