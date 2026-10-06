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
}
