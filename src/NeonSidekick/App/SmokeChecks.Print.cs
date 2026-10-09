using NeonSidekick.Printing;

namespace NeonSidekick.App;

public static partial class SmokeChecks
{
    /// <summary>The print-to-file printer every Windows 10 and 11 installs, which <see cref="ProbePrintSpooler"/> prints to.</summary>
    public const string PrintToPdfPrinter = PrintText.PrintToPdfPrinter;

    /// <summary>
    /// <c>print:spooler</c> (2026-09-28): the print layer's imports in the published binary — the printers listed (winspool),
    /// and, where <see cref="PrintToPdfPrinter"/> is installed, a real job through it: the driver's DEVMODE turned to landscape,
    /// an information context measured, a markdown page with a picture laid out, the document started with an output file so no
    /// dialog asks, drawn and ended, and the PDF looked for on disk (the spooler writes it a moment later). The file is deleted.
    /// Without that printer it passes on the listing alone and says so. Nothing reaches paper.
    /// </summary>
    public static SmokeCheck ProbePrintSpooler()
    {
        const string name = "print:spooler";
        if (!OperatingSystem.IsWindows())
        {
            return new SmokeCheck(name, true, "skipped: not Windows");
        }

        string path = Path.Combine(Path.GetTempPath(), "NeonSidekick.smoke." + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            var spooler = new WindowsPrintSpooler();
            var printers = spooler.Printers();
            if (printers.FirstOrDefault(p => string.Equals(p.Name, PrintToPdfPrinter, StringComparison.OrdinalIgnoreCase)) is not { } pdf)
            {
                return new SmokeCheck(name, true, $"{PrintText.Count(printers.Count, "printer")} listed; no {PrintToPdfPrinter} to print to");
            }

            using (var surface = spooler.Open(pdf.Name, landscape: true, out string openError))
            {
                if (surface is null)
                {
                    return new SmokeCheck(name, false, openError);
                }

                if (surface.Page.Width <= surface.Page.Height)
                {
                    return new SmokeCheck(name, false, $"landscape was not applied: the page is {surface.Page.Width:0}x{surface.Page.Height:0} pt");
                }

                double width = surface.Width("Smoke", new PrintFont(PrintFace.Sans, 10));
                if (width <= 0)
                {
                    return new SmokeCheck(name, false, "the printer's font measured nothing");
                }

                var document = new UI.Markdown.MarkdigParser().Parse("# Smoke\n\nOne **page** with `code` and a picture.\n\n- a\n- b");
                var pages = PrintLayout.Markdown(document, surface.Page, surface, 10).ToList();
                if (Viewer.ViewerImage.Decode(SolidBmp(8, 2), "smoke.bmp") is { } bitmap)
                {
                    pages.AddRange(PrintLayout.Picture(bitmap, surface.Page).Select(p => p with { Number = pages.Count + 1 }));
                }

                var sheets = PrintLayout.WithHeaders(pages, "smoke", "", surface.Page, surface);
                string? error = spooler.Print(new PrintJob(pdf.Name, "NeonSidekick smoke", sheets, Landscape: true, OutputFile: path), CancellationToken.None);
                if (error is not null)
                {
                    return new SmokeCheck(name, false, error);
                }
            }

            // The spooler writes the file after EndDoc returns: wait for it, up to ten seconds (PrintToFile, 2026-10-03).
            var state = PrintToFile.WaitForPdf(path, TimeSpan.FromSeconds(10));
            if (state is PdfFileState.Missing)
            {
                return new SmokeCheck(name, false, $"{PrintToPdfPrinter} took the job but wrote no file");
            }

            bool isPdf = state == PdfFileState.Ready;
            return new SmokeCheck(name, isPdf, isPdf
                ? $"{PrintText.Count(printers.Count, "printer")} listed; two landscape pages printed to {PrintToPdfPrinter}"
                : state == PdfFileState.Unfinished ? $"{PrintToPdfPrinter} was still writing the file after ten seconds" : $"{PrintToPdfPrinter} wrote a file that is not a PDF");
        }
        catch (Exception ex)
        {
            return new SmokeCheck(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Still held by the spooler: the temp folder's to clean.
            }
        }
    }

    /// <summary>
    /// <c>print:cups</c> (2026-10-08, printing on a Mac): libcups bound by name (it lives in the dyld shared cache), the printers
    /// listed (none is fine), and a landscape job — a markdown page and a picture — drawn by <see cref="MacPdfDocument"/> to a PDF
    /// in the temp folder: through the spooler's print-to-file path on the default printer's paper when there is one, else on
    /// the region's paper. The PDF is read back (two portrait sheets, the landscape page turned onto them, its words found by
    /// PDFKit) and deleted. Nothing is submitted, so nothing reaches paper.
    /// </summary>
    public static SmokeCheck ProbeMacPrint()
    {
        const string name = "print:cups";
        if (!OperatingSystem.IsMacOS())
        {
            return new SmokeCheck(name, true, "skipped: not macOS");
        }

        string path = Path.Combine(Path.GetTempPath(), "NeonSidekick.smoke." + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            if (!MacPrintSpooler.Available)
            {
                return new SmokeCheck(name, false, MacPrintRules.CupsLibrary + " did not load");
            }

            var spooler = new MacPrintSpooler();
            var printers = spooler.Printers();
            var fallback = printers.FirstOrDefault(p => p.IsDefault);
            string error = "";
            using var surface = fallback is null ? new MacPdfDocument(MacPrintRules.ForRegion(MacPdfDocument.Region()), landscape: true) : spooler.Open(fallback.Name, landscape: true, out error);
            if (surface is null)
            {
                return new SmokeCheck(name, false, error);
            }

            if (surface.Page.Width <= surface.Page.Height)
            {
                return new SmokeCheck(name, false, $"landscape was not applied: the page is {surface.Page.Width:0}x{surface.Page.Height:0} pt");
            }

            if (surface.Width("Smoke", new PrintFont(PrintFace.Sans, 10)) <= 0)
            {
                return new SmokeCheck(name, false, "CoreText measured nothing");
            }

            var document = new UI.Markdown.MarkdigParser().Parse("# Smoke\n\nOne **page** with `code` and a picture.\n\n- a\n- b");
            var pages = PrintLayout.Markdown(document, surface.Page, surface, 10).ToList();
            if (Viewer.ViewerImage.Decode(SolidBmp(8, 2), "smoke.bmp") is { } bitmap)
            {
                pages.AddRange(PrintLayout.Picture(bitmap, surface.Page).Select(p => p with { Number = pages.Count + 1 }));
            }

            var sheets = PrintLayout.WithHeaders(pages, "smoke", "", surface.Page, surface);
            if (fallback is not null)
            {
                string? printError = spooler.Print(new PrintJob(fallback.Name, "NeonSidekick smoke", sheets, Landscape: true, OutputFile: path), CancellationToken.None);
                if (printError is not null)
                {
                    return new SmokeCheck(name, false, printError);
                }
            }
            else
            {
                File.WriteAllBytes(path, ((MacPdfDocument)surface).Render(sheets, 1, CancellationToken.None));
            }

            byte[] pdf = File.ReadAllBytes(path);
            var boxes = MacPdfDocument.MediaBoxes(pdf);
            if (boxes.Count != sheets.Count || boxes.Any(b => b.Width >= b.Height))
            {
                return new SmokeCheck(name, false, $"the PDF has {boxes.Count} page(s), not {sheets.Count} portrait sheets");
            }

            if (MacPdfDocument.Text(pdf) is not { } text || !text.Contains("Smoke", StringComparison.Ordinal))
            {
                return new SmokeCheck(name, false, "the PDF's text was not found in it");
            }

            string paper = fallback is null ? "the region's paper" : $"{fallback.Name}'s paper";
            return new SmokeCheck(name, true, $"{PrintText.Count(printers.Count, "printer")} listed; {PrintText.Count(boxes.Count, "landscape page")} drawn to a PDF on {paper} ({boxes[0].Width:0}x{boxes[0].Height:0} pt), nothing sent");
        }
        catch (Exception ex)
        {
            return new SmokeCheck(name, false, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
