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
    /// <summary>Edge's place on this OS (2026-10-06, the macOS build: a Windows path reads as one name there, so its <c>msedge</c> was lost).</summary>
    public string? Executable { get; set; } = OperatingSystem.IsWindows()
        ? @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
        : "/Applications/Microsoft Edge.app/Contents/MacOS/msedge";

    public string? Html { get; set; }

    /// <summary>The cookies a run with <see cref="Html"/> hands back (2026-10-11), and the address it says the page ended on.</summary>
    public List<BrowserCookie> Cookies { get; } = new();

    public string FinalUrl { get; set; } = "";

    /// <summary>What <see cref="Identify"/> answers (2026-10-11): never the machine's own browser, so a header pin holds anywhere.</summary>
    public BrowserIdentity Identity { get; set; } = BrowserIdentity.Fallback;

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

    public BrowserIdentity Identify(string configuredPath) => Identity;

    public Task<BrowserDump> DumpDomAsync(string executable, Uri url, CancellationToken cancellationToken)
    {
        Runs.Add(url);
        return Task.FromResult(Html is null ? new BrowserDump(null, Failure) : new BrowserDump(Html, "", FinalUrl, Cookies.ToList()));
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

    /// <summary>
    /// What a PDF read answers (2026-10-11, the shopping list's reference documents): <see cref="PdfReadPages"/>'s pages, the count of them
    /// unless <see cref="PdfReadCount"/> says more; every read's size recorded. Since 2026-10-14 (the PDF plan) it honors the options as the
    /// real reader does — <see cref="PdfReadOptions.Pages"/> and <see cref="PdfReadOptions.PicturePages"/> (a page drawn keeps the picture it
    /// was given, a page not given is the tiny PNG; one not drawn loses it), the outline and labels when asked (<see cref="PdfOutline"/>, <see cref="PdfLabels"/>),
    /// a <see cref="PdfLocked"/> file — records each read's options in <see cref="PdfReadOptionsSeen"/>, and stops after
    /// <see cref="PdfStopAfter"/> pages with <see cref="PdfStopDetail"/>, as a run that timed out keeps what it read.
    /// </summary>
    public List<PdfPageRead> PdfReadPages { get; } = new();

    public int? PdfReadCount { get; set; }

    public List<int> PdfReads { get; } = new();

    public List<PdfReadOptions> PdfReadOptionsSeen { get; } = new();

    public List<PdfOutlineItem> PdfOutline { get; } = new();

    public List<string> PdfLabels { get; } = new();

    public bool PdfLocked { get; set; }

    public int? PdfStopAfter { get; set; }

    public string PdfStopDetail { get; set; } = "stopped after 2 min";

    /// <summary>A one-pixel PNG, the picture a drawn page gets when <see cref="PdfReadPages"/> gives none.</summary>
    public static readonly byte[] TinyPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    public Task<PdfRead> ReadPdfAsync(string executable, byte[] pdf, PdfReadOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PdfReads.Add(pdf.Length);
        PdfReadOptionsSeen.Add(options);
        if (PdfLocked)
        {
            return Task.FromResult(new PdfRead(false, HeadlessBrowser.PdfLockedDetail, 0, []) { Locked = true });
        }

        int count = PdfReadCount ?? PdfReadPages.Count;
        var wanted = options.PagesOf(count);
        var pages = new List<PdfPageRead>();
        string detail = "";
        foreach (int number in wanted)
        {
            if (PdfStopAfter is { } stop && pages.Count >= stop)
            {
                detail = PdfStopDetail;
                break;
            }

            // A page the test gave is drawn as given (its Png may be null on purpose: a page with no picture); one it did not give is
            // blank text, and drawn as the tiny PNG.
            var given = PdfReadPages.FirstOrDefault(p => p.Number == number);
            var page = given ?? new PdfPageRead(number, "", TinyPng, 1, 1);
            pages.Add(options.Draws(number) ? page : page with { Png = null });
        }

        if (detail.Length == 0 && options.Pages is null && pages.Count < count)
        {
            detail = $"read the first {pages.Count} of {count} pages";
        }

        bool ok = pages.Count > 0 || (wanted.Count == 0 && count > 0);
        return Task.FromResult(new PdfRead(ok, ok ? detail : Failure, count, pages)
        {
            Outline = options.Outline ? PdfOutline.ToList() : null,
            Labels = options.Labels ? PdfLabels.ToList() : null,
        });
    }
}
