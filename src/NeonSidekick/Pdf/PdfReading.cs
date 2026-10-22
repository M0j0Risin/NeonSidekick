using System.Collections.Concurrent;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Web;

namespace NeonSidekick.Pdf;

/// <summary>
/// A PDF's identity for the cache: the file's full path, length and last write (<see cref="WorkingDirectory.TryStamp"/>), and who reads it
/// (<paramref name="Reader"/>: empty for the working directory, a UNC share's name and account for a share — 2026-10-22, review M3: a
/// share's account that could only see the file's size and time was answered with text another account had read).
/// </summary>
public sealed record PdfDocKey(string Full, long Length, DateTime Written, string Reader = "")
{
    /// <summary>The cache's slot: the reader and the path.</summary>
    public string Slot => Reader.Length == 0 ? Full : Reader + "\n" + Full;
}

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
    private readonly Dictionary<string, Gate> _gates = new(PathComparer);
    private readonly Lock _lock = new();
    private long _tick;

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>One document's read lock and how many callers hold or wait on it (2026-10-22, review K13: a gate per path was never let go).</summary>
    private sealed class Gate
    {
        public SemaphoreSlim Lock { get; } = new(1, 1);
        public int Users { get; set; }
    }

    private sealed class Document(PdfDocKey key, string display)
    {
        public PdfDocKey Key { get; } = key;

        /// <summary>The path as the logs name it (2026-10-22, review T11: the eviction line logged the full path).</summary>
        public string Display { get; } = display;

        /// <summary>A SHA-256 of the bytes its pages were read from (2026-10-22, review Q12): pages of two versions never mix.</summary>
        public string? Hash { get; set; }
        public int PageCount { get; set; } = -1;
        public Dictionary<int, string> Texts { get; } = [];
        public IReadOnlyList<PdfOutlineItem>? Outline { get; set; }
        public IReadOnlyList<string>? Labels { get; set; }
        public IReadOnlyList<PdfFormField>? Fields { get; set; }
        public bool PureXfa { get; set; }
        public long Chars { get; set; }
        public long Used { get; set; }
    }

    /// <summary>The read locks held now (for the tests: none once every read is done, review K13).</summary>
    public int Gates
    {
        get
        {
            lock (_lock)
            {
                return _gates.Count;
            }
        }
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
        Gate gate;
        lock (_lock)
        {
            if (!_gates.TryGetValue(key.Slot, out gate!))
            {
                _gates[key.Slot] = gate = new Gate();
            }

            gate.Users++;
        }

        try
        {
            await gate.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            LetGo(key.Slot, gate);
            throw;
        }

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

            // Every page for find, but no more than one read's (2026-10-22, review M8: it was int.MaxValue, a 300,000-page PDF read whole).
            var options = new PdfReadOptions(
                MaxPages: plan.Unknown && ask.AllPages ? MaxPagesPerRead : ask.MaxPages,
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

            var document = Keep(key, display, read, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)));
            var pictures = read.Pages.Where(p => p.Png is not null).ToList();
            return Answer(document, pictures, read.Detail);   // a document over MaxChars alone is not held (Keep)
        }
        finally
        {
            gate.Lock.Release();
            LetGo(key.Slot, gate);
        }
    }

    /// <summary>One caller done with <paramref name="gate"/>; the last lets it go (review K13).</summary>
    private void LetGo(string slot, Gate gate)
    {
        lock (_lock)
        {
            if (--gate.Users == 0 && _gates.TryGetValue(slot, out var current) && ReferenceEquals(current, gate))
            {
                _gates.Remove(slot);
            }
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
            ? Enumerable.Range(1, Math.Min(held.PageCount, MaxPagesPerRead))
            : ask.Pages is { } pages ? pages.Where(p => p <= held.PageCount) : Enumerable.Range(1, Math.Min(held.PageCount, ask.MaxPages));
        return ([.. wanted.Where(p => !held.Texts.ContainsKey(p))], false, outline, labels, fields);
    }

    private Document? Take(PdfDocKey key)
    {
        lock (_lock)
        {
            if (!_documents.TryGetValue(key.Slot, out var document))
            {
                return null;
            }

            if (document.Key != key)
            {
                _documents.Remove(key.Slot);   // the file changed: read again
                return null;
            }

            document.Used = ++_tick;
            return document;
        }
    }

    private Document Keep(PdfDocKey key, string display, PdfRead read, string hash)
    {
        lock (_lock)
        {
            // Pages read from other bytes than those held start the document again (review Q12: two versions in one tick, or a file rewritten
            // between its stamp and its read, mixed their pages).
            if (!_documents.TryGetValue(key.Slot, out var document) || document.Key != key || (document.Hash is { } held && held != hash))
            {
                document = new Document(key, display);
                _documents[key.Slot] = document;
            }

            document.Hash = hash;
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

            // The outline, labels and fields count with the text (2026-10-22, review S3: only the pages' text was counted against MaxChars).
            if (document.Outline is null && read.Outline is not null)
            {
                document.Outline = read.Outline;
                document.Chars += read.Outline.Sum(o => (long)o.Title.Length);
            }

            if (document.Labels is null && read.Labels is not null)
            {
                document.Labels = read.Labels;
                document.Chars += read.Labels.Sum(l => (long)(l?.Length ?? 0));
            }

            if (document.Fields is null && read.Fields is not null)
            {
                document.Fields = read.Fields;   // a form filled is a new file, a new stamp: read again (gap 98)
                document.PureXfa = read.PureXfa;
                document.Chars += read.Fields.Sum(f => (long)f.Name.Length + f.Value.Length + f.Options.Sum(o => o.Length));
            }

            document.Used = ++_tick;
            if (document.Chars > MaxChars)
            {
                // Over the limit alone: answered, not kept (the plan's gap 93), and taken out before Evict would let every other
                // document go to make room for it (2026-10-20, review).
                _documents.Remove(key.Slot);
                return document;
            }

            Evict(key.Slot);
            return document;
        }
    }

    /// <summary>The least recently used dropped until within the limits; <paramref name="keep"/> (the one just read) is the last to go.</summary>
    private void Evict(string keep)
    {
        while (_documents.Count > MaxDocuments || _documents.Values.Sum(d => d.Chars) > MaxChars)
        {
            var oldest = _documents.Values.Where(d => !PathComparer.Equals(d.Key.Slot, keep)).MinBy(d => d.Used);
            if (oldest is null)
            {
                return;
            }

            _documents.Remove(oldest.Key.Slot);
            DiagnosticLog.Debug(PdfText.Category, $"read_pdf's cache let {oldest.Display} go ({oldest.Chars.ToString(System.Globalization.CultureInfo.InvariantCulture)} characters).");
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
