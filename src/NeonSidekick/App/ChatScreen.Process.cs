using NeonSidekick.Shell;
using NeonSidekick.UI;
using NeonSidekick.Viewer;

namespace NeonSidekick.App;

// ── /process: the background processes and their window (2026-10-05) ─────────

internal sealed partial class ChatScreen
{
    /// <summary>
    /// <c>/process</c> bare: every session of the registry on <see cref="ProcessMenu"/>'s pane (since later on 2026-10-05, the
    /// user's ask: the info pane's read-only list until then), where Enter or a double-click opens one in the window and the kill
    /// button stops one; none yet is its line through <paramref name="sink"/>. A pane under a reply too. Without the pane (a
    /// redirected console) the lines and how to open one, as notices.
    /// </summary>
    private Task ListProcessesAsync(INoticeSink sink, CancellationToken cancellationToken)
    {
        if (_pane.Enabled)
        {
            // The menu reads the list and says NoneYet itself.
            return new ProcessMenu(_processes, sink, _menuPane, OpenProcessWindow).ShowAsync(cancellationToken);
        }

        var sessions = _processes.List();
        if (sessions.Count == 0)
        {
            sink.Notice(ProcessWindowText.NoneYet);
            return Task.CompletedTask;
        }

        var lines = new List<string> { ProcessMenu.Caption(sessions), "" };
        lines.AddRange(sessions.Select(ProcessWindowText.Row));
        lines.Add("");
        lines.Add(ProcessWindowText.ListHint);
        return ShowLinesAsync(ProcessWindowText.PaneLabel, lines, sink, cancellationToken);
    }

    /// <summary>
    /// <c>/process &lt;id&gt;</c>: the session the id (or a unique start of it) names shown in the process window, the open window
    /// switched to it; Ctrl+K twice there stops it through <see cref="ProcessRegistry.StopByUser"/>, whose exit alert is the chat's
    /// line. No window here (tests, not Windows) is <see cref="ProcessWindowText.Unavailable"/>. Quick under a reply: the window is
    /// its own thread.
    /// </summary>
    private void OpenProcessWindow(string args)
    {
        string id = args.Trim();
        if (id.Contains(' ', StringComparison.Ordinal))
        {
            _transcript.Error(ProcessWindowText.UsageError(id));
            return;
        }

        switch (_processes.Find(id, out var session, out var matches))
        {
            case FindOutcome.None:
                _transcript.Error(ProcessWindowText.NoMatchError(id));
                return;
            case FindOutcome.Ambiguous:
                _transcript.Error(ProcessWindowText.AmbiguousError(id, matches));
                return;
        }

        OpenProcessWindow(session!, _transcript);
    }

    /// <summary>
    /// <paramref name="session"/> in the process window (the typed id's, or <see cref="ProcessMenu"/>'s pick, 2026-10-05), how it
    /// went through <paramref name="sink"/> — the transcript, or the list pane's status line; false where no window was made.
    /// </summary>
    private bool OpenProcessWindow(ProcessSession session, INoticeSink sink)
    {
        if (_openProcessWindow is null)
        {
            sink.Error(ProcessWindowText.UnavailableHere);
            return false;
        }

        try
        {
            var registry = _processes;
            _openProcessWindow(session, stopped => registry.StopByUser(stopped));
            sink.Notice(ProcessWindowText.OpenedNotice(session.Id));
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            sink.Error(ProcessWindowText.WindowFailedError(ex.Message));
            return false;
        }
    }

    /// <summary><c>/process</c>'s argument list: every session's id, newest first, noted with its state and command (<see cref="ProcessWindowText.CompletionNote"/>).</summary>
    private IReadOnlyList<CompletionItem> ProcessChoices() =>
        _processes.List().Reverse().Select(s => new CompletionItem(s.Id, ProcessWindowText.CompletionNote(s))).ToList();
}
