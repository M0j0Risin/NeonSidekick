using NeonSidekick.Pdf;
using NeonSidekick.Printing;
using NeonSidekick.Web;

namespace NeonSidekick.App;

public static partial class SmokeChecks
{
    /// <summary>
    /// <c>pdf:html</c> (2026-10-03): the page <c>convert_to_pdf</c> hands the browser, built in the published binary — Markdig's
    /// own HTML renderer (only its parser was proven before, <see cref="ProbeTranscriptMarkdown"/>) with the lexed code-block
    /// renderer swapped in, a table, a task list, an escaped script and a picture's alt text — and a user's page through the
    /// sanitizer. Pure: no browser, no file.
    /// </summary>
    public static SmokeCheck ProbePdfHtml()
    {
        const string name = "pdf:html";
        try
        {
            string body = PdfHtml.Markdown("# T\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\n- [x] done\n\n<script>x()</script>\n\n```csharp\nvar x = 1;\n```\n\n![alt](gone.png)", _ => null);
            string page = PdfHtml.Document("smoke", body, PdfPaperSize.Letter, landscape: false);
            string cleaned = PdfHtmlSanitizer.Rewrite("<p onclick=\"x()\">a</p><script>x()</script>", PdfPaperSize.A4, landscape: false, _ => null);
            string? missing =
                !body.Contains("<table>", StringComparison.Ordinal) ? "no table"
                : !body.Contains("task-list-item", StringComparison.Ordinal) ? "no task list"
                : !body.Contains("tk-keyword", StringComparison.Ordinal) ? "the code was not colored"
                : body.Contains("<script", StringComparison.Ordinal) ? "raw HTML got through"
                : !body.Contains("[image: alt]", StringComparison.Ordinal) ? "the missing picture's alt text is not there"
                : !page.Contains(PdfHtml.PolicyMeta, StringComparison.Ordinal) ? "the page has no policy"
                : cleaned.Contains("<script", StringComparison.Ordinal) || cleaned.Contains("onclick", StringComparison.Ordinal) ? "the sanitizer let a script through"
                : null;
            return new SmokeCheck(name, missing is null, missing ?? $"a page of {page.Length} chars; a user's page cleaned");
        }
        catch (Exception ex)
        {
            return new SmokeCheck(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// <c>pdf:browser</c> (2026-10-03): where Edge, Chrome or Brave is installed, a real one-page print to PDF through
    /// <see cref="HeadlessBrowser.PrintToPdfAsync"/> — the second command line of the one process-start site — of a page this
    /// probe writes to the temp folder, the file checked for <c>%PDF</c>. Without a browser it passes and says so. Both files go.
    /// Since 2026-10-11 it also fails a browser run outside the app's job or one that leaves the profile folder held (<see cref="BrowserLeftovers"/>).
    /// </summary>
    public static SmokeCheck ProbePdfBrowser()
    {
        const string name = "pdf:browser";
        string folder = ProbeProfileFolder();
        var browser = new HeadlessBrowser(folder);
        if (browser.Locate("") is not { } executable)
        {
            return new SmokeCheck(name, true, "skipped: no Edge, Chrome or Brave");
        }

        string stem = Path.Combine(Path.GetTempPath(), "NeonSidekick.smoke." + Guid.NewGuid().ToString("N"));
        string html = stem + ".html";
        string pdf = stem + ".pdf";
        try
        {
            File.WriteAllText(html, PdfHtml.Document("smoke", PdfHtml.Markdown("# Smoke\n\nOne **page**.", _ => null), PdfPaperSize.Letter, landscape: false));
            var made = browser.PrintToPdfAsync(executable, new Uri(html), pdf, PrintReach.Nothing, CancellationToken.None).GetAwaiter().GetResult();
            if (!made.Ok)
            {
                return new SmokeCheck(name, false, PdfText.BrowserFailed(PdfText.BrowserName(executable), made.Detail));
            }

            long bytes = new FileInfo(pdf).Length;
            return BrowserLeftovers(made.InJob, folder) is { } wrong
                ? new SmokeCheck(name, false, wrong)
                : new SmokeCheck(name, true, $"{PdfText.BrowserName(executable)} printed a page to PDF ({bytes} bytes){LeftNothing}");
        }
        catch (Exception ex)
        {
            return new SmokeCheck(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Files.WorkingDirectory.DeleteQuietly(html);
            Files.WorkingDirectory.DeleteQuietly(pdf);
            Files.WorkingDirectory.DeleteDirectoryQuietly(folder);
        }
    }

    /// <summary>
    /// <c>web:devtools</c> (2026-10-11): where Edge, Chrome or Brave is installed, a real page read through
    /// <see cref="HeadlessBrowser.DumpDomAsync"/> over the DevTools protocol — the WebSocket, the port file, the settle and the clean
    /// close, none of which <c>dotnet test</c> runs — of a page this probe writes whose script rewrites it after 2 s of real time
    /// (the old <c>--dump-dom</c> read it at load), saying what it claims to be: the installed browser's user agent, not
    /// <c>HeadlessChrome</c>, and <c>navigator.webdriver</c> false. Without a browser it passes and says so. The page goes.
    /// Later that day it also fails a browser run outside the app's job or one that leaves the profile folder held (<see cref="BrowserLeftovers"/>).
    /// </summary>
    public static SmokeCheck ProbeWebDevTools()
    {
        const string name = "web:devtools";
        string folder = ProbeProfileFolder();
        var browser = new HeadlessBrowser(folder);
        if (browser.Locate("") is not { } executable)
        {
            return new SmokeCheck(name, true, "skipped: no Edge, Chrome or Brave");
        }

        string html = Path.Combine(Path.GetTempPath(), "NeonSidekick.smoke." + Guid.NewGuid().ToString("N") + ".html");
        try
        {
            File.WriteAllText(html, "<html><body><p id=\"s\">early</p><script>setTimeout(function () { document.getElementById('s').textContent = 'late ' + navigator.userAgent + ' webdriver=' + navigator.webdriver; }, 2000);</script></body></html>");
            var dump = browser.DumpDomAsync(executable, new Uri(html), CancellationToken.None).GetAwaiter().GetResult();
            string page = dump.Html ?? "";
            string? wrong =
                dump.Html is null ? dump.Detail
                : !page.Contains("late ", StringComparison.Ordinal) ? "the page was read before its script ran"
                : page.Contains("HeadlessChrome", StringComparison.Ordinal) ? "the browser still says HeadlessChrome"
                : !page.Contains(BrowserIdentity.For(executable).UserAgent, StringComparison.Ordinal) ? "the browser did not take the user agent"
                : !page.Contains("webdriver=false", StringComparison.Ordinal) ? "navigator.webdriver is not false"
                : BrowserLeftovers(dump.InJob, folder);
            return new SmokeCheck(name, wrong is null, wrong ?? $"{PdfText.BrowserName(executable)} ran a page in real time as {BrowserIdentity.For(executable).BrandName} {BrowserIdentity.For(executable).Major}{LeftNothing}");
        }
        catch (Exception ex)
        {
            return new SmokeCheck(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Files.WorkingDirectory.DeleteQuietly(html);
            Files.WorkingDirectory.DeleteDirectoryQuietly(folder);
        }
    }

    /// <summary>
    /// <c>pdf:edit</c> (2026-10-14, the PDF plan's phase 4b and gap 87): where Edge, Chrome or Brave is installed, pdf-lib out of the
    /// binary's resources, evaluated into the PDF reads' network-less page: page 1 of <c>pdf:cmap</c>'s two-page PDF taken out into a new
    /// PDF, that one turned 90°, and the result read back with pdf.js — one page, its text, drawn wider than tall — none of which
    /// <c>dotnet test</c> proves on the published exe (a trimmed or missing resource, the evaluate's object tail, the chunked read back).
    /// Without a browser it passes and says so. The folders go.
    /// </summary>
    public static SmokeCheck ProbePdfEdit()
    {
        const string name = "pdf:edit";
        string folder = ProbeProfileFolder();
        var browser = new HeadlessBrowser(folder);
        if (browser.Locate("") is not { } executable)
        {
            return new SmokeCheck(name, true, "skipped: no Edge, Chrome or Brave");
        }

        try
        {
            var extracted = browser.EditPdfAsync(executable, new PdfEditJob(PdfEditOp.Extract, [CMapProbePdf()], _ => ([1], null)), CancellationToken.None).GetAwaiter().GetResult();
            var turned = extracted.Output is { } one
                ? browser.EditPdfAsync(executable, new PdfEditJob(PdfEditOp.Rotate, [one], count => ([.. Enumerable.Range(1, count)], null), Angle: 90), CancellationToken.None).GetAwaiter().GetResult()
                : extracted;
            var read = turned.Output is { } pdf
                ? browser.ReadPdfAsync(executable, pdf, new PdfReadOptions(MaxSide: 400), CancellationToken.None).GetAwaiter().GetResult()
                : null;
            var page = read?.Pages.FirstOrDefault();
            string wrong =
                BrowserPdfEditor.LibraryText() is null ? "this build carries no pdf-lib"
                : !extracted.Ok ? "the extract failed: " + extracted.Detail
                : !turned.Ok ? "the turn failed: " + turned.Detail
                : read is not { Ok: true } ? "the result could not be read: " + read?.Detail
                : read.PageCount != 1 || page is null ? $"the result has {read.PageCount} pages, not 1"
                : page.Text != "front" ? "the result's text is '" + page.Text + "', not page 1's"
                : page.Width <= page.Height ? $"the page was not turned ({page.Width}x{page.Height})"
                : BrowserLeftovers(turned.InJob, HeadlessBrowser.PdfFolderFor(folder)) ?? "";
            return new SmokeCheck(name, wrong.Length == 0, wrong.Length > 0 ? wrong : $"{PdfText.BrowserName(executable)} took page 1 of 2 out with pdf-lib, turned it 90° and read it back ({page!.Width}x{page.Height}){LeftNothing}");
        }
        catch (Exception ex)
        {
            return new SmokeCheck(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Files.WorkingDirectory.DeleteDirectoryQuietly(folder);
            Files.WorkingDirectory.DeleteDirectoryQuietly(HeadlessBrowser.PdfFolderFor(folder));
        }
    }

    /// <summary>What a passing browser probe adds on Windows, where the job and the held folder are checked.</summary>
    private static string LeftNothing => OperatingSystem.IsWindows() ? "; in the app's job, nothing left holding its profile folder" : "";

    /// <summary>
    /// A profile folder of the probe's own (code review, 2026-10-11: on the app's shared folder, a running copy of the app reading a
    /// page read as the probe's browser left behind), under the temp folder beside the app's; removed after
    /// (<see cref="Files.WorkingDirectory.DeleteDirectoryQuietly"/>: a temp folder left behind is harmless, and a browser still in it
    /// was already reported).
    /// </summary>
    private static string ProbeProfileFolder() => Path.Combine(Path.GetTempPath(), "NeonSidekick", "smoke-browser-" + Guid.NewGuid().ToString("N"));

    /// <summary>How long a browser probe gives its browser to let go of the profile folder after the run.</summary>
    private static readonly TimeSpan LetGo = TimeSpan.FromSeconds(3);

    /// <summary>
    /// What a browser probe's run left wrong on Windows (2026-10-11, an Edge that relaunched itself outlived the app and held the
    /// profile folder, so every later run failed): the browser started outside the app's kill-on-close job (<paramref name="inJob"/>,
    /// the run's own answer), or a browser still holding the probe's own <paramref name="folder"/> <see cref="LetGo"/> after the run
    /// (<see cref="HeadlessBrowser.ProfileInUse"/>, one look at a time inside that budget and its full looks once at the end — code
    /// review, later that day: thirty full looks of up to 100 ms each, 100 ms apart, ran to 6 s); null when neither, and off Windows,
    /// where there is no job.
    /// </summary>
    private static string? BrowserLeftovers(bool inJob, string folder)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        if (!inJob)
        {
            return "the browser ran outside the app's job (the log's EmbeddedLlm lines say why)";
        }

        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (waited.Elapsed < LetGo && HeadlessBrowser.ProfileInUse(folder, looks: 1))
        {
            Thread.Sleep(100);
        }

        return HeadlessBrowser.ProfileInUse(folder) ? "a browser was left running, holding " + folder : null;
    }
}
