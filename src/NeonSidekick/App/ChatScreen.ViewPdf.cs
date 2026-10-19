using NeonSidekick.Files;
using NeonSidekick.Viewer;

namespace NeonSidekick.App;

// ── /view of a PDF (2026-10-14, the PDF plan's phase 4a) ───────────────────

internal sealed partial class ChatScreen
{
    /// <summary>
    /// <c>/view report.pdf</c>: the PDF shown in the Browser window's own PDF viewer — a copy staged on the window's virtual host
    /// (<see cref="PdfView.Stage"/>), the window opened through the one open every caller uses (<see cref="OpenInBrowser"/>, no notice of its
    /// own: its "close it to hand its cookies" line is a site's, gap 59) — and where the window cannot take it (a Mac before macOS 14 or
    /// over SSH, no WebView2 runtime, a bot check waiting in it, the copy refused; "macOS" alone until 2026-10-18, mac-pass2 gave a Mac the
    /// window) the file goes to its own program, as <c>/open</c> sends one (Preview on a Mac). A sandbox
    /// path, as every <c>/view</c>. Everything through the flow sink, so it is safe under a reply. Any thread.
    /// </summary>
    private void OpenPdf(string path)
    {
        var outcome = _files.Resolve(path.Trim().Trim('"'), forWrite: false, out string full);
        if (outcome != FileOutcome.Ok)
        {
            _flow.Error(FileText.Error(outcome, path, "view"));
            return;
        }

        string shown = _files.Relative(full);
        if (!File.Exists(full))
        {
            _flow.Error(FileText.Error(FileOutcome.Missing, shown, "view"));
            return;
        }

        string? reason = _browserWindow is null ? ViewerText.NoBrowserWindow : _browserWindow.Unavailable();
        if (reason is null)
        {
            try
            {
                OpenInBrowser(PdfView.Stage(full), sink: null);
                _flow.Notice(ViewerText.PdfOpened(shown));
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                reason = ex.Message;
            }
        }

        _openFile(full);
        _flow.Notice(ViewerText.PdfInDefaultApp(shown, reason));
    }
}
