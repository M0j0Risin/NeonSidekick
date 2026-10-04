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
                : !body.Contains("tk-keyword", StringComparison.Ordinal) ? "the code was not coloured"
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
    /// </summary>
    public static SmokeCheck ProbePdfBrowser()
    {
        const string name = "pdf:browser";
        var browser = new HeadlessBrowser();
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
            var made = browser.PrintToPdfAsync(executable, new Uri(html), pdf, CancellationToken.None).GetAwaiter().GetResult();
            return made.Ok
                ? new SmokeCheck(name, true, $"{PdfText.BrowserName(executable)} printed a page to PDF ({new FileInfo(pdf).Length} bytes)")
                : new SmokeCheck(name, false, PdfText.BrowserFailed(PdfText.BrowserName(executable), made.Detail));
        }
        catch (Exception ex)
        {
            return new SmokeCheck(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Files.WorkingDirectory.DeleteQuietly(html);
            Files.WorkingDirectory.DeleteQuietly(pdf);
        }
    }
}
