using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.Tests;

/// <summary>The user's themes, <c>&lt;home&gt;/themes/*.json</c> (2026-10-01): the file, the catalog, the export.</summary>
public sealed class ThemeFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"), ThemeCatalog.DirectoryName);

    public ThemeFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_dir)!, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void Write(string file, string json) => File.WriteAllText(Path.Combine(_dir, file), json);

    private ThemeScan Scan() => ThemeCatalog.Scan(_dir);

    private ThemePalette User(string name) => Scan().Themes.Single(t => t.Name == name);

    private IEnumerable<string> ProblemsOf(string file) =>
        Scan().Problems.Where(p => Path.GetFileName(p.FilePath) == file).Select(p => p.Problem);

    // ── The words ──────────────────────────────────────────────────────────

    [Fact]
    public void Keys_ArePinned_OnePerSlot()
    {
        Assert.Equal(
            ["primary", "secondary", "tertiary", "deep", "highlight", "warm", "tint", "ink", "dim", "dimmer", "bg", "panelBg", "good", "bad", "warn"],
            ThemeKeys.Colors);
        Assert.Equal(Enum.GetValues<ThemeColorSlot>().Length, ThemeKeys.Colors.Count);
        Assert.Equal(Enum.GetValues<ThemeStyleSlot>().Length, ThemeKeys.Styles.Count);
        Assert.Equal(ThemeKeys.Styles.Count, ThemeKeys.Styles.Distinct().Count());
        Assert.Equal("codeKeyword", ThemeKeys.Of(ThemeStyleSlot.CodeKeyword));
        Assert.Equal("border", ThemeKeys.Of(ThemeStyleSlot.Border));
        Assert.Equal("thinking", ThemeKeys.Of(ThemeStyleSlot.Thinking));
        Assert.Equal("panelBg", ThemeKeys.Of(ThemeColorSlot.PanelBg));
        Assert.True(ThemeKeys.TryParseStyle("CODEKEYWORD", out var style) && style == ThemeStyleSlot.CodeKeyword);
        Assert.True(ThemeKeys.TryParseColor(" PanelBg ", out var color) && color == ThemeColorSlot.PanelBg);
        Assert.False(ThemeKeys.TryParseColor("primay", out _));
        // Every alias names a slot ahead of it, so its source's final style is built first.
        Assert.All(Enum.GetValues<ThemeStyleSlot>(), slot => Assert.True(ThemeKeys.AliasOf(slot) is not { } source || source < slot, slot.ToString()));
    }

    [Theory]
    [InlineData("#FF79C6", 0xFF, 0x79, 0xC6)]
    [InlineData("ff79c6", 0xFF, 0x79, 0xC6)]
    [InlineData(" #abc ", 0xAA, 0xBB, 0xCC)]
    public void TryParseColor_TakesSixOrThreeHexDigits(string text, byte r, byte g, byte b)
    {
        Assert.True(ThemeFile.TryParseColor(text, out var color));
        Assert.Equal(new Color(r, g, b), color);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    [InlineData("red")]
    [InlineData("#+12345")]
    public void TryParseColor_RefusesTheRest(string? text) => Assert.False(ThemeFile.TryParseColor(text, out _));

    [Theory]
    [InlineData("dracula", true)]
    [InlineData("my-theme_2", true)]
    [InlineData("9lives", true)]
    [InlineData("-dash", false)]
    [InlineData("Upper", false)]
    [InlineData("two words", false)]
    [InlineData("export", false)]
    [InlineData("", false)]
    [InlineData("abcdefghijabcdefghijabcdefghijabc", false)]   // 33
    public void IsValidName(string name, bool valid) => Assert.Equal(valid, ThemeFile.IsValidName(name));

    // ── The catalog ────────────────────────────────────────────────────────

    [Fact]
    public void Scan_NoFolder_IsTheBuiltInsAlone()
    {
        var scan = ThemeCatalog.Scan(Path.Combine(_dir, "missing"));
        Assert.Same(ThemeCatalog.BuiltIn, scan);
        Assert.Equal(ThemeName.Names, scan.Names);
        Assert.Same(ThemeCatalog.BuiltIn, ThemeCatalog.Scan(null));
    }

    [Fact]
    public void Scan_TheBuiltInsFirst_ThenTheUsersByName()
    {
        Write("zeta.json", """{ "colors": { "primary": "#010203" } }""");
        Write("Alpha.json", """{ "name": "alpha", "description": "first of mine" }""");
        Write("notes.txt", "not a theme");

        var scan = Scan();

        Assert.Equal([.. ThemeName.Names, "alpha", "zeta"], scan.Names);
        Assert.Empty(scan.Problems);
        Assert.Equal("first of mine", User("alpha").Description);
        Assert.Equal(ThemeText.CustomDescription, User("zeta").Description);   // none given
        Assert.False(User("zeta").IsBuiltIn);
        Assert.Equal(Path.Combine(_dir, "zeta.json"), User("zeta").SourcePath);
        Assert.All(ThemePalette.All, p => Assert.True(p.IsBuiltIn));
    }

    [Fact]
    public void AFileWithNoBase_IsSynthwave_ButForWhatItSays()
    {
        Write("dracula.json", """
            {
              // comments and a trailing comma are fine
              "name": "Dracula",
              "colors": { "primary": "#FF79C6", "BG": "#282A36", },
            }
            """);

        var dracula = User("dracula");

        Assert.Equal(new Color(0xFF, 0x79, 0xC6), dracula.Primary);
        Assert.Equal(new Color(0x28, 0x2A, 0x36), dracula.Bg);
        Assert.Equal(ThemePalette.Synthwave.Secondary, dracula.Secondary);
        Assert.Equal(ThemePalette.Synthwave.Ink, dracula.Ink);
        // Synthwave's gradient is derived, so it is derived again from the new primary.
        Assert.Equal(ThemePalette.DerivedGradient(dracula.Primary, dracula.Secondary, dracula.Tertiary, dracula.Warm, dracula.Highlight), dracula.GradientStops);
        Assert.Null(dracula.Styles);
    }

    [Fact]
    public void ABase_GivesTheRest_ItsOwnGradientAndWarnKept_ADerivedWarnFollowingTheHighlight()
    {
        Write("greener.json", """{ "base": "netrunner", "colors": { "ink": "#FFFFFF", "primary": "#00FF00" } }""");
        Write("amber.json", """{ "colors": { "highlight": "#FFAA00" } }""");
        Write("softnoir.json", """{ "base": "NOIR", "colors": { "highlight": "#FFAA00" } }""");

        var greener = User("greener");
        Assert.Equal(new Color(0xFF, 0xFF, 0xFF), greener.Ink);
        Assert.Equal(ThemePalette.Netrunner.Bg, greener.Bg);
        Assert.Equal(ThemePalette.Netrunner.GradientStops, greener.GradientStops);   // netrunner sets its own: passed on
        Assert.Equal(new Color(0xFF, 0xAA, 0x00), User("amber").Warn);              // synthwave's warn is its highlight
        Assert.Equal(ThemePalette.Noir.Warn, User("softnoir").Warn);                  // noir sets its own
    }

    [Fact]
    public void ABase_MayBeAnotherFile_AndItsStyleChangesComeToo()
    {
        Write("a.json", """{ "colors": { "primary": "#111111" }, "styles": { "codeKeyword": { "fg": "#222222", "bold": true } } }""");
        Write("b.json", """{ "base": "a", "colors": { "ink": "#333333" }, "styles": { "codeKeyword": { "italic": true } } }""");

        var b = User("b");

        Assert.Equal(new Color(0x11, 0x11, 0x11), b.Primary);
        Assert.Equal(new Color(0x33, 0x33, 0x33), b.Ink);
        var change = b.Styles![ThemeStyleSlot.CodeKeyword];
        Assert.Equal(new Color(0x22, 0x22, 0x22), change.Foreground);
        Assert.Equal(Decoration.Bold | Decoration.Italic, change.Set);
        Assert.Empty(Scan().Problems);
    }

    [Fact]
    public void Styles_TakeHexOrARole_AndFlagsOnOrOff()
    {
        Write("s.json", """
            {
              "colors": { "dim": "#555555" },
              "styles": {
                "codeComment": { "fg": "dim", "italic": false, "underline": true },
                "menuHighlight": { "bg": "#44475A" },
              }
            }
            """);

        var styles = User("s").Styles!;

        Assert.Equal(new StyleOverride(new Color(0x55, 0x55, 0x55), null, Decoration.Underline, Decoration.Italic), styles[ThemeStyleSlot.CodeComment]);   // the role in this theme
        Assert.Equal(new StyleOverride(null, new Color(0x44, 0x47, 0x5A)), styles[ThemeStyleSlot.MenuHighlight]);
    }

    [Fact]
    public void BadKeysAndValues_AreSkippedWithANote_AndTheThemeStillLoads()
    {
        Write("typos.json", """
            {
              "colors": { "primay": "#FFFFFF", "ink": "white", "bg": "#000000" },
              "gradient": ["#000000"],
              "styles": { "codeKeywrd": { "bold": true }, "codeString": { "fg": "#XYZ", "bold": true } }
            }
            """);

        var typos = User("typos");

        Assert.Equal(new Color(0, 0, 0), typos.Bg);
        Assert.Equal(ThemePalette.Synthwave.Ink, typos.Ink);
        Assert.Equal(new StyleOverride(Set: Decoration.Bold), typos.Styles![ThemeStyleSlot.CodeString]);
        Assert.Equal(
            [
                ThemeText.UnknownColor("primay"),
                ThemeText.BadColor("ink", "white"),
                ThemeText.BadGradient(1),
                ThemeText.UnknownStyle("codeKeywrd"),
                ThemeText.BadColor("codeString.fg", "#XYZ"),
            ],
            ProblemsOf("typos.json"));
    }

    [Fact]
    public void AGradient_TakesTwoToSixteenStops()
    {
        Write("two.json", """{ "gradient": ["#000000", "#FFFFFF"] }""");
        Write("many.json", "{ \"gradient\": [" + string.Join(", ", Enumerable.Repeat("\"#123456\"", 17)) + "] }");
        Write("badstop.json", """{ "gradient": ["#000000", "nope"] }""");

        Assert.Equal([new Color(0, 0, 0), new Color(0xFF, 0xFF, 0xFF)], User("two").GradientStops);
        Assert.Equal(ThemePalette.Synthwave.GradientStops, User("many").GradientStops);
        Assert.Equal([ThemeText.BadGradient(17)], ProblemsOf("many.json"));
        Assert.Equal([ThemeText.BadColor("gradient[1]", "nope")], ProblemsOf("badstop.json"));
    }

    [Fact]
    public void WholeFilesAreSkipped_ForTheirName_TheirJson_OrTheirBase()
    {
        Write("synthwave.json", "{}");
        Write("x.json", """{ "name": "dup" }""");
        Write("y.json", """{ "name": "dup" }""");
        Write("broken.json", "{ \"name\": ");
        Write("bad name.json", "{}");
        Write("orphan.json", """{ "base": "nothing" }""");
        Write("c1.json", """{ "base": "c2" }""");
        Write("c2.json", """{ "base": "c1" }""");
        Write("self.json", """{ "base": "self" }""");
        Write("child.json", """{ "base": "orphan" }""");

        var scan = Scan();

        Assert.Equal([.. ThemeName.Names, "dup"], scan.Names);
        Assert.Same(ThemePalette.Synthwave, scan.Themes[0]);   // the built-in wins
        Assert.Equal([ThemeText.BuiltInClash("synthwave")], ProblemsOf("synthwave.json"));
        Assert.Equal([ThemeText.Duplicate("dup", Path.Combine(_dir, "x.json"))], ProblemsOf("y.json"));
        Assert.StartsWith("skipped: not a theme file", Assert.Single(ProblemsOf("broken.json")));
        Assert.Equal([ThemeText.BadName("bad name")], ProblemsOf("bad name.json"));
        Assert.Equal([ThemeText.NoBase("nothing")], ProblemsOf("orphan.json"));
        Assert.Equal([ThemeText.BaseFailed("orphan")], ProblemsOf("child.json"));
        Assert.Equal([ThemeText.BaseCycle("self")], ProblemsOf("self.json"));
        // c1 is resolved first: c2's base leads back to it, and c1's base then did not load.
        Assert.Equal([ThemeText.BaseCycle("c1")], ProblemsOf("c2.json"));
        Assert.Equal([ThemeText.BaseFailed("c2")], ProblemsOf("c1.json"));
    }

    [Fact]
    public void Report_SaysEachProblemAsAWarning_NamingTheFile()
    {
        Write("orphan.json", """{ "base": "nothing" }""");
        var warnings = new List<DiagnosticEvent>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == "Theme" && e.Level == DiagnosticLevel.Warning) warnings.Add(e); };
        DiagnosticLog.Emitted += capture;
        try
        {
            ThemeCatalog.Report(Scan());
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        Assert.Contains("orphan.json: " + ThemeText.NoBase("nothing"), Assert.Single(warnings).Message);
    }

    // ── Equality, Use and Resolve ──────────────────────────────────────────

    [Fact]
    public void ARescan_IsANewInstance_WithTheSameLook_AndAnEditIsNot()
    {
        Write("mine.json", """{ "colors": { "primary": "#123456" }, "styles": { "hint": { "italic": true } } }""");
        var first = User("mine");
        var second = User("mine");

        Assert.NotSame(first, second);
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());

        Write("mine.json", """{ "colors": { "primary": "#123456" }, "styles": { "hint": { "italic": false } } }""");
        Assert.NotEqual(first, User("mine"));
        Assert.NotEqual(ThemePalette.Synthwave, ThemePalette.Netrunner);
    }

    [Fact]
    public void Use_KeepsTheInstanceInForce_ForTheSameLook()
    {
        using var scope = new ThemeScope();
        Write("mine.json", """{ "colors": { "primary": "#123456" } }""");
        var first = User("mine");
        Theme.Use(first);
        Theme.Use(User("mine"));

        Assert.Same(first, Theme.Current);
        Assert.Equal(new Color(0x12, 0x34, 0x56), Theme.Primary);
    }

    [Fact]
    public void Resolve_FindsAUserTheme_AndApplyPutsItInForce()
    {
        using var scope = new ThemeScope();
        Write("mine.json", """{ "colors": { "primary": "#123456" } }""");
        var settings = new AppSettingsData { Theme = "Mine" };

        Assert.Equal("mine", ThemeName.Resolve(settings, _dir).Name);
        Assert.Same(ThemePalette.Synthwave, ThemeName.Resolve(settings));   // the built-ins alone do not know it
        ThemeName.Apply(settings, _dir);
        Assert.Equal("mine", Theme.Current.Name);
        Assert.Same(ThemePalette.Noir, ThemeName.Resolve(new AppSettingsData { Theme = "noir" }, Path.Combine(_dir, "missing")));
    }

    [Fact]
    public void Resolve_AFileThatNoLongerLoads_WarnsListingTheUsersToo()
    {
        Write("other.json", "{}");
        var warnings = new List<DiagnosticEvent>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == "Theme" && e.Level == DiagnosticLevel.Warning) warnings.Add(e); };
        DiagnosticLog.Emitted += capture;
        try
        {
            Assert.Same(ThemePalette.Synthwave, ThemeName.Resolve(new AppSettingsData { Theme = "gone" }, _dir));
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        Assert.Contains("Theme='gone' is not one of synthwave, netrunner, nostromo, noir, cyberpunk, vaporwave, mainframe, grid, replicant, abyssal, other. Using synthwave.", Assert.Single(warnings).Message);
    }

    // ── Export ─────────────────────────────────────────────────────────────

    [Fact]
    public void Export_IsAFullFile_ThatReadsBackToTheSameLook()
    {
        string json = ThemeFile.Export(ThemePalette.Nostromo, "my-nostromo");
        Write("my-nostromo.json", json);

        var copy = User("my-nostromo");

        Assert.Contains("\"name\": \"my-nostromo\"", json);
        Assert.Contains("\"panelBg\": \"#1A1004\"", json);
        Assert.DoesNotContain("\"base\"", json);
        Assert.Equal(ThemePalette.Nostromo.Colors(), copy.Colors());
        Assert.Equal(ThemePalette.Nostromo.GradientStops, copy.GradientStops);
        Assert.Equal(ThemePalette.Nostromo.Description, copy.Description);
        Assert.Null(copy.Styles);
        Assert.Empty(Scan().Problems);
    }

    [Fact]
    public void Export_KeepsTheStyleChanges()
    {
        Write("a.json", """{ "styles": { "codeKeyword": { "fg": "#222222", "bold": true, "italic": false } } }""");
        var a = User("a");

        Write("b.json", ThemeFile.Export(a, "b"));

        Assert.Equal(a.Styles![ThemeStyleSlot.CodeKeyword], User("b").Styles![ThemeStyleSlot.CodeKeyword]);
        Assert.Equal(a.Colors(), User("b").Colors());
    }
}
