using NeonSidekick.UI;

namespace NeonSidekick.Tests;

public class InputLayoutTests
{
    [Theory]
    [InlineData("hello", 5, 10, "hello", 0, 5)]                                  // fits whole, cursor at the end
    [InlineData("hello", 2, 10, "hello", 0, 2)]                                  // fits whole, cursor inside
    [InlineData("", 0, 5, "", 0, 0)]                                             // empty: one empty row
    [InlineData("aaaa bbbb cccc", 14, 10, "aaaa bbbb|cccc", 1, 4)]              // breaks at the last space; the space is dropped
    [InlineData("aaaa bbbb cccc", 10, 10, "aaaa bbbb|cccc", 1, 0)]              // right after the breaking space: the next row's first cell
    [InlineData("aaaa bbbb cccc", 9, 10, "aaaa bbbb|cccc", 0, 9)]               // on the breaking space: the row's end
    [InlineData("aaaa bbbb cccc", 12, 10, "aaaa bbbb|cccc", 1, 2)]              // mid-word after the break
    [InlineData("abcdefgh", 8, 5, "abcde|fgh", 1, 3)]                           // a word longer than a row breaks by cells
    [InlineData("abcdefgh", 5, 5, "abcde|fgh", 1, 0)]                           // the cursor at a cell break: the next row
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 30, 17, "aaaaaaaaaaaaaaaaa|aaaaaaaaaaaaa", 1, 13)]
    [InlineData("abcd  ", 6, 5, "abcd |", 1, 0)]                                // the second space overflows and is the break
    [InlineData("ab cd ef gh", 11, 5, "ab cd|ef gh", 1, 5)]                     // a row filled exactly, then the space breaks it
    [InlineData("ab日本", 4, 5, "ab日|本", 1, 2)]                                 // a wide character never splits and never overfills
    [InlineData("日本語", 3, 1, "日|本|語", 2, 2)]                                  // wider than the row: placed anyway, one per row
    [InlineData("x😀y", 3, 3, "x😀|y", 1, 0)]                                    // a surrogate pair is one element
    [InlineData("x😀y", 1, 3, "x😀|y", 0, 1)]
    [InlineData("abc", 9, 0, "a|b|c", 2, 1)]                                    // the cursor clamped, the budget at least one cell
    [InlineData("ab\ncd", 5, 10, "ab|cd", 1, 2)]                                // a pasted line break is a hard break, dropped like a breaking space
    [InlineData("ab\ncd", 2, 10, "ab|cd", 0, 2)]                                // on the break: the row's end
    [InlineData("ab\ncd", 3, 10, "ab|cd", 1, 0)]                                // after it: the next row's first cell
    [InlineData("ab\n\ncd", 3, 10, "ab||cd", 1, 0)]                             // two breaks: an empty row between
    [InlineData("\n", 1, 10, "|", 1, 0)]                                        // a break alone: two empty rows
    [InlineData("aaaa bbbb\ncc", 12, 6, "aaaa|bbbb|cc", 2, 2)]                  // wrapping and a hard break together
    public void Wrap_IsPinned(string text, int cursor, int available, string rows, int cursorRow, int cursorCol)
    {
        var layout = InputLayout.Wrap(text, cursor, available);

        Assert.Equal(rows.Split('|'), layout.Rows);
        Assert.Equal(cursorRow, layout.CursorRow);
        Assert.Equal(cursorCol, layout.CursorCol);
    }

    [Fact]
    public void IndexAt_MapsARowAndAColumn_BackToTheDraft()
    {
        // The Up/Down row moves (2026-09-21): a space-broken row, the dropped space past its end.
        var layout = InputLayout.Wrap("aaaa bbbb cccc", 0, 10);   // "aaaa bbbb" | "cccc"
        Assert.Equal(2, layout.IndexAt(0, 2));       // under the element
        Assert.Equal(9, layout.IndexAt(0, 9));       // past the row's last cell: the breaking space
        Assert.Equal(9, layout.IndexAt(0, 30));      // and any column beyond
        Assert.Equal(12, layout.IndexAt(1, 2));
        Assert.Equal(14, layout.IndexAt(1, 9));      // the last row's end is the text's end
        Assert.Equal(0, layout.IndexAt(0, -1));      // a negative column is the row's start
        Assert.Equal(12, layout.IndexAt(5, 2));      // the row clamped
        Assert.Equal(2, layout.IndexAt(-1, 2));

        // A cell-broken row: past its end is one element back, since its end IS the next row's start.
        layout = InputLayout.Wrap("abcdefgh", 0, 5);   // "abcde" | "fgh"
        Assert.Equal(4, layout.IndexAt(0, 9));
        Assert.Equal(4, layout.IndexAt(0, 4));
        Assert.Equal(8, layout.IndexAt(1, 9));

        // A wide character straddled: the column inside it lands on it; a hard break's row.
        layout = InputLayout.Wrap("a漢b\ncd", 0, 10);   // "a漢b" | "cd"
        Assert.Equal(1, layout.IndexAt(0, 1));
        Assert.Equal(1, layout.IndexAt(0, 2));
        Assert.Equal(2, layout.IndexAt(0, 3));
        Assert.Equal(3, layout.IndexAt(0, 4));       // past the end: before the '\n'
        Assert.Equal(5, layout.IndexAt(1, 1));
        Assert.Equal(0, InputLayout.Wrap("", 0, 5).IndexAt(0, 3));
    }

    [Fact]
    public void Starts_AreEachRowsFirstIndex()
    {
        var layout = InputLayout.Wrap("aaaa bbbb cccc", 0, 10);
        Assert.Equal(new[] { 0, 10 }, layout.Starts);
        layout = InputLayout.Wrap("abcdefgh", 0, 5);
        Assert.Equal(new[] { 0, 5 }, layout.Starts);
        Assert.Equal(new[] { 0 }, InputLayout.Wrap("", 0, 5).Starts);
    }

    [Fact]
    public void Wrap_NeverProducesARowWiderThanTheBudget_UnlessOneElementIs()
    {
        string text = "the quick brown fox jumps over the lazy dog, and again, and 日本語の文章 too, with a verylongwordthatdoesnotfitanywhere at the end";
        for (int available = 1; available <= 40; available++)
        {
            var layout = InputLayout.Wrap(text, text.Length, available);
            foreach (string row in layout.Rows)
            {
                Assert.True(TextCells.Width(row) <= Math.Max(available, 2), $"'{row}' at {available}");
            }

            // Every character but the dropped spaces is in a row, in order.
            Assert.Equal(text.Replace(" ", ""), string.Concat(layout.Rows).Replace(" ", ""));

            // Each row starts where the text says, and the gap to the next start is the dropped space or nothing.
            Assert.Equal(layout.Rows.Count, layout.Starts.Count);
            for (int i = 0; i < layout.Rows.Count; i++)
            {
                Assert.Equal(layout.Rows[i], text.Substring(layout.Starts[i], layout.Rows[i].Length));
                int next = i + 1 < layout.Starts.Count ? layout.Starts[i + 1] : text.Length;
                Assert.InRange(next - layout.Starts[i] - layout.Rows[i].Length, 0, 1);
            }
        }
    }

    /// <summary>A base and its U+FE0F, or a ZWJ sequence, are one cluster to the wrap (2026-10-04): never parted over two rows.</summary>
    [Fact]
    public void Wrap_NeverPartsACluster()
    {
        // "ab⚙️": the gear and its selector are two cells together; on a three-cell row they go down as one.
        var layout = InputLayout.Wrap("ab\u2699\uFE0F", 4, 3);
        Assert.Equal(["ab", "\u2699\uFE0F"], layout.Rows);
        Assert.Equal(2, TextCells.ClusterWidth("\u2699\uFE0F", 0, out int length));
        Assert.Equal(2, length);
        Assert.Equal(2, TextCells.ClusterWidth("\u26D3\uFE0F\u200D\U0001F4A5", 0, out length));   // ⛓️‍💥: as wide as its first part
        Assert.Equal(5, length);
    }

    /// <summary>The BMP emoji drawn two cells wide (2026-10-04, the UI review found ✅ ❌ ⭐ ⛔ ➕ ⬛ counted one).</summary>
    [Theory]
    [InlineData('\u2705')]
    [InlineData('\u274C')]
    [InlineData('\u2B50')]
    [InlineData('\u26D4')]
    [InlineData('\u2795')]
    [InlineData('\u2B1B')]
    [InlineData('\u2615')]
    [InlineData('\u231A')]
    [InlineData('\u2728')]
    [InlineData('\u2757')]
    public void TheWideBmpEmoji_AreTwoCells(char c) => Assert.Equal(2, TextCells.Width(c));

    [Theory]
    [InlineData('\u2699')]   // ⚙ text presentation by default: one cell bare
    [InlineData('\u2714')]   // ✔
    [InlineData('\u25CF')]   // ●
    [InlineData('\u2190')]   // ←
    [InlineData('a')]
    public void TextPresentationCharacters_StayOneCell(char c) => Assert.Equal(1, TextCells.Width(c));
}
