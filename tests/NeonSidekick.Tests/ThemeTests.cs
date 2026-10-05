using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.Tests;

public class ThemeTests
{
    [Fact]
    public void ToHex_RendersUppercaseSixDigitHex()
    {
        Assert.Equal("#FF2E97", Theme.ToHex(new Color(0xFF, 0x2E, 0x97)));
        Assert.Equal("#0B0416", Theme.ToHex(new Color(0x0B, 0x04, 0x16)));
    }

    /// <summary>
    /// The colour properties read the palette in force, role by role. Synthwave's hex values were pinned here until the themes became
    /// <c>assets/themes</c>' files (2026-10-05, the user's ask): the file is the source now, and editing it is the point.
    /// </summary>
    [Fact]
    public void Palette_TheRolesReadThePaletteInForce()
    {
        using var scope = new ThemeScope();
        var p = ShippedThemes.Synthwave;
        Assert.Equal(
            [p.Primary, p.Secondary, p.Tertiary, p.Deep, p.Highlight, p.Warm, p.Tint, p.Ink, p.Dim, p.Dimmer, p.Bg, p.PanelBg, p.Good, p.Bad, p.Warn],
            [Theme.Primary, Theme.Secondary, Theme.Tertiary, Theme.Deep, Theme.Highlight, Theme.Warm, Theme.Tint, Theme.Ink, Theme.Dim, Theme.Dimmer, Theme.Bg, Theme.PanelBg, Theme.Good, Theme.Bad, Theme.Warn]);
        Assert.Equal(Theme.Dimmer, Theme.Placeholder.Foreground);
    }

    [Fact]
    public void GradientMarkup_StartsCyanAndEndsAmber()
    {
        string markup = Theme.GradientMarkup("ab");
        Assert.StartsWith("[#33E0FF]a[/]", markup);
        Assert.EndsWith("[#FFC832]b[/]", markup);
    }

    [Fact]
    public void GradientMarkup_EscapesMarkupCharacters()
    {
        string markup = Theme.GradientMarkup("[");
        Assert.Equal("[#33E0FF][[[/]", markup);
        Assert.NotNull(new Markup(markup)); // parses
    }

    [Fact]
    public void Rule_ProducesExactlyWidthGlyphs()
    {
        string markup = Theme.Rule(43);
        int glyphs = markup.Count(c => c == '─');
        Assert.Equal(43, glyphs);
        Assert.NotNull(new Markup(markup));
    }

    [Fact]
    public void Rule_ZeroWidthIsEmpty()
    {
        Assert.Equal(string.Empty, Theme.Rule(0));
    }

    [Fact]
    public void AccentMarkup_EscapesText()
    {
        string markup = Theme.AccentMarkup("a[b]");
        Assert.Equal("[#FF2E97 bold]a[[b]][/]", markup);
    }

    [Fact]
    public void SampleGradient_Endpoints()
    {
        Assert.Equal(Theme.Secondary, Theme.SampleGradient(0, Theme.GradientStops));
        Assert.Equal(Theme.Highlight, Theme.SampleGradient(1, Theme.GradientStops));
        Assert.Equal(Theme.Primary, Theme.SampleGradient(0.5, Theme.GradientStops));
    }

    // ── Themes (2026-09-23) ─────────────────────────────────────────────────

    [Fact]
    public void TheSuiteWearsSynthwave_TheAppStartsOnTheDefault()
    {
        using var scope = new ThemeScope();
        Assert.Same(ShippedThemes.Synthwave, Theme.Current);   // ModuleInit and ThemeScope: the renders the tests pin
        Assert.Equal("collider", ThemeLibrary.Default.Name);  // what Theme starts on in the app (2026-10-05)
        Assert.Equal(ShippedThemes.Synthwave.GradientStops, Theme.GradientStops);
    }

    [Fact]
    public void Use_SwapsEveryColourAndStyle_AndSynthwaveComesBack()
    {
        using var scope = new ThemeScope();
        Theme.Use(ShippedThemes.Netrunner);

        Assert.Same(ShippedThemes.Netrunner, Theme.Current);
        Assert.Equal("#00FF41", Theme.ToHex(Theme.Primary));
        Assert.Equal(ShippedThemes.Netrunner.Secondary, Theme.User.Foreground);
        Assert.Equal(ShippedThemes.Netrunner.Deep, Theme.PaneRule.Foreground);
        Assert.Equal(ShippedThemes.Netrunner.PanelBg, Theme.CodeString.Background);
        Assert.Equal(ShippedThemes.Netrunner.Highlight, Theme.CodeString.Foreground);
        Assert.Equal("[#00FF41 bold]x[/]", Theme.AccentMarkup("x"));
        Assert.StartsWith("[#0F6B2E]a[/]", Theme.GradientMarkup("ab"));

        Theme.Use(ShippedThemes.Synthwave);
        Assert.Equal("#FF2E97", Theme.ToHex(Theme.Primary));
        Assert.Equal(ShippedThemes.Synthwave.Secondary, Theme.User.Foreground);
    }

    [Fact]
    public void Noir_IsGreyscale_ButForTheRedOfFailures()
    {
        var noir = ShippedThemes.Noir;
        Color[] rest = [noir.Primary, noir.Secondary, noir.Tertiary, noir.Deep, noir.Highlight, noir.Warm, noir.Tint, noir.Ink, noir.Dim, noir.Dimmer, noir.Bg, noir.PanelBg, noir.Good, noir.Warn, .. noir.GradientStops];
        Assert.All(rest, c => Assert.True(c.R == c.G && c.G == c.B, $"{Theme.ToHex(c)} is not a grey"));
        Assert.True(noir.Bad.R > noir.Bad.G && noir.Bad.R > noir.Bad.B);   // the user's call: grey plus a muted red for errors
    }

    public static TheoryData<string> Palettes() => new(ShippedThemes.All.Select(p => p.Name));

    [Theory]
    [MemberData(nameof(Palettes))]
    public void EveryPalette_IsReadable_AndKeepsItsRolesApart(string name)
    {
        var p = ShippedThemes.All.Single(t => t.Name == name);
        Assert.Equal(name, name.ToLowerInvariant());
        Assert.NotEmpty(p.Description);
        Assert.InRange(p.GradientStops.Length, 2, ThemeFile.MaxGradientStops);   // five on the ten compiled ones until 2026-10-05; a file gives 2 to 16
        // WCAG contrast over the page and the lifted fill: body text comfortably, dim text legibly.
        Assert.True(Contrast(p.Ink, p.Bg) >= 7, $"{name}: ink on bg {Contrast(p.Ink, p.Bg):0.0}");
        Assert.True(Contrast(p.Ink, p.PanelBg) >= 7, $"{name}: ink on panel {Contrast(p.Ink, p.PanelBg):0.0}");
        Assert.True(Contrast(p.Dim, p.Bg) >= 3, $"{name}: dim on bg {Contrast(p.Dim, p.Bg):0.0}");
        Assert.True(Contrast(p.Dim, p.PanelBg) >= 3, $"{name}: dim on panel {Contrast(p.Dim, p.PanelBg):0.0}");
        Assert.True(Contrast(p.Bad, p.Bg) >= 3, $"{name}: bad on bg {Contrast(p.Bad, p.Bg):0.0}");
        Assert.True(Contrast(p.Good, p.Bg) >= 3, $"{name}: good on bg {Contrast(p.Good, p.Bg):0.0}");
        Assert.True(Contrast(p.Bg, p.Secondary) >= 4.5, $"{name}: the selection {Contrast(p.Bg, p.Secondary):0.0}");
        Assert.NotEqual(p.Good, p.Bad);
        Assert.NotEqual(p.Primary, p.Secondary);
        Assert.NotEqual(p.Bg, p.PanelBg);
    }

    // ── Style changes (2026-10-01, the user's themes) ───────────────────────

    [Fact]
    public void NoChanges_AnAliasIsItsSourcesStyle()
    {
        using var scope = new ThemeScope();
        Assert.Equal(Theme.AccentSecondary, Theme.User);
        Assert.Equal(Theme.AccentSecondary, Theme.SpinnerStyle);
        Assert.Equal(Theme.DimText, Theme.Hint);
        Assert.Equal(Theme.CodeKeyword, Theme.CodeTag);
        Assert.Equal(Theme.PaneRule, Theme.MarkdownRule);
    }

    [Fact]
    public void AChange_RestylesItsSlot_AndFlowsIntoItsAliases_ButNotIntoOneChangedItself()
    {
        using var scope = new ThemeScope();
        var green = new Color(0, 0xFF, 0);
        var red = new Color(0xFF, 0, 0);
        Theme.Use(ShippedThemes.Synthwave with
        {
            Name = "custom",
            Styles = new Dictionary<ThemeStyleSlot, StyleOverride>
            {
                [ThemeStyleSlot.AccentSecondary] = new(Foreground: green, Clear: Decoration.Bold),
                [ThemeStyleSlot.Spinner] = new(Foreground: red),
                [ThemeStyleSlot.CodeComment] = new(Background: red, Set: Decoration.Underline, Clear: Decoration.Italic),
            },
        });

        Assert.Equal(green, Theme.AccentSecondary.Foreground);
        Assert.Equal(Decoration.None, Theme.AccentSecondary.Decoration);
        Assert.Equal(green, Theme.User.Foreground);              // the alias follows its source
        Assert.Equal(ShippedThemes.Synthwave.Tertiary, Theme.MarkdownHeading.Foreground);   // H2 off the secondary since 2026-10-04 (the tertiary's)
        Assert.Equal(Theme.MarkdownBold, Theme.MarkdownHeading3);                         // H3 and below: the body ink, bold
        Assert.Equal(red, Theme.SpinnerStyle.Foreground);        // changed itself
        Assert.Equal(Decoration.None, Theme.SpinnerStyle.Decoration);   // over its source's final style
        Assert.Equal(ShippedThemes.Synthwave.Dim, Theme.CodeComment.Foreground);
        Assert.Equal(red, Theme.CodeComment.Background);
        Assert.Equal(Decoration.Underline, Theme.CodeComment.Decoration);
        Assert.Equal(ShippedThemes.Synthwave.Secondary, Theme.TableHeader.Foreground);   // no alias: untouched
        Assert.Equal(ShippedThemes.Synthwave.Secondary, Theme.Secondary);                // the palette colour stays
    }

    [Fact]
    public void Synthwave_StylesAreTheOriginalCompositions()
    {
        using var scope = new ThemeScope();
        var p = ShippedThemes.Synthwave;
        Assert.Equal(new Style(p.Primary, decoration: Decoration.Bold), Theme.Accent);
        Assert.Equal(new Style(p.Ink, p.PanelBg), Theme.MenuHighlight);
        Assert.Equal(new Style(p.Bg, p.Secondary), Theme.SelectedText);
        Assert.Equal(new Style(p.Dim, p.PanelBg, Decoration.Italic), Theme.ThinkingText);
        Assert.Equal(new Style(p.Tertiary, p.PanelBg, Decoration.Bold), Theme.CodeHeading);
        Assert.Equal(new Style(p.Dimmer), Theme.Placeholder);
    }

    /// <summary>The WCAG 2 contrast ratio of two colours, 1 to 21.</summary>
    internal static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    /// <summary>The pickers' preview (2026-10-02): a palette's styles as <see cref="Theme.Use"/> would give them, the one in force untouched.</summary>
    [Fact]
    public void StylesOf_IsWhatUseWouldGive_AndPutsNothingInForce()
    {
        using var scope = new ThemeScope();
        foreach (var palette in ShippedThemes.All)
        {
            var styles = Theme.StylesOf(palette);
            Assert.Same(ShippedThemes.Synthwave, Theme.Current);

            Theme.Use(palette);
            foreach (var slot in Enum.GetValues<ThemeStyleSlot>())
            {
                Assert.Equal(Theme.Of(slot), styles(slot));
            }

            Theme.Use(ShippedThemes.Synthwave);
        }
    }

    [Fact]
    public void GradientAndRule_OverAPalettesStops_AreTheFormsInForce()
    {
        using var scope = new ThemeScope();
        var other = ShippedThemes.All[1];
        string gradient = Theme.GradientMarkup("NEON", other.GradientStops);
        string rule = Theme.Rule(17, other.GradientStops);

        Theme.Use(other);

        Assert.Equal(Theme.GradientMarkup("NEON"), gradient);
        Assert.Equal(Theme.Rule(17), rule);
    }
}
