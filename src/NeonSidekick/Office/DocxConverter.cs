using System.Diagnostics;
using System.Globalization;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Pdf;
using NeonSidekick.Printing;
using NeonSidekick.UI.Markdown;

namespace NeonSidekick.Office;

/// <summary>
/// What to make a Word document of (2026-10-29, the Office writing plan, <see cref="PdfRequest"/>'s twin): exactly one of a sandbox
/// <paramref name="Path"/> (Markdown or text) or <paramref name="Markdown"/> text (the reply, the model's own) under <paramref name="Title"/>;
/// where it goes (<paramref name="To"/>: a file, a folder, or blank for beside the source), whether it may replace a file there, and the page.
/// <paramref name="ByUser"/>: the user's own <c>/docx</c>, whose write takes the app's pass (<see cref="WriteGuard.AppWrites"/>);
/// <paramref name="FromReply"/>: its <c>reply</c>, the Markdown being the last reply (named and said as "Reply" when it has no heading).
/// </summary>
public sealed record DocxRequest(string? Path = null, string? Markdown = null, string? Title = null, string? To = null, bool Overwrite = false, bool Landscape = false, PdfPaperSize? Paper = null, bool ByUser = false, bool FromReply = false);

/// <summary>
/// The one home of a Word conversion (2026-10-29, the Office writing plan, <see cref="PdfConverter"/>'s twin): <c>convert_to_docx</c>,
/// <c>/docx</c> and headless all come through here, so they can't drift. The source is read as <c>convert_to_pdf</c> reads text (whole,
/// up to <see cref="PrintText.MaxTextBytes"/>, never windowed); Markdown is parsed by <see cref="MarkdigParser"/>, a <c>.txt</c> split into
/// paragraphs on blank lines with each line break kept. The name follows <see cref="OutputNames"/> (beside the source, a <c>.neon</c>
/// source at the top, text as <c>title-yyyy-MM-dd-HHmm.docx</c>); an unnamed default never replaces a file. Pictures come from the sandbox
/// only (<see cref="SandboxPictures"/>, the PDF's finder) or a <c>data:</c> address, each through <see cref="WordPicture.From"/>, held to
/// <see cref="ImageFile.MaxFileBytes"/> each and <see cref="PdfConverter.MaxInlineBytes"/> in all; past the second the conversion is refused,
/// never left short. Needs no browser, so it is never null. Every failure is an <c>Error:</c> sentence, never a throw.
/// </summary>
public sealed class DocxConverter
{
    public const string Extension = ".docx";

    private readonly WorkingDirectory _files;
    private readonly TimeProvider _time;
    private readonly IMarkdownParser _parser = new MarkdigParser();

    public DocxConverter(WorkingDirectory files, TimeProvider? time = null)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Whether <paramref name="path"/> is a source a Word document is made from: Markdown as <c>/print</c> judges it, or <c>.txt</c>.</summary>
    public static bool IsSource(string path) =>
        PrintService.IsMarkdown(path) || System.IO.Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase);

    public string Convert(DocxRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Path) == (request.Markdown is null))
        {
            return OfficeText.OneDocumentSource;
        }

        // The target's name judged before the source is read and parsed (2026-10-29, review P5-13).
        if (OutputNames.Refusal(request.To, Extension, _files.IsExistingDirectory) is { } refusal)
        {
            return refusal;
        }

        var clock = Stopwatch.StartNew();
        string? given = string.IsNullOrWhiteSpace(request.Title) ? null : request.Title.Trim();
        string display;
        string? sourceFull = null;
        string text;
        if (request.Markdown is not null)
        {
            display = request.FromReply ? PrintText.ReplyTitle : given ?? OfficeText.YourMarkdown;
            text = request.Markdown;
        }
        else if (Read(request.Path!, out sourceFull, out display, out string? error) is { } read)
        {
            text = read;
        }
        else
        {
            return error!;
        }

        bool markdown = sourceFull is null || PrintService.IsMarkdown(sourceFull);
        var document = markdown ? _parser.Parse(text) : Paragraphs(text);
        if (document.Blocks.Count == 0)
        {
            return OfficeText.EmptyDocument(display);
        }

        cancellationToken.ThrowIfCancellationRequested();
        // One rule for every source (2026-10-29, review P2-9, P5-6, P5-7, P7-3): a given title heads the pages, else the first # heading;
        // Markdown text is named by it too (a file keeps its own name), and said by it when it has no title. The reply's text is "Reply"
        // and named reply-… without one.
        string? heading = WordWriter.FirstHeading(document);
        string? header = given ?? heading;
        string? title = header ?? (sourceFull is null ? null : System.IO.Path.GetFileNameWithoutExtension(sourceFull));
        if (request.Markdown is not null && !request.FromReply && given is null && heading is not null)
        {
            display = heading;
        }

        string? sourceRelative = sourceFull is null ? null : display;
        string target = OutputNames.For(request.To, sourceRelative, null, sourceFull is null ? header : null, _time.GetLocalNow(), _files.IsExistingDirectory, Extension,
            fallbackStem: request.FromReply ? OfficeText.ReplyStem : OfficeText.DocumentStem);
        // An unnamed default never replaces a file (convert_to_pdf's rule, review R2): the first free name, and overwrite is for a named to
        // alone; the user's own /docx with overwrite still replaces its default file.
        bool named = !OutputNames.IntoFolder(request.To, _files.IsExistingDirectory);
        bool overwrite = (named || request.ByUser) && request.Overwrite;
        if (!named && !overwrite && _files.Resolve(target, forWrite: false, out string wanted) == FileOutcome.Ok)
        {
            target = _files.Relative(WorkingDirectory.FirstFree(wanted));
        }

        PendingWrite? pending = null;
        var begun = request.ByUser ? WriteGuard.AsApp(() => _files.BeginWrite(target, overwrite, out pending)) : _files.BeginWrite(target, overwrite, out pending);
        if (pending is null)
        {
            return FileText.Error(begun.Outcome, begun.Relative, "write", begun.Detail);
        }

        using (pending)
        {
            try
            {
                string folder = sourceFull is null ? _files.Root : System.IO.Path.GetDirectoryName(sourceFull)!;
                var pictures = new SandboxPictures(_files, folder, PdfConverter.MaxInlineBytes);
                var page = new WordPage(request.Paper ?? PdfPaper.Default, request.Landscape);
                var stats = WordWriter.Write(pending.Stream, document, page, header, title, address => Picture(pictures, address, cancellationToken), _time, cancellationToken);
                if (pictures.OverBudget)
                {
                    return OfficeText.PicturesTooLarge((int)(PdfConverter.MaxInlineBytes / 1_000_000));
                }

                var result = pending.Commit();
                if (result.Outcome != FileOutcome.Ok)
                {
                    return OfficeText.WriteFailed(result);
                }

                DiagnosticLog.Debug(OfficeText.Category, $"docx {(result.Replaced ? "replaced" : "made")} {result.Relative}: {stats.Pictures.ToString(CultureInfo.InvariantCulture)} pictures, {result.Bytes.ToString(CultureInfo.InvariantCulture)} bytes, {clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)} ms");
                return OfficeText.Document(result, display, stats.Pictures, stats.Missing);
            }
            catch (Exception ex) when (WorkingDirectory.IsFileFailure(ex) || ex is System.Xml.XmlException)
            {
                return FileText.CouldNot(OfficeText.MakeDocumentVerb, display, ex.Message);
            }
        }
    }

    /// <summary>The source file's text, or null with the refusal: resolved in the sandbox, Markdown or text, within the size cap, decoded whole.</summary>
    private string? Read(string path, out string? full, out string display, out string? error)
    {
        string given = path.Trim();
        display = given;
        error = null;
        var outcome = _files.Resolve(given, forWrite: false, out string resolved);
        full = resolved;
        if (outcome != FileOutcome.Ok)
        {
            error = FileText.Error(outcome, given, "read");
            return null;
        }

        if (Directory.Exists(resolved))
        {
            error = FileText.Error(FileOutcome.IsDirectory, given, "read");
            return null;
        }

        if (!File.Exists(resolved))
        {
            error = FileText.Error(FileOutcome.Missing, given, "read");
            return null;
        }

        display = _files.Relative(resolved);
        if (!IsSource(resolved))
        {
            error = OfficeText.NotADocumentSource(display);
            return null;
        }

        try
        {
            if (new FileInfo(resolved).Length > PrintText.MaxTextBytes)
            {
                error = OfficeText.DocumentTooLarge(display);
                return null;
            }

            return WorkingDirectory.Decode(RegularFiles.ReadAllBytes(resolved), out _);
        }
        catch (Exception ex) when (WorkingDirectory.IsFileFailure(ex))
        {
            error = FileText.CouldNot("read", display, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// A text file as paragraphs: split on blank lines, each line break inside one kept, so a poem, an address or a log reads as written.
    /// </summary>
    public static MarkdownDocument Paragraphs(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var blocks = new List<MarkdownBlock>();
        var lines = new List<string>();
        foreach (string raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            if (raw.Trim().Length == 0)
            {
                Flush();
                continue;
            }

            lines.Add(raw.TrimEnd());
        }

        Flush();
        return new MarkdownDocument(blocks);

        void Flush()
        {
            if (lines.Count == 0)
            {
                return;
            }

            var inlines = new List<MarkdownInline>();
            foreach (string line in lines)
            {
                if (inlines.Count > 0)
                {
                    inlines.Add(new HardBreak());
                }

                inlines.Add(new TextRun(line));
            }

            blocks.Add(new ParagraphBlock(inlines));
            lines.Clear();
        }
    }

    /// <summary>Whether a picture file's name is one Word embeds (PNG, JPEG, GIF); <see cref="WordPicture.From"/> judges the bytes after.</summary>
    private static bool IsEmbeddable(string full) =>
        Images.ImageFormats.ByExtension(full) is { } format && (format == Images.ImageFormats.Png || format == Images.ImageFormats.Jpeg || format == Images.ImageFormats.Gif);

    /// <summary>
    /// A picture's address → the picture, or null for the placeholder: a <c>data:image/png|jpeg|gif;base64,…</c> address decoded and counted
    /// against the same caps (bad base64 is the placeholder), anything else found in the sandbox by <paramref name="pictures"/>. Nothing is
    /// fetched from the web.
    /// </summary>
    private static WordPicture? Picture(SandboxPictures pictures, string address, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (pictures.OverBudget)
        {
            return null;
        }

        byte[]? bytes;
        if (address.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            bytes = DataPicture(address);
            if (bytes is null || !pictures.Count(bytes.LongLength))
            {
                return null;
            }
        }
        else
        {
            bytes = pictures.Bytes(address, IsEmbeddable);
        }

        return bytes is null ? null : WordPicture.From(bytes);
    }

    /// <summary>A <c>data:</c> picture's bytes: PNG, JPEG or GIF in base64, no bigger than a picture's cap; null otherwise.</summary>
    public static byte[]? DataPicture(string address)
    {
        ArgumentNullException.ThrowIfNull(address);
        int comma = address.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0)
        {
            return null;
        }

        string head = address[..comma].ToLowerInvariant();
        if (head is not ("data:image/png;base64" or "data:image/jpeg;base64" or "data:image/jpg;base64" or "data:image/gif;base64"))
        {
            return null;
        }

        string body = address[(comma + 1)..].Trim();
        if (body.Length / 4L * 3 > ImageFile.MaxFileBytes)
        {
            return null;
        }

        var buffer = new byte[body.Length / 4 * 3 + 3];
        return System.Convert.TryFromBase64String(body, buffer, out int written) ? buffer[..written] : null;
    }
}
