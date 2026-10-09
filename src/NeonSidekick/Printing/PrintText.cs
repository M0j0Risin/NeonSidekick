using System.Globalization;

namespace NeonSidekick.Printing;

/// <summary>
/// The printing words (2026-09-28): the tools' results and failures, the confirm question, the page header's number, the
/// printer list and the <c>/print</c> lines. Every failure starts <c>Error:</c>; a result's first line is its header, which is
/// the transcript's note. Pure; the policy refusals are pinned by tests.
/// </summary>
public static class PrintText
{
    /// <summary>The log category.</summary>
    public const string Category = "Print";

    /// <summary>The most copies one print takes.</summary>
    public const int MaxCopies = 10;

    /// <summary>A text file over this many bytes is refused: two megabytes is some four hundred pages of listing.</summary>
    public const long MaxTextBytes = 2_000_000;

    /// <summary>The print-to-file printer every Windows 10 and 11 installs: the smoke's probe and the PDF fallback print to it.</summary>
    public const string PrintToPdfPrinter = "Microsoft Print to PDF";

    /// <summary>The header's title for <c>/print reply</c>.</summary>
    public const string ReplyTitle = "Reply";

    // ─── the policy ─────────────────────────────────────────────────────────────

    /// <summary>The policy is <c>off</c>. Pinned.</summary>
    public const string PolicyOff = "Error: Print action policy is off, so the model may not print; the user can print with /print";

    /// <summary>The user said no on the pane. Pinned.</summary>
    public const string Declined = "Error: the user declined this print; do not retry it unless they ask";

    /// <summary>The print needed the user's yes and nothing could ask (headless, no pane). Pinned.</summary>
    public const string NotAsked = "Error: printing needs the user's yes and nobody could be asked; the user can print with /print, or allow it with Print action policy";

    // ─── failures ───────────────────────────────────────────────────────────────

    public const string NoPrinter = "Error: no printer is installed on this PC";

    /// <summary>A Mac with no printer set up (2026-10-08): where to add one. Pinned.</summary>
    public const string MacNoPrinter = "Error: no printer is set up on this Mac; add one in System Settings › Printers & Scanners";

    /// <summary>No printer: <see cref="NoPrinter"/>, or on a Mac <see cref="MacNoPrinter"/>.</summary>
    public static string NoPrinterHere => OperatingSystem.IsMacOS() ? MacNoPrinter : NoPrinter;

    /// <summary>
    /// Printing on neither Windows nor a Mac (2026-10-06 as NeedsWindows, the macOS build before its spooler; since 2026-10-08,
    /// when a Mac prints through CUPS, only other systems say it: the print group is not offered and <c>/print</c> says this). Pinned.
    /// </summary>
    public const string NotHere = "Printing needs Windows or a Mac: this build has no printer support.";

    /// <summary>Whether this system prints: Windows (winspool) or a Mac (CUPS, 2026-10-08).</summary>
    public static bool Supported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    public const string NoReply = "Error: there is no reply to print yet";

    public static string NoDefault(IReadOnlyList<PrinterInfo> printers) =>
        "Error: Windows has no default printer; name one: " + Names(printers);

    /// <summary><see cref="NoDefault"/> on a Mac (2026-10-08). Pinned.</summary>
    public static string MacNoDefault(IReadOnlyList<PrinterInfo> printers) =>
        "Error: this Mac has no default printer; name one: " + Names(printers);

    /// <summary>No default: <see cref="NoDefault"/>, or on a Mac <see cref="MacNoDefault"/>.</summary>
    public static string NoDefaultHere(IReadOnlyList<PrinterInfo> printers) => OperatingSystem.IsMacOS() ? MacNoDefault(printers) : NoDefault(printers);

    public static string UnknownPrinter(string name, IReadOnlyList<PrinterInfo> printers) =>
        $"Error: no printer named '{name.Trim()}'; the printers are: " + Names(printers);

    public static string AmbiguousPrinter(string name, IReadOnlyList<PrinterInfo> matches) =>
        $"Error: '{name.Trim()}' could be " + Names(matches) + "; name one";

    public static string CannotOpen(string printer, string detail) => $"Error: cannot open the printer '{printer}': {detail.Trim()}";

    public static string Failed(string printer, string detail) => $"Error: printing to '{printer}' failed: {detail.Trim()}";

    public static string PrinterCancelled(string printer) => $"Error: '{printer}' asked where to save the file, and the dialog was cancelled; nothing was printed";

    public static string ShellFailed(string file, string detail) => $"Error: Windows could not start printing {file}: {detail.Trim()}";

    public static string NoHandler(string file) =>
        $"Error: no program on this PC prints {Extension(file)} files, so {file} cannot be printed; the sidekick itself prints text, code, markdown and pictures";

    public static string ShellDefaultOnly(string file) =>
        $"Error: {file} is printed by the program Windows has for {Extension(file)} files, on the Windows default printer as it is set up; leave out printer, copies, pages and landscape";

    public static string NoFileOutput(string file) =>
        $"Error: {file} is printed by the program Windows has for {Extension(file)} files, which cannot print to a file";

    // ─── a file sent as it is (2026-10-08, a Mac's PDF straight to CUPS: IPrintSpooler.ShellPrintTakesOptions) ──

    /// <summary>A file the app does not draw and CUPS does not take as it is (an Office file). Pinned.</summary>
    public static string MacNoHandler(string file) =>
        $"Error: {file} cannot be printed from here on a Mac: only PDF files go to the printer as they are, and the sidekick itself prints text, code, markdown and pictures; print {Extension(file)} files from the program that made them";

    /// <summary>A PDF asked for landscape: its pages keep their own orientation. Pinned.</summary>
    public static string PdfLandscape(string file) =>
        $"Error: {file} goes to the printer as it is, each page as the PDF lays it out; leave out landscape";

    /// <summary>A PDF asked to print to a file. Pinned.</summary>
    public static string PdfNoFileOutput(string file) =>
        $"Error: {file} goes to the printer as it is and cannot print to a file";

    /// <summary>A PDF CoreGraphics cannot open, or one locked by a password. Pinned.</summary>
    public static string NotAPdf(string file) =>
        $"Error: {file} could not be read as a PDF (a damaged file, or one locked by a password)";

    public static string TooLarge(string file) =>
        $"Error: {file} is over {(MaxTextBytes / 1_000_000).ToString(CultureInfo.InvariantCulture)} MB, too long to print";

    public static string NotAPicture(string file) => $"Error: {file} could not be read as a picture";

    public static string BadCopies(string given) =>
        $"Error: '{given.Trim()}' is not a number of copies; use 1 to {MaxCopies.ToString(CultureInfo.InvariantCulture)}";

    public static string BadPages(string spec, int total) =>
        $"Error: '{spec}' is not a page range of this {total.ToString(CultureInfo.InvariantCulture)}-page document; use 3, 1-3, 4- or 1,3,5-7";

    // ─── the confirm pane ───────────────────────────────────────────────────────

    /// <summary>The question on the pane before the model prints: what, where, how many sheets.</summary>
    public static string ConfirmQuestion(PrintPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        // A file sent as it is with its pages counted (a Mac's PDF, 2026-10-08) asks as a drawn one does; Windows' shell plans count none.
        if (plan.Kind == PrintKind.Shell && plan.TotalPages == 0)
        {
            return $"Let the model print {plan.Display} with the program Windows has for it, on {plan.Printer}?";
        }

        return $"Let the model print {plan.Display} ({Sheets(plan)}) on {plan.Printer}?";
    }

    // ─── results ────────────────────────────────────────────────────────────────

    /// <summary>The transcript's one-line note for a result: its first line.</summary>
    public static string Note(string result)
    {
        ArgumentNullException.ThrowIfNull(result);
        int newline = result.IndexOf('\n', StringComparison.Ordinal);
        return newline < 0 ? result : result[..newline];
    }

    /// <summary>A job the spooler took.</summary>
    public static string Printed(PrintPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return $"Printed {plan.Display}: {Sheets(plan)} to {plan.Printer}";
    }

    /// <summary>A file handed to its own program's <c>print</c> command.</summary>
    public static string ShellStarted(PrintPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return $"Sent {plan.Display} to the program Windows prints it with, on {plan.Printer}; it prints from there";
    }

    /// <summary>How much paper: <c>3 pages</c>, <c>pages 2-3 of 5</c>, <c>× 2 copies</c>, <c>landscape</c>.</summary>
    public static string Sheets(PrintPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        string pages = plan.Pages.Count == plan.TotalPages
            ? Count(plan.TotalPages, "page")
            : (plan.Pages.Count == 1 ? "page " : "pages ") + Ranges(plan.Pages) + " of " + plan.TotalPages.ToString(CultureInfo.InvariantCulture);
        string copies = plan.Copies > 1 ? $" × {plan.Copies.ToString(CultureInfo.InvariantCulture)} copies" : "";
        string landscape = plan.Landscape ? ", landscape" : "";
        return pages + copies + landscape;
    }

    /// <summary>Page numbers as ranges: <c>1-3, 5</c>.</summary>
    public static string Ranges(IReadOnlyList<int> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        var parts = new List<string>();
        int i = 0;
        while (i < pages.Count)
        {
            int j = i;
            while (j + 1 < pages.Count && pages[j + 1] == pages[j] + 1)
            {
                j++;
            }

            parts.Add(j == i ? pages[i].ToString(CultureInfo.InvariantCulture) : pages[i].ToString(CultureInfo.InvariantCulture) + "-" + pages[j].ToString(CultureInfo.InvariantCulture));
            i = j + 1;
        }

        return string.Join(", ", parts);
    }

    /// <summary>The header's page number.</summary>
    public static string PageOf(int number, int total) =>
        $"page {number.ToString(CultureInfo.InvariantCulture)} of {total.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The header's time stamp.</summary>
    public static string Stamp(DateTimeOffset now) => now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    // ─── the printers ───────────────────────────────────────────────────────────

    /// <summary>The printers for the model: the header line, then one per printer with its marks.</summary>
    public static string PrinterList(IReadOnlyList<PrinterInfo> printers, string? setting)
    {
        ArgumentNullException.ThrowIfNull(printers);
        if (printers.Count == 0)
        {
            return OperatingSystem.IsMacOS() ? MacNoPrinters : NoPrinters;
        }

        return Count(printers.Count, "printer") + "\n" + string.Join("\n", printers.Select(p => "- " + PrinterLine(p, setting)));
    }

    public const string NoPrinters = "No printers are installed";

    /// <summary>A Mac's <see cref="NoPrinters"/> (2026-10-08). Pinned.</summary>
    public const string MacNoPrinters = "No printers are set up on this Mac";

    /// <summary>The default printer's mark: <c>Windows default</c>, on a Mac (2026-10-08) <c>system default</c>.</summary>
    public static string DefaultMark => OperatingSystem.IsMacOS() ? "system default" : "Windows default";

    /// <summary>
    /// One printer with its marks: the system's default, the <c>Print default printer</c> setting. A Mac's printer (2026-10-08)
    /// shows its description after the queue name, the name <c>printer=</c> wants: <c>Brother_HL_L2340D_series — Brother HL-L2340D series</c>.
    /// </summary>
    public static string PrinterLine(PrinterInfo printer, string? setting)
    {
        ArgumentNullException.ThrowIfNull(printer);
        var marks = new List<string>(2);
        if (printer.IsDefault)
        {
            marks.Add(DefaultMark);
        }

        if (!string.IsNullOrWhiteSpace(setting) && string.Equals(printer.Name, setting.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            marks.Add("Print default printer");
        }

        string description = string.IsNullOrEmpty(printer.Description) ? "" : " — " + printer.Description;
        return printer.Name + description + (marks.Count == 0 ? "" : " (" + string.Join(", ", marks) + ")");
    }

    // ─── /print ─────────────────────────────────────────────────────────────────

    public const string Working = "Printing";

    /// <summary>A <c>/print</c> cancelled by ESC or Ctrl+C (2026-10-08): a job half sent was withdrawn, so nothing prints. Pinned.</summary>
    public const string Cancelled = "(print cancelled)";

    public const string ReplyWord = "reply";

    public const string PrintersWord = "printers";

    public const string Usage =
        "Usage: /print <file> [printer=<name>] [copies=N] [pages=1-3] [landscape] · /print reply [options] for the last reply · /print printers";

    public const string ReplyNote = "print the last reply";

    public const string PrintersNote = "list the printers";

    public const string LandscapeWord = "landscape";

    // ─── helpers ────────────────────────────────────────────────────────────────

    public static string Names(IReadOnlyList<PrinterInfo> printers) => string.Join(", ", printers.Select(p => p.Name));

    public static string Count(int n, string singular) =>
        n.ToString(CultureInfo.InvariantCulture) + " " + (n == 1 ? singular : singular + "s");

    private static string Extension(string file)
    {
        string extension = Path.GetExtension(file);
        return extension.Length == 0 ? "extensionless" : extension.ToLowerInvariant();
    }
}
