using System.Globalization;

namespace NeonSidekick.Printing;

/// <summary>
/// A sheet of paper as CUPS gives it (2026-10-08, printing on a Mac): the media's name (<c>na_letter_8.5x11in</c>, null when the
/// paper was not the printer's), its size and the margins the printer cannot reach, all in points, portrait.
/// </summary>
public sealed record MacPaper(string? Media, double Width, double Height, double Left, double Top, double Right, double Bottom);

/// <summary>
/// The decisions behind <see cref="MacPrintSpooler"/> (2026-10-08, printing on a Mac), pure so they are covered on every OS:
/// the paper from CUPS' hundredths of a millimetre (or Letter/A4 by the Mac's region when the printer will not say), the page
/// turned for landscape, the faces a <see cref="PrintFont"/> is drawn in, where an op lands in PDF space (y up, from the
/// sheet's bottom-left corner), the job's options, and a printer's line from its CUPS options.
/// <para>
/// Landscape is drawn, not asked for: the PDF's pages are the portrait paper with the landscape page turned a quarter
/// counter-clockwise onto it (its top along the paper's left edge), which is IPP's <c>orientation-requested=4</c> done by the
/// app. So nothing depends on how a filter or a printer reads a wide page, and no orientation option is sent.
/// </para>
/// </summary>
public static class MacPrintRules
{
    /// <summary>The CUPS library, in the dyld shared cache on every Mac (not a file on disk: bind by name, never by <c>File.Exists</c>).</summary>
    public const string CupsLibrary = "/usr/lib/libcups.2.dylib";

    /// <summary>The proportional face: Helvetica Neue, on every Mac, Segoe UI's stand-in.</summary>
    public const string SansFamily = "HelveticaNeue";

    /// <summary>The fixed face: Menlo, on every Mac, Consolas' stand-in.</summary>
    public const string MonoFamily = "Menlo";

    /// <summary>The margin taken when the printer gives none: a quarter inch, the test paper's (<see cref="PageMetrics.Letter"/>).</summary>
    public const double FallbackMargin = 18;

    /// <summary>The regions on US Letter; every other one is on A4, as CUPS itself and the print dialog choose.</summary>
    public static readonly IReadOnlySet<string> LetterRegions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "US", "CA", "MX", "PR", "PH", "CL", "CO", "VE", "CR", "GT", "PA", "DO", "SV", "NI", "BZ",
    };

    /// <summary>Hundredths of a millimetre (CUPS' <c>cups_size_t</c>) in points.</summary>
    public static double Points(int hundredthsOfMm) => hundredthsOfMm * 72.0 / 2540.0;

    /// <summary>The paper from CUPS' <c>cups_size_t</c>: its name, width, length and the four margins, in hundredths of a millimetre.</summary>
    public static MacPaper FromCups(string media, int width, int length, int bottom, int left, int right, int top) =>
        new(string.IsNullOrWhiteSpace(media) ? null : media, Points(width), Points(length), Points(left), Points(top), Points(right), Points(bottom));

    /// <summary>The paper when the printer will not say (2026-10-08): Letter in <see cref="LetterRegions"/>, else A4, a quarter-inch margin, no media name sent.</summary>
    public static MacPaper ForRegion(string? region) => region is not null && LetterRegions.Contains(region.Trim())
        ? new MacPaper(null, 612, 792, FallbackMargin, FallbackMargin, FallbackMargin, FallbackMargin)
        : new MacPaper(null, 595.2756, 841.8898, FallbackMargin, FallbackMargin, FallbackMargin, FallbackMargin);

    /// <summary>
    /// The page the layout sees: the paper as it is, or turned for landscape. Turned a quarter counter-clockwise, the landscape
    /// page's left edge is the paper's bottom, its top the paper's left, its right the paper's top and its bottom the paper's right.
    /// </summary>
    public static PageMetrics Metrics(MacPaper paper, bool landscape)
    {
        ArgumentNullException.ThrowIfNull(paper);
        return landscape
            ? new PageMetrics(paper.Height, paper.Width, paper.Bottom, paper.Left, paper.Height - paper.Top, paper.Width - paper.Right)
            : new PageMetrics(paper.Width, paper.Height, paper.Left, paper.Top, paper.Width - paper.Right, paper.Height - paper.Bottom);
    }

    /// <summary>The PostScript name a font is drawn in: Helvetica Neue for prose, Menlo for code, each in its four styles.</summary>
    public static string FontName(PrintFont font)
    {
        ArgumentNullException.ThrowIfNull(font);
        string style = (font.Bold, font.Italic) switch
        {
            (true, true) => "BoldItalic",
            (true, false) => "Bold",
            (false, true) => "Italic",
            _ => "",
        };
        return font.Face == PrintFace.Mono
            ? MonoFamily + "-" + (style.Length == 0 ? "Regular" : style)
            : style.Length == 0 ? SansFamily : SansFamily + "-" + style;
    }

    /// <summary>A text op's baseline origin in the page's PDF space (y up from the page's bottom-left corner).</summary>
    public static (double X, double Y) Baseline(double x, double y, PageMetrics page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return (x, page.Height - y);
    }

    /// <summary>A box's or a picture's rectangle in the page's PDF space: its bottom-left corner, then its size.</summary>
    public static (double X, double Y, double Width, double Height) Rect(double x, double y, double width, double height, PageMetrics page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return (x, page.Height - y - height, width, height);
    }

    /// <summary>
    /// What the PDF context's matrix is turned by for a landscape page: translate by the paper's width, then rotate a quarter
    /// counter-clockwise (CoreGraphics applies the rotation first). Nothing for portrait.
    /// </summary>
    public static (double TranslateX, double Angle) Turn(MacPaper paper, bool landscape)
    {
        ArgumentNullException.ThrowIfNull(paper);
        return landscape ? (paper.Width, Math.PI / 2) : (0, 0);
    }

    /// <summary>Where a point of the page's PDF space lands on the paper's (both y up): <see cref="Turn"/> applied, for the tests.</summary>
    public static (double X, double Y) OnPaper(double x, double y, MacPaper paper, bool landscape)
    {
        var (translate, angle) = Turn(paper, landscape);
        double cos = Math.Cos(angle);
        double sin = Math.Sin(angle);
        return (x * cos - y * sin + translate, x * sin + y * cos);
    }

    /// <summary>The job's options: the paper's media by name when the printer gave it, so CUPS never picks another sheet.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> JobOptions(MacPaper paper)
    {
        ArgumentNullException.ThrowIfNull(paper);
        return paper.Media is { } media ? [new("media", media)] : [];
    }

    /// <summary>A PDF's options sent as it is: the copies (CUPS collates them), and the pages unless all are chosen.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> FileOptions(int copies, IReadOnlyList<int>? pages)
    {
        var options = new List<KeyValuePair<string, string>>(2);
        if (copies > 1)
        {
            options.Add(new("copies", copies.ToString(CultureInfo.InvariantCulture)));
            options.Add(new("multiple-document-handling", "separate-documents-collated-copies"));
        }

        if (pages is { Count: > 0 })
        {
            options.Add(new("page-ranges", PrintText.Ranges(pages).Replace(" ", "", StringComparison.Ordinal)));
        }

        return options;
    }

    /// <summary>Whether a file goes to CUPS as it is: a PDF, which every Mac's CUPS prints itself (<c>cgpdftopdf</c>).</summary>
    public static bool IsPdf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A printer from a CUPS destination: its queue name, whether it is the default, and its <c>printer-info</c> when that says more.</summary>
    public static PrinterInfo Printer(string name, bool isDefault, IReadOnlyDictionary<string, string> options)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(options);
        string? info = options.TryGetValue("printer-info", out string? value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
        return new PrinterInfo(name, isDefault, info is null || string.Equals(info, name, StringComparison.Ordinal) ? null : info);
    }

    /// <summary>The CUPS error string as a sentence's tail: the library's words, or a plain fallback when it has none.</summary>
    public static string Detail(string? cupsError) =>
        string.IsNullOrWhiteSpace(cupsError) ? "CUPS gave no reason" : cupsError.Trim();
}
