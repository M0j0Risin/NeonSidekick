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

    // What the last draw showed and where, what was on show before the last change, and when that was. Under the gate.
    private OverlayAim? _aimShown;
    private int _aimTop;
    private OverlayAim? _aimBefore;
    private int _aimBeforeTop;
    private long _aimChangedAt;
    private bool _aimChanged;
    private long _clickDroppedAt;
    private bool _clickDropped;

    // The target with the slab, and the pointer's move count it's hidden until. Under the gate.
    private OverlayTarget? _hoverTarget;
    private int _hoverQuietMoves;

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
    /// within <see cref="ClickGrace"/>, and that spot on the old layout didn't show what it shows now, so the click was aimed at something
    /// else. A queued click is read right after the draw, so it falls inside the grace, and no press time is needed. Costs one click at
    /// most: for <see cref="DroppedClickPass"/> after a drop, every click counts. Only for clicks on list rows: the ×, the strip and the
    /// ▲▼ row work at any speed. The caller resets its double-click pair on a drop.
    /// </summary>
    public bool ClickIsStale(int x, int row)
    {
        if (!GuardsStaleClicks || !Enabled)
        {
            return false;
        }

        lock (_gate)
        {
            long now = _time.GetTimestamp();
            if (!_aimChanged || _time.GetElapsedTime(_aimChangedAt, now) > ClickGrace
                || (_clickDropped && _time.GetElapsedTime(_clickDroppedAt, now) <= DroppedClickPass))
            {
                return false;
            }

            if (_aimBefore is { } before && _aimShown is { } shown && string.Equals(before.Page, shown.Page, StringComparison.Ordinal)
                && shown.At(row, x) is { } here && before.At(row + _aimTop - _aimBeforeTop, x) is { } there
                && string.Equals(here.Text, there.Text, StringComparison.Ordinal))
            {
                return false;   // the same page, and this spot shows what it showed
            }

            _clickDropped = true;
            _clickDroppedAt = now;
            return true;
        }
    }

    /// <summary>The window row of the overlay's first row as last drawn, the pane being at the window's foot. Never asks the terminal, so a hover can read it.</summary>
    internal int OverlayTopRow => Height - 1 - LastRowBelowCursor - CursorDepth;

    /// <summary>
    /// Told at the end of every draw: when what the overlay shows or where it stands differs from the last draw's, the moment is noted. A
    /// second change within the grace keeps the first layout as the old one, what a click still in the queue was aimed at.
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
        if (!_aimChanged || _time.GetElapsedTime(_aimChangedAt, now) > ClickGrace)
        {
            _aimBefore = _aimShown;
            _aimBeforeTop = _aimTop;
        }

        _aimShown = aim;
        _aimTop = top;
        _aimChangedAt = now;
        _aimChanged = true;
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
    /// </summary>
    private bool HoverTargetChangedLocked()
    {
        int moves = Volatile.Read(ref _pointerMoves);
        if (Volatile.Read(ref _focusLost) == 1)
        {
            _hoverQuietMoves = moves;
        }

        OverlayTarget? wanted = null;
        if (_hoverAsked && _overlay?.Aim is { } aim && moves != _hoverQuietMoves && _drawnOverlay && !ResizePending && _geometry?.WindowTop() is int windowTop)
        {
            long at = Volatile.Read(ref _pointerAt);
            wanted = aim.At((int)(uint)at - windowTop - OverlayTopRow, (int)(at >> 32)) is { Cursor: false } target ? target : null;
        }

        if (wanted == _hoverTarget)
        {
            return false;
        }

        var was = _hoverTarget;
        _hoverTarget = wanted;
        RepaintHoverRows(was, wanted);
        return true;
    }

    /// <summary>
    /// The rows the slab left and came to, again in place, nothing else: a whole pane for a pointer's move would flicker where the terminal has no
    /// synchronized output. The rest of a row is erased only when the row leaves some: an erase on a full row, its cursor waiting at the
    /// edge, wipes the last cell (seen in Windows Terminal: every cut row lost its ellipsis). Under the gate.
    /// </summary>
    private void RepaintHoverRows(OverlayTarget? was, OverlayTarget? now)
    {
        if (_overlay is not { } overlay || !_drawn || _batch > 0 || _modal > 0)
        {
            return;
        }

        int w = Width;
        var lines = Segment.SplitLines(overlay.Content.GetSegments(_inner), w);
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

            var line = slab is { } s ? OverlayAim.Slab(lines[row], s.First, Math.Min(s.Last, w - 2), Theme.MenuHover.Background) : lines[row];
            RedrawRow(row - CursorDepth, () =>
            {
                _inner.Write(new SegmentList(line));
                if (FittedMarkup.RowCells(line) < w)
                {
                    _inner.Write(EraseLineEnd);
                }
            });
        }
    }
}
