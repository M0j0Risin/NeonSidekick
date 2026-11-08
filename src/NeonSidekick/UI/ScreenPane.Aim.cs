using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.UI;

/// <summary>
/// Where clicks and the pointer land on an overlay (2026-11-07, menu clicks that hit the wrong item): a click aimed at an older layout
/// is dropped, and the row under the pointer gets a slab. Both read the <see cref="OverlayAim"/> the pane handed
/// <see cref="ShowOverlay"/>.
/// </summary>
public sealed partial class ScreenPane
{
    /// <summary>How long after the overlay's rows change a click on a changed row is taken as aimed at the old ones. A constant, not a setting.</summary>
    public static readonly TimeSpan ClickGrace = TimeSpan.FromMilliseconds(250);

    /// <summary>How long after a dropped click every click counts, so a page changing faster than the grace never stops answering.</summary>
    public static readonly TimeSpan DroppedClickPass = TimeSpan.FromSeconds(1);

    /// <summary>How many of the layouts the overlay showed <see cref="ClickIsStale"/> remembers (2026-11-07, the second code review).</summary>
    private const int AimHistoryLength = 8;

    // What the last draw showed and where, and when that changed. Under the gate.
    private OverlayAim? _aimShown;
    private int _aimTop;
    private long? _aimChangedAt;
    private long? _clickDroppedAt;

    // The layouts the overlay showed, oldest first, each with the moment it went on show (2026-11-07, the second code review: a click compared
    // the one before a burst of changes with the last, so two changes after a press, the second past the grace, judged it against a layout
    // it was never aimed at). Under the gate.
    private readonly List<(OverlayAim? Aim, int Top, long From)> _aimHistory = [];

    // The target with the slab, and the pointer's move count it's hidden until. Under the gate.
    private OverlayTarget? _hoverTarget;
    private int _hoverQuietMoves;

    // The overlay's rows as the last draw rendered them, before the slab, and the overlay and width they're for. Under the gate.
    private (Overlay Overlay, int Width, List<SegmentLine> Lines)? _overlayDrawnLines;

    // What the last lookup found and what it was worked out from: the pointer's move count, the aim, the drawn top row. Under the gate.
    private (int Moves, OverlayAim Aim, int Top, OverlayTarget? Found)? _hoverLookedUp;

    // Written by the input reader's thread (FocusChanged). _focusChange has its one reader, the companion, so this is its own.
    private int _focusLost;

    /// <summary>
    /// Whether <see cref="ClickIsStale"/> can say yes: only over a real mouse, which <c>SidekickApp</c> sets beside
    /// <c>ChatScreen.UseHover</c>. Scripted input clicks right after a page opens, with no time passing, and a dropped click there would hang it.
    /// </summary>
    public bool GuardsStaleClicks { get; set; }

    /// <summary>The target with the slab now (tests).</summary>
    internal OverlayTarget? HoverTargetNow
    {
        get
        {
            lock (_gate)
            {
                return _hoverTarget;
            }
        }
    }

    /// <summary>
    /// Whether a click on overlay row <paramref name="row"/>, column <paramref name="x"/> should be dropped: the overlay's rows changed
    /// after, or within <see cref="ClickGrace"/> before, the click was pressed (<paramref name="pressedAt"/>, <see cref="InputEvent.Click.At"/>), and that
    /// spot on the layout the click was aimed at, the one on show <see cref="ClickGrace"/> before the press, didn't show what it shows now. Measured
    /// from the press since 2026-11-07 (the code review: from the read, a click waiting behind a slow callback outlived the grace and landed on the
    /// new row), and against the remembered layouts since later that day (the second review), so any number of changes after the press count. A
    /// press older than every layout remembered is taken as stale. A click with no press time (0, scripted input) or one stamped after now
    /// (another clock) is taken as pressed now. Costs one click at most: for <see cref="DroppedClickPass"/> after a drop, every click counts. Only
    /// for clicks on list rows: the ×, the strip and the ▲▼ row work at any speed. The caller resets its double-click pair on a drop
    /// (<see cref="DropsStaleClick"/>).
    /// </summary>
    public bool ClickIsStale(int x, int row, long pressedAt = 0)
    {
        if (!GuardsStaleClicks || !Enabled)
        {
            return false;
        }

        lock (_gate)
        {
            long now = _time.GetTimestamp();
            long pressed = pressedAt > 0 && pressedAt < now ? pressedAt : now;
            if (_aimChangedAt is not { } changed || _time.GetElapsedTime(changed, pressed) > ClickGrace
                || (_clickDroppedAt is { } dropped && _time.GetElapsedTime(dropped, now) <= DroppedClickPass))
            {
                return false;
            }

            if (AimedAtLocked(pressed) is { Aim: { } before } aimed && _aimShown is { } shown && string.Equals(before.Page, shown.Page, StringComparison.Ordinal)
                && shown.At(row, x) is { } here && before.At(row + _aimTop - aimed.Top, x) is { } there
                && string.Equals(here.Text, there.Text, StringComparison.Ordinal))
            {
                return false;   // the same page, and this spot shows what it showed
            }

            _clickDroppedAt = now;
            return true;
        }
    }

    /// <summary>
    /// <see cref="ClickIsStale"/> for <paramref name="click"/> on overlay row <paramref name="row"/>, judged from its press, and on a drop
    /// <paramref name="pair"/> reset, so the click after it starts a new pair (2026-11-07, the second code review: every click site wrote both out).
    /// </summary>
    public bool DropsStaleClick(InputEvent.Click click, int row, DoubleClick pair)
    {
        ArgumentNullException.ThrowIfNull(click);
        ArgumentNullException.ThrowIfNull(pair);
        if (!ClickIsStale(click.X, row, click.At))
        {
            return false;
        }

        pair.Reset();
        return true;
    }

    /// <summary>The window row of the overlay's first row as last drawn, the pane being at the window's foot. Never asks the terminal, so a hover can read it.</summary>
    internal int OverlayTopRow => Height - 1 - LastRowBelowCursor - CursorDepth;

    /// <summary>
    /// Told at the end of every draw: when what the overlay shows or where it stands differs from the last draw's, the moment is noted and the
    /// layout joins the remembered ones (<see cref="AimHistoryLength"/>), what a click still in the queue may have been aimed at.
    /// </summary>
    private void NoteAimDrawnLocked()
    {
        var aim = _overlay?.Aim;
        int top = _overlay is null ? -1 : OverlayTopRow;
        if (top == _aimTop && OverlayAim.SameShown(aim, _aimShown))
        {
            _aimShown = aim;
            return;
        }

        long now = _time.GetTimestamp();
        if (_aimHistory.Count == AimHistoryLength)
        {
            _aimHistory.RemoveAt(0);
        }

        _aimHistory.Add((aim, top, now));
        _aimShown = aim;
        _aimTop = top;
        _aimChangedAt = now;
    }

    /// <summary>The layout on show <see cref="ClickGrace"/> before <paramref name="pressed"/>, what a click pressed then was aimed at; null when none remembered reaches back that far. Under the gate.</summary>
    private (OverlayAim? Aim, int Top)? AimedAtLocked(long pressed)
    {
        for (int i = _aimHistory.Count - 1; i >= 0; i--)
        {
            var (aim, top, from) = _aimHistory[i];
            if (from <= pressed && _time.GetElapsedTime(from, pressed) >= ClickGrace)
            {
                return (aim, top);
            }
        }

        return null;
    }

    /// <summary>A new draw by a pane (a key, a click, a refresh): the slab hides until the pointer next moves, so it never competes with the keys' highlight or sits on a row that moved. Under the gate.</summary>
    private void HideHoverLocked()
    {
        _hoverTarget = null;
        _hoverQuietMoves = Volatile.Read(ref _pointerMoves);
    }

    /// <summary>
    /// The target under the pointer worked out on the tick, as <see cref="LinkHintLocked"/> does for links, never from the pointer's
    /// callback, and painted when it changed. Never asks the terminal where the cursor is. A pointer leaving the window sends nothing, so the slab
    /// can stay until focus goes, and focus coming back doesn't bring it back before the pointer next moves. Under the gate.
    /// <para>Looked up again only when the pointer moved, the aim changed or a draw moved the overlay's top row (2026-11-07, the code review): an
    /// idle tick reads no window top and scans no targets. A console scrolled with no move and no draw keeps the slab where it was until the next
    /// move; clicks don't depend on it.</para>
    /// </summary>
    private void TrackHoverLocked()
    {
        int moves = Volatile.Read(ref _pointerMoves);
        if (Volatile.Read(ref _focusLost) == 1)
        {
            _hoverQuietMoves = moves;
        }

        OverlayTarget? wanted = null;
        if (_hoverAsked && _overlay?.Aim is { } aim && moves != _hoverQuietMoves && _drawnOverlay && !ResizePending)
        {
            if (_hoverLookedUp is { } seen && seen.Moves == moves && ReferenceEquals(seen.Aim, aim) && seen.Top == _aimTop)
            {
                wanted = seen.Found;
            }
            else if (_geometry?.WindowTop() is int windowTop)
            {
                long at = Volatile.Read(ref _pointerAt);
                wanted = aim.At((int)(uint)at - windowTop - OverlayTopRow, (int)(at >> 32)) is { Cursor: false } target ? target : null;
                _hoverLookedUp = (moves, aim, _aimTop, wanted);
            }
        }

        if (wanted == _hoverTarget)
        {
            return;
        }

        var was = _hoverTarget;
        _hoverTarget = wanted;
        RepaintHoverRows(was, wanted);
    }

    /// <summary>
    /// The rows the slab left and came to, again in place, nothing else: a whole pane for a pointer's move would flicker where the terminal has no
    /// synchronized output. The rest of a row is erased only when the row leaves some: an erase on a full row, its cursor waiting at the
    /// edge, wipes the last cell (seen in Windows Terminal: every cut row lost its ellipsis). The rows are the last draw's (2026-11-07, the code
    /// review: rendering the whole overlay again for each row crossed), so a repaint matches what's on the screen. Under the gate.
    /// </summary>
    private void RepaintHoverRows(OverlayTarget? was, OverlayTarget? now)
    {
        if (_overlay is not { } overlay || !_drawn || _batch > 0 || _modal > 0)
        {
            return;
        }

        int w = Width;
        var lines = _overlayDrawnLines is { } drawn && ReferenceEquals(drawn.Overlay, overlay) && drawn.Width == w
            ? drawn.Lines
            : Segment.SplitLines(overlay.Content.GetSegments(_inner), w);
        if (was is { } left && left.Row != now?.Row)
        {
            Repaint(left.Row, null);
        }

        if (now is { } came)
        {
            Repaint(came.Row, came);
        }

        void Repaint(int row, OverlayTarget? slab)
        {
            if (row >= _overlayRows || row >= lines.Count)
            {
                return;
            }

            var line = slab is { } s ? OverlayAim.Slab(lines[row], s.First, Math.Min(s.Last, w - 2), Theme.MenuHover) : lines[row];
            RedrawRow(row - CursorDepth, () =>
            {
                _inner.Write(new SegmentList(line));
                EraseRowRest(line, w);
            });
        }
    }
}
