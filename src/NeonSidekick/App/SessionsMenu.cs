using NeonSidekick.Sessions;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// The bare <c>/sessions</c> screen (2026-09-18): this profile's stored sessions on a
/// <see cref="MenuPane"/> page, newest first, one row each — the id, the last turn's moment and the
/// turn count dim, then the title, the conversation on screen marked. Enter or a double-click on a
/// row opens the row page under the list (<see cref="RowTitle"/>): <c>restore</c> hands the id back
/// to the screen (the pane closed; <see cref="ChatScreen"/> replays it), <c>rename</c> reads a new
/// title in the pane's input slot (<see cref="MenuPane.EditAsync"/>), <c>purge</c> removes the row
/// after a yes/no confirmation kept under the list (the <see cref="SkillsMenu"/> shape). Mid-turn —
/// the pane opens on the watcher task while a reply runs — the list shows and any pick is refused
/// (<see cref="SettingsMenu.NotWhileReplyRunsNotice"/>). Every notice goes to the pane's status line
/// while it is open, else to the transcript. Without the pane the list prints as numbered lines and
/// nothing else (<see cref="MemoryMenu"/>'s fallback); the typed forms of <c>/sessions</c> do the rest.
/// <para>The rename box stands alone too (<see cref="RenameAsync"/>, 2026-09-28, the user's ask): a double-click on the
/// session's name at the upper rule's right edge, or the bare <c>/sessions title</c>, opens the <c>rename</c> row on its
/// own with the title in the slot — under a reply as at idle.</para>
/// <para>Under the list (2026-10-07, the user's ask: a row cut at the edge lost the title's end and the mark of the conversation on
/// screen), the cursor's session in full: its whole title, then when it started and last ran, its turns, its model and whether it
/// is the one on screen (<see cref="Footer"/>). Typing narrows the list to the sessions whose title or model holds the text
/// (<see cref="MenuFilter"/>), the first ESC clearing it.</para>
/// </summary>
internal sealed class SessionsMenu
{
    // The key hints. Pinned.
    public const string Title = ChatScreen.SessionsToolGlyph + " Sessions";   // the glyph the toolbar wears for the pane too (later on 2026-09-21)
    public const string Keys = "Enter = open · " + MenuFilter.TypeAndCloseKeys;   // type to filter since 2026-10-07
    public const string RowKeys = SettingsMenu.PickKeys;

    public const string EmptyNotice = "(" + NoticeGlyphs.Session + "no sessions)";   // the balloon since 2026-09-22

    /// <summary>The dim note after the row of the conversation on screen.</summary>
    public const string CurrentNote = "this conversation";

    public const string RestoreWord = "restore";
    public const string RenameWord = "rename";
    public const string PurgeWord = "purge";

    /// <summary>What a declined confirmation says on the status line: the transcript's word.</summary>
    public const string KeptNotice = ChatScreen.KeptNotice;

    /// <summary>The row page's rows, in order; the picked index is read against it.</summary>
    public static readonly IReadOnlyList<string> RowWords = [RestoreWord, RenameWord, PurgeWord];

    private readonly SessionStore _store;
    private readonly Func<long?> _current;
    private readonly INoticeSink _transcript;
    private readonly MenuPane _pane;
    private readonly InputLine _input;
    private readonly TimeProvider _time;
    private readonly Action<long> _purged;

    /// <param name="store">The profile's store; the list is read when the pane opens and again after every change.</param>
    /// <param name="current">The session on screen (marked in the list, never restored); null before its first turn.</param>
    /// <param name="transcript">Where the lines outside the pane go: the screen's deferring sink, since the list may open while a reply runs.</param>
    /// <param name="pane">The menu host in the bottom pane.</param>
    /// <param name="input">The line the rename is typed on, in the pane's slot.</param>
    /// <param name="time">The clock whose local zone the moments are shown in.</param>
    /// <param name="purged">Told the id of every session purged here, so the screen can forget the current one.</param>
    public SessionsMenu(SessionStore store, Func<long?> current, INoticeSink transcript, MenuPane pane, InputLine input, TimeProvider time, Action<long> purged)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _current = current ?? throw new ArgumentNullException(nameof(current));
        _transcript = transcript ?? throw new ArgumentNullException(nameof(transcript));
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _purged = purged ?? throw new ArgumentNullException(nameof(purged));
    }

    /// <summary>Where a notice goes: the pane's status line while the list is open there, else the transcript.</summary>
    private INoticeSink Sink => _pane.IsOpen ? _pane : _transcript;

    // ── Pinned statics ──────────────────────────────────────────────────────

    /// <summary>The row page's title: <c>Sessions › #12 Title</c>.</summary>
    public static string RowTitle(SessionSummary session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return Title + " › " + SessionText.Id(session.Id) + " " + session.Title;
    }

    /// <summary>
    /// A list row: <c>#12  2026-09-18 14:05  12 turns</c> dim, the title, and <see cref="CurrentNote"/>
    /// dim after the conversation on screen. The id and the turns label are padded to
    /// <paramref name="idWidth"/> and <paramref name="turnsWidth"/> — the widest in the list shown
    /// (<see cref="Widths"/>) — so <c>#3</c> and <c>1 turn</c> line up under <c>#12</c> and <c>5 turns</c>.
    /// </summary>
    public static string RowMarkup(SessionSummary session, bool current, TimeZoneInfo zone, int idWidth = 0, int turnsWidth = 0)
    {
        ArgumentNullException.ThrowIfNull(session);
        string row = Theme.DimMarkup(SessionText.Id(session.Id).PadRight(idWidth) + "  " + SessionText.Moment(session.UpdatedAt, zone) + "  " + SessionText.Turns(session.Turns).PadRight(turnsWidth)) + "  " + Markup.Escape(SessionText.DisplayTitle(session));
        return current ? row + "  " + Theme.DimMarkup(CurrentNote) : row;
    }

    /// <summary>The two column widths of a list: the widest id and the widest turns label in it.</summary>
    public static (int Id, int Turns) Widths(IReadOnlyList<SessionSummary> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        return sessions.Count == 0 ? (0, 0) : (sessions.Max(s => SessionText.Id(s.Id).Length), sessions.Max(s => SessionText.Turns(s.Turns).Length));
    }

    /// <summary>
    /// The footer under the list for <paramref name="session"/> (2026-10-07): its whole title, then
    /// <c>started 2026-10-07 09:12 · last 2026-10-07 14:40 · 14 turns · qwen3 · this conversation</c>, the model left out when none
    /// was stored. Pinned.
    /// </summary>
    public static MenuFooter Footer(SessionSummary session, bool current, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(session);
        var facts = new List<string>
        {
            "started " + SessionText.Moment(session.StartedAt, zone),
            "last " + SessionText.Moment(session.UpdatedAt, zone),
            SessionText.Turns(session.Turns),
        };
        if (!string.IsNullOrWhiteSpace(session.Model))
        {
            facts.Add(session.Model);
        }

        if (current)
        {
            facts.Add(CurrentNote);
        }

        return new MenuFooter(SessionText.DisplayTitle(session), string.Join(" · ", facts));
    }

    /// <summary>Whether <paramref name="session"/> stays under <paramref name="filter"/>: its title or its model holds it (2026-10-07).</summary>
    public static bool Matches(string filter, SessionSummary session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return MenuFilter.Matches(filter, SessionText.DisplayTitle(session), session.Model);
    }

    /// <summary>The row page's rows: each word padded to nine, what it does dim after it.</summary>
    public static string RowPageRow(string word) => Markup.Escape(word.PadRight(9)) + Theme.DimMarkup(word switch
    {
        RestoreWord => "load it into the transcript and go on from there",
        RenameWord => "give it a new title",
        _ => "remove it and its turns for good",
    });

    /// <summary>The no-pane fallback: <c>1. #12 · 2026-09-18 14:05 · 12 turns · Title</c> per session.</summary>
    public static IReadOnlyList<string> ListLines(IReadOnlyList<SessionSummary> sessions, long? current, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        var lines = new List<string>(sessions.Count);
        for (int i = 0; i < sessions.Count; i++)
        {
            lines.Add((i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + ". " + SessionText.Label(sessions[i], zone) + (sessions[i].Id == current ? " (" + CurrentNote + ")" : ""));
        }

        return lines;
    }

    public static string PurgePrompt(SessionSummary session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return NoticeGlyphs.Session + "Purge session " + SessionText.Id(session.Id) + " \"" + session.Title + "\" (" + SessionText.Turns(session.Turns) + ")?";
    }

    public static string PurgedNotice(long id) => "(" + ChatScreen.TrashGlyph + "purged session " + SessionText.Id(id) + ")";

    public static string PurgeFailedError(long id) => "Could not purge session " + SessionText.Id(id) + "; it may be gone already";

    public static string RenamedNotice(string title) => "(" + NoticeGlyphs.Session + "renamed: " + title + ")";

    public static string RenameFailedError(long id) => "Could not rename session " + SessionText.Id(id) + "; it may be gone already";

    /// <summary>The row page's <c>restore</c> on the conversation already on screen.</summary>
    public const string CurrentNotice = "(" + NoticeGlyphs.Session + "that is this conversation)";

    // ── Screen ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Shows the list; the id of the session the user picked <c>restore</c> for (the pane closed),
    /// null when the pane closed any other way.
    /// </summary>
    /// <param name="midTurn">The pane opened while a reply runs: the list shows, a pick is refused.</param>
    public async Task<long?> ShowAsync(CancellationToken cancellationToken, bool midTurn = false)
    {
        var zone = _time.LocalTimeZone;
        var sessions = _store.List(0);
        if (sessions.Count == 0)
        {
            // The pane opens on the empty list too (2026-10-07, the consistency pass); without one, the line.
            if (_pane.Enabled)
            {
                await _pane.ShowEmptyAsync(Title, EmptyNotice, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _transcript.Notice(EmptyNotice);
            }

            return null;
        }

        if (!_pane.Enabled)
        {
            foreach (var line in ListLines(sessions, _current(), zone))
            {
                _transcript.Notice(line);
            }

            return null;
        }

        int cursor = 0;
        string filter = "";
        try
        {
            while (true)
            {
                long? current = _current();
                var (idWidth, turnsWidth) = Widths(sessions);
                // The rows under the filter (2026-10-07): the indices into the list, the cursor kept on the same session when it stays.
                var shown = Enumerable.Range(0, sessions.Count).Where(i => Matches(filter, sessions[i])).ToList();
                List<string> rows = shown.Count > 0 ? shown.Select(i => RowMarkup(sessions[i], sessions[i].Id == current, zone, idWidth, turnsWidth)).ToList() : [MenuFilter.NoMatchRow(filter)];
                var page = new MenuPage(Title, rows, MenuFilter.Hint(Keys, filter))
                {
                    Filter = filter,
                    Caption = MenuFilter.CaptionOrNull(filter, shown.Count, sessions.Count),
                    Footer = (_, row) => row < shown.Count ? Footer(sessions[shown[row]], sessions[shown[row]].Id == current, zone) : null,
                };
                var picked = await _pane.PickAsync(page, Math.Max(0, shown.IndexOf(cursor)), cancellationToken).ConfigureAwait(false);
                if (picked is not { } pick)
                {
                    return null;
                }

                if (pick.Filter is { } typed)
                {
                    filter = typed;
                    cursor = -1;   // the first row left
                    continue;
                }

                if (pick.Row < 0 || pick.Row >= shown.Count)
                {
                    continue;   // the no-match row
                }

                cursor = shown[pick.Row];
                if (midTurn)
                {
                    Sink.Notice(SettingsMenu.NotWhileReplyRunsNotice);
                    continue;
                }

                var session = sessions[cursor];
                var outcome = await PickRowAsync(session, session.Id == current, cancellationToken).ConfigureAwait(false);
                if (outcome == RowOutcome.Restore)
                {
                    return session.Id;
                }

                if (outcome == RowOutcome.Changed)
                {
                    sessions = _store.List(0);
                    if (sessions.Count == 0)
                    {
                        _pane.Close();
                        _transcript.Notice(EmptyNotice);
                        return null;
                    }

                    cursor = Math.Min(cursor, sessions.Count - 1);
                }
            }
        }
        finally
        {
            _pane.Close();
        }
    }

    /// <summary>
    /// The rename box alone (2026-09-28, the user's ask: a double-click on the upper rule's session name, or the bare
    /// <c>/sessions title</c>): the row page's <c>rename</c> row with the title of session <paramref name="id"/> in the
    /// pane's input slot, then the pane closed. True when the title changed. The caller checks the pane is enabled;
    /// mid-turn is fine, the store holding its own lock.
    /// </summary>
    public async Task<bool> RenameAsync(long id, CancellationToken cancellationToken)
    {
        if (_store.Summary(id) is not { } session)
        {
            _transcript.Error(RenameFailedError(id));
            return false;
        }

        try
        {
            var page = new MenuPage(RowTitle(session), [RowPageRow(RenameWord)], RowKeys);
            return await EditTitleAsync(page, 0, session, cancellationToken, closeFirst: true).ConfigureAwait(false) == true;
        }
        finally
        {
            _pane.Close();
        }
    }

    /// <summary>
    /// Reads a new title for <paramref name="session"/> on row <paramref name="row"/> of <paramref name="page"/>, its old
    /// one in the slot, and stores it as the user's: null when nothing was submitted (ESC, a blank), true when it landed,
    /// false when the store refused it (the session gone) — the notice or the error said so. With
    /// <paramref name="closeFirst"/> the pane closes before the store is asked, so the line lands in the transcript, not on
    /// a status line about to go.
    /// </summary>
    private async Task<bool?> EditTitleAsync(MenuPage page, int row, SessionSummary session, CancellationToken cancellationToken, bool closeFirst = false)
    {
        var typed = await _pane.EditAsync(page with { Hint = SettingsMenu.EditKeys }, row, _input, session.Title, allowEmpty: false, cancellationToken).ConfigureAwait(false);
        if (closeFirst)
        {
            _pane.Close();
        }

        if (typed is not InputResult.Submitted { Text: var text } || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string title = SessionText.FirstLineTitle(text);
        if (_store.SetTitle(session.Id, title, TitleSource.User))
        {
            Sink.Notice(RenamedNotice(title));
            return true;
        }

        Sink.Error(RenameFailedError(session.Id));
        return false;
    }

    private enum RowOutcome
    {
        Nothing,
        Changed,
        Restore,
    }

    /// <summary>The row page under the list, then the act; <see cref="RowOutcome.Changed"/> when the list is stale.</summary>
    private async Task<RowOutcome> PickRowAsync(SessionSummary session, bool current, CancellationToken cancellationToken)
    {
        var page = new MenuPage(RowTitle(session), RowWords.Select(RowPageRow).ToList(), RowKeys);
        var picked = await _pane.PickAsync(page, 0, cancellationToken).ConfigureAwait(false);
        if (picked is not { Row: var row })
        {
            return RowOutcome.Nothing;
        }

        switch (RowWords[Math.Clamp(row, 0, RowWords.Count - 1)])
        {
            case RestoreWord:
                if (current)
                {
                    Sink.Notice(CurrentNotice);
                    return RowOutcome.Nothing;
                }

                return RowOutcome.Restore;

            case RenameWord:
                return await EditTitleAsync(page, row, session, cancellationToken).ConfigureAwait(false) is null ? RowOutcome.Nothing : RowOutcome.Changed;

            default:
                if (!await ConfirmAsync(PurgePrompt(session), cancellationToken).ConfigureAwait(false))
                {
                    Sink.Notice(KeptNotice);
                    return RowOutcome.Nothing;
                }

                if (_store.Purge(session.Id))
                {
                    _purged(session.Id);
                    Sink.Notice(PurgedNotice(session.Id));
                }
                else
                {
                    Sink.Error(PurgeFailedError(session.Id));
                }

                return RowOutcome.Changed;
        }
    }

    /// <summary>The yes/no page kept under the list (<see cref="SettingsMenu.ConfirmAsync"/>'s rows, keys and hotkeys, the cursor on No): true for Enter on Yes alone.</summary>
    private async Task<bool> ConfirmAsync(string question, CancellationToken cancellationToken)
    {
        var page = new MenuPage(question, SettingsMenu.ConfirmRows, SettingsMenu.ConfirmKeys) { Hotkeys = SettingsMenu.ConfirmHotkeys };
        var picked = await _pane.PickAsync(page, 0, cancellationToken).ConfigureAwait(false);
        return picked is { Row: 1 };
    }
}
