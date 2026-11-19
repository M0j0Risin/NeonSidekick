using System.Text.Json;
using NeonSidekick.App;
using NeonSidekick.Llm;
using NeonSidekick.Plans;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The theme repaint's review fixes (2026-11-17, the admin-themes-render review's §1): the latest <see cref="ChatScreen.ThemeRepaintTurns"/>
/// turns under a line naming the rest (#40, the user's call), the <c>/copy</c> log and the Web UI's copy left as they were (#1, #154), a
/// picture keeping its id across repaints (#90), lines printed after the repaint (#7, #159), and a light theme on the terminal's own page (#179).
/// </summary>
public partial class ChatScreenTests
{
    [Fact]
    public async Task Theme_OverMoreTurnsThanTheCap_RedrawsTheLatest_UnderAnEarlierTurnsLine_LeavingCopyAndThePage()
    {
        using var theme = new ThemeScope();
        _settings.Update(d => { d.TtsOutput = false; d.WelcomeSplashMode = "off"; });
        PaneOf40Rows();
        int turns = ChatScreen.ThemeRepaintTurns + 2;
        for (int i = 1; i <= turns; i++)
        {
            _chat.EnqueueText(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"reply {i:00}"));
        }

        int copies = -1, feed = -1, copiesAfter = -1, feedAfter = -1;
        var steps = Enumerable.Range(1, turns).Select(i => Line(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"turn {i:00}"))).ToList();
        steps.Add(input => { copies = _running!.CopyLogCountForTests; feed = Web.Feed.Count; PushLine(input, "/theme CyberPunk"); });
        steps.Add(input => { copiesAfter = _running!.CopyLogCountForTests; feedAfter = Web.Feed.Count; PushLine(input, "/exit"); });
        StepsWhenIdle([.. steps]);

        string output = await RunAsync();

        string repainted = output[output.LastIndexOf(ThemeText.EarlierTurns(2), StringComparison.Ordinal)..];
        Assert.DoesNotContain("› turn 01", repainted);
        Assert.DoesNotContain("› turn 02", repainted);
        Assert.Contains("› turn 03", repainted);
        Assert.Contains("reply 22", repainted);
        Assert.Contains("  · Theme: cyberpunk", repainted);
        Assert.Equal(turns, copies);
        Assert.Equal(copies, copiesAfter);   // the /copy log not filled a second time
        Assert.InRange(feedAfter - feed, 0, 2);   // the page's copy left as it was: only the command and its saved line, no replayed turns
        Assert.Equal("(1 earlier turn isn't redrawn; /sessions shows it)", ThemeText.EarlierTurns(1));
    }

    [Fact]
    public async Task Theme_UnderTheCap_DrawsEveryTurn_AndNoEarlierTurnsLine()
    {
        using var theme = new ThemeScope();
        _settings.Update(d => { d.TtsOutput = false; d.WelcomeSplashMode = "off"; });
        PaneOf40Rows();
        _chat.EnqueueText("one").EnqueueText("two").EnqueueText("three");
        LinesWhenIdle("a", "b", "c", "/theme noir", "/exit");

        string output = await RunAsync();

        string repainted = output[NthScreen(output, 1)..];
        Assert.Contains("› a", repainted);
        Assert.Contains("› c", repainted);
        Assert.DoesNotContain("aren't redrawn", output);
    }

    /// <summary>A second theme repaint adds no picture entries (#90): each history picture keeps the id the first repaint gave it.</summary>
    [WindowsFact]
    public async Task Theme_ASecondRepaint_ReusesThePicturesIds()
    {
        using var theme = new ThemeScope();
        _settings.Update(d => { d.TtsOutput = false; d.ShowImageThumbnails = true; d.WelcomeSplashMode = "off"; });
        PaneOf40Rows();
        long id = SeedPictureSession();
        int afterFirst = -1, afterSecond = -1;
        StepsWhenIdle(
            Line("/sessions " + id),
            Line("/theme noir"),
            input => { afterFirst = _running!.PictureCountForTests; PushLine(input, "/theme cyberpunk"); },
            input => { afterSecond = _running!.PictureCountForTests; PushLine(input, "/exit"); });

        await RunAsync();

        Assert.True(afterFirst > 0);
        Assert.Equal(afterFirst, afterSecond);
    }

    /// <summary>The tools switch's line comes after the theme's repaint (#7, #159), so the redraw never wipes it.</summary>
    [Fact]
    public async Task Theme_WithTheToolsSwitch_TheToolsLine_ComesAfterTheRepaint()
    {
        using var theme = new ThemeScope();
        _settings.Update(d => { d.TtsOutput = false; d.WelcomeSplashMode = "off"; d.Theme = "synthwave"; });
        PaneOf40Rows();
        _chat.EnqueueText("one").EnqueueText("two");
        LinesWhenIdle("hi", "/profile reload", "again", "/exit");
        var edited = AppSettings.Copy(_settings.Current);
        edited.Theme = "noir";
        edited.LlmOfferTools = !edited.LlmOfferTools;
        await _settings.FlushAsync();
        File.WriteAllText(_settings.FilePath, JsonSerializer.Serialize(edited, SettingsJsonContext.Default.AppSettingsData));

        string output = await RunAsync();

        string repainted = output[NthScreen(output, 1)..];
        Assert.Contains(ChatScreen.ToolsChangedNotice(edited.LlmOfferTools), repainted);
        Assert.True(repainted.IndexOf("  · Theme: noir", StringComparison.Ordinal) < repainted.IndexOf(ChatScreen.ToolsChangedNotice(edited.LlmOfferTools), StringComparison.Ordinal));
    }

    /// <summary>
    /// A theme handed over at a run's turn end (botchat, <c>/test</c>, an advisor, a custom command) is put in force, and its repaint and saved
    /// line wait for the idle line (#76): here they come after the next turn's reply. A main-chat hand-over repaints at once, before it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Theme_HandedOverAtARunsEnd_RepaintsAtTheIdleLine_ElseAtOnce(bool run)
    {
        using var theme = new ThemeScope();
        _settings.Update(d => { d.TtsOutput = false; d.WelcomeSplashMode = "off"; d.Theme = "synthwave"; });
        PaneOf40Rows();
        _chat.EnqueueText("one").EnqueueText("two");
        string? inForce = null;
        int repaints = -1;
        StepsWhenIdle(
            Line("hi"),
            input =>
            {
                _settings.Update(d => d.Theme = "noir");
                _running!.HandOverForTestsAsync(SettingsChanges.Theme, run).GetAwaiter().GetResult();
                inForce = Theme.Current.Name;
                PushLine(input, "again");
            },
            input => { repaints = _running!.ThemeRepaintsForTests; PushLine(input, "/exit"); });

        string output = await RunAsync();

        Assert.Equal("noir", inForce);   // put in force at the hand-over either way
        Assert.Equal(1, repaints);   // one repaint, at once or at the idle line
        int saved = output.IndexOf("  · Theme: noir", StringComparison.Ordinal);
        int reply = output.LastIndexOf("two", StringComparison.Ordinal);
        Assert.True(saved >= 0, output);
        Assert.Equal(saved, output.LastIndexOf("  · Theme: noir", StringComparison.Ordinal));   // said once
        Assert.Equal(run, saved > reply);
    }

    /// <summary>
    /// A theme handed over during a Ralph pass (2026-11-17, the user's call: never repainted during an automation loop): put in force and said at
    /// once, and the loop's lines all stay. Each pass line, the steps' notices and the stop's summary are drawn once, never again by a repaint, then
    /// or at the idle line. A main-chat hand-over still repaints at once (<see cref="Theme_HandedOverAtARunsEnd_RepaintsAtTheIdleLine_ElseAtOnce"/>).
    /// </summary>
    [Fact]
    public async Task Theme_HandedOverDuringARalphPass_IsSaid_AndTheLoopsLinesStay()
    {
        using var theme = new ThemeScope();
        RalphPlan();
        RalphFixture();
        _settings.Update(d => { d.WelcomeSplashMode = "off"; d.Theme = "synthwave"; });
        RalphPass("1");
        RalphPass("2");
        string? inForce = null;
        _chat.BeforeUpdateOf = async (request, update, _) =>
        {
            if (request == 1 && update == 0)
            {
                // Pass 1's closing reply: the hand-over a pane closed under it would make at the turn's end.
                _settings.Update(d => d.Theme = "noir");
                await _running!.HandOverForTestsAsync(SettingsChanges.Theme, run: false);
                inForce = Theme.Current.Name;
            }
        };

        int repaints = -1;
        StepsWhenIdle(Line("/plan ralph add-guide"), Yes, input => { repaints = _running!.ThemeRepaintsForTests; PushLine(input, "/exit"); });

        string output = await RunAsync();

        Assert.Equal("noir", inForce);
        Assert.Equal(4, _chat.Requests.Count);
        Assert.Equal(0, repaints);   // neither at the hand-over nor at the idle line after the loop
        // The transcript as the screen keeps it (its last copy, written out at the exit): every line of the loop, in order, the theme's in place.
        string kept = output[output.LastIndexOf(RalphText.TranscriptLine(1, 1, 2, "One"), StringComparison.Ordinal)..];
        string[] order =
        [
            RalphText.TranscriptLine(1, 1, 2, "One"), "  · Theme: noir", RalphText.StepDoneNotice(1, 2, "One"),
            RalphText.TranscriptLine(2, 2, 2, "Two"), RalphText.StepDoneNotice(2, 2, "Two"), RalphText.StopNotice(RalphStop.Done, PlanPath, 2, 2),
        ];
        int at = -1;
        foreach (string line in order)
        {
            int next = kept.IndexOf(line, at + 1, StringComparison.Ordinal);
            Assert.True(next > at, line + " missing or out of order in:\n" + kept);
            at = next;
        }
    }

    /// <summary>The Ralph loop's passes are turns inside an automation loop; a turn once it has stopped is not (2026-11-17).</summary>
    [Fact]
    public async Task Ralph_ThePasses_AreInsideTheLoop_AndALaterTurnOutside()
    {
        RalphPlan();
        RalphFixture(Line("/plan ralph add-guide"), Yes, Line("after"), Line("/exit"));
        RalphPass("1");
        RalphPass("2");
        _chat.EnqueueText("An ordinary turn.");
        var seen = new Dictionary<int, bool>();
        _chat.BeforeUpdateOf = (request, update, _) =>
        {
            seen.TryAdd(request, _running!.InAutomationLoopForTests);
            return Task.CompletedTask;
        };

        await RunAsync();

        Assert.Equal(5, _chat.Requests.Count);
        Assert.All(Enumerable.Range(0, 4), i => Assert.True(seen[i]));
        Assert.False(seen[4]);
    }

    /// <summary>
    /// A gap review's passes are turns inside an automation loop; the planning turn and the plan approved on the pane the review brings back
    /// are not, though that pane opens under the last pass's driver (2026-11-17).
    /// </summary>
    [Fact]
    public async Task PlanGaps_ThePasses_AreInsideTheLoop_ThePlanningAndTheRunOutside()
    {
        _settings.Update(d => d.PlanGapCleanPasses = 2);   // the confirming pass is this test's subject (1 is the default since 2026-11-19)
        PlanFixture([[Keys.Char('g'), Keys.Enter], [Keys.Char('a'), Keys.Enter]], "/plan Add a guide", "/exit");
        _chat.Enqueue(PresentPlanCall());
        _chat.EnqueueText("Closing the gaps.");
        _chat.Enqueue(PresentGapsCall("p2", RevisedPlanMarkdown, "Add a test step"));
        _chat.EnqueueText("Pass 1 done.");
        _chat.Enqueue(PresentGapsCall("p3", RevisedPlanMarkdown));
        _chat.EnqueueText("Pass 2 clean.");
        _chat.Enqueue(PresentGapsCall("p4", RevisedPlanMarkdown));
        _chat.EnqueueText("Approved, starting.");
        _chat.EnqueueText("Done.");
        var seen = new Dictionary<int, bool>();
        _chat.BeforeUpdateOf = (request, update, _) =>
        {
            seen.TryAdd(request, _running!.InAutomationLoopForTests);
            return Task.CompletedTask;
        };

        await RunAsync();

        Assert.Equal(9, _chat.Requests.Count);
        Assert.False(seen[0]);   // the planning turn
        Assert.All([2, 3, 4, 5, 6], i => Assert.True(seen[i]));   // the passes
        Assert.False(seen[8]);   // the plan carried out once the review is over
    }

    /// <summary>A code audit's passes are turns inside an automation loop, its last one too, and a turn after it is not (2026-11-17).</summary>
    [Fact]
    public async Task CodeAudit_ThePasses_AreInsideTheLoop_ALaterTurnOutside()
    {
        AuditFixture(Line("/code-audit src security"), Key(Keys.Enter), Line("after"), Line("/exit"));
        ThreePasses();
        _chat.EnqueueText("An ordinary turn.");
        var seen = new Dictionary<int, bool>();
        _chat.BeforeUpdateOf = (request, update, _) =>
        {
            seen.TryAdd(request, _running!.InAutomationLoopForTests);
            return Task.CompletedTask;
        };

        await RunAsync();

        Assert.Equal(8, _chat.Requests.Count);
        Assert.All(Enumerable.Range(0, 7), i => Assert.True(seen[i]));
        Assert.False(seen[7]);
    }

    /// <summary>A light theme with Themed background off says so after the saved line (#179); with it on, nothing more.</summary>
    [Fact]
    public async Task Theme_ALightOne_OnTheTerminalsOwnPage_SaysSo()
    {
        using var theme = new ThemeScope();
        _settings.Update(d => { d.TtsOutput = false; d.WelcomeSplashMode = "off"; d.ThemedBackground = false; });
        PaneOf40Rows();
        LinesWhenIdle("/theme paper", "/theme synthwave", "/exit");

        string output = await RunAsync();

        int saved = output.IndexOf("  · Theme: paper", StringComparison.Ordinal);
        int warned = output.IndexOf(ThemeText.LightNeedsBackground[..40], StringComparison.Ordinal);
        Assert.True(saved >= 0 && warned > saved, output);
        Assert.Equal(warned, output.LastIndexOf(ThemeText.LightNeedsBackground[..40], StringComparison.Ordinal));   // the dark one says nothing
    }
}
