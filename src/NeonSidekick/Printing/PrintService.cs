using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Settings;
using NeonSidekick.UI.Markdown;
using NeonSidekick.Viewer;

namespace NeonSidekick.Printing;

/// <summary>How a print is made: a listing, styled markdown, a picture — all drawn by the app — or the file's own program.</summary>
public enum PrintKind
{
    Listing,
    Markdown,
    Picture,
    Shell,
}

/// <summary>
/// What to print: a sandbox path, or markdown text (<c>/print reply</c>) with its <paramref name="Title"/>; the printer (blank:
/// the setting, then the Windows default), the copies, the page range and the orientation. <paramref name="OutputFile"/>
/// (2026-10-03, the PDF fallback of <c>convert_to_pdf</c>) is the file a print-to-file printer writes instead of asking where;
/// a file only the shell's program prints cannot take one.
/// </summary>
public sealed record PrintRequest(string? Path, string? Printer = null, int Copies = 1, string? Pages = null, bool Landscape = false, string? Markdown = null, string? Title = null, string? OutputFile = null);

/// <summary>
/// A print ready to go: how, what it shows as (<paramref name="Display"/>, the path relative to the working directory or the
/// reply's title), the printer, the whole document's page count and the pages chosen, the copies, the orientation, and either
/// the job to send or the file to hand to its own program.
/// </summary>
public sealed record PrintPlan(PrintKind Kind, string Display, string Printer, int TotalPages, IReadOnlyList<int> Pages, int Copies, bool Landscape, PrintJob? Job, string? ShellPath);

/// <summary>
/// The one door to printing (2026-09-28): <c>print_file</c>, <c>list_printers</c>, <c>/print</c> and headless all come through
/// here. <see cref="Prepare"/> judges the request — the sandbox, the file's kind, the printer, the copies, the page range — and
/// lays the pages out against the printer's own paper and fonts, so the confirm pane can say how many sheets; <see cref="Print"/>
/// sends what was prepared. A file the app does not draw (a PDF, an Office file) goes to the program Windows has for it, on the
/// Windows default printer. Every failure is an <c>Error:</c> sentence, never a throw.
/// </summary>
public sealed class PrintService
{
    private readonly IPrintSpooler _spooler;
    private readonly WorkingDirectory _files;
    private readonly Func<AppSettingsData> _effective;
    private readonly TimeProvider _time;
    private readonly IMarkdownParser _parser;

    public PrintService(IPrintSpooler spooler, WorkingDirectory files, Func<AppSettingsData> effective, TimeProvider? time = null, IMarkdownParser? parser = null)
    {
        _spooler = spooler ?? throw new ArgumentNullException(nameof(spooler));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
        _time = time ?? TimeProvider.System;
        _parser = parser ?? new MarkdigParser();
    }

    public AppSettingsData Effective => _effective();

    /// <summary>The installed printers.</summary>
    public IReadOnlyList<PrinterInfo> Printers() => _spooler.Printers();

    /// <summary>The body size in force: the setting, held to its range.</summary>
    public double FontSize => Math.Clamp(_effective().PrintFontSize, PrintLayout.MinFontSize, PrintLayout.MaxFontSize);

    /// <summary>
    /// The printer a request means: <paramref name="requested"/> when given — its exact name, else the one printer whose name
    /// holds it — else the setting <c>Print default printer</c>, else the Windows default.
    /// </summary>
    public (string? Name, string? Error) ResolvePrinter(string? requested, IReadOnlyList<PrinterInfo> printers)
    {
        ArgumentNullException.ThrowIfNull(printers);
        if (printers.Count == 0)
        {
            return (null, PrintText.NoPrinterHere);
        }

        string wanted = (requested ?? "").Trim();
        if (wanted.Length == 0)
        {
            wanted = (_effective().PrintDefaultPrinter ?? "").Trim();
        }

        if (wanted.Length == 0)
        {
            return printers.FirstOrDefault(p => p.IsDefault) is { } fallback ? (fallback.Name, null) : (null, PrintText.NoDefaultHere(printers));
        }

        if (printers.FirstOrDefault(p => string.Equals(p.Name, wanted, StringComparison.OrdinalIgnoreCase)) is { } exact)
        {
            return (exact.Name, null);
        }

        // A Mac's printer by the name System Settings shows too (2026-10-08, the user's call): CUPS' queue is Brother_HL_L2340D_series,
        // its description Brother HL-L2340D series. Windows sets no description, so nothing changes there.
        if (printers.FirstOrDefault(p => string.Equals(p.Description, wanted, StringComparison.OrdinalIgnoreCase)) is { } described)
        {
            return (described.Name, null);
        }

        var near = printers.Where(p => p.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase) || (p.Description?.Contains(wanted, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        return near.Count switch
        {
            1 => (near[0].Name, null),
            0 => (null, PrintText.UnknownPrinter(wanted, printers)),
            _ => (null, PrintText.AmbiguousPrinter(wanted, near)),
        };
    }

    /// <summary>The request judged and laid out: the plan, or why not.</summary>
    public (PrintPlan? Plan, string? Error) Prepare(PrintRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Copies is < 1 or > PrintText.MaxCopies)
        {
            return (null, PrintText.BadCopies(request.Copies.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        PrintKind kind;
        string display;
        string? full = null;
        string? text = null;
        ViewerBitmap? bitmap = null;
        if (request.Markdown is not null)
        {
            kind = PrintKind.Markdown;
            text = request.Markdown;
            display = string.IsNullOrWhiteSpace(request.Title) ? PrintText.ReplyTitle : request.Title.Trim();
        }
        else
        {
            string path = (request.Path ?? "").Trim();
            if (path.Length == 0)
            {
                return (null, FileText.PathRequired(Llm.Tools.FileTool.PathArgument));
            }

            var outcome = _files.Resolve(path, forWrite: false, out string resolved);
            if (outcome != FileOutcome.Ok)
            {
                return (null, FileText.Error(outcome, path, "print"));
            }

            if (Directory.Exists(resolved))
            {
                return (null, FileText.Error(FileOutcome.IsDirectory, path, "print"));
            }

            if (!File.Exists(resolved))
            {
                return (null, FileText.Error(FileOutcome.Missing, path, "print"));
            }

            full = resolved;
            display = _files.Relative(resolved);
            (kind, text, bitmap, string? error) = Load(resolved, display);
            if (error is not null)
            {
                return (null, error);
            }

            // A PDF whose head is all ASCII reads as text; where PDFs go to the printer as they are (a Mac, 2026-10-08) it goes so,
            // never as a listing of its source. Windows' spooler takes no options, so nothing changes there.
            if (kind == PrintKind.Listing && _spooler.ShellPrintTakesOptions && _spooler.CanShellPrint(resolved))
            {
                kind = PrintKind.Shell;
            }
        }

        var printers = _spooler.Printers();
        if (kind == PrintKind.Shell && _spooler.ShellPrintTakesOptions)
        {
            return PrepareAsItIs(request, display, full!, printers);
        }

        if (kind == PrintKind.Shell)
        {
            if (request.OutputFile is not null)
            {
                return (null, PrintText.NoFileOutput(display));
            }

            if (!string.IsNullOrWhiteSpace(request.Printer) || request.Copies != 1 || !string.IsNullOrWhiteSpace(request.Pages) || request.Landscape)
            {
                return (null, PrintText.ShellDefaultOnly(display));
            }

            if (!_spooler.CanShellPrint(full!))
            {
                return (null, PrintText.NoHandler(display));
            }

            string shellPrinter = printers.FirstOrDefault(p => p.IsDefault)?.Name ?? "the Windows default printer";
            return (new PrintPlan(kind, display, shellPrinter, 0, [], 1, false, null, full), null);
        }

        var (printer, printerError) = ResolvePrinter(request.Printer, printers);
        if (printer is null)
        {
            return (null, printerError);
        }

        using var surface = _spooler.Open(printer, request.Landscape, out string openError);
        if (surface is null)
        {
            return (null, openError);
        }

        IReadOnlyList<PrintPage> pages = kind switch
        {
            PrintKind.Picture => PrintLayout.Picture(bitmap!, surface.Page),
            PrintKind.Markdown => PrintLayout.Markdown(_parser.Parse(text!), surface.Page, surface, FontSize),
            _ => PrintLayout.Listing(text!, surface.Page, surface, FontSize),
        };
        pages = PrintLayout.WithHeaders(pages, display, PrintText.Stamp(_time.GetLocalNow()), surface.Page, surface);
        if (!PrintLayout.PageRange(request.Pages, pages.Count, out var chosen, out string rangeError))
        {
            return (null, rangeError);
        }

        var job = new PrintJob(printer, display, pages.Where(p => chosen.Contains(p.Number)).ToList(), request.Landscape, request.Copies, request.OutputFile);
        return (new PrintPlan(kind, display, printer, pages.Count, [.. chosen], request.Copies, request.Landscape, job, null), null);
    }

    /// <summary>
    /// A file sent to the printer as it is (2026-10-08, a Mac's PDF straight to CUPS; the user's call: printer, copies and pages
    /// as for a drawn file). Its pages are counted so a range is judged and the pane says how many; landscape is refused, the
    /// PDF's pages keeping their own orientation, and so is an output file.
    /// </summary>
    private (PrintPlan? Plan, string? Error) PrepareAsItIs(PrintRequest request, string display, string full, IReadOnlyList<PrinterInfo> printers)
    {
        if (request.OutputFile is not null)
        {
            return (null, PrintText.PdfNoFileOutput(display));
        }

        if (!_spooler.CanShellPrint(full))
        {
            return (null, PrintText.MacNoHandler(display));
        }

        if (request.Landscape)
        {
            return (null, PrintText.PdfLandscape(display));
        }

        var (printer, printerError) = ResolvePrinter(request.Printer, printers);
        if (printer is null)
        {
            return (null, printerError);
        }

        int total = _spooler.ShellPageCount(full);
        if (total <= 0)
        {
            return (null, PrintText.NotAPdf(display));
        }

        if (!PrintLayout.PageRange(request.Pages, total, out var chosen, out string rangeError))
        {
            return (null, rangeError);
        }

        return (new PrintPlan(PrintKind.Shell, display, printer, total, [.. chosen], request.Copies, false, null, full), null);
    }

    /// <summary>What the file is and its content: a picture decoded, text read (markdown by its extension), anything else the shell's.</summary>
    private static (PrintKind Kind, string? Text, ViewerBitmap? Bitmap, string? Error) Load(string full, string display)
    {
        try
        {
            if (Files.ImageFile.IsImagePath(full))
            {
                var info = new FileInfo(full);
                if (info.Length > Files.ImageFile.MaxFileBytes)
                {
                    return (PrintKind.Picture, null, null, FileText.Error(FileOutcome.ImageTooBig, display, "print"));
                }

                var bitmap = ViewerImage.Decode(File.ReadAllBytes(full), display);
                return bitmap is null ? (PrintKind.Picture, null, null, PrintText.NotAPicture(display)) : (PrintKind.Picture, null, bitmap, null);
            }

            if (!WorkingDirectory.IsTextFile(full))
            {
                return (PrintKind.Shell, null, null, null);
            }

            if (new FileInfo(full).Length > PrintText.MaxTextBytes)
            {
                return (PrintKind.Listing, null, null, PrintText.TooLarge(display));
            }

            string text = WorkingDirectory.Decode(File.ReadAllBytes(full), out _);
            return (IsMarkdown(full) ? PrintKind.Markdown : PrintKind.Listing, text, null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (PrintKind.Listing, null, null, PrintText.Failed(display, ex.Message));
        }
    }

    /// <summary>Whether a text file prints as styled markdown: <c>.md</c> or <c>.markdown</c>.</summary>
    public static bool IsMarkdown(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string extension = Path.GetExtension(path);
        return string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, ".markdown", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Sends <paramref name="plan"/>, off the caller's thread: the result line, or the failure.</summary>
    public async Task<string> PrintAsync(PrintPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        // A file sent as it is with its pages counted (a Mac's PDF) takes the plan's printer, copies and pages; Windows' shell plans count none.
        bool asItIs = plan.Kind == PrintKind.Shell && plan.TotalPages > 0;
        IReadOnlyList<int>? pages = plan.Pages.Count == plan.TotalPages ? null : plan.Pages;
        string? error = await Task.Run(() => asItIs ? _spooler.ShellPrint(plan.ShellPath!, plan.Printer, plan.Copies, pages, cancellationToken) : plan.Kind == PrintKind.Shell ? _spooler.ShellPrint(plan.ShellPath!) : _spooler.Print(plan.Job!, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (error is not null)
        {
            DiagnosticLog.Warn(PrintText.Category, error);
            return error;
        }

        string done = plan.Kind == PrintKind.Shell && !asItIs ? PrintText.ShellStarted(plan) : PrintText.Printed(plan);
        DiagnosticLog.Info(PrintText.Category, done);
        return done;
    }

    /// <summary><see cref="Prepare"/> then <see cref="PrintAsync"/>, the layout off the caller's thread too.</summary>
    public async Task<string> RunAsync(PrintRequest request, CancellationToken cancellationToken)
    {
        var (plan, error) = await Task.Run(() => Prepare(request), cancellationToken).ConfigureAwait(false);
        return plan is null ? error! : await PrintAsync(plan, cancellationToken).ConfigureAwait(false);
    }
}
