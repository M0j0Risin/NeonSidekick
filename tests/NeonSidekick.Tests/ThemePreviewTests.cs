using NeonSidekick.Files;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using NeonSidekick.UI.Markdown;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

/// <summary>The theme pickers' preview beside the list (2026-10-02, the user's ask).</summary>
public class ThemePreviewTests
{
    private const string Banner = "N E O N   S I D E K I C K";

    private static List<Segment> Row(IRenderable line, int width)
    {
        using var console = new TestConsole();
        return line.Render(RenderOptions.Create(console, console.Profile.Capabilities), width).ToList();
    }

    private static string Text(IEnumerable<Segment> row) => string.Concat(row.Select(s => s.Text));

    [Theory]
    [InlineData(72, 40)]
    [InlineData(40, 30)]
    [InlineData(12, 5)]
    public void EveryRow_IsTheWidth_AndEveryCellIsOnABackground(int width, int rows)
    {
        foreach (var palette in ShippedThemes.All)
        {
            var lines = ThemePreview.Lines(palette, width, rows, Banner, "1.2.3");

            Assert.Equal(rows, lines.Count);
            foreach (var line in lines)
            {
                var row = Row(line, width);
                Assert.Equal(width, Segment.CellCount(row));
                Assert.All(row, s => Assert.NotEqual(Color.Default, s.Style.Background));
                Assert.Equal(palette.Bg, row[0].Style.Background);
                Assert.Equal(palette.Bg, row[^1].Style.Background);
            }
        }
    }

    /// <summary>Themed background off (2026-10-03): the card on the terminal's own background; a style's own fill (the code block) stays.</summary>
    [Fact]
    public void TheCard_WithoutThemedBackground_LeavesTheTerminalsOwn()
    {
        var palette = ShippedThemes.Synthwave;
        var rows = ThemePreview.Lines(palette, 72, 60, Banner, "1.2.3", themedBackground: false).Select(line => Row(line, 72)).ToList();

        foreach (var row in rows)
        {
            Assert.Equal(72, Segment.CellCount(row));
            Assert.Equal(Color.Default, row[0].Style.Background);
            Assert.Equal(Color.Default, row[^1].Style.Background);
            Assert.DoesNotContain(row, s => s.Style.Background == palette.Bg);
        }

        Assert.Contains(rows.SelectMany(row => row), s => s.Style.Background == palette.PanelBg);
    }

    [Fact]
    public void ARoomyCard_IsTheWholeScreen_NamingTheTheme_ThenBlankRows()
    {
        var palette = ShippedThemes.All[1];
        var lines = ThemePreview.Lines(palette, 72, 60, Banner, "1.2.3").Select(line => Text(Row(line, 72))).ToList();

        Assert.StartsWith(" " + Banner + "  v1.2.3", lines[0]);
        Assert.Contains(ThemeText.PreviewNotice(palette.Name), string.Join("\n", lines));
        Assert.Contains(ThemeText.PreviewPlaceholder, string.Join("\n", lines));
        Assert.True(string.IsNullOrWhiteSpace(lines[^1]));
    }

    [Fact]
    public void AShortCard_KeepsTheBannerAndTheRuleFirst_InScreenOrder()
    {
        var palette = ShippedThemes.Synthwave;
        var lines = ThemePreview.Lines(palette, 40, 4, Banner, "1.2.3").Select(line => Text(Row(line, 40))).ToList();

        Assert.Equal(4, lines.Count);
        Assert.StartsWith(" " + Banner, lines[0]);
        Assert.Equal(" " + new string('─', 38) + " ", lines[1]);
        Assert.Equal(" " + ThemeText.PreviewUser.PadRight(38) + " ", lines[2]);   // whole in the card's 38 inner cells (cut there until 2026-11-20), the margin after
        Assert.DoesNotContain(lines, string.IsNullOrWhiteSpace);
    }

    [Fact]
    public void TheCard_UsesThePalettesStyles_NotTheOneInForce()
    {
        using var scope = new ThemeScope();
        var other = ShippedThemes.All.First(p => p.Secondary != ShippedThemes.Synthwave.Secondary);
        var lines = ThemePreview.Lines(other, 72, 60, Banner, "1.2.3");

        var user = lines.Select(line => Row(line, 72)).First(row => Text(row).Contains(ThemeText.PreviewUser, StringComparison.Ordinal));
        Assert.Equal(Theme.StylesOf(other)(ThemeStyleSlot.User).Foreground, user.First(s => s.Text == ThemeText.PreviewUser).Style.Foreground);
        Assert.Same(ShippedThemes.Synthwave, Theme.Current);
    }

    [Fact]
    public void TheCard_ShowsAnEditsDiff_ItsRowsOnThePalettesOwnSlabs_ToTheMargin()
    {
        // 2026-10-03, the user's ask: a removed and an added line, as the transcript draws an edit.
        using var scope = new ThemeScope();
        var other = ShippedThemes.All.First(p => p.Good != ShippedThemes.Synthwave.Good && p.Bad != ShippedThemes.Synthwave.Bad);
        var styles = Theme.StylesOf(other);
        var rows = ThemePreview.Lines(other, 72, 60, Banner, "1.2.3").Select(line => Row(line, 72)).ToList();
        var texts = rows.Select(Text).ToList();

        int note = texts.FindIndex(t => t.Contains(ThemeText.PreviewDiffNote, StringComparison.Ordinal));
        Assert.True(note > 0);
        Assert.Contains(DiffView.Elbow + "Added 1 line, removed 1 line", texts[note + 1]);
        Assert.StartsWith(" " + DiffView.Indent + "3 - [string]$last = " + ThemeText.PreviewDiffOld + " ", texts[note + 2]);   // string seal = …; until 2026-11-20
        Assert.StartsWith(" " + DiffView.Indent + "3 + [string]$last = " + ThemeText.PreviewDiffNew + " ", texts[note + 3]);

        foreach (var (row, slot) in new[] { (rows[note + 2], ThemeStyleSlot.DiffRemoved), (rows[note + 3], ThemeStyleSlot.DiffAdded) })
        {
            var slab = styles(slot).Background;
            Assert.NotEqual(Theme.StylesOf(ShippedThemes.Synthwave)(slot).Background, slab);
            // The margin and the indent on the card's own background, then the slab from the number to the right margin.
            var onSlab = row.Skip(2).Take(row.Count - 3).ToList();
            Assert.All(onSlab, s => Assert.Equal(slab, s.Style.Background));
            Assert.Equal(72 - 2 - DiffView.Indent.Length, Segment.CellCount(onSlab));
            Assert.Equal(other.Bg, row[^1].Style.Background);
        }
    }

    [Fact]
    public void NoRows_IsNoLines()
    {
        Assert.Empty(ThemePreview.Lines(ShippedThemes.Synthwave, 40, 0, Banner, "1.2.3"));
    }

    /// <summary>Diff collapse count (2026-10-04, the user's ask): past it the sample diff's summary is the fold's row, unfolded, its rows still drawn.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    public void TheCardsDiff_FollowsDiffCollapseCount_InItsUnfoldedLook(int count, bool folds)
    {
        var texts = ThemePreview.Lines(ShippedThemes.Synthwave, 72, 60, Banner, "1.2.3", diffCollapseCount: count).Select(line => Text(Row(line, 72))).ToList();
        int note = texts.FindIndex(t => t.Contains(ThemeText.PreviewDiffNote, StringComparison.Ordinal));

        string expected = folds
            ? ToolGroupText.ExpandedGlyph + " Added 1 line, removed 1 line · 2 rows"
            : DiffView.Elbow + "Added 1 line, removed 1 line";
        Assert.StartsWith(" " + DiffView.Indent + expected, texts[note + 1]);
        Assert.StartsWith(" " + DiffView.Indent + "3 - ", texts[note + 2]);
        Assert.StartsWith(" " + DiffView.Indent + "3 + ", texts[note + 3]);
    }

    /// <summary>synthwave's gradient with its 15 roles each a color no other role or stop has, and no style overrides: a role's color seen means that role shows.</summary>
    private static ThemePalette Distinct()
    {
        Color C(int i) => new((byte)(3 + i * 11), (byte)(1 + i), (byte)(2 + i));
        return ShippedThemes.Synthwave with
        {
            Primary = C(0), Secondary = C(1), Tertiary = C(2), Deep = C(3), Highlight = C(4), Warm = C(5), Tint = C(6), Ink = C(7), Dim = C(8),
            Dimmer = C(9), Bg = C(10), PanelBg = C(11), Good = C(12), Bad = C(13), Warn = C(14), Styles = null,
        };
    }

    /// <summary>
    /// Every editable color shows in the preview (2026-11-20, the user's question from /theme edit: tint never did, dimmer and deep not in
    /// the editor's card or under Gradient rules): at the whole screen, at the editor's card (22 rows on its 40-row screen, and 30) and an
    /// 18-row card, at the widest side column and the narrowest, under Gradient rules and Themed background each on and off. A role shows
    /// when its color is some cell's ink or ground.
    /// </summary>
    [Theory]
    [InlineData(72, 60)]
    [InlineData(72, 22)]
    [InlineData(72, 18)]
    [InlineData(40, 22)]
    [InlineData(40, 18)]
    public void EveryRole_ShowsInTheCard(int width, int rows)
    {
        var palette = Distinct();
        var roles = new (string Name, Color Color)[]
        {
            ("primary", palette.Primary), ("secondary", palette.Secondary), ("tertiary", palette.Tertiary), ("deep", palette.Deep),
            ("highlight", palette.Highlight), ("warm", palette.Warm), ("tint", palette.Tint), ("ink", palette.Ink), ("dim", palette.Dim),
            ("dimmer", palette.Dimmer), ("bg", palette.Bg), ("panelBg", palette.PanelBg), ("good", palette.Good), ("bad", palette.Bad), ("warn", palette.Warn),
        };
        using var scope = new ThemeScope();
        foreach (bool gradient in new[] { true, false })
        {
            Theme.UseRuleGradient(gradient);
            foreach (bool themed in new[] { true, false })
            {
                var cells = ThemePreview.Lines(palette, width, rows, Banner, "1.2.3", themedBackground: themed).SelectMany(line => Row(line, width)).ToList();
                var seen = cells.SelectMany(s => new[] { s.Style.Foreground, s.Style.Background }).ToHashSet();
                Assert.All(roles, role => Assert.True(seen.Contains(role.Color), $"{role.Name} missing at {width}x{rows}, gradient {gradient}, themed {themed}"));
            }
        }
    }

    /// <summary>The code block is lexed as a reply's PowerShell is (2026-11-20): $variables in tint, the number warm, the string highlight, the keyword primary, the function tertiary.</summary>
    [Fact]
    public void TheCodeBlock_WearsTheLexersKinds()
    {
        var palette = Distinct();
        var styles = Theme.StylesOf(palette);
        var cells = ThemePreview.Lines(palette, 72, 60, Banner, "1.2.3").SelectMany(line => Row(line, 72)).ToList();

        Color Ink(string text) => cells.First(s => s.Text == text).Style.Foreground;
        Assert.Equal("powershell", ThemeText.PreviewCodeLabel);
        Assert.Same(CodeLanguages.PowerShell, CodeLanguages.Find(ThemeText.PreviewCodeLabel));   // the label is the lexer's (#67)
        Assert.Equal(styles(ThemeStyleSlot.CodeType).Foreground, Ink("string"));   // [string]: secondary, the type color (#45)
        Assert.Equal(palette.Secondary, styles(ThemeStyleSlot.CodeType).Foreground);
        Assert.Equal(styles(ThemeStyleSlot.CodeVariable).Foreground, Ink("$t"));
        Assert.Equal(styles(ThemeStyleSlot.CodeVariable).Foreground, Ink("$stops"));
        Assert.Equal(styles(ThemeStyleSlot.CodeNumber).Foreground, Ink("4"));
        Assert.Equal(styles(ThemeStyleSlot.CodeString).Foreground, Ink(ThemeText.PreviewDiffNew));
        Assert.Equal(styles(ThemeStyleSlot.CodeKeyword).Foreground, Ink("function"));
        Assert.Equal(styles(ThemeStyleSlot.CodeFunction).Foreground, Ink("Pick"));
        Assert.Equal(styles(ThemeStyleSlot.CodeComment).Foreground, Ink(ThemeText.PreviewCodeComment));
        Assert.Equal(palette.Tint, styles(ThemeStyleSlot.CodeVariable).Foreground);
    }

    /// <summary>
    /// Every story row fits the narrowest card's 38 inner cells whole, its mark with it (2026-11-20, the memory-remember review's #64, #65); the
    /// prose row keeps its tuned cut, and the app's own lines (the diff note, notices, menu, hint, placeholder) aren't held to it.
    /// </summary>
    [Fact]
    public void EveryStoryRow_FitsTheNarrowestCard()
    {
        string[] rows =
        [
            ThemeText.PreviewUser, ThemeText.PreviewHeading, "  • " + ThemeText.PreviewBullet, "  ▌ " + ThemeText.PreviewQuote,
            ThemeText.PreviewCodeComment, ThemeText.PreviewCodeLine, ThemeText.PreviewCodeLastName + ThemeText.PreviewDiffNew, ThemeText.PreviewThinking,
        ];

        Assert.All(rows, row => Assert.True(TextCells.Width(row) <= 38, row));
    }

    /// <summary>An 18-row card, the editor's smallest, shows the whole code block: its label and three rows (2026-11-20, #29, #41).</summary>
    [Theory]
    [InlineData(72)]
    [InlineData(40)]
    public void AnEighteenRowCard_ShowsTheWholeCodeBlock(int width)
    {
        var texts = ThemePreview.Lines(ShippedThemes.Synthwave, width, 18, Banner, "1.2.3").Select(line => Text(Row(line, width))).ToList();

        Assert.Contains(texts, t => t.Trim() == ThemeText.PreviewCodeLabel);
        Assert.Contains(texts, t => t.Contains(ThemeText.PreviewCodeComment, StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Contains(ThemeText.PreviewCodeLine, StringComparison.Ordinal));
        Assert.Contains(texts, t => t.TrimStart().StartsWith(ThemeText.PreviewCodeLastName + ThemeText.PreviewDiffNew, StringComparison.Ordinal));
    }

    /// <summary>The edit's note tells the truth about its block (2026-11-20): three rows, and the words WorkingDirectory.Count gives them.</summary>
    [Fact]
    public void TheEditsNote_CountsTheBlocksOwnRows()
    {
        string block = ThemeText.PreviewCodeComment + "\n" + ThemeText.PreviewCodeLine + "\n" + ThemeText.PreviewCodeLastName + ThemeText.PreviewDiffNew + "\n";
        WorkingDirectory.Count(block, out int lines, out int words);

        Assert.Equal(ThemeText.PreviewDiffNote, $"edited {ThemeText.PreviewDiffFile} (line {ThemeText.PreviewDiffLine}; now {lines} lines, {words} words)");
        Assert.True(TextCells.Width(ThemeText.PreviewCodeLine) <= 38);   // whole on the narrowest card
    }
}
