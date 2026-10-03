using NeonSidekick.UI;
using Spectre.Console;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

/// <summary>The heading rule of <c>/tools</c>, <c>/sys</c> and <c>/mcp</c> (2026-10-03, the user's call).</summary>
public class SectionRuleTests
{
    private static string Rule(int width) => new(ScreenPane.RuleGlyph, width);

    private static string Render(string markup, int width)
    {
        using var console = new TestConsole();
        console.Profile.Width = width;
        console.Write(new SectionRule(markup));
        return console.Output.TrimEnd('\n');
    }

    [Fact]
    public void Markup_IsTheLeadTheLabelTheCountAndTheNote_InTheirStyles_Escaped()
    {
        string rule = Theme.PaneRule.ToMarkup();
        string label = $"[{rule}]──[/] [{Theme.SectionHeading.ToMarkup()}]Files[/]";
        Assert.Equal(label, SectionRule.Markup("Files"));
        Assert.Equal(label + Theme.DimMarkup(" · 12 of 14"), SectionRule.Markup("Files", "12 of 14"));
        Assert.Equal(label + Theme.DimMarkup(" · 14") + $" [{rule}]──[/] " + Theme.DimMarkup("off: File tools is off"), SectionRule.Markup("Files", "14", "off: File tools is off"));
        Assert.Equal(label + $" [{rule}]──[/] " + Theme.DimMarkup("default"), SectionRule.Markup("Files", note: "default"));
        Assert.Equal(label, SectionRule.Markup("Files", "", ""));
        Assert.Equal("── a[b] · [1] ── [x]", Markup.Remove(SectionRule.Markup("a[b]", "[1]", "[x]")));   // escaped, not parsed
        Assert.Equal("──", SectionRule.Lead);
        Assert.Equal(" · ", SectionRule.CountSeparator);
    }

    [Fact]
    public void Render_FillsToTheEdge_OrCutsWithAnEllipsis_WithNoFill()
    {
        Assert.Equal("── Clock · 3 " + Rule(60 - 13), Render(SectionRule.Markup("Clock", "3"), 60));
        Assert.Equal(60, TextCells.Width(Render(SectionRule.Markup("Clock", "3"), 60)));
        // Exactly as wide as the room, or one short: no fill (a space alone would be a stray cell).
        Assert.Equal("── Clock · 3", Render(SectionRule.Markup("Clock", "3"), 12));
        Assert.Equal("── Clock · 3", Render(SectionRule.Markup("Clock", "3"), 13));
        Assert.Equal("── Clock · 3 ─", Render(SectionRule.Markup("Clock", "3"), 14));
        // Wider than the room: cut, an ellipsis, nothing after it.
        Assert.Equal("── Files · 14 ── off: File…", Render(SectionRule.Markup("Files", "14", "off: File tools is off"), 27));
    }

    [Fact]
    public void Render_DrawsTheFillInThePaneRuleColour()
    {
        using var console = new TestConsole();
        console.Profile.Width = 30;
        console.EmitAnsiSequences();
        console.Write(new SectionRule(SectionRule.Markup("Clock")));
        using var plain = new TestConsole();
        plain.EmitAnsiSequences();
        plain.Write(new Text(Rule(30 - 9), Theme.PaneRule));
        Assert.Contains(plain.Output.TrimEnd('\n'), console.Output);
    }
}
