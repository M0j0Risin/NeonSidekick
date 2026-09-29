using NeonSidekick.Printing;

namespace NeonSidekick.App;

public static partial class SmokeChecks
{
    /// <summary>The print-to-file printer every Windows 10 and 11 installs, which <see cref="ProbePrintSpooler"/> prints to.</summary>
    public const string PrintToPdfPrinter = "Microsoft Print to PDF";

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

            // The spooler writes the file after EndDoc returns: wait for it, up to ten seconds.
            for (int i = 0; i < 100 && !(File.Exists(path) && new FileInfo(path).Length > 0); i++)
            {
                Thread.Sleep(100);
            }

            if (!File.Exists(path))
            {
                return new SmokeCheck(name, false, $"{PrintToPdfPrinter} took the job but wrote no file");
            }

            byte[] head = new byte[4];
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                stream.ReadAtLeast(head, 4, throwOnEndOfStream: false);
            }

            bool isPdf = head[0] == (byte)'%' && head[1] == (byte)'P' && head[2] == (byte)'D' && head[3] == (byte)'F';
            return new SmokeCheck(name, isPdf, isPdf
                ? $"{PrintText.Count(printers.Count, "printer")} listed; two landscape pages printed to {PrintToPdfPrinter}"
                : $"{PrintToPdfPrinter} wrote a file that is not a PDF");
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
}
