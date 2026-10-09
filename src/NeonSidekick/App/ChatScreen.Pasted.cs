using System.Globalization;
using NeonSidekick.Camera;
using NeonSidekick.Comfy;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.UI;

namespace NeonSidekick.App;

/// <summary>
/// The clipboard's pictures saved (2026-10-09, the user's ask: every picture kept under the profile's <c>images</c> folder, out of the
/// working directory). A picture pasted from the clipboard lived in the input line's store alone until then — written only as
/// <c>generate_image</c>'s input (into the ComfyUI output folder's <c>.pasted</c>) or into the temp folder for a double-click. Now it
/// is written once, when its message is sent (the user's pick over at paste time: a paste deleted from the draft never reaches the
/// disk), into the <c>Pasted images folder</c> as <c>pasted-yyyyMMdd-HHmmss.png</c> (<see cref="ComfyStudio.PastedStem"/>, a
/// <c>-2</c>… on a clash), its own bytes at full size; the sent text's <c>[Image #N]</c> becomes <c>[Image #N, saved as &lt;path&gt;]</c>
/// (<see cref="PasteBlocks.SavedLabel"/>, the user's pick) so the picture tools can reach the file. A dropped file keeps its own and is
/// not copied. A resend, <c>/rewind</c> or a <c>generate_image</c> input reuses the file while it is there. A write the folder
/// refuses sends the picture all the same, unnamed, with a warning.
/// </summary>
internal sealed partial class ChatScreen
{
    /// <summary>The warning when a pasted picture could not be saved: it is sent all the same, with no path. Pinned.</summary>
    public static string PastedNotSavedWarning(int number, string why) =>
        $"{PasteBlocks.ImageLabel(number)} was not saved ({why}); it is sent without a file.";

    /// <summary>
    /// <paramref name="text"/> and <paramref name="images"/> with each clipboard picture among them saved (<see cref="SavePasted"/>): the
    /// attachment's path the file's, the text's first <c>[Image #N]</c> naming it. Pictures that are not the line's pastes pass untouched.
    /// </summary>
    private (string Text, IReadOnlyList<ImageAttachment> Images) SavePastedPictures(string text, IReadOnlyList<ImageAttachment> images)
    {
        if (images.Count == 0 || _input?.Pastes is not { } pastes)
        {
            return (text, images);
        }

        ImageAttachment[]? changed = null;
        for (int i = 0; i < images.Count; i++)
        {
            if (pastes.NumberOf(images[i]) is not int number || SavePasted(number) is not { } saved)
            {
                continue;
            }

            changed ??= images.ToArray();
            changed[i] = images[i] with { Path = saved };
            string label = PasteBlocks.ImageLabel(number);
            int at = text.IndexOf(label, StringComparison.Ordinal);
            if (at >= 0)
            {
                text = string.Concat(text.AsSpan(0, at), PasteBlocks.SavedLabel(number, saved), text.AsSpan(at + label.Length));
            }
        }

        return (text, changed ?? images);
    }

    /// <summary>
    /// Clipboard picture <paramref name="number"/> saved into the <c>Pasted images folder</c> once, its full path; the file it was saved
    /// as before while that is still there. Null for a picture that is no clipboard paste (a dropped file) and for a write refused —
    /// that one warned through the flow, so it waits for a running reply. Any thread.
    /// </summary>
    private string? SavePasted(int number)
    {
        if (_input?.Pastes is not { } pastes || !pastes.IsClipboard(number))
        {
            return null;
        }

        if (pastes.SavedPath(number) is { } before && File.Exists(before))
        {
            return before;
        }

        if (pastes.Original(number) is not { } picture)
        {
            return null;
        }

        string stem = CameraCapture.Under(CameraCapture.OutputFolder(_effective().PastedImagesFolder), ComfyStudio.PastedStem(_time.GetLocalNow()));
        string extension = ImageFile.ExtensionOf(picture.Bytes);
        for (int n = 1; ; n++)
        {
            string relative = n == 1 ? stem + extension : stem + "-" + n.ToString(CultureInfo.InvariantCulture) + extension;
            var written = _images.WriteBytes(relative, picture.Bytes, overwrite: false);
            if (written.Outcome == FileOutcome.Exists && n < 100)
            {
                continue;
            }

            if (written.Outcome != FileOutcome.Ok)
            {
                string why = FileText.Error(written.Outcome, relative, "write", written.Detail);
                DiagnosticLog.Warn("Paste", $"{PasteBlocks.ImageLabel(number)} not saved: {why}");
                _flow.Warning(PastedNotSavedWarning(number, why));
                return null;
            }

            pastes.MarkSaved(number, written.Relative);
            DiagnosticLog.Debug("Paste", $"{PasteBlocks.ImageLabel(number)} saved as {written.Relative}");
            return written.Relative;
        }
    }

    /// <summary>
    /// A pasted picture for <c>generate_image</c>'s <c>[Image #N]</c> input (ComfyStudio's <c>pasted</c> hook): a clipboard picture saved
    /// first when its message was not sent yet, so the input and the later message share one file; a dropped file as it is.
    /// </summary>
    private PastedPicture? PastedForComfy(int number)
    {
        if (_input?.Pastes is not { } pastes)
        {
            return null;
        }

        SavePasted(number);
        return pastes.Original(number);
    }
}
