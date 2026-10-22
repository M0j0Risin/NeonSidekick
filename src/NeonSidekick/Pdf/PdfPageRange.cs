using System.Globalization;

namespace NeonSidekick.Pdf;

/// <summary>
/// A <c>pages</c> argument read (2026-10-14, the PDF plan's foundations): <c>"3"</c>, <c>"2-5"</c>, <c>"5-"</c> (to the end), or a list of
/// those joined by commas (<c>"1,3,7-9"</c>), spaces allowed, physical page numbers from 1. <see cref="TryParse"/> keeps the open end
/// open (a document's count may not be known yet); <see cref="Resolve"/> closes it against a count and drops what lies past it. Pure.
/// </summary>
public sealed class PdfPageRange
{
    /// <summary>The most pages one range may name, so <c>"1-999999999"</c> never becomes a list that size.</summary>
    public const int MaxPages = 100_000;

    /// <summary>
    /// The most comma-separated parts one <c>pages</c> argument may hold (2026-10-22, review K10/S1): <c>"1-2000,"</c> a hundred thousand
    /// times named 200 million pages for <see cref="Resolve"/> to sort on the turn's thread. A person's list never comes near it.
    /// </summary>
    public const int MaxParts = 100;

    private readonly IReadOnlyList<(int From, int? To)> _parts;

    private PdfPageRange(IReadOnlyList<(int From, int? To)> parts) => _parts = parts;

    /// <summary>The first page the range names.</summary>
    public int First => _parts.Min(p => p.From);

    /// <summary>Whether it runs to the end of the document (a part like <c>"5-"</c>).</summary>
    public bool OpenEnded => _parts.Any(p => p.To is null);

    /// <summary>Reads <paramref name="spec"/>; false with <paramref name="error"/> (a sentence naming the bad part) for anything else.</summary>
    public static bool TryParse(string spec, out PdfPageRange? range, out string error)
    {
        ArgumentNullException.ThrowIfNull(spec);
        range = null;
        var parts = new List<(int, int?)>();
        string[] raws = spec.Split(',', StringSplitOptions.TrimEntries);
        if (raws.Length > MaxParts)
        {
            error = PdfText.TooManyPageParts(MaxParts);
            return false;
        }

        foreach (string raw in raws)
        {
            if (raw.Length == 0)
            {
                error = PdfText.BadPages(spec);
                return false;
            }

            int dash = raw.IndexOf('-');
            if (dash < 0)
            {
                if (!TryPage(raw, out int page))
                {
                    error = PdfText.BadPages(spec);
                    return false;
                }

                parts.Add((page, page));
                continue;
            }

            string left = raw[..dash].Trim();
            string right = raw[(dash + 1)..].Trim();
            if (!TryPage(left, out int from) || (right.Length > 0 && !TryPage(right, out _)))
            {
                error = PdfText.BadPages(spec);
                return false;
            }

            int? to = right.Length == 0 ? null : int.Parse(right, NumberStyles.None, CultureInfo.InvariantCulture);
            if (to < from || (to ?? from) - from >= MaxPages)
            {
                error = PdfText.BadPages(spec);
                return false;
            }

            parts.Add((from, to));
        }

        range = new PdfPageRange(parts);
        error = "";
        return true;
    }

    /// <summary>
    /// The pages named, sorted and once each, an open end closed at <paramref name="pageCount"/>, and nothing past it. A caller that
    /// does not know the count yet passes the most it will read: the reader leaves out what lies past the document's end. The walk counts
    /// in <see cref="long"/> (2026-10-22, review S1): a last page of <see cref="int.MaxValue"/> made an <c>int</c> counter wrap and never end,
    /// filling the set until the process ran out of memory.
    /// </summary>
    public IReadOnlyList<int> Resolve(int pageCount)
    {
        var pages = new SortedSet<int>();
        foreach (var (from, to) in _parts)
        {
            long last = Math.Min(to ?? pageCount, pageCount);
            for (long page = from; page <= last; page++)
            {
                pages.Add((int)page);
            }
        }

        return [.. pages];
    }

    /// <summary>
    /// The pages in the order written, each once (2026-10-22, review R6: <c>pdf_edit</c>'s extract of <c>3,1,2</c> came out 1, 2, 3, its
    /// order lost and the result said done), held to <paramref name="pageCount"/>.
    /// </summary>
    public IReadOnlyList<int> InOrder(int pageCount)
    {
        var seen = new HashSet<int>();
        var pages = new List<int>();
        foreach (var (from, to) in _parts)
        {
            long last = Math.Min(to ?? pageCount, pageCount);
            for (long page = from; page <= last; page++)
            {
                if (seen.Add((int)page))
                {
                    pages.Add((int)page);
                }
            }
        }

        return pages;
    }

    /// <summary>
    /// The page a read that starts at <paramref name="first"/> and takes at most <paramref name="pages"/> pages ends on, held at
    /// <see cref="int.MaxValue"/> (2026-10-22, review S1: <c>First + MaxPagesPerRead - 1</c> overflowed for a first page near the top).
    /// </summary>
    public static int LastOf(int first, int pages) => (int)Math.Min((long)first + pages - 1, int.MaxValue);

    private static bool TryPage(string text, out int page) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out page) && page >= 1;
}
