using System.Globalization;
using NeonSidekick.Web;

namespace NeonSidekick.Pdf;

/// <summary>
/// One edit asked for (2026-10-14, the PDF plan's phase 4b): the operation, the PDFs it takes (relative to the working directory; a merge
/// two or more, the rest one), the pages (<see cref="Pages"/> as written: <c>"2-5"</c>), the angle for a turn, the fields for a form, and
/// where the new PDF goes (<see cref="To"/>: a file, a folder, or blank for beside the first PDF under a name of its own).
/// </summary>
public sealed record PdfEditRequest(
    PdfEditOp Op,
    IReadOnlyList<string> Paths,
    string? Pages = null,
    int Angle = 0,
    IReadOnlyList<PdfFieldValue>? Fields = null,
    bool Flatten = false,
    string? To = null,
    bool Overwrite = false);

/// <summary>
/// What each edit needs and makes (2026-10-14, the PDF plan's phase 4b), checked before any file is read: the action words <c>pdf_edit</c>
/// and <c>/pdf</c> share, each action's arguments (<see cref="Check"/>), the pages it acts on once the page count is known
/// (<see cref="PagesFor"/>: a delete never takes every page), and the new file's name (<see cref="Suffix"/> after the first PDF's own:
/// <c>report-p2-5.pdf</c>, <c>a-merged.pdf</c>). Pure.
/// </summary>
public static class PdfEditPlan
{
    public const string MergeWord = "merge";
    public const string ExtractWord = "extract";
    public const string DeleteWord = "delete";
    public const string RotateWord = "rotate";
    public const string FillFormWord = "fill_form";

    /// <summary>The action words in the order the tool's schema and the help list them.</summary>
    public static readonly IReadOnlyList<string> ActionWords = [MergeWord, ExtractWord, DeleteWord, RotateWord, FillFormWord];

    /// <summary>The most PDFs one merge takes.</summary>
    public const int MaxMergeFiles = 20;

    /// <summary>The angles a turn takes, clockwise; <c>-90</c> reads as 270.</summary>
    public static readonly IReadOnlyList<int> Angles = [90, 180, 270];

    public static bool TryAction(string word, out PdfEditOp op)
    {
        ArgumentNullException.ThrowIfNull(word);
        switch (word.Trim().ToLowerInvariant().Replace('-', '_'))
        {
            case MergeWord: op = PdfEditOp.Merge; return true;
            case ExtractWord: op = PdfEditOp.Extract; return true;
            case DeleteWord: op = PdfEditOp.Delete; return true;
            case RotateWord: op = PdfEditOp.Rotate; return true;
            case FillFormWord or "fill" or "fillform": op = PdfEditOp.FillForm; return true;
            default: op = default; return false;
        }
    }

    public static string WordOf(PdfEditOp op) => op switch
    {
        PdfEditOp.Merge => MergeWord,
        PdfEditOp.Extract => ExtractWord,
        PdfEditOp.Delete => DeleteWord,
        PdfEditOp.Rotate => RotateWord,
        _ => FillFormWord,
    };

    /// <summary>An angle given as a turn: 90, 180 or 270 (a negative one counter-clockwise, read as its clockwise twin); null for any other.</summary>
    public static int? AngleOf(int given)
    {
        int clockwise = ((given % 360) + 360) % 360;
        return Angles.Contains(clockwise) ? clockwise : null;
    }

    /// <summary>
    /// What the request lacks or has too much of for its action, as the sentence a caller answers with; null when it is whole. The pages,
    /// when given, are parsed here too, so a bad range is refused before any file is read.
    /// </summary>
    public static string? Check(PdfEditRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string action = WordOf(request.Op);
        int paths = request.Paths.Count(p => !string.IsNullOrWhiteSpace(p));
        if (request.Op == PdfEditOp.Merge)
        {
            if (paths < 2)
            {
                return PdfText.MergeNeedsTwo;
            }

            if (paths > MaxMergeFiles)
            {
                return PdfText.MergeTooMany(MaxMergeFiles);
            }
        }
        else if (paths != 1)
        {
            return PdfText.OnePdf(action);
        }

        bool pages = !string.IsNullOrWhiteSpace(request.Pages);
        if (request.Op is PdfEditOp.Extract or PdfEditOp.Delete && !pages)
        {
            return PdfText.PagesNeeded(action);
        }

        if (request.Op is PdfEditOp.Merge or PdfEditOp.FillForm && pages)
        {
            return PdfText.NotFor("pages", action);
        }

        if (pages && !PdfPageRange.TryParse(request.Pages!.Trim().Trim('"'), out _, out string bad))
        {
            return bad;
        }

        if (request.Op == PdfEditOp.Rotate)
        {
            if (AngleOf(request.Angle) is null)
            {
                return PdfText.BadAngle(request.Angle);
            }
        }
        else if (request.Angle != 0)
        {
            return PdfText.NotFor("angle", action);
        }

        bool fields = request.Fields is { Count: > 0 };
        if (request.Op == PdfEditOp.FillForm && !fields)
        {
            return PdfText.FieldsNeeded;
        }

        if (request.Op != PdfEditOp.FillForm && (fields || request.Flatten))
        {
            return PdfText.NotFor(fields ? "fields" : "flatten", action);
        }

        return null;
    }

    /// <summary>
    /// The pages the edit acts on in a PDF of <paramref name="pageCount"/> (physical, sorted): none for a merge or a form, every page for a
    /// turn without pages; the refusal when the range names none of them, or when a delete would leave none.
    /// </summary>
    public static (IReadOnlyList<int>? Pages, string? Error) PagesFor(PdfEditRequest request, string display, int pageCount)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Op is PdfEditOp.Merge or PdfEditOp.FillForm)
        {
            return ([], null);
        }

        string spec = (request.Pages ?? "").Trim().Trim('"');
        if (spec.Length == 0)
        {
            return ([.. Enumerable.Range(1, pageCount)], null);   // a turn of every page
        }

        if (!PdfPageRange.TryParse(spec, out var range, out string bad))
        {
            return (null, bad);
        }

        var pages = range!.Resolve(pageCount);
        if (pages.Count == 0)
        {
            return (null, PdfText.PastTheEnd(display, pageCount, spec));
        }

        if (request.Op == PdfEditOp.Delete && pages.Count >= pageCount)
        {
            return (null, PdfText.CannotDeleteAll(display, pageCount));
        }

        return (pages, null);
    }

    /// <summary>
    /// What goes after the first PDF's name in the new file's: <c>-merged</c>, <c>-p2-5</c> (the pages, a list's commas as underscores;
    /// <c>-pages</c> when that grows long), <c>-edited</c> for a delete, <c>-rotated</c>, <c>-filled</c>.
    /// </summary>
    public static string Suffix(PdfEditOp op, IReadOnlyList<int> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        switch (op)
        {
            case PdfEditOp.Merge:
                return "-merged";
            case PdfEditOp.Extract:
                string spec = PdfText.Spec(pages).Replace(',', '_');
                return spec.Length is > 0 and <= 24 ? "-p" + spec : "-pages";
            case PdfEditOp.Delete:
                return "-edited";
            case PdfEditOp.Rotate:
                return "-rotated";
            default:
                return "-filled";
        }
    }

    /// <summary>The sentence a made PDF's result ends on: what was done, to which PDF.</summary>
    public static string WhatWasDone(PdfEditRequest request, IReadOnlyList<string> displays, IReadOnlyList<int> pages)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(displays);
        string first = displays.Count > 0 ? displays[0] : "";
        return request.Op switch
        {
            PdfEditOp.Merge => string.Join(", ", displays.SkipLast(1)) + " and " + displays[^1] + " merged",
            PdfEditOp.Extract => (pages.Count == 1 ? "page " : "pages ") + PdfText.Runs(pages) + " of " + first,
            PdfEditOp.Delete => first + " without " + (pages.Count == 1 ? "page " : "pages ") + PdfText.Runs(pages),
            PdfEditOp.Rotate => first + " with " + (pages.Count == 1 ? "page " : "pages ") + PdfText.Runs(pages) + " turned " + AngleOf(request.Angle)!.Value.ToString(CultureInfo.InvariantCulture) + "°",
            _ => first + " with " + Fields(request.Fields?.Count ?? 0) + " filled in" + (request.Flatten ? ", flattened" : ""),
        };
    }

    private static string Fields(int count) => count.ToString(CultureInfo.InvariantCulture) + (count == 1 ? " field" : " fields");
}
