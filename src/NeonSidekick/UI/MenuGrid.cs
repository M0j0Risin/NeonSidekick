using Spectre.Console;

namespace NeonSidekick.UI;

/// <summary>
/// One line of a <see cref="MenuGrid"/> page as laid out: a heading row (<see cref="Heading"/>, its index into the rows), a gap row
/// (<see cref="Gap"/>, the blank before a heading), or a run of <see cref="Count"/> cells starting at row <see cref="First"/>.
/// </summary>
public readonly record struct MenuGridLine(int First, int Count, int Heading = -1, bool Gap = false)
{
    /// <summary>Whether the line holds cells the cursor can rest on.</summary>
    public bool IsCells => Heading < 0 && !Gap;
}

/// <summary>
/// A page whose rows are cells laid out left to right in lines (2026-10-10, the avatar picker: 1,600 emoji one a row would be a
/// list nobody scrolls), each <see cref="CellWidth"/> cells wide; a heading row (<see cref="MenuPage.Headings"/>) breaks the flow with
/// a rule of its own, a gap row (<c>""</c>) a blank line. ←/→ step a cell and run on across lines, ↑/↓ keep the column on the line
/// above or below; tabs then step with Tab and Shift+Tab alone (<see cref="MenuPane"/>). The geometry is here, pure; the pane
/// draws and reads keys.
/// </summary>
public sealed record MenuGrid(int CellWidth)
{
    /// <summary>What the cursor's cell starts with in place of its leading blank: the list's pointer, one cell. Pinned.</summary>
    public const string CursorMark = "▸";

    /// <summary>The cells a line of <paramref name="width"/> holds, past the pointer's indent; at least one.</summary>
    public int Columns(int width) => Math.Max(1, (width - TextCells.Width(MenuPane.NoPointer)) / Math.Max(1, CellWidth));

    /// <summary>
    /// <paramref name="count"/> rows laid out <paramref name="columns"/> to a line: each heading and each gap (an empty row on a page
    /// with headings, <paramref name="isGap"/>) a line of its own, every run of cells between them cut into lines. Pure.
    /// </summary>
    public static IReadOnlyList<MenuGridLine> Lay(int count, IReadOnlySet<int>? headings, Func<int, bool> isGap, int columns)
    {
        ArgumentNullException.ThrowIfNull(isGap);
        columns = Math.Max(1, columns);
        var lines = new List<MenuGridLine>();
        int run = -1;
        for (int r = 0; r < count; r++)
        {
            bool heading = headings?.Contains(r) ?? false;
            bool gap = !heading && headings is not null && isGap(r);
            if (heading || gap)
            {
                Flush(r);
                lines.Add(heading ? new MenuGridLine(r, 0, Heading: r) : new MenuGridLine(r, 0, Gap: true));
                continue;
            }

            if (run < 0)
            {
                run = r;
            }
        }

        Flush(count);
        return lines;

        void Flush(int end)
        {
            for (int start = run; run >= 0 && start < end; start += columns)
            {
                lines.Add(new MenuGridLine(start, Math.Min(columns, end - start)));
            }

            run = -1;
        }
    }

    /// <summary>The index of the line holding <paramref name="row"/>; 0 when none does.</summary>
    public static int LineOf(IReadOnlyList<MenuGridLine> lines, int row)
    {
        ArgumentNullException.ThrowIfNull(lines);
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.IsCells ? row >= line.First && row < line.First + line.Count : row == line.First)
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>
    /// The row ↑ (<paramref name="step"/> −1) or ↓ (+1) takes the cursor from <paramref name="row"/> to, <paramref name="lines"/>
    /// lines at a time (the page keys pass a view's worth): the cell in the same column on the cell line that far away, past headings
    /// and gaps, the line's last when it is shorter. Wraps past the ends when <paramref name="wrap"/>, else stops at the last cell
    /// line that way. <paramref name="row"/> itself when the page has no other cell line. Pure.
    /// </summary>
    public static int Vertical(IReadOnlyList<MenuGridLine> layout, int row, int step, bool wrap, int lines = 1)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var cells = Enumerable.Range(0, layout.Count).Where(i => layout[i].IsCells).ToList();
        if (cells.Count == 0)
        {
            return row;
        }

        int here = LineOf(layout, row);
        int at = cells.IndexOf(here);
        if (at < 0)
        {
            return layout[cells[0]].First;
        }

        int column = row - layout[here].First;
        int target = at + (step < 0 ? -1 : 1) * Math.Max(1, lines);
        target = wrap ? ((target % cells.Count) + cells.Count) % cells.Count : Math.Clamp(target, 0, cells.Count - 1);
        var line = layout[cells[target]];
        return line.First + Math.Min(column, line.Count - 1);
    }

    /// <summary>
    /// The row a click lands on: <paramref name="x"/> (the window's column) on <paramref name="line"/>, past the pointer's indent;
    /// null on a heading, a gap, the indent or past the line's last cell. Pure.
    /// </summary>
    public int? CellAt(MenuGridLine line, int x)
    {
        int offset = x - TextCells.Width(MenuPane.NoPointer);
        if (!line.IsCells || offset < 0)
        {
            return null;
        }

        int column = offset / Math.Max(1, CellWidth);
        return column < line.Count ? line.First + column : null;
    }

    /// <summary>
    /// One cell line as markup: the pointer's indent, then each cell <see cref="CellWidth"/> wide — a blank, the cell's markup, blanks
    /// to the width — the cursor's on <see cref="Theme.MenuHighlight"/> and a marked one (<paramref name="marks"/>) on its style's slab,
    /// so the slab shows behind an emoji, which no dimming does. Pure.
    /// </summary>
    public string LineMarkup(MenuGridLine line, IReadOnlyList<string> rows, int cursor, IReadOnlyDictionary<int, Style>? marks)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var markup = new System.Text.StringBuilder(MenuPane.NoPointer);
        for (int r = line.First; r < line.First + line.Count; r++)
        {
            string cell = rows[r];
            int pad = Math.Max(0, CellWidth - 1 - TextCells.Width(Markup.Remove(cell)));
            // The cursor's cell leads with the list's pointer too (the first look in Windows Terminal: a highlight four cells wide
            // behind a picture read as nothing).
            string body = (r == cursor ? CursorMark : " ") + cell + new string(' ', pad);
            Style? style = r == cursor ? Theme.MenuHighlight : marks is not null && marks.TryGetValue(r, out var mark) ? (Style?)mark : null;
            markup.Append(style is { } s ? $"[{s.ToMarkup()}]{body}[/]" : body);
        }

        return markup.ToString();
    }

    /// <summary>
    /// The cells in lines <paramref name="first"/> to <paramref name="first"/> + <paramref name="shown"/> of <paramref name="layout"/>,
    /// as the more row counts them: how many come before, how many are shown, how many there are. Pure.
    /// </summary>
    public static (int Before, int Shown, int Total) CellSpan(IReadOnlyList<MenuGridLine> layout, int first, int shown)
    {
        ArgumentNullException.ThrowIfNull(layout);
        int before = layout.Take(first).Where(l => l.IsCells).Sum(l => l.Count);
        int inView = layout.Skip(first).Take(shown).Where(l => l.IsCells).Sum(l => l.Count);
        return (before, inView, layout.Where(l => l.IsCells).Sum(l => l.Count));
    }
}
