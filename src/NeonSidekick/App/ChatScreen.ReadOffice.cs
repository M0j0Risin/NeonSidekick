using NeonSidekick.Files;
using NeonSidekick.Office;
using NeonSidekick.UI;

namespace NeonSidekick.App;

// ── /read of a Word document or a presentation (2026-10-28, the Office plan) ─────

internal sealed partial class ChatScreen
{
    /// <summary>
    /// The display path of the Office file <paramref name="path"/> names in the working directory (by its extension, the one
    /// <see cref="OfficeFile"/> list), or null for anything else: asked before the text read, whose binary probe would answer with
    /// <c>read_office</c>'s model hint and whose 8 MB cap would refuse a big document.
    /// </summary>
    private string? OfficeAt(string path) =>
        OfficeFile.IsOfficePath(path) && _files.Resolve(path, forWrite: false, out string full) == FileOutcome.Ok && File.Exists(full) ? _files.Relative(full) : null;

    /// <summary>
    /// A Word document's or a presentation's text for <c>/read</c> (<see cref="OfficeReader.ReadAloud"/>: plain paragraphs, slide titles
    /// as sentences, no markers), cut at <see cref="WorkingDirectory.MaxReadChars"/> as a text file is; a workbook refused with a short
    /// reason. Read under a spinner and the watch as <see cref="PdfAloudAsync"/> is, so ESC and the Web UI's Stop stop it. The text, whether
    /// it was cut, or the error to print (null with no text: stopped, the notice printed).
    /// </summary>
    private async Task<(string? Text, bool Cut, string? Error)> OfficeAloudAsync(string path, CancellationToken cancellationToken)
    {
        (string? Text, bool Cut, string? Error) result = (null, false, null);
        bool stopped = await WaitUnderWatchAsync(token => _transcript.WithSpinnerAsync(OfficeText.Reading, async () =>
        {
            result = await Task.Run(() => OfficeReader.ReadAloud(_files, path, token), token).ConfigureAwait(false);
            return true;
        }), KeySource.IsTurnCancel, cancellationToken).ConfigureAwait(false);
        if (stopped)
        {
            _transcript.Notice(OfficeText.ReadAloudStopped);
            return (null, false, null);
        }

        return result;
    }
}
