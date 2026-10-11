using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.Tests;

/// <summary>
/// The grid layout of a menu page (2026-10-10, the avatar picker): cells in lines of the width, a heading or a gap a line of its own,
/// the arrows' moves, a click's cell, the cursor's and a marked cell's slabs, and the more row's count in cells.
/// </summary>
public sealed class MenuGridTests
{
    // Rows: 0 heading, 1-2 cells, 3 gap, 4 heading, 5-11 cells.
    private static readonly HashSet<int> s_headings = [0, 4];

    private static bool Gap(int row) => row == 3;

    private static IReadOnlyList<MenuGridLine> Layout(int columns) => MenuGrid.Lay(12, s_headings, Gap, columns);

    [Fact]
    public void Columns_AreTheWidthPastThePointer_ByTheCellWidth()
    {
        var grid = new MenuGrid(4);
        Assert.Equal(19, grid.Columns(80));   // 78 cells past the pointer's two, four a cell
        Assert.Equal(1, grid.Columns(3));
    }

    [Fact]
    public void Lay_PutsHeadingsAndGaps_OnLinesOfTheirOwn_AndCutsTheRuns()
    {
        Assert.Equal(
            [
                new MenuGridLine(0, 0, Heading: 0),
                new MenuGridLine(1, 2),
                new MenuGridLine(3, 0, Gap: true),
                new MenuGridLine(4, 0, Heading: 4),
                new MenuGridLine(5, 3),
                new MenuGridLine(8, 3),
                new MenuGridLine(11, 1),
            ],
            Layout(3));
    }

    [Fact]
    public void Lay_WithoutHeadings_IsCellsAlone_AnEmptyRowACellToo()
    {
        Assert.Equal([new MenuGridLine(0, 2), new MenuGridLine(2, 1)], MenuGrid.Lay(3, null, _ => true, 2));
    }

    [Fact]
    public void UpAndDown_KeepTheColumn_PastHeadings_AndWrap()
    {
        var layout = Layout(3);
        Assert.Equal(9, MenuGrid.Vertical(layout, 6, +1, wrap: true));     // column 1 of 5-7 → column 1 of 8-10
        Assert.Equal(11, MenuGrid.Vertical(layout, 10, +1, wrap: true));   // column 2 → the short last line's only cell
        Assert.Equal(6, MenuGrid.Vertical(layout, 2, +1, wrap: true));     // past the gap and the heading, column 1 kept
        Assert.Equal(2, MenuGrid.Vertical(layout, 6, -1, wrap: true));
        Assert.Equal(11, MenuGrid.Vertical(layout, 1, -1, wrap: true));    // up from the top wraps to the last line
        Assert.Equal(1, MenuGrid.Vertical(layout, 1, -1, wrap: false));    // the page keys stop
        Assert.Equal(11, MenuGrid.Vertical(layout, 1, +1, wrap: false, lines: 10));
    }

    [Fact]
    public void LineOf_FindsTheRowsLine()
    {
        var layout = Layout(3);
        Assert.Equal(1, MenuGrid.LineOf(layout, 2));
        Assert.Equal(5, MenuGrid.LineOf(layout, 9));
        Assert.Equal(3, MenuGrid.LineOf(layout, 4));
    }

    [Fact]
    public void CellAt_MapsAColumn_ToACell()
    {
        var grid = new MenuGrid(4);
        var line = new MenuGridLine(5, 3);
        Assert.Null(grid.CellAt(line, 1));    // the pointer's indent
        Assert.Equal(5, grid.CellAt(line, 2));
        Assert.Equal(5, grid.CellAt(line, 5));
        Assert.Equal(6, grid.CellAt(line, 6));
        Assert.Equal(7, grid.CellAt(line, 13));
        Assert.Null(grid.CellAt(line, 14));   // past the line's last cell
        Assert.Null(grid.CellAt(new MenuGridLine(0, 0, Heading: 0), 4));
    }

    [Fact]
    public void LineMarkup_PadsEveryCell_AndPutsTheCursorAndAMark_OnSlabs()
    {
        var grid = new MenuGrid(4);
        var rows = new List<string> { "🦊", "🐙", "👍🏽" };
        var marks = new Dictionary<int, Style> { [1] = Theme.ToolbarOff };
        string markup = grid.LineMarkup(new MenuGridLine(0, 3), rows, cursor: 0, marks);
        Assert.Equal(MenuPane.NoPointer + MenuGrid.CursorMark + "🦊  🐙  👍🏽 ", Markup.Remove(markup));
        Assert.Equal(2 + 3 * 4, TextCells.Width(Markup.Remove(markup)));
        Assert.StartsWith(MenuPane.NoPointer + "[" + Theme.MenuHighlight.ToMarkup() + "]" + MenuGrid.CursorMark + "🦊 [/]", markup);
        Assert.Contains("[" + Theme.ToolbarOff.ToMarkup() + "] 🐙 [/]", markup);
    }

    [Fact]
    public void CellSpan_CountsCells_NotLines()
    {
        var layout = Layout(3);
        Assert.Equal((2, 6, 9), MenuGrid.CellSpan(layout, 2, 4));   // lines 2-5: the gap, the heading, 5-7 and 8-10
    }

    [Fact]
    public void APaste_GoesOnTheFilter_CutBetweenTextElements()
    {
        Assert.Equal("🦊", MenuFilter.Pasted("", " 🦊"));
        Assert.Equal("fox 🦊", MenuFilter.Pasted("fox", " 🦊"));
        Assert.Equal("a b", MenuFilter.Pasted("a", "\nb"));
        Assert.Null(MenuFilter.Pasted("", "   "));
        Assert.Null(MenuFilter.Pasted(new string('x', MenuFilter.MaxLength), "y"));
        Assert.Null(MenuFilter.Pasted(new string('x', MenuFilter.MaxLength - 1), "👨‍👩‍👧"));   // never half an emoji: the whole one is past the cap
    }

    [Fact]
    public void Backspace_TakesAWholeEmoji()
    {
        var backspace = new ConsoleKeyInfo('\b', ConsoleKey.Backspace, false, false, false);
        Assert.Equal("fox ", MenuFilter.Edit("fox 👨‍👩‍👧", backspace, spaceToggles: false));
        Assert.Equal("", MenuFilter.Edit("👍🏽", backspace, spaceToggles: false));
        Assert.Equal("fo", MenuFilter.Edit("fox", backspace, spaceToggles: false));
    }
}
