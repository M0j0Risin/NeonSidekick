using System.Text.Json.Nodes;
using NeonSidekick.App;
using NeonSidekick.Llm;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/theme edit</c> on the screen (2026-11-16, plan sharded-hugging-puppy #54): the fifteen color rows, ←/→ lighter and darker, the color
/// picker's bars and hex row, the buttons, Esc with changes, a built-in's copy, the contrast warnings, and a save that rewrites only
/// <c>colors</c> and repaints when the theme was the one in force.
/// </summary>
public partial class ChatScreenTests
{
    private const int DimRow = 8;

    /// <summary>The editor is a pane: a screen 120 by 40, the list and the preview side by side.</summary>
    private void EditorPane()
    {
        _console.Profile.Width = 120;
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
    }

    private string MineTheme() => Path.Combine(_settings.ThemesDirectory, "mine.json");

    /// <summary>A user theme "mine": synthwave's colors with a description and a key the app doesn't know, which a save keeps.</summary>
    private string WriteMine()
    {
        string json = ThemeJson.File("\"name\": \"mine\", \"description\": \"my own\", \"extra\": 1");
        WriteUserTheme("mine.json", json);
        return json;
    }

    /// <summary>A notice's words before <paramref name="path"/>: a long path wraps to a line of its own.</summary>
    private static string BeforePath(string notice, string path) => notice[..notice.IndexOf(path, StringComparison.Ordinal)].TrimEnd();

    private static string? ColorIn(string path, string role) => (string?)JsonNode.Parse(File.ReadAllText(path))!["colors"]![role];

    private void PushKeys(ConsoleKeyInfo key, int times = 1)
    {
        for (int i = 0; i < times; i++)
        {
            _console.Input.PushKey(key);
        }
    }

    [Fact]
    public async Task ThemeEdit_TheThemeInForce_ALighterStep_Saved_RewritesOnlyColors_AndRepaintsKeepingTheConversation()
    {
        using var theme = new ThemeScope();
        EditorPane();
        WriteMine();
        _settings.Update(d => { d.TtsOutput = false; d.Theme = "mine"; });
        ThemeName.Apply(_settings.Current, _settings.ThemesDirectory);   // as Program does at the start
        _chat.EnqueueText("hello");
        _chat.EnqueueText("again to you");
        // Each step when the screen waits idle, so the keys reach the editor, not the input line under the reply.
        var down = Enumerable.Repeat(Keys.Down, DimRow);
        StepsWhenIdle(Line("hi"), Line("/theme edit"), Keys2([.. down, Keys.Right, Keys.Char('s')]), Line("again"), Line("/exit"));   // no name: the theme in force; dim 4% lighter

        string output = await RunAsync();

        var lighter = HslColor.From(ShippedThemes.Synthwave.Dim).WithLightness(SettingsMenu.EditLightnessStep).ToColor();
        Assert.Equal(Theme.ToHex(lighter), ColorIn(MineTheme(), "dim"));
        Assert.Equal(Theme.ToHex(ShippedThemes.Synthwave.Primary), ColorIn(MineTheme(), "primary"));
        var root = JsonNode.Parse(File.ReadAllText(MineTheme()))!.AsObject();
        Assert.Equal(1, (int)root["extra"]!);
        Assert.Equal("my own", (string?)root["description"]);
        Assert.Contains("  · " + BeforePath(ThemeText.EditSaved("mine", MineTheme()), MineTheme()), output);
        Assert.Equal(lighter, Theme.Current.Dim);                                       // put in force again, the edit with it
        Assert.Equal(2, _chat.Requests[1].Count(ConversationHistory.IsTurnStart));      // "hi" kept through the repaint
    }

    [Fact]
    public async Task ThemeEdit_ABuiltIn_OffersItsCopy_SavesTheCopyOnly_ThenOffersThatCopy()
    {
        using var theme = new ThemeScope();
        EditorPane();
        _settings.Update(d => { d.TtsOutput = false; d.Theme = "synthwave"; });
        PushLine("/theme edit noir");
        PushKeys(Keys.Char('y'));
        PushKeys(Keys.Enter);                    // yes: noir-custom made
        PushKeys(Keys.Down, DimRow);
        PushKeys(Keys.Left);                     // dim 4% darker
        PushKeys(Keys.Char('s'));
        PushLine("/theme edit noir");
        PushKeys(Keys.Char('n'));
        PushKeys(Keys.Enter);                    // no: the copy is left
        PushLine("/exit");

        string output = await RunAsync();

        string copy = Path.Combine(_settings.ThemesDirectory, "noir-custom.json");
        var darker = HslColor.From(ShippedThemes.Noir.Dim).WithLightness(-SettingsMenu.EditLightnessStep).ToColor();
        Assert.Equal(Theme.ToHex(darker), ColorIn(copy, "dim"));
        Assert.Equal(Theme.ToHex(ShippedThemes.Noir.Primary), ColorIn(copy, "primary"));
        Assert.Contains("  · " + BeforePath(ThemeText.EditSavedNotInForce("noir-custom", copy), copy), output);
        Assert.Contains(ThemeText.EditCopyQuestion("noir", "noir-custom")[..40], output);
        Assert.Same(ShippedThemes.Synthwave, Theme.Current);                            // another theme saved: nothing put in force
        Assert.Single(Directory.GetFiles(_settings.ThemesDirectory));                  // one copy, never a second
    }

    [Fact]
    public async Task ThemeEdit_ThePickersHueBar_EnterUsesTheColor()
    {
        using var theme = new ThemeScope();
        EditorPane();
        WriteMine();
        _settings.Update(d => { d.TtsOutput = false; d.Theme = "synthwave"; });
        PushLine("/theme edit mine");
        PushKeys(Keys.Enter);                    // primary's picker, on the hue bar
        PushKeys(Keys.Right, 3);                 // +6°
        PushKeys(Keys.Enter);                    // use it
        PushKeys(Keys.Char('s'));
        PushLine("/exit");

        string output = await RunAsync();

        var moved = SettingsMenu.Step(HslColor.From(ShippedThemes.Synthwave.Primary), 0, 3).ToColor();
        Assert.Equal(Theme.ToHex(moved), ColorIn(MineTheme(), "primary"));
        Assert.Contains(ThemeText.PickerHue, output);
        Assert.Contains("  · " + BeforePath(ThemeText.EditSavedNotInForce("mine", MineTheme()), MineTheme()), output);
    }

    [Fact]
    public async Task ThemeEdit_ThePickersHexRow_RefusesABadValue_AndTakesAShortOne()
    {
        using var theme = new ThemeScope();
        EditorPane();
        WriteMine();
        _settings.Update(d => { d.TtsOutput = false; d.Theme = "synthwave"; });
        PushLine("/theme edit mine");
        PushKeys(Keys.Enter);                    // primary's picker
        PushKeys(Keys.Down, 3);                  // the hex row
        PushKeys(Keys.Enter);
        PushKeys(Keys.Backspace, 7);
        PushLine("#zz");                         // refused, the picker stays
        PushKeys(Keys.Enter);
        PushKeys(Keys.Backspace, 7);
        PushLine("#123");                        // taken: straight back to the editor
        PushKeys(Keys.Char('s'));
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal("#112233", ColorIn(MineTheme(), "primary"));
        Assert.Contains(ThemeText.PickerBadHex("#zz"), output);
    }

    [Fact]
    public async Task ThemeEdit_Reset_PutsTheRowBack_SaveSaysNoChanges_EscWithChangesAsks_YesDiscards()
    {
        using var theme = new ThemeScope();
        EditorPane();
        string json = WriteMine();
        _settings.Update(d => { d.TtsOutput = false; d.Theme = "synthwave"; });
        PushLine("/theme edit mine");
        PushKeys(Keys.Down, DimRow);
        PushKeys(Keys.Right);
        PushKeys(Keys.Char('r'));                // the row back to the file's value
        PushKeys(Keys.Char('s'));                // nothing to save
        PushKeys(Keys.Right);
        PushKeys(Keys.Escape);                   // a change: asks
        PushKeys(Keys.Char('n'));
        PushKeys(Keys.Enter);                    // no: the editor again
        PushKeys(Keys.Escape);
        PushKeys(Keys.Char('y'));
        PushKeys(Keys.Enter);                    // yes: lost
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal(json, File.ReadAllText(MineTheme()));
        Assert.Contains(ThemeText.EditUnchanged, output);
        Assert.Contains(ThemeText.EditDiscardQuestion, output);
    }

    [Fact]
    public async Task ThemeEdit_ThePairsUnderTheirBars_AreWarnings()
    {
        using var theme = new ThemeScope();
        EditorPane();
        WriteUserTheme("dull.json", ThemeJson.File("\"name\": \"dull\"", ("dimmer", Theme.ToHex(ShippedThemes.Synthwave.Bg))));
        _settings.Update(d => { d.TtsOutput = false; d.Theme = "synthwave"; });
        PushLine("/theme edit dull");
        PushKeys(Keys.Escape);                   // no change: closes without asking
        PushLine("/theme edit synthwave-custom");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("⚠ dimmer on bg 1.0:1, wants 2.5", output);
        Assert.DoesNotContain(ThemeText.EditDiscardQuestion, output);
        Assert.Contains("  ✗ " + SettingsMenu.ThemeNameError("synthwave-custom", ThemeCatalog.Scan(_settings.ThemesDirectory).Names)[..40], output);
    }

    /// <summary>
    /// The editor's lines come after the repaint (2026-11-17, the admin-themes-render review's #18, #34): the save's line follows the theme's
    /// own saved line, so the redraw never wipes it.
    /// </summary>
    [Fact]
    public async Task ThemeEdit_TheSavesLine_ComesAfterTheRepaint()
    {
        using var theme = new ThemeScope();
        EditorPane();
        WriteMine();
        _settings.Update(d => { d.TtsOutput = false; d.Theme = "mine"; });
        ThemeName.Apply(_settings.Current, _settings.ThemesDirectory);
        StepsWhenIdle(Line("/theme edit"), Keys2([.. Enumerable.Repeat(Keys.Down, DimRow), Keys.Right, Keys.Char('s')]), Line("/exit"));

        string output = await RunAsync();

        string saved = BeforePath(ThemeText.EditSaved("mine", MineTheme()), MineTheme());
        string themeLine = SettingsMenu.SavedNotice(SettingsField.Theme, _settings.Current, _settings.ProfileDirectory);
        Assert.True(output.LastIndexOf(saved, StringComparison.Ordinal) > output.LastIndexOf(themeLine, StringComparison.Ordinal), output);
    }

    /// <summary>
    /// A built-in's copy says what was copied, not "edit it, then /theme" (#194), keeps its role words (#98), and a no to the copy says
    /// unchanged (#195); d with a change asks (#45).
    /// </summary>
    [Fact]
    public async Task ThemeEdit_ABuiltInsCopy_SaysCopied_KeepsItsWords_AndDAsks()
    {
        using var theme = new ThemeScope();
        EditorPane();
        _settings.Update(d => { d.TtsOutput = false; d.Theme = "synthwave"; });
        PushLine("/theme edit bauhaus");
        PushKeys(Keys.Char('y'));
        PushKeys(Keys.Enter);                    // yes: bauhaus-custom made
        PushKeys(Keys.Right);                    // primary lighter
        PushKeys(Keys.Char('d'));                // a change: asks
        PushKeys(Keys.Char('n'));
        PushKeys(Keys.Enter);                    // no: the editor again
        PushKeys(Keys.Char('d'));
        PushKeys(Keys.Char('y'));
        PushKeys(Keys.Enter);                    // yes: dropped
        PushLine("/theme edit noir");
        PushKeys(Keys.Char('n'));
        PushKeys(Keys.Enter);                    // no copy
        PushLine("/exit");

        string output = await RunAsync();

        string copy = Path.Combine(_settings.ThemesDirectory, "bauhaus-custom.json");
        Assert.Contains("\"bg\": \"secondary\"", File.ReadAllText(copy), StringComparison.Ordinal);
        Assert.Contains("Copied bauhaus to bauhaus-custom", output);   // its path wraps to a line of its own
        Assert.DoesNotContain(ThemeText.ExportDone("bauhaus-custom", copy)[..30], output);
        Assert.Contains(ThemeText.EditDiscardQuestion, output);
        Assert.Equal(Theme.ToHex(ThemeLibrary.Get("bauhaus").Primary), ColorIn(copy, "primary"));
        Assert.False(File.Exists(Path.Combine(_settings.ThemesDirectory, "noir-custom.json")));
        Assert.Contains("  · " + SettingsMenu.UnchangedNotice, output);
    }

    /// <summary>A name not found is the one tried: the theme in force when none is given (#93).</summary>
    [Fact]
    public async Task ThemeEdit_AMissedName_NamesTheOneTried()
    {
        using var theme = new ThemeScope();
        EditorPane();
        _settings.Update(d => { d.TtsOutput = false; d.Theme = "synthwave"; });
        PushLine("/theme edit nope");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("No theme named \"nope\"", output);
        Assert.DoesNotContain("No theme named \"\"", output);
    }

    /// <summary>A file deleted while the editor was open is offered back (#214): yes writes the opened text with the new color.</summary>
    [Fact]
    public async Task ThemeEdit_AFileGoneAtSave_WritesTheOpenedTextWithTheColors()
    {
        using var theme = new ThemeScope();
        EditorPane();
        string json = WriteMine();
        _settings.Update(d => { d.TtsOutput = false; d.Theme = "synthwave"; });
        StepsWhenIdle(
            Line("/theme edit mine"),
            input => { File.Delete(MineTheme()); input.Push(Keys.Right, Keys.Char('s')); },
            Keys2(Keys.Char('y'), Keys.Enter),
            Line("/exit"));

        string output = await RunAsync();

        var lighter = HslColor.From(ShippedThemes.Synthwave.Primary).WithLightness(SettingsMenu.EditLightnessStep).ToColor();
        Assert.Equal(json.Replace("\"primary\": \"" + Theme.ToHex(ShippedThemes.Synthwave.Primary) + "\"", "\"primary\": \"" + Theme.ToHex(lighter) + "\"", StringComparison.Ordinal),
            File.ReadAllText(MineTheme()));
    }
}
