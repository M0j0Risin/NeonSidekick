using System.Text.Json;
using NeonSidekick.App;
using NeonSidekick.Llm;
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
        StepsWhenIdle(
            Line("hi"),
            input =>
            {
                _settings.Update(d => d.Theme = "noir");
                _running!.HandOverForTestsAsync(SettingsChanges.Theme, run).GetAwaiter().GetResult();
                inForce = Theme.Current.Name;
                PushLine(input, "again");
            },
            Line("/exit"));

        string output = await RunAsync();

        Assert.Equal("noir", inForce);   // put in force at the hand-over either way
        int saved = output.IndexOf("  · Theme: noir", StringComparison.Ordinal);
        int reply = output.LastIndexOf("two", StringComparison.Ordinal);
        Assert.True(saved >= 0, output);
        Assert.Equal(saved, output.LastIndexOf("  · Theme: noir", StringComparison.Ordinal));   // said once
        Assert.Equal(run, saved > reply);
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
