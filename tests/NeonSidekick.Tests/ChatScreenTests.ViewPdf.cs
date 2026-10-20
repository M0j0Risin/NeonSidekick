using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Viewer;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/view</c> of a PDF (2026-10-14, the PDF plan's phase 4a): staged and shown in the Browser window, even where there is no picture viewer
/// (gap 35); in its own program where the window can't take it; refused with <c>--chat</c> or <c>--thumbs</c>.
/// </summary>
public partial class ChatScreenTests
{
    private string WorkingPdf(string name)
    {
        string files = Path.Combine(_settings.ProfileDirectory, WorkingDirectory.DefaultFolderName);
        Directory.CreateDirectory(files);
        string path = Path.Combine(files, name);
        File.WriteAllText(path, "%PDF-1.7\n%%EOF\n");
        return path;
    }

    [Fact]
    public async Task ViewAPdf_StagesACopy_AndOpensItInTheBrowserWindow()
    {
        _settings.Update(d => d.TtsOutput = false);
        var window = new FakeBrowserWindow();
        _browserWindow = window;
        string file = WorkingPdf("report.pdf");
        PushLine("/view report.pdf");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains(ViewerText.PdfOpened("report.pdf"), output);
        var url = Assert.Single(window.Browses)!;
        Assert.True(PdfView.IsOurs(url.AbsoluteUri));
        Assert.EndsWith("/report.pdf", url.AbsoluteUri, StringComparison.Ordinal);
        // The fake's visit ends at once, and a visit's end (the window closed) clears the copies: none is left behind.
        Assert.Null(PdfView.OriginalOf(url.AbsoluteUri));
        Assert.False(File.Exists(Path.Combine(PdfView.Folder, url.Segments[1].Trim('/'), "report.pdf")));
        Assert.True(File.Exists(file));
        Assert.Empty(_openedFiles);
    }

    /// <summary>
    /// A second <c>/view</c> while the window is open keeps the first's copy and its own until the window closes, then both go (2026-10-20,
    /// review: the second open was never followed, so a copy staged as the window closed was cleared under a new window or never cleared).
    /// </summary>
    [Fact]
    public async Task ViewTwoPdfs_InOneVisit_KeepsBothCopies_UntilTheWindowCloses()
    {
        _settings.Update(d => d.TtsOutput = false);
        var window = new FakeBrowserWindow { HoldVisit = true };
        _browserWindow = window;
        WorkingPdf("a.pdf");
        WorkingPdf("b.pdf");
        bool bothWhileOpen = false;
        StepsWhenIdle(
            Line("/view a.pdf"),
            Line("/view b.pdf"),
            input =>
            {
                bothWhileOpen = window.Browses.Count == 2 && window.Browses.All(u => PdfView.StagedFileOf(u!.AbsoluteUri) is not null);
                window.EndVisit();
                PushLine(input, "/exit");
            });

        await RunAsync();

        Assert.True(bothWhileOpen);
        for (int i = 0; i < 100 && window.Browses.Any(u => PdfView.OriginalOf(u!.AbsoluteUri) is not null); i++)
        {
            await Task.Delay(50);
        }

        Assert.All(window.Browses, u => Assert.Null(PdfView.OriginalOf(u!.AbsoluteUri)));
    }

    [Fact]
    public async Task ViewAPdf_GoesToItsOwnProgram_WhenTheWindowCantTakeIt()
    {
        _settings.Update(d => d.TtsOutput = false);
        _browserWindow = new FakeBrowserWindow { UnavailableReason = "a bot check is waiting in it" };
        string file = WorkingPdf("report.pdf");
        PushLine("/view report.pdf");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal(file, Assert.Single(_openedFiles));
        Assert.Contains(ViewerText.PdfInDefaultApp("report.pdf", "a bot check is waiting in it"), output);
    }

    [Fact]
    public async Task ViewAPdf_WithNoWindowAtAll_GoesToItsOwnProgram_NotTheTranscript()
    {
        _settings.Update(d => d.TtsOutput = false);
        _browserWindow = null;
        string file = WorkingPdf("report.pdf");
        PushLine("/view report.pdf");
        PushLine("/view report.pdf --thumbs");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal(file, Assert.Single(_openedFiles));
        Assert.Contains("  ✗ " + ChatScreen.ViewPdfFlagsError, output);
        Assert.DoesNotContain(FileText.NotAnImage("report.pdf"), output);
    }
}
