using NeonSidekick.Companion;
using NeonSidekick.Diagnostics;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.UI;

/// <summary>
/// The welcome splash's robot (2026-10-20, the user's ask: <c>Welcome splash</c> <c>robot</c>, the AnimationTrials proof of concept
/// moved in). The screen hands the pane the character (<see cref="ShowCompanion"/>), and the pane stands it in its padding — the
/// blank rows between the transcript's end and the pane while the transcript is short — on the pane's top row, centered: the whole
/// character when the padding holds it, just its head when it doesn't, nothing when not even that fits (<see cref="CompanionFit"/>).
/// Every <see cref="Draw"/> places it afresh and writes its rows as part of the pane's own frame, so a resize, an overlay or the
/// startup's lines refit it, and no redraw ever blanks it. It is never in the store: scrolling back and the Web UI's feed see blank
/// rows. Until later that day (the user's call, round 2) it stood over a block of blank rows written under the banner, sized once.
///
/// <para>Between the pane's draws a timer of its own at <see cref="CompanionFrame"/> — the 100 ms <see cref="Tick"/> is too slow for
/// the 45 ms head step and the 60 ms boot flicker — hands it the pointer, focus and caret that came in (the pointer's rows counted
/// from the window's top, the caret's from the pane's own counts: no cursor query), advances it, and when a pixel changed repaints
/// just its rows where the last draw placed it, with relative moves from the cursor and the cursor put back, in one synchronized
/// frame. Nothing is painted while scrolled, lifted, in a batch or a modal, or between a resize and the tick's redraw; the character
/// keeps its clock all the same.</para>
///
/// <para>It goes at the first sent line (<see cref="HideCompanion"/>; a goodbye played until round 2), with <see cref="Clear"/> and
/// with the pane. Every way it ends calls <see cref="Hover"/> with false; showing one calls it with true.</para>
/// </summary>
public sealed partial class ScreenPane
{
    /// <summary>The companion's frame interval: 30 frames a second.</summary>
    public static readonly TimeSpan CompanionFrame = TimeSpan.FromMilliseconds(33);

    /// <summary>
    /// The narrowest window the companion is placed in: its canvas and a column, so its last cell never lands in the window's
    /// last column (a write there leaves the terminal's wrap pending).
    /// </summary>
    public const int MinCompanionWidth = 25;

    private const string CompanionCategory = "Splash";

    private ICompanion? _companion;
    private ITimer? _companionTimer;
    private (string Text, int Cursor) _companionCaret;

    // Where the last draw stood the companion (its top screen row, first column and fit), null when it placed none; and the fit
    // last logged ("whole", "head" or "none"), so a change is logged once.
    private (int Top, int Column, CompanionFit Fit)? _companionAt;
    private string? _companionFitLogged;

    // Written by the input reader's thread, read by the frame: the pointer packed (column high, row low) with a count of moves, and a
    // focus change (0 none, 1 in, 2 out).
    private long _pointerAt;
    private int _pointerMoves;
    private int _pointerSeen;
    private int _focusChange;

    /// <summary>
    /// Asks the input reader for the mouse's moves with no button held (2026-10-20, <see cref="IMouseInput.Hover"/>): true while a
    /// companion stands, false once it is gone. Null — the tests, a reader without hover — asks nothing.
    /// </summary>
    public Action<bool>? Hover { get; set; }

    /// <summary>Whether a companion is on the pane.</summary>
    public bool HasCompanion
    {
        get
        {
            lock (_gate)
            {
                return _companion is not null;
            }
        }
    }

    /// <summary>
    /// <paramref name="companion"/> stood in the pane's padding from now on until it is hidden (see the class's summary), the pane
    /// drawn again at once while it is on the screen (else the next draw places it). A companion already shown is replaced.
    /// Nothing when the pane is disabled.
    /// </summary>
    public void ShowCompanion(ICompanion companion)
    {
        ArgumentNullException.ThrowIfNull(companion);
        if (!Enabled)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _companionTimer?.Dispose();
            _companion = companion;
            _companionAt = null;
            _companionFitLogged = null;
            _companionCaret = (_text, _cursor);
            _pointerSeen = Volatile.Read(ref _pointerMoves);
            Interlocked.Exchange(ref _focusChange, 0);
            _companionTimer = _time.CreateTimer(_ => OnCompanionFrame(), null, CompanionFrame, CompanionFrame);
            DiagnosticLog.Debug(CompanionCategory, "Robot shown.");
            if (_drawn)
            {
                Redraw();
            }
        }

        Hover?.Invoke(true);
    }

    /// <summary>
    /// The companion gone at once (the first sent line): its frames stop and it is forgotten. It blanks nothing itself — the caller
    /// draws next (a sent line's commit draws the pane, whose padding then has no companion to place).
    /// </summary>
    public void HideCompanion()
    {
        bool had;
        lock (_gate)
        {
            had = _companion is not null;
            StopCompanionLocked();
        }

        if (had)
        {
            Hover?.Invoke(false);
        }
    }

    /// <summary>The mouse is over screen buffer cell (<paramref name="x"/>, <paramref name="y"/>) with no button held: the next frame hands it to the companion. Any thread; never blocks.</summary>
    public void PointerMoved(int x, int y)
    {
        Volatile.Write(ref _pointerAt, ((long)x << 32) | (uint)y);
        Interlocked.Increment(ref _pointerMoves);
    }

    /// <summary>The window gained or lost focus: the next frame hands it to the companion. Any thread; never blocks.</summary>
    public void FocusChanged(bool focused) => Interlocked.Exchange(ref _focusChange, focused ? 1 : 2);

    /// <summary>Stops the frames and forgets the companion and where it stood. Under the gate.</summary>
    private void StopCompanionLocked()
    {
        _companionTimer?.Dispose();
        _companionTimer = null;
        _companion = null;
        _companionAt = null;
        _companionFitLogged = null;
    }

    /// <summary>
    /// The draw's part (2026-10-20, round 2): the companion placed in <paramref name="pad"/> blank rows that start at screen row
    /// <paramref name="padTop"/> — its last rows the pad's last, centered across <paramref name="width"/> — and its rows laid out for
    /// <see cref="WriteCompanionRow"/>. Returns the pad row its first row is written on (<paramref name="pad"/> when none is placed).
    /// Under the gate, from <see cref="Draw"/>'s bottom branch.
    /// </summary>
    private int PlaceCompanion(int padTop, int pad, int width)
    {
        _companionAt = null;
        if (_companion is not { } companion)
        {
            return pad;
        }

        CompanionFit? fit = width >= MinCompanionWidth ? CompanionFit.For(companion.Canvas, width - 1, pad) : null;
        string kind = fit is not { } f ? "none" : f.IsHeadOnly ? "head" : "whole";
        if (!string.Equals(kind, _companionFitLogged, StringComparison.Ordinal))
        {
            _companionFitLogged = kind;
            DiagnosticLog.Debug(CompanionCategory, "Robot fits: " + kind);
        }

        if (fit is not { } placed)
        {
            return pad;
        }

        companion.HeadOnly = placed.IsHeadOnly;
        int first = pad - placed.Rows;
        _companionAt = (padTop + first, Math.Max(0, (width - placed.Columns) / 2), placed);
        _companionRows = CompanionRows(companion, placed);
        return first;
    }

    // The placed companion's rows as the last draw laid them out, for its pad rows.
    private List<SegmentLine> _companionRows = [];

    /// <summary>The companion's frame as rows of segments, cut to <paramref name="fit"/>.</summary>
    private List<SegmentLine> CompanionRows(ICompanion companion, CompanionFit fit) =>
        Segment.SplitLines(new RobotView(companion.Frame, fit.PixelRows).GetSegments(_inner), fit.Columns);

    /// <summary>Row <paramref name="index"/> of the placed companion where the draw's cursor stands (a pad row's start): its column's blanks, then its cells.</summary>
    private void WriteCompanionRow(int index)
    {
        if (_companionAt is not { } at || index < 0 || index >= _companionRows.Count)
        {
            return;
        }

        if (at.Column > 0)
        {
            _inner.Write(new RawText(new string(' ', at.Column)));
        }

        _inner.Write(new SegmentList(_companionRows[index]));
    }

    private void OnCompanionFrame()
    {
        bool ended = false;
        lock (_gate)
        {
            if (_companion is not { } companion || _disposed)
            {
                return;
            }

            try
            {
                CompanionFrameLocked(companion);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                DiagnosticLog.Warn(CompanionCategory, "The robot stopped: " + ex.Message);
                StopCompanionLocked();
                ended = true;
            }
        }

        if (ended)
        {
            Hover?.Invoke(false);
        }
    }

    /// <summary>One frame (see the class's summary). Under the gate.</summary>
    private void CompanionFrameLocked(ICompanion companion)
    {
        bool current = Width == _lastWidth && Height == _lastHeight;
        int cursorRow = Height - 1 - LastRowBelowCursor;
        if (_companionAt is { } at && current)
        {
            companion.SetOrigin(at.Column, at.Top);
            FeedCompanion(companion, cursorRow);
        }

        bool changed = companion.Update();
        if (changed && current && _companionAt is { } placed && CanPaintCompanion())
        {
            PaintCompanion(companion, placed, cursorRow);
        }
    }

    /// <summary>Whether the pane is on the screen as drawn at the bottom, so the companion's rows are where the last draw put them.</summary>
    private bool CanPaintCompanion() =>
        _drawn && !_drawnScrolled && _top < 0 && !_blank && _batch == 0 && _modal == 0;

    /// <summary>The pointer, focus and caret that came in since the last frame, handed to the companion.</summary>
    private void FeedCompanion(ICompanion companion, int cursorRow)
    {
        int moves = Volatile.Read(ref _pointerMoves);
        if (moves != _pointerSeen)
        {
            _pointerSeen = moves;
            long at = Volatile.Read(ref _pointerAt);
            int top = _geometry?.WindowTop() ?? 0;
            companion.OnPointer((int)(at >> 32), (int)(uint)at - top);
        }

        switch (Interlocked.Exchange(ref _focusChange, 0))
        {
            case 1:
                companion.OnFocus(true);
                break;
            case 2:
                companion.OnFocus(false);
                break;
        }

        var caret = (_text, _cursor);
        if (caret != _companionCaret)
        {
            _companionCaret = caret;
            if (!_drawnOverlay || _drawnInput)
            {
                int start = TextCells.Width(InputLine.PromptGlyph);
                companion.OnCaret(start + _cursorCell, cursorRow, start, Width);
            }
        }
    }

    /// <summary>The companion's rows painted again where the last draw placed it, the cursor back on the caret, in one frame.</summary>
    private void PaintCompanion(ICompanion companion, (int Top, int Column, CompanionFit Fit) at, int cursorRow)
    {
        _companionRows = CompanionRows(companion, at.Fit);
        BeginSync();
        try
        {
            _inner.Cursor.Show(false);
            for (int i = 0; i < _companionRows.Count && i < at.Fit.Rows; i++)
            {
                int up = cursorRow - (at.Top + i);
                if (up <= 0)
                {
                    continue;
                }

                _inner.Cursor.Move(CursorDirection.Up, up);
                ColumnZero();
                if (at.Column > 0)
                {
                    _inner.Cursor.Move(CursorDirection.Right, at.Column);
                }

                _inner.Write(new SegmentList(_companionRows[i]));
                _inner.Cursor.Move(CursorDirection.Down, up);
                ColumnZero();
            }

            if (!_drawnOverlay || _drawnInput)
            {
                _inner.Cursor.Move(CursorDirection.Right, TextCells.Width(InputLine.PromptGlyph) + _cursorCell);
                _inner.Cursor.Show(true);
            }
        }
        finally
        {
            EndSync();
        }
    }
}
