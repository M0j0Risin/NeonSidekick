using System.Text;

namespace NeonSidekick.WebUi;

/// <summary>What a transcript entry is, as the Web UI's page draws it.</summary>
public enum FeedKind
{
    User,
    Speaker,
    Assistant,
    Tool,
    ToolResult,
    Notice,
    Warning,
    Error,
    Alert,
    Separator,
    Picture,

    /// <summary>A command from the page at work (later on 2026-10-14): the spinner's label, taken out when it ends.</summary>
    Working,
}

/// <summary>What changed in the feed: an entry added, text grown on one, a reply ended, or everything cleared.</summary>
public abstract record FeedChange
{
    public sealed record Appended(WebEntry Entry) : FeedChange;

    public sealed record Grew(long Seq, int At, string Text) : FeedChange;

    public sealed record Ended(long Seq, string Markdown) : FeedChange;

    public sealed record Cleared : FeedChange;

    public sealed record Removed(long Seq) : FeedChange;
}

/// <summary>
/// The transcript as the Web UI shows it (2026-10-14): what <see cref="UI.TranscriptRenderer"/> draws, recorded as entries — a reply one
/// entry that grows by its deltas and keeps its Markdown, a tool its one line, a notice its text. Recorded from the screen's start
/// whether or not the Web UI is on, so turning it on shows the session so far; bounded (<see cref="MaxEntries"/> entries, about
/// <see cref="MaxChars"/> characters, the oldest dropped first). Thread-safe: the turn task writes, a Kestrel thread reads a
/// <see cref="Snapshot"/>. <see cref="Changed"/> runs on the writing task, so its subscribers must be quick (the session only hands
/// the change to the pages' queues). Pure otherwise.
/// A picture entry carries its pictures (later on 2026-10-14): the feed counts the entries holding each picture's id, so the server serves a
/// picture only while an entry holds it (<see cref="Holds"/>) and the screen hears when the last one goes (<see cref="PictureReleased"/>) — a
/// trim, a <see cref="Clear"/> (a new conversation, a profile switch, a restore, a rewind) or a <see cref="Remove"/>.
/// </summary>
public sealed class TranscriptFeed
{
    /// <summary>The most entries kept.</summary>
    public const int MaxEntries = 500;

    /// <summary>About the most characters kept, all entries together.</summary>
    public const int MaxChars = 4_000_000;

    /// <summary>The longest tool result line kept.</summary>
    public const int MaxResultChars = 300;

    private readonly object _gate = new();
    private readonly LinkedList<Entry> _entries = new();
    private long _next = 1;
    private long _chars;
    private Entry? _open;
    private string? _replyGlyph;   // the reply's glyph, kept for its text after a tool line or a notice
    private readonly Dictionary<int, int> _held = [];   // a picture's id → the entries holding it
    private readonly List<int> _released = [];   // ids whose last entry went, raised outside the lock

    /// <summary>Raised after every change, on the writing task.</summary>
    public event Action<FeedChange>? Changed;

    /// <summary>A picture's last entry went (its id); raised on the writing task, outside the feed's lock.</summary>
    public event Action<int>? PictureReleased;

    /// <summary>Whether an entry kept now holds picture <paramref name="id"/> (any thread: the server asks before it serves one).</summary>
    public bool Holds(int id)
    {
        lock (_gate)
        {
            return _held.ContainsKey(id);
        }
    }

    /// <summary>The entries kept now (tests).</summary>
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

    /// <summary>A whole entry of <paramref name="kind"/>; a reply still open is ended first.</summary>
    public long Add(FeedKind kind, string text, string? glyph = null, bool failed = false, IReadOnlyList<WebPicture>? pictures = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (kind == FeedKind.ToolResult && text.Length > MaxResultChars)
        {
            text = text[..MaxResultChars] + "…";
        }

        WebEntry added;
        FeedChange? ended;
        lock (_gate)
        {
            ended = CloseOpen();
            var entry = Push(kind, text, glyph, failed, pictures is { Count: > 0 } ? pictures : null);
            added = entry.ToWeb(null);
        }

        Raise(ended);
        Raise(new FeedChange.Appended(added));
        RaiseReleased();
        return added.Seq;
    }

    /// <summary>Takes entry <paramref name="seq"/> out (the splash cycled to another picture); false when it is gone already or still streaming.</summary>
    public bool Remove(long seq)
    {
        lock (_gate)
        {
            var node = _entries.First;
            while (node is not null && node.Value.Seq != seq)
            {
                node = node.Next;
            }

            if (node is null || node.Value == _open)
            {
                return false;
            }

            _chars -= node.Value.Text.Length;
            _entries.Remove(node);
            Release(node.Value);
        }

        Raise(new FeedChange.Removed(seq));
        RaiseReleased();
        return true;
    }

    /// <summary>A reply starts: an entry that <see cref="Append"/> grows until <see cref="End"/>.</summary>
    public long Begin(string? glyph)
    {
        WebEntry added;
        FeedChange? ended;
        lock (_gate)
        {
            ended = CloseOpen();
            _replyGlyph = glyph;
            _open = Push(FeedKind.Assistant, "", glyph, failed: false);
            added = _open.ToWeb(null);
        }

        Raise(ended);
        Raise(new FeedChange.Appended(added));
        RaiseReleased();
        return added.Seq;
    }

    /// <summary>Text for the reply streaming now; a reply is begun when none is open (text written with no begin).</summary>
    public void Append(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return;
        }

        FeedChange? appended = null;
        FeedChange grew;
        lock (_gate)
        {
            if (_open is null)
            {
                _open = Push(FeedKind.Assistant, "", _replyGlyph, failed: false);
                appended = new FeedChange.Appended(_open.ToWeb(null));
            }

            var open = _open;
            int at;
            lock (open)
            {
                // The entry's own lock too: a snapshot reads its text under that lock alone.
                at = open.Text.Length;
                open.Text.Append(text);
            }

            _chars += text.Length;
            grew = new FeedChange.Grew(_open.Seq, at, text);
            Trim();
        }

        Raise(appended);
        Raise(grew);
        RaiseReleased();
    }

    /// <summary>The reply streaming now is over: its Markdown is final.</summary>
    public void End()
    {
        FeedChange? ended;
        lock (_gate)
        {
            ended = CloseOpen();
        }

        Raise(ended);
    }

    /// <summary>Everything gone: the screen was wiped for a new conversation.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _chars = 0;
            _open = null;
            _replyGlyph = null;
            _released.AddRange(_held.Keys);
            _held.Clear();
        }

        Raise(new FeedChange.Cleared());
        RaiseReleased();
    }

    /// <summary>The entries kept now, oldest first; <paramref name="html"/> renders a finished reply's Markdown (each one once, cached).</summary>
    public IReadOnlyList<WebEntry> Snapshot(Func<string, string> html)
    {
        ArgumentNullException.ThrowIfNull(html);
        List<Entry> entries;
        lock (_gate)
        {
            entries = [.. _entries];
        }

        var list = new List<WebEntry>(entries.Count);
        foreach (var entry in entries)
        {
            list.Add(entry.ToWeb(html));
        }

        return list;
    }

    private Entry Push(FeedKind kind, string text, string? glyph, bool failed, IReadOnlyList<WebPicture>? pictures = null)
    {
        var entry = new Entry(_next++, kind, glyph, failed, pictures);
        entry.Text.Append(text);
        _entries.AddLast(entry);
        _chars += text.Length;
        foreach (var picture in pictures ?? [])
        {
            _held[picture.Id] = _held.GetValueOrDefault(picture.Id) + 1;
        }

        Trim();
        return entry;
    }

    /// <summary>An entry's pictures let go (under the lock): an id no entry holds any more is raised after it.</summary>
    private void Release(Entry entry)
    {
        foreach (var picture in entry.Pictures ?? [])
        {
            if (!_held.TryGetValue(picture.Id, out int count))
            {
                continue;
            }

            if (count <= 1)
            {
                _held.Remove(picture.Id);
                _released.Add(picture.Id);
            }
            else
            {
                _held[picture.Id] = count - 1;
            }
        }
    }

    private void RaiseReleased()
    {
        int[] released;
        lock (_gate)
        {
            if (_released.Count == 0)
            {
                return;
            }

            // An id held again since (the same picture added anew) is not let go.
            released = [.. _released.Where(id => !_held.ContainsKey(id)).Distinct()];
            _released.Clear();
        }

        foreach (int id in released)
        {
            PictureReleased?.Invoke(id);
        }
    }

    private FeedChange? CloseOpen()
    {
        if (_open is not { } open)
        {
            return null;
        }

        _open = null;
        lock (open)
        {
            open.Ended = true;
        }

        return new FeedChange.Ended(open.Seq, open.Text.ToString());
    }

    private void Trim()
    {
        while (_entries.Count > 1 && (_entries.Count > MaxEntries || _chars > MaxChars) && _entries.First is { } first && first.Value != _open)
        {
            _chars -= first.Value.Text.Length;
            _entries.RemoveFirst();
            Release(first.Value);
        }
    }

    private void Raise(FeedChange? change)
    {
        if (change is not null)
        {
            Changed?.Invoke(change);
        }
    }

    private sealed class Entry(long seq, FeedKind kind, string? glyph, bool failed, IReadOnlyList<WebPicture>? pictures)
    {
        private string? _html;
        private int _htmlLength = -1;

        public long Seq { get; } = seq;

        public FeedKind Kind { get; } = kind;

        public string? Glyph { get; } = glyph;

        public bool Failed { get; } = failed;

        public IReadOnlyList<WebPicture>? Pictures { get; } = pictures;

        public StringBuilder Text { get; } = new();

        /// <summary>A reply whose text is final (read under the feed's lock or after).</summary>
        public volatile bool Ended;

        public WebEntry ToWeb(Func<string, string>? html)
        {
            string text;
            bool ended;
            lock (this)
            {
                text = Text.ToString();
                ended = Ended;
            }

            string? rendered = null;
            if (html is not null && Kind == FeedKind.Assistant && ended)
            {
                lock (this)
                {
                    if (_htmlLength != text.Length)
                    {
                        _html = html(text);
                        _htmlLength = text.Length;
                    }

                    rendered = _html;
                }
            }

            return new WebEntry(Seq, KindName(Kind), text, Glyph, rendered, Failed, Pictures);
        }
    }

    /// <summary>The kind's word on the wire.</summary>
    public static string KindName(FeedKind kind) => kind switch
    {
        FeedKind.User => "user",
        FeedKind.Speaker => "speaker",
        FeedKind.Assistant => "assistant",
        FeedKind.Tool => "tool",
        FeedKind.ToolResult => "result",
        FeedKind.Notice => "notice",
        FeedKind.Warning => "warning",
        FeedKind.Error => "error",
        FeedKind.Alert => "alert",
        FeedKind.Separator => "separator",
        FeedKind.Picture => "picture",
        FeedKind.Working => "working",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>A working line's end (later on 2026-10-14): taken out, so the pages drop it and a snapshot never shows a finished one.</summary>
    public void EndWorking(long seq) => Remove(seq);
}
