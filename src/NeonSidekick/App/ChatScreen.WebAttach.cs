using System.Globalization;
using NeonSidekick.Camera;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Pdf;
using NeonSidekick.UI;
using NeonSidekick.WebUi;

namespace NeonSidekick.App;

/// <summary>
/// Pictures and PDFs sent from the phone (2026-11-16, plan lucid-roaming-lantern #39). The page's uploads ride its line from the session; as the
/// line goes to its turn (the idle line's take, or a botchat's interjection take), each is saved once into <i>Pasted images folder</i> as
/// <c>phone-yyyyMMdd-HHmmss.&lt;ext&gt;</c> (a picture without its metadata, its orientation kept; a PDF as it came), and the line is built with the
/// labels a sent PC paste ends with, ahead of what was typed: the model's text names the saved files, the › row and the Queue read the bare labels.
/// A save that fails sends a picture all the same, unnamed (the PC's warning names the file; the page's words never a PC path); a PDF can't go
/// without its file and is left off, with a notice at both. A give-back keeps each saved path, so a resend saves no second copy.
/// </summary>
internal sealed partial class ChatScreen
{
    /// <summary>A phone attachment's file name before its extension, as <see cref="Comfy.ComfyStudio.PastedStem"/> is a PC paste's.</summary>
    public static string PhoneStem(DateTimeOffset local) => "phone-" + local.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

    /// <summary>
    /// <paramref name="line"/> (a page's line from its text alone) with its uploads saved and attached: <see cref="SubmittedLine.Text"/> the saved
    /// labels then the typed text, <see cref="SubmittedLine.Draft"/> the typed text alone, <see cref="SubmittedLine.Label"/> and
    /// <see cref="SubmittedLine.Shown"/> the bare labels then the typed text; null when nothing is left to send (every PDF left off, nothing typed).
    /// On the screen's task.
    /// </summary>
    private SubmittedLine? PhoneLine(SubmittedLine line, IReadOnlyList<WebHeldAttachment> attachments, string? page)
    {
        var images = new List<ImageAttachment>(attachments.Count);
        var kept = new List<WebHeldAttachment>(attachments.Count);
        var sent = new List<string>(attachments.Count);
        var bare = new List<string>(attachments.Count);
        int number = 0;
        foreach (var attachment in attachments)
        {
            number++;
            var held = attachment.Saved is { } before && File.Exists(before) ? attachment : SavePhone(attachment, number, page);
            kept.Add(held);
            if (held.Kind == WebAttachKind.Picture && held.Picture is { } picture)
            {
                images.Add(held.Saved is { } path ? picture with { Path = path } : picture);
                sent.Add(held.Saved is { } savedAs ? PasteBlocks.SavedLabel(number, savedAs) : PasteBlocks.ImageLabel(number));
                bare.Add(PasteBlocks.ImageLabel(number));
                continue;
            }

            if (held.Saved is not { } full)
            {
                continue;   // its save failed: said already, at the PC and on the page
            }

            var (document, error) = LoadAttachedPdf(full);
            if (document is null)
            {
                string why = error ?? FileText.Missing(full);
                DiagnosticLog.Warn(WebUiCategory, "A phone PDF was left off: " + why);
                _transcript.WarningAtPc(why);
                SayToPage(page, PhonePdfWords(why, full, number));
                continue;
            }

            string where = _files.Contains(full) && _files.Resolve(full, forWrite: false, out string inside) == FileOutcome.Ok ? _files.Relative(inside) : full;
            images.Add(document);
            sent.Add(PdfText.DocumentSentLabel(number, where));
            bare.Add(PdfText.DocumentLabel(number, Path.GetFileName(full)));
        }

        string typed = line.Text;
        if (images.Count == 0 && typed.Trim().Length == 0)
        {
            return null;
        }

        string text = Joined(sent, typed);
        string shown = Joined(bare, typed);
        string label = shown.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');
        return line with { Text = text, Images = images, Label = label, Shown = shown, WebAttachments = kept };

        static string Joined(List<string> labels, string typed) =>
            labels.Count == 0 ? typed : typed.Length == 0 ? string.Join(' ', labels) : string.Join(' ', labels) + " " + typed;
    }

    /// <summary>
    /// <paramref name="attachment"/> saved into <i>Pasted images folder</i>, its full path in <see cref="WebHeldAttachment.Saved"/>; as it was when
    /// the write failed (warned at the PC with the reason, on the page in its own words).
    /// </summary>
    private WebHeldAttachment SavePhone(WebHeldAttachment attachment, int number, string? page)
    {
        byte[] bytes;
        string extension;
        if (attachment.Kind == WebAttachKind.Pdf)
        {
            bytes = attachment.Bytes;   // as it came: its header was checked
            extension = ".pdf";
        }
        else if (Images.MetadataStripper.Strip(attachment.Bytes, Images.MetadataStripper.ExifOrientation(attachment.Bytes) ?? 1).Result is { } stripped)
        {
            // The phone's own bytes, its GPS and EXIF gone, its orientation written back (Strip's default of 1 would save a portrait sideways).
            bytes = stripped.Bytes;
            extension = ImageFile.ExtensionFor(attachment.MediaType, attachment.Bytes);
        }
        else
        {
            // A container the stripper doesn't walk (a BMP, a HEIC a Mac decoded) or one whose walk fails: the loader's re-encode, stripped already.
            var picture = attachment.Picture!;
            bytes = picture.Bytes;
            extension = ImageFile.ExtensionFor(picture.MediaType, picture.Bytes);
        }

        string stem = CameraCapture.Under(CameraCapture.OutputFolder(_effective().PastedImagesFolder), PhoneStem(_time.GetLocalNow()));
        var (written, why) = Images.ImageOutput.WriteNumbered(_images, stem, extension, bytes);
        bool pdf = attachment.Kind == WebAttachKind.Pdf;
        if (why is not null)
        {
            string label = pdf ? "[PDF #" + number.ToString(CultureInfo.InvariantCulture) + "]" : PasteBlocks.ImageLabel(number);
            DiagnosticLog.Warn(WebUiCategory, label + " from the phone not saved: " + why);
            _transcript.WarningAtPc(pdf ? WebUiText.PhonePdfNotSavedWarning(number, why) : PastedNotSavedWarning(number, why));
            SayToPage(page, WebUiText.PhoneNotSaved(number, pdf));
            return attachment;
        }

        DiagnosticLog.Debug(WebUiCategory, "A phone " + (pdf ? "PDF" : "picture") + " saved as " + written.Relative);
        return attachment with { Saved = written.Relative };
    }

    /// <summary>
    /// What the page reads for a phone PDF left off: the wire cap's and the header's words as they are (they name the <c>phone-…</c> file
    /// alone), anything else (a missing file, a read's system message, which may name the PC's path) <see cref="WebUiText.PhoneNotSaved"/>.
    /// </summary>
    private string PhonePdfWords(string error, string full, int number)
    {
        var info = new FileInfo(full);
        bool safe = error == PdfText.NotAPdf(info.Name)
            || (info.Exists && (error == PdfText.TooBigToAttachAsText(info.Name, info.Length)
                || error == PdfText.TooBigToAttach(info.Name, info.Length, AttachWireCap(_effective().LlmPictureMegabytes) * 3 / 4)));
        return safe ? error : WebUiText.PhoneNotSaved(number, pdf: true);
    }

    /// <summary>
    /// A page's line refused before its turn (no server, a PDF the send-time check refused, an embedded model without vision): its typed text and
    /// its chips back to its page's draft, the reason said there too, as a withdrawn line goes; false for any other line (a PC line, an app's pass),
    /// which the caller handles as before.
    /// </summary>
    private bool PutBackPageLine(string text, string? error, bool pass)
    {
        if (!_sentFromWeb || pass)
        {
            return false;
        }

        WithdrawWebLine(_sentDraft ?? PasteBlocks.WithoutSavedPaths(text), _sentWebAttachments);
        if (error is not null)
        {
            SayToPage(_sentFromWebPage?.Page ?? _sentWebAttachments?.FirstOrDefault()?.Page, error);
        }

        return true;
    }

    /// <summary>A notice on <paramref name="page"/> alone (its chip row's message); nothing without a page.</summary>
    private void SayToPage(string? page, string text)
    {
        if (page is not null)
        {
            _webUi.SayToPage(page, text);
        }
    }
}
