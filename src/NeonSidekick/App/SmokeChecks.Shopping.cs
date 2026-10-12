using NeonSidekick.Pdf;
using NeonSidekick.Printing;
using NeonSidekick.Web;

namespace NeonSidekick.App;

public static partial class SmokeChecks
{
    /// <summary>
    /// <c>shopping:pdf-read</c> (2026-10-11, the shopping list's reference documents): where Edge, Chrome or Brave is installed, a page
    /// this probe writes printed to PDF by the browser (<see cref="HeadlessBrowser.PrintToPdfAsync"/>), then read back through
    /// <see cref="HeadlessBrowser.ReadPdfAsync"/> — pdf.js out of the binary's embedded resources, imported into the blank page as
    /// <c>data:</c> modules, the page's text and its PNG over DevTools, none of which <c>dotnet test</c> proves on the published exe.
    /// Without a browser it passes and says so. Fails too on a run outside the app's job or a profile folder left held
    /// (<see cref="BrowserLeftovers"/>). The files go.
    /// </summary>
    public static SmokeCheck ProbeShoppingPdfRead()
    {
        const string name = "shopping:pdf-read";
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
            File.WriteAllText(html, PdfHtml.Document("smoke", PdfHtml.Markdown("# Store guide\n\nEggs aisle seven", _ => null), PdfPaperSize.Letter, landscape: false));
            var made = browser.PrintToPdfAsync(executable, new Uri(html), pdf, CancellationToken.None).GetAwaiter().GetResult();
            if (!made.Ok)
            {
                return new SmokeCheck(name, false, "could not make the PDF to read: " + made.Detail);
            }

            var read = browser.ReadPdfAsync(executable, File.ReadAllBytes(pdf), new PdfReadOptions(MaxPages: 1, MaxSide: 600), CancellationToken.None).GetAwaiter().GetResult();
            var page = read.Pages.FirstOrDefault();
            string? wrong =
                !read.Ok ? read.Detail
                : !page!.Text.Contains("Eggs aisle seven", StringComparison.Ordinal) ? "the page's text was not read: " + page.Text
                : page.Png is not { Length: > 8 } png || png[1] != (byte)'P' ? "the page was not drawn"
                : BrowserLeftovers(read.InJob, folder);
            return new SmokeCheck(name, wrong is null, wrong ?? $"{PdfText.BrowserName(executable)} read a PDF's text and drew its page ({page!.Width}x{page.Height}){LeftNothing}");
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
}