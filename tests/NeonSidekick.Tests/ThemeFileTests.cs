using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.Tests;

/// <summary>
/// The theme files (2026-10-01): the user's, <c>&lt;home&gt;/themes/*.json</c>, and since 2026-10-05 the built-ins, <c>assets/themes</c>
/// embedded (<see cref="ThemeLibrary"/>, more in <c>ThemeLibraryTests</c>): the file, the catalog, the export, and the bar every
/// shipped theme is held to. A file stands alone since 2026-10-05 (no <c>base</c>), so a test's file is <see cref="ThemeJson.File"/>.
/// </summary>
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

    /// <summary>Every colour role but <paramref name="set"/>, in role order: what a file setting only those leaves out.</summary>
    private static List<string> AllBut(params string[] set) => [.. ThemeKeys.Colors.Where(role => !set.Contains(role))];

    // ── Subfolders (2026-10-02) ─────────────────────────────────────────────

    private void WriteIn(string folder, string file, string json)
    {
        Directory.CreateDirectory(Path.Combine(_dir, folder));
        File.WriteAllText(Path.Combine(_dir, folder, file), json);
    }

    [Fact]
    public void AFirstLevelSubfolder_IsRead_ADeeperOne_AndADotFolder_AreNot()
    {
        WriteIn("cosmos", "dawn.json", ThemeJson.File(null, ("primary", "#112233")));
        WriteIn(Path.Combine("cosmos", "deeper"), "hidden.json", ThemeJson.File());
        WriteIn(".parked", "parked.json", ThemeJson.File());
        Write("loose.json", ThemeJson.File());

        var scan = Scan();

        Assert.Equal(AToZ("dawn", "loose"), scan.Names);   // the user's themes in name order, wherever they sit
        Assert.Empty(scan.Problems);
    }

    [WindowsFact]
    public void ALooseFile_KeepsItsName_OverASubfoldersFile_WhichIsSkipped_NamedByItsPath()
    {
        Write("mine.json", ThemeJson.File(null, ("primary", "#112233")));
        WriteIn("solid", "mine.json", ThemeJson.File(null, ("primary", "#445566")));
        WriteIn("a-first", "mine.json", ThemeJson.File());   // a subfolder sorting before the loose file's name changes nothing: the folder's own come first

        var scan = Scan();

        Assert.Equal(new Color(0x11, 0x22, 0x33), User("mine").Primary);
        Assert.Equal(
            [("a-first\\mine.json", ThemeText.Duplicate("mine", "mine.json")), ("solid\\mine.json", ThemeText.Duplicate("mine", "mine.json"))],
            scan.Problems.Select(p => (p.Shown!, p.Problem)));
        Assert.Equal("solid\\mine.json: skipped: mine.json already names a theme \"mine\"", ThemeText.Problem(scan.Problems[1].Shown!, scan.Problems[1].Problem));
    }

    /// <summary>The Unix twin of <see cref="ALooseFile_KeepsItsName_OverASubfoldersFile_WhichIsSkipped_NamedByItsPath"/> (2026-10-06, the macOS build): its paths with <c>/</c>.</summary>
    [UnixFact]
    public void ALooseFile_KeepsItsName_OverASubfoldersFile_WhichIsSkipped_NamedByItsPath_Unix()
    {
        Write("mine.json", ThemeJson.File(null, ("primary", "#112233")));
        WriteIn("solid", "mine.json", ThemeJson.File(null, ("primary", "#445566")));
        WriteIn("a-first", "mine.json", ThemeJson.File());   // a subfolder sorting before the loose file's name changes nothing: the folder's own come first

        var scan = Scan();

        Assert.Equal(new Color(0x11, 0x22, 0x33), User("mine").Primary);
        Assert.Equal(
            [("a-first/mine.json", ThemeText.Duplicate("mine", "mine.json")), ("solid/mine.json", ThemeText.Duplicate("mine", "mine.json"))],
            scan.Problems.Select(p => (p.Shown!, p.Problem)));
        Assert.Equal("solid/mine.json: skipped: mine.json already names a theme \"mine\"", ThemeText.Problem(scan.Problems[1].Shown!, scan.Problems[1].Problem));
    }

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
    public void Scan_TheBuiltInsAndTheUsers_AToZ()
    {
        Write("zeta.json", ThemeJson.File(null, ("primary", "#010203")));
        Write("Alpha.json", ThemeJson.File("\"name\": \"alpha\", \"description\": \"first of mine\""));
        Write("notes.txt", "not a theme");

        var scan = Scan();

        Assert.Equal(AToZ("alpha", "zeta"), scan.Names);   // one list by name since 2026-10-03
        Assert.Empty(scan.Problems);
        Assert.Equal("first of mine", User("alpha").Description);
        Assert.Equal(ThemeText.CustomDescription, User("zeta").Description);   // none given
        Assert.False(User("zeta").IsBuiltIn);
        Assert.Equal(Path.Combine(_dir, "zeta.json"), User("zeta").SourcePath);
        Assert.All(ShippedThemes.All, p => Assert.True(p.IsBuiltIn));
    }

    [Fact]
    public void AFile_StandsAlone_ItsGradientDerivedFromItsOwnColours()
    {
        string colors = ThemeJson.Colors(("primary", "#FF79C6")).Replace("\"bg\"", "\"BG\"").Replace(" }", ", }");
        Write("dracula.json", $$"""
            {
              // comments, any case in a role and a trailing comma are fine
              "name": "Dracula",
              "colors": {{colors}},
            }
            """);

        var dracula = User("dracula");

        Assert.Equal(new Color(0xFF, 0x79, 0xC6), dracula.Primary);
        Assert.Equal(ShippedThemes.Synthwave.Bg, dracula.Bg);
        Assert.Equal(ThemePalette.DerivedGradient(dracula.Primary, dracula.Secondary, dracula.Tertiary, dracula.Warm, dracula.Highlight), dracula.GradientStops);
        Assert.Null(dracula.Styles);
        Assert.Empty(ProblemsOf("dracula.json"));
    }

    [Fact]
    public void AFileLeavingColoursOut_IsSkipped_NamingThem_AndABase_IsIgnoredWithANote()
    {
        Write("greener.json", """{ "base": "netrunner", "colors": { "ink": "#FFFFFF", "primary": "#00FF00" } }""");
        Write("based.json", ThemeJson.File("\"base\": \"noir\""));
        Write("empty.json", "{}");

        var scan = Scan();

        Assert.Equal(AToZ("based"), scan.Names);
        Assert.Equal([ThemeText.BaseIgnored("netrunner"), ThemeText.MissingColors(AllBut("primary", "ink"))], ProblemsOf("greener.json"));
        Assert.Equal([ThemeText.MissingColors(ThemeKeys.Colors)], ProblemsOf("empty.json"));
        Assert.Equal([ThemeText.BaseIgnored("noir")], ProblemsOf("based.json"));   // loads, its own colours: synthwave's here, not noir's
        Assert.Equal(ShippedThemes.Synthwave.Colors(), User("based").Colors());
        Assert.Equal(
            "skipped: it does not set secondary, tertiary (a theme sets all 15 colours; /theme export writes a full file)",
            ThemeText.MissingColors(["secondary", "tertiary"]));
    }

    [Fact]
    public void Styles_TakeHexOrARole_AndFlagsOnOrOff()
    {
        Write("s.json", ThemeJson.File("""
            "styles": {
              "codeComment": { "fg": "dim", "italic": false, "underline": true },
              "menuHighlight": { "bg": "#44475A" },
            }
            """, ("dim", "#555555")));

        var styles = User("s").Styles!;

        Assert.Equal(new StyleOverride(new Color(0x55, 0x55, 0x55), null, Decoration.Underline, Decoration.Italic), styles[ThemeStyleSlot.CodeComment]);   // the role in this theme
        Assert.Equal(new StyleOverride(null, new Color(0x44, 0x47, 0x5A)), styles[ThemeStyleSlot.MenuHighlight]);
    }

    [Fact]
    public void BadKeysAndValues_AreSkippedWithANote_AndTheThemeStillLoads()
    {
        Write("typos.json", ThemeJson.File("""
            "gradient": ["#000000"],
            "styles": { "codeKeywrd": { "bold": true }, "codeString": { "fg": "#XYZ", "bold": true } }
            """).Replace("\"colors\": { ", "\"colors\": { \"primay\": \"#FFFFFF\", "));

        var typos = User("typos");

        Assert.Equal(ShippedThemes.Synthwave.Colors(), typos.Colors());
        Assert.Equal(new StyleOverride(Set: Decoration.Bold), typos.Styles![ThemeStyleSlot.CodeString]);
        Assert.Equal(
            [
                ThemeText.UnknownColor("primay"),
                ThemeText.BadGradient(1),
                ThemeText.UnknownStyle("codeKeywrd"),
                ThemeText.BadColor("codeString.fg", "#XYZ"),
            ],
            ProblemsOf("typos.json"));
    }

    [Fact]
    public void ABadColourValue_LeavesItsRoleUnset_SoTheFileIsSkipped()
    {
        Write("white.json", ThemeJson.File(null, ("ink", "white")));

        Assert.Equal(AToZ(), Scan().Names);
        Assert.Equal([ThemeText.BadColor("ink", "white"), ThemeText.MissingColors(["ink"])], ProblemsOf("white.json"));
    }

    [Fact]
    public void AGradient_TakesTwoToSixteenStops()
    {
        Write("two.json", ThemeJson.File("""  "gradient": ["#000000", "#FFFFFF"]  """));
        Write("many.json", ThemeJson.File("\"gradient\": [" + string.Join(", ", Enumerable.Repeat("\"#123456\"", 17)) + "]"));
        Write("badstop.json", ThemeJson.File("""  "gradient": ["#000000", "nope"]  """));

        var many = User("many");
        Assert.Equal([new Color(0, 0, 0), new Color(0xFF, 0xFF, 0xFF)], User("two").GradientStops);
        Assert.Equal(ThemePalette.DerivedGradient(many.Primary, many.Secondary, many.Tertiary, many.Warm, many.Highlight), many.GradientStops);
        Assert.Equal([ThemeText.BadGradient(17)], ProblemsOf("many.json"));
        Assert.Equal([ThemeText.BadColor("gradient[1]", "nope")], ProblemsOf("badstop.json"));
    }

    [Fact]
    public void WholeFilesAreSkipped_ForTheirName_TheirJson_OrTheirColours()
    {
        Write("x.json", ThemeJson.File("\"name\": \"dup\""));
        Write("y.json", ThemeJson.File("\"name\": \"dup\""));
        Write("broken.json", "{ \"name\": ");
        Write("bad name.json", ThemeJson.File());
        Write("partial.json", """{ "colors": { "primary": "#123456" } }""");

        var scan = Scan();

        Assert.Equal(AToZ("dup"), scan.Names);
        Assert.Equal([ThemeText.Duplicate("dup", "x.json")], ProblemsOf("y.json"));
        Assert.StartsWith("skipped: not a theme file", Assert.Single(ProblemsOf("broken.json")));
        Assert.Equal([ThemeText.BadName("bad name")], ProblemsOf("bad name.json"));
        Assert.Equal([ThemeText.MissingColors(AllBut("primary"))], ProblemsOf("partial.json"));
    }

    [Fact]
    public void AFileThatFails_StillClaimsItsName()
    {
        Write("a.json", """{ "name": "dup", "colors": {} }""");
        Write("b.json", ThemeJson.File("\"name\": \"dup\""));

        Assert.Equal(AToZ(), Scan().Names);
        Assert.Equal([ThemeText.Duplicate("dup", "a.json")], ProblemsOf("b.json"));
    }

    // ── Overrides (later on 2026-10-01, the user's call: a file named like a built-in wins) ──

    private static int BuiltInIndex(string name) => Array.IndexOf(ThemeName.Names, name);

    /// <summary>The built-ins' names and <paramref name="mine"/> as one list A to Z, the scan's order since 2026-10-03.</summary>
    private static List<string> AToZ(params string[] mine) => [.. ThemeName.Names.Concat(mine).Order(StringComparer.Ordinal)];

    [Fact]
    public void AFileNamedLikeABuiltIn_TakesItsPlace_StandingAlone()
    {
        Write("noir.json", ThemeJson.File(null, ("primary", "#123456")));
        Write("synthwave.json", ThemeJson.File("\"description\": \"my synthwave\"", ("secondary", "#654321")));

        var scan = Scan();
        var noir = scan.Themes.Single(t => t.Name == "noir");
        var synthwave = scan.Themes.Single(t => t.Name == "synthwave");

        Assert.Empty(scan.Problems);
        Assert.Equal(ThemeName.Names, scan.Names);   // listed once, in the built-in's stead
        Assert.Equal(Path.Combine(_dir, "noir.json"), noir.SourcePath);
        Assert.False(noir.IsBuiltIn);
        // Nothing comes from the built-in it replaces: noir's name, the file's colours (synthwave's but the primary).
        Assert.Equal(new Color(0x12, 0x34, 0x56), noir.Primary);
        Assert.Equal(ShippedThemes.Synthwave.Ink, noir.Ink);
        Assert.Equal("my synthwave", synthwave.Description);
        Assert.Equal(new Color(0x65, 0x43, 0x21), synthwave.Secondary);
    }

    [Fact]
    public void AnOverrideThatFails_LeavesTheBuiltIn()
    {
        Write("noir.json", """{ "colors": { "ink": "#EEEEEE" } }""");
        Write("grid.json", "not json");

        var scan = Scan();

        Assert.Same(ShippedThemes.Noir, scan.Themes[BuiltInIndex("noir")]);
        Assert.Same(ShippedThemes.Grid, scan.Themes[BuiltInIndex("grid")]);
        Assert.Equal([ThemeText.MissingColors(AllBut("ink"))], ProblemsOf("noir.json"));
        Assert.Equal(ThemeName.Names, scan.Names);
    }

    [Fact]
    public void Resolve_ABuiltInsName_FindsItsOverride_AndTheFallbackIsTheOverriddenDefault()
    {
        Write("noir.json", ThemeJson.File(null, ("primary", "#123456")));
        Write(ThemeName.Default + ".json", ThemeJson.File(null, ("primary", "#654321")));

        var noir = ThemeName.Resolve(new AppSettingsData { Theme = "noir" }, _dir);
        Assert.Equal(new Color(0x12, 0x34, 0x56), noir.Primary);
        Assert.Same(ShippedThemes.Noir, ThemeName.Resolve(new AppSettingsData { Theme = "noir" }));   // the built-ins alone
        Assert.Equal(new Color(0x65, 0x43, 0x21), ThemeName.Resolve(new AppSettingsData(), _dir).Primary);
        Assert.Equal(new Color(0x65, 0x43, 0x21), ThemeName.Resolve(new AppSettingsData { Theme = "gone" }, _dir).Primary);
    }

    [Fact]
    public void Report_SaysEachProblemAsAWarning_NamingTheFile()
    {
        Write("orphan.json", """{ "colors": { "primary": "#123456" } }""");
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

        Assert.Contains("orphan.json: " + ThemeText.MissingColors(AllBut("primary")), Assert.Single(warnings).Message);
    }

    // ── Equality, Use and Resolve ──────────────────────────────────────────

    [Fact]
    public void ARescan_IsANewInstance_WithTheSameLook_AndAnEditIsNot()
    {
        Write("mine.json", ThemeJson.File("""  "styles": { "hint": { "italic": true } }  """, ("primary", "#123456")));
        var first = User("mine");
        var second = User("mine");

        Assert.NotSame(first, second);
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());

        Write("mine.json", ThemeJson.File("""  "styles": { "hint": { "italic": false } }  """, ("primary", "#123456")));
        Assert.NotEqual(first, User("mine"));
        Assert.NotEqual(ShippedThemes.Synthwave, ShippedThemes.Netrunner);
    }

    [Fact]
    public void Use_KeepsTheInstanceInForce_ForTheSameLook()
    {
        using var scope = new ThemeScope();
        Write("mine.json", ThemeJson.File(null, ("primary", "#123456")));
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
        Write("mine.json", ThemeJson.File(null, ("primary", "#123456")));
        var settings = new AppSettingsData { Theme = "Mine" };

        Assert.Equal("mine", ThemeName.Resolve(settings, _dir).Name);
        Assert.Same(ThemeLibrary.Default, ThemeName.Resolve(settings));   // the built-ins alone do not know it
        ThemeName.Apply(settings, _dir);
        Assert.Equal("mine", Theme.Current.Name);
        Assert.Same(ShippedThemes.Noir, ThemeName.Resolve(new AppSettingsData { Theme = "noir" }, Path.Combine(_dir, "missing")));
    }

    [Fact]
    public void Resolve_AFileThatNoLongerLoads_WarnsListingTheUsersToo()
    {
        Write("other.json", ThemeJson.File());
        var warnings = new List<DiagnosticEvent>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == "Theme" && e.Level == DiagnosticLevel.Warning) warnings.Add(e); };
        DiagnosticLog.Emitted += capture;
        try
        {
            Assert.Same(ThemeLibrary.Default, ThemeName.Resolve(new AppSettingsData { Theme = "gone" }, _dir));
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        Assert.Contains($"Theme='gone' is not one of {string.Join(", ", AToZ("other"))}. Using collider.", Assert.Single(warnings).Message);
    }

    // ── Export ─────────────────────────────────────────────────────────────

    [Fact]
    public void Export_IsAFullFile_ThatReadsBackToTheSameLook()
    {
        string json = ThemeFile.Export(ShippedThemes.Nostromo, "my-nostromo");
        Write("my-nostromo.json", json);

        var copy = User("my-nostromo");

        Assert.Contains("\"name\": \"my-nostromo\"", json);
        Assert.Contains("\"panelBg\": \"" + Theme.ToHex(ShippedThemes.Nostromo.PanelBg) + "\"", json);
        Assert.DoesNotContain("\"base\"", json);
        Assert.Equal(ShippedThemes.Nostromo.Colors(), copy.Colors());
        Assert.Equal(ShippedThemes.Nostromo.GradientStops, copy.GradientStops);
        Assert.Equal(ShippedThemes.Nostromo.Description, copy.Description);
        Assert.Null(copy.Styles);
        Assert.Empty(Scan().Problems);
    }

    [Fact]
    public void Export_KeepsTheStyleChanges()
    {
        Write("a.json", ThemeJson.File("""  "styles": { "codeKeyword": { "fg": "#222222", "bold": true, "italic": false } }  """));
        var a = User("a");

        Write("b.json", ThemeFile.Export(a, "b"));

        Assert.Equal(a.Styles![ThemeStyleSlot.CodeKeyword], User("b").Styles![ThemeStyleSlot.CodeKeyword]);
        Assert.Equal(a.Colors(), User("b").Colors());
    }

    /// <summary>
    /// Every built-in written out by <c>/theme export</c> under its own name and dropped into the themes folder replaces it with the
    /// very same look and no problem (2026-10-05; the repo's <c>built-in</c> folder of exports was held to this until then).
    /// </summary>
    [Fact]
    public void EveryBuiltIn_ExportedUnderItsOwnName_ReplacesItWithTheSameLook()
    {
        foreach (var builtIn in ShippedThemes.All)
        {
            Write(builtIn.Name + ".json", ThemeFile.Export(builtIn, builtIn.Name));
        }

        var scan = Scan();

        Assert.Empty(scan.Problems);
        Assert.Equal(ThemeName.Names, scan.Names);
        foreach (var builtIn in ShippedThemes.All)
        {
            var file = scan.Themes.Single(t => t.Name == builtIn.Name);
            Assert.False(file.IsBuiltIn, file.Name);
            Assert.Equal(builtIn with { SourcePath = file.SourcePath }, file);
        }
    }

    // ── The shipped themes (assets/themes, the built-ins since 2026-10-05) ──

    /// <summary>The styles that are strokes or a hint rather than text to read: they need only be seen, not read.</summary>
    private static readonly ThemeStyleSlot[] Strokes =
        [ThemeStyleSlot.Border, ThemeStyleSlot.PaneRule, ThemeStyleSlot.MarkdownRule, ThemeStyleSlot.Placeholder];

    /// <summary>
    /// Each built-in is held to the bar of <c>ThemeTests.EveryPalette_IsReadable_AndKeepsItsRolesApart</c> and then style by style,
    /// since a file may restyle any of them: every text style at least 3:1 on its own background (the page's when it has none), the
    /// strokes at least visible; the accent and the first heading are never the very style of body or bold text, which would make them
    /// vanish (glacier, chalkboard and hal had); and a style change must change something, since the atlas and a reader copying the
    /// file count it as one. The original ten were held only to the first bar until they became files beside the rest (2026-10-05).
    /// </summary>
    [Fact]
    public void BuiltIns_AreReadable_StyleByStyle()
    {
        using var scope = new ThemeScope();
        var failures = new List<string>();
        foreach (var p in ShippedThemes.All)
        {
            Check(p.Name, ThemeTests.Contrast(p.Ink, p.Bg) >= 7, "ink on bg");
            Check(p.Name, ThemeTests.Contrast(p.Ink, p.PanelBg) >= 7, "ink on panel");
            Check(p.Name, ThemeTests.Contrast(p.Dim, p.Bg) >= 3, "dim on bg");
            Check(p.Name, ThemeTests.Contrast(p.Dim, p.PanelBg) >= 3, "dim on panel");
            Check(p.Name, ThemeTests.Contrast(p.Bad, p.Bg) >= 3, "bad on bg");
            Check(p.Name, ThemeTests.Contrast(p.Good, p.Bg) >= 3, "good on bg");
            Check(p.Name, ThemeTests.Contrast(p.Bg, p.Secondary) >= 4.5, "the selection");

            Theme.Use(p);
            foreach (var slot in Enum.GetValues<ThemeStyleSlot>())
            {
                var style = Theme.Of(slot);
                double ratio = ThemeTests.Contrast(style.Foreground, style.Background == Color.Default ? p.Bg : style.Background);
                double floor = Strokes.Contains(slot) ? 1.3 : 3;
                Check(p.Name, ratio >= floor, $"{ThemeKeys.Of(slot)} at {ratio:0.00}:1");
            }

            Check(p.Name, Theme.Accent != Theme.Body, "accent is body text");
            Check(p.Name, Theme.MarkdownHeading1 != Theme.MarkdownBold, "markdownHeading1 is bold text");

            foreach (var (slot, _) in p.Styles ?? new Dictionary<ThemeStyleSlot, StyleOverride>())
            {
                Theme.Use(p);
                var changed = Theme.Of(slot);
                Theme.Use(p with { Styles = p.Styles!.Where(s => s.Key != slot).ToDictionary() });
                Check(p.Name, Theme.Of(slot) != changed, $"the change to {ThemeKeys.Of(slot)} restates its derived style");
            }
        }

        Assert.Empty(failures);

        void Check(string name, bool ok, string what)
        {
            if (!ok)
            {
                failures.Add($"{name}: {what}");
            }
        }
    }

    /// <summary>A solid theme's banner is one colour throughout, and that colour is the whole title, so it is held to 4.5:1.</summary>
    [Fact]
    public void SolidBanners_AreOneReadableColour()
    {
        var themes = ShippedThemes.All.Where(t => ThemeLibrary.CategoryOf(t.Name) == "solid").ToList();

        Assert.NotEmpty(themes);
        Assert.All(themes, t =>
        {
            Assert.Single(t.GradientStops.Distinct());
            Assert.True(ThemeTests.Contrast(t.GradientStops[0], t.Bg) >= 4.5, $"{t.Name}: banner {ThemeTests.Contrast(t.GradientStops[0], t.Bg):0.00}:1");
        });
    }
}
