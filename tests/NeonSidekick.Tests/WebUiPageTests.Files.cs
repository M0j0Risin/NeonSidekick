using NeonSidekick.Tests.Fakes;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The 🗂️ sheet's Files tab in the browser (2026-11-16, plan lucid-roaming-lantern #41): each row a <c>target=_blank rel=noopener</c> link to
/// the server's url with its glyph, name, folder and size; none in an installed app's own window; <see cref="WebUiText.FilesNone"/> when there
/// are none; a moved files tag re-reads the open tab.
/// </summary>
public sealed partial class WebUiPageTests
{
    private static WebFileRow FileRowOf(string name, string folder = "out") =>
        new(name, folder, "3 KB", "pdf", "📄", WebUiHost.MadeFilePrefix + "0123456789abcdef0123456789abcdef/" + Uri.EscapeDataString(name));

    private async Task<HeadlessPage?> FilesPageAsync(WebFilesView view, string? init = null)
    {
        _backend.FilesView = view;
        _backend.Snap = new WebSnapshot([], [], State() with { FilesTag = "one" }, WebUiText.Words, null, null);
        if (await SignedInAsync("/") is not { } page)
        {
            return null;
        }

        if (init is not null)
        {
            await page.EvalAsync(init);
        }

        await page.WaitAsync("window.NeonManage !== undefined", "manage.js");
        await page.EvalAsync("window.NeonManage.open('files')");
        await page.WaitAsync(ManageShown + " && document.querySelector('#manage-list').children.length > 0", "the sheet on its Files tab");
        return page;
    }

    [WindowsFact]
    public async Task EachRow_IsALinkToTheServersUrl_WithItsGlyphNameFolderAndSize()
    {
        if (await FilesPageAsync(new WebFilesView([FileRowOf("plan #2.pdf"), FileRowOf("b.pdf", "images")])) is not { } page)
        {
            return;
        }

        const string Rows = "document.querySelectorAll('#manage-list a.file-row')";
        Assert.Equal(2, (await page.EvalAsync(Rows + ".length")).GetInt32());
        Assert.Equal("_blank|noopener", await page.TextAsync(Rows + "[0].target + '|' + " + Rows + "[0].rel"));
        Assert.EndsWith("/plan%20%232.pdf", await page.TextAsync(Rows + "[0].getAttribute('href')"), StringComparison.Ordinal);
        Assert.Equal("📄|plan #2.pdf|out · 3 KB", await page.TextAsync(
            "[" + Rows + "[0].querySelector('.manage-glyph'), " + Rows + "[0].querySelector('.manage-text'), " + Rows + "[0].querySelector('.manage-meta')].map(e => e.textContent).join('|')"));
        Assert.Equal(WebUiText.FilesHint, await page.TextAsync("document.querySelector('#manage-list .files-hint').textContent"));
    }

    /// <summary>A long Files list (2026-11-20, beside the Sessions tab's clipped titles): every row keeps its 44 px and the list scrolls.</summary>
    [WindowsFact]
    public async Task ALongList_KeepsEachRowsHeight_AndScrolls()
    {
        var rows = Enumerable.Range(0, 30).Select(i => FileRowOf("file " + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".pdf")).ToArray();
        if (await FilesPageAsync(new WebFilesView(rows)) is not { } page)
        {
            return;
        }

        Assert.Equal(30, (await page.EvalAsync("document.querySelectorAll('#manage-list a.file-row').length")).GetInt32());
        Assert.True((await page.EvalAsync("Math.min(...[...document.querySelectorAll('#manage-list a.file-row')].map(r => r.getBoundingClientRect().height))")).GetDouble() >= 44);
        Assert.True((await page.EvalAsync("(() => { const l = document.getElementById('manage-list'); return l.scrollHeight > l.clientHeight + 1; })()")).GetBoolean());
    }

    [WindowsFact]
    public async Task InAnInstalledAppsOwnWindow_ARowOpensInPlace()
    {
        const string Standalone = "window.matchMedia = q => ({ matches: q === '(display-mode: standalone)', addEventListener() {}, removeEventListener() {} });";
        if (await FilesPageAsync(new WebFilesView([FileRowOf("a.pdf")]), Standalone) is not { } page)
        {
            return;
        }

        Assert.Equal("", await page.TextAsync("document.querySelector('#manage-list a.file-row').target"));
    }

    /// <summary>
    /// A made PDF opens in the browser's own viewer under the page's policy (no sandbox, which would block the viewer's scripts): the tab stays on
    /// the file's url (a block lands on <c>chrome-error://</c>) and the document is the viewer's wrapper, holding an <c>embed</c> of the PDF.
    /// </summary>
    [WindowsFact]
    public async Task AMadePdf_OpensInTheViewer_NotBlocked()
    {
        string dir = Path.Combine(Path.GetTempPath(), "neon-made-pdf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string pdf = Path.Combine(dir, "report.pdf");
        File.WriteAllBytes(pdf, BrowserPdfReaderTests.MiniPdf("A page of the report."));
        _backend.MadeFilePaths["0123456789abcdef0123456789abcdef"] = pdf;
        try
        {
            if (await SignedInAsync("/") is not { } page)
            {
                return;
            }

            string url = Root + "api/files/0123456789abcdef0123456789abcdef/report.pdf";
            await page.GoAsync(url);
            await page.WaitAsync("document.querySelector('embed[type=\"application/pdf\"]') !== null", "the PDF viewer's embed", seconds: 20,
                show: "location.href + ' ' + document.documentElement.outerHTML.slice(0, 400)");
            Assert.Equal(url, await page.TextAsync("location.href"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [WindowsFact]
    public async Task NoFiles_SaysSo_AndAMovedTag_ReReadsTheOpenTab()
    {
        if (await FilesPageAsync(new WebFilesView([FileRowOf("a.pdf")])) is not { } page)
        {
            return;
        }

        _backend.FilesView = new WebFilesView([]);
        Publish("state", Json(State() with { FilesTag = "two" }));
        await page.WaitAsync("document.querySelector('#manage-list .manage-none') !== null", "emptied on the moved tag");
        Assert.Equal(WebUiText.FilesNone, await page.TextAsync("document.querySelector('#manage-list .manage-none').textContent"));
    }
}
