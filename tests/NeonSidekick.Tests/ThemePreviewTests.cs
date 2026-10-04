using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
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
        foreach (var palette in ThemePalette.All)
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
        var palette = ThemePalette.Synthwave;
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
        var palette = ThemePalette.All[1];
        var lines = ThemePreview.Lines(palette, 72, 60, Banner, "1.2.3").Select(line => Text(Row(line, 72))).ToList();

        Assert.StartsWith(" " + Banner + "  v1.2.3", lines[0]);
        Assert.Contains(ThemeText.PreviewNotice(palette.Name), string.Join("\n", lines));
        Assert.Contains(ThemeText.PreviewPlaceholder, string.Join("\n", lines));
        Assert.True(string.IsNullOrWhiteSpace(lines[^1]));
    }

    [Fact]
    public void AShortCard_KeepsTheBannerAndTheRuleFirst_InScreenOrder()
    {
        var palette = ThemePalette.Synthwave;
        var lines = ThemePreview.Lines(palette, 40, 4, Banner, "1.2.3").Select(line => Text(Row(line, 40))).ToList();

        Assert.Equal(4, lines.Count);
        Assert.StartsWith(" " + Banner, lines[0]);
        Assert.Equal(" " + new string('─', 38) + " ", lines[1]);
        Assert.Equal(" " + ThemeText.PreviewUser[..38] + " ", lines[2]);   // cut at the card's 38 inner cells, the margin after
        Assert.DoesNotContain(lines, string.IsNullOrWhiteSpace);
    }

    [Fact]
    public void TheCard_UsesThePalettesStyles_NotTheOneInForce()
    {
        using var scope = new ThemeScope();
        var other = ThemePalette.All.First(p => p.Secondary != ThemePalette.Synthwave.Secondary);
        var lines = ThemePreview.Lines(other, 72, 60, Banner, "1.2.3");

        var user = lines.Select(line => Row(line, 72)).First(row => Text(row).Contains(ThemeText.PreviewUser, StringComparison.Ordinal));
        Assert.Equal(Theme.StylesOf(other)(ThemeStyleSlot.User).Foreground, user.First(s => s.Text == ThemeText.PreviewUser).Style.Foreground);
        Assert.Same(ThemePalette.Synthwave, Theme.Current);
    }

    [Fact]
    public void TheCard_ShowsAnEditsDiff_ItsRowsOnThePalettesOwnSlabs_ToTheMargin()
    {
        // 2026-10-03, the user's ask: a removed and an added line, as the transcript draws an edit.
        using var scope = new ThemeScope();
        var other = ThemePalette.All.First(p => p.Good != ThemePalette.Synthwave.Good && p.Bad != ThemePalette.Synthwave.Bad);
        var styles = Theme.StylesOf(other);
        var rows = ThemePreview.Lines(other, 72, 60, Banner, "1.2.3").Select(line => Row(line, 72)).ToList();
        var texts = rows.Select(Text).ToList();

        int note = texts.FindIndex(t => t.Contains(ThemeText.PreviewDiffNote, StringComparison.Ordinal));
        Assert.True(note > 0);
        Assert.Contains(DiffView.Elbow + "Added 1 line, removed 1 line", texts[note + 1]);
        Assert.StartsWith(" " + DiffView.Indent + "3 - string seal = " + ThemeText.PreviewDiffOld + ";", texts[note + 2]);
        Assert.StartsWith(" " + DiffView.Indent + "3 + string seal = " + ThemeText.PreviewDiffNew + ";", texts[note + 3]);

        foreach (var (row, slot) in new[] { (rows[note + 2], ThemeStyleSlot.DiffRemoved), (rows[note + 3], ThemeStyleSlot.DiffAdded) })
        {
            var slab = styles(slot).Background;
            Assert.NotEqual(Theme.StylesOf(ThemePalette.Synthwave)(slot).Background, slab);
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
        Assert.Empty(ThemePreview.Lines(ThemePalette.Synthwave, 40, 0, Banner, "1.2.3"));
    }

    /// <summary>Diff collapse count (2026-10-04, the user's ask): past it the sample diff's summary is the fold's row, unfolded, its rows still drawn.</summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(1, true)]
    public void TheCardsDiff_FollowsDiffCollapseCount_InItsUnfoldedLook(int count, bool folds)
    {
        var texts = ThemePreview.Lines(ThemePalette.Synthwave, 72, 60, Banner, "1.2.3", diffCollapseCount: count).Select(line => Text(Row(line, 72))).ToList();
        int note = texts.FindIndex(t => t.Contains(ThemeText.PreviewDiffNote, StringComparison.Ordinal));

        string expected = folds
            ? ToolGroupText.ExpandedGlyph + " Added 1 line, removed 1 line · 2 rows"
            : DiffView.Elbow + "Added 1 line, removed 1 line";
        Assert.StartsWith(" " + DiffView.Indent + expected, texts[note + 1]);
        Assert.StartsWith(" " + DiffView.Indent + "3 - ", texts[note + 2]);
        Assert.StartsWith(" " + DiffView.Indent + "3 + ", texts[note + 3]);
    }
}
