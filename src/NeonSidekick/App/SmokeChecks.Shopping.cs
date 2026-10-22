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
            var made = browser.PrintToPdfAsync(executable, new Uri(html), pdf, PrintReach.Nothing, CancellationToken.None).GetAwaiter().GetResult();
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
                : BrowserLeftovers(read.InJob, HeadlessBrowser.PdfFolderFor(folder));   // the PDF reads' own folder since 2026-10-14
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
            Files.WorkingDirectory.DeleteDirectoryQuietly(HeadlessBrowser.PdfFolderFor(folder));
        }
    }

    /// <summary>
    /// <c>pdf:cmap</c> (2026-10-14, the PDF plan's gaps 3, 44 and 87): where Edge, Chrome or Brave is installed, a two-page PDF this probe
    /// writes by hand — Helvetica on page 1, Japanese in a non-embedded Adobe-Japan1 font with the predefined CMap <c>UniJIS-UCS2-H</c> on
    /// page 2 — read with only page 2 asked for: the page list, the packed CMaps out of the binary's resources (a build without them reads
    /// the page blank), and the reads' own network-less profile, none of which <c>dotnet test</c> proves on the published exe. Without a
    /// browser it passes and says so. The folders go.
    /// </summary>
    public static SmokeCheck ProbePdfCMap()
    {
        const string name = "pdf:cmap";
        string folder = ProbeProfileFolder();
        var browser = new HeadlessBrowser(folder);
        if (browser.Locate("") is not { } executable)
        {
            return new SmokeCheck(name, true, "skipped: no Edge, Chrome or Brave");
        }

        try
        {
            var read = browser.ReadPdfAsync(executable, CMapProbePdf(), new PdfReadOptions(Pictures: false, Pages: [2]), CancellationToken.None).GetAwaiter().GetResult();
            string wrong =
                BrowserPdfReader.CMapCount == 0 ? "this build carries no packed CMaps"
                : !read.Ok ? read.Detail
                : read.Pages is not [{ Number: 2 } page] ? "not page 2 alone: " + string.Join(",", read.Pages.Select(p => p.Number))
                : page.Text != CMapProbeText ? "the Japanese text was not read: '" + page.Text + "'"
                : BrowserLeftovers(read.InJob, HeadlessBrowser.PdfFolderFor(folder)) ?? "";
            return new SmokeCheck(name, wrong.Length == 0, wrong.Length > 0 ? wrong : $"{PdfText.BrowserName(executable)} read page 2 of 2, Japanese through {BrowserPdfReader.CMapCount} packed CMaps{LeftNothing}");
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

    private const string CMapProbeText = "日本語";

    /// <summary><see cref="ProbePdfCMap"/>'s PDF, written by hand (offsets counted): page 1 Helvetica, page 2 the Japanese.</summary>
    private static byte[] CMapProbePdf()
    {
        static string Stream(string content) => "<< /Length " + content.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + " >>\nstream\n" + content + "\nendstream";
        string hex = string.Concat(CMapProbeText.Select(c => ((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture)));
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 7 0 R >> >> /Contents 4 0 R >>",
            Stream("BT /F1 18 Tf 72 720 Td (front) Tj ET"),
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F2 8 0 R >> >> /Contents 6 0 R >>",
            Stream("BT /F2 24 Tf 72 720 Td <" + hex + "> Tj ET"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /Font /Subtype /Type0 /BaseFont /KozMinPr6N-Regular /Encoding /UniJIS-UCS2-H /DescendantFonts [9 0 R] >>",
            "<< /Type /Font /Subtype /CIDFontType0 /BaseFont /KozMinPr6N-Regular /CIDSystemInfo << /Registry (Adobe) /Ordering (Japan1) /Supplement 6 >> /FontDescriptor 10 0 R /DW 1000 >>",
            "<< /Type /FontDescriptor /FontName /KozMinPr6N-Regular /Flags 4 /FontBBox [0 -200 1000 900] /ItalicAngle 0 /Ascent 880 /Descent -120 /CapHeight 700 /StemV 80 >>",
        ];
        static string N(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var pdf = new System.Text.StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(N(i + 1)).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        int xref = pdf.Length;
        pdf.Append("xref\n0 ").Append(N(objects.Length + 1)).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets)
        {
            pdf.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        pdf.Append("trailer\n<< /Size ").Append(N(objects.Length + 1)).Append(" /Root 1 0 R >>\nstartxref\n").Append(N(xref)).Append("\n%%EOF\n");
        return System.Text.Encoding.ASCII.GetBytes(pdf.ToString());
    }
}