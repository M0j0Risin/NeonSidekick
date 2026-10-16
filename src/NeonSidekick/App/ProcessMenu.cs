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
/// <para>The state filter (2026-10-11, the user's ask: the running ones were lost among the finished): after the kill button, a radio
/// pair (running, ended) until later on 2026-10-13, since then one button (<see cref="FilterButton"/>, key f) stepping all → running →
/// ended, lit while it filters. Nothing is saved: every visit starts on all. The
/// count over the rows stays the whole list's; a filter that leaves nothing shows <see cref="NoMatchLine"/> and keeps the pane
/// open (it closes only once the registry is empty).</para>
/// </summary>
internal sealed class ProcessMenu
{
    // The label and the key hints. Pinned.
    public const string Title = ChatScreen.ProcessToolGlyph + " Process";
    public const string Keys = "Enter = open window · k = kill · f = filter · Esc = close";   // f since later on 2026-10-13 (r / e before)

    /// <summary>
    /// The title row's button: the highlighted process stopped, after a yes/no. Two spaces after the glyph (2026-10-05, the user's
    /// ask: it read as "✖kill" — Windows Terminal draws ✖ from the colour emoji font two cells wide over the one space after it,
    /// though it and <see cref="UI.TextCells"/> count one, as <c>SettingsMenu.PolicyYoloButton</c>'s ⚠).
    /// </summary>
    public const string KillButton = "✖  kill";

    /// <summary>The key that is the button.</summary>
    public const char KillKey = 'k';

    /// <summary>
    /// The filter button's titles: every process, the running ones alone, the ended ones alone (2026-10-11 as a radio pair, running and
    /// ended; one cycling button since later on 2026-10-13, the user's call, <c>/queue</c>'s shape, keeping the pair's glyph titles). Pinned.
    /// </summary>
    public const string AllButton = "show: all";

    public const string RunningButton = "● running";

    public const string EndedButton = "○ ended";

    /// <summary>The filter button's key (r and e for the pair until later on 2026-10-13).</summary>
    public const char FilterKey = 'f';

    /// <summary>The filter button's index on the title row: after the kill button, which stays first.</summary>
    public const int FilterIndex = 1;

    /// <summary>The filter button's title under <paramref name="running"/> (null = no filter, true = the running ones, false = the ended ones). Pinned.</summary>
    public static string FilterButton(bool? running) => running switch
    {
        true => RunningButton,
        false => EndedButton,
        _ => AllButton,
    };

    /// <summary>
    /// The page's buttons under <paramref name="running"/>: kill, then the filter button showing the filter in force, lit while it
    /// filters (<see cref="MenuButton.On"/>).
    /// </summary>
    public static IReadOnlyList<MenuButton> ButtonsFor(bool? running) =>
        [new(KillButton, KillKey), new(FilterButton(running), FilterKey, running is not null)];

    /// <summary>The filter after <paramref name="running"/>: all, running, ended, all again. Pure.</summary>
    public static bool? Next(bool? running) => running switch
    {
        null => true,
        true => false,
        false => null,
    };

    /// <summary>Whether <paramref name="session"/> stays under <paramref name="running"/>.</summary>
    public static bool Matches(bool? running, ProcessSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return running is not { } wanted || wanted == !session.HasExited;
    }

    /// <summary>The one dim row when the filter leaves nothing. Pinned.</summary>
    public static string NoMatchLine(bool running) => running ? "No running processes." : "No ended processes.";

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
            // The pane opens on the empty list too (2026-10-07, the consistency pass).
            await _pane.ShowEmptyAsync(Title, ProcessWindowText.NoneYet, cancellationToken).ConfigureAwait(false);
            return;
        }

        int cursor = 0;
        bool? running = null;   // the state filter (2026-10-11): none lit
        try
        {
            while (true)
            {
                var shown = sessions.Where(s => Matches(running, s)).ToList();
                List<string> rows = shown.Count > 0 ? shown.Select(RowMarkup).ToList() : [Theme.DimMarkup(NoMatchLine(running == true))];
                var page = new MenuPage(Title, rows, Keys)
                {
                    Buttons = ButtonsFor(running),
                    Caption = Caption(sessions),
                    Footer = (_, row) => row < shown.Count ? ProcessWindowText.Footer(shown[row]) : null,   // the whole command (2026-10-07)
                };
                var picked = await _pane.PickAsync(page, cursor, cancellationToken).ConfigureAwait(false);
                if (picked is not { } pick)
                {
                    return;
                }

                if (pick.Button == FilterIndex)
                {
                    // The cursor stays on its process while the new filter still shows it, else the first row.
                    var under = pick.Row >= 0 && pick.Row < shown.Count ? shown[pick.Row] : null;
                    running = Next(running);
                    sessions = _processes.List();
                    if (sessions.Count == 0)
                    {
                        return;
                    }

                    var next = sessions.Where(s => Matches(running, s)).ToList();
                    cursor = under is not null && next.Contains(under) ? next.IndexOf(under) : 0;
                    continue;
                }

                if (shown.Count == 0)
                {
                    continue;   // the no-match row: nothing to open or stop
                }

                cursor = Math.Clamp(pick.Row, 0, shown.Count - 1);
                var session = shown[cursor];
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

                cursor = CursorAfter(sessions.Where(s => Matches(running, s)).ToList(), session, cursor);
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
