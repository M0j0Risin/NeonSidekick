using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/theme delete &lt;name&gt;</c> (2026-11-20, the user's ask, plan swirling-forging-peach #9) on <see cref="SettingsMenu.DeleteThemeAsync"/>:
/// one of the user's themes after a yes, to the bin or for good as the probe says, a refusal for a built-in, an unknown name or a bad
/// count of words, and the theme in force replaced by the built-in its file overrode or by the default.
/// </summary>
public partial class SettingsMenuTests
{
    private string UserTheme(string name, string? file = null)
    {
        Directory.CreateDirectory(_settings.ThemesDirectory);
        string path = Path.Combine(_settings.ThemesDirectory, (file ?? name) + ".json");
        File.WriteAllText(path, ThemeJson.File("\"name\": \"" + name + "\""));
        return path;
    }

    /// <summary>A confirm that records each question and answers <paramref name="yes"/>.</summary>
    private static Func<string, CancellationToken, Task<bool>> Answer(bool yes, List<string> asked) =>
        (question, _) =>
        {
            asked.Add(question);
            return Task.FromResult(yes);
        };

    private string Held()
    {
        _menu.PrintHeld();
        return _console.Output;
    }

    [Fact]
    public async Task ThemeDelete_AfterAYes_SendsTheFileToTheBin_AndTheNextScanLacksIt()
    {
        using var theme = new ThemeScope();
        string path = UserTheme("mine");
        var bin = new FakeRecycleBin();
        var asked = new List<string>();

        Assert.False(await _menu.DeleteThemeAsync(" mine", Answer(true, asked), bin, CancellationToken.None));

        Assert.Equal([ThemeText.DeleteQuestion("mine", Path.Combine("themes", "mine.json"), toBin: true, revealsBuiltIn: false)], asked);
        Assert.Equal([_settings.ThemesDirectory], bin.Probed);   // where it goes, judged before the question
        Assert.Equal([path], bin.Moved);
        Assert.False(File.Exists(path));
        Assert.DoesNotContain("mine", ThemeCatalog.Scan(_settings.ThemesDirectory).Names);
        Assert.Contains(ThemeText.DeleteDone("mine", toBin: true), Held());
        Assert.Same(ShippedThemes.Synthwave, Theme.Current);   // not the theme in force: nothing else changes
    }

    [Fact]
    public async Task ThemeDelete_WithNoBin_OrADriveWithNone_IsForGood_AndSaysSo()
    {
        using var theme = new ThemeScope();
        string first = UserTheme("first");
        string second = UserTheme("second");
        var asked = new List<string>();

        Assert.False(await _menu.DeleteThemeAsync("first", Answer(true, asked), bin: null, CancellationToken.None));
        var noBin = new FakeRecycleBin { ProbeAnswer = new RecycleResult(RecycleOutcome.NoBin) };
        Assert.False(await _menu.DeleteThemeAsync("second", Answer(true, asked), noBin, CancellationToken.None));

        Assert.Equal(
            [ThemeText.DeleteQuestion("first", Path.Combine("themes", "first.json"), toBin: false, revealsBuiltIn: false),
             ThemeText.DeleteQuestion("second", Path.Combine("themes", "second.json"), toBin: false, revealsBuiltIn: false)],
            asked);
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
        Assert.Empty(noBin.Moved);
        string output = Held();
        Assert.Contains(ThemeText.DeleteDone("first", toBin: false), output);
        Assert.Contains(ThemeText.DeleteDone("second", toBin: false), output);
    }

    [Fact]
    public async Task ThemeDelete_ANo_OrABinThatRefuses_KeepsTheFile()
    {
        using var theme = new ThemeScope();
        string path = UserTheme("mine");
        var asked = new List<string>();
        var bin = new FakeRecycleBin();

        Assert.False(await _menu.DeleteThemeAsync("mine", Answer(false, asked), bin, CancellationToken.None));
        Assert.Contains(ChatScreen.KeptNotice, Held());
        Assert.Empty(bin.Moved);

        var refuses = new FakeRecycleBin { Answer = new RecycleResult(RecycleOutcome.Failed, "it is busy") };
        Assert.False(await _menu.DeleteThemeAsync("mine", Answer(true, asked), refuses, CancellationToken.None));
        Assert.Contains(ThemeText.DeleteFailed("it is busy"), Held());

        var never = new FakeRecycleBin { ProbeAnswer = new RecycleResult(RecycleOutcome.Failed, "paths too long") };
        Assert.False(await _menu.DeleteThemeAsync("mine", Answer(true, asked), never, CancellationToken.None));
        Assert.Contains(ThemeText.DeleteFailed("paths too long"), Held());
        Assert.Empty(never.Moved);

        Assert.Equal(2, asked.Count);   // the probe's refusal asks nothing
        Assert.True(File.Exists(path));   // never deleted directly behind a refusal
    }

    [Theory]
    [InlineData("synthwave", ThemeText.DeleteBuiltIn)]
    [InlineData("", ThemeText.DeleteUsage)]
    [InlineData("   ", ThemeText.DeleteUsage)]
    [InlineData(" mine other", ThemeText.DeleteUsage)]
    public async Task ThemeDelete_ABuiltIn_OrNoName_OrTwo_IsRefused_NothingAsked(string rest, string error)
    {
        using var theme = new ThemeScope();
        string path = UserTheme("mine");
        var asked = new List<string>();

        Assert.False(await _menu.DeleteThemeAsync(rest, Answer(true, asked), new FakeRecycleBin(), CancellationToken.None));

        Assert.Contains(error, Held());
        Assert.Empty(asked);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task ThemeDelete_AnUnknownName_GetsTheNameError()
    {
        var asked = new List<string>();

        Assert.False(await _menu.DeleteThemeAsync("nope", Answer(true, asked), null, CancellationToken.None));

        Assert.Contains(SettingsMenu.ThemeNameError("nope", ThemeCatalog.Scan(_settings.ThemesDirectory).Names)[..20], Held());
        Assert.Empty(asked);
    }

    [Fact]
    public async Task ThemeDelete_TheThemeInForce_PutsTheDefaultInForce_AndSavesIt()
    {
        using var theme = new ThemeScope();
        UserTheme("mine");
        _settings.Update(d => d.Theme = "mine");
        ThemeName.Apply(_settings.Current, _settings.ThemesDirectory);
        Assert.Equal("mine", Theme.Current.Name);

        Assert.True(await _menu.DeleteThemeAsync("MINE", Answer(true, []), null, CancellationToken.None));

        Assert.Equal(ThemeLibrary.Default.Name, _settings.Current.Theme);
        Assert.Equal(ShippedThemes.Synthwave, Theme.Current);
        Assert.Contains(ThemeText.DeleteInForce(ThemeLibrary.Default.Name), Held());
    }

    [Fact]
    public async Task ThemeDelete_AFileThatReplacesABuiltIn_SaysItComesBack_AndPutsItInForce()
    {
        using var theme = new ThemeScope();
        UserTheme("noir");
        _settings.Update(d => d.Theme = "noir");
        ThemeName.Apply(_settings.Current, _settings.ThemesDirectory);
        Assert.False(Theme.Current.IsBuiltIn);
        var asked = new List<string>();

        Assert.True(await _menu.DeleteThemeAsync("noir", Answer(true, asked), null, CancellationToken.None));

        Assert.Equal([ThemeText.DeleteQuestion("noir", Path.Combine("themes", "noir.json"), toBin: false, revealsBuiltIn: true)], asked);
        Assert.EndsWith("; the built-in noir comes back?", asked[0], StringComparison.Ordinal);
        Assert.Equal("noir", _settings.Current.Theme);
        Assert.Equal(ShippedThemes.Noir, Theme.Current);
        Assert.Contains(ThemeText.DeleteInForce("noir"), Held());
    }

    /// <summary>A typed delete of an in-force file named like the default (#27): the fallback put in force quietly, no "already" line.</summary>
    [Fact]
    public async Task ThemeDelete_AnInForceFileNamedSynthwave_SaysNoAlready()
    {
        using var theme = new ThemeScope();
        UserTheme("synthwave");
        _settings.Update(d => d.Theme = "synthwave");
        ThemeName.Apply(_settings.Current, _settings.ThemesDirectory);
        Assert.False(Theme.Current.IsBuiltIn);

        await _menu.DeleteThemeAsync("synthwave", Answer(true, []), null, CancellationToken.None);

        string held = Held();
        Assert.Contains(ThemeText.DeleteDone("synthwave", toBin: false), held);
        Assert.Contains(ThemeText.DeleteInForce("synthwave"), held);
        Assert.DoesNotContain(SettingsMenu.ThemeAlreadyNotice("synthwave"), held);
        Assert.Same(ShippedThemes.Synthwave, Theme.Current);
    }

    /// <summary>
    /// The <c>/theme</c> list cancelled after a Del took the theme in force (2026-11-20, #68): a cancel reads as Esc, so the fallback is in
    /// force, the list says it changed, a repaint is owed, and no "unchanged" line follows.
    /// </summary>
    [Fact]
    public async Task ThemeList_CancelledAfterAnInForceDelete_PutsTheFallbackInForce()
    {
        using var theme = new ThemeScope();
        UserTheme("zz1");
        _settings.Update(d => d.Theme = "zz1");
        ThemeName.Apply(_settings.Current, _settings.ThemesDirectory);
        var (menu, _) = PaneMenu();
        using var cancel = new CancellationTokenSource();
        menu.ThemeBin = () => new FakeRecycleBin { AnswerFor = _ => { cancel.Cancel(); return new RecycleResult(RecycleOutcome.Recycled); } };
        Push(Keys.Delete, Keys.Down, Keys.Enter);

        Assert.True(await menu.PickThemeAsync("", cancel.Token));

        Assert.Equal("synthwave", _settings.Current.Theme);
        Assert.Equal(ShippedThemes.Synthwave, Theme.Current);
        Assert.True(menu.TakeRepaintOwed());
        menu.PrintHeld();
        Assert.Contains(ThemeText.DeleteInForce("synthwave"), _console.Output);
        Assert.DoesNotContain(SettingsMenu.UnchangedNotice, _console.Output);
    }

    /// <summary>
    /// A throw out of the <c>/theme</c> list after a Del took the theme in force (#68): the list's finally still puts the fallback in force and
    /// saves it, so profile.json never names the deleted theme, and the screen is told a repaint is owed (taken once).
    /// </summary>
    [Fact]
    public async Task ThemeList_AThrowAfterAnInForceDelete_StillPutsTheFallbackInForce()
    {
        using var theme = new ThemeScope();
        UserTheme("zz1");
        UserTheme("zz2");
        _settings.Update(d => d.Theme = "zz1");
        ThemeName.Apply(_settings.Current, _settings.ThemesDirectory);
        var (menu, _) = PaneMenu();
        int moves = 0;
        var bin = new FakeRecycleBin { AnswerFor = _ => moves++ == 0 ? new RecycleResult(RecycleOutcome.Recycled) : throw new InvalidOperationException("bin broke") };
        menu.ThemeBin = () => bin;
        // zz1 (in force) deleted, the cursor on the fallback; z jumps to zz2, the last row; its delete throws.
        Push(Keys.Delete, Keys.Down, Keys.Enter, Keys.Char('z'), Keys.Delete, Keys.Down, Keys.Enter);

        await Assert.ThrowsAsync<InvalidOperationException>(() => menu.PickThemeAsync("", CancellationToken.None));

        Assert.Equal(2, bin.Moved.Count);
        Assert.Equal("synthwave", _settings.Current.Theme);
        Assert.Equal(ShippedThemes.Synthwave, Theme.Current);
        Assert.True(menu.TakeRepaintOwed());
        Assert.False(menu.TakeRepaintOwed());
    }

    [Fact]
    public async Task ThemeDelete_AThemeNamedDelete_StillLoads_AndOnlyTheFirstWordIsTheVerb()
    {
        using var theme = new ThemeScope();
        string path = UserTheme("delete");
        Assert.Contains("delete", ThemeCatalog.Scan(_settings.ThemesDirectory).Names);   // loadable, as "edit" is
        Assert.False(ThemeFile.IsValidNewName("delete"));                                  // but no new theme takes it

        Assert.Equal(ThemeText.DeleteWord, ThemeText.Subcommand("delete delete", out string rest));
        Assert.False(await _menu.DeleteThemeAsync(rest, Answer(true, []), null, CancellationToken.None));

        Assert.False(File.Exists(path));
    }
}
