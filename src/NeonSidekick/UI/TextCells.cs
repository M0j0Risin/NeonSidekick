namespace NeonSidekick.UI;

/// <summary>
/// Terminal cell widths for cursor arithmetic. Spectre's own calculator (<c>Cell</c>) is internal
/// to its assembly, so this is the table: two cells for a wide CJK/fullwidth character
/// or a surrogate pair (emoji), one for everything else — plus the handful of BMP emoji Unicode
/// lists as Wide (the clocks and hourglasses: the timer glyph on the hint row is one; the raised hand ✋ of the interrupt, 2026-09-18; the question mark ❓ of the Help pane, 2026-09-28), and zero
/// for a variation selector (U+FE0E / U+FE0F: the emoji-presentation tail of <c>🗑️</c>, which the
/// terminal draws inside the pair's two cells — 2026-09-18) — except a U+FE0F after a one-cell
/// character, which counts one: the selector makes an emoji-presentation sequence, and Windows
/// Terminal (1.22+, grapheme clusters) draws <c>✂️</c> or <c>⚙️</c> two cells wide where the bare
/// character is one (2026-09-19, the scissors of the prune lines). Combining marks are otherwise out
/// of scope.
/// </summary>
public static class TextCells
{
    /// <summary>Cells occupied by one UTF-16 unit. A lone surrogate half counts one; see <see cref="Width(string)"/> for pairs.</summary>
    public static int Width(char c)
    {
        int code = c;
        if (code is 0xFE0E or 0xFE0F)             // variation selectors: zero, they pick the presentation of the character before
        {
            return 0;
        }

        if (code is >= 0x1100 and <= 0x115F      // Hangul Jamo
            or >= 0x231A and <= 0x231B            // ⌚ ⌛ (East Asian Wide emoji in the BMP)
            or >= 0x23E9 and <= 0x23EC            // ⏩ .. ⏬
            or 0x23F0 or 0x23F3                   // ⏰ ⏳
            or 0x26A1                             // ⚡ (Wide; the drafter column of the embedded rows, 2026-09-29)
            or >= 0x270A and <= 0x270B            // ✊ ✋ (Wide since Unicode 9; the interrupt glyph, 2026-09-18)
            or >= 0x2753 and <= 0x2755 or 0x2757  // ❓ ❔ ❕ ❗ (Wide; ❓ is the Help pane's glyph, 2026-09-28)
            or >= 0x2E80 and <= 0x303E            // CJK radicals .. CJK punctuation
            or >= 0x3041 and <= 0x33FF            // Kana .. CJK compatibility
            or >= 0x3400 and <= 0x4DBF            // CJK unified (ext)
            or >= 0x4E00 and <= 0x9FFF            // CJK unified
            or >= 0xA000 and <= 0xA4CF            // Yi
            or >= 0xAC00 and <= 0xD7A3            // Hangul syllables
            or >= 0xF900 and <= 0xFAFF            // CJK compatibility ideographs
            or >= 0xFE30 and <= 0xFE4F            // CJK compatibility forms
            or >= 0xFF00 and <= 0xFF60            // Fullwidth forms
            or >= 0xFFE0 and <= 0xFFEE)           // Fullwidth signs
        {
            return 2;
        }

        return 1;
    }

    /// <summary>Cells occupied by <paramref name="text"/>; a surrogate pair is one two-cell glyph.</summary>
    public static int Width(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int cells = 0;
        for (int i = 0; i < text.Length; i++)
        {
            cells += ElementWidth(text, i, out int length);
            i += length - 1;
        }

        return cells;
    }

    /// <summary>
    /// Width of the text element starting at <paramref name="index"/> and its length in UTF-16
    /// units (2 for a surrogate pair, else 1). A U+FE0F stays an element of its own (the stepping
    /// rules never change), one cell wide after a one-cell character and zero after a wide one.
    /// </summary>
    public static int ElementWidth(string text, int index, out int length)
    {
        if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
        {
            length = 2;
            return 2;
        }

        length = 1;
        if (text[index] == '️' && index > 0)
        {
            char before = text[index - 1];
            return !char.IsLowSurrogate(before) && before is not ('︎' or '️') && Width(before) == 1 ? 1 : 0;
        }

        return Width(text[index]);
    }

    /// <summary>Length in UTF-16 units of the element that ends just before <paramref name="index"/>.</summary>
    public static int ElementLengthBefore(string text, int index)
    {
        if (index >= 2 && char.IsLowSurrogate(text[index - 1]) && char.IsHighSurrogate(text[index - 2]))
        {
            return 2;
        }

        return index > 0 ? 1 : 0;
    }
}
