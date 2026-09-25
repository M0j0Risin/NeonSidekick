using NeonSidekick.UI;

using NeonSidekick.Diagnostics;

namespace NeonSidekick.App;

/// <summary>
/// One message waiting in the <see cref="MessageQueue"/> (2026-09-18): the line the live row handed back
/// (<see cref="SubmittedLine"/> — its token form, its expansion and pictures) and the label a pane row shows
/// (a paste as <c>[Pasted text #1 +N lines]</c>). Until 2026-09-25 it held the watcher's buffered key events and was
/// replayed through the input line; the row has been the idle line's editor under a reply since, so the line is
/// whole when it is queued and <see cref="InputLine.Send"/> commits it when its turn comes.
/// </summary>
public sealed record QueuedMessage(string Label, SubmittedLine Line);

/// <summary>
/// The messages sent while a reply runs, waiting to be sent one by one (2026-09-18, behind
/// <c>Queue messages</c>). One leaf lock: <see cref="Enqueue"/> runs on the watcher task (the line
/// hook), <see cref="TryDequeue"/> and <see cref="Held"/> on the idle loop, <see cref="Remove"/>
/// on whichever task shows the <c>/queue</c> pane, <see cref="Count"/> on the pane's tick thread
/// under its own gate. No sentences here: <see cref="ChatScreen"/> and <see cref="QueueMenu"/>
/// own the wording.
/// </summary>
public sealed class MessageQueue
{
    private readonly object _gate = new();
    private readonly List<QueuedMessage> _entries = [];
    private bool _held;

    /// <summary>How many messages wait.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// A cancelled reply under <c>Queue cancel mode</c> <c>hold</c> sets this: nothing is sent by
    /// itself until the next reply ends normally, which clears it. Inert with nothing queued;
    /// <see cref="Clear"/> resets it too.
    /// </summary>
    public bool Held
    {
        get
        {
            lock (_gate)
            {
                return _held;
            }
        }

        set
        {
            lock (_gate)
            {
                _held = value;
            }
        }
    }

    /// <summary>Adds <paramref name="message"/> at the back.</summary>
    public void Enqueue(QueuedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        int count;
        lock (_gate)
        {
            _entries.Add(message);
            count = _entries.Count;
        }

        DiagnosticLog.Debug(Category, QueuedLogLine(count, message.Label));
    }

    /// <summary>The log category of the queue's lines.</summary>
    public const string Category = "App";

    /// <summary><c>Queued message 2: "…"</c> — its place in the queue and its label (the text, or a paste token). Pinned.</summary>
    public static string QueuedLogLine(int position, string label) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Queued message {position}: {LogText.Quoted(label)}");

    /// <summary><c>Dequeued message: "…" (1 waiting)</c>. Pinned.</summary>
    public static string DequeuedLogLine(string label, int waiting) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Dequeued message: {LogText.Quoted(label)} ({waiting} waiting)");

    /// <summary><c>Removed queued message: "…"</c> — the pane's Enter. Pinned.</summary>
    public static string RemovedLogLine(string label) => "Removed queued message: " + LogText.Quoted(label);

    /// <summary><c>Queue dropped (2 messages)</c> — nothing logged for an empty one. Pinned.</summary>
    public static string DroppedLogLine(int dropped) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Queue dropped ({dropped} message{(dropped == 1 ? "" : "s")})");

    /// <summary>Takes the front entry; false with nothing queued.</summary>
    public bool TryDequeue([System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out QueuedMessage message)
    {
        int waiting;
        lock (_gate)
        {
            if (_entries.Count == 0)
            {
                message = null;
                return false;
            }

            message = _entries[0];
            _entries.RemoveAt(0);
            waiting = _entries.Count;
        }

        DiagnosticLog.Debug(Category, DequeuedLogLine(message.Label, waiting));
        return true;
    }

    /// <summary>
    /// Removes the entry at <paramref name="index"/> when it still reads <paramref name="label"/> — the
    /// pane's rows are a snapshot, and the loop may have sent the front one since. False otherwise.
    /// </summary>
    public bool Remove(int index, string label)
    {
        lock (_gate)
        {
            if (index < 0 || index >= _entries.Count || !string.Equals(_entries[index].Label, label, StringComparison.Ordinal))
            {
                return false;
            }

            _entries.RemoveAt(index);
        }

        DiagnosticLog.Debug(Category, RemovedLogLine(label));
        return true;
    }

    /// <summary>The entries in order, a copy.</summary>
    public IReadOnlyList<QueuedMessage> Snapshot()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }

    /// <summary>Drops every entry and the hold; returns how many were dropped.</summary>
    public int Clear()
    {
        int dropped;
        lock (_gate)
        {
            dropped = _entries.Count;
            _entries.Clear();
            _held = false;
        }

        if (dropped > 0)
        {
            DiagnosticLog.Debug(Category, DroppedLogLine(dropped));
        }

        return dropped;
    }
}
