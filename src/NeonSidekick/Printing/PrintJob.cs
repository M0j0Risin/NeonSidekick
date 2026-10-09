using NeonSidekick.Viewer;

namespace NeonSidekick.Printing;

/// <summary>
/// A printer the spooler knows: its name as the system spells it, and whether it is the system's default. On a Mac
/// (2026-10-08) <paramref name="Description"/> is CUPS' <c>printer-info</c>, the name System Settings shows ("Brother HL-L2340D
/// series" for the queue <c>Brother_HL_L2340D_series</c>), listed beside the name and matched by <c>printer=</c> too; Windows
/// never sets it.
/// </summary>
public sealed record PrinterInfo(string Name, bool IsDefault, string? Description = null);

/// <summary>The two faces a printed page uses: a proportional one for prose, a fixed one for code and listings.</summary>
public enum PrintFace
{
    Sans,
    Mono,
}

/// <summary>One font on paper: the face, the size in points, bold and italic.</summary>
public sealed record PrintFont(PrintFace Face, double Size, bool Bold = false, bool Italic = false);

/// <summary>
/// The paper a job lands on, in points (1/72 inch) from the sheet's top-left corner: the whole sheet, and the part the printer
/// can reach (<see cref="PrintableLeft"/> … <see cref="PrintableBottom"/>). Landscape is already applied.
/// </summary>
public sealed record PageMetrics(double Width, double Height, double PrintableLeft, double PrintableTop, double PrintableRight, double PrintableBottom)
{
    /// <summary>US Letter with a quarter inch the printer cannot reach on every side: the tests' paper.</summary>
    public static PageMetrics Letter(bool landscape = false) => landscape
        ? new PageMetrics(792, 612, 18, 18, 774, 594)
        : new PageMetrics(612, 792, 18, 18, 594, 774);
}

/// <summary>How wide a run of text is on paper, in points: the printer's own fonts on the screen, a fixed advance in tests.</summary>
public interface ITextMeasure
{
    double Width(string text, PrintFont font);
}

/// <summary>One thing drawn on a page, in points from the sheet's top-left corner.</summary>
public abstract record PrintOp;

/// <summary>Text starting at <paramref name="X"/> on the baseline <paramref name="Y"/>, so runs of different sizes on one line sit on one baseline.</summary>
public sealed record PrintTextOp(double X, double Y, string Text, PrintFont Font) : PrintOp;

/// <summary>A filled black box: a rule, a quote's bar, a table's line.</summary>
public sealed record PrintBoxOp(double X, double Y, double Width, double Height) : PrintOp;

/// <summary>A picture stretched into the box.</summary>
public sealed record PrintImageOp(double X, double Y, double Width, double Height, ViewerBitmap Bitmap) : PrintOp;

/// <summary>One sheet: its number in the whole document (1-based, kept when a page range drops others) and what is on it.</summary>
public sealed record PrintPage(int Number, IReadOnlyList<PrintOp> Ops);

/// <summary>
/// What the spooler prints (2026-09-28): device-independent pages already laid out, the printer's name, the job's title in
/// the queue, the orientation the pages were laid out for, and the file a print-to-file printer writes instead of asking
/// where — the smoke probe's and the live test's, and since 2026-10-03 the PDF fallback's (<c>convert_to_pdf</c> on Microsoft
/// Print to PDF when no browser can make the PDF). Copies are pages repeated, collated, so every driver prints them.
/// </summary>
public sealed record PrintJob(string Printer, string Title, IReadOnlyList<PrintPage> Pages, bool Landscape = false, int Copies = 1, string? OutputFile = null);

/// <summary>A measuring surface for one printer and orientation: the paper, and the printer's fonts to measure with.</summary>
public interface IPrintSurface : ITextMeasure, IDisposable
{
    PageMetrics Page { get; }
}

/// <summary>
/// The seam between printing and Windows (2026-09-28): the printers, a surface to lay out against, the job sent, and the
/// shell's own <c>print</c> command for a file the app cannot draw. <see cref="WindowsPrintSpooler"/> is the real one;
/// the tests' fake records what it was given. Every failure is a sentence, never a throw.
/// </summary>
public interface IPrintSpooler
{
    /// <summary>The installed printers, the Windows default marked; empty when there are none or they cannot be listed.</summary>
    IReadOnlyList<PrinterInfo> Printers();

    /// <summary>A surface for <paramref name="printer"/>, or null with <paramref name="error"/> when it cannot be opened.</summary>
    IPrintSurface? Open(string printer, bool landscape, out string error);

    /// <summary>Sends <paramref name="job"/>: null when the spooler took it, else why not. Cancelling aborts the document.</summary>
    string? Print(PrintJob job, CancellationToken cancellationToken);

    /// <summary>Whether some program on this PC registers a <c>print</c> command for <paramref name="path"/>'s type.</summary>
    bool CanShellPrint(string path);

    /// <summary>Hands <paramref name="path"/> to that program's <c>print</c> command: null when it started, else why not.</summary>
    string? ShellPrint(string path);

    /// <summary>
    /// Whether a file the app does not draw goes to the printer as it is, so it takes a printer, copies and pages (2026-10-08,
    /// a Mac's PDF straight to CUPS). False by default: Windows' <c>print</c> verb prints on the default printer as it is set up.
    /// </summary>
    bool ShellPrintTakesOptions => false;

    /// <summary>The pages of a file <see cref="ShellPrint(string)"/> takes when <see cref="ShellPrintTakesOptions"/>: 0 when it cannot be read.</summary>
    int ShellPageCount(string path) => 0;

    /// <summary>
    /// <paramref name="path"/> sent as it is to <paramref name="printer"/>: <paramref name="copies"/>, and <paramref name="pages"/>
    /// (null: all). Null when the printer took it, else why not; cancelling withdraws a job half sent. By default the plain
    /// <see cref="ShellPrint(string)"/>, the options ignored, for a spooler that never says it takes them.
    /// </summary>
    string? ShellPrint(string path, string printer, int copies, IReadOnlyList<int>? pages, CancellationToken cancellationToken) => ShellPrint(path);
}
