using NeonSidekick.UI;

namespace NeonSidekick.Emojis;

/// <summary>
/// The avatar picker's search (2026-10-10): every word typed must match the emoji's name or one of its CLDR keywords, and the
/// emoji are ranked by how well — a whole name word 8, the start of a name word 6, a whole keyword 4, the start of a keyword 2
/// (a word of two letters or more), one less when it took letting the word's plural <c>s</c> go (as <c>neon_help</c>'s search
/// does), the whole name typed 20 more — then by catalog order.
/// A query that is itself an emoji (typed, or pasted from Windows' emoji panel) is <see cref="AsEmoji"/>'s business. Pure.
/// </summary>
public static class EmojiSearch
{
    /// <summary>The emoji of <paramref name="entries"/> every word of <paramref name="query"/> matches, best first; none for no words.</summary>
    public static IReadOnlyList<EmojiEntry> Rank(string query, IReadOnlyList<EmojiEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(entries);
        var words = Words(query);
        if (words.Count == 0)
        {
            return [];
        }

        var scored = new List<(EmojiEntry Entry, int Score)>();
        foreach (var entry in entries)
        {
            int total = 0;
            var names = Words(entry.Name);
            foreach (string word in words)
            {
                int best = Score(word, names, entry.Keywords);
                if (best == 0)
                {
                    total = 0;
                    break;
                }

                total += best;
            }

            if (total > 0)
            {
                // The whole name typed (later the same day: "cat" is the cat, before the cat face and every cat-keyworded emoji).
                if (string.Equals(string.Join(' ', names), string.Join(' ', words), StringComparison.Ordinal))
                {
                    total += 20;
                }

                scored.Add((entry, total));
            }
        }

        return [.. scored.OrderByDescending(s => s.Score).ThenBy(s => s.Entry.Order).Select(s => s.Entry)];
    }

    /// <summary>
    /// <paramref name="query"/> as one emoji when that is what it is: a single cluster (<see cref="TextCells.ClusterWidth"/>) two cells
    /// wide that is not a letter or a digit typed — what Win+. or a paste puts in the filter. Null otherwise. Pure.
    /// </summary>
    public static string? AsEmoji(string? query)
    {
        string text = (query ?? "").Trim();
        return AvatarRules.IsEmoji(text) ? text : null;
    }

    /// <summary>The words of <paramref name="text"/>, lowercase: letters, digits and <c>+</c> runs (CLDR has <c>+1</c>).</summary>
    public static List<string> Words(string text)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c) || c == '+')
            {
                current.Append(char.ToLowerInvariant(c));
            }
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            words.Add(current.ToString());
        }

        return words;
    }

    private static int Score(string word, List<string> names, IReadOnlyList<string> keywords)
    {
        int best = 0;
        bool typed = true;
        foreach (string form in Forms(word))
        {
            // The word as typed outranks it without its s (the first look: "thumbs" put the crossed fingers' "thumb" before
            // "thumbs up"), by one; every match counts twice its rank so the one fits between.
            int less = typed ? 0 : 1;
            foreach (string name in names)
            {
                best = Math.Max(best, name == form ? 8 - less : name.StartsWith(form, StringComparison.Ordinal) ? 6 - less : 0);
            }

            foreach (string keyword in keywords)
            {
                foreach (string part in Words(keyword))
                {
                    best = Math.Max(best, part == form ? 4 - less : form.Length >= 2 && part.StartsWith(form, StringComparison.Ordinal) ? 2 - less : 0);
                }
            }

            typed = false;
        }

        return best;
    }

    // The word, and without its plural s ("cats" finds the cat).
    private static IEnumerable<string> Forms(string word)
    {
        yield return word;
        if (word.Length > 3 && word[^1] == 's')
        {
            yield return word[..^1];
        }
    }
}
