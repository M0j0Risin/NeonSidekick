using System.Text;
using System.Text.RegularExpressions;

namespace NeonSidekick.Pdf;

/// <summary>
/// A PDF's pages as prose to read aloud (2026-10-14, the PDF plan's gap 89). pdf.js gives a page's text a printed line at a time, and
/// <c>/read</c>'s chunker breaks at every newline (<c>SpeakReading.Split</c>), so a PDF read as it comes would pause at each line's end,
/// cut its sentences in two, and number its lines as sentences. Each page's lines are joined with spaces; a line ending in a hyphen
/// before a lowercase letter joins without it (<c>exam-</c> + <c>ple</c>); pages stay apart, a blank line between; and a running header
/// or footer — the same first or last line on most pages, its numbers aside (<c>Page 3 of 12</c>) — is dropped, so it is not read out
/// once a page. For <c>/read</c> alone: <c>read_pdf</c>, <c>web_fetch</c> and the attachments keep the lines, which models read well and
/// tables need. Pure.
/// </summary>
public static partial class PdfReflow
{
    /// <summary>The fewest pages on which a header or footer must repeat before it is taken for one.</summary>
    public const int MinRepeats = 3;

    /// <summary><paramref name="pages"/> (in order) as paragraphs, one per page with text.</summary>
    public static string Reflow(IReadOnlyList<string> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        var lines = pages.Select(p => p.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).ToList();
        var headers = Running(lines.Where(l => l.Length > 1).Select(l => l[0]).ToList(), lines.Count);
        var footers = Running(lines.Where(l => l.Length > 1).Select(l => l[^1]).ToList(), lines.Count);
        var sb = new StringBuilder();
        foreach (var page in lines)
        {
            int start = page.Length > 1 && headers.Contains(Shape(page[0])) ? 1 : 0;
            int end = page.Length > 1 && footers.Contains(Shape(page[^1])) ? page.Length - 1 : page.Length;
            string paragraph = Join(page[start..Math.Max(start, end)]);
            if (paragraph.Length > 0)
            {
                sb.Append(sb.Length > 0 ? "\n\n" : "").Append(paragraph);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// One page's lines as one paragraph: spaces between, a hyphen at a line's end before a lowercase letter joined — dropped for a word
    /// broken by hyphenation, kept for a compound (2026-10-22, review T6: "self-" + "evident" was read "selfevident"). The page decides:
    /// the word written whole elsewhere drops it, written hyphenated keeps it; otherwise a compound's first part keeps it
    /// (<see cref="CompoundHeads"/>), and any other drops it, as typeset text breaks words far more often than at a compound's hyphen.
    /// </summary>
    public static string Join(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var words = new HashSet<string>(string.Join(' ', lines).Split([' ', '\t', ',', '.', ';', ':', '!', '?', '(', ')', '"'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.ToLowerInvariant()), StringComparer.Ordinal);
        var sb = new StringBuilder();
        foreach (string line in lines)
        {
            if (sb.Length > 1 && sb[^1] == '-' && char.IsLetter(sb[^2]) && line.Length > 0 && char.IsLower(line[0]))
            {
                int start = sb.Length - 1;
                while (start > 0 && char.IsLetter(sb[start - 1]))
                {
                    start--;
                }

                string head = sb.ToString(start, sb.Length - 1 - start).ToLowerInvariant();
                int end = 0;
                while (end < line.Length && char.IsLetter(line[end]))
                {
                    end++;
                }

                string tail = line[..end].ToLowerInvariant();
                bool keep = !words.Contains(head + tail) && (words.Contains(head + "-" + tail) || CompoundHeads.Contains(head));
                if (!keep)
                {
                    sb.Length--;
                }

                sb.Append(line);
            }
            else
            {
                sb.Append(sb.Length > 0 ? " " : "").Append(line);
            }
        }

        return sb.ToString();
    }

    /// <summary>The first parts of English compounds whose hyphen a line's end keeps (review T6).</summary>
    public static readonly IReadOnlySet<string> CompoundHeads = new HashSet<string>(StringComparer.Ordinal)
    {
        "self", "well", "non", "co", "ex", "anti", "multi", "semi", "pre", "post", "cross", "half", "all", "long", "short", "high", "low",
        "full", "part", "sub", "super", "inter", "intra", "mid", "over", "under", "quasi", "pseudo", "vice", "e",
    };

    /// <summary>The shapes (<see cref="Shape"/>) that repeat on at least half the pages, and on <see cref="MinRepeats"/> at least.</summary>
    private static HashSet<string> Running(IReadOnlyList<string> candidates, int pageCount)
    {
        int needed = Math.Max(MinRepeats, (pageCount + 1) / 2);
        return [.. candidates.Select(Shape).GroupBy(s => s, StringComparer.Ordinal).Where(g => g.Count() >= needed).Select(g => g.Key)];
    }

    /// <summary>A line with its numbers made alike, so <c>Page 3 of 12</c> and <c>Page 4 of 12</c> are one footer.</summary>
    private static string Shape(string line) => Digits().Replace(line, "#");

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();
}
