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
    /// <summary>The model moved first: the hint row opens with it rather than ending with it, and a double-click there is still /server.</summary>
    [Fact]
    public async Task HintBar_TheModelMovedFirst_LeadsTheRow_AndADoubleClickRunsTheServerFlow()
    {
        _settings.Update(d => { d.TtsOutput = false; d.LlmUrl = "http://127.0.0.1:1234/v1"; d.LlmModel = "llama"; d.HintBarOrder = ["model", "status", "queued", "keys", "timers", "reading", "usage"]; });
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

    /// <summary>The model unchecked: neither at the right edge nor anywhere on the row.</summary>
    [Fact]
    public async Task HintBar_AnUncheckedSection_IsLeftOut()
    {
        _settings.Update(d => { d.TtsOutput = false; d.HintBarItems = ["status", "queued", "keys", "timers", "reading", "usage"]; });
        _console.Profile.Height = 40;
        _console.Profile.Width = 120;
        _geometry = new ScreenGeometry(() => null, () => 100);
        PushLine("/exit");

        string output = await RunAsync();

        Assert.DoesNotContain(Row(""), output);
        Assert.DoesNotContain(ScreenPane.TrailerText("llama", ChatScreen.ModelMark("llama", _settings.Current.LlmReasoning)), output);
    }

    /// <summary>
    /// Model name on the toolbar (2026-10-12, the user's ask): after the path at the right edge by default, no reasoning mark; a
    /// double-click there is /server, as the hint row's model.
    /// </summary>
    [WindowsFact]
    public async Task Toolbar_TheModelName_EndsTheRow_WithoutItsMark_AndADoubleClickRunsTheServerFlow()
    {
        _settings.Update(d => { d.TtsOutput = false; d.LlmUrl = "http://127.0.0.1:1234/v1"; d.LlmModel = "llama"; d.ToolbarItems = ["settings", "path", "model"]; });
        _console.Profile.Height = 40;
        _console.Profile.Width = 240;
        _geometry = new ScreenGeometry(() => null, () => 100);   // the hint row 102, the toolbar 103
        StepsWhenIdle(
            input =>
            {
                _time.Advance(ScreenPane.Tick);   // the toolbar follows the connect on the tick
                input.PushClick(236, 103);
                input.PushClick(236, 103);
                input.Push(Keys.Enter, Keys.Escape, Keys.Escape);
            },
            Line("/exit"));

        string output = await RunAsync();

        string cwd = WorkingDirectory.Resolve("", _settings.ProfileDirectory);
        Assert.Contains("\n" + ScreenPane.ToolbarRow(ChatScreen.SettingsToolGlyph, cwd, "llama", 239), output);
        Assert.DoesNotContain(ScreenPane.ToolbarModelSeparator + ScreenPane.TrailerText("llama", ChatScreen.ModelMark("llama", _settings.Current.LlmReasoning)), output);   // no reasoning mark
        Assert.Contains(Titled(SettingsMenu.ServerTitle), output);
        Assert.DoesNotContain("› /server", output);
    }

    /// <summary>Model name moved ahead of the path (2026-10-12): drawn among the glyphs where it stands, the glyphs after it still buttons.</summary>
    [WindowsFact]
    public async Task Toolbar_TheModelName_MovedAmongTheGlyphs_IsDrawnThere()
    {
        _settings.Update(d =>
        {
            d.TtsOutput = false;
            d.ToolbarItems = ["settings", "tools", "model"];
            d.ToolbarOrder = ["settings", "model", "tools"];
        });
        _console.Profile.Height = 40;
        _console.Profile.Width = 120;
        _geometry = new ScreenGeometry(() => null, () => 100);
        StepsWhenIdle(
            input => { _time.Advance(ScreenPane.Tick); input.PushClick(10, 103); input.PushClick(10, 103); },   // 🛠️ after "⚙️ llama " (drawn on the tick after the connect): /tools
            Key(Keys.Escape),
            Line("/exit"));

        string output = await RunAsync();

        Assert.Contains("\n" + ChatScreen.SettingsToolGlyph + " llama " + ChatScreen.ToolsToolGlyph, output);
        Assert.Contains(ToolsText.Label + " │ ", output);
    }
}
