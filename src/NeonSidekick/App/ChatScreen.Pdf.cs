using NeonSidekick.Pdf;

namespace NeonSidekick.App;

// ── PDFs (2026-10-03) ─────────────────────────────────────────────────────

internal sealed partial class ChatScreen
{
    /// <summary>
    /// <c>/pdf …</c> (2026-10-03): <see cref="PdfCommand.RunAsync"/> under a spinner, its sentence a notice (a failure's an
    /// error). The user's own hand: no switch asked, nothing added to the conversation. Since 2026-10-14 its edit verbs too
    /// (<c>merge</c>, <c>extract</c>, <c>delete</c>, <c>rotate</c>), through <c>pdf_edit</c>'s <see cref="PdfEditing"/>, under their own spinner.
    /// </summary>
    private async Task HandlePdfAsync(string args, CancellationToken cancellationToken)
    {
        string result;
        try
        {
            string spinner = PdfCommand.ParseEdit(args, _pdfEditing.IsFile).Request is null ? PdfText.Working : PdfText.Editing;
            result = await _transcript.WithSpinnerAsync(spinner, () => PdfCommand.RunAsync(_pdf, args, () => _log.LastReply, cancellationToken, _pdfEditing)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (result.StartsWith("Error:", StringComparison.Ordinal))
        {
            _transcript.Error(result);
        }
        else
        {
            _transcript.Notice(result);
        }
    }

    /// <summary>
    /// <c>/docx …</c> (2026-10-29, the Office writing plan): <see cref="Office.DocxCommand.RunAsync"/> under a spinner, its sentence a notice (a
    /// failure's an error), as <see cref="HandlePdfAsync"/>. The user's own hand: no switch asked, nothing added to the conversation; it runs in
    /// plan mode and with File tools off. Its converter is built per call over the working folder as it is now, and the last reply read here, on
    /// the screen's thread (2026-10-30, review F17: the shared converter's folder was read lazily on the pool, so a folder changed meanwhile
    /// took the copy; the model's <c>convert_to_docx</c> keeps <see cref="_docx"/>, as a turn runs in one folder).
    /// </summary>
    private async Task HandleDocxAsync(string args, CancellationToken cancellationToken)
    {
        var converter = new Office.DocxConverter(_files.PinnedAt(_files.Root), _time);
        string? lastReply = _log.LastReply;
        string result;
        try
        {
            result = await _transcript.WithSpinnerAsync(Office.OfficeText.Working, () => Office.DocxCommand.RunAsync(converter, args, () => lastReply, cancellationToken)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (result.StartsWith("Error:", StringComparison.Ordinal))
        {
            _transcript.Error(result);
        }
        else
        {
            _transcript.Notice(result);
        }
    }
}
