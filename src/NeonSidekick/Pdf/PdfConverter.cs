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
/// <remarks><paramref name="ByUser"/> (2026-10-22, the write guard): the user's own <c>/pdf</c>, whose write takes the app's pass (<see cref="WriteGuard.AppWrites"/>).</remarks>
public sealed record PdfRequest(string? Path = null, Uri? Url = null, string? Markdown = null, string? Title = null, string? To = null, bool Overwrite = false, bool Landscape = false, PdfPaperSize? Paper = null, bool ByUser = false);

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
/// <para>
/// Since 2026-10-13 a web page behind a bot check that wants a person can be printed by the Browser window itself
/// (<see cref="BotCheckSolver"/>, per <c>Web browser botcheck window</c>): with someone to ask, the page is first fetched as
/// <c>web_fetch</c> would (<see cref="WebFetcher.FetchAsync"/>), and when that answer offers the window
/// (<see cref="BotCheck.Offers(FetchResult)"/>) the user solves the check there and WebView2 prints the page it let through. Any other
/// ending goes on as before: the headless browser prints what it gets.
/// </para>
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
    private readonly BotCheckSolver? _botCheck;

    /// <param name="tempFolder">Where the engines' files go while they work; <c>%TEMP%\NeonSidekick\pdf</c> by default.</param>
    /// <param name="printerWait">How long the printer's file is waited for; <see cref="PrinterWait"/> by default, less in tests.</param>
    /// <param name="botCheck">Who shows a web page's bot check to the user (2026-10-13); null where nobody can be asked (headless).</param>
    public PdfConverter(IHeadlessBrowser browser, WebFetcher fetcher, PrintService print, WorkingDirectory files, Func<AppSettingsData> effective, TimeProvider? time = null, string? tempFolder = null, TimeSpan? printerWait = null, BotCheckSolver? botCheck = null)
    {
        _printerWait = printerWait ?? PrinterWait;
        _botCheck = botCheck;
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
        _print = print ?? throw new ArgumentNullException(nameof(print));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
        _time = time ?? TimeProvider.System;
        _tempFolder = tempFolder ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NeonSidekick", "pdf");
    }

    /// <summary>
    /// The source loaded: its kind, how results name it, the sandbox file (null for text and pages) and its content; a web page behind
    /// a bot check a person may solve carries the check (<paramref name="Gate"/>, 2026-10-13).
    /// </summary>
    /// <remarks><paramref name="Reach"/> (2026-10-22, review R1): what a web page's print may reach, null under the network mode <c>both</c>.</remarks>
    private sealed record Source(PdfSourceKind Kind, string Display, string? Full, string? Text, byte[]? Bytes, BotCheckRequest? Gate = null, PrintReach? Reach = null);

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
        // An unnamed default is never a replace (2026-10-22, review R2, the fix plan's mechanism 4): the first free name, report-2.pdf…, and
        // overwrite is for a named to alone (pdf_edit's and image_edit's rule); the refusal no longer invites overwrite onto the user's file.
        string to = request.To?.Trim() ?? "";
        bool named = to.Length > 0 && !to.EndsWith('/') && !to.EndsWith('\\') &&!_files.IsExistingDirectory(to);
        // The user's own /pdf with overwrite still replaces its default file: the rule is the model's (it named no file to replace).
        bool overwrite = (named || request.ByUser) && request.Overwrite;
        if (!named && !overwrite && _files.Resolve(target, forWrite: false, out string wanted) == FileOutcome.Ok)
        {
            target = _files.Relative(WorkingDirectory.FirstFree(wanted));
        }

        PendingWrite? pending = null;
        var begun = request.ByUser ? WriteGuard.AsApp(() => _files.BeginWrite(target, overwrite, out pending)) : _files.BeginWrite(target, overwrite, out pending);
        if (pending is null)
        {
            return FileText.Error(begun.Outcome, begun.Relative, "write", begun.Detail);
        }

        string id = Guid.NewGuid().ToString("N");
        string htmlPath = System.IO.Path.Combine(_tempFolder, id + ".html");
        string pdfPath = System.IO.Path.Combine(_tempFolder, id + ".pdf");
        string windowPdfPath = System.IO.Path.Combine(_tempFolder, id + ".window.pdf");
        // The printer's own file (2026-10-22, review T2: the fallback wrote where the failed browser run had left part of a PDF, and the wait
        // took that file's settled length for the spooler's).
        string printerPdfPath = System.IO.Path.Combine(_tempFolder, id + ".printer.pdf");
        using (pending)
        {
            try
            {
                Directory.CreateDirectory(_tempFolder);
                string? fallbackReason = null;
                string madeWith;
                string madePath = pdfPath;
                if (source.Gate is { } gate && await WindowAsync(gate with { PdfPath = windowPdfPath }, request.Url!, effective, cancellationToken).ConfigureAwait(false))
                {
                    madeWith = BotCheckText.WindowName;
                    madePath = windowPdfPath;
                }
                else
                {
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
                        WorkingDirectory.DeleteQuietly(pdfPath);
                        string? printed = await PrinterAsync(source, request, printerPdfPath, cancellationToken).ConfigureAwait(false);
                        if (printed is not null)
                        {
                            return printed;
                        }

                        madeWith = PrintText.PrintToPdfPrinter;
                        madePath = printerPdfPath;
                    }
                    else
                    {
                        madeWith = PdfText.BrowserName(executable!);
                    }
                }

                await using (var made = new FileStream(madePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true))
                {
                    // No bigger than MaxMadeBytes into the working directory (2026-10-22, review T4: a page's own tiny @page made a PDF of
                    // countless pages, copied in whole).
                    if (made.Length > MaxMadeBytes)
                    {
                        return PdfText.MadeTooLarge(source.Display, MaxMadeBytes);
                    }

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
                WorkingDirectory.DeleteQuietly(windowPdfPath);
                WorkingDirectory.DeleteQuietly(printerPdfPath);
            }
        }
    }

    /// <summary>The largest PDF a conversion brings into the working directory (review T4, the fix plan's Limits).</summary>
    public const long MaxMadeBytes = 100L * 1024 * 1024;

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

        // A web address that is a PDF already (2026-10-14, the PDF plan's gaps 15 and 23): headless Chromium would download it, not print
        // it. A .pdf path says so before any request; the probe below catches the rest by its media type.
        if (Files.PdfFile.IsPdfPath(url.AbsolutePath))
        {
            return (null, PdfText.UrlIsPdf(url.AbsoluteUri));
        }

        var options = WebAccess.Options(effective);
        var (refused, admitted) = await _fetcher.AdmitAsync(url.Host, options, cancellationToken).ConfigureAwait(false);
        if (refused is { } refusal)
        {
            return (null, WebText.Error(refusal, url.AbsoluteUri, url.Host));
        }

        // The print is held to the address just approved (2026-10-22, review R1): a redirect, frame or refresh elsewhere fails.
        var reach = admitted is null ? null : PrintReach.For(url.IdnHost, admitted);

        // With someone to ask (2026-10-13), the page as web_fetch gets it first: a bot check a person may solve is the window's to print.
        // Asked without reading a PDF (2026-10-14): the probe only wants to know what the address is, not a 40-page read. Under the
        // default mode over HTTP alone (2026-10-20, the code review: a page of scripts sent the probe's fallback to the headless browser
        // for a DOM that was thrown away, then the print started it again); the headless browser only for a refusal, which it may pass
        // with no person, as web_fetch's fallback would.
        // The probe runs whether or not there is anyone to solve a bot check (2026-10-22, review T3: headless, or with the window off, a PDF
        // served from a path without .pdf went to Chromium, which downloaded it, and an empty page was "made"): over HTTP alone then, for
        // its media type.
        BotCheckRequest? gate = null;
        bool canAsk = _botCheck is not null && BotcheckWindow.Resolve(effective) != BotcheckKind.Disabled;
        {
            bool fallback = options.Engine == FetchEngine.Default;
            var probe = await _fetcher.FetchAsync(url, options with { ReadPdf = false, Engine = fallback || !canAsk ? FetchEngine.HttpClient : options.Engine }, cancellationToken).ConfigureAwait(false);
            if (WebFetcher.IsPdfType(probe.MediaType))
            {
                return (null, PdfText.UrlIsPdf(url.AbsoluteUri));
            }

            bool offered = canAsk && BotCheck.Offers(probe);
            if (offered && fallback)
            {
                // The headless browser got the page: no person is needed, and its print will get it too.
                offered = !(await _fetcher.FetchAsync(url, options with { ReadPdf = false, Engine = FetchEngine.Chromium }, cancellationToken).ConfigureAwait(false)).Ok;
            }

            if (offered)
            {
                gate = new BotCheckRequest(url, BotCheckUse.Pdf, BotCheck.WhyOf(probe.Detail));
            }
        }

        return (new Source(PdfSourceKind.Url, url.AbsoluteUri, null, null, null, gate, reach), null);
    }

    /// <summary>
    /// The Browser window's print of a page behind a bot check (2026-10-13): true with the PDF at the request's path once the user has the
    /// page through and the window printed it, its cookies kept for the web tools; false for any other ending, which the caller follows
    /// with the headless browser's print. The request's path is the window's alone, never the headless print's (2026-10-20, the code
    /// review: a refused page's PDF was left where the headless browser was to write, and taken as its own), and a refused one is deleted.
    /// </summary>
    private async Task<bool> WindowAsync(BotCheckRequest gate, Uri url, AppSettingsData effective, CancellationToken cancellationToken)
    {
        var solved = await _botCheck!(gate, cancellationToken).ConfigureAwait(false);
        if (solved is not { Cleared: true, PdfWritten: true })
        {
            DiagnosticLog.Info(PdfText.Category, $"The Browser window made no PDF of {url}{(solved is null ? "" : BotCheckText.For(solved))}; the headless browser prints it instead.");
            WorkingDirectory.DeleteQuietly(gate.PdfPath!);
            return false;
        }

        if (await _fetcher.SolvedElsewhereAsync(url, solved, WebAccess.Options(effective), cancellationToken).ConfigureAwait(false) is { } elsewhere)
        {
            DiagnosticLog.Info(PdfText.Category, $"The Browser window's page for {url} is not used{elsewhere}.");
            WorkingDirectory.DeleteQuietly(gate.PdfPath!);
            return false;
        }

        _fetcher.Adopt(solved.Cookies, solved.Identity, url);
        return true;
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
            // A picture the browser can't draw is refused, not printed as an empty page (2026-10-22, review S4: HEIC, TIFF and AVIF went in
            // labeled PNG and came out blank).
            if (kind == PdfSourceKind.Picture && PdfHtml.ImageMediaType(full) is null)
            {
                return (null, PdfText.PictureNotDrawable(display));
            }

            if (kind == PdfSourceKind.Picture)
            {
                return length > ImageFile.MaxFileBytes
                    ? (null, FileText.Error(FileOutcome.ImageTooBig, display, "read"))
                    : (new Source(kind, display, full, null, RegularFiles.ReadAllBytes(full)), null);
            }

            if (length > PrintText.MaxTextBytes)
            {
                return (null, PdfText.TooLarge(display));
            }

            return (new Source(kind, display, full, WorkingDirectory.Decode(RegularFiles.ReadAllBytes(full), out _), null), null);
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

        // A page the app wrote reaches nothing (2026-10-22, review R1): its pictures are inlined, and nothing else belongs in it.
        var reach = source.Kind == PdfSourceKind.Url ? source.Reach : PrintReach.Nothing;
        var made = await _browser.PrintToPdfAsync(executable, page, pdfPath, reach, cancellationToken).ConfigureAwait(false);
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
                return RegularFiles.ReadAllBytes(full);
            }
            catch (Exception ex) when (WorkingDirectory.IsFileFailure(ex))
            {
                return null;
            }
        }
    }
}
