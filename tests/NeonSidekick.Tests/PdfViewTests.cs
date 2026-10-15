using NeonSidekick.Viewer;

namespace NeonSidekick.Tests;

/// <summary>
/// A local PDF staged for the Browser window (2026-10-14, the PDF plan's phase 4a): its address, the copy and its original, the sweep of
/// folders whose process is gone, and the window's pure parts that keep the host out (the saved links, the open button's name).
/// </summary>
public sealed class PdfViewTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", "pdfview-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void TheAddress_EscapesTheName_AndIsOurs()
    {
        var url = PdfView.UrlOf("0123456789abcdef", "my report #2 100%.pdf");
        Assert.Equal("https://files.neonsidekick.example/0123456789abcdef/my%20report%20%232%20100%25.pdf", url.AbsoluteUri);
        Assert.True(PdfView.IsOurs(url.AbsoluteUri));
        Assert.True(PdfView.IsOurs("https://FILES.neonsidekick.example/x/y.pdf"));
        Assert.False(PdfView.IsOurs("http://files.neonsidekick.example/x/y.pdf"));
        Assert.False(PdfView.IsOurs("https://example.com/x.pdf"));
        Assert.False(PdfView.IsOurs(null));
    }

    [Fact]
    public void Stage_CopiesTheFile_AndRemembersItsOriginal_UntilCleared()
    {
        string source = Path.Combine(_dir, "src", "report.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "%PDF-1.7");
        string folder = Path.Combine(_dir, "stage", "1");

        var url = PdfView.Stage(source, folder);

        string random = url.Segments[1].Trim('/');
        Assert.Equal(16, random.Length);
        Assert.True(File.Exists(Path.Combine(folder, random, "report.pdf")));
        Assert.Equal(source, PdfView.OriginalOf(url.AbsoluteUri));
        Assert.Null(PdfView.OriginalOf("https://example.com/report.pdf"));

        PdfView.Clear(folder);
        Assert.False(Directory.Exists(folder));
        Assert.Null(PdfView.OriginalOf(url.AbsoluteUri));
    }

    /// <summary>The PDF plan's gap 63: a second copy of the app may be showing its own staged PDFs; only a gone process's folder is swept.</summary>
    [Fact]
    public void Sweep_TakesOnlyTheFoldersOfProcessesThatAreGone()
    {
        string root = Path.Combine(_dir, "root");
        foreach (string pid in new[] { "100", "200", "300", "notapid" })
        {
            Directory.CreateDirectory(Path.Combine(root, pid));
        }

        int gone = PdfView.Sweep(root, self: 100, alive: pid => pid == 200);

        Assert.Equal(1, gone);
        Assert.True(Directory.Exists(Path.Combine(root, "100")));      // ours
        Assert.True(Directory.Exists(Path.Combine(root, "200")));      // another copy, still running
        Assert.False(Directory.Exists(Path.Combine(root, "300")));     // gone
        Assert.True(Directory.Exists(Path.Combine(root, "notapid")));  // not one of ours to judge
        Assert.Equal(0, PdfView.Sweep(Path.Combine(_dir, "missing"), self: 1));
    }

    [Fact]
    public void AStagedPdf_IsNoSavedLink_AndTheOpenButtonSaysItsOwnProgram()
    {
        var links = new BrowserLinks(Path.Combine(_dir, "profile"), TimeProvider.System);
        Assert.Equal(BrowserLinkOutcome.LocalCopy, links.Add("https://files.neonsidekick.example/0123456789abcdef/report.pdf", null, out _));
        Assert.Equal("Error: " + BrowserLinkText.LocalCopy, BrowserLinkText.Refusal(BrowserLinkOutcome.LocalCopy));

        var pdf = BrowserBarState.Initial with { Source = "https://files.neonsidekick.example/0123456789abcdef/report.pdf" };
        var page = BrowserBarState.Initial with { Source = "https://example.com/" };
        Assert.Equal(BrowserText.OpenFileName, pdf.Name(BrowserToolItem.Open));
        Assert.Equal(BrowserText.OpenName, page.Name(BrowserToolItem.Open));
        Assert.Equal("Open in its own program", BrowserText.OpenFileName);
    }
}
