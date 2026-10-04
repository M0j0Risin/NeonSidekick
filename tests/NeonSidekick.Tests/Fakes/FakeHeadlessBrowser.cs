using System.Text;
using NeonSidekick.Web;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// The headless-browser seam without a browser: <see cref="Executable"/> is what <see cref="Locate"/>
/// finds (null = none installed), <see cref="Html"/> what a run dumps (null = the run fails with
/// <see cref="Failure"/>), and every run is recorded. A print to PDF (2026-10-03) writes <see cref="PdfBytes"/> to its output
/// path, or fails with <see cref="PdfFailure"/> when that is set.
/// </summary>
public sealed class FakeHeadlessBrowser : IHeadlessBrowser
{
    public string? Executable { get; set; } = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";

    public string? Html { get; set; }

    public string Failure { get; set; } = "it printed nothing";

    public List<Uri> Runs { get; } = new();

    public List<string> LocateCalls { get; } = new();

    public List<(Uri Page, string Output)> PdfRuns { get; } = new();

    /// <summary>The HTML of each page the app wrote, read at the run (the converter deletes it after).</summary>
    public List<string> PdfPages { get; } = new();

    public string? PdfFailure { get; set; }

    public byte[] PdfBytes { get; set; } = Encoding.ASCII.GetBytes("%PDF-1.7 fake browser");

    public string? Locate(string configuredPath)
    {
        LocateCalls.Add(configuredPath);
        return configuredPath.Length > 0 ? configuredPath : Executable;
    }

    public Task<BrowserDump> DumpDomAsync(string executable, Uri url, CancellationToken cancellationToken)
    {
        Runs.Add(url);
        return Task.FromResult(Html is null ? new BrowserDump(null, Failure) : new BrowserDump(Html, ""));
    }

    public Task<BrowserPdf> PrintToPdfAsync(string executable, Uri page, string outputPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PdfRuns.Add((page, outputPath));
        if (page.IsFile && File.Exists(page.LocalPath))
        {
            PdfPages.Add(File.ReadAllText(page.LocalPath));
        }

        if (PdfFailure is not null)
        {
            return Task.FromResult(new BrowserPdf(false, PdfFailure));
        }

        File.WriteAllBytes(outputPath, PdfBytes);
        return Task.FromResult(new BrowserPdf(true, ""));
    }
}
