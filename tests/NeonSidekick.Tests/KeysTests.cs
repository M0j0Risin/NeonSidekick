using NeonSidekick.UI;

namespace NeonSidekick.Tests;

public class KeysTests
{
    [Fact]
    public void IsCancel_IsEscape()
    {
        Assert.True(Keys.IsCancel(Keys.Escape));
        Assert.False(Keys.IsCancel(Keys.Ctrl(ConsoleKey.Q)));
        Assert.False(Keys.IsCancel(Keys.Char('e')));
    }

    [Fact]
    public void IsInterrupt_IsCtrlC_WithoutAlt()
    {
        // The console's own shape (ETX with the key) and the test factory's ('\0') both count;
        // Shift is ignored, Alt (AltGr+C) is not it, and no other key is.
        Assert.True(Keys.IsInterrupt(Keys.CtrlC));
        Assert.True(Keys.IsInterrupt(Keys.Ctrl(ConsoleKey.C)));
        Assert.True(Keys.IsInterrupt(new ConsoleKeyInfo('\x03', ConsoleKey.C, shift: true, alt: false, control: true)));
        Assert.False(Keys.IsInterrupt(new ConsoleKeyInfo('\0', ConsoleKey.C, shift: false, alt: true, control: true)));
        Assert.False(Keys.IsInterrupt(Keys.Escape));
        Assert.False(Keys.IsInterrupt(Keys.Ctrl(ConsoleKey.Q)));
        Assert.False(Keys.IsInterrupt(Keys.Char('c')));
        // Spectre's TestConsoleInput.PushText marks an upper-case letter with Control: a typed "C" is a character, never the chord.
        Assert.False(Keys.IsInterrupt(new ConsoleKeyInfo('C', ConsoleKey.C, shift: false, alt: false, control: true)));
        Assert.False(Keys.IsCancel(Keys.CtrlC));
    }

    [Fact]
    public void IsToolToggle_IsCtrlO_WithoutAlt_AndATypedOStaysAnO()
    {
        // 2026-09-22: the console's SI and the test factory's '\0' count; Alt, a typed "O" (upper case marked with Control by Spectre's test input) and "o" do not.
        Assert.True(Keys.IsToolToggle(Keys.CtrlO));
        Assert.True(Keys.IsToolToggle(Keys.Ctrl(ConsoleKey.O)));
        Assert.Equal('\x0f', Keys.CtrlO.KeyChar);
        Assert.False(Keys.IsToolToggle(new ConsoleKeyInfo('\0', ConsoleKey.O, shift: false, alt: true, control: true)));
        Assert.False(Keys.IsToolToggle(new ConsoleKeyInfo('O', ConsoleKey.O, shift: false, alt: false, control: true)));
        Assert.False(Keys.IsToolToggle(Keys.Char('o')));
        Assert.False(Keys.IsToolToggle(Keys.CtrlC));
    }

    [Fact]
    public void ShortcutLine_IsCtrlAltCNS_WithoutShift_AndAnAltGrCharacterStaysACharacter()
    {
        // 2026-09-30 (the user's ask): the test factory's '\0' and the console's control characters count.
        Assert.Equal("/clear", Keys.ShortcutLine(Keys.CtrlAltC));
        Assert.Equal("/new", Keys.ShortcutLine(Keys.CtrlAltN));
        Assert.Equal("/splash", Keys.ShortcutLine(Keys.CtrlAltS));
        Assert.Equal("/clear", Keys.ShortcutLine(new ConsoleKeyInfo('\x03', ConsoleKey.C, shift: false, alt: true, control: true)));
        Assert.Equal("/new", Keys.ShortcutLine(new ConsoleKeyInfo('\x0e', ConsoleKey.N, shift: false, alt: true, control: true)));
        Assert.Equal("/splash", Keys.ShortcutLine(new ConsoleKeyInfo('\x13', ConsoleKey.S, shift: false, alt: true, control: true)));

        Assert.Null(Keys.ShortcutLine(Keys.CtrlC));
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('\x03', ConsoleKey.C, shift: true, alt: false, control: true)));   // Ctrl+Shift+C: Windows Terminal's copy
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('\0', ConsoleKey.N, shift: true, alt: true, control: true)));
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('ć', ConsoleKey.C, shift: false, alt: true, control: true)));      // AltGr+C on a Polish layout
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('S', ConsoleKey.S, shift: false, alt: false, control: true)));      // a typed "S" in Spectre's test input
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('\0', ConsoleKey.X, shift: false, alt: true, control: true)));
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('\0', ConsoleKey.C, shift: false, alt: true, control: false)));   // Alt+C alone
        Assert.False(Keys.IsInterrupt(Keys.CtrlAltC));
    }

    [Theory]
    [InlineData(ConsoleKey.T, '\x14', "/tools")]
    [InlineData(ConsoleKey.K, '\x0b', "/skills")]
    [InlineData(ConsoleKey.P, '\x10', "/profile")]
    [InlineData(ConsoleKey.Y, '\x19', "/sys")]
    [InlineData(ConsoleKey.G, '\x07', "/usage")]
    [InlineData(ConsoleKey.E, '\x05', "/perf")]
    [InlineData(ConsoleKey.M, '\r', "/memory")]
    [InlineData(ConsoleKey.D, '\x04', "/mcp")]
    [InlineData(ConsoleKey.L, '\x0c', "/cmdlist")]
    [InlineData(ConsoleKey.O, '\x0f', "/police")]
    [InlineData(ConsoleKey.B, '\x02', "/tb")]
    public void ShortcutLine_ThePaneChords_AreTheirBareCommands(ConsoleKey key, char control, string line)
    {
        // Later on 2026-09-30 (the user's ask): the test factory's '\0' and the console's control character count; Shift,
        // Ctrl alone, Alt alone and an AltGr key that types a character are no chord.
        Assert.Equal(line, Keys.ShortcutLine(Keys.CtrlAlt(key)));
        Assert.Equal(line, Keys.ShortcutLine(new ConsoleKeyInfo(control, key, shift: false, alt: true, control: true)));
        Assert.Contains(line, NeonSidekick.App.SlashCommands.Words);
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('\0', key, shift: true, alt: true, control: true)));
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo(control, key, shift: false, alt: false, control: true)));
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('\0', key, shift: false, alt: true, control: false)));
        Assert.Null(Keys.ShortcutLine(new ConsoleKeyInfo('€', key, shift: false, alt: true, control: true)));   // AltGr+E on a German layout, and its like
    }

    [Fact]
    public void ToolGroupText_IsPinned()
    {
        Assert.Equal(1, TextCells.Width(ToolGroupText.CollapsedGlyph));
        Assert.Equal(1, TextCells.Width(ToolGroupText.ExpandedGlyph));
        Assert.Equal("  ▸ 🛠️ 6 tool calls — read_file ×3, grep ×2, run_command", ToolGroupText.Summary([("grep", 2), ("read_file", 3), ("run_command", 1)], expanded: false));
        Assert.Equal("  ▾ 🛠️ 1 tool call — grep", ToolGroupText.Summary([("grep", 1)], expanded: true));
        Assert.Equal("  ▸ 🛠️ tool calls", ToolGroupText.Summary([], expanded: false));
        Assert.Equal("  ▸ 🛠️ 4 tool calls — b ×2, a, c", ToolGroupText.Summary([("a", 1), ("b", 2), ("c", 1)], expanded: false));   // a tie keeps the order first called
        Assert.True(ToolGroupText.Summary(Enumerable.Range(0, 50).Select(i => ("tool_number_" + i, 1)).ToList(), false).Length <= TranscriptRenderer.ToolTextLimit);
        Assert.Equal("(tool calls, code blocks and thinking expanded; Ctrl+O or /collapse folds them)", ToolGroupText.ExpandedNotice(true));
        Assert.Equal("(tool calls, code blocks and thinking collapsed; Ctrl+O, /expand or a click on a summary unfolds them)", ToolGroupText.ExpandedNotice(false));
    }

    [Fact]
    public void CtrlC_IsTheConsolesShape()
    {
        Assert.Equal('\x03', Keys.CtrlC.KeyChar);
        Assert.Equal(ConsoleKey.C, Keys.CtrlC.Key);
        Assert.Equal(ConsoleModifiers.Control, Keys.CtrlC.Modifiers);
    }

    [Fact]
    public void F4_IsABareKey()
    {
        Assert.Equal(ConsoleKey.F4, Keys.F4.Key);
        Assert.Equal('\0', Keys.F4.KeyChar);
        Assert.False(Keys.IsCancel(Keys.F4));
    }

    [Fact]
    public void Char_CarriesTheCharacterWithNoKey()
    {
        var key = Keys.Char('é');
        Assert.Equal('é', key.KeyChar);
        Assert.Equal(ConsoleKey.None, key.Key);
        Assert.Equal((ConsoleModifiers)0, key.Modifiers);
    }
}

public class PromptResultTests
{
    [Fact]
    public void Canceled_IsOneSharedInstance()
    {
        Assert.Same(PromptResult<string>.Canceled, PromptResult<string>.Canceled);
        Assert.True(PromptResult<string>.Canceled.IsCanceled);
        Assert.Null(PromptResult<string>.Canceled.Value);
    }

    [Fact]
    public void From_WrapsTheValue_WithRecordEquality()
    {
        Assert.Equal(PromptResult<string>.From("a"), PromptResult<string>.From("a"));
        Assert.NotEqual(PromptResult<string>.From("a"), PromptResult<string>.Canceled);
        Assert.Throws<ArgumentNullException>(() => PromptResult<string>.From(null!));
    }
}

public class TextCellsTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData("abc", 3)]
    [InlineData("日本語", 6)]
    [InlineData("a日b", 4)]
    [InlineData("😀", 2)]
    [InlineData("x😀y", 4)]
    [InlineData("⏰", 2)]
    [InlineData("⏳", 2)]
    [InlineData("✋", 2)]                    // ✋ (2026-09-18): the interrupt glyph, a BMP character Unicode lists as Wide
    [InlineData("✊", 2)]
    [InlineData("🎤 👂 ✋", 8)]              // the speech strip with the interrupt
    [InlineData("⏰ tea 09:00", 12)]
    [InlineData("\U0001F5D1", 2)]                // 🗑 bare: a surrogate pair
    [InlineData("\U0001F5D1️", 2)]          // 🗑️ (2026-09-18): the variation selector is zero, drawn inside the pair's two cells
    [InlineData("\U0001F5D1️ ", 3)]
    [InlineData("⚙️", 2)]              // ⚙️ (2026-09-19): the gear is Neutral, one cell bare; U+FE0F makes the two-cell emoji, so the selector counts one
    [InlineData("✂️", 2)]              // ✂️: the prune lines' scissors, the same shape
    [InlineData("✂️ x", 4)]
    [InlineData("✋️", 2)]              // a selector after a wide BMP character stays zero
    [InlineData("\U0001F5DC️", 2)]          // 🗜️: the compact lines' clamp, a pair + the selector
    [InlineData("\U0001F6D1", 2)]                // 🛑: the stop line's sign, a pair alone
    [InlineData("️", 0)]                    // a selector with nothing before it
    [InlineData("️️", 0)]              // two selectors: the second follows a selector, not a character
    [InlineData("⛓️‍\U0001F4A5", 2)]   // ⛓️‍💥 (later on 2026-09-29, the uncensored column): a ZWJ sequence, one glyph as wide as its first part
    [InlineData("⛓️‍\U0001F4A5 x", 4)]
    [InlineData("\U0001F468‍\U0001F469‍\U0001F467", 2)]   // 👨‍👩‍👧: the family, three pairs joined, two cells
    [InlineData("\U0001F4A2", 2)]                // 💢: the aggressive build's, a pair alone
    public void Width_CountsCells(string text, int cells)
    {
        Assert.Equal(cells, TextCells.Width(text));
    }

    [Fact]
    public void Width_AVariationSelectorIsZero()
    {
        Assert.Equal(0, TextCells.Width('️'));
        Assert.Equal(0, TextCells.Width('︎'));
    }

    [Fact]
    public void ElementLengthBefore_StepsOverASurrogatePair()
    {
        Assert.Equal(2, TextCells.ElementLengthBefore("a😀", 3));
        Assert.Equal(1, TextCells.ElementLengthBefore("ab", 2));
        Assert.Equal(0, TextCells.ElementLengthBefore("ab", 0));
    }
}
