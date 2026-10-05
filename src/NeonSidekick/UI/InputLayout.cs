namespace NeonSidekick.UI;

/// <summary>
/// The input area's rows: <paramref name="Rows"/> is the draft word-wrapped to the row budget,
/// <paramref name="Starts"/> each row's first UTF-16 index in the draft (a row's end is the next
/// start, less one when a breaking space was dropped between them), <paramref name="CursorRow"/> /
/// <paramref name="CursorCol"/> where the cursor sits in them (the column in cells, after the glyph
/// or the indent). Pure; the pane applies the height cap and the vertical viewport on top.
/// </summary>
public sealed record InputLayout(IReadOnlyList<string> Rows, IReadOnlyList<int> Starts, int CursorRow, int CursorCol)
{
    /// <summary>
    /// Word-wraps <paramref name="text"/> into rows of at most <paramref name="availableCells"/>
    /// cells: a row breaks at its last space (the breaking space is dropped from the row; the next
    /// row starts right after it), a word longer than a row breaks by cells, never inside a wide
    /// character or a surrogate pair; an element wider than an empty row is placed anyway. The
    /// cursor (a UTF-16 index, clamped) lands on the last row that starts at or before it, so a
    /// cursor right after a breaking space is at the next row's first cell and a cursor on the space
    /// itself at its row's end. A <c>'\n'</c> (a pasted line break) is a hard break: the row ends
    /// before it and the next starts after it, the same as a dropped breaking space, so two of them
    /// make an empty row. Empty text is one empty row.
    /// </summary>
    public static InputLayout Wrap(string text, int cursor, int availableCells)
    {
        ArgumentNullException.ThrowIfNull(text);
        cursor = Math.Clamp(cursor, 0, text.Length);
        availableCells = Math.Max(1, availableCells);

        var rows = new List<string>();
        var starts = new List<int> { 0 };
        int rowStart = 0;
        int cells = 0;
        int lastSpace = -1;
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '\n')
            {
                rows.Add(text[rowStart..i]);
                rowStart = i + 1;
                starts.Add(rowStart);
                cells = 0;
                lastSpace = -1;
                i = rowStart;
                continue;
            }

            // A cluster at a time (2026-10-04): a base and its U+FE0F, or a ZWJ sequence, never part over two rows.
            int w = TextCells.ClusterWidth(text, i, out int length);
            length = Math.Max(1, length);
            if (cells > 0 && cells + w > availableCells)
            {
                if (text[i] == ' ' || lastSpace >= 0)
                {
                    int breakAt = text[i] == ' ' ? i : lastSpace;
                    rows.Add(text[rowStart..breakAt]);
                    rowStart = breakAt + 1;
                }
                else
                {
                    rows.Add(text[rowStart..i]);
                    rowStart = i;
                }

                starts.Add(rowStart);
                cells = 0;
                lastSpace = -1;
                i = rowStart;
                continue;
            }

            cells += w;
            if (text[i] == ' ')
            {
                lastSpace = i;
            }

            i += length;
        }

        rows.Add(text[rowStart..]);

        int row = starts.Count - 1;
        while (row > 0 && starts[row] > cursor)
        {
            row--;
        }

        return new InputLayout(rows, starts, row, TextCells.Width(text[starts[row]..cursor]));
    }

    /// <summary>
    /// The UTF-16 index for cell column <paramref name="col"/> on row <paramref name="row"/> (2026-09-21,
    /// the Up/Down row moves): the element under the column, or the row's end past its last cell —
    /// one element back on a row the wrap broke by cells, whose end IS the next row's start and
    /// would show up there (the rule a click follows in <c>ScreenPane.TryHitInput</c>). The row is
    /// clamped to the rows there are; a negative column is the row's start. Pure.
    /// </summary>
    public int IndexAt(int row, int col)
    {
        row = Math.Clamp(row, 0, Rows.Count - 1);
        string text = Rows[row];
        int start = Starts[row];
        int? next = row + 1 < Starts.Count ? Starts[row + 1] : null;
        return start + IndexInRow(text, col, next is { } n && start + text.Length == n);
    }

    /// <summary>
    /// Where cell column <paramref name="col"/> lands in <paramref name="row"/>: the element under it,
    /// or the row's length past its last cell — less one element when <paramref name="cellBroken"/>
    /// (the row's end is the next row's start). Shared with the pane's click mapping.
    /// </summary>
    public static int IndexInRow(string row, int col, bool cellBroken)
    {
        ArgumentNullException.ThrowIfNull(row);
        int cells = 0;
        int i = 0;
        while (i < row.Length)
        {
            int w = TextCells.ElementWidth(row, i, out int length);
            if (col < cells + w)
            {
                break;
            }

            cells += w;
            i += Math.Max(1, length);
        }

        // Past a row that was broken by cells, its end IS the next row's start and the cursor
        // would show up there; the last character of this row is what was meant.
        if (i == row.Length && row.Length > 0 && cellBroken)
        {
            i -= TextCells.ElementLengthBefore(row, row.Length);
        }

        return i;
    }
}
