using System.Globalization;
using System.Text;
using NeonSidekick.Pdf;

namespace NeonSidekick.Web;

/// <summary>One passage <c>web_fetch</c>'s <c>find</c> returns: where it starts in the page's text and the original text itself.</summary>
public sealed record FoundPassage(int Offset, string Text, bool InOrder);

/// <summary>
/// What <see cref="WebFind.Passages"/> found: the passages to show, best first, how many matched at or after the offset asked, and the
/// offset the next page of passages starts at when the cap cut the list (null when every one was shown).
/// </summary>
public sealed record FindResult(IReadOnlyList<FoundPassage> Shown, int Total, int? NextOffset = null);

/// <summary>
/// A page's text folded for <c>find</c> (2026-11-16, the web-tools review's #8): the folded copies with their maps back to the original,
/// built once per fetched page and reused while the model pages through its passages. Immutable.
/// </summary>
public sealed class FoldedText
{
    internal FoldedText(string source, IReadOnlyList<(string Text, int[]? Map)> copies)
    {
        Source = source;
        Copies = copies;
    }

    /// <summary>The text these copies were folded from.</summary>
    public string Source { get; }

    /// <summary>The folded copies, each with its map (null where every folded character sits at its own index).</summary>
    internal IReadOnlyList<(string Text, int[]? Map)> Copies { get; }
}

/// <summary>
/// <c>web_fetch</c>'s <c>find</c> (2026-11-15, the web-tools plan's §1, feature idea #26): the passages of a long page that hold
/// <b>every</b> word asked, by <c>read_pdf</c>'s rule (<see cref="PdfFind"/>): case and accents folded, substrings matched
/// (<c>warrant</c> finds <c>warranty</c>), a word broken by a line-end hyphen found too. The search runs over folded copies that map
/// back to the original, so each passage's offset is the one <c>offset</c> reads from. Pure; pinned by <c>WebFindTests</c>.
/// <para>
/// Since 2026-11-16 (the web-tools review's #1, #6, #8, #73, #78, #103, #117, #186): folded per grapheme cluster and recomposed, so Hangul
/// and Tamil's two-part vowels match; format characters (a soft hyphen inside a word, a zero-width space) dropped; each word's hits kept
/// as a sorted list and a window checked against them by binary search, never re-folded; a window slides toward the other words' nearest
/// hits; an overlapping window that can't merge is kept as it is; an edge with no space near moves outward to the next one, so a link
/// is kept whole.
/// </para>
/// </summary>
public static class WebFind
{
    /// <summary>About how long a passage is, before it is cut to word edges and merged with its neighbors.</summary>
    public const int PassageChars = 600;

    /// <summary>The most passages one answer shows; the rest are counted, and the next answer starts at the first left out.</summary>
    public const int MaxPassages = 10;

    /// <summary>How far a passage's edge moves inward to land on a space or a line break.</summary>
    private const int EdgeSlack = 60;

    /// <summary>How far an edge moves outward when no space is near inward (2026-11-16, the review's #186): a long link kept whole.</summary>
    public const int LinkSlack = 300;

    private const char ZeroWidthJoiner = '‍';

    private const char SoftHyphen = '­';

    /// <summary>
    /// The passages of <paramref name="text"/> holding every one of <paramref name="words"/> (from <see cref="PdfFind.Words"/>), from
    /// <paramref name="from"/> on. <paramref name="folded"/> is the page folded already (<see cref="Fold(string)"/>, of this text or a same-length copy with the app's spans blanked), or null to fold it here.
    /// </summary>
    public static FindResult Passages(string text, IReadOnlyList<string> words, int from = 0, FoldedText? folded = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(words);
        if (words.Count == 0 || text.Length == 0)
        {
            return new FindResult([], 0);
        }

        // A folded copy of the same length is this text or its masked copy (WebFetchTool.Masked): its offsets index this text.
        folded = folded is not null && folded.Source.Length == text.Length ? folded : Fold(text);
        var hits = new List<IReadOnlyList<(int Start, int End)>>(words.Count);
        foreach (string word in words)
        {
            var list = Hits(folded, word);
            if (list.Count == 0)
            {
                return new FindResult([], 0);
            }

            hits.Add(list);
        }

        // Every passage holding all the words holds the rarest one: its hits are the centers to try.
        int rarest = 0;
        for (int w = 1; w < hits.Count; w++)
        {
            if (hits[w].Count < hits[rarest].Count)
            {
                rarest = w;
            }
        }

        var windows = new List<(int Start, int End)>();
        foreach (var (center, _) in hits[rarest])
        {
            if (windows.Count > 0 && center < windows[^1].End)
            {
                continue;   // the hit is in the passage before already
            }

            if (Best(text, center, hits, rarest) is not { } window)
            {
                continue;
            }

            if (windows.Count > 0 && window.Start < windows[^1].End)
            {
                var last = windows[^1];
                if (window.End <= last.End)
                {
                    continue;
                }

                if (window.End - last.Start <= 2 * PassageChars)
                {
                    // Overlapping the one before: one passage, at most two passages long.
                    windows[^1] = (last.Start, window.End);
                    continue;
                }

                // Moved past the one before; if that loses a word, the window is kept as it was, overlap and all (#6).
                var shifted = (Start: last.End, window.End);
                windows.Add(Holds(hits, shifted.Start, shifted.End) ? shifted : window);
                continue;
            }

            windows.Add(window);
        }

        var after = windows.Where(w => w.Start >= from).ToList();
        var page = after.Take(MaxPassages).ToList();
        int? next = after.Count > MaxPassages ? after[MaxPassages].Start : null;
        var passages = page.Select(w => new FoundPassage(w.Start, text[w.Start..w.End], InOrder(hits, w.Start, w.End))).ToList();
        var shown = passages.Where(p => p.InOrder).Concat(passages.Where(p => !p.InOrder)).ToList();
        return new FindResult(shown, after.Count, next);
    }

    /// <summary>Whether <paramref name="passage"/> holds <paramref name="words"/> one after another in the order asked (any copy).</summary>
    public static bool InOrder(string passage, IReadOnlyList<string> words)
    {
        ArgumentNullException.ThrowIfNull(passage);
        ArgumentNullException.ThrowIfNull(words);
        var folded = Fold(passage);
        var hits = words.Select(w => Hits(folded, w)).ToList();
        return hits.All(h => h.Count > 0) && InOrder(hits, 0, passage.Length);
    }

    /// <summary>
    /// <paramref name="text"/> folded for <c>find</c>: lowercased, its accents off and recomposed per grapheme cluster, its format characters
    /// dropped, then that copy with each line-end hyphen joined away and kept (<see cref="PdfFind.Joined"/>'s three ways), each with a map
    /// back to the original's characters. Text that is ASCII and holds no line-end hyphen is one copy with no map.
    /// </summary>
    public static FoldedText Fold(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var (folded, map) = FoldOne(text);
        bool hyphens = folded.Contains("-\n", StringComparison.Ordinal) || folded.Contains("-\r\n", StringComparison.Ordinal)
            || folded.Contains(SoftHyphen + "\n", StringComparison.Ordinal);
        return hyphens
            ? new FoldedText(text, [(folded, map), Join(folded, map, keepHyphen: false), Join(folded, map, keepHyphen: true)])
            : new FoldedText(text, [(folded, map)]);
    }

    /// <summary>
    /// <paramref name="text"/> folded (<see cref="Fold(string)"/>'s first copy) with its map: the original index of each folded character, then
    /// one past the original's end; null when every character folded to one at the same index (an ASCII page).
    /// </summary>
    public static (string Text, int[]? Map) FoldOne(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (IsAscii(text))
        {
            return (text.ToLowerInvariant(), null);
        }

        var sb = new StringBuilder(text.Length);
        var map = new List<int>(text.Length + 1);
        var clusters = StringInfo.GetTextElementEnumerator(text);
        while (clusters.MoveNext())
        {
            int start = clusters.ElementIndex;
            string cluster = clusters.GetTextElement();
            if (cluster.Length == 1 && cluster[0] < 0x80)
            {
                sb.Append(char.ToLowerInvariant(cluster[0]));
                map.Add(start);
                continue;
            }

            // A soft hyphen ending a line stays, for the joins; inside a word it goes, as a zero-width space does.
            bool lineEnd = start + cluster.Length < text.Length && text[start + cluster.Length] is '\n' or '\r';
            foreach (char c in FoldCluster(cluster, lineEnd))
            {
                sb.Append(c);
                map.Add(start);
            }
        }

        map.Add(text.Length);
        return (sb.ToString(), [.. map]);
    }

    // One grapheme cluster decomposed, its non-spacing marks and format characters dropped (a joiner between two parts of it kept),
    // lowercased and recomposed, so a cluster never composes across its edge and a FormC word matches it.
    private static string FoldCluster(string cluster, bool lineEnd)
    {
        string decomposed = cluster.IsNormalized(NormalizationForm.FormD) ? cluster : cluster.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        for (int i = 0; i < decomposed.Length; i++)
        {
            char c = decomposed[i];
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (category == UnicodeCategory.Format && !(c == ZeroWidthJoiner && i > 0 && i < decomposed.Length - 1) && !(c == SoftHyphen && lineEnd))
            {
                continue;
            }

            sb.Append(char.ToLowerInvariant(c));
        }

        string lowered = sb.ToString();
        return lowered.IsNormalized(NormalizationForm.FormC) ? lowered : lowered.Normalize(NormalizationForm.FormC);
    }

    private static bool IsAscii(string text)
    {
        foreach (char c in text)
        {
            if (c >= 0x80)
            {
                return false;
            }
        }

        return true;
    }

    // The folded copy with "-\r\n", "-\n" (the hyphen dropped or kept) and a soft hyphen's "­\n" taken out; only characters go, so the
    // map still points into the original.
    private static (string Text, int[]? Map) Join(string folded, int[]? map, bool keepHyphen)
    {
        int At(int i) => map is null ? i : map[i];
        var sb = new StringBuilder(folded.Length);
        var joined = new List<int>(folded.Length + 1);
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
            else if (c == SoftHyphen && i + 1 < folded.Length && folded[i + 1] == '\n')
            {
                // A soft hyphen goes either way: it was never a printed one.
                i += 1;
                continue;
            }

            if (skip > 0)
            {
                if (keepHyphen)
                {
                    sb.Append('-');
                    joined.Add(At(i));
                }

                i += skip - 1;
                continue;
            }

            sb.Append(c);
            joined.Add(At(i));
        }

        joined.Add(map is null ? folded.Length : map[^1]);
        return (sb.ToString(), [.. joined]);
    }

    // Where word is found in the original, over all the copies: each hit's start and end, sorted by start, once each.
    private static List<(int Start, int End)> Hits(FoldedText folded, string word)
    {
        var hits = new SortedDictionary<int, int>();
        foreach (var (copy, map) in folded.Copies)
        {
            for (int at = copy.IndexOf(word, StringComparison.Ordinal); at >= 0; at = copy.IndexOf(word, at + 1, StringComparison.Ordinal))
            {
                int start = map is null ? at : map[at];
                int end = map is null ? at + word.Length : map[at + word.Length];
                if (!hits.TryGetValue(start, out int kept) || end < kept)
                {
                    hits[start] = end;
                }
            }
        }

        return [.. hits.Select(h => (h.Key, Math.Max(h.Value, h.Key + 1)))];
    }

    // The first hit starting at or after start (binary search), or -1.
    private static int FirstFrom(IReadOnlyList<(int Start, int End)> hits, int start)
    {
        int lo = 0;
        int hi = hits.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (hits[mid].Start < start)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo < hits.Count ? lo : -1;
    }

    // Whether [start, end) holds a hit of every word.
    private static bool Holds(IReadOnlyList<IReadOnlyList<(int Start, int End)>> hits, int start, int end) =>
        hits.All(h => HitWithin(h, start, end) >= 0);

    // The index of a hit lying wholly in [start, end), or -1.
    private static int HitWithin(IReadOnlyList<(int Start, int End)> hits, int start, int end)
    {
        for (int i = FirstFrom(hits, start); i >= 0 && i < hits.Count && hits[i].Start < end; i++)
        {
            if (hits[i].End <= end)
            {
                return i;
            }
        }

        return -1;
    }

    // Whether the words' hits in [start, end) run one after another in the order asked.
    private static bool InOrder(IReadOnlyList<IReadOnlyList<(int Start, int End)>> hits, int start, int end)
    {
        int at = start;
        foreach (var list in hits)
        {
            int i = HitWithin(list, at, end);
            if (i < 0)
            {
                return false;
            }

            at = list[i].End;
        }

        return true;
    }

    // The passage around center holding every word: the plain window first, else one that slides toward the other words' nearest hits
    // (#103: words 300–600 characters apart were reported missing); null when no window of about PassageChars holds them all.
    private static (int Start, int End)? Best(string text, int center, IReadOnlyList<IReadOnlyList<(int Start, int End)>> hits, int rarest)
    {
        var window = Window(text, center, center + 1);
        if (Holds(hits, window.Start, window.End))
        {
            return window;
        }

        int low = center;
        int high = hits[rarest][FirstFrom(hits[rarest], center)].End;
        for (int w = 0; w < hits.Count; w++)
        {
            if (w == rarest)
            {
                continue;
            }

            var (start, end) = Nearest(hits[w], center);
            low = Math.Min(low, start);
            high = Math.Max(high, end);
        }

        if (high - low > PassageChars)
        {
            return null;
        }

        var slid = Window(text, (low + high) / 2, high, low);
        return Holds(hits, slid.Start, slid.End) ? slid : null;
    }

    // The hit nearest center, before or after it.
    private static (int Start, int End) Nearest(IReadOnlyList<(int Start, int End)> hits, int center)
    {
        int after = FirstFrom(hits, center);
        int before = after < 0 ? hits.Count - 1 : after - 1;
        if (after < 0)
        {
            return hits[before];
        }

        return before >= 0 && center - hits[before].Start < hits[after].Start - center ? hits[before] : hits[after];
    }

    // About PassageChars around center, each edge moved to a space or a line break when one is near: inward within EdgeSlack, else outward
    // within LinkSlack (#186: a link longer than the slack was cut mid-URL), else cut where it is; never splitting a surrogate pair. The
    // inward moves never pass mustStart or mustEnd, so the words that placed the window stay inside it.
    private static (int Start, int End) Window(string text, int center, int mustEnd, int? mustStart = null)
    {
        int keepFrom = mustStart ?? center;
        int start = Math.Max(0, center - PassageChars / 2);
        int end = Math.Min(text.Length, start + PassageChars);
        start = Math.Max(0, Math.Min(start, end - PassageChars));
        start = Math.Min(start, keepFrom);
        end = Math.Max(end, Math.Min(text.Length, mustEnd));
        if (start > 0 && !char.IsWhiteSpace(text[start - 1]))
        {
            int moved = -1;
            int limit = Math.Min(keepFrom, start + EdgeSlack);
            for (int i = start; i < limit; i++)
            {
                if (char.IsWhiteSpace(text[i]))
                {
                    moved = i + 1;
                    break;
                }
            }

            if (moved < 0)
            {
                for (int i = start - 1; i >= Math.Max(0, start - LinkSlack); i--)
                {
                    if (char.IsWhiteSpace(text[i]))
                    {
                        moved = i + 1;
                        break;
                    }
                }
            }

            start = moved >= 0 ? moved : start;
        }

        if (end < text.Length && !char.IsWhiteSpace(text[end]))
        {
            int moved = -1;
            int limit = Math.Max(Math.Max(center + 1, mustEnd), end - EdgeSlack);
            for (int i = end - 1; i >= limit; i--)
            {
                if (char.IsWhiteSpace(text[i]))
                {
                    moved = i;
                    break;
                }
            }

            if (moved < 0)
            {
                for (int i = end; i < Math.Min(text.Length, end + LinkSlack); i++)
                {
                    if (char.IsWhiteSpace(text[i]))
                    {
                        moved = i;
                        break;
                    }
                }
            }

            end = moved >= 0 ? moved : end;
        }

        start = Files.TextCut.SafeStart(text, start);
        if (end > start && end < text.Length && char.IsLowSurrogate(text[end]) && char.IsHighSurrogate(text[end - 1]))
        {
            end--;
        }

        return (start, Math.Max(end, Math.Min(text.Length, start + 1)));
    }
}
