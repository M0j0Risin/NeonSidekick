using System.Globalization;
using System.Text;

namespace NeonSidekick.Pdf;

/// <summary>
/// <c>read_pdf</c>'s <c>find</c> (2026-10-14, the PDF plan's gap 93, the user's pick for long PDFs): a page matches when it holds
/// <b>every</b> word asked, case and accents folded away (<c>Résumé</c> finds <c>resume</c>), and its context is its first line that
/// holds the first word, cut to <see cref="ContextChars"/> around it. Plain substring matching, as a reader scans: <c>warrant</c> finds
/// <c>warranty</c>. Pure.
/// </summary>
public static class PdfFind
{
    /// <summary>The most pages one answer lists; the rest are counted.</summary>
    public const int MaxResults = 20;

    /// <summary>The longest context line.</summary>
    public const int ContextChars = 160;

    /// <summary>The words of <paramref name="query"/>, folded (<see cref="Fold"/>), once each, in order; quotes ignored.</summary>
    public static IReadOnlyList<string> Words(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return [.. Fold(query.Replace('"', ' ')).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal)];
    }

    /// <summary><paramref name="text"/> lowercased with its accents taken off (decomposed, the combining marks dropped).</summary>
    public static string Fold(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sb = new StringBuilder(text.Length);
        foreach (char c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Whether <paramref name="pageText"/> holds every one of <paramref name="words"/> (folded already).</summary>
    public static bool Matches(string pageText, IReadOnlyList<string> words)
    {
        ArgumentNullException.ThrowIfNull(pageText);
        ArgumentNullException.ThrowIfNull(words);
        if (words.Count == 0)
        {
            return false;
        }

        // Also with a line-end hyphen joined away or kept (2026-10-22, review T5: "warran-\nty" was never found as "warranty").
        string folded = Fold(pageText);
        string joined = Joined(folded, keepHyphen: false);
        string hyphened = Joined(folded, keepHyphen: true);
        return words.All(w => folded.Contains(w, StringComparison.Ordinal) || joined.Contains(w, StringComparison.Ordinal) || hyphened.Contains(w, StringComparison.Ordinal));
    }

    /// <summary>The text with each hyphen ending a line joined to the next line, the hyphen dropped or kept.</summary>
    public static string Joined(string text, bool keepHyphen)
    {
        ArgumentNullException.ThrowIfNull(text);
        string join = keepHyphen ? "-" : "";
        return text.Replace("-\r\n", join, StringComparison.Ordinal).Replace("-\n", join, StringComparison.Ordinal).Replace("\u00AD\n", "", StringComparison.Ordinal);
    }

    /// <summary>The page's first line holding the first word (or any word), cut to <see cref="ContextChars"/> around it; empty when none does.</summary>
    public static string Context(string pageText, IReadOnlyList<string> words)
    {
        ArgumentNullException.ThrowIfNull(pageText);
        ArgumentNullException.ThrowIfNull(words);
        var lines = pageText.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (string word in words)
        {
            foreach (string line in lines)
            {
                string folded = Fold(line);
                int at = folded.IndexOf(word, StringComparison.Ordinal);
                if (at < 0)
                {
                    continue;
                }

                if (line.Length <= ContextChars)
                {
                    return line;
                }

                // The fold may shorten the line (a decomposed accent dropped): place the window by proportion.
                int center = folded.Length == 0 ? 0 : (int)((long)at * line.Length / folded.Length);
                // The window's edges never split a surrogate pair (2026-10-22, review G20).
                int start = Files.TextCut.SafeStart(line, Math.Clamp(center - ContextChars / 3, 0, line.Length - ContextChars));
                int length = Files.TextCut.SafeLength(line.AsSpan(start), Math.Min(ContextChars, line.Length - start));
                return (start > 0 ? "…" : "") + line.Substring(start, length).Trim() + (start + length < line.Length ? "…" : "");
            }
        }

        // A word found only across a line's hyphen: its two lines as one (review T5).
        var joinedLines = Joined(pageText, keepHyphen: false).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (string word in words)
        {
            if (joinedLines.FirstOrDefault(l => Fold(l).Contains(word, StringComparison.Ordinal)) is { } line)
            {
                return line.Length <= ContextChars ? line : Files.TextCut.Safe(line, ContextChars - 1) + "…";
            }
        }

        return "";
    }
}
