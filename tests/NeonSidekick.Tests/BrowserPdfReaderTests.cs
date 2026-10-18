using System.Globalization;
using System.Text;
using System.Text.Json;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Web;

namespace NeonSidekick.Tests;

/// <summary>
/// Reading a PDF through the headless browser (2026-10-11, the shopping list's reference documents): pdf.js embedded, imported into a
/// blank page, each page's text and picture read back. The pure half everywhere; the live read on the machine's own browser, on every
/// system since 2026-10-17 (the Mac pass, the user's call: all four passed on a Mac's Chrome; Windows only until then, for no written reason).
/// </summary>
public sealed class BrowserPdfReaderTests
{
    /// <summary>A minimal PDF, one page per text, each line of a page's text a line of Helvetica (written by hand: offsets counted).</summary>
    public static byte[] MiniPdf(params string[] pages)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [" + string.Join(" ", pages.Select((_, i) => (4 + i * 2).ToString(CultureInfo.InvariantCulture) + " 0 R")) + "] /Count " + pages.Length.ToString(CultureInfo.InvariantCulture) + " >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        foreach (string text in pages)
        {
            int content = objects.Count + 2;
            objects.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents " + content.ToString(CultureInfo.InvariantCulture) + " 0 R >>");
            var stream = new StringBuilder("BT /F1 18 Tf 72 720 Td 22 TL");
            foreach (string line in text.Split('\n'))
            {
                stream.Append(" (").Append(line.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)")).Append(") Tj T*");
            }

            stream.Append(" ET");
            objects.Add("<< /Length " + stream.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + stream + "\nendstream");
        }

        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        int xref = pdf.Length;
        pdf.Append("xref\n0 ").Append((objects.Count + 1).ToString(CultureInfo.InvariantCulture)).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets)
        {
            pdf.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        pdf.Append("trailer\n<< /Size ").Append((objects.Count + 1).ToString(CultureInfo.InvariantCulture)).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    [Fact]
    public void TheModules_AreEmbedded()
    {
        Assert.NotNull(BrowserPdfReader.ModuleBase64(BrowserPdfReader.LibraryResource));
        Assert.NotNull(BrowserPdfReader.ModuleBase64(BrowserPdfReader.WorkerResource));
        Assert.Null(BrowserPdfReader.ModuleBase64("pdfjs/none.mjs"));
    }

    [Fact]
    public void TheExpressions_ImportOpenAndReadAPage_WithoutEval()
    {
        Assert.StartsWith("(async () => { const lib = await import('data:text/javascript;base64,QUJD'); globalThis.pdfjsWorker = await import('data:text/javascript;base64,REVG');", BrowserPdfReader.LoadExpression("QUJD", "REVG"), StringComparison.Ordinal);
        Assert.Contains("isEvalSupported: false", BrowserPdfReader.OpenExpression("JVBE"), StringComparison.Ordinal);
        Assert.Contains("getPage(3)", BrowserPdfReader.PageExpression(3, 1600, picture: true), StringComparison.Ordinal);
        Assert.Contains("1600 / Math.max", BrowserPdfReader.PageExpression(3, 1600, picture: true), StringComparison.Ordinal);
        Assert.Contains("(i.str || '') + (i.hasEOL ? '\\n' : '')).join('')", BrowserPdfReader.PageExpression(3, 1600, picture: true), StringComparison.Ordinal);
        Assert.DoesNotContain("canvas", BrowserPdfReader.PageExpression(3, 1600, picture: false), StringComparison.Ordinal);
    }

    [Fact]
    public void ReadPage_TakesTheTextTidied_AndThePicturesBytes()
    {
        using var doc = JsonDocument.Parse("{\"t\":\"Aisle  7 \\n\\n  Eggs\\tMilk \\n\",\"p\":\"data:image/png;base64,iVBO\",\"w\":10,\"h\":20}");
        var page = BrowserPdfReader.ReadPage(2, doc.RootElement)!;
        Assert.Equal(2, page.Number);
        Assert.Equal("Aisle 7\nEggs Milk", page.Text);
        Assert.Equal(Convert.FromBase64String("iVBO"), page.Png);
        Assert.Equal((10, 20), (page.Width, page.Height));

        using var bad = JsonDocument.Parse("{\"x\":1}");
        Assert.Null(BrowserPdfReader.ReadPage(1, bad.RootElement));
    }

    [Fact]
    public async Task TheMachinesBrowser_ReadsEachPagesText_AndDrawsIt()
    {
        var browser = new HeadlessBrowser(Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", "pdf-" + Guid.NewGuid().ToString("N")));
        if (browser.Locate("") is not { } executable)
        {
            return;   // no browser installed: nothing to prove here
        }

        var read = await browser.ReadPdfAsync(executable, MiniPdf("Aisle 7\nEggs and milk", "Aisle 12\nGoya beans", "Pharmacy"), new PdfReadOptions(MaxPages: 2, MaxSide: 800), CancellationToken.None);

        Assert.True(read.Ok, read.Detail);
        Assert.Equal(3, read.PageCount);
        Assert.Equal("read the first 2 of 3 pages", read.Detail);
        Assert.Equal(["Aisle 7\nEggs and milk", "Aisle 12\nGoya beans"], read.Pages.Select(p => p.Text));
        var png = read.Pages[0].Png!;
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], png.Take(4));
        Assert.Equal(800, Math.Max(read.Pages[0].Width, read.Pages[0].Height));   // 792 points high, scaled to the longer side's 800

        var notPdf = await browser.ReadPdfAsync(executable, Encoding.ASCII.GetBytes("hello"), PdfReadOptions.Default, CancellationToken.None);
        Assert.False(notPdf.Ok);
        Assert.StartsWith("not a PDF pdf.js can open: ", notPdf.Detail, StringComparison.Ordinal);
    }

    private static HeadlessBrowser LiveBrowser(out string? executable)
    {
        var browser = new HeadlessBrowser(Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", "pdf-" + Guid.NewGuid().ToString("N")));
        executable = browser.Locate("");
        return browser;
    }

    /// <summary>
    /// The PDF plan's gaps 44, 91, 92 and 94 on the machine's own browser (2026-10-14): exactly the pages asked, a picture of only the one
    /// asked, the outline's two levels with their pages, the printed labels, and a page past the end left out.
    /// </summary>
    [Fact]
    public async Task TheMachinesBrowser_ReadsTheAskedPages_TheOutline_AndTheLabels()
    {
        var browser = LiveBrowser(out string? executable);
        if (executable is null)
        {
            return;   // no browser installed: nothing to prove here
        }

        var read = await browser.ReadPdfAsync(executable, TestPdfs.WithOutlineAndLabels(), new PdfReadOptions(MaxSide: 400, Pages: [2, 3, 7], PicturePages: [3], Outline: true, Labels: true), CancellationToken.None);

        Assert.True(read.Ok, read.Detail);
        Assert.Equal(3, read.PageCount);
        Assert.Equal("", read.Detail);
        Assert.Equal([2, 3], read.Pages.Select(p => p.Number));
        Assert.Equal(["two", "three"], read.Pages.Select(p => p.Text));
        Assert.Null(read.Pages[0].Png);
        Assert.NotNull(read.Pages[1].Png);
        Assert.Equal([new PdfOutlineItem("Front", 1, 1), new PdfOutlineItem("Preface", 2, 2), new PdfOutlineItem("Body", 1, 3)], read.Outline!);
        Assert.Equal(["i", "ii", "1"], read.Labels!);

        var none = await browser.ReadPdfAsync(executable, TestPdfs.WithOutlineAndLabels(), new PdfReadOptions(Pages: [9]), CancellationToken.None);
        Assert.True(none.Ok, none.Detail);
        Assert.Equal(3, none.PageCount);
        Assert.Empty(none.Pages);
        Assert.Null(none.Outline);
    }

    /// <summary>The PDF plan's gap 2 (2026-10-14): a file that asks for a password is <see cref="PdfRead.Locked"/>, not a raw error.</summary>
    [Fact]
    public async Task TheMachinesBrowser_SaysALockedPdfIsLocked()
    {
        var browser = LiveBrowser(out string? executable);
        if (executable is null)
        {
            return;
        }

        var read = await browser.ReadPdfAsync(executable, TestPdfs.Locked(), PdfReadOptions.Default, CancellationToken.None);
        Assert.False(read.Ok);
        Assert.True(read.Locked, read.Detail);
        Assert.Equal(HeadlessBrowser.PdfLockedDetail, read.Detail);
    }

    /// <summary>The PDF plan's gap 3 (2026-10-14): a Japanese page whose font needs a predefined CMap reads its text, from the packed CMaps.</summary>
    [Fact]
    public async Task TheMachinesBrowser_ReadsAPageThatNeedsAPredefinedCMap()
    {
        Assert.True(BrowserPdfReader.CMapCount > 100, "the packed CMaps are embedded");
        var browser = LiveBrowser(out string? executable);
        if (executable is null)
        {
            return;
        }

        var read = await browser.ReadPdfAsync(executable, TestPdfs.Cjk(), new PdfReadOptions(Pictures: false), CancellationToken.None);
        Assert.True(read.Ok, read.Detail);
        Assert.Equal(TestPdfs.CjkText, Assert.Single(read.Pages).Text);
    }

    [Fact]
    public void TheOutline_AndTheLabels_AreReadFromTheirAnswers()
    {
        using var outline = JsonDocument.Parse("""{ "o": [ { "t": "  A   title ", "l": 1, "p": 3 }, { "t": "No page", "l": 2, "p": 0 }, { "t": "", "l": 1, "p": 1 }, { "x": 1 } ] }""");
        Assert.Equal([new PdfOutlineItem("A title", 1, 3), new PdfOutlineItem("No page", 2, null)], BrowserPdfReader.ReadOutline(outline.RootElement));
        using var labels = JsonDocument.Parse("""{ "l": [ "i", " ii ", 3 ] }""");
        Assert.Equal(["i", "ii", ""], BrowserPdfReader.ReadLabels(labels.RootElement));
        using var locked = JsonDocument.Parse("""{ "locked": true }""");
        Assert.True(BrowserPdfReader.IsLocked(locked.RootElement));
        Assert.Contains("PasswordException", BrowserPdfReader.OpenExpression("AA=="), StringComparison.Ordinal);
        Assert.Contains("CMapReaderFactory: NeonCMaps", BrowserPdfReader.OpenExpression("AA=="), StringComparison.Ordinal);
    }
}
