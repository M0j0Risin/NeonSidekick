using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Plans;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>Plan mode's second round on the screen (2026-09-26): plans marked done or incomplete, <c>/plan open</c>, and <c>/plan save</c> after the hint.</summary>
public partial class ChatScreenTests
{
    private string PlansFolder => Path.Combine(_settings.ProfileDirectory, WorkingDirectory.DefaultFolderName, ".neon", "plans");

    /// <summary>Every box of the plan file ticked while request <paramref name="request"/> streams: the model carrying the plan out.</summary>
    private void TickPlanDuring(int request, string? file = null)
    {
        _chat.BeforeUpdateOf = (at, _, _) =>
        {
            string path = file ?? PlanFile;
            if (at == request && File.Exists(path))
            {
                File.WriteAllText(path, File.ReadAllText(path).Replace("- [ ]", "- [x]", StringComparison.Ordinal));
            }

            return Task.CompletedTask;
        };
    }

    private PlanHeader? PlanFileHeader => File.Exists(PlanFile) ? PlanDocument.TryParse(File.ReadAllText(PlanFile)) : null;

    private void OldPlan(string name, PlanStatus status, string body, int revision = 2)
    {
        Directory.CreateDirectory(PlansFolder);
        var at = new DateTimeOffset(2026, 9, 20, 9, 0, 0, TimeSpan.Zero);
        File.WriteAllText(Path.Combine(PlansFolder, name + ".md"), PlanDocument.Render(new PlanHeader(status, revision, at, at, "Add a guide"), "Guide", body));
    }

    // ── (3) done / incomplete ───────────────────────────────────────────────

    [Fact]
    public async Task Plan_CarriedOutWithEveryStepTicked_IsMarkedDone()
    {
        PlanFixture([[Keys.Char('a'), Keys.Enter]], "/plan Add a guide", "/exit");
        _chat.Enqueue(PresentPlanCall());
        _chat.EnqueueText("Approved.");
        _chat.EnqueueText("Done: wrote it.");
        TickPlanDuring(2);

        string output = await RunAsync();

        Assert.Equal((PlanStatus.Done, 1, 1), (PlanFileHeader!.Status, PlanFileHeader.StepsDone, PlanFileHeader.StepsTotal));
        Assert.Contains(PlanText.DoneNotice(PlanPath), output);
        Assert.DoesNotContain("marked incomplete", output);
    }

    [Fact]
    public async Task Plan_LeftIncomplete_IsSaidOnce_AndALaterTurnThatFinishesIt_MarksItDone()
    {
        PlanFixture([[Keys.Char('a'), Keys.Enter]], "/plan Add a guide", "anything else?", "go on", "/exit");
        _chat.Enqueue(PresentPlanCall());
        _chat.EnqueueText("Approved.");
        _chat.EnqueueText("Started; one question first.");
        _chat.EnqueueText("Not yet.");
        _chat.EnqueueText("Finished.");
        TickPlanDuring(4);

        string output = await RunAsync();

        Assert.Equal(5, _chat.Requests.Count);
        Assert.Equal(1, CountOccurrences(output, PlanText.IncompleteNotice(PlanPath, 0, 1)));   // the count did not move on the second turn
        Assert.Contains(PlanText.DoneNotice(PlanPath), output);
        Assert.Equal(PlanStatus.Done, PlanFileHeader!.Status);
    }

    [Fact]
    public async Task Plan_WithoutCheckboxes_StaysApproved_Quietly()
    {
        PlanFixture([[Keys.Char('a'), Keys.Enter]], "/plan Add a guide", "/exit");
        _chat.Enqueue(FakeChatClient.Call("p1", PresentPlanTool.ToolName, new Dictionary<string, object?>
        {
            [PresentPlanTool.TitleArgument] = "Guide",
            [PresentPlanTool.NameArgument] = "add-guide",
            [PresentPlanTool.MarkdownArgument] = "# Guide\n\nWrite it.",
        }));
        _chat.EnqueueText("Approved.");
        _chat.EnqueueText("Done.");

        string output = await RunAsync();

        Assert.Equal(PlanStatus.Approved, PlanFileStatus);
        Assert.DoesNotContain("marked incomplete", output);
        Assert.DoesNotContain("marked done", output);
    }

    [Fact]
    public async Task Plan_NewConversation_StopsTracking()
    {
        PlanFixture([[Keys.Char('a'), Keys.Enter]], "/plan Add a guide", "/new", "hello", "/exit");
        _chat.Enqueue(PresentPlanCall());
        _chat.EnqueueText("Approved.");
        _chat.EnqueueText("Started.");
        _chat.EnqueueText("Hi.");
        TickPlanDuring(3);   // ticked after /new: nobody is tracking it any more

        string output = await RunAsync();

        Assert.DoesNotContain(PlanText.DoneNotice(PlanPath), output);
        Assert.Equal(PlanStatus.Incomplete, PlanFileStatus);
    }

    // ── (2) /plan open ──────────────────────────────────────────────────────

    [Fact]
    public async Task Plan_Open_ReopensTheFile_AsksTheModel_AndAPresentOverwritesTheSameFile()
    {
        OldPlan("add-guide", PlanStatus.Incomplete, "# Guide\n- [x] One\n- [ ] Two");
        PlanFixture([[Keys.Escape]], "/plan open add-guide", "/exit");
        _chat.EnqueueText("What should change?");

        string output = await RunAsync();

        string message = PlanText.OpenMessage(PlanPath, 1, 2);
        Assert.Equal(message, LastUserText(_chat.Requests[0]));
        Assert.Contains(PresentPlanTool.ToolName, OfferedNames(_chat.Options[0]));
        Assert.DoesNotContain(WriteFileTool.ToolName, OfferedNames(_chat.Options[0]));
        Assert.Contains(PlanText.Directive("Add a guide", PlanPath, 2), _chat.Requests[0][0].Text);
        Assert.Contains(PlanText.OpenedNotice(PlanPath, PlanStatus.Incomplete, 1, 2), output);
        Assert.Equal(PlanStatus.Draft, PlanFileStatus);
        Assert.Contains(InputLine.PromptGlyph + PlanText.Placeholder, output);
    }

    [Fact]
    public async Task Plan_Open_ThenPresent_IsTheNextRevisionOfThatFile()
    {
        OldPlan("add-guide", PlanStatus.Cancelled, "# Guide\n- [ ] One");
        PlanFixture([[Keys.Escape]], "/plan open add-guide", "/exit");
        _chat.Enqueue(PresentPlanCall());
        _chat.EnqueueText("Revised.");

        await RunAsync();

        Assert.Equal(PlanText.RefineResult(PlanPath, null), PlanResultOf(_chat.Requests[1], "p1"));
        Assert.Equal(3, PlanFileHeader!.Revision);
        Assert.Single(Directory.GetFiles(PlansFolder));
    }

    [Fact]
    public async Task Plan_Open_AnUnknownWordErrors_SeveralWordsAreARequirement_AndAloneItLists()
    {
        _settings.Update(d => d.TtsOutput = false);
        _chat.EnqueueText("What should the doors do?");
        PushLine("/plan open");
        PushLine("/plan open nope");
        PushLine("/plan open the pod bay doors");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(PlanText.NoPlansNotice, output);
        Assert.Contains("  ✗ " + PlanText.NoSuchPlanError("nope"), output);
        Assert.Contains(PlanText.EnteredNotice, output);
        Assert.Equal("open the pod bay doors", LastUserText(Assert.Single(_chat.Requests)));
    }

    [Fact]
    public async Task Plan_OpenAlone_ListsThePlans()
    {
        OldPlan("add-guide", PlanStatus.Incomplete, "# Guide\n- [x] One\n- [ ] Two");
        _settings.Update(d => d.TtsOutput = false);
        PushLine("/plan open");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("add-guide — Guide (incomplete · 1/2 · 2026-09-20", output);
        Assert.Empty(_chat.Requests);
    }

    // ── (6) the hint and /plan save ─────────────────────────────────────────

    private const string PlanLikeReply = "# Guide\n\n- [ ] Write it\n- [ ] Link it";

    /// <summary>The pane on, the lines and keys at the idle line in order: /plan save's pane is an idle-line pane, so its keys are steps too.</summary>
    private void SaveFixture(params Action<ScriptedInput>[] steps)
    {
        _settings.Update(d => d.TtsOutput = false);
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        StepsWhenIdle(steps);
    }

    [Fact]
    public async Task Plan_AReplyThatLooksLikeAPlan_IsHinted_AndSaveThenApproveRunsIt()
    {
        SaveFixture(Line("/plan Add a guide"), Line("/plan save"), Key(Keys.Char('a')), Key(Keys.Enter), Line("/exit"));
        _chat.EnqueueText(PlanLikeReply);
        _chat.EnqueueText("Done.");

        string output = await RunAsync();

        Assert.Contains(PlanText.UnpresentedHint, output);
        Assert.Contains(PlanText.SavedNotice(".neon/plans/guide.md", 1), output);
        string file = File.ReadAllText(Path.Combine(PlansFolder, "guide.md"));
        Assert.Contains("- [ ] Link it", file);
        Assert.Equal(2, _chat.Requests.Count);
        Assert.Equal(PlanText.ExecuteMessage(".neon/plans/guide.md"), LastUserText(_chat.Requests[1]));
        Assert.Contains(WriteFileTool.ToolName, OfferedNames(_chat.Options[1]));
    }

    [Fact]
    public async Task Plan_SaveWithChangesAskedFor_SendsThem()
    {
        SaveFixture(Line("/plan Add a guide"), Line("/plan save my-guide"), Key(Keys.Char('r')), Key(Keys.Enter), Line("Mention tests"), Line("/exit"));
        _chat.EnqueueText(PlanLikeReply);
        _chat.EnqueueText("Will do.");

        await RunAsync();

        Assert.Equal(2, _chat.Requests.Count);
        Assert.Equal(PlanText.SavedFeedbackMessage(".neon/plans/my-guide.md", "Mention tests"), LastUserText(_chat.Requests[1]));
        Assert.True(File.Exists(Path.Combine(PlansFolder, "my-guide.md")));
    }

    [Fact]
    public async Task Plan_NoHint_WhenPresented_OrNotAPlan_AndSaveWithNothingErrors()
    {
        _settings.Update(d => d.TtsOutput = false);
        _chat.EnqueueText("What should the guide cover?");
        PushLine("/plan Add a guide");
        PushLine("/plan save");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.DoesNotContain(PlanText.UnpresentedHint, output);
        Assert.Contains("  ✗ " + PlanText.NothingToSaveError, output);
        Assert.False(Directory.Exists(PlansFolder) && Directory.GetFiles(PlansFolder).Length > 0);
    }

    private static int CountOccurrences(string text, string part)
    {
        int count = 0;
        for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
