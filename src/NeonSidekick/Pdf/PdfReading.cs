using System.Collections.Concurrent;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Web;

namespace NeonSidekick.Pdf;

/// <summary>A PDF's identity for the cache: the file's full path, length and last write (<see cref="WorkingDirectory.TryStamp"/>).</summary>
public sealed record PdfDocKey(string Full, long Length, DateTime Written);

/// <summary>
/// What a read asks for: <see cref="Pages"/> (physical numbers; null = the first <see cref="MaxPages"/>), every page with
/// <see cref="AllPages"/> (<c>find</c>'s), the pages to draw (<see cref="PicturePages"/>, never cached), the outline and labels, and
/// (2026-10-14, gap 98) the form's fields.
/// </summary>
public sealed record PdfAsk(IReadOnlyList<int>? Pages, IReadOnlyList<int> PicturePages, bool Outline, bool Labels, bool AllPages = false, int MaxPages = 40, bool Fields = false);

/// <summary>
/// A read's answer: the document's page count, the text of every page held for it now (the cache's, a copy), the pictures drawn this time,
/// the outline and labels when asked, why it stopped short (<see cref="Detail"/>), or <see cref="Error"/> (a whole sentence) instead.
/// </summary>
public sealed record PdfPages(
    string? Error,
    int PageCount,
    IReadOnlyDictionary<int, string> Texts,
    IReadOnlyList<PdfPageRead> Pictures,
    IReadOnlyList<PdfOutlineItem>? Outline,
    IReadOnlyList<string>? Labels,
    string Detail)
{
    public bool Locked { get; init; }

    /// <summary>The form's fields when asked (gap 98; empty without a form), and whether the form is XFA alone.</summary>
    public IReadOnlyList<PdfFormField>? Fields { get; init; }

    public bool PureXfa { get; init; }

    /// <summary>The printed label of <paramref name="page"/>, when the PDF has labels.</summary>
    public string? LabelOf(int page) => Labels is { Count: > 0 } labels && page >= 1 && page <= labels.Count ? labels[page - 1] : null;
}

/// <summary>
/// The PDFs read for the model (2026-10-14, the PDF plan's phase 1 and gaps 28, 47, 95 and 97): <see cref="IHeadlessBrowser.ReadPdfAsync"/>
/// with an in-memory cache of page texts, kept per page under the file's stamp (a changed file is read again), with its page count,
/// outline and labels, the least recently used dropped past <see cref="MaxDocuments"/> or <see cref="MaxChars"/>, and a document over that
/// alone not kept at all. Pictures are never cached: they are drawn when asked. A call first takes what is held; the file's bytes are
/// read (through the caller's <c>bytes</c>, so a UNC share reads them under its own sign-in) only when something is missing, and only the
/// pages still missing are asked for. One read at a time per document: a second caller waits for the first and then finds most of what
/// it wants held. A caller that cancels stops only its own read (the plan's gap 47 shared one read with a count of waiters; a read per
/// caller in turn gives the same: no caller's ESC ends another's, and the second asks only for what the first did not get). The same
/// shape as <c>Shopping/ShoppingReference</c>, not its code: that one's index is a database that outlives the run.
/// </summary>
public sealed class PdfReading(IHeadlessBrowser? browser, Func<string?> executable)
{
    /// <summary>The most documents held.</summary>
    public const int MaxDocuments = 16;

    /// <summary>The most characters held across them: a 1,000-page manual's text fits.</summary>
    public const long MaxChars = 8_000_000;

    /// <summary>The most pages one read sets out to read when the count is not known yet and a range runs open.</summary>
    public const int MaxPagesPerRead = 2_000;

    private readonly Func<string?> _executable = executable ?? throw new ArgumentNullException(nameof(executable));
    private readonly Dictionary<string, Document> _documents = new(PathComparer);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(PathComparer);
    private readonly Lock _lock = new();
    private long _tick;

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed class Document(PdfDocKey key)
    {
        public PdfDocKey Key { get; } = key;
        public int PageCount { get; set; } = -1;
        public Dictionary<int, string> Texts { get; } = [];
        public IReadOnlyList<PdfOutlineItem>? Outline { get; set; }
        public IReadOnlyList<string>? Labels { get; set; }
        public IReadOnlyList<PdfFormField>? Fields { get; set; }
        public bool PureXfa { get; set; }
        public long Chars { get; set; }
        public long Used { get; set; }
    }

    /// <summary>The documents held now (for the tests).</summary>
    public int Held
    {
        get
        {
            lock (_lock)
            {
                return _documents.Count;
            }
        }
    }

    /// <summary>
    /// <paramref name="ask"/> answered for the file <paramref name="key"/> names, its bytes from <paramref name="bytes"/> when a read is
    /// needed (a sentence instead of bytes is the answer's error, as it is). Throws only for <paramref name="cancellationToken"/>.
    /// </summary>
    public async Task<PdfPages> ReadAsync(PdfDocKey key, string display, Func<CancellationToken, Task<(byte[]? Bytes, string? Error)>> bytes, PdfAsk ask, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(ask);
        var gate = _gates.GetOrAdd(key.Full, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var held = Take(key);
            var plan = Plan(held, ask);
            if (held is not null && plan.Pages.Count == 0 && !plan.Outline && !plan.Labels && !plan.Fields && ask.PicturePages.Count == 0 && !plan.Unknown)
            {
                return Answer(held, [], "");
            }

            if (browser is null || _executable() is not { } exe)
            {
                return Fail(PdfText.NoBrowserToRead);
            }

            var (data, error) = await bytes(cancellationToken).ConfigureAwait(false);
            if (data is null)
            {
                return Fail(error ?? PdfText.ReadFailed(display, "nothing was read"));
            }

            if (!PdfFile.HasPdfHeader(data))
            {
                return Fail(PdfText.NotAPdf(display));
            }

            var options = new PdfReadOptions(
                MaxPages: plan.Unknown && ask.AllPages ? int.MaxValue : ask.MaxPages,
                Pictures: false,
                Pages: plan.Unknown && ask.Pages is null ? null : [.. plan.Pages.Union(ask.PicturePages).Order()],
                PicturePages: ask.PicturePages,
                Outline: plan.Outline,
                Labels: plan.Labels,
                Fields: plan.Fields);
            var read = await browser.ReadPdfAsync(exe, data, options, cancellationToken).ConfigureAwait(false);
            if (read.Locked)
            {
                return Fail(PdfText.Locked(display)) with { Locked = true };
            }

            if (!read.Ok)
            {
                return Fail(PdfText.ReadFailed(display, read.Detail));
            }

            var document = Keep(key, read);
            var pictures = read.Pages.Where(p => p.Png is not null).ToList();
            var answer = Answer(document, pictures, read.Detail);
            if (document.Chars > MaxChars)
            {
                Forget(key);   // over the limit alone: answered, not kept (the plan's gap 93)
            }

            return answer;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>What a read needs: the pages whose text is not held, whether the count is not known yet, and the outline, labels and fields if missing.</summary>
    private static (IReadOnlyList<int> Pages, bool Unknown, bool Outline, bool Labels, bool Fields) Plan(Document? held, PdfAsk ask)
    {
        bool outline = ask.Outline && held?.Outline is null;
        bool labels = ask.Labels && held?.Labels is null;
        bool fields = ask.Fields && held?.Fields is null;
        if (held is null || held.PageCount < 0)
        {
            return (ask.Pages ?? [], true, outline, labels, fields);
        }

        IEnumerable<int> wanted = ask.AllPages
            ? Enumerable.Range(1, held.PageCount)
            : ask.Pages is { } pages ? pages.Where(p => p <= held.PageCount) : Enumerable.Range(1, Math.Min(held.PageCount, ask.MaxPages));
        return ([.. wanted.Where(p => !held.Texts.ContainsKey(p))], false, outline, labels, fields);
    }

    private Document? Take(PdfDocKey key)
    {
        lock (_lock)
        {
            if (!_documents.TryGetValue(key.Full, out var document))
            {
                return null;
            }

            if (document.Key != key)
            {
                _documents.Remove(key.Full);   // the file changed: read again
                return null;
            }

            document.Used = ++_tick;
            return document;
        }
    }

    private Document Keep(PdfDocKey key, PdfRead read)
    {
        lock (_lock)
        {
            if (!_documents.TryGetValue(key.Full, out var document) || document.Key != key)
            {
                document = new Document(key);
                _documents[key.Full] = document;
            }

            document.PageCount = read.PageCount;
            foreach (var page in read.Pages)
            {
                if (document.Texts.TryGetValue(page.Number, out var before))
                {
                    document.Chars -= before.Length;
                }

                document.Texts[page.Number] = page.Text;
                document.Chars += page.Text.Length;
            }

            document.Outline ??= read.Outline;
            document.Labels ??= read.Labels;
            if (document.Fields is null && read.Fields is not null)
            {
                document.Fields = read.Fields;   // a form filled is a new file, a new stamp: read again (gap 98)
                document.PureXfa = read.PureXfa;
            }

            document.Used = ++_tick;
            Evict(key.Full);
            return document;
        }
    }

    /// <summary>The least recently used dropped until within the limits; <paramref name="keep"/> (the one just read) is the last to go.</summary>
    private void Evict(string keep)
    {
        while (_documents.Count > MaxDocuments || _documents.Values.Sum(d => d.Chars) > MaxChars)
        {
            var oldest = _documents.Values.Where(d => !PathComparer.Equals(d.Key.Full, keep)).MinBy(d => d.Used);
            if (oldest is null)
            {
                return;
            }

            _documents.Remove(oldest.Key.Full);
            DiagnosticLog.Debug(PdfText.Category, $"read_pdf's cache let {oldest.Key.Full} go ({oldest.Chars.ToString(System.Globalization.CultureInfo.InvariantCulture)} characters).");
        }
    }

    private void Forget(PdfDocKey key)
    {
        lock (_lock)
        {
            _documents.Remove(key.Full);
        }
    }

    private PdfPages Answer(Document document, IReadOnlyList<PdfPageRead> pictures, string detail)
    {
        lock (_lock)
        {
            return new PdfPages(null, document.PageCount, new Dictionary<int, string>(document.Texts), pictures, document.Outline, document.Labels, detail) { Fields = document.Fields, PureXfa = document.PureXfa };
        }
    }

    private static PdfPages Fail(string error) => new(error, 0, new Dictionary<int, string>(), [], null, null, "");

    /// <summary>
    /// The file <paramref name="path"/> names in <paramref name="files"/> as a cache key and its display path, or the refusal (a sentence):
    /// outside the sandbox, missing, a folder, over <see cref="BrowserPdfReader.MaxBytes"/>. One metadata call; nothing read. Shared by
    /// <c>read_pdf</c> and <c>/read</c> (2026-10-14).
    /// </summary>
    public static string? Locate(WorkingDirectory files, string path, out PdfDocKey? key, out string display)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(path);
        key = null;
        display = path;
        var outcome = files.Resolve(path, forWrite: false, out string full);
        if (outcome != FileOutcome.Ok)
        {
            return FileText.Error(outcome, path, "read");
        }

        display = files.Relative(full);
        if (Directory.Exists(full))
        {
            return FileText.Error(FileOutcome.IsDirectory, files.Relative(full, isDirectory: true), "read");
        }

        if (files.TryStamp(path) is not { } stamp)
        {
            return FileText.Error(FileOutcome.Missing, display, "read");
        }

        if (stamp.Length > BrowserPdfReader.MaxBytes)
        {
            return PdfText.TooBigToRead(display);
        }

        key = new PdfDocKey(stamp.Full, stamp.Length, stamp.Written);
        return null;
    }

    /// <summary>The file's bytes for a read, or the sentence why not (the bytes callback of <see cref="ReadAsync"/>).</summary>
    public static (byte[]? Bytes, string? Error) BytesOf(WorkingDirectory files, string path)
    {
        ArgumentNullException.ThrowIfNull(files);
        var read = files.ReadBytes(path, BrowserPdfReader.MaxBytes);
        return read.Outcome == FileOutcome.Ok
            ? (read.Bytes, null)
            : (null, read.Outcome == FileOutcome.TooBig ? PdfText.TooBigToRead(read.Relative) : FileText.Error(read.Outcome, read.Relative, "read", read.Detail));
    }
}
