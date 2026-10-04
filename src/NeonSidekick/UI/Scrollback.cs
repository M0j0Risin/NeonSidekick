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
/// is a group too (<see cref="BeginCodeGroup"/>), its label line the summary and its rows the
/// members. It differs from a tool run in three ways: the summary always shows (the plain label
/// until the block folds), it is measured by its source lines rather than its rows (a wrapped line
/// counts once), and while it is live every member shows — the block streamed at full height and
/// folds only once it is over. <see cref="ExpandAll"/> and <see cref="Toggle"/> are the runs'.</para>
///
/// <para>Thinking (2026-09-26, the user's ask): the model's thinking block is a group too
/// (<see cref="BeginThinkingGroup"/>), its header line the summary and its rows the members. It is a
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
/// </summary>
public sealed class Scrollback
{
    /// <summary>The most rows kept; whole lines are dropped from the front past it (a constant, not a setting).</summary>
    public const int MaxRows = 10_000;

    private readonly List<Line> _lines = new();
    private readonly List<SegmentLine> _rows = new();
    private readonly Dictionary<int, Group> _groups = new();
    private int _width = -1;
    private Group? _open;
    private int _nextGroup = 1;
    private bool _reshaped;

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

        /// <summary>A code block's group (<see cref="BeginCodeGroup"/>): the summary is its label, shown <see cref="Plain"/> until the block folds, and every member shows while it is live.</summary>
        public bool Code;

        /// <summary>A thinking block's group (<see cref="BeginThinkingGroup"/>): a code block's kind that is always over, so it folds the moment it ends.</summary>
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

    /// <summary>A tool run is open: the next member joins it. An open code or thinking block is not one (<see cref="BeginCodeGroup"/>, <see cref="BeginThinkingGroup"/>).</summary>
    public bool GroupOpen => _open is { Streamed: false };

    /// <summary>A code block's group is open (<see cref="BeginCodeGroup"/>): its next rows join it.</summary>
    public bool CodeGroupOpen => _open is { Code: true };

    /// <summary>A thinking block's group is open (<see cref="BeginThinkingGroup"/>): its next rows join it.</summary>
    public bool ThinkingGroupOpen => _open is { Thinking: true };

    /// <summary>The open code block's label line as drawn; empty without one.</summary>
    public IReadOnlyList<Segment> CodeGroupLabel => _open is { Code: true, Plain: { } plain } ? plain : Array.Empty<Segment>();

    /// <summary>The rows at the cached width (0 before the first <see cref="Rows"/> or <see cref="Append"/>).</summary>
    public int Count => _rows.Count;

    /// <summary>The logical lines kept (tests).</summary>
    public int LineCount => _lines.Count;

    /// <summary>The last line has no line break after it yet: the flow cursor sits at its end, not on the row under it.</summary>
    public bool LastLineOpen => _lines.Count > 0 && !_lines[^1].Closed;

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
    public int Append(IReadOnlyList<Segment> segments, int width, bool member, IReadOnlyList<IReadOnlyList<PictureSpan>>? pictures, FoldSpec? fold)
    {
        ArgumentNullException.ThrowIfNull(segments);
        _picturing = pictures;
        _pictured = 0;
        Layout(width);
        if (member)
        {
            if (_open is null)
            {
                // A diff written with no run (the one before this run) is over.
                EndDiffs();
                _open = NewGroup(0, Array.Empty<Segment>());
            }
        }
        else if (segments.Any(s => !s.IsControlCode))
        {
            EndGroup();
        }

        // The open line is wrapped again with what joins it: its rows leave the cache first.
        var touched = new List<Line>();
        Line? current = null;
        if (LastLineOpen)
        {
            current = _lines[^1];
            _rows.RemoveRange(_rows.Count - current.Rows, current.Rows);
            touched.Add(current);
            if (pictures is { Count: > 0 } && segments.Any(s => !s.IsControlCode))
            {
                // A picture's first row joins the open line (later on 2026-09-24): the first spans are that line's.
                var spans = pictures[_pictured++];
                current.Pictures = spans.Count > 0 ? spans : null;
            }
        }

        _tagging = member ? _open : null;
        _unitTaken = false;

        foreach (var segment in segments)
        {
            if (segment.IsControlCode)
            {
                continue;
            }

            if (segment.IsLineBreak)
            {
                Close(ref current, touched);
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
                        current ??= Open(touched);
                        current.Segments.Add(new Segment(piece, segment.Style));
                    }
                }

                if (end < 0)
                {
                    break;
                }

                Close(ref current, touched);
                from = end + 1;
            }
        }

        _tagging = null;
        _picturing = null;
        if (fold is { } spec)
        {
            MakeDiff(spec, touched);
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
            line.Rows = Wrap(line, width, _rows);
        }

        return Trim();
    }

    /// <summary>Everything forgotten (the screen was cleared); the pane-wide <see cref="ExpandAll"/> stays.</summary>
    public void Clear()
    {
        _lines.Clear();
        _rows.Clear();
        _groups.Clear();
        _liveDiffs.Clear();
        _open = null;
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
    public void SetGroupSummary(IReadOnlyList<Segment> collapsed, IReadOnlyList<Segment> expanded)
    {
        ArgumentNullException.ThrowIfNull(collapsed);
        ArgumentNullException.ThrowIfNull(expanded);
        if (_open is not { } group)
        {
            return;
        }

        group.Collapsed = collapsed.Where(s => !s.IsControlCode && !s.IsLineBreak).ToList();
        group.Open = expanded.Where(s => !s.IsControlCode && !s.IsLineBreak).ToList();
        if (group.Folds)
        {
            Relayout(group.Summary);
        }
    }

    /// <summary>
    /// Opens a code block's group (the open group ended first; an open last line closed) whose
    /// summary is <paramref name="label"/> — the block's label line as drawn, laid out at once, so it
    /// takes its row now as any line would — folding past <paramref name="keep"/> source lines once
    /// it is over (0 = never). <see cref="SetCodeGroupSummary"/> gives the folded look and the size.
    /// </summary>
    public void BeginCodeGroup(int keep, IReadOnlyList<Segment> label) => BeginStreamedGroup(keep, label, thinking: false);

    /// <summary>
    /// Opens a thinking block's group (the open group ended first; an open last line closed) whose
    /// summary is <paramref name="header"/> — the block's header line as drawn while it streams —
    /// folding the moment it ends. <see cref="SetThinkingGroupSummary"/> gives the folded look.
    /// </summary>
    public void BeginThinkingGroup(IReadOnlyList<Segment> header) => BeginStreamedGroup(0, header, thinking: true);

    /// <summary>The open thinking block's summary folded (<paramref name="collapsed"/>) and unfolded (<paramref name="expanded"/>). Nothing without one.</summary>
    public void SetThinkingGroupSummary(IReadOnlyList<Segment> collapsed, IReadOnlyList<Segment> expanded)
    {
        if (_open is { Thinking: true })
        {
            SetGroupSummary(collapsed, expanded);
        }
    }

    private void BeginStreamedGroup(int keep, IReadOnlyList<Segment> label, bool thinking)
    {
        ArgumentNullException.ThrowIfNull(label);
        EndGroup();
        if (LastLineOpen)
        {
            _lines[^1].Closed = true;
        }

        var summary = new Line { Closed = true, Member = -1 };
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
    public void SetCodeGroupSummary(IReadOnlyList<Segment> collapsed, IReadOnlyList<Segment> expanded, int size)
    {
        ArgumentNullException.ThrowIfNull(collapsed);
        ArgumentNullException.ThrowIfNull(expanded);
        if (_open is not { Code: true } group)
        {
            return;
        }

        group.Size = size;
        SetGroupSummary(collapsed, expanded);
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
    /// the span covering it on a picture's line, or null. Only while the line takes one row — narrowed past a strip, the
    /// line wraps and its tiles are no longer where they were drawn.
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

    private void Layout(int width)
    {
        width = Math.Max(1, width);
        if (width == _width)
        {
            return;
        }

        _width = width;
        _rows.Clear();
        foreach (var line in _lines)
        {
            line.Rows = Wrap(line, width, _rows);
        }

        Trim();
    }

    /// <summary>
    /// Whole lines off the front while the rows — or the lines, a folded run's hidden members taking
    /// none — exceed <see cref="MaxRows"/> (the last line always stays; a tool run goes whole, its
    /// summary with its members); the rows dropped.
    /// </summary>
    private int Trim()
    {
        int dropped = 0;
        while ((_rows.Count > MaxRows || _lines.Count > MaxRows) && _lines.Count > 1)
        {
            int count = 1;
            if (_lines[0].Group is { } group)
            {
                while (count < _lines.Count && _lines[count].Group == group)
                {
                    count++;
                }

                if (count >= _lines.Count)
                {
                    break;
                }

                _groups.Remove(group.Id);
            }
            else if (_lines[0].Fold is { } diff)
            {
                // A diff with no run goes whole too, its summary with its rows (2026-10-04).
                while (count < _lines.Count && _lines[count].Fold == diff)
                {
                    count++;
                }

                if (count >= _lines.Count)
                {
                    break;
                }
            }

            int rows = 0;
            for (int i = 0; i < count; i++)
            {
                rows += _lines[i].Rows;
                if (_lines[i] is { FoldHead: true, Fold: { } gone })
                {
                    _groups.Remove(gone.Id);
                    _liveDiffs.Remove(gone);
                }
            }

            _rows.RemoveRange(0, rows);
            _lines.RemoveRange(0, count);
            dropped += rows;
        }

        return dropped;
    }

    /// <summary>
    /// <paramref name="line"/> wrapped at <paramref name="width"/> onto <paramref name="rows"/> with the
    /// terminal's rule: cells accumulate on a row, a row is left when the next character does not
    /// fit (or the row is full and another comes), each row's runs keep their styles; an empty
    /// line is one empty row, a line a fold hides none. Returns the rows added.
    /// </summary>
    private int Wrap(Line line, int width, List<SegmentLine> rows)
    {
        if (Shown(line) is not { } shown)
        {
            return 0;
        }

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
