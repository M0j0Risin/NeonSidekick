using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Plans;
using NeonSidekick.Sessions;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// Plan mode on the screen (2026-09-26): <c>/plan</c> enters it, the turns offer the read-only tools and
/// <c>present_plan</c>, the approval pane decides, an approval runs the plan with every tool again.
/// </summary>
public partial class ChatScreenTests
{
    private const string PlanMarkdown = "# Guide\n\n## Steps\n- [ ] Write it\n";
    private const string PlanPath = ".neon/plans/add-guide.md";

    private string PlanFile => Path.Combine(_settings.ProfileDirectory, WorkingDirectory.DefaultFolderName, ".neon", "plans", "add-guide.md");

    private static ChatResponseUpdate PresentPlanCall(string id = "p1") =>
        FakeChatClient.Call(id, PresentPlanTool.ToolName, new Dictionary<string, object?>
        {
            [PresentPlanTool.TitleArgument] = "Guide",
            [PresentPlanTool.NameArgument] = "add-guide",
            [PresentPlanTool.MarkdownArgument] = PlanMarkdown,
        });

    private static List<string> OfferedNames(ChatOptions? options) => options?.Tools?.Cast<AIFunction>().Select(t => t.Name).ToList() ?? [];

    private static object? PlanResultOf(IReadOnlyList<ChatMessage> request, string callId) =>
        request.SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Single(r => r.CallId == callId).Result;

    private static string? LastUserText(IReadOnlyList<ChatMessage> request) => request.Last(m => m.Role == ChatRole.User).Text;

    private PlanStatus? PlanFileStatus => File.Exists(PlanFile) ? PlanDocument.TryParse(File.ReadAllText(PlanFile))?.Status : null;

    /// <summary>
    /// The pane on, <paramref name="lines"/> typed at the idle line one per wait, and each approval pane answered with the
    /// next of <paramref name="answers"/>, pushed once when that pane first waits (a new pane is a new <see cref="KeySource.PendingLine"/>).
    /// </summary>
    private void PlanFixture(ConsoleKeyInfo[][] answers, params string[] lines)
    {
        _settings.Update(d => d.TtsOutput = false);
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        var input = Scripted();
        StepsWhenIdle([.. lines.Select(Line)]);
        var idle = input.OnWait!;
        var pending = new Queue<ConsoleKeyInfo[]>(answers);
        Task? answered = null;
        input.OnWait = () =>
        {
            if (_keys is { PendingLine: { IsCompleted: false } pane })
            {
                if (!ReferenceEquals(pane, answered) && pending.TryDequeue(out var keys))
                {
                    answered = pane;
                    input.Push(keys);
                }

                return;
            }

            idle();
        };
    }

    private static ConsoleKeyInfo[] PlanTyped(string text) => [.. text.Select(Keys.Char), Keys.Enter];

    [Fact]
    public async Task Plan_ApprovedOnThePane_RunsThePlan_WithEveryToolAgain()
    {
        PlanFixture([[Keys.Char('a'), Keys.Enter]], "/plan Add a guide", "/exit");
        // A plans folder of the user's own (2026-09-26): the app's plans live under .neon/plans and never touch it.
        string mine = Path.Combine(_settings.ProfileDirectory, WorkingDirectory.DefaultFolderName, "plans", "add-guide.md");
        Directory.CreateDirectory(Path.GetDirectoryName(mine)!);
        File.WriteAllText(mine, "the user's own");
        _chat.Enqueue(PresentPlanCall());
        _chat.EnqueueText("Approved, starting.");
        _chat.EnqueueText("Done: wrote it.");

        string output = await RunAsync();

        Assert.Equal(3, _chat.Requests.Count);
        // Planning: the read-only tools and present_plan, the directive in the system message, the requirement sent.
        var planning = OfferedNames(_chat.Options[0]);
        Assert.Contains(PresentPlanTool.ToolName, planning);
        Assert.Contains(ReadFileTool.ToolName, planning);
        Assert.DoesNotContain(WriteFileTool.ToolName, planning);
        Assert.DoesNotContain(RunCommandTool.ToolName, planning);
        Assert.DoesNotContain(GitCommitTool.ToolName, planning);
        Assert.Contains(PlanText.Directive("Add a guide", null, 0), _chat.Requests[0][0].Text);
        Assert.Equal("Add a guide", LastUserText(_chat.Requests[0]));
        Assert.Equal(PlanText.ApprovedResult(PlanPath), PlanResultOf(_chat.Requests[1], "p1"));
        // Carrying it out: every tool again, no directive, the message naming the file; the file marked approved.
        var doing = OfferedNames(_chat.Options[2]);
        Assert.Contains(WriteFileTool.ToolName, doing);
        Assert.DoesNotContain(PresentPlanTool.ToolName, doing);
        Assert.DoesNotContain("PLAN MODE", _chat.Requests[2][0].Text);
        Assert.Equal(PlanText.ExecuteMessage(PlanPath), LastUserText(_chat.Requests[2]));
        // Carried out without ticking its one step (the fake never writes the file): tracked, and marked incomplete after the turn.
        Assert.Equal(PlanStatus.Incomplete, PlanFileStatus);
        Assert.Contains(PlanText.IncompleteNotice(PlanPath, 0, 1), Output);
        Assert.Contains("Write it", File.ReadAllText(PlanFile));
        Assert.Equal("the user's own", File.ReadAllText(mine));
        Assert.Equal(".neon/plans/add-guide.md", PlanPath);
        // The screen: the notices, the pane with its rows, the plan printed, the glyph while planning, the placeholder back after.
        Assert.Contains(PlanText.EnteredNotice, output);
        Assert.Contains(PlanText.SavedNotice(PlanPath, 1), output);
        Assert.Contains(PlanText.ApprovalTitle, output);
        Assert.Contains(PlanText.ApproveFreshRow, output);
        Assert.Contains(PlanText.Caption(new PlanPresentation("Guide", PlanPath, 1, "")), output);
        Assert.Contains("Write it", output);
        Assert.Contains(PlanText.ApprovedNotice(PlanPath, fresh: false), output);
        Assert.Contains("Done: wrote it.", output);
        Assert.Contains(PlanText.Glyph + " ", output);
        Assert.DoesNotContain("🛠️ " + PresentPlanTool.ToolName, output);
        Assert.Contains(InputLine.PromptGlyph + ChatScreen.InputPlaceholder, output);
    }

    [Fact]
    public async Task Plan_RefinedWithFeedback_StaysPlanning_ThenCancelKeepsTheFileMarked()
    {
        PlanFixture([[Keys.Char('r'), Keys.Enter, .. PlanTyped("Mention tests")]], "/plan Add a guide", "/plan", "/plan cancel", "/exit");
        _chat.Enqueue(PresentPlanCall());
        _chat.EnqueueText("I will revise it.");

        string output = await RunAsync();

        Assert.Equal(2, _chat.Requests.Count);
        Assert.Equal(PlanText.RefineResult(PlanPath, "Mention tests"), PlanResultOf(_chat.Requests[1], "p1"));
        Assert.Contains(PlanText.FeedbackNotice("Mention tests"), output);
        Assert.Contains(InputLine.PromptGlyph + PlanText.Placeholder, output);
        Assert.Contains(PlanText.Glyph + " Planning: Add a guide", output);
        Assert.Contains($"Plan: {PlanPath} (revision 1)", output);
        Assert.Contains(PlanText.CancelledNotice(PlanPath), output);
        Assert.Equal(PlanStatus.Cancelled, PlanFileStatus);
    }

    [Fact]
    public async Task Plan_EscOnThePane_IsKeepRefining_DetailRidesPlanMode_AndNewLeavesIt()
    {
        PlanFixture([[Keys.Escape]], "/plan Add a guide", "Also a code of conduct", "/new", "hello", "/exit");
        _chat.Enqueue(PresentPlanCall());
        _chat.EnqueueText("What should change?");
        _chat.EnqueueText("Noted.");
        _chat.EnqueueText("Hi.");

        string output = await RunAsync();

        Assert.Equal(4, _chat.Requests.Count);
        Assert.Equal(PlanText.RefineResult(PlanPath, null), PlanResultOf(_chat.Requests[1], "p1"));
        Assert.Contains(PresentPlanTool.ToolName, OfferedNames(_chat.Options[2]));
        Assert.Contains(PlanText.Directive("Add a guide", PlanPath, 1), _chat.Requests[2][0].Text);
        Assert.Contains(PlanText.LeftOnResetNotice(PlanPath), output);
        Assert.Contains(WriteFileTool.ToolName, OfferedNames(_chat.Options[3]));
        Assert.DoesNotContain("PLAN MODE", _chat.Requests[3][0].Text);
        Assert.Equal(PlanStatus.Draft, PlanFileStatus);
    }

    [Fact]
    public async Task Plan_CancelledOnThePane_EndsPlanMode_AndTheNextTurnHasEveryTool()
    {
        PlanFixture([[Keys.Char('c'), Keys.Enter]], "/plan Add a guide", "go on", "/exit");
        _chat.Enqueue(PresentPlanCall());
        _chat.EnqueueText("Dropped.");
        _chat.EnqueueText("Sure.");

        string output = await RunAsync();

        Assert.Equal(PlanText.CancelledResult(PlanPath), PlanResultOf(_chat.Requests[1], "p1"));
        Assert.Equal(PlanStatus.Cancelled, PlanFileStatus);
        Assert.Contains(PlanText.CancelledNotice(PlanPath), output);
        Assert.Contains(WriteFileTool.ToolName, OfferedNames(_chat.Options[2]));
        Assert.DoesNotContain(PresentPlanTool.ToolName, OfferedNames(_chat.Options[2]));
    }

    [Fact]
    public async Task Plan_ApprovedFresh_StartsANewConversation_WithThePlanInTheMessage()
    {
        PlanFixture([[Keys.Char('f'), Keys.Enter]], "/plan Add a guide", "/exit");
        _chat.Enqueue(PresentPlanCall());
        _chat.EnqueueText("Approved.");
        _chat.EnqueueText("Done.");

        string output = await RunAsync();

        Assert.Equal(3, _chat.Requests.Count);
        var user = Assert.Single(_chat.Requests[2], m => m.Role == ChatRole.User);
        Assert.Equal(PlanText.ExecuteFreshMessage(PlanPath, PlanDocument.Body(File.ReadAllText(PlanFile))), user.Text);
        Assert.DoesNotContain(_chat.Requests[2], m => m.Text == "Add a guide");
        Assert.Contains(ChatScreen.NewConversationNotice, output);
        Assert.Contains(PlanText.ApprovedNotice(PlanPath, fresh: true), output);
        // Carried out without ticking its one step (the fake never writes the file): tracked, and marked incomplete after the turn.
        Assert.Equal(PlanStatus.Incomplete, PlanFileStatus);
        Assert.Contains(PlanText.IncompleteNotice(PlanPath, 0, 1), Output);
    }

    [Fact]
    public async Task Plan_ApproveTyped_AfterThePaneWasLeft_RunsThePlan()
    {
        PlanFixture([[Keys.Escape]], "/plan Add a guide", "/plan approve", "/exit");
        _chat.Enqueue(PresentPlanCall());
        _chat.EnqueueText("Here it is.");
        _chat.EnqueueText("Done.");

        await RunAsync();

        Assert.Equal(3, _chat.Requests.Count);
        Assert.Equal(PlanText.ExecuteMessage(PlanPath), LastUserText(_chat.Requests[2]));
        Assert.Contains(WriteFileTool.ToolName, OfferedNames(_chat.Options[2]));
        // Carried out without ticking its one step (the fake never writes the file): tracked, and marked incomplete after the turn.
        Assert.Equal(PlanStatus.Incomplete, PlanFileStatus);
        Assert.Contains(PlanText.IncompleteNotice(PlanPath, 0, 1), Output);
    }

    [Fact]
    public async Task Plan_TheWordsThatCannotRun_SaySo()
    {
        _settings.Update(d => d.TtsOutput = false);
        _chat.EnqueueText("What should it cover?");
        PushLine("/plan");
        PushLine("/plan approve");
        PushLine("/plan Add a guide");
        PushLine("/plan approve");
        PushLine("/plan show");
        PushLine("/plan cancel");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("  ✗ " + PlanText.UsageError, output);
        Assert.Contains("  ✗ " + PlanText.NotPlanningError, output);
        Assert.Contains("  ✗ " + PlanText.NothingPresentedError, output);
        Assert.Contains("Plan: not presented yet", output);
        Assert.Contains(PlanText.CancelledNotice(null), output);
        Assert.Single(_chat.Requests);
        Assert.False(File.Exists(PlanFile));
    }

    [Fact]
    public async Task Plan_WithoutTools_IsRefused()
    {
        _settings.Update(d => { d.TtsOutput = false; d.LlmOfferTools = false; });
        PushLine("/plan Add a guide");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("  ✗ " + PlanText.NeedsToolsError, output);
        Assert.Empty(_chat.Requests);
    }

    [Fact]
    public async Task Plan_WithoutThePane_IsSaved_ApprovedByTyping_AndSysShowsIt()
    {
        _settings.Update(d => d.TtsOutput = false);
        _chat.Enqueue(PresentPlanCall());
        _chat.EnqueueText("Saved; approve with /plan approve.");
        _chat.EnqueueText("Done.");
        PushLine("/plan Add a guide");
        PushLine("/sys");
        PushLine("/plan approve --fresh");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal(PlanText.SavedResult(PlanPath), PlanResultOf(_chat.Requests[1], "p1"));
        Assert.Contains(SystemPromptSummary.PlanModeHeading, output);
        Assert.Contains(ChatScreen.PlanGroupName + " (1)", output);
        Assert.Equal(3, _chat.Requests.Count);
        Assert.StartsWith(PlanText.ExecuteMessage(PlanPath), LastUserText(_chat.Requests[2]));
        // Carried out without ticking its one step (the fake never writes the file): tracked, and marked incomplete after the turn.
        Assert.Equal(PlanStatus.Incomplete, PlanFileStatus);
        Assert.Contains(PlanText.IncompleteNotice(PlanPath, 0, 1), Output);
    }

    [Fact]
    public async Task Plan_RestoredSession_IsStillPlanning()
    {
        PlanFixture([[Keys.Escape]], "/plan Add a guide", "/new", "/sessions 1", "more detail", "/exit");
        _chat.Enqueue(PresentPlanCall());
        _chat.EnqueueText("Here it is.");
        _chat.EnqueueText("Noted.");

        string output = await RunAsync();

        using (var store = new SessionStore(_settings.ProfileDirectory, _time))
        {
            SessionHistory.FromJson(store.Load(1)!.HistoryJson, out var stored);
            Assert.Equal(PlanPath, stored!.Path);
        }

        Assert.Equal(3, _chat.Requests.Count);
        Assert.Contains(PresentPlanTool.ToolName, OfferedNames(_chat.Options[2]));
        Assert.Contains(PlanText.Directive("Add a guide", PlanPath, 1), _chat.Requests[2][0].Text);
        Assert.Contains($"Plan: {PlanPath} (revision 1)", output);
    }
}
