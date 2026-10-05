using NeonSidekick.Shell;
using NeonSidekick.UI;
using NeonSidekick.Viewer;

namespace NeonSidekick.App;

// ── /process: the background processes and their window (2026-10-05) ─────────

internal sealed partial class ChatScreen
{
    /// <summary>
    /// <c>/process</c> bare: every session of the registry on the info pane (<see cref="ProcessWindowText.Row"/>, oldest first) and
    /// how to open one; none yet is its line through <paramref name="sink"/>. Read-only, so a pane under a reply too.
    /// </summary>
    private Task ListProcessesAsync(INoticeSink sink, CancellationToken cancellationToken)
    {
        var sessions = _processes.List();
        if (sessions.Count == 0)
        {
            sink.Notice(ProcessWindowText.NoneYet);
            return Task.CompletedTask;
        }

        var lines = new List<string> { ShellText.ListHeader(sessions.Count, sessions.Count(s => !s.HasExited)), "" };
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

        if (_openProcessWindow is null)
        {
            _transcript.Error(ProcessWindowText.Unavailable);
            return;
        }

        try
        {
            var registry = _processes;
            _openProcessWindow(session!, stopped => registry.StopByUser(stopped));
            _transcript.Notice(ProcessWindowText.OpenedNotice(session!.Id));
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            _transcript.Error(ProcessWindowText.WindowFailedError(ex.Message));
        }
    }

    /// <summary><c>/process</c>'s argument list: every session's id, newest first, noted with its state and command (<see cref="ProcessWindowText.CompletionNote"/>).</summary>
    private IReadOnlyList<CompletionItem> ProcessChoices() =>
        _processes.List().Reverse().Select(s => new CompletionItem(s.Id, ProcessWindowText.CompletionNote(s))).ToList();
}
