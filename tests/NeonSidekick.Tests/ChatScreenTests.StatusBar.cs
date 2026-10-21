using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The Hint bar's sections on the screen and the toolbar's Model name (2026-10-12, the user's asks): the row in the user's order, a
/// section unchecked left out, the model in the flow and its double-click; the model's name on the toolbar after the path or among
/// the glyphs, without its reasoning mark.
/// </summary>
public partial class ChatScreenTests
{
    /// <summary>The model pinned left and moved first (2026-10-13: pinned left; moved first alone until then): the hint row opens with it, and a double-click there is still /server.</summary>
    [Fact]
    public async Task HintBar_TheModelPinnedLeftFirst_LeadsTheRow_AndADoubleClickRunsTheServerFlow()
    {
        _settings.Update(d => { d.TtsOutput = false; d.LlmUrl = "http://127.0.0.1:1234/v1"; d.LlmModel = "llama"; d.StatusBarOrder = ["model", "status", "queued", "keys", "timers", "reading", "usage"]; d.StatusBarRight = ["cwd"]; });
        _console.Profile.Height = 40;
        _console.Profile.Width = 240;
        _geometry = new ScreenGeometry(() => null, () => 100);
        StepsWhenIdle(
            input =>
            {
                input.PushClick(2, 102);
                input.PushClick(2, 102);
                input.Push(Keys.Enter, Keys.Escape, Keys.Escape);   // the server in use, keep the model, keep the level
            },
            Line("/exit"));

        string output = await RunAsync();

        string model = ScreenPane.TrailerText("llama", ChatScreen.ModelMark("llama", _settings.Current.LlmReasoning));
        Assert.Contains("\n" + model, output);
        Assert.DoesNotContain(Row(""), output);   // never at the right edge
        Assert.Contains(Titled(SettingsMenu.ServerTitle), output);
        Assert.DoesNotContain("› /server", output);
    }

    /// <summary>
    /// A double-click on the timers (2026-10-13, the user's ask) asks "Cancel all timers?": a no keeps them, a yes stops every one with
    /// /timer stop all's notice; the tally beside them, no longer in their place.
    /// </summary>
    [Fact]
    public async Task HintBar_ADoubleClickOnTheTimers_AsksAndCancelsThemAll()
    {
        _settings.Update(d => { d.TtsOutput = false; d.StatusBarItems = ["timers", "usage"]; d.ToolbarItems = []; });
        _console.Profile.Height = 40;
        _console.Profile.Width = 120;
        _geometry = new ScreenGeometry(() => null, () => 100);   // an empty line: row 100, the rule 101, the hint row 102
        StepsWhenIdle(
            Line("/timer 10m tea"),
            Line("/timer 5m eggs"),
            input => { input.PushClick(4, 102); input.PushClick(4, 102); },   // the timers: the question, No under the cursor
            Key(Keys.Enter),                                                   // no: kept
            input => { input.PushClick(4, 102); input.PushClick(4, 102); },
            input => input.Push(Keys.Char('y'), Keys.Enter),                   // yes: all stopped
            Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(Titled(Timers.TimerText.CancelAllPrompt), output);
        Assert.Contains("  · " + ChatScreen.KeptNotice + "\n", output);
        Assert.Contains(Timers.TimerText.StoppedAll(2), output);
        Assert.True(output.IndexOf(ChatScreen.KeptNotice, StringComparison.Ordinal) < output.IndexOf(Timers.TimerText.StoppedAll(2), StringComparison.Ordinal), output);
    }

    /// <summary>The model unchecked: neither at the right edge nor anywhere on the row.</summary>
    [Fact]
    public async Task HintBar_AnUncheckedSection_IsLeftOut()
    {
        _settings.Update(d => { d.TtsOutput = false; d.StatusBarItems = ["status", "queued", "keys", "timers", "reading", "usage"]; });
        _console.Profile.Height = 40;
        _console.Profile.Width = 120;
        _geometry = new ScreenGeometry(() => null, () => 100);
        PushLine("/exit");

        string output = await RunAsync();

        Assert.DoesNotContain(Row(""), output);
        Assert.DoesNotContain(ScreenPane.TrailerText("llama", ChatScreen.ModelMark("llama", _settings.Current.LlmReasoning)), output);
    }

    /// <summary>
    /// Model name on the toolbar (2026-10-12, the user's ask): after the path at the right edge by default, its reasoning mark after it
    /// (later that day, the user's ask); a double-click on the name is /server and one on the mark /reasoning, as on the hint row.
    /// </summary>
    [WindowsFact]
    public async Task Toolbar_TheModelName_EndsTheRow_WithItsMark_AndADoubleClickRunsTheServerFlow()
    {
        _settings.Update(d => { d.TtsOutput = false; d.LlmUrl = "http://127.0.0.1:1234/v1"; d.LlmModel = "llama"; d.ToolbarItems = ["settings", "path", "model"]; });
        _console.Profile.Height = 40;
        _console.Profile.Width = 240;
        _geometry = new ScreenGeometry(() => null, () => 100);   // the hint row 102, the toolbar 103
        StepsWhenIdle(
            input =>
            {
                _time.Advance(ScreenPane.Tick);   // the toolbar follows the connect on the tick
                input.PushClick(234, 103);   // "llama ○" on 232–238: the name
                input.PushClick(234, 103);
                input.Push(Keys.Enter, Keys.Escape, Keys.Escape);
            },
            input => { input.PushClick(238, 103); input.PushClick(238, 103); },   // the mark: the reasoning picker
            Key(Keys.Escape),
            Line("/exit"));

        string output = await RunAsync();

        string cwd = WorkingDirectory.Resolve("", _settings.ProfileDirectory);
        string mark = ChatScreen.ModelMark("llama", _settings.Current.LlmReasoning);
        Assert.Contains("\n" + ScreenPane.ToolbarRow(ChatScreen.SettingsToolGlyph, cwd, "llama", mark, 239), output);
        Assert.Contains(ScreenPane.ToolbarModelSeparator + ScreenPane.TrailerText("llama", mark), output);
        Assert.Contains(Titled(SettingsMenu.ReasoningTitle), output);
        Assert.Contains(Titled(SettingsMenu.ServerTitle), output);
        Assert.DoesNotContain("› /server", output);
    }

    /// <summary>Model name pinned left among the glyphs (2026-10-13; moved ahead of the path until then): drawn where it stands, the glyphs after it still buttons.</summary>
    [WindowsFact]
    public async Task Toolbar_TheModelName_PinnedLeftAmongTheGlyphs_IsDrawnThere()
    {
        _settings.Update(d =>
        {
            d.TtsOutput = false;
            d.ToolbarItems = ["settings", "tools", "model"];
            d.ToolbarOrder = ["settings", "model", "tools"];
            d.ToolbarRight = ["path"];
        });
        _console.Profile.Height = 40;
        _console.Profile.Width = 120;
        _geometry = new ScreenGeometry(() => null, () => 100);
        StepsWhenIdle(
            input => { _time.Advance(ScreenPane.Tick); input.PushClick(12, 103); input.PushClick(12, 103); },   // 🛠️ after "⚙️ llama ○ " (drawn on the tick after the connect): /tools
            Key(Keys.Escape),
            Line("/exit"));

        string output = await RunAsync();

        Assert.Contains("\n" + ChatScreen.SettingsToolGlyph + " " + ScreenPane.TrailerText("llama", ChatScreen.ModelMark("llama", _settings.Current.LlmReasoning)) + " " + ChatScreen.ToolsToolGlyph, output);
        Assert.Contains(ToolsText.Label + " │ ", output);
    }

    /// <summary>
    /// A glyph pinned right on the tool bar (2026-10-13, the user's ask): it ends the row, the left group's glyph still at column 0, and a
    /// double-click on it opens its pane; the path pinned left stands among the glyphs.
    /// </summary>
    [WindowsFact]
    public async Task Toolbar_AGlyphPinnedRight_EndsTheRow_AndADoubleClickOpensItsPane()
    {
        _settings.Update(d =>
        {
            d.TtsOutput = false;
            d.ToolbarItems = ["settings", "tools", "path"];
            d.ToolbarRight = ["tools"];
        });
        _console.Profile.Height = 40;
        _console.Profile.Width = 120;
        _geometry = new ScreenGeometry(() => null, () => 100);
        StepsWhenIdle(
            input => { input.PushClick(117, 103); input.PushClick(117, 103); },   // 🛠️ on the row's last two cells (117–118): /tools
            Key(Keys.Escape),
            Line("/exit"));

        string output = await RunAsync();

        string cwd = WorkingDirectory.Resolve("", _settings.ProfileDirectory);
        string left = ChatScreen.SettingsToolGlyph + " " + cwd;
        Assert.Contains("\n" + left + new string(' ', 119 - TextCells.Width(left) - 2) + ChatScreen.ToolsToolGlyph, output);
        Assert.Contains(ToolsText.Label + " │ ", output);
    }



    /// <summary>
    /// Hint bar's Working directory (later on 2026-10-12, the user's ask, off by default): checked, it shares the right edge with the
    /// model, after it, and a double-click on it is /cwd browse, as the tool bar's path.
    /// </summary>
    [WindowsFact]
    public async Task HintBar_TheWorkingDirectory_SharesTheRightEdge_AndADoubleClickBrowses()
    {
        _settings.Update(d => { d.TtsOutput = false; d.StatusBarItems = [.. StatusBarItems.Names]; d.ToolbarItems = []; });
        _console.Profile.Height = 40;
        _console.Profile.Width = 240;
        _geometry = new ScreenGeometry(() => null, () => 100);
        StepsWhenIdle(
            input => { input.PushClick(237, 102); input.PushClick(237, 102); },   // the path ends on the last cell
            Key(Keys.Escape),
            Line("/exit"));

        string output = await RunAsync();

        string cwd = WorkingDirectory.Resolve("", _settings.ProfileDirectory);
        string model = ScreenPane.TrailerText("llama", ChatScreen.ModelMark("llama", _settings.Current.LlmReasoning));
        Assert.Contains(model + ScreenPane.ToolbarModelSeparator + cwd, output);
        Assert.Contains(Titled(FolderText.Title + " │ " + FolderText.CollapseAllButton + " "), output);
        Assert.Null(StatusBarItems.Save(StatusBarItems.Resolve(null).ToHashSet()));   // off by default
    }

    /// <summary>
    /// The tool bar's default order (2026-10-13, the user's ask): a dim │ ahead of 💾 (later still that day), one after ⏳ (after 💾 until it came) and one after 🐚, each between two drawn glyphs
    /// (the fixture saves a bare order for the column pins; this test takes the default again).
    /// </summary>
    [WindowsFact]
    public async Task Toolbar_TheDefaultOrder_DrawsItsThreeDividers()
    {
        _settings.Update(d => { d.TtsOutput = false; d.ToolbarItems = null; d.ToolbarOrder = null; });
        _console.Profile.Height = 40;
        _console.Profile.Width = 240;
        _geometry = new ScreenGeometry(() => null, () => 100);
        LinesWhenIdle("/exit");

        string output = await RunAsync();

        Assert.Contains(ChatScreen.SkillsToolGlyph + " " + ScreenPane.DividerGlyph + " " + ChatScreen.MemoryToolGlyph + " " + ChatScreen.PromptsToolGlyph + " " + ChatScreen.QueueToolGlyph + " " + ScreenPane.DividerGlyph + " ", output);   // 🎓 │ 💾 ⏳ │ (later still on 2026-10-13), 🎓 │ 💾 📣 ⏳ │ since 2026-10-14
        Assert.Contains(ChatScreen.ShellToolGlyph + " " + ScreenPane.DividerGlyph + " " + ChatScreen.FilesToolGlyph, output);
    }

    /// <summary>
    /// The spinner pinned right (2026-10-13, the user's ask: the spinner a Hint bar section, always on but movable): under a reply it
    /// stands at the right with the stop hint after it and the model's name, not at the row's start.
    /// </summary>
    [WindowsFact]
    public async Task HintBar_TheSpinnerPinnedRight_StandsAtTheRightUnderAReply()
    {
        _settings.Update(d => { d.TtsOutput = false; d.StatusBarRight = [StatusBarItems.Spinner, StatusBarItems.Model, StatusBarItems.WorkingDirectory]; d.ToolbarItems = []; });
        _console.Profile.Height = 40;
        _console.Profile.Width = 240;
        _geometry = new ScreenGeometry(() => null, () => 100);
        _chat.EnqueueText("Hello.");
        LinesWhenIdle("hi", "/exit");

        string output = await RunAsync();

        string spinner = Theme.SpinnerFrames[0] + " " + ScreenPane.BusyText(ChatScreen.ThinkingLabel, TimeSpan.Zero) + ScreenPane.HintSeparator + ScreenPane.StopHint;
        string line = output.Split('\n').Last(l => l.Contains(spinner, StringComparison.Ordinal));
        Assert.True(TextCells.Width(line[..line.IndexOf(spinner, StringComparison.Ordinal)]) > 150, line);   // at the right, not at column 0
        Assert.Contains(spinner + ScreenPane.HintSeparator + "llama", line);
    }

    /// <summary>
    /// The Hints bar (2026-10-14, the user's ask): the first Ctrl+C's hint on a row of its own over the Status bar, which keeps the
    /// model; a double-click on that row opens /settings, as the other bars' plain text does.
    /// </summary>
    [Fact]
    public async Task HintsBar_TheFirstCtrlC_HasARowOfItsOwn_ADoubleClickThereOpensTheSettings()
    {
        _settings.Update(d => { d.TtsOutput = false; d.ToolbarItems = []; });
        _console.Profile.Height = 40;
        _console.Profile.Width = 120;
        _geometry = new ScreenGeometry(() => null, () => 100);   // an empty line: row 100, the rule 101, the Hints bar 102 (while it has a row), the Status bar 103
        StepsWhenIdle(
            Key(Keys.CtrlC),
            input => { input.PushClick(5, 102); input.PushClick(5, 102); },   // the Hints bar's text: the settings
            Key(Keys.Escape),
            Line("/exit"));

        string output = await RunAsync();

        Assert.Contains("\n" + ChatScreen.ExitHint + "\n" + Row(""), output);
        Assert.Contains("\n" + Titled(SettingsMenu.Title + " │ Client · Servers · Advisors · Automation · Voice ") + "\n", output);
        Assert.DoesNotContain("› /settings", output);
    }

    /// <summary>The Hints bar off (2026-10-14): the first Ctrl+C's hint on the Status bar, in the text sections' place, as the hint row had it.</summary>
    [Fact]
    public async Task HintsBar_Off_TheFirstCtrlC_StandsOnTheStatusBar()
    {
        _settings.Update(d => { d.TtsOutput = false; d.ToolbarItems = []; d.HintsBar = HintsBarMode.Off; });
        _console.Profile.Height = 40;
        _console.Profile.Width = 120;
        _geometry = new ScreenGeometry(() => null, () => 100);
        StepsWhenIdle(Key(Keys.CtrlC), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(Row(ChatScreen.ExitHint), output);
        Assert.DoesNotContain("\n" + ChatScreen.ExitHint + "\n", output);
    }
}
