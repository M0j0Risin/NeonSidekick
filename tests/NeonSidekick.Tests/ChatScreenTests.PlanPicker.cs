using NeonSidekick.App;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Plans;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The plans picker (2026-10-18, the user's ask: bare <c>/plan open</c> picks a plan rather than listing them, <see cref="PlansMenu"/>) and
/// <c>/plan open &lt;name&gt; --chat</c> (the plan opened to talk it through, nothing sent): Enter and a double-click in the typed mode, <c>o</c>
/// open, <c>c</c> talk, the filter, the footer, an unreadable plan, no plans, and the typo path opening the picker after its error.
/// </summary>
public partial class ChatScreenTests
{
    private const string OtherPlanPath = ".neon/plans/zz-other.md";

    /// <summary>The pane on, two plans on disk (add-guide listed first: the same time, so by path), the steps at the idle line.</summary>
    private void PickerFixture(params Action<ScriptedInput>[] steps)
    {
        _settings.Update(d => d.TtsOutput = false);
        _console.Profile.Height = 40;
        _console.Profile.Width = 160;
        _geometry = new ScreenGeometry(() => null, () => 100);   // the pane's title on row 100, its rows from 102
        OldPlan("add-guide", PlanStatus.Incomplete, "# Guide\n- [x] One\n- [ ] Two");
        OldPlan("zz-other", PlanStatus.Draft, "# Other\n- [ ] Only");
        StepsWhenIdle(steps);
    }

    [Fact]
    public async Task PlanPicker_ListsThePlans_WithTheFooter_AndEscClosesIt()
    {
        PickerFixture(Line("/plan open"), Key(Keys.Escape), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(PlanText.PickerTitle, output);
        Assert.Contains("add-guide — Guide (incomplete · 1/2 · 2026-09-20", output);
        Assert.Contains("zz-other — Other (draft · 0/1 · 2026-09-20", output);
        Assert.Contains("Requirement: Add a guide", output);
        Assert.Contains(PlanPath, output);
        Assert.Contains(PlanText.PickerKeys(PlanPickMode.Open, ralphButton: true), output);   // not planning: the Ralph button offered
        Assert.Empty(_chat.Requests);
        Assert.Equal(PlanStatus.Incomplete, PlanFileStatus);
    }

    [Fact]
    public async Task PlanPicker_Enter_OpensThePlan()
    {
        PickerFixture(Line("/plan open"), Key(Keys.Enter), Line("/exit"));
        _chat.EnqueueText("What should change?");

        string output = await RunAsync();

        Assert.Equal(PlanText.OpenMessage(PlanPath, 1, 2), LastUserText(Assert.Single(_chat.Requests)));
        Assert.Contains(PlanText.OpenedNotice(PlanPath, PlanStatus.Incomplete, 1, 2), output);
        Assert.Equal(PlanStatus.Draft, PlanFileStatus);
    }

    [Fact]
    public async Task PlanPicker_WithChat_EnterTalks_AndNothingIsSent()
    {
        PickerFixture(Line("/plan open --chat"), Key(Keys.Enter), Line("/exit"));

        string output = await RunAsync();

        Assert.Empty(_chat.Requests);
        Assert.Contains(PlanText.PickerKeys(PlanPickMode.Talk, ralphButton: true), output);
        Assert.Contains(PlanText.OpenChatNotice, output);
        Assert.Contains(InputLine.PromptGlyph + PlanText.TalkingPlaceholder, output);
    }

    [Fact]
    public async Task PlanPicker_TheButtons_CrossTheModes()
    {
        // c from /plan open talks; o from /plan open --chat opens.
        PickerFixture(Line("/plan open"), Key(Keys.Char(PlanText.TalkKey)), Line("/plan open --chat"), Key(Keys.Down), Key(Keys.Char(PlanText.OpenKey)), Line("/exit"));
        _chat.EnqueueText("What should change?");

        string output = await RunAsync();

        Assert.Contains(PlanText.OpenChatNotice, output);
        Assert.Equal(PlanText.OpenMessage(OtherPlanPath, 0, 1), LastUserText(Assert.Single(_chat.Requests)));
    }

    [Fact]
    public async Task PlanPicker_ASingleClickMoves_ADoubleClickOpens()
    {
        PickerFixture(
            Line("/plan open"),
            input => input.PushClick(4, 103),                                // zz-other: the highlight moves
            input => { input.PushClick(4, 102); input.PushClick(4, 102); },  // add-guide, twice: opened
            Line("/exit"));
        _chat.EnqueueText("What should change?");

        await RunAsync();

        Assert.Equal(PlanText.OpenMessage(PlanPath, 1, 2), LastUserText(Assert.Single(_chat.Requests)));
    }

    [Fact]
    public async Task PlanPicker_TheFilter_KeepsTheMatches_AndSaysWhenNoneMatch()
    {
        PickerFixture(
            Line("/plan open"),
            Key(new ConsoleKeyInfo('O', ConsoleKey.O, true, false, false)),   // Shift+o: "O" filters (o is the open button)
            Key(Keys.Char('t')),
            Key(Keys.Char('h')),
            Key(Keys.Enter),                                                  // "Oth": Other alone, opened
            Line("/plan open"),
            Key(Keys.Char('z')),
            Key(Keys.Char('q')),
            Key(Keys.Escape),                                                 // the filter cleared
            Key(Keys.Escape),                                                 // the pane closed
            Line("/exit"));
        _chat.EnqueueText("What should change?");

        string output = await RunAsync();

        Assert.Contains(MenuFilter.Caption("Oth", 1, 2), output);
        Assert.Equal(PlanText.OpenMessage(OtherPlanPath, 0, 1), LastUserText(Assert.Single(_chat.Requests)));
        Assert.Contains(MenuFilter.NoMatchLine("zq"), output);
    }

    [Fact]
    public async Task PlanPicker_AnUnreadablePlan_SaysSo_AndStays()
    {
        PickerFixture(Line("/plan open"), Key(Keys.Enter), Key(Keys.Escape), Line("/exit"));
        File.WriteAllBytes(Path.Combine(PlansFolder, "blob.md"), [0x50, 0x4B, 0x03, 0x04, 0x00, 0x00, 0x01, 0x02]);   // a zip: never read whole
        File.SetLastWriteTime(Path.Combine(PlansFolder, "blob.md"), new DateTime(2030, 1, 1));   // the newest, so first

        string output = await RunAsync();

        Assert.Contains(PlanText.PickerUnreadableError(".neon/plans/blob.md"), output);
        Assert.Empty(_chat.Requests);
    }

    [Fact]
    public async Task PlanPicker_NoPlans_IsOneLine()
    {
        _settings.Update(d => d.TtsOutput = false);
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        StepsWhenIdle(Line("/plan open"), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(PlanText.NoPlansNotice, output);
        Assert.DoesNotContain(PlanText.PickerTitle, output);
    }

    [Fact]
    public async Task PlanPicker_ATypo_ErrorsThenOpensThePicker()
    {
        PickerFixture(Line("/plan open nope"), Key(Keys.Enter), Line("/exit"));
        _chat.EnqueueText("What should change?");

        string output = await RunAsync();

        Assert.Contains("  ✗ " + PlanText.NoSuchPlanError("nope"), output);
        Assert.Contains(PlanText.PickerTitle, output);
        Assert.Equal(PlanText.OpenMessage(PlanPath, 1, 2), LastUserText(Assert.Single(_chat.Requests)));
    }

    [Fact]
    public async Task PlanPicker_ATypoWithChat_OpensItToTalk()
    {
        PickerFixture(Line("/plan open nope --chat"), Key(Keys.Enter), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains("  ✗ " + PlanText.NoSuchPlanError("nope"), output);
        Assert.Contains(PlanText.OpenChatNotice, output);
        Assert.Empty(_chat.Requests);
    }

    // ── /plan open <name> --chat ────────────────────────────────────────────

    [Fact]
    public async Task PlanOpenChat_SendsNothing_TalksIt_AndTheFirstMessageCarriesTheTalkingDirective()
    {
        OldPlan("add-guide", PlanStatus.Incomplete, "# Guide\n- [x] One\n- [ ] Two");
        PlanFixture([], "/plan open add-guide --chat", "What about tests?", "/exit");
        _chat.EnqueueText("Tests belong in step two.");

        string output = await RunAsync();

        Assert.Contains(PlanText.OpenedNotice(PlanPath, PlanStatus.Incomplete, 1, 2), output);
        Assert.Contains(PlanText.OpenChatNotice, output);
        Assert.Equal("What about tests?", LastUserText(Assert.Single(_chat.Requests)));
        string system = _chat.Requests[0][0].Text;
        Assert.Contains(PlanText.Directive("Add a guide", PlanPath, 2, null, null, PlanText.ResearchTools, new PlanTalk(Talking: true, Opening: false)), system);
        Assert.Contains("Read it before you discuss it if its text isn't in this conversation.", system);
        Assert.Contains(PresentPlanTool.ToolName, OfferedNames(_chat.Options[0]));
        Assert.Equal(PlanStatus.Draft, PlanFileStatus);
    }

    [Fact]
    public async Task PlanOpenChat_SwitchFirst_ThenReady_AsksForThePlan()
    {
        OldPlan("add-guide", PlanStatus.Incomplete, "# Guide\n- [x] One\n- [ ] Two");
        PlanFixture([], "/plan open --CHAT add-guide", "/plan ready", "/exit");
        _chat.EnqueueText("Here it is.");

        string output = await RunAsync();

        Assert.Contains(PlanText.OpenChatNotice, output);
        Assert.Equal(PlanText.ReadyMessage, LastUserText(Assert.Single(_chat.Requests)));
        Assert.DoesNotContain(PlanText.TalkingLine, _chat.Requests[0][0].Text);
    }
}
