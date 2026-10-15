using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Pdf;
using NeonSidekick.Settings;
using NeonSidekick.Unc;
using NeonSidekick.Web;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>read_pdf(path, pages?, pictures?, find?, share?)</c> (2026-10-14, the PDF plan's phase 1, the user's ask: the app carried pdf.js for
/// the shopping list alone, and a PDF was "not a text file" to every other tool): a PDF's text page by page through
/// <see cref="PdfReading"/> (pdf.js in the headless browser, the texts cached under the file's stamp). The answer's first line is its
/// header, as <c>read_file</c>'s is — <c>report.pdf (pages 1–12 of 87; next: pages "13-")</c> — so the transcript's note says what came
/// back; pages follow under <c>--- page N ---</c> headings (the printed label beside the number when they differ, gap 92) until
/// <see cref="WorkingDirectory.MaxReadChars"/>, the first page always (cut, with a note, when it alone is longer: gap 90).
///
/// <para>Without <c>pages</c> the answer starts with the outline's first two levels (gap 91), so a long document can be read by its
/// sections. <c>pictures: true</c> draws the pages too — up to <c>File view image max (per call)</c> of them — and sends them after the
/// result as <c>view_image</c> sends its pictures (<see cref="ToolImageResult"/>), with no file and marked as a PDF's page (gaps 21, 32) so a
/// stored session and a trimmed request name the page instead. <c>find</c> lists the pages holding every word with a line each
/// (<see cref="PdfFind"/>, gap 93), reading the whole text once. <c>share</c> (or a full <c>\\server\share</c> path) reads a PDF on an
/// offered UNC share where it is: the stamp and the bytes under the share's sign-in, the browser outside it.</para>
/// </summary>
public sealed class ReadPdfTool : FileTool
{
    public const string ToolName = "read_pdf";
    public const string PagesArgument = "pages";
    public const string PicturesArgument = "pictures";
    public const string FindArgument = "find";
    public const string ShareArgument = UncTool.ShareArgument;

    /// <summary>The most pages one call reads for its text (the rest are the next call's).</summary>
    public const int PagesPerCall = 40;

    /// <summary>The outline's caps in an answer (gap 91).</summary>
    public const int MaxOutlineLines = 60;

    public const int MaxOutlineChars = 4_000;

    private static readonly JsonElement Schema = SchemaFor(shares: false);
    private static readonly JsonElement ShareSchema = SchemaFor(shares: true);

    private static JsonElement SchemaFor(bool shares) => ToolSchema.Parse($$"""
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "The PDF to read, relative to the working directory{{(shares ? " (or to the share when share is given)" : "")}}." },
            "pages": { "type": "string", "description": "Which pages, by physical page number from 1: \"3\", \"2-5\", \"5-\" (to the end) or \"1,3,7-9\". Leave it out to start at page 1 with the PDF's outline; after a partial read, call again with the pages its header names as next." },
            "pictures": { "type": "boolean", "description": "true also sends the pages themselves as pictures (a scan, a chart, a form, a page with no text), a few per call." },
            "find": { "type": "string", "description": "Words to look for: lists the pages that hold every word, with a line from each, instead of the text. Combine with pages to search only those." }{{(shares ? ShareProperty : "")}}
          },
          "required": ["path"]
        }
        """);

    private const string ShareProperty =
        """
        ,
            "share": { "type": "string", "description": "A UNC share's name (unc_shares lists them) to read the PDF there instead of the working directory; not needed when path is a full \\\\server\\share path under one." }
        """;

    /// <summary>The description. Pinned.</summary>
    public const string BaseDescription =
        "Reads a PDF under the working directory (the user's cwd / current directory): its text page by page, starting with the outline of its bookmarks when no pages are given. " +
        "pages takes physical page numbers (\"3\", \"2-5\", \"5-\", \"1,3,7-9\"); a page's printed number shows beside it when it differs. " +
        "A long PDF comes back in part, its header naming the pages to read next. find lists the pages that hold every word given. " +
        "pictures: true sends the pages as pictures too (for a scan, a chart or a form; it needs a model that sees pictures). read_file cannot read a PDF.";

    private const string ShareDescription = " With share, or a full \\\\server\\share path, it reads a PDF on that UNC share where it is.";

    private readonly PdfReading _reading;
    private readonly Func<AppSettingsData> _effective;
    private readonly UncAccess? _unc;

    /// <param name="files">The working directory.</param>
    /// <param name="reading">The reader and its cache, shared with the rest of the app.</param>
    /// <param name="effective">The settings in force: the picture cap and the offered shares, read at every call.</param>
    /// <param name="unc">The UNC shares' door; null = the working directory alone.</param>
    /// <param name="images">The profile's images folder, read by its full paths; null = the working directory alone.</param>
    public ReadPdfTool(WorkingDirectory files, PdfReading reading, Func<AppSettingsData> effective, UncAccess? unc = null, WorkingDirectory? images = null) : base(files, images)
    {
        _reading = reading ?? throw new ArgumentNullException(nameof(reading));
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
        _unc = unc;
    }

    public override string Name => ToolName;

    public override string Description => SharesOffered ? BaseDescription + ShareDescription : BaseDescription;

    public override JsonElement JsonSchema => SharesOffered ? ShareSchema : Schema;

    private bool SharesOffered => _unc is not null && _unc.IsOffered(_effective());

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!RequirePath(arguments, PathArgument, out string path, out string error))
        {
            return error;
        }

        if (ReadFlag(arguments, PicturesArgument, out string raw) is not { } pictures)
        {
            return FileText.BadBoolean(PicturesArgument, raw);
        }

        string find = ToolArguments.ReadString(arguments, FindArgument).Trim();
        string spec = ToolArguments.ReadString(arguments, PagesArgument).Trim().Trim('"');
        if (find.Length > 0 && pictures)
        {
            return PdfText.FindNoPictures;
        }

        PdfPageRange? range = null;
        if (spec.Length > 0 && !PdfPageRange.TryParse(spec, out range, out string bad))
        {
            return bad;
        }

        var request = new Request(spec, range, pictures, find, ViewImageTool.LimitOf(_effective()));
        string? share = ToolArguments.ReadString(arguments, ShareArgument).Trim() is { Length: > 0 } named ? named : null;
        return share is null && !OnAShare(path)
            ? await ReadLocalAsync(path, request, cancellationToken).ConfigureAwait(false)
            : await ReadOnShareAsync(share, path, request, cancellationToken).ConfigureAwait(false);
    }

    private sealed record Request(string Spec, PdfPageRange? Range, bool Pictures, string Find, int PictureLimit);

    private async Task<object?> ReadLocalAsync(string path, Request request, CancellationToken cancellationToken)
    {
        var files = FilesFor(path);
        if (Stamped(files, path, out var key, out string display) is { } refused)
        {
            return refused;
        }

        return await AnswerAsync(key!, display, token => Task.FromResult(Bytes(files, path)), request, text => text, cancellationToken).ConfigureAwait(false);
    }

    private async Task<object?> ReadOnShareAsync(string? share, string path, Request request, CancellationToken cancellationToken)
    {
        if (!SharesOffered)
        {
            return UncText.NoSharesOffered;
        }

        var target = _unc!.Resolve(share, path, out string relative, out string? error);
        if (target is null)
        {
            return error!;
        }

        var stamped = await _unc.RunAsync(target, write: false, files => (Refused: Stamped(files, relative, out var key, out string display), Key: key, Display: display), cancellationToken).ConfigureAwait(false);
        if (stamped.Error is { } failed)
        {
            return failed;
        }

        var (refused, docKey, shown) = stamped.Value;
        if (refused is not null)
        {
            return UncText.Scoped(refused, target);
        }

        return await AnswerAsync(docKey!, shown, async token =>
        {
            var read = await _unc.RunAsync(target, write: false, files => Bytes(files, relative), token).ConfigureAwait(false);
            return read.Error is { } e ? (null, e) : read.Value;
        }, request, text => UncText.Scoped(text, target), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether a call without a share means one: a full path outside the working directory (and the images folder) while a share is offered (<c>open</c>'s rule).</summary>
    private bool OnAShare(string path)
    {
        string spelled = UncPath.Spell(path, OperatingSystem.IsWindows());
        return UncCatalog.IsAbsolute(spelled) && SharesOffered && (UncPath.IsNetwork(spelled, OperatingSystem.IsWindows()) || FilesFor(spelled).Resolve(spelled, forWrite: false, out _) != FileOutcome.Ok);
    }

    private static string? Stamped(WorkingDirectory files, string path, out PdfDocKey? key, out string display) => PdfReading.Locate(files, path, out key, out display);

    private static (byte[]? Bytes, string? Error) Bytes(WorkingDirectory files, string path) => PdfReading.BytesOf(files, path);

    /// <summary>The read asked of <see cref="PdfReading"/> for the request, then the answer composed from what it holds.</summary>
    private async Task<object?> AnswerAsync(PdfDocKey key, string display, Func<CancellationToken, Task<(byte[]? Bytes, string? Error)>> bytes, Request request, Func<string, string> scope, CancellationToken cancellationToken)
    {
        bool finding = request.Find.Length > 0;
        IReadOnlyList<int>? pages = request.Range?.Resolve(request.Range.First + PdfReading.MaxPagesPerRead - 1);
        var pictures = !request.Pictures ? [] : (pages ?? [.. Enumerable.Range(1, request.PictureLimit)]).Take(request.PictureLimit).ToList();
        var ask = finding
            ? new PdfAsk(pages, [], Outline: false, Labels: true, AllPages: pages is null, MaxPages: PdfReading.MaxPagesPerRead)
            : new PdfAsk(pages?.Take(PagesPerCall).ToList(), pictures, Outline: request.Range is null, Labels: true, MaxPages: PagesPerCall);
        var read = await _reading.ReadAsync(key, display, bytes, ask, cancellationToken).ConfigureAwait(false);
        if (read.Error is { } error)
        {
            return scope(error);
        }

        if (finding)
        {
            return scope(FindAnswer(display, request, read));
        }

        var asked = request.Range?.Resolve(read.PageCount) ?? [.. Enumerable.Range(1, read.PageCount)];
        if (asked.Count == 0)
        {
            return scope(PdfText.PastTheEnd(display, read.PageCount, request.Spec));
        }

        string text = TextAnswer(display, request, read, asked);
        var drawn = read.Pictures.Where(p => p.Png is not null).OrderBy(p => p.Number).ToList();
        if (!request.Pictures)
        {
            return scope(text);
        }

        var wantedPictures = asked.Take(request.PictureLimit).ToList();
        var leftOut = asked.Skip(request.PictureLimit).Take(PagesPerCall).ToList();
        var notDrawn = wantedPictures.Where(n => drawn.All(p => p.Number != n)).ToList();
        var sb = new StringBuilder(text);
        if (drawn.Count > 0)
        {
            sb.Append('\n').Append(PdfText.PicturesNote([.. drawn.Select(p => p.Number)], leftOut));
        }

        if (notDrawn.Count > 0)
        {
            sb.Append('\n').Append(PdfText.NotDrawn(notDrawn));
        }

        string answer = scope(sb.ToString());
        if (drawn.Count == 0)
        {
            return answer;
        }

        var images = drawn.Select(p => new ImageAttachment("", p.Png!, ImageFile.Png, p.Width, p.Height) { PdfPage = display + "#" + p.Number.ToString(CultureInfo.InvariantCulture) }).ToList();
        return new ToolImageResult(answer, images);
    }

    /// <summary>The header, the outline when no pages were given, and the pages' text up to the cap.</summary>
    private static string TextAnswer(string display, Request request, PdfPages read, IReadOnlyList<int> asked)
    {
        var body = new StringBuilder();
        if (request.Range is null && read.Outline is { Count: > 0 } outline)
        {
            body.Append(PdfText.OutlineHeading);
            int shownItems = 0;
            foreach (var item in outline)
            {
                string line = PdfText.OutlineLine(item.Title, item.Level, item.Page);
                if (shownItems >= MaxOutlineLines || body.Length + line.Length > MaxOutlineChars)
                {
                    body.Append('\n').Append(PdfText.OutlineMore);
                    break;
                }

                body.Append('\n').Append(line);
                shownItems++;
            }
        }

        var shown = new List<int>();
        bool anyText = false;
        int budget = WorkingDirectory.MaxReadChars;
        foreach (int page in asked)
        {
            if (!read.Texts.TryGetValue(page, out string? pageText))
            {
                break;   // not read: past this call's pages, or the read stopped short
            }

            string heading = PdfText.PageHeading(page, read.LabelOf(page));
            int room = budget - body.Length - heading.Length - 2;
            if (shown.Count > 0 && pageText.Length > room)
            {
                break;
            }

            body.Append(body.Length > 0 ? "\n" : "").Append(heading);
            if (pageText.Length > 0)
            {
                anyText = true;
                if (pageText.Length > Math.Max(room, 0) && shown.Count == 0)
                {
                    int cut = Math.Max(room, 1000);
                    body.Append('\n').Append(pageText.AsSpan(0, Math.Min(cut, pageText.Length))).Append('\n').Append(PdfText.PageCut(page, Math.Min(cut, pageText.Length), pageText.Length));
                }
                else
                {
                    body.Append('\n').Append(pageText);
                }
            }

            shown.Add(page);
        }

        if (shown.Count > 0 && !anyText)
        {
            body.Append('\n').Append(PdfText.NoTextLayer);
        }

        int? next = shown.Count < asked.Count ? asked[shown.Count] : null;
        if (next is not null && read.Detail.Length > 0 && !read.Texts.ContainsKey(next.Value))
        {
            body.Append('\n').Append(PdfText.StoppedShort(read.Detail));
        }

        string header = shown.Count == 0
            ? PdfText.ReadFailed(display, read.Detail.Length > 0 ? read.Detail : "no page could be read")
            : PdfText.Header(display, shown, read.PageCount, next);
        return body.Length == 0 ? header : header + "\n" + body;
    }

    /// <summary><c>find</c>'s answer: the pages holding every word (the first <see cref="PdfFind.MaxResults"/>), a line from each.</summary>
    private static string FindAnswer(string display, Request request, PdfPages read)
    {
        var words = PdfFind.Words(request.Find);
        var within = request.Range?.Resolve(read.PageCount) ?? [.. Enumerable.Range(1, read.PageCount)];
        var searched = within.Where(read.Texts.ContainsKey).ToList();
        var matches = words.Count == 0 ? [] : searched.Where(n => PdfFind.Matches(read.Texts[n], words)).ToList();
        string stopped = searched.Count < within.Count && read.Detail.Length > 0 ? "\n" + PdfText.StoppedShort(read.Detail) : "";
        if (matches.Count == 0)
        {
            return PdfText.NotFound(display, request.Find, searched.Count, read.PageCount) + stopped;
        }

        var sb = new StringBuilder(PdfText.Found(display, request.Find, matches.Count, searched.Count, read.PageCount));
        foreach (int page in matches.Take(PdfFind.MaxResults))
        {
            sb.Append('\n').Append(PdfText.FoundLine(page, read.LabelOf(page), PdfFind.Context(read.Texts[page], words)));
        }

        if (matches.Count > PdfFind.MaxResults)
        {
            sb.Append('\n').Append(PdfText.FoundMore(matches.Count - PdfFind.MaxResults));
        }

        return sb + stopped;
    }
}
