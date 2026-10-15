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
