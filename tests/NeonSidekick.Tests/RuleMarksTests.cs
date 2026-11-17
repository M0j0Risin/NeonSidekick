using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.Tests;

/// <summary><c>/mark</c>'s colors (2026-10-18, <see cref="RuleMarks"/>): the names and their order, the parse, and a shade that reads on every built-in theme.</summary>
public sealed class RuleMarksTests
{
    [Fact]
    public void TheNames_AreTen_InTheUsersOrder()
    {
        Assert.Equal(["red", "orange", "yellow", "green", "cyan", "blue", "purple", "pink", "white", "gray"], RuleMarks.Names);
        Assert.Equal("default", RuleMarks.Default);
    }

    [Theory]
    [InlineData("red", "red")]
    [InlineData(" GREEN ", "green")]
    [InlineData("grey", "gray")]
    [InlineData("Grey", "gray")]
    [InlineData("gray", "gray")]
    public void TryParse_TrimsFoldsCase_AndReadsGreyAsGray(string word, string expected)
    {
        Assert.True(RuleMarks.TryParse(word, out string name));
        Assert.Equal(expected, name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("default")]
    [InlineData("#ff0000")]
    [InlineData("magenta")]
    [InlineData(null)]
    public void TryParse_RefusesAnythingElse(string? word)
    {
        Assert.False(RuleMarks.TryParse(word, out string name));
        Assert.Equal("", name);
    }

    [Fact]
    public void EveryColor_IsDistinct_AndHasAStyle()
    {
        var synthwave = Fakes.ShippedThemes.Synthwave;
        var paper = ThemeLibrary.All.Single(t => t.Name == "paper");
        Assert.Equal(RuleMarks.Names.Count, RuleMarks.Names.Select(n => RuleMarks.ColorOf(n)).Distinct().Count());
        Assert.Equal(RuleMarks.Names.Count, RuleMarks.Names.Select(n => RuleMarks.ColorOf(n, light: true)).Distinct().Count());
        foreach (string name in RuleMarks.Names)
        {
            Assert.Equal(RuleMarks.ColorOf(name), RuleMarks.StyleFor(name, synthwave.Bg, synthwave.Ink)!.Value.Foreground);
            Assert.Equal(RuleMarks.ColorOf(name, light: true), RuleMarks.StyleFor(name, paper.Bg, paper.Ink)!.Value.Foreground);
        }

        Assert.Null(RuleMarks.StyleFor(null, synthwave.Bg, synthwave.Ink));
        Assert.Null(RuleMarks.StyleFor("magenta", synthwave.Bg, synthwave.Ink));
    }

    [Fact]
    public void EveryColor_ReadsOnEveryBuiltInTheme()
    {
        foreach (var theme in ThemeLibrary.All)
        {
            foreach (string name in RuleMarks.Names)
            {
                double ratio = ThemeTests.Contrast(RuleMarks.StyleFor(name, theme.Bg, theme.Ink)!.Value.Foreground, theme.Bg);
                Assert.True(ratio >= 4.5, $"{name} on {theme.Name}: {ratio:0.00}:1");
            }
        }
    }

    /// <summary>
    /// The advisors' and Claude's names read on every built-in (2026-11-17, the admin-themes-render review's #153): the deeper shade on a
    /// light page, the bright one on a dark page.
    /// </summary>
    [Fact]
    public void TheAdvisorAndClaudeNames_ReadOnTheDaylightThemes()
    {
        using var scope = new Fakes.ThemeScope();
        foreach (var theme in ThemeLibrary.All.Where(t => ColorContrast.IsLight(t.Bg)))
        {
            Theme.Use(theme);
            Assert.True(Theme.PageIsLight, theme.Name);
            Assert.True(ThemeTests.Contrast(App.ChatScreen.AdvisorColor, theme.Bg) >= 4.5, theme.Name);
            Assert.True(ThemeTests.Contrast(App.ChatScreen.ClaudeColor, theme.Bg) >= 4.5, theme.Name);
        }

        Theme.UseThemedBackground(false);   // the terminal's own page, taken as dark
        Assert.False(Theme.PageIsLight);
        Assert.Equal(App.ChatScreen.AdvisorDarkColor, App.ChatScreen.AdvisorColor);
    }

    /// <summary>
    /// A custom mid-tone ground (2026-11-17, the admin-themes-render review's #122): a mark under 4.5:1 is moved toward the ink until it reads,
    /// and one already over comes back as the table has it.
    /// </summary>
    [Fact]
    public void OnAMidToneGround_EveryColorIsLiftedToTheFloor_AndNoFurther()
    {
        var ground = new Color(0x8A, 0x8A, 0x8A);
        var ink = new Color(0, 0, 0);
        bool light = ColorContrast.IsLight(ground);
        foreach (string name in RuleMarks.Names)
        {
            var drawn = RuleMarks.StyleFor(name, ground, ink)!.Value.Foreground;
            Assert.True(ThemeTests.Contrast(drawn, ground) >= RuleMarks.Target, $"{name}: {ThemeTests.Contrast(drawn, ground):0.00}:1");
            var table = RuleMarks.ColorOf(name, light);
            if (ThemeTests.Contrast(table, ground) >= RuleMarks.Target)
            {
                Assert.Equal(table, drawn);
            }
        }
    }

    [Fact]
    public void TheWords_ArePinned()
    {
        Assert.Equal("No color named 'teal': /mark takes red, orange, yellow, green, cyan, blue, purple, pink, white, gray or default", MarkText.UnknownError("teal"));
        Assert.Equal("The rules around the draft line are green now; /mark default puts the theme's back.", MarkText.SetNotice("green"));
        Assert.Equal("The rules around the draft line are the theme's again.", MarkText.DefaultNotice);
        Assert.Equal("The rules are the theme's; /mark takes red, orange, yellow, green, cyan, blue, purple, pink, white, gray or default.", MarkText.ShowLine(null));
        Assert.StartsWith("The rules are pink; ", MarkText.ShowLine("pink"));
    }
}
