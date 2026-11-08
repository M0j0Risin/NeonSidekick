using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The cell widths of an emoji-presentation sequence of a text-default character (a character and its U+FE0F) in Terminal.app
/// (2026-10-06, the user's report: the toolbar's clicks opened /settings, landing two cells off per ⚙️ or 🛠️). Measured there
/// with cursor reports: ⚙️ 🛠️ 🖥️ ✂️ 🗑️ one cell each, 🎓 💾 🔒 👮 🐚 📁 🌐 two. The pure overload only: the static switch is the
/// app's, set once at start-up, and tests run side by side.
/// </summary>
public sealed class TextCellsNarrowTests
{
    private static int Cells(string text, bool narrow)
    {
        int cells = 0;
        for (int i = 0; i < text.Length; i++)
        {
            cells += TextCells.ElementWidth(text, i, out int length, narrow);
            i += length - 1;
        }

        return cells;
    }

    /// <summary>
    /// The one cut (2026-11-02, review P2-9): the hover hint's, the /usage labels' and the tool lines' cuts are <see cref="TextCells.Cut"/>,
    /// so a ZWJ emoji or a selector at the edge goes whole or not at all, the same in each, and the result fits.
    /// </summary>
    [Theory]
    [InlineData("ab👩\u200D💻cd", 4)]
    [InlineData("ab👩\u200D💻cd", 5)]
    [InlineData("ab⚙️cd", 4)]
    [InlineData("abcdef", 3)]
    public void TheThreeCuts_AreOne_AndNeverPartAGlyph(string text, int cells)
    {
        string cut = TextCells.Cut(text, cells);
        Assert.Equal(cut, App.UsageText.CutToCells(text, cells));
        Assert.Equal(cut, TranscriptRenderer.Truncate(text, cells));
        Assert.True(TextCells.Width(cut) <= cells);
        Assert.EndsWith("…", cut, StringComparison.Ordinal);
        Assert.DoesNotContain("\u200D…", cut, StringComparison.Ordinal);   // never a joiner left at the edge
        Assert.False(cut.Length >= 2 && char.IsHighSurrogate(cut[^2]));
        Assert.Equal("", TextCells.Cut(text, 0));
    }

    /// <summary>A list column's cell (2026-11-08, /services' and /tasks' columns): exactly its width, cut with "…" or padded.</summary>
    [Theory]
    [InlineData("ab", 5, "ab   ")]
    [InlineData("abcdef", 4, "abc…")]
    [InlineData("", 3, "   ")]
    [InlineData("abc", 0, "")]
    [InlineData("🎓", 4, "🎓  ")]
    [InlineData("🎓🎓🎓", 4, "🎓… ")]   // the cut is a cell short of the next wide glyph, so it is padded too
    public void Cell_IsExactlyItsWidth(string text, int cells, string expected)
    {
        string cell = TextCells.Cell(text, cells);
        Assert.Equal(expected, cell);
        Assert.Equal(cells, TextCells.Width(cell));
    }

    [Theory]
    [InlineData("⚙️", 1, 2)]   // U+2699 U+FE0F: the base one cell, the selector none in Terminal.app, one elsewhere
    [InlineData("🛠️", 1, 2)]   // U+1F6E0 U+FE0F: a pair with its selector, one cell there
    [InlineData("🖥️", 1, 2)]
    [InlineData("✂️", 1, 2)]
    [InlineData("🗑️", 1, 2)]
    [InlineData("🎓", 2, 2)]    // emoji presentation by default: two everywhere
    [InlineData("💾", 2, 2)]
    [InlineData("🌐", 2, 2)]
    [InlineData("⏳", 2, 2)]
    [InlineData("a", 1, 1)]
    public void ASelectorSequence_IsOneCellInTerminalApp_TwoElsewhere(string glyph, int narrow, int wide)
    {
        Assert.Equal(narrow, Cells(glyph, narrow: true));
        Assert.Equal(wide, Cells(glyph, narrow: false));
    }

    [Fact]
    public void TheToolbarsStart_IsFourCellsShorterThere()
    {
        // ⚙️ 🛠️ 🎓 💾: the two selector glyphs lose a cell each, the spaces and the two-cell emoji keep theirs.
        const string strip = "⚙️ 🛠️ 🎓 💾";
        Assert.Equal(1 + 1 + 1 + 1 + 2 + 1 + 2, Cells(strip, narrow: true));
        Assert.Equal(2 + 1 + 2 + 1 + 2 + 1 + 2, Cells(strip, narrow: false));
    }

    [Theory]
    [InlineData("Apple_Terminal", true)]
    [InlineData("iTerm.app", true)]          // on its alternate screen, where the app runs (measured later on 2026-10-06)
    [InlineData("vscode", false)]
    [InlineData("apple_terminal", false)]
    [InlineData(null, false)]
    public void TerminalApp_AndITerm2_DrawThemNarrow(string? termProgram, bool narrow) =>
        Assert.Equal(narrow, TextCells.ForTerminal(termProgram));

    [Theory]
    // Terminal.app: ⚙ at 0, 🛠 at 2, each painted over the blank after it, so both of its cells take the click; 🎓 4–5, 💾 7–8.
    [InlineData(true, 0, "⚙️", 0)]
    [InlineData(true, 1, "⚙️", 0)]
    [InlineData(true, 2, "🛠️", 2)]
    [InlineData(true, 3, "🛠️", 2)]
    [InlineData(true, 4, "🎓", 4)]
    [InlineData(true, 5, "🎓", 4)]
    [InlineData(true, 6, "", -1)]
    [InlineData(true, 7, "💾", 7)]
    [InlineData(true, 8, "💾", 7)]
    // Elsewhere, two cells each, as before: ⚙ 0–1, blank 2, 🛠 3–4, blank 5, 🎓 6–7, blank 8, 💾 9–10.
    [InlineData(false, 1, "⚙️", 0)]
    [InlineData(false, 2, "", -1)]
    [InlineData(false, 4, "🛠️", 3)]
    [InlineData(false, 9, "💾", 9)]
    public void AToolbarClick_LandsOnTheGlyphTheTerminalPainted(bool narrow, int x, string glyph, int column)
    {
        var hit = NeonSidekick.UI.ScreenPane.ToolbarHitAt("⚙️ 🛠️ 🎓 💾", -1, 0, x, narrow);
        Assert.Equal(glyph, hit.Glyph);
        Assert.Equal(column, hit.Column);
    }

    [Fact]
    public void InTerminalApp_ANarrowGlyph_GetsASpaceOfItsOwn_TheRestIsUntouched()
    {
        // 2026-10-06, the user's ask: ⚙️ 🛠️ 🎓 ran together there, each narrow glyph painted over the space after it.
        Assert.Equal("⚙️  🛠️  🎓 💾", TextCells.SpaceSelectorSequences("⚙️ 🛠️ 🎓 💾", narrow: true));
        Assert.Equal("⚙️ 🛠️ 🎓 💾", TextCells.SpaceSelectorSequences("⚙️ 🛠️ 🎓 💾", narrow: false));
        Assert.Equal("🎓 💾 a", TextCells.SpaceSelectorSequences("🎓 💾 a", narrow: true));
        Assert.Equal("✂️ ", TextCells.SpaceSelectorSequences("✂️", narrow: true));
        // Spaced, the strip is as wide in Terminal.app as it is elsewhere unspaced: two cells a glyph, one a gap.
        Assert.Equal(Cells("⚙️ 🛠️ 🎓 💾", narrow: false), Cells(TextCells.SpaceSelectorSequences("⚙️ 🛠️ 🎓 💾", narrow: true), narrow: true));
    }

    /// <summary>
    /// The panes' titles in Terminal.app and iTerm2 (2026-10-07, the user's screenshot: <c>🛠️Tools</c>, <c>⚙️Settings</c>): the label is
    /// spaced as the toolbar is, and the strip's layout counts the extra cell, so the tabs and their clicks move with it. The static
    /// switch is set here and put back: the suite runs serially (<c>AssemblyInfo.cs</c>).
    /// </summary>
    [Fact]
    public void PaneTitles_AreSpaced_WhenTheTerminalDrawsTheGlyphNarrow()
    {
        string[] tabs = ["Offered", "Ask"];
        int wideIndent = InfoPane.TabStripLayout("🛠️ Tools", tabs, 200).Places[0].Column;
        bool was = TextCells.NarrowSelectorSequences;
        try
        {
            TextCells.NarrowSelectorSequences = true;
            Assert.Contains("🛠️  Tools", InfoPane.TabStripMarkup("🛠️ Tools", tabs, 0), StringComparison.Ordinal);
            Assert.Contains("🛠️  Tools", InfoPane.TabStripRows("🛠️ Tools", tabs, 0, 200)[0], StringComparison.Ordinal);
            Assert.Contains("⚙️  Settings", MenuPane.TitleMarkup("⚙️ Settings"), StringComparison.Ordinal);
            // One cell narrower glyph, one more space: the first tab stays where it is elsewhere, and so do the clicks.
            Assert.Equal(wideIndent, InfoPane.TabStripLayout("🛠️ Tools", tabs, 200).Places[0].Column);
            Assert.Equal(0, InfoPane.TabAt("🛠️ Tools", tabs, wideIndent));
        }
        finally
        {
            TextCells.NarrowSelectorSequences = was;
        }

        Assert.Contains("🛠️ Tools", InfoPane.TabStripMarkup("🛠️ Tools", tabs, 0), StringComparison.Ordinal);
        Assert.DoesNotContain("🛠️  Tools", InfoPane.TabStripMarkup("🛠️ Tools", tabs, 0), StringComparison.Ordinal);
    }
}
