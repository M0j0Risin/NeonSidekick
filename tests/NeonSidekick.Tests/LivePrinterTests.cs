using NeonSidekick.App;
using NeonSidekick.Printing;
using NeonSidekick.UI.Markdown;

namespace NeonSidekick.Tests;

/// <summary>
/// Skips unless <c>NEONSIDEKICK_TEST_PRINTER</c> names a CUPS queue on this Mac (2026-10-08). A local gate, never CI's: the jobs
/// go to that queue for real, but held (<c>job-hold-until=indefinite</c>) and cancelled by the test, so nothing reaches paper.
/// </summary>
public sealed class LivePrinterFactAttribute : FactAttribute
{
    public const string Variable = "NEONSIDEKICK_TEST_PRINTER";

    public static string? Printer => Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } name ? name : null;

    public LivePrinterFactAttribute()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Skip = MacFactAttribute.SkipReason;
        }
        else if (Printer is null)
        {
            Skip = $"{Variable} names no CUPS queue to hold a job on.";
        }
    }
}

/// <summary>
/// <see cref="MacPrintSpooler"/> against a real queue (2026-10-08): a drawn job and a PDF sent as it is, each held and found
/// held, then cancelled; and a print cancelled once CUPS has made its job, which must leave no job behind.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public sealed class LivePrinterTests
{
    private static readonly KeyValuePair<string, string>[] Hold = [new("job-hold-until", "indefinite")];

    private static PrintJob Job(MacPrintSpooler spooler, string printer)
    {
        using var surface = spooler.Open(printer, landscape: false, out string error) ?? throw new InvalidOperationException(error);
        var pages = PrintLayout.Markdown(new MarkdigParser().Parse("# Held\n\nA NeonSidekick test job, held and cancelled."), surface.Page, surface, 10);
        return new PrintJob(printer, "NeonSidekick held test", PrintLayout.WithHeaders(pages, "held.md", "", surface.Page, surface));
    }

    [LivePrinterFact]
    public void ADrawnJob_IsHeld_ThenCancelled()
    {
        string printer = LivePrinterFactAttribute.Printer!;
        int id = 0;
        var spooler = new MacPrintSpooler(Hold, created => id = created);
        try
        {
            Assert.Null(spooler.Print(Job(spooler, printer), CancellationToken.None));

            Assert.NotEqual(0, id);
            Assert.Contains((id, CupsNative.JobHeld), MacPrintSpooler.ActiveJobs(printer));
        }
        finally
        {
            if (id != 0)
            {
                Assert.True(MacPrintSpooler.Cancel(printer, id));
            }
        }

        Assert.DoesNotContain(MacPrintSpooler.ActiveJobs(printer), j => j.Id == id);
    }

    [LivePrinterFact]
    public void APdf_IsHeld_WithItsOptions_ThenCancelled()
    {
        string printer = LivePrinterFactAttribute.Printer!;
        string path = Path.Combine(Path.GetTempPath(), "NeonSidekick.held." + Guid.NewGuid().ToString("N") + ".pdf");
        int id = 0;
        var spooler = new MacPrintSpooler(Hold, created => id = created);
        try
        {
            using (var surface = new MacPdfDocument(MacPrintRules.ForRegion("US"), landscape: false))
            {
                var job = Job(spooler, printer);
                File.WriteAllBytes(path, surface.Render(job.Pages, 3, CancellationToken.None));
            }

            Assert.Equal(3, spooler.ShellPageCount(path));
            Assert.Null(spooler.ShellPrint(path, printer, 2, [1, 3], CancellationToken.None));

            Assert.Contains((id, CupsNative.JobHeld), MacPrintSpooler.ActiveJobs(printer));
        }
        finally
        {
            if (id != 0)
            {
                Assert.True(MacPrintSpooler.Cancel(printer, id));
            }

            File.Delete(path);
        }
    }

    [LivePrinterFact]
    public void APrintCancelled_OnceItsJobIsMade_LeavesNoJob()
    {
        string printer = LivePrinterFactAttribute.Printer!;
        using var cancel = new CancellationTokenSource();
        int id = 0;
        var spooler = new MacPrintSpooler(Hold, created =>
        {
            id = created;
            cancel.Cancel();
        });

        Assert.Throws<OperationCanceledException>(() => spooler.Print(Job(spooler, printer), cancel.Token));

        Assert.NotEqual(0, id);
        Assert.DoesNotContain(MacPrintSpooler.ActiveJobs(printer), j => j.Id == id);
    }
}
