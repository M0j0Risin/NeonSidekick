using System.Globalization;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// The <c>/queue</c> screen (2026-09-18): one row per message waiting in the <see cref="MessageQueue"/>
/// (its number, then its label — a paste as <c>[Pasted text +N lines]</c>), Enter or a double-click removes the highlighted one and shows the
/// list again, the <see cref="ClearAllButton"/> on the title row (a click, or <see cref="ClearAllKey"/>;
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
/// </summary>
internal sealed class QueueMenu
{
    // The label and the key hints: the pane shows the label as its title and the keys in its hint row. Pinned.
    public const string Title = "⏳ Queue";
    public const string Keys = "Enter = remove · c = clear all · ESC = back";

    /// <summary>The key hints with the <see cref="SendButton"/> on the title row (2026-10-05). Pinned.</summary>
    public const string KeysWithSend = "Enter = remove · s = send · c = clear all · ESC = back";
    public const string EmptyNotice = "(" + NoticeGlyphs.Queue + "nothing queued)";   // the hourglass since 2026-09-22

    /// <summary>The one button on the title row (2026-09-21), drawn as a dim tab: every queued message dropped.</summary>
    public const string ClearAllButton = "⊠ clear all";

    /// <summary>The key that is the button.</summary>
    public const char ClearAllKey = 'c';

    /// <summary>The button that sends the front message now (2026-10-05), first on the title row while the queue is held at the idle line.</summary>
    public const string SendButton = "➤ send";

    /// <summary>The key that is the send button.</summary>
    public const char SendKey = 's';

    /// <summary>The page's buttons: clear all alone.</summary>
    public static readonly IReadOnlyList<MenuButton> Buttons = [new(ClearAllButton, ClearAllKey)];

    /// <summary>The page's buttons while a send is offered: send, then clear all.</summary>
    public static readonly IReadOnlyList<MenuButton> ButtonsWithSend = [new(SendButton, SendKey), new(ClearAllButton, ClearAllKey)];

    private readonly MessageQueue _queue;
    private readonly INoticeSink _transcript;
    private readonly MenuPane _pane;

    /// <param name="transcript">Where the lines outside the pane go: the screen's deferring sink, since the list may open while a reply runs.</param>
    /// <param name="pane">The menu host in the bottom pane.</param>
    public QueueMenu(MessageQueue queue, INoticeSink transcript, MenuPane pane)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _transcript = transcript ?? throw new ArgumentNullException(nameof(transcript));
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
    }

    /// <summary>Where a notice goes: the pane's status line while the list is open there, else the transcript.</summary>
    private INoticeSink Sink => _pane.IsOpen ? _pane : _transcript;

    // ── Pinned statics ──────────────────────────────────────────────────────

    public static string RemovedNotice(string text) => $"({NoticeGlyphs.Queue}removed: {text})";

    /// <summary>One menu row as markup: the position (1-based) dimmed, two spaces, the text escaped; the pane cuts it at the edge.</summary>
    public static string RowMarkup(int index, string text) =>
        Theme.DimMarkup((index + 1).ToString(CultureInfo.InvariantCulture)) + "  " + Markup.Escape(text);

    // ── Screen ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The pane, until it closes. <paramref name="offerSend"/> (the idle line's) offers the <see cref="SendButton"/> while the
    /// queue is held; its press takes the front message off the queue and returns its line for the caller to send. Null otherwise.
    /// </summary>
    public async Task<SubmittedLine?> ShowAsync(CancellationToken cancellationToken, bool offerSend = false)
    {
        var entries = _queue.Snapshot();
        if (entries.Count == 0 || !_pane.Enabled)
        {
            _transcript.Notice(EmptyNotice);
            return null;
        }

        // What ends the visit is said after the pane has closed, so it lands in the transcript
        // rather than on a status line the close forgets.
        string? closingNotice = null;
        int cursor = 0;
        try
        {
            while (true)
            {
                bool send = offerSend && _queue.Held;
                var buttons = send ? ButtonsWithSend : Buttons;
                var page = new MenuPage(Title, entries.Select((entry, i) => RowMarkup(i, entry.Label)).ToList(), send ? KeysWithSend : Keys) { Buttons = buttons };
                var picked = await _pane.PickAsync(page, cursor, cancellationToken).ConfigureAwait(false);
                if (picked is not { Row: var row })
                {
                    return null;
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
                    int dropped = _queue.Clear();
                    closingNotice = dropped > 0 ? ChatScreen.QueueDroppedNotice(dropped) : EmptyNotice;
                    return null;
                }

                string label = entries[row].Label;
                if (_queue.Remove(row, label))
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
}
