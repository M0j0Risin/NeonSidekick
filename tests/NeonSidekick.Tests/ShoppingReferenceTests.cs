using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Plans;
using NeonSidekick.Shopping;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Web;

namespace NeonSidekick.Tests;

/// <summary>
/// A store's reference documents (2026-10-11, the shopping list's phase 3): the folder read into the index — a PDF through the browser
/// (a fake here; <see cref="BrowserPdfReaderTests"/> and the <c>shopping:pdf-read</c> smoke prove the real one), text as one page, a
/// picture as one page — again only when a file changed; searched for a product's lines; and the tools over it.
/// </summary>
public sealed class ShoppingReferenceTests : IDisposable
{
    /// <summary>A 1×1 PNG.</summary>
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly ShoppingStoreHolder _holder;
    private readonly ShoppingList _list;
    private readonly FakeHeadlessBrowser _browser = new();
    private readonly ShoppingReference _reference;
    private readonly ShopStore _heb;

    public ShoppingReferenceTests()
    {
        _holder = new ShoppingStoreHolder(() => _dir, _time);
        _list = new ShoppingList(_holder.Current);
        _reference = new ShoppingReference(_list, _browser, () => _browser.Executable);
        _list.AddStore("H-E-B #372");
        _heb = _list.StoreFor(null, out _)!;
        _browser.PdfReadPages.Add(new PdfPageRead(1, "©2019 HEB\nBread ..... 2\nBeans (Canned) ..... 5\nEggs ..... Dairy", null, 0, 0));
        _browser.PdfReadPages.Add(new PdfPageRead(2, "Dairy Frozen Foods Seafood Market Bakery", Png, 1, 1));
    }

    public void Dispose()
    {
        _holder.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Folder => _list.Store.ReferenceFolder(_heb);

    private string Put(string name, byte[] bytes)
    {
        string path = Path.Combine(Folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static async Task<object?> Call(AIFunction tool, params (string Name, object? Value)[] pairs) =>
        await tool.InvokeAsync(new AIFunctionArguments(pairs.ToDictionary(p => p.Name, p => p.Value)));

    [Fact]
    public async Task TheFolder_IsReadOnce_AgainWhenAFileChanges_AndAFileGoneIsForgotten()
    {
        Assert.False(_reference.IsStale(_heb));
        Put("guide.pdf", BrowserPdfReaderTests.MiniPdf("x"));
        Put("notes.md", System.Text.Encoding.UTF8.GetBytes("Goya   is on Aisle 2\r\n\r\nTortillas by the bakery"));
        Put("maps/front.png", Png);
        Put("readme.docx", [1, 2, 3]);   // not a kind read: never listed
        Assert.True(_reference.IsStale(_heb));

        var first = await _reference.RefreshAsync(_heb, force: false, CancellationToken.None);
        Assert.Equal(new ShopRefSummary(3, 0, 0, 4, []), first with { Problems = [] });
        Assert.Empty(first.Problems);
        Assert.Single(_browser.PdfReads);
        Assert.False(_reference.IsStale(_heb));

        // The page with a picture keeps it in the cache; the picture file is its own page's picture.
        var map = _list.Store.RefPage(_heb.Id, "guide.pdf", 2)!;
        Assert.Equal(Path.Combine(_reference.CacheFolder(_heb), "guide.pdf-p2.png"), map.Image);
        Assert.Equal(Png, File.ReadAllBytes(map.Image!));
        Assert.Equal(Path.Combine(Folder, "maps", "front.png"), _list.Store.RefPage(_heb.Id, Path.Combine("maps", "front.png"), 1)!.Image);
        Assert.Equal("Goya is on Aisle 2\nTortillas by the bakery", _list.Store.RefPage(_heb.Id, "NOTES.md", 1)!.Text);

        // Unchanged: nothing read; reread: every file; a file gone: forgotten with its pictures.
        Assert.Equal(new ShopRefSummary(0, 3, 0, 4, []), (await _reference.RefreshAsync(_heb, force: false, CancellationToken.None)) with { Problems = [] });
        Assert.Equal(3, (await _reference.RefreshAsync(_heb, force: true, CancellationToken.None)).Read);
        File.Delete(Path.Combine(Folder, "guide.pdf"));
        Assert.Equal(1, (await _reference.RefreshAsync(_heb, force: false, CancellationToken.None)).Removed);
        Assert.False(File.Exists(map.Image));
        Assert.Null(_list.Store.RefPage(_heb.Id, "guide.pdf", 1));
    }

    [Fact]
    public async Task APdf_WithNoBrowser_IsAProblem_TheOtherFilesStillRead()
    {
        var blind = new ShoppingReference(_list, null, () => null);
        Put("guide.pdf", BrowserPdfReaderTests.MiniPdf("x"));
        Put("notes.txt", "Bread: aisle 2"u8.ToArray());

        var summary = await blind.RefreshAsync(_heb, force: false, CancellationToken.None);

        Assert.Equal(1, summary.Read);
        Assert.Equal(["guide.pdf: " + ShoppingText.NoBrowserForPdf], summary.Problems);
        Assert.True(blind.IsStale(_heb));   // read again once a browser is there
    }

    [Fact]
    public async Task Search_FindsThePagesLines_PluralsFolded()
    {
        Put("guide.pdf", BrowserPdfReaderTests.MiniPdf("x"));
        await _reference.RefreshAsync(_heb, force: false, CancellationToken.None);

        var bread = Assert.Single(_reference.Search(_heb, "sourdough bread"));
        Assert.Equal(("guide.pdf", 1), (bread.File, bread.Page));
        Assert.Equal(["Bread ..... 2"], bread.Lines);
        Assert.Equal(["Eggs ..... Dairy"], _reference.Search(_heb, "egg")[0].Lines);
        Assert.Equal(["Beans (Canned) ..... 5"], _reference.Search(_heb, "canned bean")[0].Lines);
        Assert.Empty(_reference.Search(_heb, "kombucha"));
        Assert.Equal("\"eggs\" OR \"egg\"* OR \"berries\" OR \"berr\"*", ShoppingReference.FtsQuery("Eggs, berries; a"));
    }

    [WindowsFact]
    public async Task TheTools_LocateWithTheGuidesLines_AndReferenceReadsAPageWithItsPicture()
    {
        Put("guide.pdf", BrowserPdfReaderTests.MiniPdf("x"));
        var tools = ChatScreen.ShoppingTools(_list, null, _reference);
        Assert.Contains(ShoppingReferenceTool.ToolName, PlanTools.ReadOnly);

        // locate reads the changed folder first, then says what the guide says.
        string located = (string)(await Call(tools.Single(t => t.Name == ShoppingLocateTool.ToolName), ("term", "bread")))!;
        Assert.EndsWith("no fact places it.\nFrom the store's reference documents:\nguide.pdf p1:\n  Bread ..... 2", located, StringComparison.Ordinal);

        var reference = tools.Single(t => t.Name == ShoppingReferenceTool.ToolName);
        Assert.Equal("Reference documents of H-E-B #372 (" + Folder + "):\n- guide.pdf: 2 pages", await Call(reference));
        Assert.StartsWith("From the store's reference documents:\nguide.pdf p2:\n  Dairy Frozen Foods", (string)(await Call(reference, ("query", "frozen")))!, StringComparison.Ordinal);
        Assert.Equal(ShoppingText.NothingInReference(_heb, "kombucha"), await Call(reference, ("query", "kombucha")));
        Assert.Equal(ShoppingText.NoSuchPage(_heb, "guide.pdf", 9), await Call(reference, ("file", "guide.pdf"), ("page", 9)));

        Assert.Equal("guide.pdf p2 (it has a picture: picture: true to see it):\nDairy Frozen Foods Seafood Market Bakery", await Call(reference, ("file", "guide.pdf"), ("page", 2)));
        var seen = Assert.IsType<ToolImageResult>(await Call(reference, ("file", "GUIDE.pdf"), ("page", 2), ("picture", true)));
        Assert.Equal("guide.pdf p2 (its picture is in the next message):\nDairy Frozen Foods Seafood Market Bakery", seen.Text);
        Assert.Equal((1, 1), (seen.Images[0].Width, seen.Images[0].Height));
        Assert.Equal("guide.pdf p1:\n©2019 HEB\nBread ..... 2\nBeans (Canned) ..... 5\nEggs ..... Dairy\n" + ShoppingText.NoPicture("guide.pdf", 1), await Call(reference, ("file", "guide.pdf"), ("picture", true)));

        Assert.StartsWith("Reference documents of H-E-B #372: 1 file read, 0 unchanged; 2 pages held.\nReference documents of", (string)(await Call(reference, ("reread", true)))!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reindex_ReadsEveryFileAgain()
    {
        Put("guide.pdf", BrowserPdfReaderTests.MiniPdf("x"));
        var files = new WorkingDirectory(() => Path.Combine(_dir, "files"), _time);
        var line = ShoppingCommand.Parse("reindex");
        Assert.Equal(new ShoppingCommandLine(ShoppingVerb.Reindex, ""), line);

        Assert.Equal(["Reference documents of H-E-B #372: 1 file read, 0 unchanged; 2 pages held."], await ShoppingCommandRunner.RunAsync(_list, line, files, null, null, CancellationToken.None, _reference));
        Assert.Equal([ShoppingText.NoSuchStore("Kroger", _list.Store.Stores())], await ShoppingCommandRunner.RunAsync(_list, ShoppingCommand.Parse("reindex Kroger"), files, null, null, CancellationToken.None, _reference));
        Assert.Single(_browser.PdfReads);   // the second named no store: nothing read
    }

    [Fact]
    public void PictureName_IsTheFilesPathMadeOneName()
    {
        Assert.Equal("maps-floor-plan.pdf-p2.png", ShoppingReference.PictureName(Path.Combine("maps", "floor plan.pdf"), 2));
        Assert.True(ShoppingReference.IsReadable("Guide.PDF"));
        Assert.True(ShoppingReference.IsReadable("map.jpeg"));
        Assert.False(ShoppingReference.IsReadable("guide.docx"));
    }

    [WindowsFact]
    public async Task RenamingAStore_MovesItsFolders_ThePicturesFollow_AndAFolderInUseStaysPut()
    {
        Put("guide.pdf", BrowserPdfReaderTests.MiniPdf("x"));
        Put("maps/front.png", Png);
        await _reference.RefreshAsync(_heb, force: false, CancellationToken.None);

        string answer = _list.RenameStore(_heb, "H-E-B Bandera");

        var renamed = _list.Store.FindStore("H-E-B Bandera")!;
        Assert.Equal("h-e-b-bandera", renamed.Folder);
        Assert.EndsWith(ShoppingText.FolderMoved(_list.Store.ReferenceFolder(renamed)), answer, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_list.Store.ReferenceRoot, "h-e-b-372")));
        var map = _list.Store.RefPage(renamed.Id, "guide.pdf", 2)!;
        Assert.Equal(Path.Combine(_reference.CacheFolder(renamed), "guide.pdf-p2.png"), map.Image);
        Assert.True(File.Exists(map.Image));
        Assert.True(File.Exists(_list.Store.RefPage(renamed.Id, Path.Combine("maps", "front.png"), 1)!.Image));
        Assert.False(_reference.IsStale(renamed));   // nothing to read again

        // A file in the folder held open: the move fails, is undone, and the store is renamed with its folder as it was.
        using (File.Open(Path.Combine(_list.Store.ReferenceFolder(renamed), "guide.pdf"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            string kept = _list.RenameStore(renamed, "Bandera");
            Assert.Contains("Its folder stays h-e-b-bandera: ", kept, StringComparison.Ordinal);
        }

        var bandera = _list.Store.FindStore("Bandera")!;
        Assert.Equal("h-e-b-bandera", bandera.Folder);
        Assert.True(File.Exists(_list.Store.RefPage(bandera.Id, "guide.pdf", 2)!.Image));
        Assert.False(Directory.Exists(Path.Combine(_list.Store.ReferenceRoot, "bandera")));
        Assert.False(Directory.Exists(Path.Combine(_reference.CacheFolder(bandera), "..", "bandera")));
    }

    [Fact]
    public void APicturePath_IsStoredRelative_AndAFullOneFromBefore_FollowsARename()
    {
        var store = _list.Store;
        string full = Path.Combine(store.Folder, "cache", "h-e-b-372", "guide.pdf-p2.png");
        Assert.Equal(Path.Combine("cache", "h-e-b-372", "guide.pdf-p2.png"), store.StoredImage(full));
        Assert.Equal(full, store.FullImage(Path.Combine("cache", "h-e-b-372", "guide.pdf-p2.png")));
        Assert.Equal(Path.Combine("cache", "bandera", "guide.pdf-p2.png"), store.MovedImage(full, "h-e-b-372", "bandera"));
        Assert.Equal(Path.Combine("reference", "bandera", "maps", "front.png"), store.MovedImage(Path.Combine("reference", "H-E-B-372", "maps", "front.png"), "h-e-b-372", "bandera"));
        Assert.Equal(Path.Combine("cache", "other", "x.png"), store.MovedImage(Path.Combine("cache", "other", "x.png"), "h-e-b-372", "bandera"));
        string outside = Path.Combine(Path.GetTempPath(), "elsewhere.png");
        Assert.Equal(outside, store.StoredImage(outside));
    }
}