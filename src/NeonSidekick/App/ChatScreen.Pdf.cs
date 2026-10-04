using NeonSidekick.Pdf;

namespace NeonSidekick.App;

// ── PDFs (2026-10-03) ─────────────────────────────────────────────────────

internal sealed partial class ChatScreen
{
    /// <summary>
    /// <c>/pdf …</c> (2026-10-03): <see cref="PdfCommand.RunAsync"/> under a spinner, its sentence a notice (a failure's an
    /// error). The user's own hand: no switch asked, nothing added to the conversation.
    /// </summary>
    private async Task HandlePdfAsync(string args, CancellationToken cancellationToken)
    {
        string result;
        try
        {
            result = await _transcript.WithSpinnerAsync(PdfText.Working, () => PdfCommand.RunAsync(_pdf, args, () => _log.LastReply, cancellationToken)).ConfigureAwait(false);
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
