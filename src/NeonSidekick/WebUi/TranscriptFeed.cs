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

    /// <summary>The model's thinking (2026-10-15), streamed, then folded under its summary.</summary>
    Thinking,

    /// <summary>A <c>/cmd</c> run's output (2026-10-15): one stream's consecutive lines.</summary>
    Output,

    /// <summary>A tool run's head (2026-10-15): its summary over the tool lines that carry its seq.</summary>
    Run,
}

/// <summary>What changed in the feed: an entry added, text grown on one, an entry ended or its text replaced, one removed, or everything cleared.</summary>
public abstract record FeedChange
{
    public sealed record Appended(WebEntry Entry) : FeedChange;

    public sealed record Grew(long Seq, int At, string Text) : FeedChange;

    /// <summary>An entry is over: a reply's final Markdown, a thinking block's summary, a run or an output ended (2026-10-15: the kind and summary).</summary>
    public sealed record Ended(long Seq, string Markdown, FeedKind Kind = FeedKind.Assistant, string? Summary = null) : FeedChange;

    /// <summary>A run's summary changed (2026-10-15): a call counted, a result failed.</summary>
    public sealed record Replaced(long Seq, string Text, bool Failed) : FeedChange;

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
/// What the terminal shows besides (2026-10-15, the plan's Part B): a reply is begun lazily — its entry pushed at its first words, so no empty
/// bubble stands before tool lines, and one that said nothing at all ends as <see cref="WebEntry.NoReply"/>; a stretch after a break is
/// <see cref="WebEntry.Continues"/> once the reply has spoken (the PC's flush-left text); the thinking streams into an entry of its own, folded
/// under <c>ThinkingFoldText</c>'s summary when anything else is written; a <c>/cmd</c> run's lines group per stream; and while
/// <see cref="ToolKeep"/> is above 0 the tool lines gather under a run's head whose summary follows the renderer's tally.
/// </summary>
public sealed class TranscriptFeed
{
    /// <summary>The most entries kept.</summary>
    public const int MaxEntries = 500;

    /// <summary>About the most characters kept, all entries together.</summary>
    public const int MaxChars = 4_000_000;

    /// <summary>The longest tool result line kept.</summary>
    public const int MaxResultChars = 300;

    /// <summary>The most characters one thinking block keeps (2026-10-15); past it the block ends with "…" and grows no more.</summary>
    public const int MaxThinkingChars = 64_000;

    /// <summary>
    /// The most characters one <c>/cmd</c> output entry keeps (2026-10-19, the review: a command printing a long log grew one entry without bound,
    /// and the trim could not drop the entry growing); past it the entry ends with "…" and grows no more, as a thinking block does.
    /// </summary>
    public const int MaxOutputChars = 64_000;

    private readonly object _gate = new();
    private readonly LinkedList<Entry> _entries = new();
    private long _next = 1;
    private long _chars;
    private Entry? _open;   // the entry growing now: a reply's stretch, a thinking block, or a stream of output
    private string? _replyGlyph;   // the reply's glyph, kept for its text after a tool line or a notice
    private bool _replyOpen;   // Begin was called and End not yet
    private bool _replyWrote;   // anything was written since Begin
    private bool _replySpoke;   // the reply's own text was written since Begin
    private Entry? _run;   // the open tool run's head
    private string _runSummary = "";   // the renderer's tally, held for the next run when none is open
    private bool _runFailed;
    private readonly Dictionary<int, int> _held = [];   // a picture's id → the entries holding it
    private readonly List<int> _released = [];   // ids whose last entry went, raised outside the lock

    /// <summary>Raised after every change, on the writing task.</summary>
    public event Action<FeedChange>? Changed;

    /// <summary>A picture's last entry went (its id); raised on the writing task, outside the feed's lock.</summary>
    public event Action<int>? PictureReleased;

    /// <summary>
    /// How many units of a tool run stay while it runs (2026-10-15, the screen's <c>Tool collapse count</c>, read on the writing task as a run
    /// opens): above 0 the tool lines gather under a run's head, 0 (the default) leaves them flat.
    /// </summary>
    public Func<int>? ToolKeep { get; set; }

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

    /// <summary>
    /// A whole entry of <paramref name="kind"/>; whatever grows now is ended first. A tool line (<see cref="FeedKind.Tool"/>,
    /// <see cref="FeedKind.ToolResult"/>) joins the open run, opening one while <see cref="ToolKeep"/> is above 0; any other kind ends the run.
    /// <paramref name="joins"/> marks a tool line that completes the call before it (one unit); <paramref name="diff"/> is an edit's rows;
    /// <paramref name="splash"/> (2026-10-18) marks the welcome splash's pictures, which the page draws first, centered and filling it;
    /// <paramref name="tone"/> (2026-10-21) is a notice's role, the page's <c>tone-</c> class (<see cref="ToneWord"/>).
    /// </summary>
    public long Add(FeedKind kind, string text, string? glyph = null, bool failed = false, IReadOnlyList<WebPicture>? pictures = null, bool joins = false, WebDiff? diff = null, bool splash = false, UI.NoticeTone tone = UI.NoticeTone.Plain)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (kind == FeedKind.ToolResult && text.Length > MaxResultChars)
        {
            text = text[..MaxResultChars] + "…";
        }

        WebEntry added;
        var changes = new List<FeedChange>(3);
        lock (_gate)
        {
            Add(changes, CloseOpen());
            bool tool = kind is FeedKind.Tool or FeedKind.ToolResult;
            if (!tool)
            {
                Add(changes, EndRunLocked());
            }
            else if (_run is null && ToolKeep?.Invoke() is > 0 and int keep)
            {
                _run = Push(FeedKind.Run, _runSummary, null, _runFailed);
                _run.Keep = keep;
                changes.Add(new FeedChange.Appended(_run.ToWeb(null)));
            }

            var entry = Push(kind, text, glyph, failed, pictures is { Count: > 0 } ? pictures : null);
            if (tool && _run is { } run)
            {
                entry.Run = run.Seq;
                entry.Joins = joins;
            }

            entry.Diff = diff;
            entry.Splash = splash;
            entry.Tone = tone;
            _replyWrote |= _replyOpen;
            added = entry.ToWeb(null);
        }

        changes.Add(new FeedChange.Appended(added));
        RaiseAll(changes);
        return added.Seq;
    }

    /// <summary>Takes entry <paramref name="seq"/> out (the splash cycled to another picture, or gone at the first sent line since 2026-10-18); false when it is gone already or still streaming.</summary>
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

    /// <summary>
    /// A reply starts (2026-10-15: lazily — nothing is pushed until its first words, so a reply that only calls tools leaves no empty entry):
    /// whatever grows now ends, and the run with it.
    /// </summary>
    public void Begin(string? glyph)
    {
        var changes = new List<FeedChange>(2);
        lock (_gate)
        {
            Add(changes, CloseOpen());
            Add(changes, EndRunLocked());
            _replyGlyph = glyph;
            _replyOpen = true;
            _replyWrote = false;
            _replySpoke = false;
        }

        RaiseAll(changes);
    }

    /// <summary>
    /// Text for the reply streaming now. A new stretch (after a tool line, a thinking block, a notice, or with no <see cref="Begin"/>) starts at
    /// its first non-blank text, its leading blanks dropped as the PC drops them, and is <see cref="WebEntry.Continues"/> once the reply has spoken.
    /// </summary>
    public void Append(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return;
        }

        var changes = new List<FeedChange>(4);
        lock (_gate)
        {
            if (_open is not { Kind: FeedKind.Assistant })
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    return;   // the blanks between a break and the words are nobody's
                }

                Add(changes, CloseOpen());
                Add(changes, EndRunLocked());
                _open = Push(FeedKind.Assistant, "", _replyGlyph, failed: false);
                _open.Continues = _replySpoke;
                changes.Add(new FeedChange.Appended(_open.ToWeb(null)));
                text = text.TrimStart();
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
            _replyWrote |= _replyOpen;
            _replySpoke = true;
            changes.Add(new FeedChange.Grew(open.Seq, at, text));
            Trim();
        }

        RaiseAll(changes);
    }

    /// <summary>
    /// The reply is over: what grows ends. A reply that wrote nothing at all since <see cref="Begin"/> — no words, no tool line, no thinking, no
    /// notice — ends as an entry of its own marked <see cref="WebEntry.NoReply"/>, the PC's "(no reply)".
    /// </summary>
    public void End()
    {
        var changes = new List<FeedChange>(2);
        lock (_gate)
        {
            Add(changes, CloseOpen());
            if (_replyOpen && !_replyWrote)
            {
                var empty = Push(FeedKind.Assistant, "", _replyGlyph, failed: false);
                empty.NoReply = true;
                empty.Ended = true;
                changes.Add(new FeedChange.Appended(empty.ToWeb(null)));
            }

            _replyOpen = false;
        }

        RaiseAll(changes);
    }

    /// <summary>
    /// The model's thinking (2026-10-15): grows the open thinking block, or starts one at its first non-blank text (whatever else grows ends).
    /// <paramref name="elapsed"/> is how long it has streamed, the summary's figure when it folds. Past <see cref="MaxThinkingChars"/> the block
    /// ends with "…" and keeps nothing more.
    /// </summary>
    public void AppendThinking(string text, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(text);
        var changes = new List<FeedChange>(4);
        lock (_gate)
        {
            if (_open is not { Kind: FeedKind.Thinking })
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    return;
                }

                Add(changes, CloseOpen());
                Add(changes, EndRunLocked());
                _open = Push(FeedKind.Thinking, "", null, failed: false);
                changes.Add(new FeedChange.Appended(_open.ToWeb(null)));
                text = text.TrimStart();
                _replyWrote |= _replyOpen;
            }

            var open = _open;
            open.Elapsed = elapsed;
            if (open.Capped)
            {
                return;
            }

            int room = MaxThinkingChars - open.Text.Length;
            if (text.Length >= room)
            {
                text = text[..Math.Max(0, room)] + "…";
                open.Capped = true;
            }

            int at;
            lock (open)
            {
                at = open.Text.Length;
                open.Text.Append(text);
            }

            _chars += text.Length;
            changes.Add(new FeedChange.Grew(open.Seq, at, text));
            Trim();
        }

        RaiseAll(changes);
    }

    /// <summary>
    /// A line a <c>/cmd</c> run printed (2026-10-15): joins the open output of the same stream, or starts one (stderr's marked failed); any other
    /// write ends it. Never part of a tool run. Past <see cref="MaxOutputChars"/> the entry ends with "…" and keeps nothing more.
    /// </summary>
    public void AppendOutput(string text, bool isError)
    {
        ArgumentNullException.ThrowIfNull(text);
        var changes = new List<FeedChange>(4);
        lock (_gate)
        {
            if (_open is { Kind: FeedKind.Output } same && same.Failed == isError)
            {
                text = "\n" + text;
            }
            else
            {
                Add(changes, CloseOpen());
                Add(changes, EndRunLocked());
                _open = Push(FeedKind.Output, "", null, isError);
                changes.Add(new FeedChange.Appended(_open.ToWeb(null)));
                _replyWrote |= _replyOpen;
            }

            var open = _open;
            if (open.Capped)
            {
                RaiseAll(changes);
                return;
            }

            int room = MaxOutputChars - open.Text.Length;
            if (text.Length >= room)
            {
                text = text[..Math.Max(0, room)] + "…";
                open.Capped = true;
            }

            int at;
            lock (open)
            {
                at = open.Text.Length;
                open.Text.Append(text);
            }

            _chars += text.Length;
            changes.Add(new FeedChange.Grew(open.Seq, at, text));
            Trim();
        }

        RaiseAll(changes);
    }

    /// <summary>
    /// The tool run's summary as the renderer's tally reads now (2026-10-15, <c>ToolGroupText.Bare</c>): the open run's head is rewritten, or,
    /// with none open (the call is counted before its line is written), it is held for the next one.
    /// </summary>
    public void ToolSummary(string text, bool failed)
    {
        ArgumentNullException.ThrowIfNull(text);
        FeedChange? replaced = null;
        lock (_gate)
        {
            _runSummary = text;
            _runFailed = failed;
            if (_run is { } run)
            {
                lock (run)
                {
                    _chars += text.Length - run.Text.Length;
                    run.Text.Clear().Append(text);
                }

                run.Failed = failed;
                replaced = new FeedChange.Replaced(run.Seq, text, failed);
            }
        }

        Raise(replaced);
    }

    /// <summary>The tool run is over (2026-10-15, the renderer's own end of a run): its head ends, so a page folds a long one; the tally starts again.</summary>
    public void EndToolRun()
    {
        FeedChange? ended;
        lock (_gate)
        {
            ended = EndRunLocked();
            _runSummary = "";
            _runFailed = false;
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
            _run = null;
            _replyGlyph = null;
            _replyOpen = false;
            _replyWrote = false;
            _replySpoke = false;
            _runSummary = "";
            _runFailed = false;
            _released.AddRange(_held.Keys);
            _held.Clear();
        }

        Raise(new FeedChange.Cleared());
        RaiseReleased();
    }

    /// <summary>
    /// The entries kept now, oldest first; <paramref name="html"/> renders a finished reply's Markdown (each one once, cached). With
    /// <paramref name="defer"/> (2026-10-15, a page's reconnect, often on mobile data) a finished thinking block goes as its summary alone and a folded
    /// diff of more than <see cref="DeferRows"/> rows as its head and fold alone, each <see cref="WebEntry.Deferred"/>: the page reads it whole from
    /// <see cref="EntryOf"/> when it is opened.
    /// </summary>
    public IReadOnlyList<WebEntry> Snapshot(Func<string, string> html, bool defer = false)
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
            list.Add(entry.ToWeb(html, defer));
        }

        return list;
    }

    /// <summary>The diff rows past which a snapshot defers a folded diff (2026-10-15).</summary>
    public const int DeferRows = 200;

    /// <summary>Entry <paramref name="seq"/> whole while the feed holds it (2026-10-15, a deferred one opened on the page), or null.</summary>
    public WebEntry? EntryOf(long seq, Func<string, string> html)
    {
        ArgumentNullException.ThrowIfNull(html);
        Entry? found;
        lock (_gate)
        {
            found = _entries.FirstOrDefault(e => e.Seq == seq);
        }

        return found?.ToWeb(html);
    }

    /// <summary>
    /// Finished reply <paramref name="seq"/>'s Markdown as HTML through the entry's own cache (2026-10-19, the review: the <c>end</c> event rendered
    /// it a second time beside the snapshot's): rendered once whoever asks first. Null when the feed no longer holds it or it is no finished reply.
    /// Looked for from the newest, where a reply that just ended is.
    /// </summary>
    public string? HtmlOf(long seq, Func<string, string> html)
    {
        ArgumentNullException.ThrowIfNull(html);
        Entry? found = null;
        lock (_gate)
        {
            for (var node = _entries.Last; node is not null; node = node.Previous)
            {
                if (node.Value.Seq == seq)
                {
                    found = node.Value;
                    break;
                }
            }
        }

        return found?.Html(html);
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

    /// <summary>Under the lock: the growing entry ends — a reply's Markdown is final, a thinking block takes its summary.</summary>
    private FeedChange? CloseOpen()
    {
        if (_open is not { } open)
        {
            return null;
        }

        _open = null;
        string text;
        lock (open)
        {
            open.Ended = true;
            text = open.Text.ToString();
        }

        if (open.Kind == FeedKind.Thinking)
        {
            open.Summary = UI.ThinkingFoldText.Bare(open.Elapsed, text);
        }

        return new FeedChange.Ended(open.Seq, text, open.Kind, open.Summary);
    }

    /// <summary>Under the lock: the open run's head ends.</summary>
    private FeedChange? EndRunLocked()
    {
        if (_run is not { } run)
        {
            return null;
        }

        _run = null;
        lock (run)
        {
            run.Ended = true;
        }

        return new FeedChange.Ended(run.Seq, "", FeedKind.Run);
    }

    /// <summary>
    /// The oldest entries dropped while the feed holds too many or too much. The entry growing and the open run's head are passed over, never a
    /// stop (2026-10-19, the review: once either was the oldest nothing more was dropped, and a long run or output grew the feed without bound),
    /// and so is the newest, which <see cref="Push"/> has just added for its caller.
    /// </summary>
    private void Trim()
    {
        var node = _entries.First;
        while (node is not null && node != _entries.Last && (_entries.Count > MaxEntries || _chars > MaxChars))
        {
            var next = node.Next;
            if (node.Value != _open && node.Value != _run)
            {
                _chars -= node.Value.Text.Length;
                _entries.Remove(node);
                Release(node.Value);
            }

            node = next;
        }
    }

    private static void Add(List<FeedChange> changes, FeedChange? change)
    {
        if (change is not null)
        {
            changes.Add(change);
        }
    }

    private void RaiseAll(List<FeedChange> changes)
    {
        foreach (var change in changes)
        {
            Raise(change);
        }

        RaiseReleased();
    }

    private void Raise(FeedChange? change)
    {
        if (change is not null)
        {
            Changed?.Invoke(change);
        }
    }

    /// <summary>The page's name for a kind.</summary>
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
        FeedKind.Thinking => "thinking",
        FeedKind.Output => "output",
        FeedKind.Run => "run",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>
    /// The page's word for a notice's tone (2026-10-21): null for a plain one, else the <c>tone-</c> class <c>app.css</c> draws it in. A fixed
    /// set, so nothing but these words reaches the page's class list.
    /// </summary>
    public static string? ToneWord(UI.NoticeTone tone) => tone switch
    {
        UI.NoticeTone.Plain => null,
        UI.NoticeTone.Bad => "bad",
        UI.NoticeTone.Warn => "warn",
        UI.NoticeTone.Dim => "dim",
        UI.NoticeTone.Progress => "progress",
        _ => throw new ArgumentOutOfRangeException(nameof(tone), tone, null),
    };

    private sealed class Entry(long seq, FeedKind kind, string? glyph, bool failed, IReadOnlyList<WebPicture>? pictures)
    {
        private string? _html;
        private int _htmlLength = -1;

        public long Seq { get; } = seq;

        public FeedKind Kind { get; } = kind;

        public string? Glyph { get; } = glyph;

        /// <summary>A failed result, stderr's output, or a run with a failed result (the run's changes as results land).</summary>
        public bool Failed { get; set; } = failed;

        public IReadOnlyList<WebPicture>? Pictures { get; } = pictures;

        public StringBuilder Text { get; } = new();

        /// <summary>An entry whose text is final (read under the feed's lock or after).</summary>
        public volatile bool Ended;

        /// <summary>A reply's stretch after a break, once the reply had spoken: drawn with no glyph.</summary>
        public bool Continues { get; set; }

        /// <summary>A reply that wrote nothing at all.</summary>
        public bool NoReply { get; set; }

        /// <summary>A thinking block's summary once it folds.</summary>
        public string? Summary { get; set; }

        /// <summary>How long a thinking block has streamed.</summary>
        public TimeSpan Elapsed { get; set; }

        /// <summary>A thinking block cut at <see cref="MaxThinkingChars"/>, or an output entry at <see cref="MaxOutputChars"/>.</summary>
        public bool Capped { get; set; }

        /// <summary>A tool line's run (its head's seq).</summary>
        public long? Run { get; set; }

        /// <summary>A tool line that completes the call before it.</summary>
        public bool Joins { get; set; }

        /// <summary>A run's keep count.</summary>
        public int? Keep { get; set; }

        /// <summary>An edit's rows.</summary>
        public WebDiff? Diff { get; set; }

        /// <summary>The welcome splash's pictures (2026-10-18).</summary>
        public bool Splash { get; set; }

        /// <summary>A notice's role (2026-10-21).</summary>
        public UI.NoticeTone Tone { get; set; }

        /// <summary>A finished reply's Markdown as HTML, rendered once and cached (re-rendered only if its text changed); null for anything else.</summary>
        public string? Html(Func<string, string> html)
        {
            if (Kind != FeedKind.Assistant || NoReply)
            {
                return null;
            }

            lock (this)
            {
                if (!Ended)
                {
                    return null;
                }

                string text = Text.ToString();
                if (_htmlLength != text.Length)
                {
                    _html = html(text);
                    _htmlLength = text.Length;
                }

                return _html;
            }
        }

        public WebEntry ToWeb(Func<string, string>? html, bool defer = false)
        {
            string text;
            bool ended;
            lock (this)
            {
                text = Text.ToString();
                ended = Ended;
            }

            // A reconnect's snapshot leaves out what a page reads on opening it (2026-10-15).
            bool deferText = defer && Kind == FeedKind.Thinking && ended && text.Length > 0;
            bool deferDiff = defer && Diff is { Fold: > 0 } folded && folded.Rows.Count > DeferRows;

            string? rendered = html is null ? null : Html(html);
            return new WebEntry(Seq, KindName(Kind), deferText ? "" : text, Glyph, rendered, Failed, Pictures)
            {
                Continues = Continues ? true : null,
                NoReply = NoReply ? true : null,
                Summary = Summary,
                Ended = Kind is FeedKind.Run or FeedKind.Thinking or FeedKind.Output && ended ? true : null,
                Run = Run,
                Joins = Joins ? true : null,
                Keep = Keep,
                Diff = deferDiff ? Diff! with { Rows = [], More = null } : Diff,
                Deferred = deferText || deferDiff ? true : null,
                Splash = Splash ? true : null,
                Tone = ToneWord(Tone),
            };
        }
    }
}
