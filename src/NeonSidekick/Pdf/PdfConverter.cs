using System.Text;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Printing;
using NeonSidekick.Settings;
using NeonSidekick.Web;

namespace NeonSidekick.Pdf;

/// <summary>
/// What to make a PDF of (2026-10-03): exactly one of a sandbox <paramref name="Path"/>, a web page's <paramref name="Url"/>, or
/// <paramref name="Markdown"/> text (the reply, the model's own) under <paramref name="Title"/>; where it goes
/// (<paramref name="To"/>: a file, a folder, or blank for beside the source), whether it may replace a file there, and the page
/// (<paramref name="Landscape"/>, <paramref name="Paper"/>; a web page keeps its own).
/// </summary>
public sealed record PdfRequest(string? Path = null, Uri? Url = null, string? Markdown = null, string? Title = null, string? To = null, bool Overwrite = false, bool Landscape = false, PdfPaperSize? Paper = null);

/// <summary>
/// The one door to making PDFs (2026-10-03, the user's ask: PDF conversion with no new package): <c>convert_to_pdf</c>,
/// <c>/pdf</c> and headless all come through here. Two engines, chosen by <c>PDF engine</c> (<see cref="PdfEngine"/>):
/// <list type="bullet">
/// <item>the browser (Edge, Chrome or Brave, <see cref="IHeadlessBrowser.PrintToPdfAsync"/>) prints a page this class writes to
/// the temp folder — Markdown, a listing or a picture through <see cref="PdfHtml"/>, a user's HTML through
/// <see cref="PdfHtmlSanitizer"/> — or a web page, after <c>Web browser network mode</c> has judged its host;</item>
/// <item>Microsoft Print to PDF (<see cref="PrintService"/> with an output file) draws Markdown, text and pictures as
/// <c>/print</c> would, in black and white on the driver's paper, when there is no browser or (under <c>auto</c>) the browser
/// failed.</item>
/// </list>
/// Every refusal comes before any work: the source, the engine, then the output through
/// <see cref="WorkingDirectory.BeginWrite"/> (outside the sandbox, a folder in the way, a file there without overwrite). The
/// engine writes a temp file; the finished PDF is copied into the sandbox and the temp files go. Pictures a page shows are
/// inlined from the sandbox only, <see cref="MaxInlineBytes"/> at most. Every failure is an <c>Error:</c> sentence, never a throw.
/// </summary>
public sealed class PdfConverter
{
    /// <summary>The most picture and stylesheet bytes one page inlines: base64 makes them a third bigger again.</summary>
    public const long MaxInlineBytes = 64_000_000;

    /// <summary>How long Microsoft Print to PDF gets to write the file after the job is in.</summary>
    public static readonly TimeSpan PrinterWait = TimeSpan.FromSeconds(60);

    private readonly IHeadlessBrowser _browser;
    private readonly WebFetcher _fetcher;
    private readonly PrintService _print;
    private readonly WorkingDirectory _files;
    private readonly Func<AppSettingsData> _effective;
    private readonly TimeProvider _time;
    private readonly string _tempFolder;
    private readonly TimeSpan _printerWait;

    /// <param name="tempFolder">Where the engines' files go while they work; <c>%TEMP%\NeonSidekick\pdf</c> by default.</param>
    /// <param name="printerWait">How long the printer's file is waited for; <see cref="PrinterWait"/> by default, less in tests.</param>
    public PdfConverter(IHeadlessBrowser browser, WebFetcher fetcher, PrintService print, WorkingDirectory files, Func<AppSettingsData> effective, TimeProvider? time = null, string? tempFolder = null, TimeSpan? printerWait = null)
    {
        _printerWait = printerWait ?? PrinterWait;
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
        _print = print ?? throw new ArgumentNullException(nameof(print));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
        _time = time ?? TimeProvider.System;
        _tempFolder = tempFolder ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NeonSidekick", "pdf");
    }

    /// <summary>The source loaded: its kind, how results name it, the sandbox file (null for text and pages) and its content.</summary>
    private sealed record Source(PdfSourceKind Kind, string Display, string? Full, string? Text, byte[]? Bytes);

    public async Task<string> ConvertAsync(PdfRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        int sources = (string.IsNullOrWhiteSpace(request.Path) ? 0 : 1) + (request.Url is null ? 0 : 1) + (request.Markdown is null ? 0 : 1);
        if (sources != 1)
        {
            return PdfText.OneSource;
        }

        var effective = _effective();
        var (source, error) = request.Url is not null
            ? await WebSourceAsync(request, effective, cancellationToken).ConfigureAwait(false)
            : request.Markdown is not null
                ? (new Source(PdfSourceKind.Markdown, string.IsNullOrWhiteSpace(request.Title) ? PrintText.ReplyTitle : request.Title.Trim(), null, request.Markdown, null), null)
                : FileSource(request.Path!);
        if (source is null)
        {
            return error!;
        }

        string engine = PdfEngine.Resolve(effective.PdfEngine);
        string? executable = engine == PdfEngine.Printer ? null : _browser.Locate(effective.WebBrowserPath ?? "");
        string what = source.Kind == PdfSourceKind.Url ? "a web page" : source.Display;
        var (route, routeError) = PdfPlan.Choose(source.Kind, engine, executable is not null, NeedsPrinterCheck(engine, executable) && PrinterInstalled(), what);
        if (route is null)
        {
            return routeError!;
        }

        string? sourceRelative = source.Full is null ? null : source.Display;
        string target = PdfPlan.OutputFor(request.To, sourceRelative, request.Url, request.Title, _time.GetLocalNow(), _files.IsExistingDirectory);
        var begun = _files.BeginWrite(target, request.Overwrite, out var pending);
        if (pending is null)
        {
            return FileText.Error(begun.Outcome, begun.Relative, "write", begun.Detail);
        }

        string id = Guid.NewGuid().ToString("N");
        string htmlPath = System.IO.Path.Combine(_tempFolder, id + ".html");
        string pdfPath = System.IO.Path.Combine(_tempFolder, id + ".pdf");
        using (pending)
        {
            try
            {
                Directory.CreateDirectory(_tempFolder);
                string? fallbackReason = null;
                string madeWith;
                if (route == PdfRoute.Browser)
                {
                    var (ok, detail) = await BrowserAsync(executable!, source, request, htmlPath, pdfPath, cancellationToken).ConfigureAwait(false);
                    if (!ok && detail.StartsWith("Error:", StringComparison.Ordinal))
                    {
                        return detail;
                    }

                    if (!ok)
                    {
                        if (engine != PdfEngine.Auto || PdfPlan.NeedsBrowser(source.Kind) || !PrinterInstalled())
                        {
                            return PdfText.BrowserFailed(PdfText.BrowserName(executable!), detail);
                        }

                        DiagnosticLog.Warn(PdfText.Category, $"{PdfText.BrowserName(executable!)} could not make a PDF of {source.Display} ({detail}); trying {PrintText.PrintToPdfPrinter}.");
                        fallbackReason = detail;
                        route = PdfRoute.Printer;
                    }
                }

                if (route == PdfRoute.Printer)
                {
                    string? printed = await PrinterAsync(source, request, pdfPath, cancellationToken).ConfigureAwait(false);
                    if (printed is not null)
                    {
                        return printed;
                    }

                    madeWith = PrintText.PrintToPdfPrinter;
                }
                else
                {
                    madeWith = PdfText.BrowserName(executable!);
                }

                await using (var made = new FileStream(pdfPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true))
                {
                    await made.CopyToAsync(pending.Stream, cancellationToken).ConfigureAwait(false);
                }

                string result = PdfText.Made(pending.Commit(), source.Display, madeWith, fallbackReason);
                DiagnosticLog.Info(PdfText.Category, result);
                return result;
            }
            catch (Exception ex) when (WorkingDirectory.IsFileFailure(ex))
            {
                return FileText.CouldNot("make a PDF of", source.Display, ex.Message);
            }
            finally
            {
                WorkingDirectory.DeleteQuietly(htmlPath);
                WorkingDirectory.DeleteQuietly(pdfPath);
            }
        }
    }

    /// <summary>Whether choosing needs to know about the printer: not when the browser is there and wanted.</summary>
    private static bool NeedsPrinterCheck(string engine, string? executable) =>
        engine == PdfEngine.Printer || (engine == PdfEngine.Auto && executable is null);

    private bool PrinterInstalled() =>
        _print.Printers().Any(p => string.Equals(p.Name, PrintText.PrintToPdfPrinter, StringComparison.OrdinalIgnoreCase));

    private async Task<(Source?, string?)> WebSourceAsync(PdfRequest request, AppSettingsData effective, CancellationToken cancellationToken)
    {
        var url = request.Url!;
        if (!WebFetcher.IsHttp(url))
        {
            return (null, WebText.NotHttp(url.OriginalString));
        }

        if (request.Landscape || request.Paper is not null)
        {
            return (null, PdfText.PageLayoutFixed);
        }

        if (await _fetcher.PreflightAsync(url.Host, WebAccess.Options(effective), cancellationToken).ConfigureAwait(false) is { } refused)
        {
            return (null, WebText.Error(refused, url.AbsoluteUri, url.Host));
        }

        return (new Source(PdfSourceKind.Url, url.AbsoluteUri, null, null, null), null);
    }

    private (Source?, string?) FileSource(string path)
    {
        string given = path.Trim();
        var outcome = _files.Resolve(given, forWrite: false, out string full);
        if (outcome != FileOutcome.Ok)
        {
            return (null, FileText.Error(outcome, given, "read"));
        }

        if (Directory.Exists(full))
        {
            return (null, FileText.Error(FileOutcome.IsDirectory, given, "read"));
        }

        if (!File.Exists(full))
        {
            return (null, FileText.Error(FileOutcome.Missing, given, "read"));
        }

        string display = _files.Relative(full);
        if (PdfPlan.Classify(full, WorkingDirectory.IsTextFile, out bool isPdf) is not { } kind)
        {
            return (null, isPdf ? PdfText.AlreadyPdf(display) : PdfText.NotConvertible(display));
        }

        try
        {
            long length = new FileInfo(full).Length;
            if (kind == PdfSourceKind.Picture)
            {
                return length > ImageFile.MaxFileBytes
                    ? (null, FileText.Error(FileOutcome.ImageTooBig, display, "read"))
                    : (new Source(kind, display, full, null, File.ReadAllBytes(full)), null);
            }

            if (length > PrintText.MaxTextBytes)
            {
                return (null, PdfText.TooLarge(display));
            }

            return (new Source(kind, display, full, WorkingDirectory.Decode(File.ReadAllBytes(full), out _), null), null);
        }
        catch (Exception ex) when (WorkingDirectory.IsFileFailure(ex))
        {
            return (null, FileText.CouldNot("read", display, ex.Message));
        }
    }

    /// <summary>The browser's run: (true, "") with the PDF at <paramref name="pdfPath"/>, (false, why) when it failed, or (false, an <c>Error:</c> sentence) for a page that could not be built.</summary>
    private async Task<(bool Ok, string Detail)> BrowserAsync(string executable, Source source, PdfRequest request, string htmlPath, string pdfPath, CancellationToken cancellationToken)
    {
        Uri page;
        if (source.Kind == PdfSourceKind.Url)
        {
            page = new Uri(source.Display);
        }
        else
        {
            var (html, error) = Page(source, request);
            if (html is null)
            {
                return (false, error!);
            }

            await File.WriteAllTextAsync(htmlPath, html, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            page = new Uri(htmlPath);
        }

        var made = await _browser.PrintToPdfAsync(executable, page, pdfPath, cancellationToken).ConfigureAwait(false);
        return (made.Ok, made.Detail);
    }

    /// <summary>The page the browser prints for a file or text: the HTML, or null with the refusal.</summary>
    private (string? Html, string? Error) Page(Source source, PdfRequest request)
    {
        var paper = request.Paper ?? PdfPaper.Default;
        string folder = source.Full is null ? _files.Root : System.IO.Path.GetDirectoryName(source.Full)!;
        var inliner = new Inliner(_files, folder);
        string html = source.Kind switch
        {
            PdfSourceKind.Picture => PdfHtml.Document(source.Display, PdfHtml.Picture(PdfHtml.DataUri(source.Bytes!, PdfHtml.ImageMediaType(source.Full!) ?? "image/png"), source.Display), paper, request.Landscape),
            PdfSourceKind.Listing => PdfHtml.Document(source.Display, PdfHtml.Listing(source.Text!, source.Display), paper, request.Landscape),
            PdfSourceKind.Html => PdfHtmlSanitizer.Rewrite(source.Text!, paper, request.Landscape, inliner.Bytes),
            _ => PdfHtml.Document(source.Display, PdfHtml.Markdown(source.Text!, inliner.Picture), paper, request.Landscape),
        };
        return inliner.OverBudget ? (null, PdfText.PicturesTooLarge((int)(MaxInlineBytes / 1_000_000))) : (html, null);
    }

    /// <summary>Microsoft Print to PDF's run: null with the PDF at <paramref name="pdfPath"/>, else the failure.</summary>
    private async Task<string?> PrinterAsync(Source source, PdfRequest request, string pdfPath, CancellationToken cancellationToken)
    {
        var print = source.Full is null
            ? new PrintRequest(null, Printer: PrintText.PrintToPdfPrinter, Landscape: request.Landscape, Markdown: source.Text, Title: source.Display, OutputFile: pdfPath)
            : new PrintRequest(source.Full, Printer: PrintText.PrintToPdfPrinter, Landscape: request.Landscape, OutputFile: pdfPath);
        var (plan, error) = await Task.Run(() => _print.Prepare(print), cancellationToken).ConfigureAwait(false);
        if (plan is null)
        {
            return error;
        }

        string printed = await _print.PrintAsync(plan, cancellationToken).ConfigureAwait(false);
        if (printed.StartsWith("Error:", StringComparison.Ordinal))
        {
            return printed;
        }

        return await PrintToFile.WaitForPdfAsync(pdfPath, _printerWait, _time, cancellationToken).ConfigureAwait(false) switch
        {
            PdfFileState.Ready => null,
            PdfFileState.Missing => PdfText.PrinterNoFile(PrintText.PrintToPdfPrinter),
            PdfFileState.Unfinished => PdfText.PrinterUnfinished(PrintText.PrintToPdfPrinter),
            _ => PdfText.PrinterNotPdf(PrintText.PrintToPdfPrinter),
        };
    }

    /// <summary>
    /// The files a page shows, read from the sandbox only: an address relative to <c>folder</c> (the source's own) or a
    /// <c>file:</c> URL, judged by <see cref="WorkingDirectory.Resolve(string, bool, out string)"/>; a web address, a missing
    /// file or one outside is null. Counts what it hands out against <see cref="MaxInlineBytes"/>.
    /// </summary>
    private sealed class Inliner(WorkingDirectory files, string folder)
    {
        private long _total;

        public bool OverBudget { get; private set; }

        /// <summary>A Markdown picture as a <c>data:</c> URI, or null.</summary>
        public string? Picture(string address)
        {
            if (address.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                return address;
            }

            string? full = Locate(address);
            if (full is null || PdfHtml.ImageMediaType(full) is not { } type)
            {
                return null;
            }

            return Read(full, ImageFile.MaxFileBytes) is { } bytes ? PdfHtml.DataUri(bytes, type) : null;
        }

        /// <summary>A file an HTML page names, its bytes, or null.</summary>
        public byte[]? Bytes(string address) => Locate(address) is { } full ? Read(full, ImageFile.MaxFileBytes) : null;

        private string? Locate(string address)
        {
            string text = address.Trim();
            int cut = text.IndexOfAny(['?', '#']);
            if (cut >= 0)
            {
                text = text[..cut];
            }

            if (text.Length == 0)
            {
                return null;
            }

            string candidate;
            if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && !(text.Length > 1 && text[1] == ':'))
            {
                if (!uri.IsFile)
                {
                    return null;
                }

                candidate = uri.LocalPath;
            }
            else
            {
                try
                {
                    candidate = System.IO.Path.Combine(folder, Uri.UnescapeDataString(text).Replace('/', System.IO.Path.DirectorySeparatorChar));
                }
                catch (ArgumentException)
                {
                    return null;
                }
            }

            return files.Resolve(candidate, forWrite: false, out string full) == FileOutcome.Ok && File.Exists(full) ? full : null;
        }

        private byte[]? Read(string full, long cap)
        {
            try
            {
                long length = new FileInfo(full).Length;
                if (length > cap)
                {
                    return null;
                }

                if (_total + length > MaxInlineBytes)
                {
                    OverBudget = true;
                    return null;
                }

                _total += length;
                return File.ReadAllBytes(full);
            }
            catch (Exception ex) when (WorkingDirectory.IsFileFailure(ex))
            {
                return null;
            }
        }
    }
}
