using System.Text;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Viewer;

/// <summary>What a key, or the wheel, asks of the log window (2026-10-02).</summary>
public enum LogViewAction
{
    None,

    /// <summary>↑: one row up.</summary>
    LineUp,

    /// <summary>↓: one row down; reaching the bottom follows again.</summary>
    LineDown,

    /// <summary>PgUp: a page up.</summary>
    PageUp,

    /// <summary>PgDn: a page down; reaching the bottom follows again.</summary>
    PageDown,

    /// <summary>Ctrl+Home: the first line, following paused (the user's spec).</summary>
    Top,

    /// <summary>Ctrl+End or Ctrl+E: the last line, following again (the user's spec).</summary>
    Bottom,

    /// <summary>Ctrl+A: every line held selected.</summary>
    SelectAll,

    /// <summary>Ctrl+C: the selection to the clipboard.</summary>
    Copy,

    /// <summary>F11 (or a double-click): full screen on or off.</summary>
    ToggleFullScreen,

    /// <summary>Esc in full screen: back to a window.</summary>
    LeaveFullScreen,

    /// <summary>Esc in a window: the window closed.</summary>
    Close,
}

/// <summary>A place in the log: a line by its <see cref="LogLine.Seq"/> and a character of its text (the caret before it). Ordered as read.</summary>
public readonly record struct LogPosition(long Seq, int Index) : IComparable<LogPosition>
{
    public int CompareTo(LogPosition other) => Seq != other.Seq ? Seq.CompareTo(other.Seq) : Index.CompareTo(other.Index);

    public static bool operator <(LogPosition left, LogPosition right) => left.CompareTo(right) < 0;

    public static bool operator >(LogPosition left, LogPosition right) => left.CompareTo(right) > 0;

    public static bool operator <=(LogPosition left, LogPosition right) => left.CompareTo(right) <= 0;

    public static bool operator >=(LogPosition left, LogPosition right) => left.CompareTo(right) >= 0;
}

/// <summary>One row on screen: <see cref="Length"/> characters of a line's display <see cref="Text"/> from <see cref="Start"/>.</summary>
public readonly record struct LogRow(long Seq, DiagnosticLevel Level, string Text, int Start, int Length)
{
    /// <summary>Whether the row is the last of its piece of the line: the text ends after it, or a line break does.</summary>
    public bool EndsSegment => Start + Length >= Text.Length || Text[Start + Length] == '\n';
}

/// <summary>
/// What the log window decides without a window (2026-10-02, the user's ask: <c>/log</c> opens a window that follows the
/// log): the lines it mirrors from the <see cref="DiagnosticBuffer"/>, wrapped to a monospace grid of <see cref="Columns"/>
/// cells, the rows in view, whether it follows the newest line, the selection and the scroll bar's arithmetic. Pure, so
/// every rule is tested without a window; <c>LogWindow</c> only measures the font, draws and forwards the input.
///
/// <para>Following (the user's spec): on while the view's bottom is the last row, so a new line moves the view with it;
/// any scroll that leaves the bottom pauses it and any that reaches the bottom (the wheel, ↓, PgDn, the scroll bar) turns
/// it on again, as Ctrl+End and Ctrl+E do; Ctrl+Home goes to the top and pauses. A paused view is anchored to its own rows,
/// which never renumber: a line added at the end or dropped from the front (the buffer is a ring) leaves it where it is,
/// unless the very rows it shows are dropped.</para>
///
/// <para>A line is shown as the file holds it, wrapped at the cell (no word wrap: a log line is paths and numbers, and the
/// grid keeps a click's character exact); a line break inside a message starts a new row, a tab or other control character
/// is a space. The selection is a range of <see cref="LogPosition"/>s, so it stays on its lines while they move.</para>
/// </summary>
public sealed class LogViewState
{
    /// <summary>WHEEL_DELTA: one notch of the wheel.</summary>
    public const int WheelDelta = 120;

    // Virtual-key codes (winuser.h) the window answers besides ViewerState's Esc, F11, ↑, ↓, Home and End.
    public const int VkPrior = 0x21;
    public const int VkNext = 0x22;
    public const int VkA = 0x41;
    public const int VkC = 0x43;
    public const int VkE = 0x45;

    private readonly List<Entry> _entries = [];
    private long _end;
    private long _top;
    private LogPosition? _anchor;
    private LogPosition? _caret;

    /// <summary>The cells a row holds; at least one.</summary>
    public int Columns { get; private set; } = 80;

    /// <summary>The whole rows the view shows; at least one. The window draws one more, cut by its bottom edge.</summary>
    public int VisibleRows { get; private set; } = 25;

    /// <summary>Whether the view follows the newest line (see the class notes). On from the start.</summary>
    public bool Following { get; private set; } = true;

    /// <summary>The lines held.</summary>
    public int LineCount => _entries.Count;

    /// <summary>The rows every line held wraps to.</summary>
    public long TotalRows => _end - Base;

    /// <summary>The first row in view, from 0.</summary>
    public long TopRow => _top - Base;

    /// <summary>The <see cref="LogLine.Seq"/> after the last line held: what to ask the buffer for next.</summary>
    public long NextSeq => _entries.Count > 0 ? _entries[^1].Seq + 1 : _nextSeq;

    private long _nextSeq;

    // The rows never renumber: a line keeps its first row while lines come and go, and Base is the first row held.
    private long Base => _entries.Count > 0 ? _entries[0].RowStart : _end;

    private long MaxTop => Math.Max(Base, _end - VisibleRows);

    private sealed class Entry(long seq, DiagnosticLevel level, string text)
    {
        public long Seq { get; } = seq;
        public DiagnosticLevel Level { get; } = level;
        public string Text { get; } = text;
        public long RowStart { get; set; }
        public int Rows { get; set; }
    }

    /// <summary>
    /// The buffer's news: every line of <paramref name="lines"/> after the last one held added at the end, and the lines before
    /// <paramref name="firstHeld"/> (the buffer's oldest, <see cref="DiagnosticBuffer.CopySince"/>) dropped from the front.
    /// A following view moves to the new bottom; a selection on dropped lines is cut to what is left. True when anything changed.
    /// </summary>
    public bool Append(IReadOnlyList<LogLine> lines, long firstHeld)
    {
        ArgumentNullException.ThrowIfNull(lines);
        int drop = 0;
        while (drop < _entries.Count && _entries[drop].Seq < firstHeld)
        {
            drop++;
        }

        bool changed = drop > 0;
        if (drop > 0)
        {
            _nextSeq = _entries[drop - 1].Seq + 1;
            _entries.RemoveRange(0, drop);
        }

        foreach (var line in lines)
        {
            if (line.Seq < firstHeld || line.Seq < NextSeq)
            {
                continue;
            }

            var entry = new Entry(line.Seq, line.Level, Display(line.Text)) { RowStart = _end };
            entry.Rows = CountRows(entry.Text, Columns);
            _end += entry.Rows;
            _entries.Add(entry);
            changed = true;
        }

        if (!changed)
        {
            return false;
        }

        ClipSelection();
        _top = Following ? MaxTop : Math.Clamp(_top, Base, MaxTop);
        return true;
    }

    /// <summary>
    /// The grid the window's client area holds now: <paramref name="columns"/> cells by <paramref name="visibleRows"/> whole
    /// rows. New columns re-wrap every line, the first line in view kept first; following stays as it was.
    /// </summary>
    public void Resize(int columns, int visibleRows)
    {
        columns = Math.Max(1, columns);
        visibleRows = Math.Max(1, visibleRows);
        if (columns != Columns)
        {
            int at = _entries.Count > 0 ? FindEntry(_top) : -1;
            int within = at >= 0 ? (int)(_top - _entries[at].RowStart) : 0;
            Columns = columns;
            long row = Base;
            foreach (var entry in _entries)
            {
                entry.RowStart = row;
                entry.Rows = CountRows(entry.Text, columns);
                row += entry.Rows;
            }

            _end = row;
            _top = at >= 0 ? _entries[at].RowStart + Math.Min(within, _entries[at].Rows - 1) : _end;
        }

        VisibleRows = visibleRows;
        _top = Following ? MaxTop : Math.Clamp(_top, Base, MaxTop);
    }

    /// <summary>The rows from the first in view: <see cref="VisibleRows"/> plus <paramref name="extra"/> at most (the cut-off row under them).</summary>
    public IReadOnlyList<LogRow> Rows(int extra = 1)
    {
        var rows = new List<LogRow>(VisibleRows + extra);
        if (_entries.Count == 0)
        {
            return rows;
        }

        int want = VisibleRows + Math.Max(0, extra);
        int at = FindEntry(_top);
        int skip = (int)(_top - _entries[at].RowStart);
        for (; at < _entries.Count && rows.Count < want; at++)
        {
            var entry = _entries[at];
            var walker = new RowWalker(entry.Text, Columns);
            int index = 0;
            while (rows.Count < want && walker.Next(out int start, out int length))
            {
                if (index++ >= skip)
                {
                    rows.Add(new LogRow(entry.Seq, entry.Level, entry.Text, start, length));
                }
            }

            skip = 0;
        }

        return rows;
    }

    /// <summary>The view moved by <paramref name="rows"/> (down when positive), kept on the lines; following is whether it ends at the bottom.</summary>
    public void ScrollBy(long rows) => ScrollToRow(_top + rows);

    /// <summary>The view's first row set to <paramref name="row"/> (from 0; the scroll bar's thumb), kept on the lines; following as <see cref="ScrollBy"/>.</summary>
    public void ScrollTo(long row) => ScrollToRow(Base + row);

    /// <summary>The first line in view and following paused (Ctrl+Home, the user's spec), however few lines there are.</summary>
    public void ScrollToTop()
    {
        _top = Base;
        Following = false;
    }

    /// <summary>The last line in view and following again (Ctrl+End, Ctrl+E).</summary>
    public void ScrollToBottom()
    {
        _top = MaxTop;
        Following = true;
    }

    /// <summary>A scrolling or selecting action done; false for one the window does itself (copy, full screen, close) or none.</summary>
    public bool Apply(LogViewAction action)
    {
        switch (action)
        {
            case LogViewAction.LineUp:
                ScrollBy(-1);
                return true;
            case LogViewAction.LineDown:
                ScrollBy(1);
                return true;
            case LogViewAction.PageUp:
                ScrollBy(-Page);
                return true;
            case LogViewAction.PageDown:
                ScrollBy(Page);
                return true;
            case LogViewAction.Top:
                ScrollToTop();
                return true;
            case LogViewAction.Bottom:
                ScrollToBottom();
                return true;
            case LogViewAction.SelectAll:
                SelectAll();
                return true;
            default:
                return false;
        }
    }

    /// <summary>The rows a page moves: the view less one, so the row at its edge stays in sight.</summary>
    public int Page => Math.Max(1, VisibleRows - 1);

    /// <summary>
    /// The place under a cell: <paramref name="row"/> rows below the first in view (negative above it, past the last row
    /// below it: the first or last place held) and <paramref name="column"/> cells in, held to the row's text. Null with no line.
    /// </summary>
    public LogPosition? HitTest(int row, int column)
    {
        if (_entries.Count == 0)
        {
            return null;
        }

        long abs = _top + row;
        if (abs < Base)
        {
            return new LogPosition(_entries[0].Seq, 0);
        }

        if (abs >= _end)
        {
            return new LogPosition(_entries[^1].Seq, _entries[^1].Text.Length);
        }

        var entry = _entries[FindEntry(abs)];
        var (start, length) = RowSpan(entry.Text, Columns, (int)(abs - entry.RowStart));
        int index = start + Math.Clamp(column, 0, length);
        if (index > 0 && index < entry.Text.Length && char.IsLowSurrogate(entry.Text[index]))
        {
            index--;
        }

        return new LogPosition(entry.Seq, index);
    }

    /// <summary>A selection started at <paramref name="at"/>: a press, nothing selected until it moves.</summary>
    public void BeginSelection(LogPosition at)
    {
        _anchor = at;
        _caret = at;
    }

    /// <summary>The selection stretched to <paramref name="to"/> from where it started (a drag, or a Shift+click).</summary>
    public void ExtendSelection(LogPosition to)
    {
        _anchor ??= to;
        _caret = to;
    }

    /// <summary>Every line held selected (Ctrl+A); the view does not move.</summary>
    public void SelectAll()
    {
        if (_entries.Count == 0)
        {
            return;
        }

        _anchor = new LogPosition(_entries[0].Seq, 0);
        _caret = new LogPosition(_entries[^1].Seq, _entries[^1].Text.Length);
    }

    public void ClearSelection()
    {
        _anchor = null;
        _caret = null;
    }

    /// <summary>Whether anything is selected: a press that has not moved selects nothing.</summary>
    public bool HasSelection => Selection is not null;

    /// <summary>The selection in reading order, or null when nothing is selected.</summary>
    public (LogPosition From, LogPosition To)? Selection =>
        _anchor is { } a && _caret is { } c && a != c ? (a < c ? (a, c) : (c, a)) : null;

    /// <summary>The selected text, lines (and the breaks inside them) joined by CRLF, as the clipboard wants it; empty when nothing is selected.</summary>
    public string SelectedText()
    {
        if (Selection is not { } selection)
        {
            return "";
        }

        var text = new StringBuilder();
        int first = FindSeq(selection.From.Seq);
        for (int i = first; i < _entries.Count && _entries[i].Seq <= selection.To.Seq; i++)
        {
            var entry = _entries[i];
            int from = entry.Seq == selection.From.Seq ? Math.Min(selection.From.Index, entry.Text.Length) : 0;
            int to = entry.Seq == selection.To.Seq ? Math.Min(selection.To.Index, entry.Text.Length) : entry.Text.Length;
            if (i > first)
            {
                text.Append("\r\n");
            }

            if (to > from)
            {
                text.Append(entry.Text.AsSpan(from, to - from)).Replace("\n", "\r\n", text.Length - (to - from), to - from);
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// The cells of <paramref name="row"/> to draw selected, from its first cell: its selected characters, and one cell more
    /// when the selection runs on past the row's line break (so a selected empty line shows). Null when none.
    /// </summary>
    public (int From, int To)? RowSelection(LogRow row)
    {
        if (Selection is not { } selection)
        {
            return null;
        }

        var rowStart = new LogPosition(row.Seq, row.Start);
        var rowEnd = new LogPosition(row.Seq, row.Start + row.Length);
        if (selection.To < rowStart || selection.From > rowEnd)
        {
            return null;
        }

        int from = (selection.From > rowStart ? selection.From.Index : row.Start) - row.Start;
        int to = (selection.To < rowEnd ? selection.To.Index : row.Start + row.Length) - row.Start;
        if (selection.To > rowEnd && row.EndsSegment)
        {
            to = row.Length + 1;
        }

        return to > from ? (from, to) : null;
    }

    /// <summary>
    /// The scroll bar's thumb for <paramref name="total"/> rows, <paramref name="visible"/> of them in view from
    /// <paramref name="top"/>, on a track <paramref name="track"/> pixels long: its offset and length, never shorter than
    /// <paramref name="minLength"/>; the whole track while every row is in view. Pure.
    /// </summary>
    public static (int Offset, int Length) Thumb(long total, long visible, long top, int track, int minLength)
    {
        if (track <= 0)
        {
            return (0, 0);
        }

        if (total <= visible || visible <= 0)
        {
            return (0, track);
        }

        int length = (int)Math.Clamp(track * visible / total, Math.Min(minLength, track), track);
        long maxTop = total - visible;
        int offset = (int)((track - length) * Math.Clamp(top, 0, maxTop) / maxTop);
        return (offset, length);
    }

    /// <summary>The first row in view for a thumb dragged to <paramref name="offset"/> (<see cref="Thumb"/>'s inverse). Pure.</summary>
    public static long TopForThumb(int offset, long total, long visible, int track, int length)
    {
        long maxTop = total - visible;
        int travel = track - length;
        if (maxTop <= 0 || travel <= 0)
        {
            return 0;
        }

        return Math.Clamp((long)Math.Round((double)offset * maxTop / travel, MidpointRounding.AwayFromZero), 0, maxTop);
    }

    /// <summary>
    /// The rows a wheel message scrolls (down when positive): <paramref name="delta"/> (WM_MOUSEWHEEL's, up when positive)
    /// added to <paramref name="remainder"/>, the whole notches taken, <paramref name="linesPerNotch"/> rows each
    /// (SPI_GETWHEELSCROLLLINES: 0 is none, -1 — WHEEL_PAGESCROLL — a <paramref name="page"/> each). A precision touchpad's
    /// small deltas add up to a notch. Pure.
    /// </summary>
    public static int WheelRows(ref int remainder, int delta, int linesPerNotch, int page)
    {
        remainder += delta;
        int notches = remainder / WheelDelta;
        remainder -= notches * WheelDelta;
        if (linesPerNotch == 0)
        {
            return 0;
        }

        return -notches * (linesPerNotch < 0 ? Math.Max(1, page) : linesPerNotch);
    }

    /// <summary>
    /// What a key does (the user's spec): Esc out of full screen, then the window closed; F11 full screen; Ctrl+Home the top,
    /// following paused; Ctrl+End or Ctrl+E the bottom, following again; Ctrl+A every line selected, Ctrl+C the selection
    /// copied; ↑/↓ a row, PgUp/PgDn a page. Pure.
    /// </summary>
    public static LogViewAction ActionFor(int virtualKey, bool control, bool fullScreen) => (virtualKey, control) switch
    {
        (ViewerState.VkEscape, _) => fullScreen ? LogViewAction.LeaveFullScreen : LogViewAction.Close,
        (ViewerState.VkF11, _) => LogViewAction.ToggleFullScreen,
        (ViewerState.VkHome, true) => LogViewAction.Top,
        (ViewerState.VkEnd, true) or (VkE, true) => LogViewAction.Bottom,
        (VkA, true) => LogViewAction.SelectAll,
        (VkC, true) => LogViewAction.Copy,
        (ViewerState.VkUp, false) => LogViewAction.LineUp,
        (ViewerState.VkDown, false) => LogViewAction.LineDown,
        (VkPrior, false) => LogViewAction.PageUp,
        (VkNext, false) => LogViewAction.PageDown,
        _ => LogViewAction.None,
    };

    /// <summary>
    /// A line as the window draws it: CRLF and a lone CR a line break, a tab or any other control character a space, the
    /// breaks at its end dropped. Pure.
    /// </summary>
    public static string Display(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = text.TrimEnd('\r', '\n');
        bool clean = true;
        foreach (char c in text)
        {
            if (c < ' ' && c != '\n')
            {
                clean = false;
                break;
            }
        }

        if (clean)
        {
            return text;
        }

        var display = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    continue;
                }

                display.Append('\n');
            }
            else
            {
                display.Append(c < ' ' && c != '\n' ? ' ' : c);
            }
        }

        return display.ToString();
    }

    /// <summary>The rows <paramref name="text"/> (a <see cref="Display"/> line) wraps to at <paramref name="columns"/> cells: one per line break more, one at least. Pure.</summary>
    public static int CountRows(string text, int columns)
    {
        var walker = new RowWalker(text, Math.Max(1, columns));
        int rows = 0;
        while (walker.Next(out _, out _))
        {
            rows++;
        }

        return rows;
    }

    /// <summary>Where row <paramref name="row"/> of <paramref name="text"/> starts and how long it is, wrapped at <paramref name="columns"/>. Pure.</summary>
    public static (int Start, int Length) RowSpan(string text, int columns, int row)
    {
        var walker = new RowWalker(text, Math.Max(1, columns));
        int start = 0, length = 0;
        for (int i = 0; i <= row && walker.Next(out start, out length); i++)
        {
        }

        return (start, length);
    }

    private void ScrollToRow(long abs)
    {
        _top = Math.Clamp(abs, Base, MaxTop);
        Following = _top >= MaxTop;
    }

    // The selection's lines dropped from the front: what is left of it kept, or none.
    private void ClipSelection()
    {
        if (Selection is not { } selection)
        {
            return;
        }

        if (_entries.Count == 0 || selection.To.Seq < _entries[0].Seq)
        {
            ClearSelection();
            return;
        }

        if (selection.From.Seq < _entries[0].Seq)
        {
            var first = new LogPosition(_entries[0].Seq, 0);
            if (_anchor == selection.From)
            {
                _anchor = first;
            }
            else
            {
                _caret = first;
            }
        }
    }

    // The entry holding row abs (abs within the rows held): the last whose first row is at or before it.
    private int FindEntry(long abs)
    {
        int lo = 0, hi = _entries.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_entries[mid].RowStart <= abs)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }

    // The entry with seq, or the first after it.
    private int FindSeq(long seq)
    {
        int lo = 0, hi = _entries.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_entries[mid].Seq < seq)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    // A line's rows in order: each piece between line breaks cut every `columns` characters (never between a surrogate pair),
    // an empty piece one empty row.
    private struct RowWalker(string text, int columns)
    {
        private int _position;
        private bool _done;

        public bool Next(out int start, out int length)
        {
            if (_done)
            {
                start = length = 0;
                return false;
            }

            int lineEnd = text.IndexOf('\n', _position);
            if (lineEnd < 0)
            {
                lineEnd = text.Length;
            }

            int rest = lineEnd - _position;
            int take = Math.Min(columns, rest);
            if (take < rest && take > 1 && char.IsHighSurrogate(text[_position + take - 1]))
            {
                take--;
            }

            start = _position;
            length = take;
            if (take >= rest)
            {
                if (lineEnd >= text.Length)
                {
                    _done = true;
                }
                else
                {
                    _position = lineEnd + 1;
                }
            }
            else
            {
                _position += take;
            }

            return true;
        }
    }
}
