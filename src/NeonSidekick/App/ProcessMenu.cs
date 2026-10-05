using NeonSidekick.Shell;
using NeonSidekick.UI;
using NeonSidekick.Viewer;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// The bare <c>/process</c> screen (2026-10-05, the user's ask: the info pane's rows could not be picked, so opening one meant
/// typing its id): every background process of the registry on a <see cref="MenuPane"/> page, oldest first, the row
/// <see cref="ProcessWindowText.Row"/>'s (an ended one dim) under the count (<see cref="ShellText.ListHeader"/>). Enter or a
/// double-click opens the highlighted one in the process window, the pane kept open; the <see cref="KillButton"/> on the title
/// row (a click, or <see cref="KillKey"/>) stops it after a yes/no kept under the list (the cursor on No), through
/// <see cref="ProcessRegistry.StopByUser"/> as the window's Ctrl+K twice does — so its exit is the chat's "stopped by you" line
/// and a seeded poll, as there. The <see cref="QueueMenu"/> shape: it opens under a reply too (a <c>Pane</c>-class command),
/// where both acts are safe — the window is its own thread and the stop writes nothing to the screen. The list is read again
/// after every act. Without the pane the screen prints the lines instead (<c>ChatScreen.ListProcessesAsync</c>).
/// </summary>
internal sealed class ProcessMenu
{
    // The label and the key hints. Pinned.
    public const string Title = ChatScreen.ProcessToolGlyph + " Process";
    public const string Keys = "Enter = open window · k = kill · ESC = close";

    /// <summary>The title row's button: the highlighted process stopped, after a yes/no.</summary>
    public const string KillButton = "✖ kill";

    /// <summary>The key that is the button.</summary>
    public const char KillKey = 'k';

    /// <summary>The page's buttons: the one.</summary>
    public static readonly IReadOnlyList<MenuButton> Buttons = [new(KillButton, KillKey)];

    /// <summary>What a declined kill says on the status line: the transcript's word.</summary>
    public const string KeptNotice = ChatScreen.KeptNotice;

    private readonly ProcessRegistry _processes;
    private readonly INoticeSink _transcript;
    private readonly MenuPane _pane;
    private readonly Func<ProcessSession, INoticeSink, bool> _open;

    /// <param name="processes">The registry: the list is read when the pane opens and again after every act.</param>
    /// <param name="transcript">Where the lines outside the pane go: the screen's transcript, or its deferring sink under a reply.</param>
    /// <param name="pane">The menu host in the bottom pane.</param>
    /// <param name="open">What Enter hands the session to: the screen's process window opener, saying how it went through the sink it is given.</param>
    public ProcessMenu(ProcessRegistry processes, INoticeSink transcript, MenuPane pane, Func<ProcessSession, INoticeSink, bool> open)
    {
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _transcript = transcript ?? throw new ArgumentNullException(nameof(transcript));
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
        _open = open ?? throw new ArgumentNullException(nameof(open));
    }

    /// <summary>Where a notice goes: the pane's status line while the list is open there, else the transcript.</summary>
    private INoticeSink Sink => _pane.IsOpen ? _pane : _transcript;

    // ── Pinned statics ──────────────────────────────────────────────────────

    /// <summary>A list row: <see cref="ProcessWindowText.Row"/> escaped, dim once the process has ended.</summary>
    public static string RowMarkup(ProcessSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        string row = ProcessWindowText.Row(session);
        return session.HasExited ? Theme.DimMarkup(row) : Markup.Escape(row);
    }

    /// <summary>The count over the rows: <see cref="ShellText.ListHeader"/>'s.</summary>
    public static string Caption(IReadOnlyList<ProcessSession> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        return ShellText.ListHeader(sessions.Count, sessions.Count(s => !s.HasExited));
    }

    /// <summary>The question before the kill: <c>⚡ Stop proc_3f2a1b (npm run dev)?</c></summary>
    public static string KillPrompt(ProcessSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return ChatScreen.ProcessToolGlyph + " Stop " + session.Id + " (" + ProcessWindowText.Label(session.Label) + ")?";
    }

    /// <summary>The kill sent: the exit's own line follows in the chat.</summary>
    public static string StoppingNotice(string id) => "(" + ChatScreen.ProcessToolGlyph + " stopping " + id + ")";

    /// <summary>The kill on a process that has ended already (or ended while the question stood): its state, nothing done.</summary>
    public static string EndedNotice(ProcessSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return "(" + ChatScreen.ProcessToolGlyph + " " + session.Id + " has ended: " + ProcessWindowText.State(session) + ")";
    }

    /// <summary>
    /// The row <paramref name="session"/> stands on in <paramref name="sessions"/> (the list read again), or, once it is gone, the row
    /// it stood on (<paramref name="row"/>) kept in range.
    /// </summary>
    public static int CursorAfter(IReadOnlyList<ProcessSession> sessions, ProcessSession session, int row)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        for (int i = 0; i < sessions.Count; i++)
        {
            if (ReferenceEquals(sessions[i], session))
            {
                return i;
            }
        }

        return Math.Clamp(row, 0, Math.Max(0, sessions.Count - 1));
    }

    // ── Screen ──────────────────────────────────────────────────────────────

    /// <summary>Shows the list until ESC; none to show is <see cref="ProcessWindowText.NoneYet"/> on the transcript.</summary>
    public async Task ShowAsync(CancellationToken cancellationToken)
    {
        var sessions = _processes.List();
        if (sessions.Count == 0)
        {
            _transcript.Notice(ProcessWindowText.NoneYet);
            return;
        }

        int cursor = 0;
        try
        {
            while (true)
            {
                var page = new MenuPage(Title, sessions.Select(RowMarkup).ToList(), Keys) { Buttons = Buttons, Caption = Caption(sessions) };
                var picked = await _pane.PickAsync(page, cursor, cancellationToken).ConfigureAwait(false);
                if (picked is not { } pick)
                {
                    return;
                }

                cursor = Math.Clamp(pick.Row, 0, sessions.Count - 1);
                var session = sessions[cursor];
                if (pick.Button < 0)
                {
                    _open(session, Sink);
                }
                else
                {
                    await KillAsync(session, cancellationToken).ConfigureAwait(false);
                }

                // A process may have started meanwhile (the model's, under a reply), and the oldest finished ones gone from the front
                // (the registry's eviction): the list again, the cursor on the same process wherever it moved to (2026-10-05, the code
                // review: kept as a row number, it could land on another process, and the kill button then asked about that one).
                sessions = _processes.List();
                if (sessions.Count == 0)
                {
                    return;
                }

                cursor = CursorAfter(sessions, session, cursor);
            }
        }
        finally
        {
            _pane.Close();
        }
    }

    /// <summary>The kill button on <paramref name="session"/>: an ended one says so, a running one asks and is stopped on Yes.</summary>
    private async Task KillAsync(ProcessSession session, CancellationToken cancellationToken)
    {
        if (session.HasExited)
        {
            Sink.Notice(EndedNotice(session));
            return;
        }

        if (!await ConfirmAsync(KillPrompt(session), cancellationToken).ConfigureAwait(false))
        {
            Sink.Notice(KeptNotice);
            return;
        }

        Sink.Notice(_processes.StopByUser(session) ? StoppingNotice(session.Id) : EndedNotice(session));
    }

    /// <summary>The yes/no page kept under the list (<see cref="SettingsMenu.ConfirmAsync"/>'s rows, keys and hotkeys, the cursor on No): true for Enter on Yes alone.</summary>
    private async Task<bool> ConfirmAsync(string question, CancellationToken cancellationToken)
    {
        var page = new MenuPage(question, SettingsMenu.ConfirmRows, SettingsMenu.ConfirmKeys) { Hotkeys = SettingsMenu.ConfirmHotkeys };
        var picked = await _pane.PickAsync(page, 0, cancellationToken).ConfigureAwait(false);
        return picked is { Row: 1 };
    }
}
