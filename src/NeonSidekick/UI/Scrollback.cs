using System.Text;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.UI;

/// <summary>
/// The transcript the pane wrote, kept by the app: the alternate screen buffer the screen runs in
/// has no scrollback, so <see cref="ScreenPane"/> retains every flow write here and paints a window
/// of it while the user scrolls (PgUp/PgDn). The source is the <em>logical lines</em> — the segments
/// between line breaks, styles kept, an open last line while no break has ended it; the rows are
/// those lines wrapped at one width with the terminal's own rule, the one <c>ScreenPane.Track</c>
/// counts by (<see cref="TextCells"/> widths, a row left only when the next character does not
/// fit), so the count and the store agree cell for cell. Not Spectre's <c>SplitLines</c>: it measures
/// with its own cell table, keeps a wide character on the row the terminal wraps it from, and
/// drops a blank line inside one text segment. The rows are cached at the last width asked for,
/// extended on every append (the open line wrapped again with what joins it), rebuilt on a width
/// change; past <see cref="MaxRows"/> the oldest lines go. No lock of its own: the pane calls it
/// under its own.
///
/// <para>Tool runs (2026-09-22, the user's ask): a run of consecutive 🛠️ lines is a <em>group</em> —
/// a summary line the store keeps ahead of its members (<see cref="BeginGroup"/>, the members through
/// <see cref="Append(IReadOnlyList{Segment}, int, bool)"/> with <c>member</c>). Once a group holds more
/// members than its <c>keep</c> (the <c>Tool collapse count</c> it opened with; 0 never collapses) the
/// summary shows and the members fold — a member counted by the write it came in with since 2026-10-03, so
/// an edit's note and its diff are one: while the run is live only its last <c>keep</c> stay, after
/// <see cref="EndGroup"/> none — unless the group is expanded (<see cref="Toggle"/>, or the pane-wide
/// <see cref="ExpandAll"/> it follows until toggled on its own). A hidden line takes no rows, so the
/// rows, the scroll and the pane's hit-tests all read the folded shape. Any change to rows other than
/// an append at the end sets <see cref="TakeReshaped"/>: the pane then rebuilds the screen from here.</para>
///
/// <para>Code blocks (later on 2026-09-22, the user's ask): a top-level code block of a styled reply
/// is a group too (<see cref="BeginCodeGroup(int, IReadOnlyList{Segment})"/>), its label line the summary and its rows the
/// members. It differs from a tool run in three ways: the summary always shows (the plain label
/// until the block folds), it is measured by its source lines rather than its rows (a wrapped line
/// counts once), and while it is live every member shows — the block streamed at full height and
/// folds only once it is over. <see cref="ExpandAll"/> and <see cref="Toggle"/> are the runs'.</para>
///
/// <para>Thinking (2026-09-26, the user's ask): the model's thinking block is a group too
/// (<see cref="BeginThinkingGroup(IReadOnlyList{Segment})"/>), its header line the summary and its rows the members. It is a
/// code block's kind — the header shows as drawn and every member streams while it is live — except
/// that it keeps nothing: once over it always folds, to <c>▸ 💭 thought for 4.2s</c>, and unfolds as the
/// runs do (a click, <see cref="ExpandAll"/>).</para>
///
/// <para>Diffs (2026-10-04, the user's ask, <c>Diff collapse count</c>): an edit's diff is a group too, the only one that nests —
/// its lines stay members of the tool run they came in (<see cref="Line.Group"/>) and are a diff's besides (<see cref="Line.Fold"/>).
/// A write carries it as a <see cref="FoldSpec"/>: its <c>└ Added …</c> row the summary, the rows under it the members, measured by
/// the diff's rows. It is a code block's kind while live (every row shows, the summary as drawn) and stays live until the run it
/// came in ends — with no run, until the next write of any kind (<see cref="EndGroup"/>); past its keep it then folds to
/// <c>▸ Added 3 lines, removed 1 line · 14 rows</c>, and unfolds as the runs do. A run folded over it hides it whole.</para>
///
/// <para>A resize (2026-10-29, the user's report: the banner and the transcript did not follow one; the resize review's fixes the same
/// day): a standalone write the pane can render again is kept as <em>one</em> line holding its own lines (<see cref="Line.Parts"/>) and
/// the renderable (<see cref="Source"/>), laid out again at a new width, the newest first within <see cref="RenderBudget"/> and the rest
/// once the size holds (<see cref="RenderPending"/>). Every other line remembers the width it was written at and, narrower, wraps by a
/// cleaner rule than the terminal's (<see cref="Cut"/>). Rows the pane holds — the scroll, the find — follow as <see cref="Anchor"/>s.</para>
/// </summary>
public sealed class Scrollback
{
    /// <summary>The most rows kept; whole lines are dropped from the front past it (a constant, not a setting).</summary>
    public const int MaxRows = 10_000;

    /// <summary>
    /// The most lines a write may have and still be rendered again on a resize (2026-10-29, the resize review): a write is one line
    /// in the store, so the cap drops it whole — a longer one stays plain lines, wrapped by the narrower rule, as any reply is.
    /// </summary>
    public const int MaxSourceLines = 500;

    /// <summary>
    /// What <see cref="Render"/> answers for a source it will not render now (2026-10-29, the resize review: a container written under
    /// another theme than the one in force): the store wraps it this time and keeps it. Compared by reference.
    /// </summary>
    public static readonly IReadOnlyList<Segment> NotNow = new List<Segment>().AsReadOnly();

    private readonly List<Line> _lines = new();
    private readonly List<SegmentLine> _rows = new();
    private readonly Dictionary<int, Group> _groups = new();
    private int _width = -1;
    private Group? _open;
    private int _nextGroup = 1;
    private bool _reshaped;

    // The lines ever dropped from the front (2026-10-29, the resize review): a Place counts its lines from the first line the store
    // ever held, so a mark taken before the cap moved (or a Clear) reads as gone, never as some other line.
    private int _frontDropped;

    // The diffs still live (2026-10-04): they fold, past their keep, when the run they came in ends or the next write comes.
    private readonly List<Group> _liveDiffs = new();

    /// <summary>
    /// A diff in a write (2026-10-04, <see cref="IFoldLayout"/>): its summary is the write's line <paramref name="Head"/> (counted from the
    /// first line the write touches, an open line it continues included), its members every line after it in the write; past
    /// <paramref name="Keep"/> (0 = never) by its <paramref name="Size"/> it folds once over, its summary then <paramref name="Collapsed"/>
    /// or <paramref name="Expanded"/>.
    /// </summary>
    public sealed record FoldSpec(int Head, int Keep, int Size, IReadOnlyList<Segment> Collapsed, IReadOnlyList<Segment> Expanded);

    /// <summary>One logical line: its segments (no line break, no control code, no <c>\r</c>) and the rows it took at the cached width.</summary>
    private sealed class Line
    {
        public readonly List<Segment> Segments = new();
        public bool Closed;
        public int Rows;

        /// <summary>The tool run this line is the summary or a member of; null for any other line.</summary>
        public Group? Group;

        /// <summary>The member's place in its group; −1 for the summary.</summary>
        public int Member = -1;

        /// <summary>
        /// The write the member came in with, its place among the group's <see cref="Group.Units"/> (2026-10-03, the diffs under file
        /// edits): what a run's keep count counts, so a write of many lines — an edit's note and its diff — is one, as every
        /// one-line tool note always was.
        /// </summary>
        public int Unit;

        /// <summary>The pictures drawn on this line and their columns (later on 2026-09-24): what a double-click there opens. Null for any other line.</summary>
        public IReadOnlyList<PictureSpan>? Pictures;

        /// <summary>The diff this line is the summary or a row of (2026-10-04), beside the run in <see cref="Group"/>; null for any other line.</summary>
        public Group? Fold;

        /// <summary>The line is its <see cref="Fold"/>'s summary row (the diff's <see cref="Group.Summary"/>).</summary>
        public bool FoldHead;

        /// <summary>
        /// The width the line was written at (2026-10-29, the user's report: a narrowed window cut every line by the character —
        /// rules spilled a stub, words broke, padding took rows): set by the append that opens it (an open line joined at another width
        /// keeps the wider of the two, the resize review), and on a summary when its look is set. Narrower than it, the line is wrapped
        /// by the cleaner rules (<see cref="Cut"/>); 0 (never set) keeps the exact cut.
        /// </summary>
        public int BornWidth;

        /// <summary>The leading blank cells a narrowed picture line dropped at the cached width (2026-10-29): <see cref="PictureAt"/> adds them to a click's column.</summary>
        public int Shift;

        /// <summary>
        /// A whole write's own lines at its <see cref="BornWidth"/> (2026-10-29, the resize review: a write spread over a head line and
        /// lines of no rows lost rows in every partial re-wrap): the line is that write, closed; <see cref="Segments"/> stays empty. Null
        /// for any other line.
        /// </summary>
        public List<List<Segment>>? Parts;

        /// <summary>What <see cref="Parts"/> came from, to render again at another width; null once a render failed (the parts wrap then).</summary>
        public Source? Reflow;
    }

    /// <summary>
    /// A write the store can render again at another width (2026-10-29, the user's report: the banner and the transcript did not
    /// follow a resize): the renderable it came from (a notice, a sent line, the banner — <c>ScreenPane</c> decides which) and the width
    /// it was written at. At that width its line shows its own parts; at another, the rows the renderable gave there. One other width's
    /// rows are kept, so a fold's re-layout renders nothing — and, since 2026-10-30 (review F33), going home and back renders nothing either.
    /// </summary>
    private sealed class Source(IRenderable renderable, int width)
    {
        public readonly IRenderable Renderable = renderable;
        public readonly int Width = width;
        public int CachedWidth = -1;

        /// <summary>Its lines rendered at <see cref="CachedWidth"/>; null there when the render lost text (the parts wrap at that width).</summary>
        public List<List<Segment>>? Cached;
    }

    /// <summary>A write that left its last line open (2026-10-29): kept for a source only if a bare line break closes that line next.</summary>
    private sealed record Waiting(IRenderable Renderable, int Born, Line First);

    /// <summary>
    /// Renders a <see cref="Source"/> at a width (2026-10-29): the segments as a flow write of it there would give, null when it
    /// failed — that source is then dropped for good and its parts wrap — or <see cref="NotNow"/>. <c>ScreenPane</c> sets it; without one,
    /// sources do nothing and every line wraps from its segments.
    /// </summary>
    public Func<IRenderable, int, IReadOnlyList<Segment>?>? Render { get; set; }

    // The write whose last line is open, waiting for a bare line break (2026-10-29).
    private Waiting? _waiting;

    // Sources rendered afresh (2026-10-29): a re-layout's count of them is the resize's Debug line (WidthChange.Rendered).
    private int _rendered;

    // Some source shows wrapped at the cached width for want of time (2026-10-29, the resize review): RenderPending has work.
    private bool _pending;

    // The shift each picture takes at the cached width (2026-10-29, the resize review): one for all its rows, by its span id.
    private readonly Dictionary<int, int> _shifts = new();

    /// <summary>
    /// How long a re-layout at a new width spends rendering sources again, the newest first (2026-10-29: 10,000 notices took 0.8–1.4 s,
    /// on the pane's tick under its gate — a drag froze the window). The ones it does not reach wrap by the cleaner rule at that width
    /// (<see cref="Cut"/>) until <see cref="RenderPending"/> reaches them; the newest are the ones on the screen.
    /// </summary>
    public TimeSpan RenderBudget { get; set; } = TimeSpan.FromMilliseconds(20);

    /// <summary>The clock <see cref="RenderBudget"/> is measured by (2026-10-29, the resize review): the pane's, so its tests' fake time never runs it out.</summary>
    public TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>Some source shows wrapped for want of time or theme: <see cref="RenderPending"/> has work (tests).</summary>
    public bool Pending => _pending;

    /// <summary>Marks every source not shown rendered as worth another try (2026-10-29, the resize review: the theme a container was written under is back).</summary>
    public void MarkPending() => _pending = true;

    /// <summary>
    /// The sources a real width change shows re-rendered at <paramref name="width"/>, rendered now from the newest back while
    /// <see cref="RenderBudget"/> lasts (2026-10-29); the rest wrap there until <see cref="RenderPending"/>.
    /// </summary>
    private void RenderNewestFirst(int width)
    {
        long started = Clock.GetTimestamp();
        for (int i = _lines.Count - 1; i >= 0; i--)
        {
            if (_lines[i].Reflow is not { } source || source.Width == width || source.CachedWidth == width)
            {
                continue;
            }

            if (Clock.GetElapsedTime(started) >= RenderBudget)
            {
                _pending = true;
                return;
            }

            RenderAt(_lines[i], source, width);
        }
    }

    /// <summary>
    /// The sources a re-layout left wrapped at the cached width, rendered now from the newest back (2026-10-29, the resize review: the
    /// docs say the transcript is laid out again, and those past the budget never were): at least one, then while <see cref="RenderBudget"/>
    /// lasts; the rows laid out again with the held places told (<see cref="Relaying"/>, <see cref="Relaid"/>). The pane calls it on a
    /// tick the size held. True when the screen must be drawn again: some line it rendered has a row in the last <paramref name="tailRows"/>
    /// — what the bottom's flow shows — or the pane is <paramref name="scrolled"/> (its window moves with its anchor). A line wholly above
    /// the tail changes nothing on the screen (2026-10-30, review F32: every such tick redrew it).
    /// </summary>
    public bool RenderPending(int tailRows = int.MaxValue, bool scrolled = true)
    {
        if (!_pending || Render is null || _width <= 0)
        {
            _pending = false;
            return false;
        }

        _pending = false;
        int rendered = _rendered;
        long started = Clock.GetTimestamp();
        bool any = false;
        bool attempted = false;
        var laid = new List<Line>();
        for (int i = _lines.Count - 1; i >= 0; i--)
        {
            if (_lines[i].Reflow is not { } source || source.Width == _width || source.CachedWidth == _width)
            {
                continue;
            }

            // At least one try a call, then while the budget lasts (2026-10-30, review F28: gated on a render that cached rows, a run of
            // renders that lost text or were put off never met the budget and all ran in one tick).
            if (attempted && Clock.GetElapsedTime(started) >= RenderBudget)
            {
                _pending = true;
                break;
            }

            RenderAt(_lines[i], source, _width);
            attempted = true;
            if (_lines[i].Reflow is { Cached: not null } && source.CachedWidth == _width)
            {
                any = true;
                laid.Add(_lines[i]);
            }
        }

        if (!any)
        {
            return false;
        }

        Relaying?.Invoke();
        Rebuild(_width);
        bool shown = scrolled || InTail(laid, tailRows);
        _reshaped |= shown;
        Relaid?.Invoke(new WidthChange(_width, _rendered - rendered, Clock.GetElapsedTime(started).TotalMilliseconds));
        return shown;
    }

    /// <summary>Whether any of <paramref name="lines"/> has a row among the last <paramref name="tailRows"/> at the cached width.</summary>
    private bool InTail(List<Line> lines, int tailRows)
    {
        var wanted = new HashSet<Line>(lines);
        int below = 0;
        for (int i = _lines.Count - 1; i >= 0 && below < tailRows; i--)
        {
            if (wanted.Contains(_lines[i]) && _lines[i].Rows > 0)
            {
                return true;
            }

            below += _lines[i].Rows;
        }

        return false;
    }

    /// <summary>
    /// <paramref name="source"/> rendered at <paramref name="width"/> into its cache (2026-10-29), split as an append splits. A failed render
    /// drops it (its parts wrap); <see cref="NotNow"/> leaves it be; a render of a Spectre widget narrower than its own width that lost
    /// letters or digits — a table crops or shows "…" rather than throw (the resize review) — is kept as "wrap the parts here".
    /// </summary>
    private void RenderAt(Line line, Source source, int width)
    {
        var segments = Render!(source.Renderable, width);
        if (segments is null)
        {
            line.Reflow = null;
            return;
        }

        if (ReferenceEquals(segments, NotNow))
        {
            return;
        }

        var lines = SplitLines(segments);
        bool lost = source.Renderable is not IReflowable && width < source.Width && Letters(lines) < Letters(line.Parts!);
        source.Cached = lost ? null : lines;
        source.CachedWidth = width;
        _rendered++;
    }

    /// <summary>The letters and digits in <paramref name="lines"/>: what a narrower table's render must keep (borders and "…" are neither).</summary>
    private static int Letters(List<List<Segment>> lines)
    {
        int count = 0;
        foreach (var line in lines)
        {
            foreach (var segment in line)
            {
                foreach (char c in segment.Text)
                {
                    if (char.IsLetterOrDigit(c))
                    {
                        count++;
                    }
                }
            }
        }

        return count;
    }

    /// <summary>A tool run or a code block: its summary line, its members in order, how many stay while it runs, and its own expanded state (null = the store's <see cref="ExpandAll"/>).</summary>
    private sealed class Group(int id, int keep, Line summary, IReadOnlyList<Segment> lead)
    {
        public readonly int Id = id;
        public readonly int Keep = keep;
        public readonly Line Summary = summary;
        public readonly List<Line> Members = new();

        /// <summary>The writes the members came in with (<see cref="Line.Unit"/>): one per line for a one-line note, one for a diff's block.</summary>
        public int Units;
        public readonly IReadOnlyList<Segment> Lead = lead;
        public bool Live = true;
        public bool? Expanded;
        public IReadOnlyList<Segment> Collapsed = Array.Empty<Segment>();
        public IReadOnlyList<Segment> Open = Array.Empty<Segment>();

        /// <summary>A code block's group (<see cref="BeginCodeGroup(int, IReadOnlyList{Segment})"/>): the summary is its label, shown <see cref="Plain"/> until the block folds, and every member shows while it is live.</summary>
        public bool Code;

        /// <summary>A thinking block's group (<see cref="BeginThinkingGroup(IReadOnlyList{Segment})"/>): a code block's kind that is always over, so it folds the moment it ends.</summary>
        public bool Thinking;

        /// <summary>An edit's diff (<see cref="FoldSpec"/>, 2026-10-04): its summary is its head row as drawn until it folds, every row shows while it is live, and it is never the open group.</summary>
        public bool Diff;

        /// <summary>Shown at full height while live, its summary as drawn until it folds: a code block, a thinking block or a diff.</summary>
        public bool Streamed => Code || Thinking || Diff;

        /// <summary>The code block's label line as the reply drew it; null for a tool run (its summary hides until the run folds).</summary>
        public IReadOnlyList<Segment>? Plain;

        /// <summary>The code block's source lines (a diff's rows), measured against <see cref="Keep"/> in place of the member rows; null for a tool run.</summary>
        public int? Size;

        /// <summary>More writes (or source lines) than it keeps: the summary shows and the members fold.</summary>
        public bool Over => Thinking || (Keep > 0 && (Size ?? Units) > Keep);

        /// <summary>Folded or unfolded as the summary reads it: over, and — a code or thinking block — no longer live.</summary>
        public bool Folds => Over && !(Streamed && Live);
    }

    /// <summary>
    /// Whether a group without its own state shows every member (Ctrl+O, <c>/expand</c>):
    /// <see cref="SetAllExpanded"/> sets it and forgets every group's own state.
    /// </summary>
    public bool ExpandAll { get; private set; }

    /// <summary>
    /// Some tool run, code block or thinking block in the store folds (<see cref="Group.Folds"/>): what the upper rule's
    /// ⤡ is drawn for (2026-09-28, the user's ask: the button only while there is something to unfold or fold). A run
    /// under its keep count and a block still streaming do not count. Read on the tick, so a plain loop.
    /// </summary>
    public bool AnyFolds
    {
        get
        {
            foreach (var group in _groups.Values)
            {
                if (group.Folds)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>A tool run is open: the next member joins it. An open code or thinking block is not one (<see cref="BeginCodeGroup(int, IReadOnlyList{Segment})"/>, <see cref="BeginThinkingGroup(IReadOnlyList{Segment})"/>).</summary>
    public bool GroupOpen => _open is { Streamed: false };

    /// <summary>A code block's group is open (<see cref="BeginCodeGroup(int, IReadOnlyList{Segment})"/>): its next rows join it.</summary>
    public bool CodeGroupOpen => _open is { Code: true };

    /// <summary>A thinking block's group is open (<see cref="BeginThinkingGroup(IReadOnlyList{Segment})"/>): its next rows join it.</summary>
    public bool ThinkingGroupOpen => _open is { Thinking: true };

    /// <summary>The open code block's label line as drawn; empty without one.</summary>
    public IReadOnlyList<Segment> CodeGroupLabel => _open is { Code: true, Plain: { } plain } ? plain : Array.Empty<Segment>();

    /// <summary>The rows at the cached width (0 before the first <see cref="Rows"/> or <see cref="Append(IReadOnlyList{Segment}, int)"/>).</summary>
    public int Count => _rows.Count;

    /// <summary>The logical lines kept (tests).</summary>
    public int LineCount => _lines.Count;

    /// <summary>
    /// A place in the store (<see cref="Mark"/>, 2026-10-11): the lines before it — counted from the first line the store ever held since
    /// 2026-10-29 (the resize review), so a mark the cap or a clear went past reads as gone — and of the open last line the segments and
    /// rows it held — a picture's write leaves its last row open, and the next write joins it.
    /// </summary>
    public readonly record struct Place(int Lines, int Segments, int Rows)
    {
        /// <summary>
        /// The line the mark leans on (2026-10-30, review F27: a waiting write merged into one line moved every later index, and a mark
        /// taken inside it named another line): the open line it sits in, or the line before it; null in an empty store.
        /// </summary>
        internal object? Anchor { get; init; }

        /// <summary>Where <see cref="Anchor"/> stood, counted as <see cref="Lines"/> is.</summary>
        internal int AnchorAt { get; init; }
    }

    /// <summary>Where the next write starts, for <see cref="Since"/>: after the last line, or inside it while it is open.</summary>
    public Place Mark => LastLineOpen
        ? new(_frontDropped + _lines.Count - 1, _lines[^1].Segments.Count, _lines[^1].Rows) { Anchor = _lines[^1], AnchorAt = _frontDropped + _lines.Count - 1 }
        : new(_frontDropped + _lines.Count, 0, 0) { Anchor = _lines.Count > 0 ? _lines[^1] : null, AnchorAt = _frontDropped + _lines.Count - 1 };

    /// <summary>The last line has no line break after it yet: the flow cursor sits at its end, not on the row under it.</summary>
    public bool LastLineOpen => _lines.Count > 0 && !_lines[^1].Closed;

    /// <summary>The open last line's rows at the cached width, 0 without one (2026-10-30, review F24: the rows a write joining it finds on the screen).</summary>
    internal int OpenLineRows => LastLineOpen ? _lines[^1].Rows : 0;

    /// <summary>
    /// The open last line was written wider than <paramref name="width"/> (2026-10-30, review F112): a write that closes it lays it out by the
    /// narrower rule, so the pane draws the store's rows for it.
    /// </summary>
    internal bool OpenLineWiderThan(int width) => LastLineOpen && _lines[^1].BornWidth > width;

    /// <summary>The screen no longer shows the store's rows (2026-10-30, review F112): the pane's next draw writes the flow again from here.</summary>
    internal void MarkReshaped() => _reshaped = true;

    /// <summary>
    /// What was written since a <see cref="Mark"/> (<see cref="Since"/>, 2026-10-11) as the writes that put it back (2026-10-29): each
    /// line's segments with a line break after a closed one — written where the mark stood, the first joins an open line as it did —
    /// and the <see cref="Rows"/> they added at the width they were read at.
    /// </summary>
    public sealed record Copied(IReadOnlyList<Chunk> Chunks, int Rows)
    {
        /// <summary>Every chunk's segments in order: the copy as one write (2026-10-11's shape).</summary>
        public IReadOnlyList<Segment> Segments => [.. Chunks.SelectMany(chunk => chunk.Segments)];
    }

    /// <summary>
    /// One write of a <see cref="Copied"/> (2026-10-29): its segments, a line break after a closed line, the renderable it came from if
    /// any, and the width it was written at (<paramref name="Born"/>; 0 for the store's width), so the lines put back under a refitted
    /// splash still follow a resize.
    /// </summary>
    public sealed record Chunk(IReadOnlyList<Segment> Segments, IRenderable? Source, int Born = 0);

    /// <summary>
    /// What was written after <paramref name="mark"/> as the writes that put it back (2026-10-11, the welcome splash drawn first and
    /// fitted again over the startup's lines, <c>ChatScreen.RefitSplash</c>). Null with nothing visible written, a mark the store no longer
    /// holds (the cap dropped lines from the front since), a line that folds (a tool run, a code or thinking block, a diff) or holds a
    /// picture after it — neither survives a copy — or a write rendered again at another width (2026-10-29, the resize review: the
    /// terminal would show the copy as written, the store otherwise), or a mark a merge moved (2026-10-30, <see cref="Place.Anchor"/>):
    /// nothing is copied. A narrowed text line is copied with its width (<see cref="Chunk.Born"/>).
    /// </summary>
    public Copied? Since(Place mark, int width)
    {
        Layout(width);
        int first = mark.Lines - _frontDropped;
        if (first < 0 || first >= _lines.Count || mark.Segments > _lines[first].Segments.Count)
        {
            return null;
        }

        // A merge since that took in the mark's line, or ran at or before it, moved or replaced it (2026-10-30, review F27): gone, as a
        // dropped mark is. One wholly after it leaves it be.
        int anchorAt = mark.AnchorAt - _frontDropped;
        if (mark.Anchor is not null && (anchorAt < 0 || anchorAt >= _lines.Count || !ReferenceEquals(_lines[anchorAt], mark.Anchor)))
        {
            return null;
        }

        var chunks = new List<Chunk>();
        int rows = 0;
        for (int i = first; i < _lines.Count; i++)
        {
            var line = _lines[i];
            bool joined = i == first && (mark.Segments > 0 || mark.Rows > 0);
            // A narrowed text line is copied with the width it was written at (2026-10-30, review F26: refusing it skipped the splash's
            // refit after any narrowing at startup): put back at that width, it goes the narrower path on the screen and in the store.
            if (line.Group is not null || line.Fold is not null || (!joined && line.Pictures is not null)
                || (Narrowed(line, _width) && line.Pictures is not null) || (line.Reflow is { } source && source.Width != _width))
            {
                return null;
            }

            var segments = new List<Segment>();
            if (line.Parts is { } parts)
            {
                foreach (var part in parts)
                {
                    segments.AddRange(part);
                    segments.Add(Segment.LineBreak);
                }
            }
            else
            {
                segments.AddRange(joined ? line.Segments.Skip(mark.Segments) : line.Segments);
                if (line.Closed)
                {
                    segments.Add(Segment.LineBreak);
                }
            }

            chunks.Add(new Chunk(segments, line.Reflow?.Renderable, line.BornWidth));
            rows += joined ? line.Rows - mark.Rows : line.Rows;
        }

        // Nothing to see (the break that closed a picture's open row, a blank line): nothing worth drawing the picture again for.
        bool visible = chunks.Any(chunk => chunk.Segments.Any(s => !s.IsLineBreak && !string.IsNullOrWhiteSpace(s.Text)));
        return visible ? new Copied(chunks, Math.Max(0, rows)) : null;
    }

    /// <summary>The rows at <paramref name="width"/>, laid out afresh when the width changed.</summary>
    public IReadOnlyList<SegmentLine> Rows(int width)
    {
        Layout(width);
        return _rows;
    }

    /// <summary>
    /// <paramref name="segments"/> — one flow write as Spectre emitted it — appended: control codes
    /// and <c>\r</c> skipped, a line break (a break segment, or a <c>\n</c> inside a text) closing the
    /// line, the text after it opening the next; the rows at <paramref name="width"/> extended.
    /// Returns the rows dropped from the front to stay under <see cref="MaxRows"/>, so a caller
    /// anchored on a row can move with it.
    /// </summary>
    public int Append(IReadOnlyList<Segment> segments, int width) => Append(segments, width, member: false);

    /// <summary>
    /// <see cref="Append(IReadOnlyList{Segment}, int)"/>, the lines it opens members of the open tool
    /// run when <paramref name="member"/> (a run is opened first when none is: <see cref="BeginGroup"/>
    /// with nothing kept). Any other append ends the open run first — text, a notice, the reply's
    /// block: the run is over the moment something else is said.
    /// </summary>
    public int Append(IReadOnlyList<Segment> segments, int width, bool member) => Append(segments, width, member, null);

    /// <summary>
    /// <see cref="Append(IReadOnlyList{Segment}, int, bool)"/>, the lines it opens tagged in order with
    /// <paramref name="pictures"/> — the spans of a picture's rows (later on 2026-09-24), one list per line.
    /// </summary>
    public int Append(IReadOnlyList<Segment> segments, int width, bool member, IReadOnlyList<IReadOnlyList<PictureSpan>>? pictures) =>
        Append(segments, width, member, pictures, null);

    /// <summary>
    /// <see cref="Append(IReadOnlyList{Segment}, int, bool, IReadOnlyList{IReadOnlyList{PictureSpan}}?)"/>, the lines it touches from
    /// <paramref name="fold"/>'s head on made a diff (2026-10-04): live until the run ends or, with none, the next write.
    /// </summary>
    public int Append(IReadOnlyList<Segment> segments, int width, bool member, IReadOnlyList<IReadOnlyList<PictureSpan>>? pictures, FoldSpec? fold) =>
        Append(segments, width, member, pictures, fold, null);

    /// <summary>
    /// <see cref="Append(IReadOnlyList{Segment}, int, bool, IReadOnlyList{IReadOnlyList{PictureSpan}}?, FoldSpec?)"/> with the renderable the
    /// segments came from (2026-10-29, <see cref="Source"/>), written at <paramref name="width"/>.
    /// </summary>
    public int Append(IReadOnlyList<Segment> segments, int width, bool member, IReadOnlyList<IReadOnlyList<PictureSpan>>? pictures, FoldSpec? fold, IRenderable? source) =>
        Append(segments, width, member, pictures, fold, source, 0);

    /// <summary>
    /// <see cref="Append(IReadOnlyList{Segment}, int, bool, IReadOnlyList{IReadOnlyList{PictureSpan}}?, FoldSpec?)"/>, laid out at
    /// <paramref name="width"/>, the segments rendered at <paramref name="born"/> (2026-10-29, the resize review: the two can differ — a
    /// width read once per write, a streaming reply kept at the width it began committing at; 0 for <paramref name="width"/>). The lines are
    /// kept with <paramref name="source"/> — the renderable the segments came from — so they can be rendered again at another width
    /// (<see cref="Source"/>): only a standalone write keeps it (no open line to join, not a tool run's member, no pictures, nothing that
    /// folds, some text, at most <see cref="MaxSourceLines"/> lines), as one line. A write that leaves its last line open keeps it only if
    /// the next append is a bare line break that closes that line.
    /// </summary>
    public int Append(IReadOnlyList<Segment> segments, int width, bool member, IReadOnlyList<IReadOnlyList<PictureSpan>>? pictures, FoldSpec? fold, IRenderable? source, int born)
    {
        ArgumentNullException.ThrowIfNull(segments);
        _picturing = pictures;
        _pictured = 0;
        Layout(width);
        born = born > 0 ? born : _width;
        bool visible = segments.Any(s => !s.IsControlCode);

        // A waiting write is kept only by the bare break that closes its own open line; control codes alone leave it waiting (2026-10-29).
        Waiting? closing = null;
        if (_waiting is { } waiting && visible)
        {
            _waiting = null;
            // Its lines are still the store's last (every other write, group or clear since dropped it), the last still open.
            if (!member && BareBreak(segments) && LastLineOpen && _lines.LastIndexOf(waiting.First) >= 0)
            {
                closing = waiting;
            }
        }

        bool standalone = source is not null && !LastLineOpen && !member && pictures is null && fold is null;
        if (member)
        {
            if (_open is null)
            {
                // A diff written with no run (the one before this run) is over.
                EndDiffs();
                _open = NewGroup(0, Array.Empty<Segment>());
            }
        }
        else if (visible)
        {
            EndGroup();
        }

        // The open line is wrapped again with what joins it: its rows leave the cache first.
        var touched = new List<Line>();
        Line? current = null;
        Line? joined = null;
        if (LastLineOpen)
        {
            current = _lines[^1];
            joined = current;
            _rows.RemoveRange(_rows.Count - current.Rows, current.Rows);
            touched.Add(current);
            if (pictures is { Count: > 0 } && visible)
            {
                // A picture's first row joins the open line (later on 2026-09-24): the first spans are that line's.
                var spans = pictures[_pictured++];
                current.Pictures = spans.Count > 0 ? spans : null;
            }
        }

        _tagging = member ? _open : null;
        // A call's result joins its call's unit (2026-10-04, ContinueUnit): a run's keep count counts calls.
        _unitTaken = ContinueUnit && _open is { Units: > 0 };

        foreach (var piece in Pieces(segments))
        {
            if (piece is null)
            {
                Close(ref current, touched);
                continue;
            }

            current ??= Open(touched);
            current.Segments.Add(piece);
        }

        _tagging = null;
        _picturing = null;
        foreach (var line in touched)
        {
            // An open line joined at another width keeps the wider (the resize review): narrower than either, it overflows.
            line.BornWidth = line == joined && visible ? Math.Max(line.BornWidth, born) : line == joined ? line.BornWidth : born;
        }

        if (fold is { } spec)
        {
            MakeDiff(spec, touched);
        }

        if (standalone && touched.Count > 0)
        {
            if (!touched[^1].Closed)
            {
                if (touched.Count <= MaxSourceLines)
                {
                    _waiting = new Waiting(source!, born, touched[0]);
                }
            }
            else if (Merged(touched, touched.Count, source!, born) is { } line)
            {
                touched = [line];
            }
        }
        else if (closing is not null && touched is [var closed] && closed.Closed)
        {
            // The bare line break the waiting write was missing: its lines are one now, their rows out of the cache first.
            int first = _lines.LastIndexOf(closing.First);
            if (first >= 0)
            {
                int cached = 0;
                for (int i = first; i < _lines.Count - 1; i++)
                {
                    cached += _lines[i].Rows;
                }

                _rows.RemoveRange(_rows.Count - cached, cached);
                if (Merged(_lines.GetRange(first, _lines.Count - first), _lines.Count - first, closing.Renderable, closing.Born) is { } line)
                {
                    touched = [line];
                }
                else
                {
                    // Not worth a source after all (blank): its lines go back into the cache as they were.
                    touched = _lines.GetRange(first, _lines.Count - first);
                }
            }
        }

        if (_width > 0 && touched.Any(line => line.Pictures is not null))
        {
            touched = ShiftPictures(touched);
        }

        if (member && _open!.Folds)
        {
            // The fold moved (a member hid, the summary showed or grew): the run laid out again —
            // the touched lines' rows are out of the cache already.
            foreach (var line in touched)
            {
                line.Rows = 0;
            }

            Relayout(_open.Summary);
            return Trim();
        }

        foreach (var line in touched)
        {
            line.Rows = Wrap(line, _width, _rows);
        }

        if (touched.Any(line => line.Reflow is { } kept && kept.Width != _width))
        {
            _pending = true;   // written at another width than the layout's (a resize between the render and here): render it there later
        }

        return Trim();
    }

    /// <summary>
    /// The shift of each picture <paramref name="touched"/> holds, worked out over every row of it (2026-10-30, review F110: over the
    /// touched rows alone, a narrowing that closed a picture's last row gave that row a shift its earlier rows did not have — a shear).
    /// A picture's rows are the store's last lines, so its earlier rows sit just before the touched ones; when its shift changed they
    /// are taken out of the cache and come back to be wrapped again, and the pane redraws (<see cref="Reshaped"/>). The lines to wrap,
    /// in order.
    /// </summary>
    private List<Line> ShiftPictures(List<Line> touched)
    {
        var ids = new HashSet<int>();
        foreach (var line in touched)
        {
            if (line.Pictures is [var span, ..])
            {
                ids.Add(span.Id);
            }
        }

        int first = _lines.LastIndexOf(touched[0]);
        int from = first;
        while (from > 0 && _lines[from - 1].Pictures is [var span, ..] && ids.Contains(span.Id))
        {
            from--;
        }

        var before = ids.ToDictionary(id => id, id => _shifts.TryGetValue(id, out int shift) ? shift : -1);
        Shifts(_lines.Skip(from), _width);
        if (from == first || ids.All(id => (_shifts.TryGetValue(id, out int shift) ? shift : -1) == before[id]))
        {
            return touched;
        }

        // Their rows are the cache's last: the touched lines' are out of it already.
        var earlier = _lines.GetRange(from, first - from);
        int cached = earlier.Sum(line => line.Rows);
        _rows.RemoveRange(_rows.Count - cached, cached);
        _reshaped = true;
        return [.. earlier, .. touched];
    }

    /// <summary>
    /// The last <paramref name="count"/> lines (<paramref name="lines"/>, every one closed, no rows in the cache) made one line holding
    /// them as its parts, kept with <paramref name="renderable"/> (2026-10-29, the resize review). Null, and nothing changed, when the
    /// write is past <see cref="MaxSourceLines"/> or holds no text (a blank line is the same at every width).
    /// </summary>
    private Line? Merged(List<Line> lines, int count, IRenderable renderable, int born)
    {
        if (count > MaxSourceLines || !lines.Any(line => line.Segments.Any(s => !string.IsNullOrWhiteSpace(s.Text))))
        {
            return null;
        }

        var merged = new Line
        {
            Closed = true,
            BornWidth = born,
            Parts = lines.Select(line => line.Segments).ToList(),
            Reflow = new Source(renderable, born),
        };
        _lines.RemoveRange(_lines.Count - count, count);
        _lines.Add(merged);
        return merged;
    }

    /// <summary>
    /// One write's segments as the store reads them (2026-10-29, shared by <see cref="Append(IReadOnlyList{Segment}, int)"/> and a
    /// source's render, so both split alike): control codes skipped, <c>\r</c> removed, the text between line breaks as pieces in
    /// their styles, and null for each line break — a break segment, or a <c>\n</c> inside a text.
    /// </summary>
    private static IEnumerable<Segment?> Pieces(IReadOnlyList<Segment> segments)
    {
        foreach (var segment in segments)
        {
            if (segment.IsControlCode)
            {
                continue;
            }

            if (segment.IsLineBreak)
            {
                yield return null;
                continue;
            }

            string text = segment.Text;
            int from = 0;
            while (from <= text.Length)
            {
                int end = text.IndexOf('\n', from);
                int stop = end < 0 ? text.Length : end;
                if (stop > from)
                {
                    string piece = text[from..stop];
                    if (piece.Contains('\r'))
                    {
                        piece = piece.Replace("\r", "", StringComparison.Ordinal);
                    }

                    if (piece.Length > 0)
                    {
                        yield return new Segment(piece, segment.Style);
                    }
                }

                if (end < 0)
                {
                    break;
                }

                yield return null;
                from = end + 1;
            }
        }
    }

    /// <summary>
    /// <paramref name="segments"/> split into lines by <see cref="Pieces"/> (2026-10-29): a line break closes the line (an empty one
    /// when none is open), so a trailing break — <c>MarkupLine</c>'s — closes the last line rather than adding a blank one.
    /// </summary>
    private static List<List<Segment>> SplitLines(IReadOnlyList<Segment> segments)
    {
        var lines = new List<List<Segment>>();
        List<Segment>? current = null;
        foreach (var piece in Pieces(segments))
        {
            if (piece is null)
            {
                lines.Add(current ?? new List<Segment>());
                current = null;
            }
            else
            {
                (current ??= new List<Segment>()).Add(piece);
            }
        }

        if (current is not null)
        {
            lines.Add(current);
        }

        return lines;
    }

    /// <summary>The write is one line break and nothing else (<c>WriteLine()</c> after a <c>Write</c>): what a waiting write needs (2026-10-29).</summary>
    private static bool BareBreak(IReadOnlyList<Segment> segments)
    {
        int breaks = 0;
        foreach (var piece in Pieces(segments))
        {
            if (piece is not null || ++breaks > 1)
            {
                return false;
            }
        }

        return breaks == 1;
    }

    /// <summary>
    /// Whether <paramref name="segments"/> would join the open last line where it is laid out narrower than written — a picture's last
    /// row, cut at the edge (2026-10-29, the resize review): text joined there would follow the cut on the screen and the whole line in
    /// the store, so the pane ends the row first. A write that starts with a line break only closes it.
    /// </summary>
    public bool JoinNeedsBreak(IReadOnlyList<Segment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (!LastLineOpen || _width <= 0 || !Narrowed(_lines[^1], _width))
        {
            return false;
        }

        foreach (var piece in Pieces(segments))
        {
            return piece is not null;
        }

        return false;
    }

    /// <summary>Everything forgotten (the screen was cleared); the pane-wide <see cref="ExpandAll"/> stays. A mark from before it reads as gone.</summary>
    public void Clear()
    {
        // One past the lines held: a mark taken at the very end, before the clear, would otherwise read as the first new line.
        _frontDropped += _lines.Count + 1;
        _lines.Clear();
        _rows.Clear();
        _groups.Clear();
        _liveDiffs.Clear();
        _shifts.Clear();
        _open = null;
        _waiting = null;
        _pending = false;
        _reshaped = false;
    }

    /// <summary>
    /// Opens a tool run (the open one ended first) that keeps its last <paramref name="keep"/>
    /// members while it runs (0 = never folds). <paramref name="lead"/> is the reply's glyph when
    /// the run starts right after it: drawn over the first visible row's indent, so a fold never
    /// hides it. <paramref name="absorbOpenLine"/> takes an open last line (that glyph, written
    /// bare into the flow) out of the store — the run's lead carries it now.
    /// </summary>
    public void BeginGroup(int keep, IReadOnlyList<Segment>? lead = null, bool absorbOpenLine = false)
    {
        EndGroup();
        _waiting = null;
        if (LastLineOpen)
        {
            if (absorbOpenLine)
            {
                var open = _lines[^1];
                if (_width > 0)
                {
                    _rows.RemoveRange(_rows.Count - open.Rows, open.Rows);
                }

                _lines.RemoveAt(_lines.Count - 1);
                _reshaped = true;
            }
            else
            {
                _lines[^1].Closed = true;
            }
        }

        _open = NewGroup(Math.Max(0, keep), lead?.Where(s => !s.IsControlCode && !s.IsLineBreak).ToList() ?? (IReadOnlyList<Segment>)Array.Empty<Segment>());
    }

    /// <summary>
    /// The open run's summary as it reads folded (<paramref name="collapsed"/>) and unfolded
    /// (<paramref name="expanded"/>); shown only while the run holds more than it keeps. Nothing
    /// without an open run.
    /// </summary>
    public void SetGroupSummary(IReadOnlyList<Segment> collapsed, IReadOnlyList<Segment> expanded) => SetGroupSummary(collapsed, expanded, 0, 0);

    /// <summary>
    /// <see cref="SetGroupSummary(IReadOnlyList{Segment}, IReadOnlyList{Segment})"/>, the store laid out at <paramref name="width"/> first
    /// and the summary's look rendered at <paramref name="born"/> (2026-10-29, the resize review: the store's cached width can lag the
    /// window's; 0 for the cached width).
    /// </summary>
    public void SetGroupSummary(IReadOnlyList<Segment> collapsed, IReadOnlyList<Segment> expanded, int width, int born)
    {
        ArgumentNullException.ThrowIfNull(collapsed);
        ArgumentNullException.ThrowIfNull(expanded);
        if (_open is not { } group)
        {
            return;
        }

        if (width > 0)
        {
            Layout(width);
        }

        group.Collapsed = collapsed.Where(s => !s.IsControlCode && !s.IsLineBreak).ToList();
        group.Open = expanded.Where(s => !s.IsControlCode && !s.IsLineBreak).ToList();
        group.Summary.BornWidth = born > 0 ? born : Math.Max(0, _width);   // drawn at the window's width now (2026-10-29)
        if (group.Folds)
        {
            Relayout(group.Summary);
        }
    }

    /// <summary>
    /// Opens a code block's group (the open group ended first; an open last line closed) whose
    /// summary is <paramref name="label"/> — the block's label line as drawn, laid out at once, so it
    /// takes its row now as any line would — folding past <paramref name="keep"/> source lines once
    /// it is over (0 = never). <see cref="SetCodeGroupSummary(IReadOnlyList{Segment}, IReadOnlyList{Segment}, int)"/> gives the folded look and the size.
    /// </summary>
    public void BeginCodeGroup(int keep, IReadOnlyList<Segment> label) => BeginStreamedGroup(keep, label, thinking: false, 0, 0);

    /// <summary><see cref="BeginCodeGroup(int, IReadOnlyList{Segment})"/> laid out at <paramref name="width"/>, the label rendered at <paramref name="born"/> (2026-10-29, the resize review).</summary>
    public void BeginCodeGroup(int keep, IReadOnlyList<Segment> label, int width, int born) => BeginStreamedGroup(keep, label, thinking: false, width, born);

    /// <summary>
    /// Opens a thinking block's group (the open group ended first; an open last line closed) whose
    /// summary is <paramref name="header"/> — the block's header line as drawn while it streams —
    /// folding the moment it ends. <see cref="SetThinkingGroupSummary(IReadOnlyList{Segment}, IReadOnlyList{Segment})"/> gives the folded look.
    /// </summary>
    public void BeginThinkingGroup(IReadOnlyList<Segment> header) => BeginStreamedGroup(0, header, thinking: true, 0, 0);

    /// <summary><see cref="BeginThinkingGroup(IReadOnlyList{Segment})"/> laid out at <paramref name="width"/>, the header rendered at <paramref name="born"/> (2026-10-29, the resize review).</summary>
    public void BeginThinkingGroup(IReadOnlyList<Segment> header, int width, int born) => BeginStreamedGroup(0, header, thinking: true, width, born);

    /// <summary>The open thinking block's summary folded (<paramref name="collapsed"/>) and unfolded (<paramref name="expanded"/>). Nothing without one.</summary>
    public void SetThinkingGroupSummary(IReadOnlyList<Segment> collapsed, IReadOnlyList<Segment> expanded) => SetThinkingGroupSummary(collapsed, expanded, 0, 0);

    /// <summary><see cref="SetThinkingGroupSummary(IReadOnlyList{Segment}, IReadOnlyList{Segment})"/> laid out at <paramref name="width"/>, rendered at <paramref name="born"/> (2026-10-29).</summary>
    public void SetThinkingGroupSummary(IReadOnlyList<Segment> collapsed, IReadOnlyList<Segment> expanded, int width, int born)
    {
        if (_open is { Thinking: true })
        {
            SetGroupSummary(collapsed, expanded, width, born);
        }
    }

    private void BeginStreamedGroup(int keep, IReadOnlyList<Segment> label, bool thinking, int width, int born)
    {
        ArgumentNullException.ThrowIfNull(label);
        if (width > 0)
        {
            Layout(width);
        }

        EndGroup();
        _waiting = null;
        if (LastLineOpen)
        {
            _lines[^1].Closed = true;
        }

        var summary = new Line { Closed = true, Member = -1, BornWidth = born > 0 ? born : Math.Max(0, _width) };
        var group = new Group(_nextGroup++, Math.Max(0, keep), summary, Array.Empty<Segment>())
        {
            Code = !thinking,
            Thinking = thinking,
            Plain = label.Where(s => !s.IsControlCode && !s.IsLineBreak).ToList(),
        };
        summary.Group = group;
        _lines.Add(summary);
        _groups[group.Id] = group;
        _open = group;
        if (_width > 0)
        {
            summary.Rows = Wrap(summary, _width, _rows);
        }
    }

    /// <summary>
    /// The open code block's summary folded (<paramref name="collapsed"/>) and unfolded
    /// (<paramref name="expanded"/>), and its <paramref name="size"/> in source lines so far.
    /// Nothing without an open code block.
    /// </summary>
    public void SetCodeGroupSummary(IReadOnlyList<Segment> collapsed, IReadOnlyList<Segment> expanded, int size) => SetCodeGroupSummary(collapsed, expanded, size, 0, 0);

    /// <summary><see cref="SetCodeGroupSummary(IReadOnlyList{Segment}, IReadOnlyList{Segment}, int)"/> laid out at <paramref name="width"/>, rendered at <paramref name="born"/> (2026-10-29).</summary>
    public void SetCodeGroupSummary(IReadOnlyList<Segment> collapsed, IReadOnlyList<Segment> expanded, int size, int width, int born)
    {
        ArgumentNullException.ThrowIfNull(collapsed);
        ArgumentNullException.ThrowIfNull(expanded);
        if (_open is not { Code: true } group)
        {
            return;
        }

        group.Size = size;
        SetGroupSummary(collapsed, expanded, width, born);
    }

    /// <summary>
    /// The open run is over: a folded one shrinks to its summary. Every live diff is over too — its run's, or with no run the
    /// one before this write (2026-10-04) — and one past its keep folds. Nothing else without an open run.
    /// </summary>
    public void EndGroup()
    {
        EndDiffs();
        if (_open is not { } group)
        {
            return;
        }

        _open = null;
        group.Live = false;
        if (group.Over && (!Expanded(group) || group.Streamed))
        {
            // A code or thinking block's summary changes look even unfolded: the label becomes ▾ … · n lines.
            Relayout(group.Summary);
        }
    }

    /// <summary>The run whose summary is on store row <paramref name="row"/> at the cached width, if any (a click there toggles it).</summary>
    public int? GroupAtRow(int row)
    {
        int at = 0;
        foreach (var line in _lines)
        {
            if (row < at + line.Rows)
            {
                if (line.FoldHead && line.Fold is { Folds: true } diff)
                {
                    return diff.Id;
                }

                return line.Group is { } group && line.Member < 0 ? group.Id : null;
            }

            at += line.Rows;
        }

        return null;
    }

    /// <summary>
    /// The picture at store row <paramref name="row"/>, column <paramref name="col"/> (later on 2026-09-24): the id of
    /// the span covering it on a picture's line, or null. Only while the line takes one row, which since 2026-10-29 it
    /// always does once narrowed (<see cref="Cut"/>); the blank cells it dropped on the left (<see cref="Line.Shift"/>)
    /// are added to the column, so a centered picture moved left still answers where it is drawn.
    /// </summary>
    public int? PictureAt(int row, int col)
    {
        int at = 0;
        foreach (var line in _lines)
        {
            if (row < at + line.Rows)
            {
                if (line.Rows != 1 || line.Pictures is not { } spans)
                {
                    return null;
                }

                col += line.Shift;
                foreach (var span in spans)
                {
                    if (col >= span.Col && col < span.Col + span.Width)
                    {
                        return span.Id;
                    }
                }

                return null;
            }

            at += line.Rows;
        }

        return null;
    }

    /// <summary>Unfolds a folded run, folds an unfolded one (its own state from now on); false for no such run, or a code or thinking block that does not fold (yet: its label is only a label).</summary>
    public bool Toggle(int id)
    {
        if (!_groups.TryGetValue(id, out var group) || (group.Streamed && !group.Folds))
        {
            return false;
        }

        group.Expanded = !Expanded(group);
        Relayout(group.Summary);
        return true;
    }

    /// <summary>Every run unfolded or folded (Ctrl+O, <c>/expand</c>, <c>/collapse</c>): <see cref="ExpandAll"/> set, each run's own state forgotten.</summary>
    public void SetAllExpanded(bool expanded)
    {
        ExpandAll = expanded;
        foreach (var group in _groups.Values)
        {
            group.Expanded = null;
        }

        if (_groups.Count > 0 && _width > 0)
        {
            int width = _width;
            _width = -1;
            Layout(width);
            _reshaped = true;
        }
    }

    /// <summary>A fold the transcript's find opened (2026-10-07): the group and the state it had, to put back when the find ends.</summary>
    public sealed record Unfolded(int Id, bool? Was);

    /// <summary>
    /// Every fold that hides a line holding <paramref name="find"/> (ignoring case) opened — a tool run, a code or thinking block, a
    /// diff, both when a diff hides in a folded run (2026-10-07, phase 5 of the UI round: the transcript's find reaches into what
    /// is folded). Returns what it opened, for <see cref="Refold"/>; the rows are laid out again when anything opened.
    /// </summary>
    public IReadOnlyList<Unfolded> UnfoldMatching(string find)
    {
        ArgumentNullException.ThrowIfNull(find);
        var opened = new List<Unfolded>();
        if (find.Length == 0)
        {
            return opened;
        }

        foreach (var line in _lines)
        {
            if (Shown(line) is not null || !TextFind.LineText(line.Segments).Contains(find, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var group in (ReadOnlySpan<Group?>)[line.Group, line.Fold])
            {
                if (group is { Folds: true } folds && !Expanded(folds))
                {
                    opened.Add(new Unfolded(folds.Id, folds.Expanded));
                    folds.Expanded = true;
                }
            }
        }

        if (opened.Count > 0)
        {
            Reflow();
        }

        return opened;
    }

    /// <summary>The folds <see cref="UnfoldMatching"/> opened, put back as they were (one gone from the store meanwhile is skipped).</summary>
    public void Refold(IReadOnlyList<Unfolded> opened)
    {
        ArgumentNullException.ThrowIfNull(opened);
        bool any = false;
        foreach (var fold in opened)
        {
            if (_groups.TryGetValue(fold.Id, out var group))
            {
                group.Expanded = fold.Was;
                any = true;
            }
        }

        if (any)
        {
            Reflow();
        }
    }

    /// <summary>Every row laid out again at the cached width, the pane told to rebuild (<see cref="SetAllExpanded"/>'s way).</summary>
    private void Reflow()
    {
        if (_width > 0)
        {
            int width = _width;
            _width = -1;
            Layout(width);
        }

        _reshaped = true;
    }

    /// <summary>Rows other than the end changed and <see cref="TakeReshaped"/> has not been asked yet.</summary>
    public bool Reshaped => _reshaped;

    /// <summary>Whether rows other than the end changed since the last call (a fold moved, a run shrank or toggled): the pane then rebuilds what it shows from the store. Clears the flag.</summary>
    public bool TakeReshaped()
    {
        bool reshaped = _reshaped;
        _reshaped = false;
        return reshaped;
    }

    private bool Expanded(Group group) => group.Expanded ?? ExpandAll;

    /// <summary>
    /// The diff <paramref name="spec"/> names over the lines an append <paramref name="touched"/> (2026-10-04): its head the summary,
    /// the rest its rows, live. Nothing when it can never fold (no keep, or not past it) or the write had no such line.
    /// </summary>
    private void MakeDiff(FoldSpec spec, List<Line> touched)
    {
        if (spec.Keep <= 0 || spec.Size <= spec.Keep || spec.Head < 0 || spec.Head >= touched.Count)
        {
            return;
        }

        var head = touched[spec.Head];
        var diff = new Group(_nextGroup++, spec.Keep, head, Array.Empty<Segment>())
        {
            Diff = true,
            Size = spec.Size,
            Collapsed = spec.Collapsed.Where(s => !s.IsControlCode && !s.IsLineBreak).ToList(),
            Open = spec.Expanded.Where(s => !s.IsControlCode && !s.IsLineBreak).ToList(),
        };
        for (int i = spec.Head; i < touched.Count; i++)
        {
            touched[i].Fold = diff;
        }

        head.FoldHead = true;
        _groups[diff.Id] = diff;
        _liveDiffs.Add(diff);
    }

    /// <summary>Every live diff over (2026-10-04): past its keep it folds, laid out again from the first one's summary.</summary>
    private void EndDiffs()
    {
        if (_liveDiffs.Count == 0)
        {
            return;
        }

        foreach (var diff in _liveDiffs)
        {
            diff.Live = false;
        }

        var first = _liveDiffs[0].Summary;
        _liveDiffs.Clear();
        Relayout(first);
    }

    private Group NewGroup(int keep, IReadOnlyList<Segment> lead)
    {
        var summary = new Line { Closed = true, Member = -1 };
        var group = new Group(_nextGroup++, keep, summary, lead);
        summary.Group = group;
        _lines.Add(summary);
        _groups[group.Id] = group;
        if (_width > 0)
        {
            summary.Rows = Wrap(summary, _width, _rows);
        }

        return group;
    }

    // The run the lines an append opens belong to (members), null for a plain append.
    private Group? _tagging;

    // Whether the append's first new member has counted its write among the run's units (2026-10-03).
    private bool _unitTaken;

    /// <summary>The next member append joins the open run's last unit rather than starting one (2026-10-04: a tool's result with its call).</summary>
    public bool ContinueUnit { get; set; }

    // The picture spans for the lines an append opens, in order, and how many were given out (later on 2026-09-24).
    private IReadOnlyList<IReadOnlyList<PictureSpan>>? _picturing;
    private int _pictured;

    private Line Open(List<Line> touched)
    {
        var line = new Line();
        if (_picturing is { } pictures && _pictured < pictures.Count)
        {
            var spans = pictures[_pictured++];
            line.Pictures = spans.Count > 0 ? spans : null;
        }

        if (_tagging is { } group)
        {
            line.Group = group;
            line.Member = group.Members.Count;
            if (!_unitTaken)
            {
                group.Units++;
                _unitTaken = true;
            }

            line.Unit = group.Units - 1;
            group.Members.Add(line);
        }

        _lines.Add(line);
        touched.Add(line);
        return line;
    }

    /// <summary>
    /// The rows from <paramref name="from"/> to the end laid out again (a run near the tail: cheap),
    /// and the change flagged for the pane. Nothing before the first layout.
    /// </summary>
    private void Relayout(Line from)
    {
        _reshaped = true;
        if (_width <= 0)
        {
            return;
        }

        int index = _lines.LastIndexOf(from);
        if (index < 0)
        {
            return;
        }

        int rows = 0;
        for (int i = index; i < _lines.Count; i++)
        {
            rows += _lines[i].Rows;
        }

        _rows.RemoveRange(_rows.Count - rows, rows);
        for (int i = index; i < _lines.Count; i++)
        {
            _lines[i].Rows = Wrap(_lines[i], _width, _rows);
        }
    }

    /// <summary>
    /// What <paramref name="line"/> shows: its segments, a run's summary in the state it is in, the
    /// run's lead over the first visible row's indent — or null when a fold hides it.
    /// </summary>
    private IReadOnlyList<Segment>? Shown(Line line)
    {
        if (line.Group is not { } group)
        {
            return Folded(line, line.Segments);
        }

        bool over = group.Over;
        bool expanded = Expanded(group);
        IReadOnlyList<Segment> segments;
        if (line.Member < 0)
        {
            if (!group.Folds)
            {
                // A tool run's summary hides until it folds; a code block's is its plain label.
                if (group.Plain is null)
                {
                    return null;
                }

                segments = group.Plain;
            }
            else
            {
                segments = expanded ? group.Open : group.Collapsed;
            }
        }
        else
        {
            if (over && !expanded && !(group.Live && (group.Streamed || line.Unit >= group.Units - group.Keep)))
            {
                return null;
            }

            if (Folded(line, line.Segments) is not { } shown)
            {
                return null;
            }

            segments = shown;
        }

        // The summary is the first visible row whenever it shows; else every member does, the first leading.
        bool first = line.Member < 0 || (!over && line.Member == 0);
        return first && group.Lead.Count > 0 ? WithLead(group.Lead, segments) : segments;
    }

    /// <summary>
    /// What a diff's line shows (2026-10-04): <paramref name="segments"/> while its diff does not fold (or it is in none); once it
    /// does, the summary in the state it is in, and a row only while the diff is unfolded.
    /// </summary>
    private IReadOnlyList<Segment>? Folded(Line line, IReadOnlyList<Segment> segments)
    {
        if (line.Fold is not { Folds: true } diff)
        {
            return segments;
        }

        bool expanded = Expanded(diff);
        return line.FoldHead ? (expanded ? diff.Open : diff.Collapsed) : expanded ? segments : null;
    }

    /// <summary><paramref name="segments"/> with <paramref name="lead"/> over its leading spaces (as many as the lead's cells), or ahead of it without them.</summary>
    private static List<Segment> WithLead(IReadOnlyList<Segment> lead, IReadOnlyList<Segment> segments)
    {
        var result = new List<Segment>(lead);
        int skip = lead.Sum(s => TextCells.Width(s.Text));
        foreach (var segment in segments)
        {
            string text = segment.Text;
            int spaces = 0;
            while (skip > 0 && spaces < text.Length && text[spaces] == ' ')
            {
                spaces++;
                skip--;
            }

            if (spaces < text.Length)
            {
                skip = 0;
                result.Add(spaces == 0 ? segment : new Segment(text[spaces..], segment.Style));
            }
        }

        return result;
    }

    /// <summary>A line break: the current line closed (an empty one opened and closed when none was open — a blank row).</summary>
    private void Close(ref Line? current, List<Line> touched)
    {
        current ??= Open(touched);
        current.Closed = true;
        current = null;
    }

    /// <summary>
    /// The rows laid out at <paramref name="width"/> when it is not the cached one. A real width change (not the first layout, nor a fold's
    /// re-layout at the same width, <c>_width = −1</c>) tells the pane before (<see cref="Relaying"/>, to take its anchors) and after
    /// (<see cref="Relaid"/>), and renders sources again within <see cref="RenderBudget"/> — only a real change renders: a fold's re-layout
    /// uses what is cached and wraps the rest (2026-10-29, the resize review).
    /// </summary>
    private void Layout(int width)
    {
        width = Math.Max(1, width);
        if (width == _width)
        {
            return;
        }

        bool real = _width > 0;
        if (real)
        {
            Relaying?.Invoke();
        }

        int rendered = _rendered;
        long started = Clock.GetTimestamp();
        if (real && Render is not null)
        {
            RenderNewestFirst(width);
        }

        Rebuild(width);
        if (real)
        {
            Relaid?.Invoke(new WidthChange(width, _rendered - rendered, Clock.GetElapsedTime(started).TotalMilliseconds));
        }
    }

    /// <summary>Every row laid out afresh at <paramref name="width"/>, each picture's shift worked out first, the cap applied.</summary>
    private void Rebuild(int width)
    {
        _width = width;
        _rows.Clear();
        _shifts.Clear();
        Shifts(_lines, width);
        foreach (var line in _lines)
        {
            line.Rows = Wrap(line, width, _rows);
        }

        Trim();
    }

    /// <summary>
    /// The store laid out again (2026-10-29, <see cref="Relaid"/>): the width, how many writes were rendered again and how long it took
    /// (the resize's Debug line). The rows the pane holds follow through the <see cref="Anchor"/>s it took at <see cref="Relaying"/>.
    /// </summary>
    public sealed record WidthChange(int Width, int Rendered, double Milliseconds);

    /// <summary>
    /// Told on every real width change, and when <see cref="RenderPending"/> changed the rows, <em>before</em> the rows go (2026-10-29,
    /// the resize review): the pane takes an <see cref="Anchor"/> for each row it holds (the scroll, the find) while the old rows are
    /// there to read. Not on the first layout, nor on a fold's re-layout.
    /// </summary>
    public Action? Relaying { get; set; }

    /// <summary>
    /// Told after the rows were laid out again (2026-10-29): any call can be the first at the new width — a flow write, a restore, a hit
    /// test, a draw — so the store says so itself, and the pane moves what it holds to its anchors. Not on the first layout, nor on a
    /// fold's re-layout at the same width.
    /// </summary>
    public Action<WidthChange>? Relaid { get; set; }

    /// <summary>
    /// A place in the transcript's text that survives a re-layout (2026-10-29, the resize review: a row offset drifted when a line took
    /// more rows or fewer): the line, and how many of its characters — not white space, not a rule's — come before the place.
    /// </summary>
    public sealed class Anchor
    {
        internal Anchor(object line, int ink)
        {
            Line = line;
            Ink = ink;
        }

        internal object Line { get; }

        internal int Ink { get; }
    }

    /// <summary>Where an <see cref="Anchor"/> is now (2026-10-29): its row and character there, and its line's first row and row count.</summary>
    public readonly record struct Spot(int Row, int Start, int First, int Count);

    /// <summary>
    /// The anchor for store row <paramref name="row"/> from character <paramref name="start"/> of its text (<see cref="TextFind.LineText"/>)
    /// at the cached width; a row past the end clamps to the last, and an empty store gives none.
    /// </summary>
    public Anchor? Locate(int row, int start)
    {
        if (_rows.Count == 0)
        {
            return null;
        }

        row = Math.Clamp(row, 0, _rows.Count - 1);
        int at = 0;
        foreach (var line in _lines)
        {
            if (row < at + line.Rows)
            {
                string head = TextFind.LineText(_rows[at]);
                int ink = 0;
                for (int r = at; r < row; r++)
                {
                    ink += Ink(TextFind.LineText(_rows[r]), int.MaxValue, r == at ? null : head);
                }

                ink += Ink(TextFind.LineText(_rows[row]), Math.Max(0, start), row == at ? null : head);
                return new Anchor(line, ink);
            }

            at += line.Rows;
        }

        return null;
    }

    /// <summary>
    /// Where <paramref name="anchor"/> is at the cached width: the row and character holding its next counted character (its line's last
    /// row, at the end, when the line now holds fewer); (0, −1) for a line the cap dropped.
    /// </summary>
    public Spot Resolve(Anchor anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        int at = 0;
        foreach (var line in _lines)
        {
            if (line == anchor.Line)
            {
                string? head = line.Rows > 0 ? TextFind.LineText(_rows[at]) : null;
                int ink = 0;
                for (int r = at; r < at + line.Rows; r++)
                {
                    string text = TextFind.LineText(_rows[r]);
                    string? first = r == at ? null : head;
                    int here = Ink(text, int.MaxValue, first);
                    if (ink + here > anchor.Ink)
                    {
                        return new Spot(r, Index(text, anchor.Ink - ink, RepeatedBar(first, text)), at, line.Rows);
                    }

                    ink += here;
                }

                int last = Math.Max(at, at + line.Rows - 1);
                return new Spot(Math.Min(last, Math.Max(0, _rows.Count - 1)), line.Rows > 0 && anchor.Ink == 0 ? 0 : -1, at, line.Rows);
            }

            at += line.Rows;
        }

        return new Spot(0, -1, 0, 0);
    }

    /// <summary>Whether <paramref name="c"/> counts for an anchor: not white space, not a box-drawing or block glyph (a rule, a border, the quote's bar).</summary>
    private static bool Inked(char c) => !char.IsWhiteSpace(c) && c is not (>= '─' and <= '▟');

    /// <summary>
    /// The counted characters of <paramref name="text"/> before index <paramref name="upTo"/>; on a line's later row (<paramref name="first"/>
    /// its first row's text), not the ASCII bar a narrowed line repeats there (<see cref="RepeatedBar"/>).
    /// </summary>
    private static int Ink(string text, int upTo, string? first = null)
    {
        int ink = 0;
        for (int i = RepeatedBar(first, text); i < text.Length && i < upTo; i++)
        {
            if (Inked(text[i]))
            {
                ink++;
            }
        }

        return ink;
    }

    /// <summary>
    /// Where the text of a line's later row starts counting (2026-10-30, review F64: a narrowed <c>| a | b |</c> line repeats its ASCII bar on
    /// every row, so the same place counted one more character per row above it at one width than at another): past the leading blanks and
    /// <c>|</c> that open <paramref name="first"/> (its first row) when <paramref name="text"/> opens with the same; 0 otherwise. The box
    /// bars are never counted (<see cref="Inked"/>).
    /// </summary>
    private static int RepeatedBar(string? first, string text)
    {
        if (first is null)
        {
            return 0;
        }

        int lead = 0;
        while (lead < first.Length && first[lead] == ' ')
        {
            lead++;
        }

        return lead < first.Length && first[lead] == '|' && text.Length > lead && text.AsSpan(0, lead + 1).SequenceEqual(first.AsSpan(0, lead + 1))
            ? lead + 1
            : 0;
    }

    /// <summary>The index in <paramref name="text"/> of its counted character number <paramref name="ink"/> (from 0), counting from <paramref name="from"/>.</summary>
    private static int Index(string text, int ink, int from = 0)
    {
        for (int i = from; i < text.Length; i++)
        {
            if (Inked(text[i]) && ink-- == 0)
            {
                return i;
            }
        }

        return text.Length;
    }

    /// <summary>
    /// Whole lines off the front while the rows — or the lines, a folded run's hidden members taking
    /// none — exceed <see cref="MaxRows"/> (the last line always stays; a tool run goes whole, its
    /// summary with its members; a re-renderable write is one line, so it goes whole too); the rows dropped.
    /// </summary>
    private int Trim()
    {
        // The lines that go are counted first and removed together (2026-10-29: one at a time from the front was a move of every
        // row left for each line — narrowing a full store, thousands of them, took a tick's worth of time on its own).
        int lines = 0;
        int rows = 0;
        while ((_rows.Count - rows > MaxRows || _lines.Count - lines > MaxRows) && _lines.Count - lines > 1)
        {
            int count = 1;
            if (_lines[lines].Group is { } group)
            {
                while (lines + count < _lines.Count && _lines[lines + count].Group == group)
                {
                    count++;
                }

                if (lines + count >= _lines.Count)
                {
                    break;
                }

                _groups.Remove(group.Id);
            }
            else if (_lines[lines].Fold is { } diff)
            {
                // A diff with no run goes whole too, its summary with its rows (2026-10-04).
                while (lines + count < _lines.Count && _lines[lines + count].Fold == diff)
                {
                    count++;
                }

                if (lines + count >= _lines.Count)
                {
                    break;
                }
            }

            for (int i = lines; i < lines + count; i++)
            {
                rows += _lines[i].Rows;
                if (_lines[i] is { FoldHead: true, Fold: { } gone })
                {
                    _groups.Remove(gone.Id);
                    _liveDiffs.Remove(gone);
                }
            }

            lines += count;
        }

        if (lines == 0)
        {
            return 0;
        }

        if (_waiting is { } waiting && _lines.IndexOf(waiting.First, 0, lines) >= 0)
        {
            _waiting = null;
        }

        _rows.RemoveRange(0, rows);
        _lines.RemoveRange(0, lines);
        _frontDropped += lines;
        return rows;
    }

    /// <summary>Whether <paramref name="line"/> is laid out by the narrower rule at <paramref name="width"/>: written wider, and closed or a picture's row (an open text line keeps the exact cut).</summary>
    private static bool Narrowed(Line line, int width) =>
        line.BornWidth > 0 && width < line.BornWidth && (line.Closed || line.Pictures is not null);

    /// <summary>
    /// <paramref name="line"/> wrapped at <paramref name="width"/> onto <paramref name="rows"/> with the
    /// terminal's rule: cells accumulate on a row, a row is left when the next character does not
    /// fit (or the row is full and another comes), each row's runs keep their styles; an empty
    /// line is one empty row, a line a fold hides none. Returns the rows added.
    /// </summary>
    private int Wrap(Line line, int width, List<SegmentLine> rows)
    {
        line.Shift = 0;
        if (line.Parts is { } parts)
        {
            // A re-renderable write (2026-10-29): its own lines at its width, the rows rendered for this one when they are kept. The other
            // width's rows stay while it is home (2026-10-30, review F33: maximize and restore rendered every source again each way); one
            // copy per source, no more rows than the cap lets the store hold at that width.
            if (line.Reflow is { } source)
            {
                if (width != source.Width && source.CachedWidth == width && source.Cached is { } rendered)
                {
                    int added = 0;
                    foreach (var part in rendered)
                    {
                        added += Cut(part, width, narrower: true, picture: false, rows, out _);
                    }

                    return added;
                }
            }

            int taken = 0;
            foreach (var part in parts)
            {
                taken += Cut(part, width, width < line.BornWidth, picture: false, rows, out _);
            }

            return taken;
        }

        if (Shown(line) is not { } shown)
        {
            return 0;
        }

        // The open last line is the exact cut — the next write joins it on the screen, counted from where it ends — unless it is a
        // picture's row (the resize review): that stays one row, and the pane ends it before text joins (JoinNeedsBreak).
        int forced = line.Pictures is [var span, ..] && _shifts.TryGetValue(span.Id, out int shared) ? shared : -1;
        int count = Cut(shown, width, Narrowed(line, width), line.Pictures is not null, rows, out int shift, forced);
        line.Shift = shift;
        return count;
    }

    /// <summary>
    /// The shift each narrowed picture among <paramref name="lines"/> takes at <paramref name="width"/> (2026-10-29, the resize review: each
    /// row worked out its own, so a picture whose first row joined a label sheared): one for all its rows — the least padding any of them
    /// has on the left, no more than the most any of them overflows.
    /// </summary>
    private void Shifts(IEnumerable<Line> lines, int width)
    {
        var seen = new Dictionary<int, (int Padding, int Over)>();
        foreach (var line in lines)
        {
            if (line.Pictures is not [var span, ..] || !Narrowed(line, width))
            {
                continue;
            }

            int total = 0;
            int padding = 0;
            bool leading = true;
            foreach (var segment in line.Segments)
            {
                foreach (char c in segment.Text)
                {
                    if (leading && c == ' ' && segment.Style.Background == Color.Default)
                    {
                        padding++;
                    }
                    else
                    {
                        leading = false;
                    }
                }

                total += TextCells.Width(segment.Text);
            }

            int over = Math.Max(0, total - width);
            seen[span.Id] = seen.TryGetValue(span.Id, out var was) ? (Math.Min(was.Padding, padding), Math.Max(was.Over, over)) : (padding, over);
        }

        foreach (var (id, (padding, over)) in seen)
        {
            _shifts[id] = Math.Min(padding, over);
        }
    }

    /// <summary>
    /// One cell of a line being cut (2026-10-29): a cluster — a text element with its selectors, modifiers and joined elements (the
    /// resize review) — where it is in the line's joined text, its style and its cells.
    /// </summary>
    private readonly record struct Cell(string Source, int Start, int Length, Style Style, int Width)
    {
        /// <summary>A space or a tab (a tab is drawn as one space in a narrowed row).</summary>
        public bool Blank => Length == 1 && Source[Start] is ' ' or '\t';

        public char First => Source[Start];

        /// <summary>
        /// An emoji: a space after it is never a break, so the glyph keeps its label (2026-10-29, the resize review). Told by a variation
        /// selector or a symbol past the BMP, not by two cells (2026-10-30, review F63: Terminal.app draws a selector sequence in one,
        /// <see cref="TextCells.NarrowSelectorSequences"/>); a letter past the BMP (𝐀) is none.
        /// </summary>
        public bool Emoji => Source.AsSpan(Start, Length).Contains('️')
            || (Rune.DecodeFromUtf16(Source.AsSpan(Start, Length), out var rune, out _) == System.Buffers.OperationStatus.Done
                && !rune.IsBmp && Rune.GetUnicodeCategory(rune) == System.Globalization.UnicodeCategory.OtherSymbol);

        /// <summary>The cluster's first character is a letter, read as a whole rune (2026-10-30, review F65: a letter past the BMP is two chars).</summary>
        public bool Letter => Rune.DecodeFromUtf16(Source.AsSpan(Start, Length), out var rune, out _) == System.Buffers.OperationStatus.Done
            ? Rune.IsLetter(rune)
            : char.IsLetter(First);
    }

    /// <summary>A rule's glyph: a trailing run of it overflowing a narrowed line is dropped (an ASCII one only in a run of three or more).</summary>
    private static bool RuleGlyph(char c) => c is ScreenPane.RuleGlyph or '─' or '━' or '═' or '-';

    /// <summary>A bar a hanging indent repeats on its rows (the quote's, a table row's left border).</summary>
    private static bool BarGlyph(char c) => c is '▎' or '│' or '┃' or '|';

    /// <summary>
    /// <paramref name="line"/> fitted to <paramref name="width"/> as the store lays out a closed line written at <paramref name="bornWidth"/>
    /// (2026-10-29, the resize review: a streaming reply kept at the width it began committing at is drawn in a narrower window by the
    /// same rule its committed rows are, so the screen and the store agree).
    /// </summary>
    internal static List<SegmentLine> Fit(IReadOnlyList<Segment> line, int width, int bornWidth)
    {
        ArgumentNullException.ThrowIfNull(line);
        var rows = new List<SegmentLine>();
        Cut(line, Math.Max(1, width), width < bornWidth, picture: false, rows, out _);
        return rows;
    }

    /// <summary>
    /// <paramref name="shown"/> onto <paramref name="rows"/> at <paramref name="width"/>: the terminal's exact cut (<see cref="Exact"/>),
    /// unless <paramref name="narrower"/> and the line overflows (2026-10-29, the user's report). Then a picture line stays one row —
    /// its unstyled blank cells on the left go first, up to the overflow or <paramref name="forced"/> (the picture's shared shift), and the
    /// rest is cut at the edge — and any other line wraps by <see cref="WrapWords"/>.
    /// </summary>
    private static int Cut(IReadOnlyList<Segment> shown, int width, bool narrower, bool picture, List<SegmentLine> rows, out int shift, int forced = -1)
    {
        shift = 0;
        if (!narrower)
        {
            return Exact(shown, width, rows);
        }

        // The cells first (the resize review): a line that fits is the exact cut's one row, with nothing laid out twice.
        var text = new StringBuilder();
        var styles = new List<Style>();
        foreach (var segment in shown)
        {
            text.Append(segment.Text);
            for (int i = 0; i < segment.Text.Length; i++)
            {
                styles.Add(segment.Style);
            }
        }

        string all = text.ToString();
        var cells = new List<Cell>();
        int total = 0;
        for (int i = 0; i < all.Length;)
        {
            int length = 1;
            int cellWidth = all[i] == '\t' ? 1 : TextCells.ClusterWidth(all, i, out length);
            length = Math.Max(1, length);
            cells.Add(new Cell(all, i, length, styles[i], cellWidth));
            total += cellWidth;
            i += length;
        }

        if (total <= width && !(picture && forced > 0))
        {
            return Exact(shown, width, rows);   // a picture's row that fits still takes its picture's shift
        }

        return picture ? CutPicture(cells, total, width, rows, out shift, forced) : WrapWords(cells, total, width, rows);
    }

    /// <summary>A narrowed picture line (2026-10-29): <see cref="Cut"/>'s picture rule. Only a plain space with no background is padding — a pixel never is.</summary>
    private static int CutPicture(List<Cell> cells, int total, int width, List<SegmentLine> rows, out int shift, int forced)
    {
        int want = forced >= 0 ? forced : total - width;
        int first = 0;
        shift = 0;
        while (first < cells.Count && shift < want && cells[first].Blank && cells[first].Style.Background == Color.Default)
        {
            shift += cells[first].Width;
            first++;
        }

        int col = 0;
        int end = first;
        while (end < cells.Count && col + cells[end].Width <= width)
        {
            col += cells[end].Width;
            end++;
        }

        var row = new SegmentLine();
        AddCells(row, cells, first, end);
        rows.Add(row);
        return 1;
    }

    /// <summary>
    /// A narrowed line that is not a picture (2026-10-29): the blank cells and rule glyphs that overflow at its end dropped, its leading
    /// blanks past half the width dropped (the resize review), then wrapped at the last space before the edge — never inside its hanging
    /// indent, never right after an emoji — by the cell where a row has none. Each row after the first opens with the hanging indent: the
    /// leading blanks, a marker with no letters (a bullet, <c>1.</c>, a lead glyph) and the blanks after it, no wider than half the width;
    /// a bar marker (the quote's, a table border) is drawn again there, anything else as spaces.
    /// </summary>
    private static int WrapWords(List<Cell> cells, int total, int width, List<SegmentLine> rows)
    {
        int end = cells.Count;
        while (total > width && end > 0 && Droppable(cells, end))
        {
            total -= cells[end - 1].Width;
            end--;
        }

        int lead = 0;
        while (lead < end && cells[lead].Blank)
        {
            lead++;
        }

        int from = Math.Max(0, lead - width / 2);   // leading blanks past half the width go: the first row holds the first word
        int hang = Hang(cells, from, lead, end, width, out bool bar);
        int indent = 0;
        for (int i = from; i < hang; i++)
        {
            indent += cells[i].Width;
        }

        int start = from;
        int added = 0;
        while (from < end)
        {
            int prefix = added == 0 ? 0 : indent;
            int room = width - prefix;
            int col = 0;
            int stop = from;
            while (stop < end && col + cells[stop].Width <= room)
            {
                col += cells[stop].Width;
                stop++;
            }

            int rowEnd;
            int next;
            if (stop == end)
            {
                rowEnd = next = end;
            }
            else if (stop == from)
            {
                // A character wider than the room: alone on its row, without the indent when that is what it needs (the resize review).
                rowEnd = next = from + 1;
                if (cells[from].Width <= width)
                {
                    prefix = 0;
                }
            }
            else
            {
                // The last space before the edge, past the first word: not inside the hanging indent, not right after an emoji.
                int after = added == 0 ? hang : from;
                int space = -1;
                for (int k = stop; space < 0 && k > after; k--)
                {
                    if (cells[k].Blank && !cells[k - 1].Blank && !cells[k - 1].Emoji)
                    {
                        space = k;
                    }
                }

                if (space > from)
                {
                    rowEnd = space;
                    next = space;
                    while (next < end && cells[next].Blank)
                    {
                        next++;
                    }
                }
                else
                {
                    rowEnd = next = stop;
                }
            }

            while (next < end && cells[next].Blank)
            {
                next++;   // a row never opens with the blanks a cut left (an emoji's space)
            }

            var row = new SegmentLine();
            if (prefix > 0)
            {
                if (bar)
                {
                    AddCells(row, cells, start, hang);
                }
                else
                {
                    row.Add(new Segment(new string(' ', indent)));
                }
            }

            AddCells(row, cells, from, rowEnd);
            rows.Add(row);
            added++;
            from = next;
        }

        if (added == 0)
        {
            rows.Add(new SegmentLine());
            added = 1;
        }

        return added;
    }

    /// <summary>Whether the cell before <paramref name="end"/> may go when the line overflows: a blank, a box rule glyph, or an ASCII one in a run of three or more.</summary>
    private static bool Droppable(List<Cell> cells, int end)
    {
        var cell = cells[end - 1];
        if (cell.Blank)
        {
            return true;
        }

        if (cell.Length != 1 || !RuleGlyph(cell.First))
        {
            return false;
        }

        if (cell.First != '-')
        {
            return true;
        }

        int run = 0;
        for (int i = end - 1; i >= 0 && cells[i].Length == 1 && cells[i].First == '-'; i--)
        {
            run++;
        }

        return run >= 3;
    }

    /// <summary>
    /// Where a narrowed line's hanging indent ends (2026-10-29, the resize review): after the leading blanks from <paramref name="from"/> to
    /// <paramref name="lead"/>, a marker of at most four cells with no space and no letter (digits only as <c>1.</c> or <c>12)</c>) and the
    /// blanks after it; just the leading blanks when there is none, the line is only the marker, or it would be wider than half the width.
    /// <paramref name="bar"/>: the marker is a bar, drawn again on every row.
    /// </summary>
    private static int Hang(List<Cell> cells, int from, int lead, int end, int width, out bool bar)
    {
        bar = false;
        int m = lead;
        int cells4 = 0;
        while (m < end && !cells[m].Blank && !cells[m].Letter && cells4 + cells[m].Width <= 4)
        {
            cells4 += cells[m].Width;
            m++;
        }

        bool marker = m > lead && m < end && cells[m].Blank && Marker(cells, lead, m);
        int hang = lead;
        if (marker)
        {
            int k = m;
            while (k < end && cells[k].Blank)
            {
                k++;
            }

            if (k < end && Width(cells, from, k) <= width / 2)
            {
                hang = k;
                bar = m - lead == 1 && BarGlyph(cells[lead].First);
            }
        }

        return Width(cells, from, hang) <= width / 2 ? hang : from;
    }

    /// <summary>Whether cells <paramref name="from"/> to <paramref name="to"/> read as a marker: no digits, or digits closed by <c>.</c> or <c>)</c>.</summary>
    private static bool Marker(List<Cell> cells, int from, int to)
    {
        bool digits = false;
        for (int i = from; i < to; i++)
        {
            digits |= char.IsDigit(cells[i].First);
        }

        if (!digits)
        {
            return true;
        }

        for (int i = from; i < to - 1; i++)
        {
            if (!char.IsDigit(cells[i].First))
            {
                return false;
            }
        }

        return cells[to - 1].First is '.' or ')' && to - from > 1;
    }

    /// <summary>The cells of <paramref name="cells"/> from <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static int Width(List<Cell> cells, int from, int to)
    {
        int width = 0;
        for (int i = from; i < to; i++)
        {
            width += cells[i].Width;
        }

        return width;
    }

    /// <summary>Cells <paramref name="from"/> to <paramref name="to"/> onto <paramref name="row"/>, one segment per run of a style; a tab as a space.</summary>
    private static void AddCells(SegmentLine row, List<Cell> cells, int from, int to)
    {
        var run = new StringBuilder();
        var style = Style.Plain;
        for (int i = from; i < to; i++)
        {
            if (run.Length > 0 && !cells[i].Style.Equals(style))
            {
                row.Add(new Segment(run.ToString(), style));
                run.Clear();
            }

            style = cells[i].Style;
            if (cells[i].First == '\t')
            {
                run.Append(' ');
            }
            else
            {
                run.Append(cells[i].Source, cells[i].Start, cells[i].Length);
            }
        }

        if (run.Length > 0)
        {
            row.Add(new Segment(run.ToString(), style));
        }
    }

    /// <summary>
    /// <paramref name="shown"/> onto <paramref name="rows"/> with the terminal's rule: the exact cut <c>ScreenPane.Track</c> counts by.
    /// </summary>
    private static int Exact(IReadOnlyList<Segment> shown, int width, List<SegmentLine> rows)
    {
        int added = 1;
        var row = new SegmentLine();
        int col = 0;
        bool full = false;
        var run = new StringBuilder();
        foreach (var segment in shown)
        {
            string text = segment.Text;
            int i = 0;
            while (i < text.Length)
            {
                int cells = TextCells.ElementWidth(text, i, out int length);
                length = Math.Max(1, length);
                if (full || col + cells > width)
                {
                    if (run.Length > 0)
                    {
                        row.Add(new Segment(run.ToString(), segment.Style));
                        run.Clear();
                    }

                    rows.Add(row);
                    added++;
                    row = new SegmentLine();
                    col = 0;
                }

                run.Append(text, i, length);
                col += cells;
                full = col >= width;
                i += length;
            }

            if (run.Length > 0)
            {
                row.Add(new Segment(run.ToString(), segment.Style));
                run.Clear();
            }
        }

        rows.Add(row);
        return added;
    }
}
