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

        string folded = Fold(pageText);
        return words.All(w => folded.Contains(w, StringComparison.Ordinal));
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
                int start = Math.Clamp(center - ContextChars / 3, 0, line.Length - ContextChars);
                return (start > 0 ? "…" : "") + line.Substring(start, ContextChars).Trim() + (start + ContextChars < line.Length ? "…" : "");
            }
        }

        return "";
    }
}
