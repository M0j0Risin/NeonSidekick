using NeonSidekick.Files;
using NeonSidekick.Pdf;
using NeonSidekick.UI;

namespace NeonSidekick.App;

// ── /read of a PDF (2026-10-14, the PDF plan's gaps 7, 38, 89 and 100) ─────

internal sealed partial class ChatScreen
{
    /// <summary>The most pages one <c>/read</c> of a PDF reads for its text: the speech holds <see cref="WorkingDirectory.MaxReadChars"/> at most anyway.</summary>
    public const int ReadAloudPages = 40;

    /// <summary>
    /// A PDF's text for <c>/read</c>: its first <see cref="ReadAloudPages"/> pages through <c>read_pdf</c>'s reader and cache
    /// (<see cref="PdfReading"/>; a looped <c>/read</c> of one reads it once, then speaks from the cache), joined back into sentences
    /// (<see cref="PdfReflow"/>) and cut at <see cref="WorkingDirectory.MaxReadChars"/> as a text file is. The read runs under a spinner and
    /// the watch (<see cref="WaitUnderWatchAsync"/>): ESC stops it, and it is a <see cref="KillScopeKind.Work"/> scope, so the Web UI's Stop
    /// does too (gap 100). The text, whether it was cut, or the error to print (null with no text: stopped, the notice printed).
    /// </summary>
    private async Task<(string? Text, bool Cut, string? Error)> PdfAloudAsync(string path, CancellationToken cancellationToken)
    {
        if (PdfReading.Locate(_files, path, out var key, out string display) is { } refused)
        {
            return (null, false, refused);
        }

        PdfPages? pages = null;
        bool stopped = await WaitUnderWatchAsync(token => _transcript.WithSpinnerAsync(PdfText.ReadingAloudLabel, async () =>
        {
            pages = await _pdfReading.ReadAsync(key!, display, _ => Task.FromResult(PdfReading.BytesOf(_files, path)), new PdfAsk(null, [], Outline: false, Labels: false, MaxPages: ReadAloudPages), token).ConfigureAwait(false);
            return true;
        }), KeySource.IsTurnCancel, cancellationToken).ConfigureAwait(false);
        if (stopped || pages is null)
        {
            _transcript.Notice(PdfText.ReadAloudStopped);
            return (null, false, null);
        }

        if (pages.Error is { } error)
        {
            return (null, false, error);
        }

        var texts = new List<string>();
        for (int page = 1; page <= Math.Min(pages.PageCount, ReadAloudPages) && pages.Texts.TryGetValue(page, out string? text); page++)
        {
            texts.Add(text);
        }

        string prose = PdfReflow.Reflow(texts);
        bool cut = texts.Count < pages.PageCount || prose.Length > WorkingDirectory.MaxReadChars;
        return (prose.Length > WorkingDirectory.MaxReadChars ? prose[..WorkingDirectory.MaxReadChars] : prose, cut, null);
    }
}
