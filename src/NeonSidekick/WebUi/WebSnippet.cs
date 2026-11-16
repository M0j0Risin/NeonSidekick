using NeonSidekick.UI;

namespace NeonSidekick.WebUi;

/// <summary>
/// A session search hit's snippet for the Sessions tab (2026-11-16, plan lucid-roaming-lantern #40), from the whole marked column text
/// <c>SessionStore.Search</c> returns with marks. The text a user sent keeps a saved picture's or PDF's label with its full path, and a match
/// may fall inside one, so the marks can't be kept through the cleaning: the matched words are read off the well-formed pairs, the pairs
/// taken out, the labels cleaned (<see cref="PasteBlocks.WithoutSavedPaths"/>), and the words found again in what is left, at a word's start
/// and ignoring case. A word that matched only inside a path is gone with it. Then a window of <see cref="Sessions.SessionStore.SnippetTokens"/>
/// words around the first match, moved out to a label's edge rather than cutting one, with … at a cut. A stray marker shows as text. Pure.
/// </summary>
public static class WebSnippet
{
    /// <summary>The pair the page's search asks for: private-use characters no reply writes in practice.</summary>
    public const string Open = "";

    public const string Close = "";

    /// <summary>The parts of <paramref name="marked"/>'s window, matches flagged.</summary>
    public static IReadOnlyList<WebSnippetPart> Parts(string marked, int words = Sessions.SessionStore.SnippetTokens)
    {
        ArgumentNullException.ThrowIfNull(marked);
        var (plain, terms) = Unmark(marked);
        string text = PasteBlocks.WithoutSavedPaths(plain).Replace('\r', ' ').Replace('\n', ' ');
        var matches = Find(text, terms);
        var (start, end) = Window(text, matches.Count > 0 ? matches[0].Start : 0, words);

        var parts = new List<WebSnippetPart>();
        if (start > 0)
        {
            parts.Add(new WebSnippetPart("…"));
        }

        int at = start;
        foreach (var (s, length) in matches)
        {
            int ms = Math.Max(s, start);
            int me = Math.Min(s + length, end);
            if (me <= ms)
            {
                continue;
            }

            if (ms > at)
            {
                parts.Add(new WebSnippetPart(text[at..ms]));
            }

            parts.Add(new WebSnippetPart(text[ms..me], true));
            at = me;
        }

        if (end > at)
        {
            parts.Add(new WebSnippetPart(text[at..end]));
        }

        if (end < text.Length)
        {
            parts.Add(new WebSnippetPart("…"));
        }

        return Merge(parts);
    }

    /// <summary>The text with its well-formed pairs taken out, and the distinct words they held.</summary>
    private static (string Plain, IReadOnlyList<string> Terms) Unmark(string marked)
    {
        var plain = new System.Text.StringBuilder(marked.Length);
        var terms = new List<string>();
        int i = 0;
        while (i < marked.Length)
        {
            int open = marked.IndexOf(Open, i, StringComparison.Ordinal);
            if (open < 0)
            {
                plain.Append(marked, i, marked.Length - i);
                break;
            }

            int close = marked.IndexOf(Close, open + Open.Length, StringComparison.Ordinal);
            int next = marked.IndexOf(Open, open + Open.Length, StringComparison.Ordinal);
            if (close < 0 || (next >= 0 && next < close))
            {
                // A stray opener: kept as text.
                plain.Append(marked, i, open + Open.Length - i);
                i = open + Open.Length;
                continue;
            }

            plain.Append(marked, i, open - i);
            string term = marked[(open + Open.Length)..close];
            plain.Append(term);
            if (term.Trim().Length > 0 && !terms.Contains(term.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                terms.Add(term.Trim());
            }

            i = close + Close.Length;
        }

        return (plain.ToString(), terms);
    }

    /// <summary>Every place a term starts a word in <paramref name="text"/>, ignoring case, in order, overlaps merged.</summary>
    private static List<(int Start, int Length)> Find(string text, IReadOnlyList<string> terms)
    {
        var found = new List<(int Start, int Length)>();
        foreach (string term in terms)
        {
            int at = 0;
            while ((at = text.IndexOf(term, at, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                if (at == 0 || !char.IsLetterOrDigit(text[at - 1]))
                {
                    found.Add((at, term.Length));
                }

                at += term.Length;
            }
        }

        found.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(int Start, int Length)>();
        foreach (var hit in found)
        {
            if (merged.Count > 0 && hit.Start <= merged[^1].Start + merged[^1].Length)
            {
                var last = merged[^1];
                merged[^1] = (last.Start, Math.Max(last.Start + last.Length, hit.Start + hit.Length) - last.Start);
            }
            else
            {
                merged.Add(hit);
            }
        }

        return merged;
    }

    /// <summary>The window's bounds: a third of the words before the match, the rest from it, never cutting a label.</summary>
    private static (int Start, int End) Window(string text, int match, int words)
    {
        var starts = new List<int>();
        for (int i = 0; i < text.Length; i++)
        {
            if (!char.IsWhiteSpace(text[i]) && (i == 0 || char.IsWhiteSpace(text[i - 1])))
            {
                starts.Add(i);
            }
        }

        if (starts.Count <= words)
        {
            return (0, text.Length);
        }

        int word = starts.FindLastIndex(s => s <= match);
        int first = Math.Clamp(word - (words / 3), 0, starts.Count - words);
        int start = starts[first];
        int end = first + words < starts.Count ? starts[first + words] : text.Length;

        foreach (var (s, length) in Labels(text))
        {
            if (start > s && start < s + length)
            {
                start = s;
            }

            if (end > s && end < s + length)
            {
                end = s + length;
            }
        }

        return (start, text[..end].TrimEnd().Length);
    }

    private static IEnumerable<(int Start, int Length)> Labels(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '[' && PasteBlocks.LabelLengthAt(text, i) is > 0 and var length)
            {
                yield return (i, length);
                i += length - 1;
            }
        }
    }

    /// <summary>Neighboring parts of the same kind joined.</summary>
    private static List<WebSnippetPart> Merge(List<WebSnippetPart> parts)
    {
        var merged = new List<WebSnippetPart>();
        foreach (var part in parts)
        {
            if (merged.Count > 0 && merged[^1].Match == part.Match && part.Text != "…" && merged[^1].Text != "…")
            {
                merged[^1] = merged[^1] with { Text = merged[^1].Text + part.Text };
            }
            else
            {
                merged.Add(part);
            }
        }

        return merged;
    }
}
