using System.Globalization;
using System.Text;
using NeonSidekick.Pdf;

namespace NeonSidekick.Web;

/// <summary>One passage <c>web_fetch</c>'s <c>find</c> returns: where it starts in the page's text and the original text itself.</summary>
public sealed record FoundPassage(int Offset, string Text, bool InOrder);

/// <summary>What <see cref="WebFind.Passages"/> found: the passages to show, best first, and how many matched in all.</summary>
public sealed record FindResult(IReadOnlyList<FoundPassage> Shown, int Total);

/// <summary>
/// <c>web_fetch</c>'s <c>find</c> (2026-11-15, the web-tools plan's §1, feature idea #26): the passages of a long page that hold
/// <b>every</b> word asked, by <c>read_pdf</c>'s rule (<see cref="PdfFind"/>): case and accents folded, substrings matched
/// (<c>warrant</c> finds <c>warranty</c>), a word broken by a line-end hyphen found too. The search runs over folded copies that map
/// back to the original, so each passage's offset is the one <c>offset</c> reads from. Pure; pinned by <c>WebFindTests</c>.
/// </summary>
public static class WebFind
{
    /// <summary>About how long a passage is, before it is cut to word edges and merged with its neighbors.</summary>
    public const int PassageChars = 600;

    /// <summary>The most passages one answer shows; the rest are counted.</summary>
    public const int MaxPassages = 10;

    /// <summary>How far a passage's edge moves to land on a space or a line break.</summary>
    private const int EdgeSlack = 60;

    /// <summary>The passages of <paramref name="text"/> holding every one of <paramref name="words"/> (from <see cref="PdfFind.Words"/>).</summary>
    public static FindResult Passages(string text, IReadOnlyList<string> words)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(words);
        if (words.Count == 0 || text.Length == 0)
        {
            return new FindResult([], 0);
        }

        var copies = Copies(text);

        // Every passage holding all the words holds the rarest one: its hits are the centers to try.
        List<int>? centers = null;
        foreach (string word in words)
        {
            var hits = Hits(copies, word);
            if (centers is null || hits.Count < centers.Count)
            {
                centers = hits;
            }

            if (hits.Count == 0)
            {
                return new FindResult([], 0);
            }
        }

        var windows = new List<(int Start, int End)>();
        foreach (int center in centers!)
        {
            var (start, end) = Window(text, center);
            if (windows.Count > 0 && start < windows[^1].End)
            {
                if (center < windows[^1].End || end <= windows[^1].End)
                {
                    // The hit is in the passage before already.
                    continue;
                }

                if (end - windows[^1].Start <= 2 * PassageChars && PdfFind.Matches(text[start..end], words))
                {
                    // Overlapping the one before: one passage, at most two passages long.
                    windows[^1] = (windows[^1].Start, end);
                    continue;
                }

                start = windows[^1].End;
            }

            if (end > start && PdfFind.Matches(text[start..end], words))
            {
                windows.Add((start, end));
            }
        }

        var passages = windows.Select(w => new FoundPassage(w.Start, text[w.Start..w.End], InOrder(text[w.Start..w.End], words))).ToList();
        var shown = passages.Where(p => p.InOrder).Concat(passages.Where(p => !p.InOrder)).Take(MaxPassages).ToList();
        return new FindResult(shown, passages.Count);
    }

    /// <summary>Whether <paramref name="passage"/> holds <paramref name="words"/> one after another in the order asked (any copy).</summary>
    public static bool InOrder(string passage, IReadOnlyList<string> words)
    {
        ArgumentNullException.ThrowIfNull(passage);
        ArgumentNullException.ThrowIfNull(words);
        foreach (var (folded, _) in Copies(passage))
        {
            int at = 0;
            bool all = true;
            foreach (string word in words)
            {
                int found = folded.IndexOf(word, at, StringComparison.Ordinal);
                if (found < 0)
                {
                    all = false;
                    break;
                }

                at = found + word.Length;
            }

            if (all)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// <paramref name="text"/> folded (<see cref="Fold"/>) and that copy with each line-end hyphen joined away and kept
    /// (<see cref="PdfFind.Joined"/>'s three ways), each with a map from its characters back to the original's.
    /// </summary>
    public static IReadOnlyList<(string Text, int[] Map)> Copies(string text)
    {
        var (folded, map) = Fold(text);
        return [(folded, map), Join(folded, map, keepHyphen: false), Join(folded, map, keepHyphen: true)];
    }

    /// <summary>
    /// <paramref name="text"/> lowercased with its accents off (<see cref="PdfFind.Fold"/>'s rule, char by char so each folded character
    /// knows where it came from), and the map: the original index of each folded character, then one past the original's end.
    /// </summary>
    public static (string Text, int[] Map) Fold(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sb = new StringBuilder(text.Length);
        var map = new List<int>(text.Length + 1);
        for (int i = 0; i < text.Length; i++)
        {
            int width = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
            string unit = text.Substring(i, width);
            string decomposed = unit.IsNormalized(NormalizationForm.FormD) ? unit : unit.Normalize(NormalizationForm.FormD);
            foreach (char c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                {
                    sb.Append(char.ToLowerInvariant(c));
                    map.Add(i);
                }
            }

            i += width - 1;
        }

        map.Add(text.Length);
        return (sb.ToString(), [.. map]);
    }

    // The folded copy with "-\r\n", "-\n" (the hyphen dropped or kept) and a soft hyphen's "­\n" taken out; only characters go, so the
    // map still points into the original.
    private static (string Text, int[] Map) Join(string folded, int[] map, bool keepHyphen)
    {
        var sb = new StringBuilder(folded.Length);
        var joined = new List<int>(map.Length);
        for (int i = 0; i < folded.Length; i++)
        {
            char c = folded[i];
            int skip = 0;
            if (c == '-' && i + 1 < folded.Length && folded[i + 1] == '\n')
            {
                skip = 2;
            }
            else if (c == '-' && i + 2 < folded.Length && folded[i + 1] == '\r' && folded[i + 2] == '\n')
            {
                skip = 3;
            }
            else if (c == '­' && i + 1 < folded.Length && folded[i + 1] == '\n')
            {
                skip = 2;
                if (keepHyphen)
                {
                    // A soft hyphen goes either way: it was never a printed one.
                    i += skip - 1;
                    continue;
                }
            }

            if (skip > 0)
            {
                if (keepHyphen && c == '-')
                {
                    sb.Append('-');
                    joined.Add(map[i]);
                }

                i += skip - 1;
                continue;
            }

            sb.Append(c);
            joined.Add(map[i]);
        }

        joined.Add(map[^1]);
        return (sb.ToString(), [.. joined]);
    }

    // The original offsets where word starts, over all the copies, once each, in order.
    private static List<int> Hits(IReadOnlyList<(string Text, int[] Map)> copies, string word)
    {
        var hits = new SortedSet<int>();
        foreach (var (folded, map) in copies)
        {
            for (int at = folded.IndexOf(word, StringComparison.Ordinal); at >= 0; at = folded.IndexOf(word, at + 1, StringComparison.Ordinal))
            {
                hits.Add(map[at]);
            }
        }

        return [.. hits];
    }

    // About PassageChars around center, each edge moved to a space or a line break when one is near, never splitting a surrogate pair.
    private static (int Start, int End) Window(string text, int center)
    {
        int start = Math.Max(0, center - PassageChars / 2);
        int end = Math.Min(text.Length, start + PassageChars);
        start = Math.Max(0, Math.Min(start, end - PassageChars));
        if (start > 0)
        {
            int limit = Math.Min(center, start + EdgeSlack);
            for (int i = start; i < limit; i++)
            {
                if (char.IsWhiteSpace(text[i]))
                {
                    start = i + 1;
                    break;
                }
            }
        }

        if (end < text.Length)
        {
            int limit = Math.Max(center + 1, end - EdgeSlack);
            for (int i = end - 1; i >= limit; i--)
            {
                if (char.IsWhiteSpace(text[i]))
                {
                    end = i;
                    break;
                }
            }
        }

        start = Files.TextCut.SafeStart(text, start);
        if (end > start && end < text.Length && char.IsLowSurrogate(text[end]) && char.IsHighSurrogate(text[end - 1]))
        {
            end--;
        }

        return (start, Math.Max(end, Math.Min(text.Length, start + 1)));
    }
}
