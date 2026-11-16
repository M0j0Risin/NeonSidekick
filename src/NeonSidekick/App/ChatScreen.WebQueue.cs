using System.Collections.Concurrent;
using System.Net;
using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;
using NeonSidekick.UI;
using NeonSidekick.WebUi;

namespace NeonSidekick.App;

// ── The 🗂️ sheet's Queue tab (2026-11-16, plan lucid-roaming-lantern #42, the user's ask) ──────────────────────────────────────────
// The page sees the PC's queue and its own waiting lines, takes one back, sends the front one now and sets the two queue modes. Every
// handler runs on a Kestrel thread: nothing here prints. Notices go through _webUi.Notice (printed by the screen on its own task), the
// PC's hint row through _webUi.FlashPc, and the idle read is woken with WebNudge.

internal sealed partial class ChatScreen
{
    /// <summary>
    /// The keys of the page's lines pended at the screen (<see cref="_pendingLines"/>) that someone owns now: true, taken back from the Queue
    /// tab; false, taken by the idle line or dropped. The first <c>TryAdd</c> wins, so a removal racing the idle line's take hands nothing back
    /// twice and sends nothing taken back. Never cleared: keys are never reused, and an entry is a few bytes a pended page line.
    /// </summary>
    private readonly ConcurrentDictionary<long, bool> _claimedWebKeys = new();

    /// <summary>Bumped whenever a line enters or leaves <see cref="_pendingLines"/>: the third part of the page's queue tag.</summary>
    private long _pendingVersion;

    /// <summary>
    /// The next line left for the idle line, on the screen's task: a page line someone took back meanwhile is skipped; one taken here is
    /// claimed first, so a removal holding an older view finds it gone.
    /// </summary>
    private SubmittedLine? TakePendingLine()
    {
        while (_pendingLines.TryDequeue(out var line))
        {
            Interlocked.Increment(ref _pendingVersion);
            if (line.WebKey == 0 || _claimedWebKeys.TryAdd(line.WebKey, false))
            {
                return line;
            }
        }

        return null;
    }

    /// <summary>Tests: a line pended as a command under a botchat is.</summary>
    internal void PendForTests(SubmittedLine line) => Pend(line);

    /// <summary>Tests: the idle line's take from the pended lines.</summary>
    internal SubmittedLine? TakePendingLineForTests() => TakePendingLine();

    /// <summary>
    /// The page's queue tag: the PC's queue, the session's lines and the screen's pended ones, each by its version, and what the view's buttons
    /// follow (the saved modes, a reply running), so an open tab re-reads when anything it shows moved.
    /// </summary>
    private string WebQueueTagNow()
    {
        var saved = _settings.Current;
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{_queue.Version}.{_webUi.Version}.{Interlocked.Read(ref _pendingVersion)}.{saved.QueueMode}.{saved.QueueCancelMode}.{(_turnRunning ? 1 : 0)}");
    }

    /// <summary>The Queue tab's view (any thread, reads only): the modes as saved, as the PC pane's buttons show them.</summary>
    private WebQueueView WebQueueNow()
    {
        var saved = _settings.Current;
        var pc = _queue.Snapshot().Select((m, i) => new WebQueuePcLine(i, m.Label, m.Line.Images.Count)).ToList();
        return new WebQueueView(
            pc,
            _webUi.WaitingLines,
            QueueMode.Name(SavedQueueMode(saved)),
            QueueCancelMode.Name(SavedQueueCancel(saved)),
            _queue.Held,
            SendOffered(underReply: _turnRunning) && pc.Count > 0,
            QueueMode.Names,
            QueueCancelMode.Names);
    }

    /// <summary>
    /// A ✕ on the Queue tab: a PC line by its index and label, checked against the queue now as the pane's Enter is; a page's line by its key,
    /// from the session's lists or the screen's pended ones, back to the page's draft. Conflict when it is no longer there.
    /// </summary>
    private WebReply WebRemoveQueued(WebQueueRemoveRequest request, IPAddress? remote)
    {
        if (request.Key is not { } key)
        {
            string label = request.Label ?? "";
            if (request.Index is not { } index || !_queue.Remove(index, label))
            {
                return new WebReply(WebOutcome.Conflict, WebUiText.LineGone);
            }

            DiagnosticLog.Info(WebUiCategory, WebUiText.QueueRemovedLogLine(label, remote));
            _webUi.Notice(QueueMenu.RemovedNotice(label));
            _webUi.FlashPc(WebUiText.QueueRemovedFlash(label));
            _webUi.PublishState();
            return WebReply.Ok;
        }

        string? text = null;
        IReadOnlyList<WebHeldAttachment>? attachments = null;
        if (_webUi.TryRemoveLine(key, out var waiting))
        {
            text = waiting.Text;
            attachments = waiting.Attachments;
        }
        else if (key > 0 && _pendingLines.FirstOrDefault(l => l.FromWeb && !l.Chord && l.WebKey == key) is { } pended
            && _claimedWebKeys.TryAdd(key, true))
        {
            // Left in the screen's queue, claimed as removed: the idle line skips it, and the tab no longer lists it.
            Interlocked.Increment(ref _pendingVersion);
            text = pended.Draft;
            attachments = pended.WebAttachments;
        }

        if (text is null)
        {
            return new WebReply(WebOutcome.Conflict, WebUiText.LineGone);
        }

        _webUi.TakenBack(text, attachments);   // its chips back to its own page (2026-11-16, #39), whichever page took it back
        DiagnosticLog.Info(WebUiCategory, WebUiText.PageLineRemovedLogLine(remote));
        _webUi.FlashPc(WebUiText.PageLineRemovedFlash);
        _webUi.PublishState();
        return WebReply.Ok;
    }

    /// <summary>
    /// The Queue tab's ➤ Send now, as the pane's ➤ and <c>/queue send</c>: the front PC line left for the idle line, which is woken. Under a
    /// reply the line waits and goes first, an OK with the PC's words; with nothing queued, or a queue that sends itself, a Conflict.
    /// </summary>
    private WebReply WebSendQueued(IPAddress? remote)
    {
        bool underReply = _turnRunning;
        if (_queue.Count == 0)
        {
            return new WebReply(WebOutcome.Conflict, QueueMenu.EmptyNotice);
        }

        if (!SendOffered(underReply))
        {
            return new WebReply(WebOutcome.Conflict, QueueSendsItselfNotice);
        }

        if (!_queue.TryDequeue(out var next))
        {
            return new WebReply(WebOutcome.Conflict, QueueMenu.EmptyNotice);   // the loop took the last meanwhile
        }

        Pend(next.Line);
        DiagnosticLog.Info(WebUiCategory, WebUiText.QueueSentLogLine(next.Label, remote));
        _webUi.FlashPc(WebUiText.QueueSentFlash(next.Label));
        _webUi.PublishState();
        WebNudge();
        return underReply ? new WebReply(WebOutcome.Ok, QueueSendWaitsNotice) : WebReply.Ok;
    }

    /// <summary>The Queue tab's <i>Queue mode</i>: saved as the settings row saves it, its notice printed by the screen; in force from the next line.</summary>
    private WebReply WebSetQueueMode(Queueing mode, IPAddress? remote)
    {
        string name = QueueMode.Name(mode);
        return WebSaveQueueSetting(SettingsField.QueueMode, name, string.Equals(_settings.Current.QueueMode, name, StringComparison.Ordinal),
            d => d.QueueMode = name, remote);
    }

    /// <summary>The Queue tab's <i>Queue cancel mode</i>: read at the next cancel.</summary>
    private WebReply WebSetQueueCancelMode(QueueCancel mode, IPAddress? remote)
    {
        string name = QueueCancelMode.Name(mode);
        return WebSaveQueueSetting(SettingsField.QueueCancelMode, name, string.Equals(_settings.Current.QueueCancelMode, name, StringComparison.Ordinal),
            d => d.QueueCancelMode = name, remote);
    }

    /// <summary>The one save under both: a no-op when it already is; else the save (locked), the notice for the screen, the hint row, the audit line.</summary>
    private WebReply WebSaveQueueSetting(SettingsField field, string word, bool unchanged, Action<AppSettingsData> mutate, IPAddress? remote)
    {
        if (unchanged)
        {
            return WebReply.Ok;
        }

        _settings.Update(mutate);
        string setting = SettingsMenu.FieldName(field);
        _webUi.Notice(SettingsMenu.SavedNotice(field, _settings.Current, _settings.ProfileDirectory));
        DiagnosticLog.Info(WebUiCategory, WebUiText.QueueModeLogLine(setting, word, remote));
        _webUi.FlashPc(WebUiText.QueueModeFlash(setting, word));
        _webUi.PublishState();
        return WebReply.Ok;
    }
}
