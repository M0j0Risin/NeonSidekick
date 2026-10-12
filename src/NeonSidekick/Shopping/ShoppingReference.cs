using System.Globalization;
using System.Text;
using NeonSidekick.Diagnostics;
using NeonSidekick.Web;

namespace NeonSidekick.Shopping;

/// <summary>What a read of a store's reference folder did (2026-10-11): files read, left as they were, forgotten (gone), the pages now held, and each file's problem.</summary>
public sealed record ShopRefSummary(int Read, int Unchanged, int Removed, int Pages, IReadOnlyList<string> Problems);

/// <summary>A page of a reference document that matched a search: its lines that hold the words, and its picture (a full path; null for none).</summary>
public sealed record ShopRefHit(string File, int Page, IReadOnlyList<string> Lines, string? Image);

/// <summary>
/// A store's reference documents (2026-10-11, the user's ask: a store guide to look things up in when the list's facts do not place an
/// item; the plan's phase 3): every file in the store's folder under <c>db\shopping\reference</c> read into the store's <c>ref_docs</c>,
/// <c>ref_pages</c> and their FTS5 index — a PDF through the headless browser (<see cref="IHeadlessBrowser.ReadPdfAsync"/>, pdf.js in
/// the page: each page's text and its picture, the pictures kept in <c>db\shopping\cache\&lt;store&gt;</c>), a <c>.md</c> or <c>.txt</c>
/// as one page of text, a picture (a floor map photographed) as one page with no text. A file is read again only when its size or
/// time changed (<see cref="IsStale"/>), so the shopping tools may refresh before every search; a file gone is forgotten with its
/// pictures. <see cref="Search"/> finds the pages that hold a product's words and picks out their lines: a store guide's index is one
/// line per product ("Bread ..... 2").
/// </summary>
public sealed class ShoppingReference(ShoppingList list, IHeadlessBrowser? browser, Func<string?> executable, PdfReadOptions? options = null)
{
    private readonly ShoppingList _list = list ?? throw new ArgumentNullException(nameof(list));
    private readonly Func<string?> _executable = executable ?? throw new ArgumentNullException(nameof(executable));
    private readonly PdfReadOptions _options = options ?? PdfReadOptions.Default;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    public const string PdfExtension = ".pdf";

    /// <summary>The text files read as one page each. Pinned.</summary>
    public static readonly IReadOnlyList<string> TextExtensions = [".md", ".txt"];

    /// <summary>The pictures read as one page with no text. Pinned.</summary>
    public static readonly IReadOnlyList<string> PictureExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"];

    /// <summary>The largest text file read (characters past it are dropped).</summary>
    public const int MaxTextChars = 2 * 1024 * 1024;

    /// <summary>The folder under the store's folder holding the pages' pictures, one folder per store. Pinned.</summary>
    public const string CacheFolderName = "cache";

    /// <summary>Whether <paramref name="path"/> is a file a store's folder is read for.</summary>
    public static bool IsReadable(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Equals(PdfExtension, StringComparison.OrdinalIgnoreCase)
            || TextExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)
            || PictureExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The folder <paramref name="shop"/>'s page pictures are kept in.</summary>
    public string CacheFolder(ShopStore shop)
    {
        ArgumentNullException.ThrowIfNull(shop);
        return Path.Combine(_list.Store.Folder, CacheFolderName, shop.Folder);
    }

    /// <summary>The readable files in <paramref name="shop"/>'s folder, relative to it, with their sizes and times.</summary>
    private IReadOnlyList<(string File, string Full, long Size, long Modified)> Files(ShopStore shop)
    {
        string folder = _list.Store.ReferenceFolder(shop);
        try
        {
            return Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                    .Where(IsReadable)
                    .Select(full => { var info = new FileInfo(full); return (Path.GetRelativePath(folder, full), full, info.Length, info.LastWriteTimeUtc.Ticks); })
                    .OrderBy(f => f.Item1, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(ShoppingStore.Category, $"Could not list {folder}: {ex.Message}", ex);
            return [];
        }
    }

    /// <summary>Whether a file of <paramref name="shop"/>'s folder was added, changed or removed since it was last read.</summary>
    public bool IsStale(ShopStore shop)
    {
        var docs = _list.Store.RefDocs(shop.Id).ToDictionary(d => d.File, StringComparer.OrdinalIgnoreCase);
        var files = Files(shop);
        return files.Count != docs.Count || files.Any(f => !docs.TryGetValue(f.File, out var d) || d.Size != f.Size || d.Modified != f.Modified);
    }

    /// <summary>
    /// <paramref name="shop"/>'s folder read: each file added or changed (every file with <paramref name="force"/>) read and recorded, each
    /// file gone forgotten. One read at a time (a second waits, then finds nothing left to do). Never throws for a file: its problem is
    /// in the summary, the rest still read.
    /// </summary>
    public async Task<ShopRefSummary> RefreshAsync(ShopStore shop, bool force, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(shop);
        await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var store = _list.Store;
            var docs = store.RefDocs(shop.Id).ToDictionary(d => d.File, StringComparer.OrdinalIgnoreCase);
            var files = Files(shop);
            int read = 0, unchanged = 0, removed = 0;
            var problems = new List<string>();
            foreach (var doc in docs.Values.Where(d => !files.Any(f => string.Equals(f.File, d.File, StringComparison.OrdinalIgnoreCase))))
            {
                if (store.RemoveRefDoc(doc.Id))
                {
                    removed++;
                    DeletePictures(shop, doc.File);
                }
            }

            foreach (var (file, full, size, modified) in files)
            {
                if (!force && docs.TryGetValue(file, out var known) && known.Size == size && known.Modified == modified)
                {
                    unchanged++;
                    continue;
                }

                string? problem = await ReadAsync(shop, file, full, size, modified, cancellationToken).ConfigureAwait(false);
                if (problem is null)
                {
                    read++;
                }
                else
                {
                    problems.Add(file + ": " + problem);
                }
            }

            int pages = store.RefDocs(shop.Id).Sum(d => d.Pages);
            if (read + removed > 0 || problems.Count > 0)
            {
                DiagnosticLog.Info(ShoppingStore.Category, ShoppingText.AuditReferenceRead(shop, read, removed, pages, problems.Count));
            }

            return new ShopRefSummary(read, unchanged, removed, pages, problems);
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <summary>One file read and recorded; its problem, or null. A PDF read in part is recorded with the pages it got and its note.</summary>
    private async Task<string?> ReadAsync(ShopStore shop, string file, string full, long size, long modified, CancellationToken cancellationToken)
    {
        var store = _list.Store;
        string ext = Path.GetExtension(file);
        try
        {
            if (TextExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                string text = await File.ReadAllTextAsync(full, cancellationToken).ConfigureAwait(false);
                text = text.Length > MaxTextChars ? text[..MaxTextChars] : text;
                return store.SaveRefDoc(shop.Id, file, size, modified, 1, "", [(1, BrowserPdfReader.TidyText(text.Replace("\r", "", StringComparison.Ordinal)), null)]) ? null : ShoppingText.WriteFailed;
            }

            if (PictureExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                return store.SaveRefDoc(shop.Id, file, size, modified, 1, "", [(1, "", store.StoredImage(full))]) ? null : ShoppingText.WriteFailed;
            }

            if (browser is null || _executable() is not { } exe)
            {
                return ShoppingText.NoBrowserForPdf;
            }

            var pdf = await File.ReadAllBytesAsync(full, cancellationToken).ConfigureAwait(false);
            var result = await browser.ReadPdfAsync(exe, pdf, _options, cancellationToken).ConfigureAwait(false);
            if (!result.Ok)
            {
                return result.Detail;
            }

            DeletePictures(shop, file);
            string folder = CacheFolder(shop);
            Directory.CreateDirectory(folder);
            var pages = new List<(int, string, string?)>();
            foreach (var page in result.Pages)
            {
                string? image = null;
                if (page.Png is { } png)
                {
                    image = Path.Combine(folder, PictureName(file, page.Number));
                    await File.WriteAllBytesAsync(image, png, cancellationToken).ConfigureAwait(false);
                }

                pages.Add((page.Number, page.Text, image is null ? null : store.StoredImage(image)));
            }

            return store.SaveRefDoc(shop.Id, file, size, modified, result.PageCount, result.Detail, pages) ? null : ShoppingText.WriteFailed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    /// <summary>The name of a page's picture in the cache: the file's path made one name, then the page. <c>maps/floor plan.pdf</c> page 2 → <c>maps-floor-plan.pdf-p2.png</c>.</summary>
    public static string PictureName(string file, int page)
    {
        ArgumentNullException.ThrowIfNull(file);
        var sb = new StringBuilder(file.Length);
        foreach (char c in file)
        {
            sb.Append(char.IsLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-');
        }

        return sb + "-p" + page.ToString(CultureInfo.InvariantCulture) + ".png";
    }

    private void DeletePictures(ShopStore shop, string file)
    {
        string folder = CacheFolder(shop);
        if (!Directory.Exists(folder))
        {
            return;
        }

        string prefix = PictureName(file, 0)[..^"0.png".Length];
        foreach (string picture in Directory.EnumerateFiles(folder, "*.png"))
        {
            if (Path.GetFileName(picture).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.Delete(picture);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    DiagnosticLog.Debug(ShoppingStore.Category, $"Could not delete {picture}: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// The pages of <paramref name="shop"/>'s documents that hold any of <paramref name="query"/>'s words (plurals folded), the best
    /// first, at most <paramref name="pages"/>; each with its lines that hold the most of the words (at most <paramref name="lines"/>, in
    /// page order). A page whose words matched only across lines keeps its first lines.
    /// </summary>
    public IReadOnlyList<ShopRefHit> Search(ShopStore shop, string query, int pages = 3, int lines = 6)
    {
        ArgumentNullException.ThrowIfNull(shop);
        ArgumentNullException.ThrowIfNull(query);
        var words = ShoppingPlacer.Words(query).Where(w => w.Length > 1).Distinct(StringComparer.Ordinal).ToList();
        var hits = new List<ShopRefHit>();
        foreach (var page in _list.Store.SearchRef(shop.Id, FtsQuery(query), pages))
        {
            var scored = page.Text.Split('\n')
                .Select((line, at) => (line, at, score: ShoppingPlacer.Words(line).Distinct(StringComparer.Ordinal).Count(words.Contains)))
                .Where(x => x.score > 0)
                .OrderByDescending(x => x.score).ThenBy(x => x.at)
                .Take(lines)
                .OrderBy(x => x.at)
                .Select(x => x.line)
                .ToList();
            hits.Add(new ShopRefHit(page.File, page.Page, scored.Count > 0 ? scored : page.Text.Split('\n').Take(Math.Min(lines, 2)).ToList(), page.Image));
        }

        return hits;
    }

    /// <summary>
    /// The FTS5 query for a product's words: each word as typed and its folded form as a prefix (<c>eggs</c> → <c>"eggs" OR "egg"*</c>,
    /// <c>egg</c> → <c>"egg" OR "egg"*</c>, so the singular finds the plural),
    /// any of them a match, every one quoted so nothing typed is an operator. Empty for no words. Pinned.
    /// </summary>
    public static string FtsQuery(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var terms = new List<string>();
        foreach (string raw in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            string word = new string(raw.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            if (word.Length < 2)
            {
                continue;
            }

            terms.Add("\"" + word + "\"");
            string folded = ShoppingPlacer.Singular(word);
            folded = folded.EndsWith("ie", StringComparison.Ordinal) ? folded[..^2] : folded;   // berrie* would miss berry; berr* finds both
            if (folded.Length >= 3)   // egg* finds eggs too
            {
                terms.Add("\"" + folded + "\"*");
            }
        }

        return string.Join(" OR ", terms.Distinct(StringComparer.Ordinal));
    }
}
