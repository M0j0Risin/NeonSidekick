using System.Globalization;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// The <c>/queue</c> screen (2026-09-18): one row per message waiting in the <see cref="MessageQueue"/>
/// (its number, then its label — a paste as <c>[Pasted text +N lines]</c>), Enter or a double-click removes the highlighted one and shows the
/// list again (Delete too since 2026-10-16, the user's ask, <see cref="MenuPage.Removes"/>: the same question, the same stale-row check), the <see cref="ClearAllButton"/> on the title row (a click, or <see cref="ClearAllKey"/>;
/// 2026-09-21, the user's ask, the folder pane's collapse-all shape) drops every one and closes
/// with the transcript's dropped notice — what <c>/queue clear</c> prints —, ESC / Ctrl+C / the <c>×</c> back out. A list in the bottom pane (<see cref="MenuPane"/>,
/// the removal notice on its status line above the re-shown rows), the <see cref="MemoryMenu"/>
/// shape — with no prompt fallback: a message is queued only from the pane's mid-turn line hook, so
/// a console without the pane never holds one and <c>/queue</c> there prints <see cref="EmptyNotice"/>.
/// Opens mid-turn on the watcher task (a <c>Pane</c>-class command) and at the idle line over a held
/// queue; the rows are a snapshot and the removal checks the text, so a row the loop sent meanwhile
/// is not taken for another.
/// <para>The <see cref="SendButton"/> (2026-10-05, the user's ask) sends the front message now, while the queue is held (<c>Queue
/// cancel mode</c> <c>hold</c>, after a cancelled reply) and the pane is at the idle line: the pane closes and the line comes back
/// to the caller, which leaves it for the idle loop ahead of the hold (<c>ChatScreen.Pend</c>) — as if the user had sent it, so
/// hold's own rule follows: its reply's normal end releases the hold and the rest drains, a cancel holds it again. Not offered
/// under a reply (nothing can start a turn until it ends, and a normal end releases the hold anyway) nor over a queue that
/// drains by itself.</para>
/// <para>Under <c>Queue mode</c> <c>manual</c> (2026-10-09, the user's ask) the send is always offered, under a reply too: nothing
/// leaves that queue by itself, so each press sends the front message and the next waits for the next press. Under a reply the
/// line waits for the idle loop, which sends it first once the reply ends. The caller says when (<c>canSend</c>).</para>
/// <para>The two mode buttons (2026-10-13, the user's ask, with the toolbar's ⏳): <see cref="ModeButton"/> steps <c>Queue mode</c> and
/// <see cref="CancelButton"/> <c>Queue cancel mode</c> to the next word (wrapping), saved with the rows' own notice on the status line,
/// and the list shows again; on the empty queue too, whose pane is a one-row list carrying them (Memory's empty page).</para>
/// </summary>
internal sealed class QueueMenu
{
    // The label and the key hints: the pane shows the label as its title and the keys in its hint row. Pinned.
    public const string Title = ChatScreen.QueueToolGlyph + " Queue";
    public const string Keys = "Enter = remove · m = mode · k = cancel mode · c = clear all · Esc = close";   // close since 2026-10-07: the pane's top level; m and k 2026-10-13

    /// <summary>The key hints with the <see cref="SendButton"/> on the title row (2026-10-05). Pinned.</summary>
    public const string KeysWithSend = "Enter = remove · s = send · m = mode · k = cancel mode · c = clear all · Esc = close";

    /// <summary>The key hints over the empty queue (2026-10-13): the mode buttons alone. Pinned.</summary>
    public const string EmptyKeys = "m = mode · k = cancel mode · Esc = close";
    public const string EmptyNotice = "(" + NoticeGlyphs.Queue + "nothing queued)";   // the hourglass since 2026-09-22

    /// <summary>The one button on the title row (2026-09-21), drawn as a dim tab: every queued message dropped.</summary>
    public const string ClearAllButton = "⊠ clear all";

    /// <summary>The key that is the button.</summary>
    public const char ClearAllKey = 'c';

    /// <summary>The button that sends the front message now (2026-10-05), first on the title row while the queue is held at the idle line, and always under <c>Queue mode</c> <c>manual</c> (2026-10-09).</summary>
    public const string SendButton = "➤ send";

    /// <summary>The key that is the send button.</summary>
    public const char SendKey = 's';

    /// <summary>The key of the Queue mode button (2026-10-13).</summary>
    public const char ModeKey = 'm';

    /// <summary>The key of the Queue cancel mode button (2026-10-13).</summary>
    public const char CancelKey = 'k';

    /// <summary>The Queue mode button (2026-10-13, the user's ask): the word in force, <c>mode: automatic</c>; a press steps to the next. Pinned.</summary>
    public static string ModeButton(Queueing mode) => "mode: " + QueueMode.Name(mode);

    /// <summary>The Queue cancel mode button (2026-10-13, the user's ask): the word in force, <c>cancel: empty</c>; a press steps to the next. Pinned.</summary>
    public static string CancelButton(QueueCancel mode) => "cancel: " + QueueCancelMode.Name(mode);

    /// <summary>The Queue mode after <paramref name="mode"/> in <see cref="QueueMode.Names"/>' order, the last wrapping to the first. Pure.</summary>
    public static Queueing NextMode(Queueing mode)
    {
        int at = Array.IndexOf(QueueMode.Names, QueueMode.Name(mode));
        QueueMode.TryParse(QueueMode.Names[(at + 1) % QueueMode.Names.Length], out var next);
        return next;
    }

    /// <summary>The Queue cancel mode after <paramref name="mode"/> in <see cref="QueueCancelMode.Names"/>' order, the last wrapping to the first. Pure.</summary>
    public static QueueCancel NextCancel(QueueCancel mode)
    {
        int at = Array.IndexOf(QueueCancelMode.Names, QueueCancelMode.Name(mode));
        QueueCancelMode.TryParse(QueueCancelMode.Names[(at + 1) % QueueCancelMode.Names.Length], out var next);
        return next;
    }

    /// <summary>
    /// The title row's buttons: send while it is offered, the two mode buttons while <paramref name="modes"/> names them (2026-10-13),
    /// then clear all unless the queue is <paramref name="empty"/>.
    /// </summary>
    public static IReadOnlyList<MenuButton> Buttons(bool send, (Queueing Mode, QueueCancel Cancel)? modes, bool empty = false) =>
    [
        .. send ? new[] { new MenuButton(SendButton, SendKey) } : [],
        .. modes is { } m ? new[] { new MenuButton(ModeButton(m.Mode), ModeKey), new MenuButton(CancelButton(m.Cancel), CancelKey) } : [],
        .. empty ? [] : new[] { new MenuButton(ClearAllButton, ClearAllKey) },
    ];

    private readonly MessageQueue _queue;
    private readonly INoticeSink _transcript;
    private readonly MenuPane _pane;
    private readonly Func<Queueing>? _queueMode;
    private readonly Func<Queueing, bool>? _setQueueMode;
    private readonly Func<QueueCancel>? _cancelMode;
    private readonly Func<QueueCancel, bool>? _setCancelMode;

    /// <param name="transcript">Where the lines outside the pane go: the screen's deferring sink, since the list may open while a reply runs.</param>
    /// <param name="pane">The menu host in the bottom pane.</param>
    /// <param name="queueMode">The saved Queue mode (2026-10-13): with the three others, the mode buttons on the title row; null, none.</param>
    /// <param name="setQueueMode">Sets Queue mode, saving and saying so (<see cref="SettingsMenu.SetQueueMode"/>).</param>
    /// <param name="cancelMode">The saved Queue cancel mode.</param>
    /// <param name="setCancelMode">Sets Queue cancel mode, saving and saying so (<see cref="SettingsMenu.SetQueueCancelMode"/>).</param>
    public QueueMenu(MessageQueue queue, INoticeSink transcript, MenuPane pane, Func<Queueing>? queueMode = null, Func<Queueing, bool>? setQueueMode = null, Func<QueueCancel>? cancelMode = null, Func<QueueCancel, bool>? setCancelMode = null)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _transcript = transcript ?? throw new ArgumentNullException(nameof(transcript));
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
        _queueMode = queueMode;
        _setQueueMode = setQueueMode;
        _cancelMode = cancelMode;
        _setCancelMode = setCancelMode;
    }

    /// <summary>The two modes in force while the buttons are wired, else null (2026-10-13).</summary>
    private (Queueing Mode, QueueCancel Cancel)? Modes =>
        _queueMode is { } mode && _setQueueMode is not null && _cancelMode is { } cancel && _setCancelMode is not null ? (mode(), cancel()) : null;

    /// <summary>A mode button pressed (2026-10-13): the setting stepped to its next word, the rows' own notice said. False for another button.</summary>
    private bool PressMode(MenuButton button)
    {
        if (button.Key == ModeKey && _queueMode is { } mode && _setQueueMode is { } setMode)
        {
            setMode(NextMode(mode()));
            return true;
        }

        if (button.Key == CancelKey && _cancelMode is { } cancel && _setCancelMode is { } setCancel)
        {
            setCancel(NextCancel(cancel()));
            return true;
        }

        return false;
    }

    /// <summary>Where a notice goes: the pane's status line while the list is open there, else the transcript.</summary>
    private INoticeSink Sink => _pane.IsOpen ? _pane : _transcript;

    // ── Pinned statics ──────────────────────────────────────────────────────

    public static string RemovedNotice(string text) => $"({NoticeGlyphs.Queue}removed: {text})";

    /// <summary>The question before a message's removal (2026-10-07, every list's remove asks): <c>⏳ Remove "fix the tests" from the queue?</c> Pinned.</summary>
    public static string RemovePrompt(string text) => $"{NoticeGlyphs.Queue}Remove \"{text}\" from the queue?";

    /// <summary>
    /// The footer under the list for the message at <paramref name="index"/> of <paramref name="count"/> (2026-10-07, the user's ask:
    /// the row cut a long message at the edge): its whole label, then <c>2 of 5 · 1 picture</c>. Pinned.
    /// </summary>
    public static MenuFooter Footer(QueuedMessage entry, int index, int count)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string place = (index + 1).ToString(CultureInfo.InvariantCulture) + " of " + count.ToString(CultureInfo.InvariantCulture);
        int pictures = entry.Line.Images.Count;
        return new MenuFooter(entry.Label, pictures == 0 ? place : place + " · " + pictures.ToString(CultureInfo.InvariantCulture) + (pictures == 1 ? " picture" : " pictures"));
    }

    /// <summary>One menu row as markup: the position (1-based) dimmed, two spaces, the text escaped; the pane cuts it at the edge.</summary>
    public static string RowMarkup(int index, string text) =>
        Theme.DimMarkup((index + 1).ToString(CultureInfo.InvariantCulture)) + "  " + Markup.Escape(text);

    // ── Screen ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The pane, until it closes. <paramref name="canSend"/>, asked at each showing of the list, offers the <see cref="SendButton"/>
    /// while it says so (the idle line's while the queue is held, and under <c>Queue mode</c> <c>manual</c> anywhere, 2026-10-09);
    /// its press takes the front message off the queue and returns its line for the caller to send. Null otherwise.
    /// </summary>
    public async Task<SubmittedLine?> ShowAsync(CancellationToken cancellationToken, Func<bool>? canSend = null)
    {
        var entries = _queue.Snapshot();
        if (!_pane.Enabled)
        {
            _transcript.Notice(EmptyNotice);
            return null;
        }

        if (entries.Count == 0)
        {
            // The pane opens on the empty queue too (2026-10-07, the consistency pass), with the mode buttons since 2026-10-13.
            if (Modes is null)
            {
                await _pane.ShowEmptyAsync(Title, EmptyNotice, cancellationToken).ConfigureAwait(false);
                return null;
            }

            return await ShowEmptyAsync(cancellationToken, canSend).ConfigureAwait(false);
        }

        // What ends the visit is said after the pane has closed, so it lands in the transcript
        // rather than on a status line the close forgets.
        string? closingNotice = null;
        int cursor = 0;
        try
        {
            while (true)
            {
                bool send = canSend?.Invoke() == true;
                var buttons = Buttons(send, Modes);
                var shownEntries = entries;
                var page = new MenuPage(Title, entries.Select((entry, i) => RowMarkup(i, entry.Label)).ToList(), send ? KeysWithSend : Keys)
                {
                    Buttons = buttons,
                    Footer = (_, row) => row < shownEntries.Count ? Footer(shownEntries[row], row, shownEntries.Count) : null,   // the whole message (2026-10-07)
                    Removes = true,   // Delete: Enter's removal below (2026-10-16, the user's ask), its pick carrying no button
                };
                var picked = await _pane.PickAsync(page, cursor, cancellationToken).ConfigureAwait(false);
                if (picked is not { Row: var row })
                {
                    return null;
                }

                if (picked.Value.Button >= 0 && PressMode(buttons[picked.Value.Button]))
                {
                    // A mode button (2026-10-13): saved and said; the list again from a fresh snapshot, the cursor kept.
                    entries = _queue.Snapshot();
                    if (entries.Count == 0)
                    {
                        return await ShowEmptyAsync(cancellationToken, canSend).ConfigureAwait(false);   // the loop took the last meanwhile
                    }

                    cursor = Math.Min(row, entries.Count - 1);
                    continue;
                }

                if (picked.Value.Button >= 0 && buttons[picked.Value.Button].Title == SendButton)
                {
                    // Send: the front message off the queue and back to the caller (nothing queued by now is the empty notice).
                    if (_queue.TryDequeue(out var next))
                    {
                        return next.Line;
                    }

                    closingNotice = EmptyNotice;
                    return null;
                }

                if (picked.Value.Button >= 0)
                {
                    // The button: every message goes, the loop's own drop notice says how many
                    // (nothing queued by now — the loop took the last — is the empty notice).
                    int dropped = _queue.Clear(keepHold: true);   // /queue clear's: the page's held lines stay held
                    closingNotice = dropped > 0 ? ChatScreen.QueueDroppedNotice(dropped) : EmptyNotice;
                    return null;
                }

                string label = entries[row].Label;
                var now = _queue.Snapshot();
                if (row >= now.Count || now[row].Label != label)
                {
                    // The loop sent it while the list was up: no question about a message already gone, the list re-shows.
                    entries = now;
                    if (entries.Count == 0)
                    {
                        closingNotice = EmptyNotice;
                        return null;
                    }

                    cursor = Math.Min(row, entries.Count - 1);
                    continue;
                }

                var question = new MenuPage(RemovePrompt(label), SettingsMenu.ConfirmRows, SettingsMenu.ConfirmKeys) { Hotkeys = SettingsMenu.ConfirmHotkeys };
                if (await _pane.PickAsync(question, 0, cancellationToken).ConfigureAwait(false) is not { Row: 1 })
                {
                    // Kept; the list re-shows from a fresh snapshot all the same, since the loop may have sent meanwhile.
                    Sink.Notice(ChatScreen.KeptNotice);
                }
                else if (_queue.Remove(row, label))
                {
                    Sink.Notice(RemovedNotice(label));
                }

                entries = _queue.Snapshot();
                if (entries.Count == 0)
                {
                    closingNotice = EmptyNotice;
                    return null;
                }

                cursor = Math.Min(row, entries.Count - 1);
            }
        }
        finally
        {
            _pane.Close();
            if (closingNotice is not null)
            {
                _transcript.Notice(closingNotice);
            }
        }
    }

    /// <summary>
    /// The empty queue's pane with the mode buttons (2026-10-13): one dim <see cref="EmptyNotice"/> row, Enter on it nothing, a mode
    /// button saved and the page again, until ESC. A message queued meanwhile (a reply's) turns it into the list.
    /// </summary>
    private async Task<SubmittedLine?> ShowEmptyAsync(CancellationToken cancellationToken, Func<bool>? canSend)
    {
        try
        {
            while (_queue.Count == 0 && Modes is { } modes)
            {
                var buttons = Buttons(send: false, modes, empty: true);
                var page = new MenuPage(Title, [Theme.DimMarkup(EmptyNotice)], EmptyKeys) { Buttons = buttons };
                var picked = await _pane.PickAsync(page, 0, cancellationToken).ConfigureAwait(false);
                if (picked is null)
                {
                    return null;
                }

                if (picked.Value.Button >= 0)
                {
                    PressMode(buttons[picked.Value.Button]);
                }
            }
        }
        finally
        {
            _pane.Close();
        }

        return await ShowAsync(cancellationToken, canSend).ConfigureAwait(false);
    }
}
