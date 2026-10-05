using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.Tests;

public class ThemeTests
{
    [Fact]
    public void ToHex_RendersUppercaseSixDigitHex()
    {
        Assert.Equal("#FF2E97", Theme.ToHex(Theme.Primary));
        Assert.Equal("#0B0416", Theme.ToHex(Theme.Bg));
    }

    [Fact]
    public void Palette_IsPinned()
    {
        Assert.Equal("#33E0FF", Theme.ToHex(Theme.Secondary));
        Assert.Equal("#B15BFF", Theme.ToHex(Theme.Tertiary));
        Assert.Equal("#7B2FF7", Theme.ToHex(Theme.Deep));
        Assert.Equal("#FFC832", Theme.ToHex(Theme.Highlight));
        Assert.Equal("#FF8A3D", Theme.ToHex(Theme.Warm));
        Assert.Equal("#F45B9B", Theme.ToHex(Theme.Tint));
        Assert.Equal("#EFE6FF", Theme.ToHex(Theme.Ink));
        Assert.Equal("#9A8BB8", Theme.ToHex(Theme.Dim));
        Assert.Equal("#443C56", Theme.ToHex(Theme.Dimmer));
        Assert.Equal(Theme.Dimmer, Theme.Placeholder.Foreground);
        Assert.Equal("#160A28", Theme.ToHex(Theme.PanelBg));
        Assert.Equal("#3DF27A", Theme.ToHex(Theme.Good));
        Assert.Equal("#FF4D6D", Theme.ToHex(Theme.Bad));
        Assert.Equal(Theme.Highlight, Theme.Warn);
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
    public void Synthwave_IsInForceByDefault_WithItsGradient()
    {
        using var scope = new ThemeScope();
        Assert.Same(ThemePalette.Synthwave, Theme.Current);
        Assert.Equal(new[] { "#33E0FF", "#B15BFF", "#FF2E97", "#FF8A3D", "#FFC832" }, ThemePalette.Synthwave.GradientStops.Select(Theme.ToHex));
        Assert.Equal("synthwave", ThemePalette.All[0].Name);
    }

    [Fact]
    public void Use_SwapsEveryColourAndStyle_AndSynthwaveComesBack()
    {
        using var scope = new ThemeScope();
        Theme.Use(ThemePalette.Netrunner);

        Assert.Same(ThemePalette.Netrunner, Theme.Current);
        Assert.Equal("#00FF41", Theme.ToHex(Theme.Primary));
        Assert.Equal(ThemePalette.Netrunner.Secondary, Theme.User.Foreground);
        Assert.Equal(ThemePalette.Netrunner.Deep, Theme.PaneRule.Foreground);
        Assert.Equal(ThemePalette.Netrunner.PanelBg, Theme.CodeString.Background);
        Assert.Equal(ThemePalette.Netrunner.Highlight, Theme.CodeString.Foreground);
        Assert.Equal("[#00FF41 bold]x[/]", Theme.AccentMarkup("x"));
        Assert.StartsWith("[#0F6B2E]a[/]", Theme.GradientMarkup("ab"));

        Theme.Use(ThemePalette.Synthwave);
        Assert.Equal("#FF2E97", Theme.ToHex(Theme.Primary));
        Assert.Equal(ThemePalette.Synthwave.Secondary, Theme.User.Foreground);
    }

    [Fact]
    public void Noir_IsGreyscale_ButForTheRedOfFailures()
    {
        var noir = ThemePalette.Noir;
        Color[] rest = [noir.Primary, noir.Secondary, noir.Tertiary, noir.Deep, noir.Highlight, noir.Warm, noir.Tint, noir.Ink, noir.Dim, noir.Dimmer, noir.Bg, noir.PanelBg, noir.Good, noir.Warn, .. noir.GradientStops];
        Assert.All(rest, c => Assert.True(c.R == c.G && c.G == c.B, $"{Theme.ToHex(c)} is not a grey"));
        Assert.True(noir.Bad.R > noir.Bad.G && noir.Bad.R > noir.Bad.B);   // the user's call: grey plus a muted red for errors
    }

    public static TheoryData<string> Palettes() => new(ThemePalette.All.Select(p => p.Name));

    [Theory]
    [MemberData(nameof(Palettes))]
    public void EveryPalette_IsReadable_AndKeepsItsRolesApart(string name)
    {
        var p = ThemePalette.All.Single(t => t.Name == name);
        Assert.Equal(name, name.ToLowerInvariant());
        Assert.NotEmpty(p.Description);
        Assert.Equal(5, p.GradientStops.Length);
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
        Theme.Use(ThemePalette.Synthwave with
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
        Assert.Equal(ThemePalette.Synthwave.Tertiary, Theme.MarkdownHeading.Foreground);   // H2 off the secondary since 2026-10-04 (the tertiary's)
        Assert.Equal(Theme.MarkdownBold, Theme.MarkdownHeading3);                         // H3 and below: the body ink, bold
        Assert.Equal(red, Theme.SpinnerStyle.Foreground);        // changed itself
        Assert.Equal(Decoration.None, Theme.SpinnerStyle.Decoration);   // over its source's final style
        Assert.Equal(ThemePalette.Synthwave.Dim, Theme.CodeComment.Foreground);
        Assert.Equal(red, Theme.CodeComment.Background);
        Assert.Equal(Decoration.Underline, Theme.CodeComment.Decoration);
        Assert.Equal(ThemePalette.Synthwave.Secondary, Theme.TableHeader.Foreground);   // no alias: untouched
        Assert.Equal(ThemePalette.Synthwave.Secondary, Theme.Secondary);                // the palette colour stays
    }

    [Fact]
    public void Synthwave_StylesAreTheOriginalCompositions()
    {
        using var scope = new ThemeScope();
        var p = ThemePalette.Synthwave;
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
        foreach (var palette in ThemePalette.All)
        {
            var styles = Theme.StylesOf(palette);
            Assert.Same(ThemePalette.Synthwave, Theme.Current);

            Theme.Use(palette);
            foreach (var slot in Enum.GetValues<ThemeStyleSlot>())
            {
                Assert.Equal(Theme.Of(slot), styles(slot));
            }

            Theme.Use(ThemePalette.Synthwave);
        }
    }

    [Fact]
    public void GradientAndRule_OverAPalettesStops_AreTheFormsInForce()
    {
        using var scope = new ThemeScope();
        var other = ThemePalette.All[1];
        string gradient = Theme.GradientMarkup("NEON", other.GradientStops);
        string rule = Theme.Rule(17, other.GradientStops);

        Theme.Use(other);

        Assert.Equal(Theme.GradientMarkup("NEON"), gradient);
        Assert.Equal(Theme.Rule(17), rule);
    }
}
