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
        foreach (string raw in spec.Split(',', StringSplitOptions.TrimEntries))
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
    /// does not know the count yet passes the most it will read: the reader leaves out what lies past the document's end.
    /// </summary>
    public IReadOnlyList<int> Resolve(int pageCount)
    {
        var pages = new SortedSet<int>();
        foreach (var (from, to) in _parts)
        {
            int last = Math.Min(to ?? pageCount, pageCount);
            for (int page = from; page <= last; page++)
            {
                pages.Add(page);
            }
        }

        return [.. pages];
    }

    private static bool TryPage(string text, out int page) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out page) && page >= 1;
}
