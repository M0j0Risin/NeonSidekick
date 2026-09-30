using System.Buffers;
using System.Globalization;
using System.Text;

namespace NeonSidekick.UI;

/// <summary>
/// The word a double-click on the draft selects (2026-09-30, the user's ask: double-click anywhere inside "quick" in "the
/// quick brown fox" and the whole word is selected), pure over the draft's text — the editor's own, where a collapsed paste
/// or a picture is one token character. The clicked character's class decides the span, as Windows' edit controls and the
/// browsers do: a word character (a letter, a digit or <c>_</c>, by <see cref="Rune"/> so a letter past the BMP counts)
/// takes the run of word characters, so <c>quick,</c> gives <c>quick</c> and <c>quick-brown</c> gives <c>quick</c>;
/// whitespace the run of blanks (a line break is never taken); a token (<see cref="PasteBlocks.IsToken"/>) itself alone,
/// its whole label; anything else the run of the same punctuation. A click past a row's end lands on the text's end or its
/// line break, and the character before is taken.
/// </summary>
public static class DraftWords
{
    private enum Kind
    {
        Word,
        Space,
        Token,
        Break,
        Other,
    }

    /// <summary>
    /// The span <c>[Start, End)</c> the character at <paramref name="index"/> belongs to — the one before when
    /// <paramref name="index"/> is the end or a line break; an empty span (<c>Start == End == index</c>) when there is none.
    /// </summary>
    public static (int Start, int End) At(string text, int index)
    {
        ArgumentNullException.ThrowIfNull(text);
        index = Math.Clamp(index, 0, text.Length);
        if (index < text.Length)
        {
            index = StartOfElement(text, index);   // a pair's low half is its high half's element
        }

        if (index == text.Length || text[index] == '\n')
        {
            if (index == 0 || text[index - 1] == '\n')
            {
                return (index, index);
            }

            index = StartOfElement(text, index - 1);
        }

        var kind = KindAt(text, index, out _);
        if (kind == Kind.Token)
        {
            return (index, index + 1);
        }

        string? other = kind == Kind.Other ? text.Substring(index, LengthAt(text, index)) : null;
        int start = index;
        while (start > 0)
        {
            int before = StartOfElement(text, start - 1);
            if (!Same(text, before, kind, other))
            {
                break;
            }

            start = before;
        }

        int end = index;
        while (end < text.Length && Same(text, end, kind, other))
        {
            end += LengthAt(text, end);
        }

        return (start, end);
    }

    // Whether the element at `at` continues a run of `kind` (punctuation only with the same mark: "--", "...").
    private static bool Same(string text, int at, Kind kind, string? other)
    {
        if (KindAt(text, at, out int length) != kind)
        {
            return false;
        }

        return kind != Kind.Other || (length == other!.Length && string.CompareOrdinal(text, at, other, 0, length) == 0);
    }

    private static Kind KindAt(string text, int at, out int length)
    {
        char c = text[at];
        length = 1;
        if (PasteBlocks.IsToken(c))
        {
            return Kind.Token;
        }

        if (c == '\n')
        {
            return Kind.Break;
        }

        if (Rune.DecodeFromUtf16(text.AsSpan(at), out var rune, out int consumed) == OperationStatus.Done)
        {
            length = consumed;
            if (Rune.IsLetterOrDigit(rune) || rune.Value == '_' || Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark)
            {
                return Kind.Word;
            }

            return Rune.IsWhiteSpace(rune) ? Kind.Space : Kind.Other;
        }

        return char.IsWhiteSpace(c) ? Kind.Space : Kind.Other;
    }

    private static int LengthAt(string text, int at)
    {
        KindAt(text, at, out int length);
        return length;
    }

    // The start of the element ending at or containing `at`: a low surrogate steps back onto its high half.
    private static int StartOfElement(string text, int at) =>
        at > 0 && char.IsLowSurrogate(text[at]) && char.IsHighSurrogate(text[at - 1]) ? at - 1 : at;
}
