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
/// character is one (2026-09-19, the scissors of the prune lines). A zero-width joiner (U+200D) and the element right after
/// it count nothing (later on 2026-09-29, the uncensored column's broken chain <c>⛓️‍💥</c>): Windows Terminal draws a ZWJ
/// sequence as one glyph, as wide as its first part. A skin-tone modifier, a tag character, a keycap's U+20E3 and a flag's second
/// regional indicator draw inside the glyph before them too (2026-10-10, the avatar picker: <c>👍🏽</c> measured four), a skin tone
/// on a one-cell base (<c>☝🏽</c>) widening it to two as a U+FE0F does. Combining marks are otherwise out of scope.
/// </summary>
public static class TextCells
{
    /// <summary>
    /// Whether the terminal draws an emoji-presentation sequence of a text-default character — a character and its U+FE0F, such
    /// as <c>⚙️</c>, <c>🛠️</c>, <c>🖥️</c>, <c>✂️</c>, <c>🗑️</c> — one cell wide, as macOS's Terminal.app does (and iTerm2 on its alternate screen) (2026-10-06, measured
    /// there with cursor reports: 1 cell each, where every emoji-presentation character such as <c>🎓</c> is 2). Off, the default,
    /// such a sequence is two cells, as Windows Terminal, iTerm2 and the rest draw it. Set once at start-up
    /// (<see cref="ForTerminal"/>), before anything is measured; the toolbar's clicks landed two cells off per such glyph without it.
    /// </summary>
    public static bool NarrowSelectorSequences { get; set; }

    /// <summary>
    /// <paramref name="text"/> with a space after every selector sequence the terminal draws one cell wide (<paramref name="narrow"/>:
    /// Terminal.app), where it paints the picture over the cell after it (2026-10-06, the user's ask: the toolbar's ⚙️ 🛠️ 🎓 ran
    /// together there) — so the gap after it is a real cell again, as after a two-cell emoji. Unchanged when not narrow. Pure.
    /// </summary>
    /// <summary>
    /// <see cref="SpaceSelectorSequences(string, bool)"/> for this terminal (<see cref="NarrowSelectorSequences"/>): the panes'
    /// titles too (2026-10-07, the user's iTerm2 screenshot: <c>🛠️Tools</c> and <c>⚙️Settings</c> ran together, the toolbar's fix
    /// had not reached the tab strip). Unchanged on Windows.
    /// </summary>
    public static string Spaced(string text) => SpaceSelectorSequences(text, NarrowSelectorSequences);

    public static string SpaceSelectorSequences(string text, bool narrow)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!narrow || text.IndexOf('\uFE0F') < 0)
        {
            return text;
        }

        var spaced = new System.Text.StringBuilder(text.Length + 4);
        for (int i = 0; i < text.Length; i++)
        {
            int width = ElementWidth(text, i, out int length, narrow: true);
            spaced.Append(text, i, length);
            int end = i + length;
            if (width == 1 && end < text.Length && text[end] == '\uFE0F')
            {
                spaced.Append('\uFE0F').Append(' ');
                end++;
            }

            i = end - 1;
        }

        return spaced.ToString();
    }

    /// <summary>
    /// Whether the terminal named by <c>TERM_PROGRAM</c> draws a selector sequence one cell wide on the alternate screen, where the
    /// app runs (<see cref="NarrowSelectorSequences"/>): Terminal.app's <c>Apple_Terminal</c>, and iTerm2's <c>iTerm.app</c> (later on
    /// 2026-10-06, the user's report and a measurement: two cells on iTerm2's main screen, one on its alternate screen). Pure.
    /// </summary>
    public static bool ForTerminal(string? termProgram) => termProgram is "Apple_Terminal" or "iTerm.app";

    /// <summary>
    /// Whether the terminal named by <c>TERM_PROGRAM</c> draws a selector sequence one cell wide on its main screen, where headless writes
    /// (2026-10-17, the plan done line's ❤️): Terminal.app's <c>Apple_Terminal</c> alone — iTerm2 draws it two cells there (the 2026-10-06
    /// measurement, <see cref="ForTerminal"/>). Pure.
    /// </summary>
    public static bool NarrowOnMainScreen(string? termProgram) => termProgram is "Apple_Terminal";

    /// <summary>Cells occupied by one UTF-16 unit. A lone surrogate half counts one; see <see cref="Width(string)"/> for pairs.</summary>
    public static int Width(char c)
    {
        int code = c;
        if (code is 0xFE0E or 0xFE0F or 0x200D or 0x20E3)   // variation selectors: zero, they pick the presentation of the character before; the ZWJ joins; the keycap encloses
        {
            return 0;
        }

        if (code is >= 0x1100 and <= 0x115F      // Hangul Jamo
            || IsWideEmoji(code)
            || code is >= 0x2E80 and <= 0x303E    // CJK radicals .. CJK punctuation
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

    /// <summary>
    /// Every BMP character with <c>Emoji_Presentation=Yes</c> (East Asian Wide; Unicode's emoji-data.txt): drawn two cells wide with
    /// no selector. A handful until 2026-10-04 (the clocks, ⚡, ✋, ✨, ❓), when the UI review found ✅ ❌ ⭐ ⛔ ➕ ⬛ and the rest counted
    /// one cell, so a draft holding one put the cursor a cell off. Sorted by start.
    /// </summary>
    private static readonly (int First, int Last)[] WideEmoji =
    [
        (0x231A, 0x231B), (0x23E9, 0x23EC), (0x23F0, 0x23F0), (0x23F3, 0x23F3), (0x25FD, 0x25FE), (0x2614, 0x2615),
        (0x2648, 0x2653), (0x267F, 0x267F), (0x2693, 0x2693), (0x26A1, 0x26A1), (0x26AA, 0x26AB), (0x26BD, 0x26BE),
        (0x26C4, 0x26C5), (0x26CE, 0x26CE), (0x26D4, 0x26D4), (0x26EA, 0x26EA), (0x26F2, 0x26F3), (0x26F5, 0x26F5),
        (0x26FA, 0x26FA), (0x26FD, 0x26FD), (0x2705, 0x2705), (0x270A, 0x270B), (0x2728, 0x2728), (0x274C, 0x274C),
        (0x274E, 0x274E), (0x2753, 0x2755), (0x2757, 0x2757), (0x2795, 0x2797), (0x27B0, 0x27B0), (0x27BF, 0x27BF),
        (0x2B1B, 0x2B1C), (0x2B50, 0x2B50), (0x2B55, 0x2B55),
    ];

    /// <summary>Whether <paramref name="code"/> is one of <see cref="WideEmoji"/>.</summary>
    public static bool IsWideEmoji(int code)
    {
        if (code < 0x231A || code > 0x2B55)
        {
            return false;
        }

        foreach (var (first, last) in WideEmoji)
        {
            if (code < first)
            {
                return false;
            }

            if (code <= last)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Width and UTF-16 length of the cluster at <paramref name="index"/> (2026-10-04): the element there (<see cref="ElementWidth"/>)
    /// with every variation selector after it and every ZWJ-joined element — what the terminal draws as one glyph, and what a
    /// cut or a wrap must never split (a base parted from its U+FE0F put the cursor a cell off).
    /// </summary>
    public static int ClusterWidth(string text, int index, out int length)
    {
        ArgumentNullException.ThrowIfNull(text);
        int cells = ElementWidth(text, index, out length);
        int i = index + length;
        while (i < text.Length)
        {
            char c = text[i];
            if (c is '\uFE0E' or '\uFE0F' or '\u20E3')
            {
                cells += ElementWidth(text, i, out int selector);
                i += selector;
            }
            else if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) && ModifiesBefore(text, i))
            {
                // A skin tone, a tag or a flag's second indicator (2026-10-10): part of the glyph (ElementWidth says how wide).
                cells += ElementWidth(text, i, out int modifier);
                i += modifier;
            }
            else if (c == '\u200D' && i + 1 < text.Length)
            {
                i++;   // the joiner, then the element it joins (zero wide: ElementWidth knows it follows a ZWJ)
                cells += ElementWidth(text, i, out int joined);
                i += joined;
            }
            else
            {
                break;
            }
        }

        length = i - index;
        return cells;
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
    /// rules never change), one cell wide after a one-cell character and zero after a wide one. An element right after a
    /// U+200D is zero wide: the joiner makes it part of the glyph before (later on 2026-09-29).
    /// </summary>
    public static int ElementWidth(string text, int index, out int length) => ElementWidth(text, index, out length, NarrowSelectorSequences);

    /// <summary>
    /// <see cref="ElementWidth(string, int, out int)"/> for a terminal that draws a selector sequence one cell wide or two
    /// (<paramref name="narrow"/>, <see cref="NarrowSelectorSequences"/>): narrow, a surrogate pair followed by a U+FE0F is one
    /// cell (<c>🛠️</c>) and a U+FE0F after a one-cell character adds none (<c>⚙️</c>). Pure; the tests pass both.
    /// </summary>
    public static int ElementWidth(string text, int index, out int length, bool narrow)
    {
        ArgumentNullException.ThrowIfNull(text);
        bool joined = index > 0 && text[index - 1] == '\u200D';
        if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
        {
            length = 2;
            if (joined)
            {
                return 0;
            }

            if (ModifiesBefore(text, index))
            {
                // A skin tone after a one-cell BMP base (☝ ✌ ✍ ⛹) makes an emoji of it, two cells as a U+FE0F would: one here.
                char before = text[index - 1];
                bool tone = text[index] == '\uD83C' && text[index + 1] is >= '\uDFFB' and <= '\uDFFF';
                return tone && !narrow && !char.IsSurrogate(before) && before is not ('︎' or '️') && Width(before) == 1 ? 1 : 0;
            }

            return narrow && index + 2 < text.Length && text[index + 2] == '\uFE0F' ? 1 : 2;
        }

        if (joined)
        {
            length = 1;
            return 0;
        }

        length = 1;
        if (text[index] == '\uFE0F' && index > 0)
        {
            char before = text[index - 1];
            // Not after a joined element (2026-10-10, \uD83D\uDE42\u200D\u2194\uFE0F's arrow): that one is drawn inside the glyph before the joiner.
            bool afterJoined = index >= 2 && text[index - 2] == '\u200D';
            return !narrow && !afterJoined && !char.IsLowSurrogate(before) && before is not ('\uFE0E' or '\uFE0F') && Width(before) == 1 ? 1 : 0;
        }

        return Width(text[index]);
    }

    /// <summary>
    /// Whether the astral element at <paramref name="index"/> draws inside the glyph before it (2026-10-10, the avatar picker's
    /// emoji): a skin-tone modifier (U+1F3FB–U+1F3FF) after anything, a tag character (U+E0020–U+E007F, the subdivision flags'
    /// spelling), or the second regional indicator of a pair (a flag is two of them, drawn as one two-cell glyph). Each counted
    /// two cells until then, so <c>👍🏽</c> measured four.
    /// </summary>
    private static bool ModifiesBefore(string text, int index)
    {
        if (index == 0)
        {
            return false;
        }

        int code = char.ConvertToUtf32(text[index], text[index + 1]);
        if (code is >= 0x1F3FB and <= 0x1F3FF or >= 0xE0020 and <= 0xE007F)
        {
            return true;
        }

        if (!IsRegionalIndicator(code))
        {
            return false;
        }

        // The indicators right before this one: an odd count makes this the pair's second.
        int run = 0;
        for (int i = index; i >= 2 && char.IsLowSurrogate(text[i - 1]) && char.IsHighSurrogate(text[i - 2])
            && IsRegionalIndicator(char.ConvertToUtf32(text[i - 2], text[i - 1])); i -= 2)
        {
            run++;
        }

        return run % 2 == 1;
    }

    private static bool IsRegionalIndicator(int code) => code is >= 0x1F1E6 and <= 0x1F1FF;

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
