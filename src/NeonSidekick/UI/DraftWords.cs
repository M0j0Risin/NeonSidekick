using System.Buffers;
using System.Globalization;
using System.Text;

namespace NeonSidekick.UI;

/// <summary>
/// The word a double-click on the draft selects (2026-09-30, the user's ask: double-click anywhere inside "quick" in "the
/// quick brown fox" and the whole word is selected), pure over the draft's text — the editor's own, where a collapsed paste
/// or a picture is one token character. The draft is walked by grapheme cluster (later on 2026-09-30, the review's catch: by
/// rune, an emoji's variation selector joined the next word and a ZWJ family split), and the clicked cluster's first rune
/// decides the span, as Windows' edit controls and the browsers do: a word character (a letter, a digit or <c>_</c>, a
/// combining mark riding along in its cluster) takes the run of word clusters, so <c>quick,</c> gives <c>quick</c> and
/// <c>quick-brown</c> gives <c>quick</c>; whitespace the run of blanks (a line break is never taken); a token
/// (<see cref="PasteBlocks.IsToken"/>) itself alone, its whole label; anything else — a mark, an emoji — the run of the same
/// cluster. A click past a row's end lands on the text's end or its line break, and the cluster before is taken.
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
    /// The span <c>[Start, End)</c> the cluster at <paramref name="index"/> belongs to — the one before when
    /// <paramref name="index"/> is the end or a line break; an empty span (<c>Start == End</c>) when there is none.
    /// </summary>
    public static (int Start, int End) At(string text, int index)
    {
        ArgumentNullException.ThrowIfNull(text);
        index = Math.Clamp(index, 0, text.Length);
        var starts = ClusterStarts(text);
        int last = starts.Count - 1;   // starts[last] is the text's end
        int e = 0;
        while (e < last && starts[e + 1] <= index)
        {
            e++;   // the cluster holding index: a click on a pair's low half, or on a cluster's mark, is its cluster's
        }

        if (e == last || text[starts[e]] == '\n')
        {
            if (e == 0 || text[starts[e - 1]] == '\n')
            {
                return (starts[e], starts[e]);
            }

            e--;
        }

        var kind = KindOf(text, starts[e]);
        if (kind == Kind.Token)
        {
            return (starts[e], starts[e + 1]);
        }

        string? other = kind == Kind.Other ? text[starts[e]..starts[e + 1]] : null;
        int first = e;
        while (first > 0 && Same(text, starts, first - 1, kind, other))
        {
            first--;
        }

        int end = e;
        while (end + 1 < last && Same(text, starts, end + 1, kind, other))
        {
            end++;
        }

        return (starts[first], starts[end + 1]);
    }

    // Whether cluster `c` continues a run of `kind` (anything else only with the same cluster: "--", "...", "⚠️⚠️").
    private static bool Same(string text, List<int> starts, int c, Kind kind, string? other)
    {
        if (KindOf(text, starts[c]) != kind)
        {
            return false;
        }

        int length = starts[c + 1] - starts[c];
        return kind != Kind.Other || (length == other!.Length && string.CompareOrdinal(text, starts[c], other, 0, length) == 0);
    }

    private static Kind KindOf(string text, int at)
    {
        char c = text[at];
        if (PasteBlocks.IsToken(c))
        {
            return Kind.Token;
        }

        if (c == '\n')
        {
            return Kind.Break;
        }

        if (Rune.DecodeFromUtf16(text.AsSpan(at), out var rune, out _) == OperationStatus.Done)
        {
            if (Rune.IsLetterOrDigit(rune) || rune.Value == '_')
            {
                return Kind.Word;
            }

            return Rune.IsWhiteSpace(rune) ? Kind.Space : Kind.Other;
        }

        return char.IsWhiteSpace(c) ? Kind.Space : Kind.Other;
    }

    // Each grapheme cluster's start, then the text's end. A token and a line break are always clusters of their own: a mark
    // after a token is no part of its label, and a break never rides in the cluster before it.
    private static List<int> ClusterStarts(string text)
    {
        var starts = new List<int>();
        int i = 0;
        while (i < text.Length)
        {
            starts.Add(i);
            int length = 1;
            if (!PasteBlocks.IsToken(text[i]) && text[i] != '\n')
            {
                length = Math.Max(1, StringInfo.GetNextTextElementLength(text.AsSpan(i)));
                for (int j = 1; j < length; j++)
                {
                    if (PasteBlocks.IsToken(text[i + j]) || text[i + j] == '\n')
                    {
                        length = j;
                        break;
                    }
                }
            }

            i += length;
        }

        starts.Add(text.Length);
        return starts;
    }
}
