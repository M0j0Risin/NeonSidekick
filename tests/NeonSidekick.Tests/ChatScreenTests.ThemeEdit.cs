using System.Text.Json.Nodes;
using NeonSidekick.App;
using NeonSidekick.Files;
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
    public async Task ThemeEdit_Reset_PutsTheRowBack_SaveSaysNoChanges_EscWithChangesAsks_DiscardDiscards()
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
        PushKeys(Keys.Enter);                    // Keep editing: the editor again
        PushKeys(Keys.Escape);
        PushKeys(Keys.Escape);                   // Esc on the question keeps editing too
        PushKeys(Keys.Escape);
        PushKeys(Keys.Down, 2);
        PushKeys(Keys.Enter);                    // Discard: lost
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal(json, File.ReadAllText(MineTheme()));
        Assert.Contains(ThemeText.EditUnchanged, output);
        Assert.Contains(SettingsMenu.LeaveQuestion("mine"), output);
    }

    [Fact]
    public async Task ThemeEdit_ThePairsUnderTheirBars_AreWarnings()
    {
        using var theme = new ThemeScope();
        EditorPane();
        WriteUserTheme("dull.json", ThemeJson.File("\"name\": \"dull\"", ("dimmer", Theme.ToHex(ShippedThemes.Synthwave.Bg))));
        _settings.Update(d => { d.TtsOutput = false; d.Theme = "synthwave"; });
        PushLine("/theme edit dull");
        for (int i = 0; i < (int)ThemeColorSlot.Dimmer; i++)
        {
            PushKeys(Keys.Down);                 // the band under the list is the cursor's role's since 2026-11-24: dimmer's row
        }

        PushKeys(Keys.Escape);                   // no change: closes without asking
        PushLine("/theme edit synthwave-custom");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("⚠  dimmer on bg 1.0:1 (wants 2.5)", output);
        Assert.DoesNotContain(SettingsMenu.LeaveQuestion("dull"), output);
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
        PushKeys(Keys.Enter);                    // Keep editing: the editor again
        PushKeys(Keys.Char('d'));
        PushKeys(Keys.Down, 2);
        PushKeys(Keys.Enter);                    // Discard: dropped
        PushLine("/theme edit noir");
        PushKeys(Keys.Char('n'));
        PushKeys(Keys.Enter);                    // no copy
        PushLine("/exit");

        string output = await RunAsync();

        string copy = Path.Combine(_settings.ThemesDirectory, "bauhaus-custom.json");
        Assert.Contains("\"bg\": \"secondary\"", File.ReadAllText(copy), StringComparison.Ordinal);
        Assert.Contains("Copied bauhaus to bauhaus-custom", output);   // its path wraps to a line of its own
        Assert.DoesNotContain(ThemeText.ExportDone("bauhaus-custom", copy)[..30], output);
        Assert.Contains(SettingsMenu.LeaveQuestion(ThemeText.CopyName("bauhaus")), output);
        Assert.Equal(Theme.ToHex(ThemeLibrary.Get("bauhaus").Primary), ColorIn(copy, "primary"));
        Assert.False(File.Exists(Path.Combine(_settings.ThemesDirectory, "noir-custom.json")));
        Assert.Contains("  · " + SettingsMenu.UnchangedNotice, output);
    }

    /// <summary>Esc with changes, then Save on the question (2026-11-26, the user's ask): written as <c>s</c> writes it.</summary>
    [Fact]
    public async Task ThemeEdit_EscWithChanges_SaveWrites()
    {
        using var theme = new ThemeScope();
        EditorPane();
        WriteMine();
        _settings.Update(d => { d.TtsOutput = false; d.Theme = "synthwave"; });
        PushLine("/theme edit mine");
        PushKeys(Keys.Down, DimRow);
        PushKeys(Keys.Right);                    // dim 4% lighter
        PushKeys(Keys.Escape);                   // asks
        PushKeys(Keys.Down);
        PushKeys(Keys.Enter);                    // Save
        PushLine("/exit");

        string output = await RunAsync();

        var lighter = HslColor.From(ShippedThemes.Synthwave.Dim).WithLightness(SettingsMenu.EditLightnessStep).ToColor();
        Assert.Equal(Theme.ToHex(lighter), ColorIn(MineTheme(), "dim"));
        Assert.Contains(SettingsMenu.LeaveQuestion("mine"), output);
        Assert.Contains("  · " + BeforePath(ThemeText.EditSavedNotInForce("mine", MineTheme()), MineTheme()), output);
    }

    /// <summary><c>d</c> with changes, Keep editing, then <c>s</c> (2026-11-26): the editor kept the changes through the question.</summary>
    [Fact]
    public async Task ThemeEdit_DWithChanges_KeepEditing_ThenSSaves()
    {
        using var theme = new ThemeScope();
        EditorPane();
        WriteMine();
        _settings.Update(d => { d.TtsOutput = false; d.Theme = "synthwave"; });
        PushLine("/theme edit mine");
        PushKeys(Keys.Down, DimRow);
        PushKeys(Keys.Left);                     // dim 4% darker
        PushKeys(Keys.Char('d'));                // asks
        PushKeys(Keys.Enter);                    // Keep editing
        PushKeys(Keys.Char('s'));
        PushLine("/exit");

        string output = await RunAsync();

        var darker = HslColor.From(ShippedThemes.Synthwave.Dim).WithLightness(-SettingsMenu.EditLightnessStep).ToColor();
        Assert.Equal(Theme.ToHex(darker), ColorIn(MineTheme(), "dim"));
        Assert.Contains(SettingsMenu.LeaveQuestion("mine"), output);
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

    // ── The /theme list's → edit and Del delete (2026-11-20, the user's ask, plan swirling-forging-peach #9) ─────────────

    /// <summary>A user theme of <paramref name="name"/>, synthwave's colors; its path.</summary>
    private string PlainTheme(string name)
    {
        WriteUserTheme(name + ".json", ThemeJson.File("\"name\": \"" + name + "\""));
        return Path.Combine(_settings.ThemesDirectory, name + ".json");
    }

    /// <summary>The theme of <paramref name="name"/> in force, as Program puts the saved one at the start.</summary>
    private void InForce(string name)
    {
        _settings.Update(d => { d.TtsOutput = false; d.Theme = name; d.FileDeletesToRecycleBin = true; });
        ThemeName.Apply(_settings.Current, _settings.ThemesDirectory);
    }

    [Fact]
    public async Task ThemeList_Right_OnYourTheme_OpensTheEditorOnIt_AndASaveRepaints()
    {
        using var theme = new ThemeScope();
        EditorPane();
        WriteMine();
        InForce("mine");
        var down = Enumerable.Repeat(Keys.Down, DimRow);
        StepsWhenIdle(Line("/theme"), Keys2([Keys.Right, .. down, Keys.Right, Keys.Char('s'), Keys.Escape]), Line("/exit"));   // Esc: the list came back

        string output = await RunAsync();

        var lighter = HslColor.From(ShippedThemes.Synthwave.Dim).WithLightness(SettingsMenu.EditLightnessStep).ToColor();
        Assert.Equal(Theme.ToHex(lighter), ColorIn(MineTheme(), "dim"));
        Assert.Contains("  · " + BeforePath(ThemeText.EditSaved("mine", MineTheme()), MineTheme()), output);
        Assert.Equal(lighter, Theme.Current.Dim);
        Assert.Contains(SettingsMenu.ThemeKeepKeys, output);
        Assert.Contains(ThemeText.EditWord + " · " + ThemeText.DeleteWord, output);   // the title row's buttons
        Assert.DoesNotContain("  · " + SettingsMenu.UnchangedNotice, output);
    }

    [Fact]
    public async Task ThemeList_Right_OnABuiltIn_AsksForItsCopy_ThenEditsThat()
    {
        using var theme = new ThemeScope();
        EditorPane();
        InForce("synthwave");
        StepsWhenIdle(Line("/theme"), Keys2(Keys.Right, Keys.Char('y'), Keys.Enter, Keys.Escape, Keys.Escape), Line("/exit"));   // the editor's Esc, then the list's

        string output = await RunAsync();

        Assert.Contains(ThemeText.EditBuiltInQuestion("synthwave-custom"), output);
        Assert.True(File.Exists(Path.Combine(_settings.ThemesDirectory, "synthwave-custom.json")));
        Assert.Same(ShippedThemes.Synthwave, Theme.Current);
    }

    [Fact]
    public async Task ThemeList_Del_OnYourTheme_AsksOnThePane_SendsItToTheBin_AndTheListStaysOpen()
    {
        using var theme = new ThemeScope();
        EditorPane();
        string mine = PlainTheme("mine");
        PlainTheme("mine2");
        InForce("mine2");
        var bin = new FakeRecycleBin();
        _recycleBin = bin;
        // Up from mine2 to mine, Del, Yes; then Del on the row that took its place (mine2, in force): No; Esc.
        StepsWhenIdle(Line("/theme"), Keys2(Keys.Up, Keys.Delete, Keys.Down, Keys.Enter), Keys2(Keys.Delete, Keys.Enter, Keys.Escape), Line("/exit"));

        string output = await RunAsync();

        Assert.Equal([mine], bin.Moved);
        Assert.False(File.Exists(mine));
        Assert.Contains(ThemeText.DeleteQuestion("mine", Path.Combine("themes", "mine.json"), toBin: true, revealsBuiltIn: false).Split('\n')[0], output);
        Assert.Contains("  · " + ThemeText.DeleteDone("mine", toBin: true), output);   // held: the transcript keeps a trace
        Assert.Contains(ThemeText.DeleteQuestion("mine2", Path.Combine("themes", "mine2.json"), toBin: true, revealsBuiltIn: false).Split('\n')[0], output);
        Assert.True(File.Exists(Path.Combine(_settings.ThemesDirectory, "mine2.json")));   // the no kept it
        Assert.DoesNotContain("  · " + SettingsMenu.UnchangedNotice, output);            // a delete happened: no "unchanged" after it
        Assert.Equal("mine2", Theme.Current.Name);
    }

    [Fact]
    public async Task ThemeList_Del_OnABuiltIn_SaysWhy_NothingAsked()
    {
        using var theme = new ThemeScope();
        EditorPane();
        InForce("synthwave");
        StepsWhenIdle(Line("/theme"), Keys2(Keys.Delete, Keys.Escape), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(ThemeText.DeleteBuiltInHere, output);   // on the status line, whole beside the preview
        Assert.Contains("  · " + SettingsMenu.UnchangedNotice, output);
        Assert.Same(ShippedThemes.Synthwave, Theme.Current);
    }

    [Fact]
    public async Task ThemeList_Del_OnTheThemeInForce_PutsTheDefaultInForce()
    {
        using var theme = new ThemeScope();
        EditorPane();
        string mine = PlainTheme("mine");
        InForce("mine");
        var bin = new FakeRecycleBin();
        _recycleBin = bin;
        StepsWhenIdle(Line("/theme"), Keys2(Keys.Delete, Keys.Down, Keys.Enter, Keys.Escape), Line("/exit"));

        string output = await RunAsync();

        Assert.Equal([mine], bin.Moved);
        Assert.Contains("  · " + ThemeText.DeleteInForce("synthwave"), output);
        Assert.Equal("synthwave", _settings.Current.Theme);
        Assert.Equal(ShippedThemes.Synthwave, Theme.Current);

        // The fallback waits for the list to close (2026-11-20, the memory-remember review's #4, #49): said on the status line before the
        // repaint's saved line, and put in force quietly (#27).
        int fallsBack = output.IndexOf(ThemeText.DeleteFallsBack("synthwave"), StringComparison.Ordinal);
        Assert.InRange(fallsBack, 0, output.IndexOf(SettingsMenu.SavedNotice(SettingsField.Theme, _settings.Current, _settings.ProfileDirectory), StringComparison.Ordinal));
        Assert.DoesNotContain(SettingsMenu.ThemeAlreadyNotice("synthwave"), output);
        Assert.DoesNotContain("  · " + SettingsMenu.UnchangedNotice, output);
    }

    /// <summary>Enter on the fallback after the theme in force went (#17, #70): the cursor is on it, and the pick is a change, no "already".</summary>
    [Fact]
    public async Task ThemeList_Del_OnTheThemeInForce_ThenEnter_PicksTheFallback_NoAlready()
    {
        using var theme = new ThemeScope();
        EditorPane();
        PlainTheme("mine");
        InForce("mine");
        _recycleBin = new FakeRecycleBin();
        StepsWhenIdle(Line("/theme"), Keys2(Keys.Delete, Keys.Down, Keys.Enter, Keys.Enter), Line("/exit"));

        string output = await RunAsync();

        Assert.Equal("synthwave", _settings.Current.Theme);
        Assert.Equal(ShippedThemes.Synthwave, Theme.Current);
        Assert.DoesNotContain(SettingsMenu.ThemeAlreadyNotice("synthwave"), output);
        Assert.Contains(SettingsMenu.SavedNotice(SettingsField.Theme, _settings.Current, _settings.ProfileDirectory), output);   // repainted
    }

    /// <summary>→, Esc in the editor, then Enter on another theme (#3, #72): no "unchanged" from the visit, only the pick.</summary>
    [Fact]
    public async Task ThemeList_Right_ThenEscInTheEditor_ThenEnterOnAnother_SaysNoUnchanged()
    {
        using var theme = new ThemeScope();
        EditorPane();
        WriteMine();
        InForce("mine");
        StepsWhenIdle(Line("/theme"), Keys2(Keys.Right, Keys.Escape, Keys.Up, Keys.Enter), Line("/exit"));

        string output = await RunAsync();

        Assert.NotEqual("mine", Theme.Current.Name);
        Assert.NotEqual("mine", _settings.Current.Theme);
        Assert.DoesNotContain("  · " + SettingsMenu.UnchangedNotice, output);
    }

    /// <summary>→, Esc in the editor, Esc on the list: one "unchanged", the list's.</summary>
    [Fact]
    public async Task ThemeList_Right_ThenEscTwice_SaysUnchangedOnce()
    {
        using var theme = new ThemeScope();
        EditorPane();
        WriteMine();
        InForce("mine");
        StepsWhenIdle(Line("/theme"), Keys2(Keys.Right, Keys.Escape, Keys.Escape), Line("/exit"));

        string output = await RunAsync();

        Assert.Equal(1, Count(output, "  · " + SettingsMenu.UnchangedNotice));
        Assert.Equal("mine", Theme.Current.Name);
    }

    /// <summary>→ on a built-in, then No to the copy: the list is back on that built-in's row, so Enter picks it again.</summary>
    [Fact]
    public async Task ThemeList_Right_OnABuiltIn_ThenNo_ComesBackOnItsRow()
    {
        using var theme = new ThemeScope();
        EditorPane();
        InForce("synthwave");
        StepsWhenIdle(Line("/theme"), Keys2(Keys.Right, Keys.Enter, Keys.Enter), Line("/exit"));   // →, No (the first row), Enter on the list

        string output = await RunAsync();

        Assert.False(File.Exists(Path.Combine(_settings.ThemesDirectory, "synthwave-custom.json")));
        Assert.Contains(SettingsMenu.ThemeAlreadyNotice("synthwave"), output);   // the cursor stayed on synthwave
        Assert.DoesNotContain("  · " + SettingsMenu.UnchangedNotice, output);    // the No holds nothing in the list
    }

    /// <summary>→ on a built-in, yes, Esc in the editor: the copy is said, the cursor on its row, and no "unchanged" follows.</summary>
    [Fact]
    public async Task ThemeList_Right_OnABuiltIn_MakesTheCopy_AndTheCursorLandsOnIt()
    {
        using var theme = new ThemeScope();
        EditorPane();
        InForce("synthwave");
        StepsWhenIdle(Line("/theme"), Keys2(Keys.Right, Keys.Char('y'), Keys.Enter, Keys.Escape, Keys.Enter), Line("/exit"));   // y moves to Yes

        string output = await RunAsync();

        string copy = Path.Combine(_settings.ThemesDirectory, "synthwave-custom.json");
        Assert.True(File.Exists(copy));
        Assert.Contains("  · " + BeforePath(ThemeText.EditCopied("synthwave", "synthwave-custom", copy), copy).TrimEnd('(', ' '), output);   // "(path" wraps whole
        Assert.Equal("synthwave-custom", _settings.Current.Theme);   // Enter landed on the copy's row
        Assert.DoesNotContain("  · " + SettingsMenu.UnchangedNotice, output);
    }

    /// <summary>A row whose file went after the list was read (#38): nothing asked, the list read again, and the line says why.</summary>
    [Fact]
    public async Task ThemeList_Del_OnARowWhoseFileWent_SaysTheFolderChanged_NothingAsked()
    {
        using var theme = new ThemeScope();
        EditorPane();
        string mine = PlainTheme("mine");
        PlainTheme("mine2");
        InForce("mine2");
        var bin = new FakeRecycleBin();
        _recycleBin = bin;
        StepsWhenIdle(Line("/theme"), Keys2(Keys.Up), input =>
        {
            File.Delete(mine);
            input.Push(Keys.Delete, Keys.Escape);
        }, Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(ThemeText.ThemesChanged, output);
        Assert.DoesNotContain(ThemeText.DeleteQuestion("mine", Path.Combine("themes", "mine.json"), toBin: true, revealsBuiltIn: false).Split('\n')[0], output);
        Assert.Empty(bin.Moved);
        Assert.Equal("mine2", Theme.Current.Name);
    }

    /// <summary>A file that went while the question stood (#38): no done line, the bin never called, the folder's change said.</summary>
    [Fact]
    public async Task ThemeList_Del_AFileGoneWhileAsked_SaysTheFolderChanged_AndDeletesNothing()
    {
        using var theme = new ThemeScope();
        EditorPane();
        string mine = PlainTheme("mine");
        PlainTheme("mine2");
        InForce("mine2");
        var bin = new FakeRecycleBin { OnProbe = _ => File.Delete(mine) };   // the probe runs just before the question
        _recycleBin = bin;
        StepsWhenIdle(Line("/theme"), Keys2(Keys.Up, Keys.Delete, Keys.Down, Keys.Enter, Keys.Escape), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(ThemeText.ThemesChanged, output);
        Assert.DoesNotContain(ThemeText.DeleteDone("mine", toBin: true), output);
        Assert.Empty(bin.Moved);
    }

    /// <summary>A bin that refuses (#28): the file kept, the error on the status line and held, so the transcript keeps it and Esc says no "unchanged".</summary>
    [Fact]
    public async Task ThemeList_Del_ABinThatRefuses_KeepsTheFile_AndTheErrorIsHeld()
    {
        using var theme = new ThemeScope();
        EditorPane();
        string mine = PlainTheme("mine");
        PlainTheme("mine2");
        InForce("mine2");
        _recycleBin = new FakeRecycleBin { Answer = new RecycleResult(RecycleOutcome.Failed, "locked") };
        StepsWhenIdle(Line("/theme"), Keys2(Keys.Up, Keys.Delete, Keys.Down, Keys.Enter, Keys.Escape), Line("/exit"));

        string output = await RunAsync();

        Assert.True(File.Exists(mine));
        Assert.Contains(ThemeText.DeleteFailed("locked"), output);
        Assert.DoesNotContain("  · " + SettingsMenu.UnchangedNotice, output);
    }
}
