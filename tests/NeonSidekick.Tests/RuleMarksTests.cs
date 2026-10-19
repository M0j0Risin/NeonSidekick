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
        Assert.Equal(RuleMarks.Names.Count, RuleMarks.Names.Select(RuleMarks.ColorOf).Distinct().Count());
        foreach (string name in RuleMarks.Names)
        {
            Assert.Equal(RuleMarks.ColorOf(name), RuleMarks.StyleOf(name)!.Value.Foreground);
        }

        Assert.Null(RuleMarks.StyleOf(null));
        Assert.Null(RuleMarks.StyleOf("magenta"));
    }

    [Fact]
    public void EveryColor_ReadsOnEveryBuiltInTheme()
    {
        foreach (var theme in ThemeLibrary.All)
        {
            foreach (string name in RuleMarks.Names)
            {
                double ratio = ThemeTests.Contrast(RuleMarks.ColorOf(name), theme.Bg);
                Assert.True(ratio >= 4.5, $"{name} on {theme.Name}: {ratio:0.00}:1");
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
