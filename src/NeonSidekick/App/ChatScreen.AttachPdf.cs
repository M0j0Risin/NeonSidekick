using System.Globalization;
using System.Text;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Anthropic;
using NeonSidekick.Pdf;
using NeonSidekick.UI;
using NeonSidekick.Web;

namespace NeonSidekick.App;

// ── PDFs attached to a message (2026-10-14, the PDF plan's phase 3) ────────

internal sealed partial class ChatScreen
{
    /// <summary>The most pages an attached PDF's text holds; the rest is <c>read_pdf</c>'s.</summary>
    public const int AttachPages = 100;

    /// <summary>The most characters an attached PDF's text holds.</summary>
    public const int AttachChars = 100_000;

    /// <summary>
    /// The most bytes one message's pictures and PDFs may come to as sent (gaps 18 and 51): <c>LLM max images (MB)</c>, the request's own cap,
    /// as base64 carries them; at 0 (no cap) the Anthropic API's request still caps a PDF, so 24 MB. Base64 is four bytes for three.
    /// </summary>
    public static long AttachWireCap(int megabytes) => (megabytes > 0 ? megabytes : PictureBudget.DefaultMegabytes) * 1_000_000L;

    /// <summary>
    /// The input line's <see cref="InputLine.DocumentLoader"/>: a dropped or pasted <c>.pdf</c> read (its size against <see cref="AttachWireCap"/> for an endpoint that takes PDFs, else the reader's cap,
    /// its header), and its text's read started in the background through <c>read_pdf</c>'s reader and cache (gap 17), so the line goes on at
    /// once; the send waits for the read (<see cref="ReadyAttachedPdfsAsync"/>). The read is never cancelled: a token deleted from the line
    /// leaves it to finish into the cache, where a <c>read_pdf</c> of the same file finds it (the plan's gap 28 cancelled it; the cache made
    /// that not worth tracking the draft for).
    /// </summary>
    private (ImageAttachment? Document, string? Error) LoadAttachedPdf(string full)
    {
        try
        {
            var info = new FileInfo(full);
            if (!info.Exists)
            {
                return (null, FileText.Missing(full));
            }

            // A server sent the text alone (a local one, the Claude Code server) is held to what the reader takes, not to the
            // request's byte cap: only the text goes out (2026-10-20, review: a 19 MB PDF was refused as 25 MB of base64 there).
            // The send weighs the line again for the endpoint it goes to (ReadyAttachedPdfsAsync).
            if (!TakesPdf())
            {
                if (info.Length > BrowserPdfReader.MaxBytes)
                {
                    return (null, PdfText.TooBigToAttachAsText(info.Name, info.Length));
                }
            }
            else if (AttachWireCap(_effective().LlmPictureMegabytes) is var cap && (info.Length + 2) / 3 * 4 > cap)
            {
                return (null, PdfText.TooBigToAttach(info.Name, info.Length, cap * 3 / 4));
            }

            byte[] bytes = File.ReadAllBytes(info.FullName);
            if (!PdfFile.HasPdfHeader(bytes))
            {
                return (null, PdfText.NotAPdf(info.Name));
            }

            var key = new PdfDocKey(info.FullName, info.Length, info.LastWriteTimeUtc);
            return (new ImageAttachment(info.FullName, bytes, PdfFile.MediaType, 0, 0) { DocumentText = ReadAttachedAsync(key, info.Name, bytes) }, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, PdfText.ReadFailed(Path.GetFileName(full), ex.Message));
        }
    }

    /// <summary>An attached PDF's text: its first <see cref="AttachPages"/> pages under their headings, up to <see cref="AttachChars"/>. Never throws.</summary>
    private async Task<PdfDocumentText> ReadAttachedAsync(PdfDocKey key, string name, byte[] bytes)
    {
        try
        {
            var pages = await _pdfReading.ReadAsync(key, name, _ => Task.FromResult<(byte[]?, string?)>((bytes, null)), new PdfAsk(null, [], Outline: false, Labels: true, MaxPages: AttachPages), CancellationToken.None).ConfigureAwait(false);
            if (pages.Locked)
            {
                return new PdfDocumentText("", 0, 0, Locked: true);
            }

            if (pages.Error is { } error)
            {
                return new PdfDocumentText("", 0, 0, Problem: error);
            }

            var sb = new StringBuilder();
            int read = 0;
            for (int page = 1; page <= Math.Min(pages.PageCount, AttachPages) && pages.Texts.TryGetValue(page, out string? text); page++)
            {
                string heading = PdfText.PageHeading(page, pages.LabelOf(page));
                if (read > 0 && sb.Length + heading.Length + text.Length > AttachChars)
                {
                    break;
                }

                PdfText.AppendPage(sb, page, pages.LabelOf(page), text.AsSpan(0, Math.Min(text.Length, AttachChars)));

                read++;
            }

            bool anyText = pages.Texts.Values.Any(t => t.Trim().Length > 0);
            return new PdfDocumentText(anyText ? sb.ToString() : "", pages.PageCount, read);
        }
        catch (Exception ex)
        {
            return new PdfDocumentText("", 0, 0, Problem: ex.Message);
        }
    }

    /// <summary>
    /// The message's PDFs ready to send (gaps 17, 39, 43, 51, 84 and 100), or the sentence why not (null with no sentence: the wait was stopped,
    /// its notice printed): each one's text awaited under a spinner and the watch — ESC, and the Web UI's Stop through the
    /// <see cref="KillScopeKind.Work"/> scope <see cref="WaitUnderWatchAsync"/> opens, stop the send and keep the draft — then a locked PDF
    /// refused, one with no text refused for an endpoint that takes text alone, the line's pictures and PDFs held to
    /// <see cref="AttachWireCap"/> together, a long PDF warned of on the Anthropic API, and, for an endpoint that takes text, a PDF's text cut
    /// to half the model's window when it alone would fill more, with a notice and a note in the text naming <c>read_pdf</c>.
    /// </summary>
    private async Task<(IReadOnlyList<ImageAttachment>? Ready, string? Refused)> ReadyAttachedPdfsAsync(string text, IReadOnlyList<ImageAttachment> images, CancellationToken cancellationToken)
    {
        var documents = images.Where(i => i.IsDocument && i.DocumentText is not null).ToList();
        if (documents.Any(d => !d.DocumentText!.IsCompleted))
        {
            bool stopped = await WaitUnderWatchAsync(token => _transcript.WithSpinnerAsync(PdfText.ReadingAttached, async () =>
            {
                await Task.WhenAll(documents.Select(d => d.DocumentText!)).WaitAsync(token).ConfigureAwait(false);
                return true;
            }), KeySource.IsTurnCancel, cancellationToken).ConfigureAwait(false);
            if (stopped)
            {
                _transcript.Notice(PdfText.AttachWaitStopped);
                return (null, null);
            }
        }

        var baseUrl = _session.Endpoint?.BaseUrl;
        bool takesPdf = TakesPdf();
        long total = images.Sum(i => SentBytes(i, takesPdf));
        long cap = AttachWireCap(_effective().LlmPictureMegabytes);
        if (total > cap)
        {
            return (null, PdfText.TooBigToAttach(string.Join(", ", documents.Select(d => Path.GetFileName(d.Path))), total * 3 / 4, cap * 3 / 4));
        }

        int? window = _session.ContextLength?.Tokens;
        var ready = new List<ImageAttachment>(images.Count);
        foreach (var image in images)
        {
            if (!image.IsDocument || image.DocumentText is null)
            {
                ready.Add(image);
                continue;
            }

            string name = Path.GetFileName(image.Path);
            var read = await image.DocumentText.ConfigureAwait(false);
            if (read.Locked)
            {
                return (null, PdfText.AttachedLocked(name));
            }

            if (!takesPdf && read.Empty)
            {
                return (null, PdfText.NoTextForThisServer(name));
            }

            if (ClaudeApi.IsClaudeApi(baseUrl) && read.PageCount > 100)
            {
                _transcript.Warning(PdfText.ManyPagesWarning(name, read.PageCount));
            }

            if (!takesPdf && window is > 0 and var tokens && read.Text.Length / ConversationCompactor.CharsPerToken > tokens / 2)
            {
                // Gap 43: the text alone would fill most of a local server's window; cut to half of it, the rest read_pdf's.
                int keep = tokens / 2 * ConversationCompactor.CharsPerToken;
                var cut = read with { Text = read.Text[..keep] + PdfText.AttachedCut(name) };
                _transcript.Notice(PdfText.AttachedCut(name).Trim());
                ready.Add(image with { DocumentText = Task.FromResult(cut) });
                continue;
            }

            ready.Add(image);
        }

        return (ready, null);
    }

    /// <summary>Whether the chat's endpoint takes a PDF itself (a hosted API); every other one is sent its text (<see cref="PictureBudget.DocumentsAsText"/>).</summary>
    private bool TakesPdf() => ApiKeys.IsHostedApi(_session.Endpoint?.BaseUrl);

    /// <summary>
    /// What one attachment weighs on the wire, as <see cref="PictureBudget.WireBytes"/> weighs it: base64 for a picture and for a PDF sent as
    /// one, the text's bytes for a PDF sent as its text (its read done by the time the send weighs it).
    /// </summary>
    private static long SentBytes(ImageAttachment attachment, bool takesPdf) =>
        !takesPdf && attachment.IsDocument && attachment.DocumentText is { IsCompletedSuccessfully: true } read
            ? Encoding.UTF8.GetByteCount(read.Result.Text)
            : (attachment.Bytes.LongLength + 2) / 3 * 4;
}
