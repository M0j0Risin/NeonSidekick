using NeonSidekick.Diagnostics;
using NeonSidekick.UI.Markdown;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.UI;

/// <summary>
/// The screen's bottom rows: a rule, the input rows, a second rule and a hint line, kept on the last
/// rows of the window while the transcript flows above them — the Claude Code shape. An <see cref="IAnsiConsole"/>
/// the chat screen writes through: every write is a <em>flow</em> write (the transcript), and the
/// pane lifts itself out of the way first, writes, and redraws itself below.
///
/// <para>The screen runs in the terminal's <em>alternate buffer</em> (<see cref="Open"/> enters it,
/// <see cref="Close"/> leaves it: the shell's screen and scrollback come back untouched). Spectre
/// has no layout manager, no fixed region and no resize event, so the drawing is done the way Ink
/// does it: the transcript is ordinary scrolling output; the pane is drawn under it with
/// <em>padding rows</em> in between while the transcript is shorter than the window; before a flow
/// write the cursor moves back up over the pane and the padding and erases to the end of the
/// screen; after it the pane is drawn again. Once the transcript reaches the pane the padding is
/// zero and the buffer scrolls at its last row exactly as the main one did. Every compound step is
/// wrapped in synchronized output (DEC 2026, honoured by Windows Terminal, ignored elsewhere) with
/// the cursor hidden, so a redraw per streamed token does not flicker.</para>
///
/// <para>The alternate buffer keeps no scrollback, so the pane keeps its own: every flow write is
/// retained in a <see cref="Scrollback"/> (the one place is <c>Track</c>), and <see cref="ScrollPage"/>
/// (PgUp/PgDn on the input line, and the key watcher during a turn) paints a window of it in the
/// transcript region while the pane stays on the last rows — <see cref="Scrolled"/>, the rows-below
/// count on a row of its own over the upper rule (<see cref="ScrolledRow"/>; on the hint row until 2026-10-01), the anchor a store row so rows arriving below never
/// move what is read. Flow writes meanwhile go to the store alone. Paging back to the bottom,
/// Ctrl+End (<see cref="ScrollToEnd"/>, 2026-09-17), a resize (the buffer is not reflowed by the
/// terminal) and a sent line write the store's tail back as the flow and draw the pane as before.
/// The wheel scrolls it too, <see cref="WheelRows"/> a notch (<see cref="ScrollWheel"/>).</para>
///
/// <para>Where the transcript ends is <em>counted</em> from the segments Spectre emits (line breaks
/// and cell widths with the terminal's deferred-wrap rule: a row is left only when the next character
/// does not fit — the store wraps by the same rule) and <em>corrected</em> from <see cref="ScreenGeometry"/>
/// on every redraw at the bottom when the console can say where its cursor is. Relative cursor moves
/// only: padding rows are empty and no input row ever fills the width.</para>
///
/// <para>The input area holds the whole draft, word-wrapped (<see cref="InputLayout"/>) over as many
/// rows as it needs up to <see cref="MaxInputRows"/>, the pane growing upward (and beyond the cap a
/// vertical viewport keeps the cursor's row on the screen); a key that keeps the row count rewrites
/// the rows in place, one that changes it lifts and draws the pane again. The pane remembers the
/// draft and lays it out afresh on every draw, so a resize re-wraps it.</para>
///
/// <para>An <em>overlay</em> (<see cref="ShowOverlay"/>, the info pane, the menus) takes the input rows' place:
/// the rules stay above and below it (the upper one keeps its title), its own hint under the lower rule, the pane grows upward to
/// fit and the cursor is hidden until <see cref="CloseOverlay"/>. The same lift and draw, with more rows.
/// An overlay <em>with an input slot</em> (a typed settings edit) keeps the input rows under its
/// content, the cursor on them, so the input line works there unchanged; what it submits stays
/// out of the flow.</para>
///
/// <para>Disabled (no geometry, or a console without menus) the pane is a pass-through: every
/// call degenerates to the plain write the screen made before the pane existed, which is what
/// the tests over <c>TestConsole</c> see unless they build a geometry. One lock serialises the
/// screen thread (flow writes, the input line), the key watcher's preview and the pane's own
/// tick (spinner frames, a size change, a changed hint).</para>
/// </summary>
public sealed class ScreenPane : IAnsiConsole, IDisposable
{
    /// <summary>Rule (the session's name at its right edge, <see cref="RuleTitle"/>), one input row, rule, hint: the pane at its smallest (a longer draft or an overlay adds rows).</summary>
    public const int PaneRows = 4;
    public const char RuleGlyph = '─';

    /// <summary>
    /// The close glyph at the right edge of an overlay's first row (2026-09-18): the ESC key under
    /// the mouse — a menu backs out one level, an info pane closes. Drawn for an overlay shown with
    /// <c>close</c> (until 2026-09-21 only while the <c>Mouse in menus</c> setting let the pane keep
    /// the mouse; the pane always does now), in column <c>Width − 2</c> — the last column left
    /// empty as the hint row leaves it. Pinned.
    /// </summary>
    public const string CloseGlyph = "×";

    /// <summary>
    /// The upper rule's fold button (2026-09-29, the user's glyph, in place of the ↘️ ↖️ pair of 2026-09-28): one click is
    /// Ctrl+O — everything unfolded when anything is folded, else everything folded (<see cref="ToggleToolGroups"/>). One
    /// cell, a plain arrow with no emoji selector, so it takes a space either side like the rule's title.
    /// </summary>
    public const string FoldGlyph = "⤡";
    public const string Category = "Screen";

    /// <summary>The tick that advances the spinner and polls the window size.</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);

    private static readonly ControlCode SyncBegin = new("\e[?2026h");
    private static readonly ControlCode SyncEnd = new("\e[?2026l");
    private static readonly ControlCode EraseDown = new("\e[J");
    private static readonly ControlCode EraseLineEnd = new("\e[K");
    // The alternate screen buffer (the shell's screen comes back on leaving it), with Windows
    // Terminal's alternate scroll mode off while it is up: on, a wheel notch there arrives as ↑/↓
    // key records — history on the line — and the wheel is the terminal's anyway.
    private static readonly ControlCode EnterAlternate = new("\e[?1049h\e[?1007l");
    private static readonly ControlCode LeaveAlternate = new("\e[?1007h\e[?1049l");
    // The terminal profile's default background back (OSC 111), written before the buffer is left when PageBackground set one.
    private static readonly ControlCode PageBackgroundReset = new(PageBackgroundResetSequence);

    private readonly IAnsiConsole _inner;
    private readonly ScreenGeometry? _geometry;
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly ITimer? _timer;
    private readonly Scrollback _store = new();

    // The flow cursor: where the transcript's next character goes (screen rows, 0 = top).
    private int _row;
    private int _col;
    private bool _lineFull;
    private bool _drawn;
    private int _pad;
    private int _lastWidth;
    private int _lastHeight;
    private bool _inAlternate;

    // The scroll: the store row at the top of the transcript region while the user has paged up,
    // −1 at the bottom (the live state, the flow on the screen). _drawnScrolled is the shape the
    // last Draw put on the screen (Lift steps up from it, like the drawn overlay). _blank: the
    // flow is not on the screen — a scrolled draw or a resize erased it — and the next draw at
    // the bottom writes the store's tail back first (RestoreFlow). _liveCount: the live block's
    // rows not yet committed, for the rows-below count while scrolled.
    private int _top = -1;
    private bool _drawnScrolled;
    private bool _blank = true;
    private int _liveCount;

    // The live slot: the reply in progress (a ReplyBlock), laid out afresh on every draw between the
    // flow and the padding, its rows lifted with the pane's; repainted on the tick when dirty, never
    // per token. A block taller than the screen leaves over the pane commits its top rows into the
    // flow (written and tracked like any flow write) and _liveCommitted skips that many lines of the
    // next layout — the content is the whole document every time, the pane shows the tail. A code block
    // still streaming is the exception (2026-09-27): its label and last Code collapse count rows only,
    // none of it committed until its fence closes (ReplyLayout), as a thinking block streams.
    private IRenderable? _live;
    private int _liveRows;
    private int _liveCommitted;
    private bool _liveDirty;
    private int _batch;
    private int _modal;
    private bool _disposed;

    // The input area (enabled): the draft, and the rows drawn from it by the last draw. The drawn
    // fields (_shownRows, _inputRows, _cursorRow, _cursorCell) say where the terminal's cursor is
    // and are set only by Draw and the in-place rewrite; a lift steps up from them.
    private string _text = "";
    private int _cursor;
    private int _anchor = -1;
    private IReadOnlyList<(int Start, int Length)> _labels = Array.Empty<(int, int)>();
    private int _firstRow;
    private List<string> _shownRows = new() { "" };
    private List<int> _shownStarts = new() { 0 };
    private List<int> _shownNext = new() { 0 };
    private int _inputRows = 1;
    private int _cursorRow;
    private int _cursorCell;
    // The cells the last draw put on the first row for the placeholder (0 = none drawn): the
    // in-place rewrite blanks them like a draft's, so the first key takes the ghost text away.
    private int _shownGhostCells;
    private string _placeholder = "";

    // The single row of the disabled pane (drawn where the cursor is): the cells it used.
    private int _renderedCells;

    // The upper rule's title (the session's name, 2026-09-18) and the one last drawn, for the tick.
    private Func<string> _ruleTitle = static () => "";
    private string _drawnRuleTitle = "";

    // The upper rule's ⤡ (2026-09-28 as ↘️↖️, one glyph since 2026-09-29): whether the last draw put it there, and the column
    // it landed at (-1 = none drawn), for TryHitFoldButton and the tick.
    private bool _drawnFolds;
    private int _foldColumn = -1;

    // The upper rule's 🎞️ (2026-10-03, StripReopen): the label the last draw put there (null = none), and the column it
    // landed at (-1 = none drawn), for TryHitStripReopen and the tick.
    private Func<string?> _stripReopen = static () => null;
    private string? _drawnReopen;
    private int _reopenColumn = -1;

    // The upper rule's title (2026-09-28): the column it landed at (-1 = none drawn) and its cells, for TryHitRuleTitle.
    private int _ruleTitleColumn = -1;
    private int _ruleTitleCells;

    // The hint row.
    private Func<string> _hint = static () => "";
    private Func<string> _strip = static () => "";
    private Func<string> _trailer = static () => "";
    private Func<string> _trailerMark = static () => "";
    private string? _busyLabel;
    // The labels of the busy scopes still open, outermost first (a menu's spinner inside the turn's):
    // the last one is _busyLabel; a scope's end shows the one under it again. The count is the outermost's.
    private readonly List<string> _busyLabels = new();
    private long _busySince;
    private int _frame;
    private string _shownHint = "";
    // The row's whole text while a picture is dragged toward the chat line (2026-09-28, SetDragHint); null for none.
    private string? _dragHint;
    // The row's whole text while an alert stands (2026-10-01, SetAlertHint), and the UTC tick it lapses at; null for none.
    private string? _alertHint;

    // The alert is a flash (2026-10-04, the copy's "copied 142 characters"): drawn in the hint's style, not the warning's.
    private bool _alertFlash;

    // When the top busy label last changed (2026-10-04): the step's own clock beside the turn's.
    private long _stageSince;

    private Func<bool> _stopHintShown = () => false;

    private Func<bool> _stepClockShown = () => false;

    private Func<string> _userLineStyle = () => UI.UserLineStyle.Default;
    private long _alertUntil;
    private Func<string> _queued = () => "";
    private Func<string> _usage = () => "";
    private Func<string> _busyUsage = () => "";
    private Func<string, bool> _labelAfterUsage = _ => false;
    private Func<string, bool> _labelZone = _ => false;

    // The overlay (the info pane, the menus): drawn where the input row is, the cursor hidden
    // meanwhile — or, with an input slot, above the input rows, the cursor on them.
    private Overlay? _overlay;
    private int _paneRows = PaneRows;
    private int _overlayRows;

    // The overlay the last draw put on the screen (Draw is the only writer): the cursor's depth
    // is a fact about what is drawn, and a shape change lifts from the OLD shape before it draws
    // the new one — ShowOverlay and CloseOverlay set _overlay before the lift [scar 2026-09-13].
    private bool _drawnOverlay;
    private bool _drawnInput;

    // Whether the drawn overlay was shown with close (a pane — never the line's completion list):
    // TryHitOutside answers only then, so a double-click on the transcript never closes the list.
    private bool _drawnClose;

    // The column the last draw put the overlay's close glyph in; −1 when none was drawn.
    private int _closeColumn = -1;

    // The hint row as last drawn (TryHitHint's zones): the strip at column 0 — the busy row's too
    // since 2026-09-24, when drawn whole (the user's ask: the brain's double-click cancels a
    // reflection holding up the reply) — and the column the trailer starts in, −1 without one or
    // under the busy row.
    private string _hintStrip = "";
    private int _trailerColumn = -1;
    private int _markColumn = -1;

    // The queued part (Queued) as last drawn, in either row: its first column and its width in
    // cells, −1 / 0 when none was drawn (nothing queued, cut by a narrow window, or under an
    // overlay's hint — the scroll's too until 2026-10-01). TryHitQueued reads them; TryHitHint the standing row's alone.
    private int _queuedColumn = -1;
    private int _queuedCells;

    // The usage zone (HintZone.Usage, 2026-09-21) as last drawn, in either row: the token tally
    // (Usage) on the standing row, the spinner and its label on the busy row — its first column
    // and its width in cells, −1 / 0 when none was drawn (nothing counted, the timers or the exit
    // hint in the tally's place, cut by a narrow window, or under an overlay's hint — the scroll's too until 2026-10-01).
    private int _usageColumn = -1;
    private int _usageCells;

    // The busy row's label when LabelAfterUsage or LabelZone takes it (2026-09-28, HintZone.Label): −1 for none.
    private int _labelColumn = -1;
    private int _labelCells;

    // The scroll's row (2026-10-01, the user's ask: the scroll's hint had taken the whole hint row, the strip, the tally
    // and the model gone for as long as the user read back): while scrolled, ScrolledRow centered on a row of its own
    // over the upper rule — over the picture strip's rule when the strip is up, the pane's top row either way. The rows
    // the last draw gave it (0 or 1, Draw the only writer, as _stripRows) and its text as drawn (null = none; the tick's
    // comparison). The hint row stays the screen's own meanwhile.
    private int _scrollRows;
    private string? _shownScroll;

    // The toolbar (2026-09-21): the provider, the rows the last draw gave it (0 or 1 — Draw is the
    // only writer, like _drawnOverlay), the row's text as drawn (null = no row; the tick's
    // comparison), and its zones: the strip at column 0 as the row cut it, and the path's first
    // column and width, −1 / 0 when the row had no room for it.
    private Func<ToolbarParts?> _toolbar = static () => null;
    private Func<Color?> _pageBackground = static () => null;

    // The page background last written (OSC 11) while in the alternate buffer; null when the profile's own is showing.
    private Color? _pageBackgroundSent;
    private int _toolbarRows;
    private string? _shownToolbar;
    private string _shownToolbarOff = "";
    private string _toolbarStrip = "";
    private int _toolbarPathColumn = -1;
    private int _toolbarPathCells;

    // The performance bar (2026-09-29): the provider (asked with the row's cells; null = no row), the rows the last draw
    // gave it (0 or 1, Draw the only writer) and the row's text as drawn (null = no row; the tick's comparison). It is the
    // pane's last row when drawn, under the toolbar.
    private Func<int, UI.PerfRow?> _perf = static _ => null;
    private int _perfRows;
    private string? _shownPerf;

    // The menus' height cap (2026-10-01): the saved word of Menus max height, read at every menu draw; full-screen (the
    // pane as it grew before the setting) until the screen sets it.
    private Func<string> _menuHeight = static () => "full-screen";

    // The picture strip (later still on 2026-09-24): the provider, the rows the last draw gave it (0 or
    // PictureStrip.Rows — Draw is the only writer), the strip's version and highlight as drawn (the tick's
    // comparison), and where its tiles landed (TryHitStrip).
    private Func<PictureStrip?> _pictureStrip = static () => null;
    private int _stripRows;
    private int _drawnStripVersion;
    private bool _drawnStripHighlight;
    private List<PictureSpan> _stripSpans = [];

    // The button on the strip's rule (2026-09-27, the picture viewer): its label's provider, and the columns the last
    // draw put the label at (-1 = none drawn), for TryHitStripButton; the close × at the rule's right (2026-09-28), for
    // TryHitStripClose.
    private Func<string?> _stripButton = static () => null;
    private int _stripButtonColumn = -1;
    private int _stripButtonCells;
    private int _stripCloseColumn = -1;

    private sealed record Overlay(IRenderable Content, string Hint, bool Input, bool Close);

    // Where the last dismissing double-click landed (Dismiss(x, y)), until TakeDismissHit.
    private OffPaneHit? _dismissHit;

    // The command chord that last dismissed a pane (Chord), until TakeDismissChord; a new overlay keeps it.
    private string? _dismissChord;

    /// <summary>The part of the standing hint row a click landed on (<see cref="TryHitHint(int, int, out HintHit)"/>).</summary>
    public enum HintZone
    {
        /// <summary>Anywhere that is neither a strip glyph nor the trailer: the hint's own text, a separator, a blank.</summary>
        Row,

        /// <summary>One of the speech strip's glyphs at the row's start (<see cref="HintHit.Glyph"/> says which).</summary>
        Strip,

        /// <summary>The model name at the right edge, and the separator ahead of its reasoning mark (the mark itself is <see cref="Mark"/> since 2026-09-21).</summary>
        Trailer,

        /// <summary>The queued-messages count after the strip (<see cref="Queued"/>, 2026-09-18).</summary>
        Queued,

        /// <summary>
        /// The scroll's hint (<see cref="ScrolledHint"/>, later on 2026-09-18): so a double-click there is the bottom
        /// again, as Ctrl+End is. Until 2026-10-01 it was what <see cref="Row"/> was while the transcript was scrolled (the
        /// hint row carried the scroll's hint); since, it is the whole of the scroll's own row over the upper rule
        /// (<see cref="ScrolledRow"/>), and the hint row keeps its zones while scrolled.
        /// </summary>
        Scrolled,

        /// <summary>
        /// The token tally on the standing row (<see cref="Usage"/>), or the spinner and its label
        /// on the busy row (2026-09-21, the user's ask): a double-click on either opens <c>/usage</c>.
        /// After <see cref="Scrolled"/> so <c>InputLine.HintPairKey</c>'s values stand.
        /// </summary>
        Usage,

        /// <summary>
        /// The reasoning mark on the row's last cells (<see cref="TrailerMark"/>; 2026-09-21, the
        /// user's ask): a double-click there opens <c>/reasoning</c> where the name opens <c>/model</c>.
        /// Last, for the same reason.
        /// </summary>
        Mark,

        /// <summary>
        /// The busy row's label and its time when <see cref="LabelAfterUsage"/> takes it — the ComfyUI generation's
        /// <c>🖼️ 00:12</c> / <c>🎨 00:12</c> (2026-09-28, the user's ask: a double-click there cancels the pictures) — ahead
        /// of <see cref="Usage"/>, which keeps the spinner and the tally. After <see cref="Mark"/>, for the same reason.
        /// A label <see cref="LabelZone"/> takes (2026-10-01, the embedded model's load: a double-click cancels it) is the
        /// spinner's frame, its blank and the label in their usual place, ahead of the tally, which stays <see cref="Usage"/>.
        /// </summary>
        Label,
    }

    /// <summary>Where on the hint row a click landed: the zone, the strip glyph under it (<c>""</c> elsewhere) and the zone's first column (−1 for the row).</summary>
    public readonly record struct HintHit(HintZone Zone, string Glyph, int Column);

    /// <summary>
    /// What <see cref="Toolbar"/> answers for the row under the hint row (2026-09-21, the user's
    /// ask): <paramref name="Strip"/> is the glyphs pinned at column 0, <paramref name="Path"/>
    /// the text pinned at the right edge — the working directory, cut from the front to what is
    /// left (<see cref="ToolbarRow"/>) — and a target of its own (<see cref="ToolbarZone.Path"/>):
    /// a folder glyph sat beside it as the button until later that day, when the user made the
    /// path the button. <paramref name="Off"/> (2026-10-03, the user's ask) names the strip's glyphs drawn on the off slab
    /// (<see cref="Theme.ToolbarOff"/>) by their place among its glyphs, from 0: a tool switch that is off. Null or empty, none.
    /// </summary>
    public readonly record struct ToolbarParts(string Strip, string Path, IReadOnlyList<int>? Off = null);

    /// <summary>The part of the toolbar a click landed on (<see cref="TryHitToolbar"/>).</summary>
    public enum ToolbarZone
    {
        /// <summary>
        /// Anywhere that is neither a strip glyph nor the path: a separator, a blank — or anywhere on the performance bar
        /// under the row (2026-09-29, the user's ask: the bar opens <c>/settings</c> as the blanks do).
        /// </summary>
        Row,

        /// <summary>One of the strip's glyphs at the row's start (<see cref="ToolbarHit.Glyph"/> says which).</summary>
        Glyph,

        /// <summary>The path at the right edge, as drawn — the screen opens the folder picker on a pair there.</summary>
        Path,
    }

    /// <summary>Where on the toolbar a click landed: the zone, the strip glyph under it (<c>""</c> elsewhere) and the zone's first column (−1 for the row).</summary>
    public readonly record struct ToolbarHit(ToolbarZone Zone, string Glyph, int Column);

    /// <summary>A click anywhere on the performance bar (2026-09-29): the toolbar's blanks, so every reader answers it with <c>/settings</c>. Pinned.</summary>
    public static readonly ToolbarHit PerfBarHit = new(ToolbarZone.Row, "", -1);

    /// <param name="inner">The console the pane draws on.</param>
    /// <param name="geometry">Where the cursor is; null disables the pane (a plain transcript).</param>
    /// <param name="time">The clock for the tick; tests pass a manual one.</param>
    public ScreenPane(IAnsiConsole inner, ScreenGeometry? geometry, TimeProvider time)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        _geometry = geometry;
        Enabled = geometry is not null && inner.Profile.Capabilities.Interactive && inner.Profile.Capabilities.Ansi;
        if (Enabled)
        {
            // Before Open the flow is whatever the console holds, from its cursor on.
            _row = Math.Clamp(geometry!.CursorRow() ?? 0, 0, Height - 1);
            _blank = false;
            _lastWidth = Width;
            _lastHeight = Height;
            _timer = time.CreateTimer(_ => OnTick(), null, Tick, Tick);
        }
    }

    /// <summary>The pane is on the screen: the geometry is known and the console draws menus.</summary>
    public bool Enabled { get; }

    /// <summary>
    /// The close-everything signal (2026-09-18): set by <see cref="Dismiss"/> while an overlay is
    /// drawn — a double-click off the pane, read by the pane readers — and cleared by
    /// <see cref="CloseOverlay"/> (and <see cref="Close"/>). While it stands
    /// <see cref="MenuPane.PickAsync"/> returns null at entry, so every nested host on the way up
    /// backs out without a draw until the top one's close clears it; the × glyph stays one level.
    /// </summary>
    public bool Dismissed { get; private set; }

    /// <summary>Raises <see cref="Dismissed"/>; nothing without an overlay to dismiss.</summary>
    public void Dismiss()
    {
        if (!Enabled)
        {
            return;
        }

        lock (_gate)
        {
            if (_overlay is not null)
            {
                Dismissed = true;
            }
        }
    }

    /// <summary>
    /// <see cref="Dismiss()"/> by a double-click at buffer cell (<paramref name="x"/>, <paramref name="y"/>)
    /// (later on 2026-09-21, the user's ask): the part of the hint row or the toolbar under it
    /// (<see cref="OffPaneHitAt"/>) is kept for <see cref="TakeDismissHit"/>, so the screen can
    /// tell "the open pane's own glyph" (closed, nothing more) from "another pane's" (that one
    /// opens next) once the host has backed out.
    /// </summary>
    public void Dismiss(int x, int y)
    {
        var hit = OffPaneHitAt(x, y);
        if (!Enabled)
        {
            return;
        }

        lock (_gate)
        {
            if (_overlay is not null)
            {
                Dismissed = true;
                _dismissHit = hit;
            }
        }
    }

    /// <summary>
    /// The off-pane part the last <see cref="Dismiss(int, int)"/> landed on, once: cleared here,
    /// by the next <see cref="ShowOverlay"/> and by <see cref="Close"/>. Null after a
    /// <see cref="Dismiss()"/> without a click, a click on the transcript or a rule, or nothing dismissed.
    /// </summary>
    public OffPaneHit? TakeDismissHit()
    {
        lock (_gate)
        {
            var hit = _dismissHit;
            _dismissHit = null;
            return hit;
        }
    }

    /// <summary>
    /// The command chord that last dismissed a pane (<see cref="Chord"/>), once: cleared here and by <see cref="Close"/> —
    /// never by a new overlay (the review's finding, 2026-10-01: a handler that went on to show another pane after the first
    /// closed dropped the chord), so the screen runs it as soon as the hosts and the handler have backed out. Null when none
    /// did. Kept apart from <see cref="TakeDismissHit"/>, which is a click's place on the screen, not a command.
    /// </summary>
    public string? TakeDismissChord()
    {
        lock (_gate)
        {
            var chord = _dismissChord;
            _dismissChord = null;
            return chord;
        }
    }

    /// <summary>
    /// The screen's hook for a command chord pressed in a pane that leaves the pane open (2026-10-01, the user's ask:
    /// Ctrl+F <c>/perfbar</c> and Ctrl+T <c>/toolbar</c> (Ctrl+Alt+E and B until later still that day) toggle their bar in place, the tick repainting the pane's new
    /// shape; Ctrl+E <c>/explore</c> opens the file browser, later that day): true when it did the chord's command, false for every chord that closes the pane first. Null: none does.
    /// </summary>
    public Func<string, bool>? ChordInPlace { get; set; }

    // How many tool-asked panes are open (SuppressChords); while above zero a chord in a pane is nobody's.
    private int _chordsSuppressed;

    /// <summary>
    /// The chords set aside while the returned scope lasts (2026-10-01, the user's call): a pane a tool opened to ask the user
    /// (a command's approval, <c>ask_user</c>, the plan's approval…) ignores them, so a chord can never answer it by accident.
    /// </summary>
    public IDisposable SuppressChords()
    {
        Interlocked.Increment(ref _chordsSuppressed);
        return new ChordScope(this);
    }

    private sealed class ChordScope(ScreenPane pane) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                Interlocked.Decrement(ref pane._chordsSuppressed);
            }
        }
    }

    /// <summary>
    /// A command chord (<see cref="Keys.ShortcutLine"/>) pressed in a pane (2026-10-01, the user's ask: the chords work in a pane
    /// as everywhere else): what a pane reader does with it. True when the reader carries on — the chords are suppressed (a
    /// tool-asked pane), or <see cref="ChordInPlace"/> did the command with the pane open, or there is no overlay to close;
    /// false when the whole stack was dismissed (<see cref="Dismissed"/>) with <paramref name="line"/> kept for
    /// <see cref="TakeDismissChord"/>, so the screen runs it once the hosts have backed out —
    /// the double-click off a pane's path, which closes the pane its own word names and opens any other.
    /// The pane readers come here through <see cref="KeySource.ReadPaneInputAsync"/>.
    /// </summary>
    public bool Chord(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (Volatile.Read(ref _chordsSuppressed) > 0 || !Enabled)
        {
            return true;
        }

        if (ChordInPlace is { } inPlace && inPlace(line))
        {
            return true;
        }

        lock (_gate)
        {
            if (_overlay is null)
            {
                return true;
            }

            Dismissed = true;
            _dismissChord = line;
            return false;
        }
    }

    /// <summary>
    /// The part of the hint row or the toolbar a click off an open pane names (later on
    /// 2026-09-21): <see cref="Toolbar"/> on the toolbar row (a glyph, the path or the blanks) or the performance bar (the blanks' <see cref="PerfBarHit"/>, 2026-09-29),
    /// <see cref="Hint"/> on the standing hint row (the model name, its reasoning mark, a strip
    /// glyph or the rest — never the queued count or the tally, which are not drawn under a pane).
    /// </summary>
    public readonly record struct OffPaneHit(HintHit? Hint, ToolbarHit? Toolbar);

    /// <summary>
    /// <see cref="OffPaneHit"/> for buffer cell (<paramref name="x"/>, <paramref name="y"/>) —
    /// <see cref="TryHitToolbar"/> and <see cref="TryHitHint(int, int, out HintHit)"/> without
    /// their overlay guard, since this is asked while a pane is drawn (the readers' outside pair).
    /// Null on the transcript, the rules, the overlay's own rows, under the busy row (the spinner's
    /// row names nothing), when the pane is lifted, batched or modal, or when the console cannot
    /// say where the cursor is.
    /// </summary>
    public OffPaneHit? OffPaneHitAt(int x, int y)
    {
        if (!Enabled)
        {
            return null;
        }

        lock (_gate)
        {
            if (!_drawn || _batch > 0 || _modal > 0 || _geometry?.CursorTop() is not int top)
            {
                return null;
            }

            if (_perfRows > 0 && y == top + LastRowBelowCursor)
            {
                return new OffPaneHit(null, PerfBarHit);
            }

            if (_toolbarRows > 0 && y == top + ToolbarRowBelowCursor)
            {
                return new OffPaneHit(null, ToolbarHitAt(_toolbarStrip, _toolbarPathColumn, _toolbarPathCells, x));
            }

            if (_busyLabel is null && y == top + HintRowBelowCursor)
            {
                return new OffPaneHit(HintHitAt(_hintStrip, _trailerColumn, _markColumn, -1, 0, -1, 0, x), null);
            }

            return null;
        }
    }

    /// <summary>
    /// The <see cref="DoubleClick"/> key of a click off an open pane (later on 2026-09-21): the
    /// readers' <see cref="MenuPane.OutsideRow"/> on the transcript and the rules, else one key per
    /// off-pane part — the toolbar's path, its blanks, each glyph by column; the hint row's zones,
    /// each strip glyph by column — so two clicks on different parts never pair (the transcript
    /// then a toolbar glyph opens nothing). All below −2, never the pairing's own −1. Pinned.
    /// </summary>
    public int OutsideKey(int x, int y) => OutsideKeyOf(OffPaneHitAt(x, y));

    /// <summary><see cref="OutsideKey"/> for a hit already taken. Pure.</summary>
    public static int OutsideKeyOf(OffPaneHit? hit) => hit switch
    {
        { Toolbar: { } tool } => tool.Zone switch
        {
            ToolbarZone.Path => -3,
            ToolbarZone.Row => -4,
            _ => -5 - tool.Column,
        },
        { Hint: { } row } => row.Zone == HintZone.Strip ? -200 - row.Column : -100 - (int)row.Zone,
        _ => MenuPane.OutsideRow,
    };

    /// <summary>The clock the tick and the busy row run on; the overlays share it (a menu's double-click).</summary>
    public TimeProvider Time => _time;

    /// <summary>The standing hint (the state line under the input row), read on every redraw and on the tick.</summary>
    public Func<string> Hint
    {
        get => _hint;
        set => _hint = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// The status strip at column 0 of the hint row in every state — the standing hint, an overlay's
    /// hint, the spinner — with <see cref="HintSeparator"/> after it, read like <see cref="Hint"/>;
    /// empty = nothing drawn. The screen puts the speech glyphs there: status, not a hint, so a
    /// reply's spinner or a menu never takes it away (the user's call, 2026-09-15). The profile's
    /// name sat ahead of it until 2026-09-15; the window title names it now.
    /// </summary>
    public Func<string> Strip
    {
        get => _strip;
        set => _strip = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// The title at the right edge of the upper rule — the one above the input row — in every
    /// state (2026-09-18, the user's ask), read like <see cref="Strip"/> on every draw and on the
    /// tick, which repaints the pane when it changes; empty = the bare rule. The screen puts the
    /// session's name there: <see cref="RuleWithTitle"/> is the row.
    /// </summary>
    public Func<string> RuleTitle
    {
        get => _ruleTitle;
        set => _ruleTitle = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// The label at the right edge of the hint row in every state, read like <see cref="Strip"/>;
    /// empty = the row as it is. The screen puts the model's name there.
    /// </summary>
    public Func<string> Trailer
    {
        get => _trailer;
        set => _trailer = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// The queued-messages part after the row's lead in both states (2026-09-18): behind the strip
    /// on the standing row, behind the spinner's label on the busy row, <see cref="HintSeparator"/>
    /// between, read like <see cref="Strip"/>; empty = nothing drawn. Hidden under an overlay's
    /// hint and the scroll's, like the screen's own hint; ahead of that hint so a long usage part
    /// is what a narrow row cuts. The screen puts <c>📤 2 queued</c> there; the pane records where
    /// (<see cref="TryHitQueued"/>, <see cref="HintZone.Queued"/>) for the double-click that opens <c>/queue</c>.
    /// </summary>
    public Func<string> Queued
    {
        get => _queued;
        set => _queued = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// The token tally as it stands in the screen's hint (2026-09-21): not drawn by the pane —
    /// <see cref="Hint"/> carries it — but looked for in the drawn standing row so its cells are
    /// <see cref="HintZone.Usage"/> for the double-click that opens <c>/usage</c>; empty, or absent
    /// from the row (the timers or the exit hint in its place), = no zone. Read like <see cref="Strip"/>.
    /// </summary>
    public Func<string> Usage
    {
        get => _usage;
        set => _usage = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// The token tally the busy row draws after the spinner's label (2026-09-25, <c>LLM mid-turn usage</c>): the pane draws
    /// this one itself (<see cref="BusyRow(string, TimeSpan, string, string, string)"/>), re-read on every tick so a live
    /// estimate moves with the spinner; nothing under an overlay's hint or the scroll's, as the standing row's tally. Its
    /// cells join the spinner's in <see cref="HintZone.Usage"/>. Empty = the row as before.
    /// </summary>
    public Func<string> BusyUsage
    {
        get => _busyUsage;
        set => _busyUsage = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Which busy labels the row draws after the tally rather than before it (2026-09-25, the user's call: the ComfyUI
    /// generation's <c>🎨</c>, <c>⠹ 1.2k / 4.1k · 30% · 🎨 00:12</c>); asked per draw. None by default.
    /// </summary>
    public Func<string, bool> LabelAfterUsage
    {
        get => _labelAfterUsage;
        set => _labelAfterUsage = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Which busy labels are their own click zone where they stand, ahead of the tally (2026-10-01, the review's finding: the
    /// embedded model's load took a double-click anywhere on <see cref="HintZone.Usage"/> as its cancel, the tally beside
    /// it included): <see cref="HintZone.Label"/> is then the spinner's frame, its blank and the label with its time, and the
    /// tally keeps <see cref="HintZone.Usage"/>. A label <see cref="LabelAfterUsage"/> takes is its own zone already. Asked
    /// per draw; none by default.
    /// </summary>
    public Func<string, bool> LabelZone
    {
        get => _labelZone;
        set => _labelZone = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// The toolbar under the hint row (2026-09-21, the user's ask): a row the pane draws in every
    /// state — idle, busy, scrolled, under an overlay — like the strip; null (the default) = no
    /// row. Read like <see cref="Strip"/> on every draw and on the tick, which rewrites the row in
    /// place when its text changes (the working directory) and repaints the pane when the row
    /// comes or goes (the screen's <c>Show toolbar</c> switch). A window under
    /// <c>PaneRows + 2</c> rows draws none, so a transcript row survives. The screen puts the
    /// pane glyphs at the left and the working directory at the right;
    /// <see cref="ToolbarRow"/> is the row, <see cref="TryHitToolbar"/> the double-click's zones.
    /// </summary>
    public Func<ToolbarParts?> Toolbar
    {
        get => _toolbar;
        set => _toolbar = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// The terminal's page background (2026-10-03, the user's ask: a theme's <c>bg</c> never reached Windows Terminal, every
    /// unstyled cell kept the profile's colour scheme): asked at the start of every frame while the alternate buffer is up,
    /// and when the answer differs from the one last written the terminal's default background is set to it (OSC 11,
    /// <see cref="PageBackgroundSequence"/>), or given back (OSC 111) on a null. One code reaches every default cell, the
    /// erased ones and the rest of a line included, where painting cells would miss them. <see cref="Close"/> and a dispose
    /// give the profile's colour back. The screen answers the palette's <see cref="ThemePalette.Bg"/> under
    /// <c>Themed background</c>, else null; the default is null, so a pane nobody asked writes nothing.
    /// </summary>
    public Func<Color?> PageBackground
    {
        get => _pageBackground;
        set => _pageBackground = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary><c>ESC ] 11 ; rgb:rr/gg/bb ESC \</c>: the terminal's default background set to <paramref name="color"/>. Pinned.</summary>
    public static string PageBackgroundSequence(Color color) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"\e]11;rgb:{color.R:x2}/{color.G:x2}/{color.B:x2}\e\\");

    /// <summary><c>ESC ] 111 ESC \</c>: the terminal's default background back to the profile's. Pinned.</summary>
    public const string PageBackgroundResetSequence = "\e]111\e\\";

    /// <summary>The rows the toolbar took in the last draw: 1 while drawn, else 0 (the thumbnail sizing adds it to <see cref="PaneRows"/> and <see cref="InputRows"/>).</summary>
    public int ToolbarRows
    {
        get { lock (_gate) { return _toolbarRows; } }
    }

    /// <summary>
    /// The performance bar (2026-09-29, the user's ask: a third bar): asked with the cells the row has on every draw and on
    /// the tick; null draws no row. It takes the screen's last row, under the toolbar, when the window keeps a transcript
    /// row over the smallest pane and the toolbar; the tick rewrites it in place when its text changes and repaints the pane
    /// when it comes or goes (the screen's <c>Show performance bar</c> setting).
    /// </summary>
    public Func<int, UI.PerfRow?> Perf
    {
        get => _perf;
        set => _perf = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// The <c>Menus max height</c> word (2026-10-01, the user's ask): read by the <see cref="MenuPane"/>, the
    /// <see cref="InfoPane"/> and the <see cref="FolderPane"/> on every draw through <see cref="MenuContentRows"/>, so a
    /// change applies at the next pane shown. <c>full-screen</c> (the default here) is the pane as it grew before the setting.
    /// </summary>
    public Func<string> MenuHeight
    {
        get => _menuHeight;
        set => _menuHeight = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// The content rows a menu may take on a window of <paramref name="height"/> rows (the host's
    /// <see cref="LayoutHeight"/>) over <paramref name="inputRows"/> input rows: <see cref="UI.MenuHeight.ContentRows"/>
    /// under <see cref="MenuHeight"/>, never past <see cref="MaxOverlayRows"/>.
    /// </summary>
    public int MenuContentRows(int height, int inputRows) =>
        UI.MenuHeight.ContentRows(UI.MenuHeight.Resolve(_menuHeight()), height, inputRows);

    /// <summary>The rows the performance bar took in the last draw: 1 while drawn, else 0 (the thumbnail sizing adds it, as <see cref="ToolbarRows"/>).</summary>
    public int PerfRows
    {
        get { lock (_gate) { return _perfRows; } }
    }

    /// <summary>
    /// The session's picture strip (later still on 2026-09-24): drawn <see cref="UI.PictureStrip.Rows"/> tall under a rule of
    /// its own (<see cref="StripRuleRows"/>, 2026-09-25) and over the upper rule while this answers one with pictures in it, no overlay is open, the window keeps
    /// <see cref="StripTranscriptRows"/> transcript rows over the strip and the pane, and is at least
    /// <see cref="UI.PictureStrip.MinCells"/> wide; null = none (the default). Read at each draw and on the tick, so a
    /// picture added, a step or the setting's flip shows by itself; <see cref="RedrawStrip"/> shows it at once. The
    /// highlight is drawn only while the draft is empty — typing puts the arrows back on the line, and the highlight goes.
    /// </summary>
    public Func<PictureStrip?> PictureStrip
    {
        get => _pictureStrip;
        set => _pictureStrip = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// The label of the button on the picture strip's own rule (2026-09-27, the user's ask: the ComfyUI picture viewer opened
    /// from the strip): drawn at the rule's left edge since 2026-09-28 (the user's call, the close × took the right;
    /// <see cref="StripRule"/>), in <see cref="Theme.AccentSecondary"/> so it reads as something to click; null or empty = no
    /// button (the default), the × stays. Read at each draw; a click on it is <see cref="TryHitStripButton"/>.
    /// </summary>
    public Func<string?> StripButton
    {
        get => _stripButton;
        set => _stripButton = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// The label of the upper rule's button that brings a closed picture strip back (2026-10-03, the user's ask: the strip's ×
    /// left no way back short of another picture): drawn right of ⤡, or in its place without it (<see cref="UpperRule"/>), in
    /// <see cref="Theme.AccentSecondary"/> as the strip's own button. Null or empty = no button (the default); the screen
    /// answers one only while its strip is closed with pictures in it. Not drawn under an overlay, nor in a window with no
    /// room for the strip (it would bring back nothing to see). Read at each draw and on the tick; a click on it is
    /// <see cref="TryHitStripReopen"/>.
    /// </summary>
    public Func<string?> StripReopen
    {
        get => _stripReopen;
        set => _stripReopen = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Whether a click at buffer cell (<paramref name="x"/>, <paramref name="y"/>) landed on <see cref="StripButton"/>'s
    /// label on the drawn strip's rule, or the space either side of it (2026-09-28: the glyph alone is a small target).
    /// False with no strip or no button drawn, the pane lifted or disabled, or no geometry.
    /// </summary>
    public bool TryHitStripButton(int x, int y) => HitStripRule(x, y, close: false);

    /// <summary>
    /// Whether a click at buffer cell (<paramref name="x"/>, <paramref name="y"/>) landed on the close × at the right of the
    /// drawn strip's rule (2026-09-28), or the cell either side — three cells, as <see cref="TryHitClose"/> gives an
    /// overlay's. The screen puts the strip away until the next picture (<see cref="UI.PictureStrip.Close"/>). False as
    /// <see cref="TryHitStripButton"/> is.
    /// </summary>
    public bool TryHitStripClose(int x, int y) => HitStripRule(x, y, close: true);

    /// <summary>Whether (<paramref name="x"/>, <paramref name="y"/>) is on the drawn strip rule's button or its ×, with the cell either side.</summary>
    private bool HitStripRule(int x, int y, bool close)
    {
        if (!Enabled)
        {
            return false;
        }

        lock (_gate)
        {
            int column = close ? _stripCloseColumn : _stripButtonColumn;
            if (!_drawn || _drawnOverlay || _stripRows == 0 || column < 0 || _batch > 0 || _modal > 0 || _geometry?.CursorTop() is not int top)
            {
                return false;
            }

            // The strip's own rule is its first row, _stripRows over the upper rule.
            int rule = top - CursorDepth - 1;
            int cells = close ? TextCells.Width(CloseGlyph) : _stripButtonCells;
            return y == rule - _stripRows && x >= column - 1 && x <= column + cells;
        }
    }

    /// <summary>
    /// Whether a click at buffer cell (<paramref name="x"/>, <paramref name="y"/>) landed on the upper rule's
    /// <see cref="FoldGlyph"/> (<see cref="UpperRule"/>) or the space either side of it. The caller does what Ctrl+O does
    /// (<see cref="ToggleToolGroups"/>, 2026-09-29; one button for the unfold and the fold ↘️ ↖️ were until then). False with
    /// no button drawn, an overlay drawn, the pane lifted or disabled, or no geometry.
    /// </summary>
    public bool TryHitFoldButton(int x, int y)
    {
        if (!Enabled)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_drawn || _drawnOverlay || _foldColumn < 0 || _batch > 0 || _modal > 0 || _geometry?.CursorTop() is not int top)
            {
                return false;
            }

            if (y != top - CursorDepth - 1)
            {
                return false;
            }

            return x >= _foldColumn - 1 && x <= _foldColumn + TextCells.Width(FoldGlyph);
        }
    }

    /// <summary>
    /// Whether a click at buffer cell (<paramref name="x"/>, <paramref name="y"/>) landed on the upper rule's
    /// <see cref="StripReopen"/> button (2026-10-03): its cells and the space after them, and the space before them too when
    /// no ⤡ is drawn — beside ⤡ that space is ⤡'s (<see cref="TryHitFoldButton"/>). The caller opens the strip again
    /// (<see cref="UI.PictureStrip.Open"/>). False as <see cref="TryHitFoldButton"/> is.
    /// </summary>
    public bool TryHitStripReopen(int x, int y)
    {
        if (!Enabled)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_drawn || _drawnOverlay || _reopenColumn < 0 || _drawnReopen is not { } label || _batch > 0 || _modal > 0 || _geometry?.CursorTop() is not int top)
            {
                return false;
            }

            if (y != top - CursorDepth - 1)
            {
                return false;
            }

            int from = _foldColumn < 0 ? _reopenColumn - 1 : _reopenColumn;
            return x >= from && x <= _reopenColumn + TextCells.Width(label);
        }
    }

    /// <summary>
    /// Whether a click at buffer cell (<paramref name="x"/>, <paramref name="y"/>) landed on the session's name at the upper
    /// rule's right edge (2026-09-28, the user's ask: a double-click there renames the session, <see cref="UpperRuleParts.TitleColumn"/>),
    /// the space either side of it included. False with no title drawn, an overlay drawn, the pane lifted or disabled, or no geometry.
    /// </summary>
    public bool TryHitRuleTitle(int x, int y)
    {
        if (!Enabled)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_drawn || _drawnOverlay || _ruleTitleColumn < 0 || _batch > 0 || _modal > 0 || _geometry?.CursorTop() is not int top)
            {
                return false;
            }

            return y == top - CursorDepth - 1 && x >= _ruleTitleColumn - 1 && x <= _ruleTitleColumn + _ruleTitleCells;
        }
    }

    /// <summary>The transcript rows the window must keep over the strip and the smallest pane for the strip to be drawn.</summary>
    public const int StripTranscriptRows = 4;

    /// <summary>
    /// The rule over the strip (2026-09-25, the user's ask: the strip reads as part of the pane, not as more transcript): one
    /// row of <see cref="RuleGlyph"/> in <see cref="Theme.PaneRule"/>, the input rows' own rule, counted as a strip row so every
    /// sum that leaves room for the strip leaves room for it too.
    /// </summary>
    public const int StripRuleRows = 1;

    /// <summary>The pane rows a drawn strip takes: its rule and its <see cref="UI.PictureStrip.Rows"/>.</summary>
    public const int StripPaneRows = UI.PictureStrip.Rows + StripRuleRows;

    /// <summary>The rows the picture strip took in the last draw: <see cref="StripPaneRows"/> while drawn, else 0 (the thumbnail sizing adds it, as <see cref="ToolbarRows"/>).</summary>
    public int StripRows
    {
        get { lock (_gate) { return _stripRows; } }
    }

    /// <summary>The rows the strip takes in a window of <paramref name="width"/> × <paramref name="height"/> with <paramref name="toolbarRows"/> for the toolbar, as things stand: <see cref="PictureStrip"/>'s rule.</summary>
    private int StripRowsFor(int width, int height, int toolbarRows) =>
        _overlay is null && _pictureStrip() is { Count: > 0 } && StripFits(width, height, toolbarRows) ? StripPaneRows : 0;

    /// <summary>Whether a window of <paramref name="width"/> × <paramref name="height"/> with <paramref name="toolbarRows"/> for the toolbar has room for the strip: <see cref="StripTranscriptRows"/> kept over it and the smallest pane, and <see cref="UI.PictureStrip.MinCells"/> across.</summary>
    private static bool StripFits(int width, int height, int toolbarRows) =>
        height >= PaneRows + toolbarRows + StripPaneRows + StripTranscriptRows && width - 1 >= UI.PictureStrip.MinCells;

    /// <summary>The strip the provider answers now is not the drawn one: it came or went, changed, or its highlight did.</summary>
    private bool StripChanged()
    {
        if (StripRowsFor(Width, Height, BarRowsFor(Height)) != _stripRows)
        {
            return true;
        }

        return _stripRows > 0 && _pictureStrip() is { } strip && (strip.Version != _drawnStripVersion || (_text.Length == 0) != _drawnStripHighlight);
    }

    /// <summary>
    /// The pane again now if the strip changed (<see cref="StripChanged"/>), or the upper rule's buttons with it (the 🎞️ that
    /// a close puts there and a reopen takes away, 2026-10-03): the screen calls it after a step or a new picture so the strip
    /// follows the key, not the next tick. Nothing lifted, under a batch or a modal, or disabled.
    /// </summary>
    public void RedrawStrip()
    {
        if (!Enabled)
        {
            return;
        }

        lock (_gate)
        {
            if (_drawn && (StripChanged() || RuleButtonsChanged()))
            {
                Redraw();
            }
        }
    }

    /// <summary>
    /// The picture under a click at buffer cell (<paramref name="x"/>, <paramref name="y"/>) on the drawn strip (later
    /// still on 2026-09-24): its id, for the double-click that opens it. False off the strip's rows or tiles, with no
    /// strip drawn, when the pane is lifted, or when the console cannot say where the cursor is. The strip's rows sit
    /// right over the upper rule, which is one row over the area's first row.
    /// </summary>
    public bool TryHitStrip(int x, int y, out int id)
    {
        id = -1;
        if (!Enabled)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_drawn || _drawnOverlay || _stripRows == 0 || _batch > 0 || _modal > 0 || _geometry?.CursorTop() is not int top)
            {
                return false;
            }

            // The tiles are the rows between the strip's own rule and the upper rule.
            int rule = top - CursorDepth - 1;
            if (y < rule - _stripRows + StripRuleRows || y >= rule)
            {
                return false;
            }

            foreach (var span in _stripSpans)
            {
                if (x >= span.Col && x < span.Col + span.Width)
                {
                    id = span.Id;
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// The window height less the toolbar's row when <see cref="Toolbar"/> answers one: what an
    /// overlay host lays out against (the menus, the info and folder panes, the completion list),
    /// since <see cref="MaxOverlayRows"/> and <see cref="MaxInputRows"/> are counted over the rows
    /// the toolbar leaves. The provider, not the drawn count: the host lays out BEFORE the draw.
    /// </summary>
    public int LayoutHeight => Height - BarRowsFor(Height);

    /// <summary>The rows the toolbar takes in a window of <paramref name="height"/>: one when <see cref="Toolbar"/> answers and the window keeps a transcript row over the smallest pane, else none.</summary>
    private int ToolbarRowsFor(int height) => _toolbar() is not null && height >= PaneRows + 2 ? 1 : 0;

    /// <summary>The rows the performance bar takes in a window of <paramref name="height"/>: one when <see cref="Perf"/> answers and the window keeps a transcript row over the smallest pane and the toolbar, else none.</summary>
    private int PerfRowsFor(int height) => _perf(Math.Max(1, Width - 1)) is not null && height >= PaneRows + 2 + ToolbarRowsFor(height) ? 1 : 0;

    /// <summary>The rows under the hint row in a window of <paramref name="height"/>: the toolbar's and the performance bar's.</summary>
    private int BarRowsFor(int height) => ToolbarRowsFor(height) + PerfRowsFor(height);

    /// <summary>The rows under the hint row as last drawn.</summary>
    private int BarRows => _toolbarRows + _perfRows;

    /// <summary>
    /// A short glyph after the trailer, <see cref="MarkSeparator"/> between, drawn in
    /// <see cref="Theme.TrailerMark"/> and never cut — the trailer's text is cut ahead of it; read
    /// like <see cref="Trailer"/>; empty = none. The screen puts the reasoning glyph there.
    /// </summary>
    public Func<string> TrailerMark
    {
        get => _trailerMark;
        set => _trailerMark = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// The ghost text on the empty input row: drawn dim after the glyph while the draft is empty,
    /// the pane idle (no spinner) and no overlay open — never in an overlay's input slot — and
    /// gone with the first key; empty = none (the default). Cut to the row (<see cref="PlaceholderRow"/>).
    /// The screen puts <c>Type a message or /help for more info</c> there. Disabled: nothing.
    /// </summary>
    public string Placeholder
    {
        get => _placeholder;
        set => _placeholder = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Whether the draft the pane draws is empty (2026-09-20): the text of the last
    /// <see cref="ShowInput"/>, emptied by a commit or a clear. What the screen's splash hint reads
    /// from its <see cref="Hint"/> func — under the pane's lock on the tick, which is reentrant —
    /// so the row says <c>← → slideshow</c> only while an arrow would walk the pictures.
    /// A disabled pane has no draft of its own: true.
    /// </summary>
    public bool DraftEmpty
    {
        get { lock (_gate) { return _text.Length == 0; } }
    }

    /// <summary>Between the strip and the rest of the hint row (and the spinner's text and an overlay's hint, <see cref="BusyRow"/>).</summary>
    public const string HintSeparator = " · ";

    /// <summary>The least blanks between the hint row's text and the trailer.</summary>
    public const int TrailerGap = 2;

    /// <summary>Between the trailer's text and its mark.</summary>
    public const string MarkSeparator = " ";

    /// <summary>
    /// The least cells the trailer's text gives way to (2026-09-28, the user's call): below the
    /// half-row cap the model name shrinks for the text on its left, down to this, and only then is
    /// the left cut — sixteen keeps <c>Qwen3.8-27B-Unc…</c> readable and a target for the click.
    /// </summary>
    public const int TrailerMinCells = 16;

    /// <summary>The least blanks between the toolbar's strip and its path.</summary>
    public const int ToolbarGap = 2;

    /// <summary>The least cells the toolbar's path is drawn in: fewer and the path goes, as <c>SidekickApp.BannerPathMinCells</c> drops the banner's (2026-09-18).</summary>
    public const int ToolbarPathMinCells = 8;

    /// <summary>
    /// The toolbar of <paramref name="cells"/> (2026-09-21): <paramref name="strip"/> from column 0
    /// (cut to the row, <see cref="Fit"/>), <paramref name="path"/> ending on the last cell, cut
    /// from the front (<see cref="FitTail"/>) to the room <see cref="ToolbarGap"/> leaves after the
    /// strip — none under <see cref="ToolbarPathMinCells"/>, and the strip stands alone. Pinned.
    /// </summary>
    public static string ToolbarRow(string strip, string path, int cells)
    {
        ArgumentNullException.ThrowIfNull(strip);
        ArgumentNullException.ThrowIfNull(path);
        string left = Fit(strip, cells);
        int leftCells = TextCells.Width(left);
        int room = cells - leftCells - ToolbarGap;
        string shown = path.Length == 0 || room < ToolbarPathMinCells ? "" : FitTail(path, room);
        return shown.Length == 0 ? left : left + new string(' ', cells - leftCells - TextCells.Width(shown)) + shown;
    }

    /// <summary>
    /// The hint row with <paramref name="trailer"/> on its last cell: the trailer cut to half of
    /// <paramref name="cells"/> at most, <paramref name="left"/> cut ahead of the gap, blanks
    /// between; with no trailer, <paramref name="left"/> fitted as before. Pinned.
    /// </summary>
    public static string PinRight(string left, string trailer, int cells) => PinRight(left, trailer, "", cells);

    /// <summary>
    /// <see cref="PinRight(string, string, int)"/> with <paramref name="mark"/> after the trailer's
    /// text (<see cref="Trail"/>): the mark is never cut, the text is cut ahead of it. Pinned.
    /// </summary>
    public static string PinRight(string left, string trailer, string mark, int cells)
    {
        ArgumentNullException.ThrowIfNull(left);
        string right = Trail(trailer, mark, cells, TextCells.Width(left));
        if (right.Length == 0)
        {
            return Fit(left, cells);
        }

        int rightCells = TextCells.Width(right);
        string fitted = Fit(left, cells - rightCells - TrailerGap);
        return fitted + new string(' ', cells - TextCells.Width(fitted) - rightCells) + right;
    }

    /// <summary>
    /// The right part of a hint row of <paramref name="cells"/>: <paramref name="trailer"/> cut to
    /// half the row at most; with a <paramref name="mark"/>, the text cut to what the mark and its
    /// separator leave of that half and the mark whole behind it — at an absurd width the text
    /// goes to an ellipsis, then to nothing, and the mark stands alone. Since 2026-09-28 (the user's
    /// picture: an overlay's hint cut while a long model name kept its half) the text also gives way
    /// to <paramref name="leftCells"/>, the uncut width of what stands on its left: it takes what that
    /// and <see cref="TrailerGap"/> leave, never under <see cref="TrailerMinCells"/> nor over the half.
    /// None on the left (the default) is the half as before. Pinned.
    /// </summary>
    public static string Trail(string trailer, string mark, int cells, int leftCells = 0)
    {
        ArgumentNullException.ThrowIfNull(trailer);
        ArgumentNullException.ThrowIfNull(mark);
        int markCells = mark.Length == 0 ? 0 : TextCells.Width(mark) + MarkSeparator.Length;
        int room = Math.Min(cells / 2 - markCells, Math.Max(TrailerMinCells, cells - leftCells - TrailerGap - markCells));
        return mark.Length == 0 ? Fit(trailer, room) : TrailerText(Fit(trailer, room), mark);
    }

    /// <summary>
    /// The trailer's text and its mark with <see cref="MarkSeparator"/> between; either alone when
    /// the other is empty. Pinned.
    /// </summary>
    public static string TrailerText(string trailer, string mark)
    {
        ArgumentNullException.ThrowIfNull(trailer);
        ArgumentNullException.ThrowIfNull(mark);
        return trailer.Length == 0 ? mark : mark.Length == 0 ? trailer : trailer + MarkSeparator + mark;
    }

    /// <summary>
    /// The hint row's text: <paramref name="rest"/> behind the <paramref name="lead"/> (the strip)
    /// when there is one; either alone when the other is empty. Pinned.
    /// </summary>
    public static string HintRow(string lead, string rest)
    {
        ArgumentNullException.ThrowIfNull(lead);
        ArgumentNullException.ThrowIfNull(rest);
        return lead.Length == 0 ? rest : rest.Length == 0 ? lead : lead + HintSeparator + rest;
    }

    /// <summary>The strip with its separator, ahead of the spinner; empty without one. Pinned.</summary>
    public static string StripPrefix(string strip)
    {
        ArgumentNullException.ThrowIfNull(strip);
        return strip.Length == 0 ? "" : strip + HintSeparator;
    }

    /// <summary>The counted flow row (tests).</summary>
    public int FlowRow
    {
        get
        {
            lock (_gate)
            {
                return _row;
            }
        }
    }

    /// <summary>The counted flow column (tests).</summary>
    public int FlowColumn
    {
        get
        {
            lock (_gate)
            {
                return _col;
            }
        }
    }

    /// <summary>The padding rows drawn above the pane by the last redraw (tests).</summary>
    public int Padding
    {
        get
        {
            lock (_gate)
            {
                return _pad;
            }
        }
    }

    /// <summary>The overlay's content rows drawn by the last redraw, 0 without one (tests).</summary>
    public int OverlayRows
    {
        get
        {
            lock (_gate)
            {
                return _overlayRows;
            }
        }
    }

    /// <summary>An overlay is on the pane in place of the input row.</summary>
    public bool OverlayOpen
    {
        get
        {
            lock (_gate)
            {
                return _overlay is not null;
            }
        }
    }

    /// <summary>The open overlay keeps the input rows under its content (tests).</summary>
    public bool OverlayHasInput
    {
        get
        {
            lock (_gate)
            {
                return _overlay is { Input: true };
            }
        }
    }

    // ── The live slot ───────────────────────────────────────────────────────

    /// <summary>A reply is open in the live slot (set and not yet committed or discarded).</summary>
    public bool LiveOpen
    {
        get
        {
            lock (_gate)
            {
                return _live is not null;
            }
        }
    }

    /// <summary>The live rows drawn above the padding by the last redraw (tests).</summary>
    public int LiveRows
    {
        get
        {
            lock (_gate)
            {
                return _liveRows;
            }
        }
    }

    /// <summary>The lines of the live content already committed into the flow because the block outgrew the screen (tests).</summary>
    public int LiveCommitted
    {
        get
        {
            lock (_gate)
            {
                return _liveCommitted;
            }
        }
    }

    // ── The alternate buffer and the scroll ─────────────────────────────────

    /// <summary>
    /// Enters the alternate screen buffer (the shell's screen is kept for <see cref="Close"/>) with
    /// the flow at its top: the screen's start, before the banner. Disabled, or already in it: nothing.
    /// </summary>
    public void Open()
    {
        if (!Enabled)
        {
            return;
        }

        lock (_gate)
        {
            if (_inAlternate)
            {
                return;
            }

            _inner.Write(EnterAlternate);
            _inAlternate = true;
            SyncPageBackgroundLocked();
            _row = 0;
            _col = 0;
            _lineFull = false;
            _blank = true;
            DiagnosticLog.Debug(Category, AlternateEnteredLogLine(Width, Height));
        }
    }

    /// <summary><c>Alternate buffer entered (240×60)</c>. Pinned.</summary>
    public static string AlternateEnteredLogLine(int width, int height) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Alternate buffer entered ({width}×{height})");

    public const string AlternateLeftLogLine = "Alternate buffer left";

    /// <summary><c>Resized 240×60 → 200×50</c>: the screen rebuilt from the store. Pinned.</summary>
    public static string ResizedLogLine(int fromWidth, int fromHeight, int width, int height) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Resized {fromWidth}×{fromHeight} → {width}×{height}");

    /// <summary>The transcript region shows an earlier window of the store, the pane pinned under it.</summary>
    public bool Scrolled
    {
        get
        {
            lock (_gate)
            {
                return _top >= 0;
            }
        }
    }

    /// <summary>The store row on the region's first row while scrolled, −1 at the bottom (tests).</summary>
    public int ScrollTop
    {
        get
        {
            lock (_gate)
            {
                return _top;
            }
        }
    }

    /// <summary>The rows the pane keeps of the transcript, at the window's width (tests).</summary>
    public int StoredRows
    {
        get
        {
            lock (_gate)
            {
                return _store.Rows(Width).Count;
            }
        }
    }

    /// <summary>
    /// The rows under the region's last row while scrolled — the store's, and the live block's not
    /// yet committed (a reply streams on below while the user reads) — 0 at the bottom.
    /// </summary>
    public int RowsBelow
    {
        get
        {
            lock (_gate)
            {
                return RowsBelowLocked();
            }
        }
    }

    /// <summary>Scrolls the region by <paramref name="pages"/> pages (a page = the region's rows less one; negative = up, towards the start).</summary>
    public void ScrollPage(int pages) => ScrollBy(pages * Math.Max(1, ScrolledRegionRows - 1));

    /// <summary>The rows a wheel notch scrolls: Windows' own lines-per-notch default (<c>InfoPane.WheelLines</c> is the pane's own).</summary>
    public const int WheelRows = 3;

    /// <summary>Scrolls the region by <paramref name="notches"/> wheel notches (positive = away from the user = up, towards the start), <see cref="WheelRows"/> rows each.</summary>
    public void ScrollWheel(int notches) => ScrollBy(-notches * WheelRows);

    /// <summary>
    /// Scrolls the region by <paramref name="rows"/> (negative = up, towards the start), clamped to
    /// the store: a store no taller than the region never leaves the bottom, and a scroll down
    /// that reaches the last row is the bottom again (the flow written back, the live block shown).
    /// While the pane is lifted (a batch, a modal) only the anchor moves; the next draw paints it.
    /// Disabled: nothing.
    /// </summary>
    public void ScrollBy(int rows)
    {
        if (!Enabled)
        {
            return;
        }

        lock (_gate)
        {
            int count = _store.Rows(Width).Count;
            if (_top < 0 && FitsAtTheBottom(count))
            {
                return;
            }

            int max = Math.Max(0, count - ScrolledRegionRows);
            int current = _top >= 0 ? _top : max;
            int top = Math.Clamp(current + rows, 0, max);
            int next = top >= max ? -1 : top;
            if (next == _top)
            {
                return;
            }

            _top = next;
            Redraw();
        }
    }

    /// <summary>The bottom again (Ctrl+End on the line or under a turn): the flow written back where the window was; nothing while not scrolled.</summary>
    public void ScrollToEnd()
    {
        if (!Enabled)
        {
            return;
        }

        lock (_gate)
        {
            if (_top < 0)
            {
                return;
            }

            _top = -1;
            Redraw();
        }
    }

    /// <summary>
    /// The transcript's first rows in the region — Ctrl+Home (2026-09-18), <see cref="ScrollToEnd"/>'s
    /// mirror: nothing on a transcript that fits, nothing at the top already; the hint row keeps
    /// its <see cref="ScrolledHint"/> wording (the user's call: no Ctrl+Home in it).
    /// </summary>
    public void ScrollToTop()
    {
        if (!Enabled)
        {
            return;
        }

        lock (_gate)
        {
            int count = _store.Rows(Width).Count;
            int max = Math.Max(0, count - ScrolledRegionRows);
            if (FitsAtTheBottom(count) || max == 0 || _top == 0)
            {
                return;
            }

            _top = 0;
            Redraw();
        }
    }

    /// <summary>The hint row's text while scrolled: <c>⇡ 12 rows below · PgUp/PgDn scroll · Ctrl+End bottom</c>, <c>1 row</c> singular. Pinned.</summary>
    public static string ScrolledHint(int below) =>
        "⇡ " + below.ToString(System.Globalization.CultureInfo.InvariantCulture) + (below == 1 ? " row below" : " rows below") + HintSeparator + "PgUp/PgDn scroll" + HintSeparator + "Ctrl+End bottom";

    /// <summary>The transcript region's rows over a pane of <paramref name="paneRows"/>: the window less the pane, one at least. Pinned.</summary>
    public static int RegionRows(int height, int paneRows) => Math.Max(1, height - paneRows);

    private int RegionRows(int paneRows) => RegionRows(Height, paneRows);

    /// <summary>
    /// The region a scrolled draw leaves (2026-10-01): the last draw's pane with the scroll's row in it — at the bottom
    /// the pane has none, and the anchor a scroll from there sets is counted against the region it will be drawn in.
    /// </summary>
    private int ScrolledRegionRows => RegionRows(_paneRows - _scrollRows + 1);

    /// <summary>
    /// A transcript of <paramref name="count"/> rows is whole on the screen at the bottom: there is nothing to scroll to,
    /// though the scroll's row would leave the scrolled region one row short of it (2026-10-01).
    /// </summary>
    private bool FitsAtTheBottom(int count) => count <= RegionRows(_paneRows - _scrollRows);

    /// <summary>
    /// The scroll's own row (2026-10-01, the user's ask and their pick of centered): <see cref="ScrolledHint"/> cut to
    /// <paramref name="cells"/> and centered in them by the blanks at its left, the odd one at its right. Pure, pinned.
    /// </summary>
    public static string ScrolledRow(int below, int cells)
    {
        cells = Math.Max(1, cells);
        string hint = Fit(ScrolledHint(below), cells);
        return new string(' ', (cells - TextCells.Width(hint)) / 2) + hint;
    }

    private int RowsBelowLocked()
    {
        if (_top < 0)
        {
            return 0;
        }

        return Math.Max(0, _store.Rows(Width).Count - (_top + RegionRows(_paneRows))) + _liveCount;
    }

    /// <summary>
    /// Shows <paramref name="content"/> in the live slot — the reply so far, the whole of it every
    /// time — in place of what was there. Nothing is drawn here: the tick lays it out and repaints,
    /// so a stream of tokens costs one layout per <see cref="Tick"/>. Disabled, nothing (the
    /// transcript keeps its plain path).
    /// </summary>
    public void SetLive(IRenderable content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!Enabled)
        {
            return;
        }

        lock (_gate)
        {
            _live = content;
            _liveDirty = true;
        }
    }

    /// <summary>
    /// The slot's content becomes transcript: its lines not yet committed are written into the flow
    /// (counted like any flow write), the slot is empty, the pane drawn under them. The reply's end,
    /// and what any flow write does first while a slot is open, so the order on the screen is the
    /// order of the calls.
    /// </summary>
    public void CommitLive()
    {
        if (!Enabled)
        {
            return;
        }

        lock (_gate)
        {
            if (_live is null)
            {
                return;
            }

            if (_modal > 0)
            {
                var lines = RenderLines(_live, _inner, Width);
                for (int i = _liveCommitted; i < lines.Count; i++)
                {
                    _inner.Write(new SegmentList(lines[i]));
                    _inner.WriteLine();
                }

                ForgetLive();
                return;
            }

            if (_top >= 0)
            {
                // Scrolled: into the store, the count below changed (a tool run it ended may have folded above it).
                FlushLive();
                if (_store.Reshaped)
                {
                    Redraw();
                }
                else
                {
                    RedrawScrollRow();
                }

                return;
            }

            bool sync = _batch == 0 && _drawn;
            if (sync)
            {
                BeginSync();
            }

            Lift();
            RestoreFlow();
            FlushLive();
            if (_batch == 0)
            {
                Draw();
            }

            if (sync)
            {
                EndSync();
            }
        }
    }

    /// <summary>The slot is emptied without writing anything (a reply that said nothing); its drawn rows are erased.</summary>
    public void DiscardLive()
    {
        if (!Enabled)
        {
            return;
        }

        lock (_gate)
        {
            if (_live is null)
            {
                return;
            }

            ForgetLive();
            if (_top >= 0)
            {
                // Scrolled: nothing of it was drawn; the count below changed.
                RedrawScrollRow();
                return;
            }

            if (_drawn && _batch == 0 && _modal == 0)
            {
                BeginSync();
                Lift();
                Draw();
                EndSync();
            }
        }
    }

    /// <summary>The input rows drawn by the last redraw (tests).</summary>
    public int InputRows
    {
        get
        {
            lock (_gate)
            {
                return _inputRows;
            }
        }
    }

    /// <summary>The input row the terminal's cursor is on, counted from the area's first drawn row (tests: a geometry that follows the cursor).</summary>
    public int CursorInputRow
    {
        get
        {
            lock (_gate)
            {
                return _cursorRow;
            }
        }
    }

    /// <summary>The input area's most rows on a window of <paramref name="height"/>: half of it, and never past the overlay's one-transcript-row rule. Pinned.</summary>
    public static int MaxInputRows(int height) => Math.Clamp(height / 2, 1, Math.Max(1, height - 4));

    /// <summary>
    /// The content rows an overlay may take on a window of <paramref name="height"/> over
    /// <paramref name="inputRows"/> input rows (0 without a slot): everything but one transcript
    /// row, the two rules and the hint. Pinned; the menus size their viewport by it.
    /// </summary>
    public static int MaxOverlayRows(int height, int inputRows) => Math.Max(0, height - 4 - inputRows);

    private int Width => Math.Max(1, _inner.Profile.Width);

    private int Height
    {
        get
        {
            int h = _inner.Profile.Height;
            return h > 0 ? h : 24;
        }
    }

    // ── IAnsiConsole ────────────────────────────────────────────────────────

    public Profile Profile => _inner.Profile;

    public IAnsiConsoleCursor Cursor => _inner.Cursor;

    /// <summary>
    /// Keys are read through <see cref="KeySource"/>, never here: on the screen the real reader is
    /// <see cref="WindowsConsoleInput"/>, and Spectre's own input would eat the mouse records while
    /// peeking. A prompt on the bare pane fails loudly instead.
    /// </summary>
    public IAnsiConsoleInput Input => Enabled ? throw new InvalidOperationException("Read keys through KeySource (a ConsoleWithInput over the pane), not the pane.") : _inner.Input;

    public IExclusivityMode ExclusivityMode => _inner.ExclusivityMode;

    public RenderPipeline Pipeline => _inner.Pipeline;

    /// <summary>A flow write: lift the pane, write, count the rows, draw the pane again.</summary>
    public void Write(IRenderable renderable) => WriteFlow(renderable, member: false);

    /// <summary>
    /// A flow write that is a line of the open tool run (<see cref="BeginToolGroup"/>; one is opened,
    /// keeping nothing, when none is): stored as the run's member, so a fold past the run's keep
    /// hides the earlier ones and the pane rebuilds the flow from the store (2026-09-22). Disabled,
    /// the plain write.
    /// </summary>
    public void WriteToolLine(IRenderable renderable) => WriteFlow(renderable, member: true);

    /// <summary>
    /// <see cref="WriteToolLine(IRenderable)"/>, joining the run's last unit when <paramref name="sameUnit"/> (2026-10-04: a call's result
    /// with its call, so <c>Tool collapse count</c> counts calls).
    /// </summary>
    public void WriteToolLine(IRenderable renderable, bool sameUnit)
    {
        _sameUnit = sameUnit;
        try
        {
            WriteFlow(renderable, member: true);
        }
        finally
        {
            _sameUnit = false;
        }
    }

    /// <summary>
    /// A flow write of pictures (later on 2026-09-24): stored with where each landed — <paramref name="renderable"/>'s
    /// <see cref="IPictureLayout.Spans"/> after the pane rendered it, one list per line — so <see cref="PictureAt"/>
    /// can say which picture a click hit. Disabled, the plain write.
    /// </summary>
    public void WritePictures(IPictureLayout renderable)
    {
        ArgumentNullException.ThrowIfNull(renderable);
        WriteFlow((IRenderable)renderable, member: false, renderable);
    }

    /// <summary>
    /// The picture under screen cell (<paramref name="x"/>, <paramref name="y"/>) in the transcript region (later on
    /// 2026-09-24): its id, or null — off the region, off a picture, the pane disabled, lifted, or with no geometry.
    /// The row is found as <see cref="TryToggleToolGroupAt"/> finds it. A tile of the picture strip answers too (later
    /// still on 2026-09-24, <see cref="TryHitStrip"/>), so both double-click paths open it with no hook of their own.
    /// </summary>
    public int? PictureAt(int x, int y)
    {
        if (!Enabled)
        {
            return null;
        }

        lock (_gate)
        {
            if (TryHitStrip(x, y, out int id))
            {
                return id;
            }

            return StoreRowAt(y) is int row ? _store.PictureAt(row, x) : null;
        }
    }

    /// <summary>
    /// Opens a tool run that keeps its last <paramref name="keep"/> lines while it runs
    /// (<see cref="Scrollback.BeginGroup"/>; 0 never folds). <paramref name="lead"/> is the reply's
    /// glyph when the run follows it bare; <paramref name="absorbOpenLine"/> when that glyph is
    /// already in the flow as an open line (the plain reply path), which the run's lead then
    /// replaces. Nothing is drawn until the run's first line. Disabled: nothing.
    /// </summary>
    public void BeginToolGroup(int keep, IRenderable? lead = null, bool absorbOpenLine = false)
    {
        if (!Enabled)
        {
            return;
        }

        lock (_gate)
        {
            var segments = lead?.GetSegments(_inner).ToList();
            _store.BeginGroup(keep, segments, absorbOpenLine);
        }
    }

    /// <summary>
    /// The open run's summary, folded and unfolded (<see cref="Scrollback.SetGroupSummary"/>); drawn
    /// with the run's next line or its end, never on its own. Disabled: nothing.
    /// </summary>
    public void SetToolGroupSummary(IRenderable collapsed, IRenderable expanded)
    {
        ArgumentNullException.ThrowIfNull(collapsed);
        ArgumentNullException.ThrowIfNull(expanded);
        if (!Enabled)
        {
            return;
        }

        lock (_gate)
        {
            _store.SetGroupSummary(collapsed.GetSegments(_inner).ToList(), expanded.GetSegments(_inner).ToList());
        }
    }

    /// <summary>The open tool run is over: a folded one shrinks to its summary on the screen. Disabled, or none open: nothing.</summary>
    public void EndToolGroup()
    {
        if (!Enabled)
        {
            return;
        }

        lock (_gate)
        {
            _store.EndGroup();
            RedrawIfReshaped();
        }
    }

    /// <summary>A tool run is open in the store (tests; the renderer's run ends it on its own boundaries).</summary>
    public bool ToolGroupOpen
    {
        get
        {
            lock (_gate)
            {
                return _store.GroupOpen;
            }
        }
    }

    /// <summary>Whether a tool run without its own state shows every line: Ctrl+O, <c>/expand</c> and <c>/collapse</c> set it for the session.</summary>
    public bool ToolGroupsExpanded
    {
        get
        {
            lock (_gate)
            {
                return _store.ExpandAll;
            }
        }
    }

    /// <summary>
    /// Every tool run unfolded (<paramref name="expanded"/>) or folded — <c>/expand</c>,
    /// <c>/collapse</c>, Ctrl+O's flip (<see cref="Scrollback.SetAllExpanded"/>): the runs
    /// on the screen change at once, the ones to come follow. Disabled: nothing.
    /// </summary>
    public void SetToolGroupsExpanded(bool expanded)
    {
        if (!Enabled)
        {
            return;
        }

        lock (_gate)
        {
            _store.SetAllExpanded(expanded);
            RedrawIfReshaped();
        }
    }

    /// <summary>Ctrl+O (2026-09-22): <see cref="SetToolGroupsExpanded"/> the other way from what it is.</summary>
    public void ToggleToolGroups() => SetToolGroupsExpanded(!ToolGroupsExpanded);

    /// <summary>
    /// A left click at buffer cell (<paramref name="x"/>, <paramref name="y"/>) on a tool run's
    /// summary row (2026-09-22) unfolds that run, or folds it again: true, and the screen shows it.
    /// False off a summary — any other transcript row, the pane, the live reply, the padding —
    /// when the pane is lifted, or when the console cannot say where its cursor is. The row is
    /// measured up from the upper rule (<see cref="CursorDepth"/> + 1 rows over the cursor), over the
    /// picture strip when it is drawn (2026-09-25): the region's rows are the store's from <c>_top</c> while scrolled, else the flow's tail ending on
    /// the flow cursor's row.
    /// </summary>
    public bool TryToggleToolGroupAt(int x, int y)
    {
        if (!Enabled)
        {
            return false;
        }

        lock (_gate)
        {
            if (StoreRowAt(y) is not int row || _store.GroupAtRow(row) is not int id || !_store.Toggle(id))
            {
                return false;
            }

            RedrawIfReshaped();
            return true;
        }
    }

    /// <summary>
    /// The store row screen row <paramref name="y"/> shows in the transcript region, or null (lifted, mid-batch or
    /// modal, no geometry, off the region, below the flow's end). Under the lock.
    /// </summary>
    private int? StoreRowAt(int y)
    {
        if (!_drawn || _batch > 0 || _modal > 0 || _geometry?.CursorTop() is not int top)
        {
            return null;
        }

        // The picture strip and its rule sit between the region and the upper rule (2026-09-25, the user's report: a click
        // on a tool run's summary did nothing while the strip showed — every row was read the strip's height too high).
        // The scroll's row (2026-10-01) over them while scrolled.
        int region = RegionRows(_paneRows);
        int r = y - (top - CursorDepth - 1 - _stripRows - _scrollRows - region);
        if (r < 0 || r >= region)
        {
            return null;
        }

        int count = _store.Rows(Width).Count;
        int row;
        if (_drawnScrolled)
        {
            row = _top + r;
        }
        else
        {
            int last = _col > 0 ? _row : _row - 1;
            if (_blank || r > last)
            {
                return null;
            }

            row = count - 1 - (last - r);
        }

        return row < 0 || row >= count ? null : row;
    }

    /// <summary>After a store change above the flow's end: the screen drawn again from the store (the draw rebuilds the flow), unless a batch or a modal will.</summary>
    private void RedrawIfReshaped()
    {
        if (_store.Reshaped && _drawn)
        {
            Redraw();
        }
    }

    private void WriteFlow(IRenderable renderable, bool member, IPictureLayout? pictures = null)
    {
        ArgumentNullException.ThrowIfNull(renderable);
        if (!Enabled)
        {
            _inner.Write(renderable);
            return;
        }

        lock (_gate)
        {
            if (_modal > 0)
            {
                // A menu is drawing itself at the flow end; it erases its own region when it closes.
                _inner.Write(renderable);
                return;
            }

            var segments = renderable.GetSegments(_inner).ToList();
            // A picture's spans are known once it is rendered (later on 2026-09-24): the store tags its lines with them.
            var spans = pictures?.Spans;
            // A diff's fold (2026-10-04): the store makes its lines a group that folds once the run is over.
            var fold = renderable is IFoldLayout { Fold: { } layout }
                ? new Scrollback.FoldSpec(layout.Head, layout.Keep, layout.Size, layout.Collapsed.GetSegments(_inner).ToList(), layout.Expanded.GetSegments(_inner).ToList())
                : null;
            if (_top >= 0)
            {
                // Scrolled: the store takes it, the screen shows the window; the count below changed
                // (and a run that folded above it redraws the window).
                FlushLive();
                EmitAs(segments, member, spans, fold);
                if (_store.Reshaped)
                {
                    Redraw();
                }
                else
                {
                    RedrawScrollRow();
                }

                return;
            }

            bool sync = _batch == 0 && _drawn;
            if (sync)
            {
                BeginSync();
            }

            Lift();
            RestoreFlow();
            FlushLive();
            EmitAs(segments, member, spans, fold);
            if (_batch == 0)
            {
                Draw();
            }

            if (sync)
            {
                EndSync();
            }
        }
    }

    /// <summary>
    /// The one way flow segments leave the pane: at the bottom written to the console and counted
    /// (<see cref="Track"/>, which stores them too); scrolled, stored alone.
    /// </summary>
    private void Emit(List<Segment> segments)
    {
        if (_top >= 0)
        {
            Store(segments);
            return;
        }

        _inner.Write(new SegmentList(segments));
        Track(segments);
    }

    /// <summary><see cref="Emit"/> with the store told whether the segments are a tool run's line, where pictures are and what folds.</summary>
    private void EmitAs(List<Segment> segments, bool member, IReadOnlyList<IReadOnlyList<PictureSpan>>? pictures = null, Scrollback.FoldSpec? fold = null)
    {
        _member = member;
        _pictureSpans = pictures;
        _foldSpec = fold;
        try
        {
            Emit(segments);
        }
        finally
        {
            _member = false;
            _pictureSpans = null;
            _foldSpec = null;
        }
    }

    // Set around EmitAs for a picture's write (later on 2026-09-24): the spans the store tags its lines with.
    private IReadOnlyList<IReadOnlyList<PictureSpan>>? _pictureSpans;

    // Set around EmitAs for a diff's write (2026-10-04): what of it folds.
    private Scrollback.FoldSpec? _foldSpec;

    // Set around EmitAs: the segments being stored are the open tool run's line.
    private bool _member;

    // The tool write joins the run's last unit (WriteToolLine's sameUnit, 2026-10-04).
    private bool _sameUnit;

    /// <summary>The segments into the store; a scrolled anchor follows the rows the cap dropped.</summary>
    private void Store(List<Segment> segments)
    {
        _store.ContinueUnit = _member && _sameUnit;
        int dropped = _store.Append(segments, Width, _member, _pictureSpans, _foldSpec);
        _store.ContinueUnit = false;
        if (_top >= 0 && dropped > 0)
        {
            _top = Math.Max(0, _top - dropped);
        }
    }

    /// <summary>
    /// The flow back on a blank screen (a scrolled draw or a resize erased it): the store's last
    /// rows, as many as the region holds, written from the top row and counted like any flow
    /// write — the open last line without its break, so the flow cursor sits at its end. At the
    /// bottom only; nothing when the flow is already there.
    /// </summary>
    private void RestoreFlow()
    {
        if (!_blank || _top >= 0)
        {
            return;
        }

        _blank = false;
        var rows = _store.Rows(Width);
        int region = RegionRows(_paneRows);
        int first = Math.Max(0, rows.Count - region);
        _row = 0;
        _col = 0;
        _lineFull = false;
        for (int i = first; i < rows.Count; i++)
        {
            var segments = new List<Segment>(rows[i].Count + 1);
            segments.AddRange(rows[i]);
            bool last = i == rows.Count - 1;
            if (!last || !_store.LastLineOpen)
            {
                segments.Add(Segment.LineBreak);
            }

            _inner.Write(new SegmentList(segments));
            Count(segments);
        }
    }

    public void WriteAnsi(Action<AnsiWriter> write) => _inner.WriteAnsi(write);

    /// <summary>The screen is wiped: the flow starts at the top and the pane is off the screen until the next write.</summary>
    public void Clear(bool home)
    {
        _inner.Clear(home);
        if (!Enabled)
        {
            return;
        }

        lock (_gate)
        {
            _row = 0;
            _col = 0;
            _lineFull = false;
            _drawn = false;
            _drawnScrolled = false;
            _pad = 0;
            _liveRows = 0;
            _store.Clear();
            _top = -1;
            _blank = false;
            ForgetLive();
        }
    }

    // ── The pane ────────────────────────────────────────────────────────────

    /// <summary>Puts the pane on the screen if it is not there (the screen's start).</summary>
    public void Show()
    {
        if (!Enabled)
        {
            return;
        }

        lock (_gate)
        {
            if (!_drawn && _batch == 0 && _modal == 0)
            {
                Draw();
            }
        }
    }

    /// <summary>
    /// Several flow writes as one: the pane is lifted once, the writes are counted, and the pane is
    /// drawn once when the scope ends (the screen clear + banner of <c>/clear</c>).
    /// </summary>
    public IDisposable Batch()
    {
        if (!Enabled)
        {
            return Scope.None;
        }

        lock (_gate)
        {
            if (_batch++ == 0 && _drawn)
            {
                // One synchronized frame from the lift to the draw (2026-09-29, the user's report: the hint row, the
                // toolbar and the performance bar flickered at every turn's end — the reply's commit is a batch, and its
                // erase and redraw went out as dozens of writes with nothing holding them).
                BeginSync();
                _batchSync = true;
                Lift();
            }
        }

        return new Scope(() =>
        {
            lock (_gate)
            {
                if (--_batch == 0)
                {
                    if (_modal == 0)
                    {
                        Draw();
                    }

                    if (_batchSync)
                    {
                        _batchSync = false;
                        EndSync();
                    }
                }
            }
        });
    }

    // The outer batch opened a synchronized frame (it began drawn): its end closes it.
    private bool _batchSync;

    /// <summary>
    /// A menu at the flow end: the pane is lifted, <paramref name="work"/> draws and reads keys on
    /// its own (a Spectre prompt erases its region when it closes, so the flow cursor is where it
    /// was), then the pane is drawn again. Writes during the work are not counted.
    /// </summary>
    public async Task<T> ModalAsync<T>(Func<Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (!Enabled)
        {
            return await work().ConfigureAwait(false);
        }

        lock (_gate)
        {
            if (_modal++ == 0 && _drawn)
            {
                Lift();
            }
        }

        try
        {
            return await work().ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (--_modal == 0 && _batch == 0)
                {
                    // The pane back as one frame (2026-09-29); the lift above cannot be held — the work draws in between.
                    BeginSync();
                    Draw();
                    EndSync();
                }
            }
        }
    }

    /// <summary>
    /// <see cref="ModalAsync{T}"/> on the pane <paramref name="console"/> renders to — itself, or
    /// the inner console of a <see cref="ConsoleWithInput"/> — and plainly the work when there is
    /// none. The menus call this around every Spectre prompt.
    /// </summary>
    public static Task<T> ModalAsync<T>(IAnsiConsole console, Func<Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(work);
        return console switch
        {
            ScreenPane pane => pane.ModalAsync(work),
            ConsoleWithInput wrapped => ModalAsync(wrapped.Inner, work),
            _ => work(),
        };
    }

    /// <summary>
    /// The overlay: <paramref name="content"/> drawn in place of the input row (the rule stays above
    /// it, <paramref name="hint"/> below it in the hint row, the cursor hidden), the pane growing
    /// upward to fit — the info pane. Content beyond the window is cut at the bottom, one transcript
    /// row always kept. Calling it again replaces the content (a tab switch); the renderable is
    /// kept and laid out again on every redraw, so a resize follows. With <paramref name="input"/>
    /// the input rows stay under the content and the cursor on them (a typed settings edit): the
    /// draft is drawn there as on the plain pane, and what it submits is not written into the flow.
    /// With <paramref name="close"/> the first row ends in the <see cref="CloseGlyph"/> (a menu, an
    /// info pane — never the input line's completion list, which has no title row) and
    /// <see cref="TryHitClose"/> answers a click on it. Disabled: nothing.
    /// </summary>
    public void ShowOverlay(IRenderable content, string hint, bool input = false, bool close = false)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(hint);
        if (!Enabled)
        {
            return;
        }

        lock (_gate)
        {
            _overlay = new Overlay(content, hint, input, close);
            _dismissHit = null;
            Redraw();
        }
    }

    /// <summary>The input row and the standing hint again, the cursor shown.</summary>
    public void CloseOverlay()
    {
        if (!Enabled)
        {
            return;
        }

        lock (_gate)
        {
            Dismissed = false;
            if (_overlay is null)
            {
                return;
            }

            // The drawn rows stay until the redraw: the lift steps up over them (Draw resets them).
            _overlay = null;
            Redraw();
        }
    }

    /// <summary>The pane again from the flow cursor, unless a batch or a modal will draw it when it ends.</summary>
    private void Redraw()
    {
        if (_batch > 0 || _modal > 0)
        {
            return;
        }

        bool sync = _drawn;
        if (sync)
        {
            BeginSync();
        }

        Lift();
        Draw();
        if (sync)
        {
            EndSync();
        }
    }

    /// <summary>
    /// A spinner in the hint row with <paramref name="label"/> and the time since this call
    /// (<see cref="BusyText"/>) until the scope is disposed; <see cref="BusyScope.SetLabel"/> changes
    /// the label meanwhile and the count runs on. The transcript may be written under it: every
    /// write draws the pane, busy row included. Disabled: a scope that does nothing.
    /// </summary>
    public BusyScope BeginBusy(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        if (!Enabled)
        {
            return BusyScope.None;
        }

        int slot;
        lock (_gate)
        {
            if (_busyLabels.Count == 0)
            {
                _busySince = _time.GetTimestamp();
                _stageSince = _busySince;
                _frame = 0;
            }

            // Nested (a menu's spinner under a running turn): the inner label shows, the count runs
            // on from the outer's start, and the scope's end brings the outer label back. The scope
            // owns its slot, not its label: SetLabel renames the slot, shown only while it is the top.
            _busyLabels.Add(label);
            slot = _busyLabels.Count - 1;
            _busyLabel = label;
            RedrawHint();
            RefreshGhost();
        }

        return new BusyScope(this, slot);
    }

    /// <summary>The scope's label, from any thread; nothing once the scope ended. Shown when the scope is the top one; the count is not restarted.</summary>
    private void SetBusyLabel(int slot, string label)
    {
        lock (_gate)
        {
            if (slot >= _busyLabels.Count)
            {
                return;
            }

            _busyLabels[slot] = label;
            if (slot == _busyLabels.Count - 1)
            {
                if (!string.Equals(_busyLabel, label, StringComparison.Ordinal))
                {
                    _stageSince = _time.GetTimestamp();   // a new step: its own clock starts (2026-10-04)
                }

                _busyLabel = label;
                RedrawHint();
            }
        }
    }

    /// <summary>The scope's end: its slot removed, the label under it back, once.</summary>
    private void EndBusy(int slot)
    {
        lock (_gate)
        {
            if (slot < _busyLabels.Count)
            {
                _busyLabels.RemoveAt(slot);
            }

            _busyLabel = _busyLabels.Count > 0 ? _busyLabels[^1] : null;
            RedrawHint();
            RefreshGhost();
        }
    }

    /// <summary>
    /// A live spinner (<see cref="BeginBusy"/>): <see cref="SetLabel"/> renames it while it runs — a
    /// turn's stage, a download's progress, "transcribing…" — from any thread, and <see cref="Dispose"/>
    /// ends it. The scope renames its own slot, so a stage change under a nested menu spinner never
    /// touches the menu's label and shows once the menu's scope ends. Ended or disabled: nothing.
    /// </summary>
    public sealed class BusyScope : IDisposable
    {
        /// <summary>The scope a disabled pane hands out: every call a no-op.</summary>
        public static readonly BusyScope None = new(null, -1);

        private ScreenPane? _pane;
        private readonly int _slot;

        internal BusyScope(ScreenPane? pane, int slot)
        {
            _pane = pane;
            _slot = slot;
        }

        /// <summary>The spinner's label from now on; the count runs on.</summary>
        public void SetLabel(string label)
        {
            ArgumentNullException.ThrowIfNull(label);
            _pane?.SetBusyLabel(_slot, label);
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _pane, null)?.EndBusy(_slot);
        }
    }

    /// <summary>
    /// The placeholder's row again when the spinner's start or end changed whether it applies
    /// (the empty row under a spinner is bare; the idle row shows the ghost text): the one-row
    /// in-place rewrite blanks or restores it and puts the cursor back. Gated like <see cref="RedrawHint"/>.
    /// </summary>
    private void RefreshGhost()
    {
        if (!_drawn || _batch > 0 || _modal > 0 || _overlay is not null)
        {
            return;
        }

        if (GhostApplies() == _shownGhostCells > 0)
        {
            return;
        }

        var shown = LayoutInput(Width, Height - BarRows - _stripRows - _scrollRows);
        if (shown.Rows.Count == _inputRows)
        {
            RewriteInputRows(shown);
        }
        else
        {
            Redraw();
        }
    }

    /// <summary>Whether the empty row shows the placeholder now: a sentence set, an empty draft, no overlay, no spinner.</summary>
    private bool GhostApplies() => _placeholder.Length > 0 && _text.Length == 0 && _overlay is null && _busyLabel is null;

    /// <summary>The placeholder as the row shows it: cut to the row's cells at <paramref name="width"/> (<see cref="Fit"/>), so no row ever fills the width. Pinned.</summary>
    public static string PlaceholderRow(string placeholder, int width)
    {
        ArgumentNullException.ThrowIfNull(placeholder);
        return Fit(placeholder, InputLine.AvailableCells(width));
    }

    /// <summary>The spinner's label beside its elapsed time: <c>thinking 00:12</c>, <c>01:02:03</c> past an hour (<see cref="ElapsedText.Countdown"/>, the timer line's shape). Pinned.</summary>
    public static string BusyText(string label, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(label);
        return label + " " + ElapsedText.Countdown(elapsed);
    }

    /// <summary>
    /// <see cref="BusyText(string, TimeSpan)"/> with the step's own time first when the label has changed since the spinner started
    /// (2026-10-04, the UI review: a long tool showed only the turn's clock): <c>🛠️ read_file 00:03 · 00:12</c>; null is the bare form. Pinned.
    /// </summary>
    public static string BusyText(string label, TimeSpan elapsed, TimeSpan? step) =>
        step is { } own ? BusyText(label, own) + HintSeparator + ElapsedText.Countdown(elapsed) : BusyText(label, elapsed);

    /// <summary>The dim part a turn's busy row ends with while no pane is open over it (2026-10-04, the UI review). Pinned.</summary>
    public const string StopHint = "esc to stop";

    /// <summary>
    /// Whether the busy row ends with <see cref="StopHint"/> (2026-10-04): the screen says so while its turn is the one running — not
    /// for a menu's own spinner or a download, which ESC does not stop. False by default.
    /// </summary>
    public Func<bool> StopHintShown
    {
        get => _stopHintShown;
        set => _stopHintShown = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Whether the busy row shows the step's own clock beside the turn's (2026-10-04, <see cref="BusyText(string, TimeSpan, TimeSpan?)"/>):
    /// the screen says so while a tool runs; a thinking or writing stage, or any other spinner, keeps the one clock. False by default.
    /// </summary>
    /// <summary>The setting <c>User line style</c> as the screen reads it (2026-10-04): how a sent line is kept in the transcript (<see cref="InputLine.SubmittedMarkup"/>).</summary>
    public Func<string> UserLineStyle
    {
        get => _userLineStyle;
        set => _userLineStyle = value ?? throw new ArgumentNullException(nameof(value));
    }

    public Func<bool> StepClockShown
    {
        get => _stepClockShown;
        set => _stepClockShown = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// The busy row's text past the frame: <see cref="BusyText"/>, and behind <see cref="HintSeparator"/>
    /// the overlay's own hint when one is open under the spinner (<c>thinking 00:12 · ESC closes · ←/→ tabs · ↑/↓ scroll</c>:
    /// a pane opened mid-turn needs its keys named, ESC closing it rather than the turn). An empty
    /// <paramref name="overlayHint"/> is the bare <see cref="BusyText"/>. Pinned.
    /// </summary>
    public static string BusyRow(string label, TimeSpan elapsed, string overlayHint) => BusyRow(label, elapsed, overlayHint, "");

    /// <summary>
    /// <see cref="BusyRow(string, TimeSpan, string)"/> with the <paramref name="queued"/> part between the
    /// label and the overlay's hint (<c>thinking 00:12 · 📤 2 queued</c>, 2026-09-18); empty = the row as before. Pinned.
    /// </summary>
    public static string BusyRow(string label, TimeSpan elapsed, string overlayHint, string queued) => BusyRow(label, elapsed, overlayHint, queued, "");

    /// <summary>
    /// <see cref="BusyRow(string, TimeSpan, string, string)"/> with the token tally right after the label (2026-09-25,
    /// <see cref="BusyUsage"/>): <c>thinking 00:12 · ~5.1k / 151.4k · ~3% · ~41 tok/s · 📤 2 queued</c> — beside the
    /// spinner, so the two make one zone; empty = the row as before. Pinned.
    /// </summary>
    public static string BusyRow(string label, TimeSpan elapsed, string overlayHint, string queued, string usage) => BusyRow(label, elapsed, overlayHint, queued, usage, labelAfterUsage: false);

    /// <summary>
    /// <see cref="BusyRow(string, TimeSpan, string, string, string)"/> with the label and its count after the tally when
    /// <paramref name="labelAfterUsage"/> (<see cref="LabelAfterUsage"/>, 2026-09-25): <c>1.2k / 4.1k · 30% · 🎨 00:12 · 📤 2 queued</c>. Pinned.
    /// </summary>
    public static string BusyRow(string label, TimeSpan elapsed, string overlayHint, string queued, string usage, bool labelAfterUsage)
    {
        ArgumentNullException.ThrowIfNull(overlayHint);
        ArgumentNullException.ThrowIfNull(queued);
        ArgumentNullException.ThrowIfNull(usage);
        return HintRow(HintRow(Labelled(BusyText(label, elapsed), usage, labelAfterUsage), queued), overlayHint);
    }

    /// <summary>The spinner's label beside the tally, in the order <paramref name="labelAfterUsage"/> says.</summary>
    private static string Labelled(string busyText, string usage, bool labelAfterUsage) =>
        labelAfterUsage ? HintRow(usage, busyText) : HintRow(busyText, usage);

    /// <summary>
    /// The hint row's whole text while a picture is dragged off the strip or the transcript toward the chat line
    /// (2026-09-28, the user's ask and wording, <see cref="InputLine.DropOnLineHint"/>): it stands in for the standing row
    /// and the busy row alike, spinner and tally included — a drag lasts a moment — until null takes it back. No zone of the
    /// row answers a click meanwhile. Any thread; disabled: nothing.
    /// </summary>
    public void SetDragHint(string? text)
    {
        if (!Enabled)
        {
            return;
        }

        lock (_gate)
        {
            if (string.Equals(text, _dragHint, StringComparison.Ordinal))
            {
                return;
            }

            _dragHint = text;
            RedrawHint();
        }
    }

    /// <summary>The text <see cref="SetDragHint"/> put on the hint row, or null.</summary>
    public string? DragHint
    {
        get { lock (_gate) { return _dragHint; } }
    }

    /// <summary>
    /// The hint row's whole text for <paramref name="lasts"/> (2026-10-01, the kill switch's first press, the user's ask: a
    /// second press within the window unloads the embedded model): a warning that must be seen wherever the key was read,
    /// so it stands in for the standing row, a pane's hint and the busy row alike, as <see cref="SetDragHint"/>'s does (a drag
    /// still wins over it). The tick takes it back once it lapses; null takes it back at once. No zone of the row answers a
    /// click meanwhile. Any thread; disabled: nothing.
    /// </summary>
    public void SetAlertHint(string? text, TimeSpan lasts) => SetAlertHint(text, lasts, flash: false);

    /// <summary>
    /// A passing note on the hint row (2026-10-04, the UI review: a copy said nothing): <see cref="SetAlertHint(string?, TimeSpan)"/>'s
    /// slot for <see cref="FlashLasts"/>, in the hint's style rather than the warning's. Any thread; disabled: nothing.
    /// </summary>
    public void Flash(string text) => SetAlertHint(text, FlashLasts, flash: true);

    /// <summary>How long <see cref="Flash"/> stands. Pinned.</summary>
    public static readonly TimeSpan FlashLasts = TimeSpan.FromSeconds(2);

    private void SetAlertHint(string? text, TimeSpan lasts, bool flash)
    {
        if (!Enabled)
        {
            return;
        }

        lock (_gate)
        {
            _alertFlash = flash;
            _alertUntil = text is null ? 0 : _time.GetUtcNow().UtcTicks + lasts.Ticks;
            if (string.Equals(text, _alertHint, StringComparison.Ordinal))
            {
                return;
            }

            _alertHint = text;
            RedrawHint();
        }
    }

    /// <summary>The text <see cref="SetAlertHint"/> put on the hint row while it stands, or null.</summary>
    public string? AlertHint
    {
        get { lock (_gate) { return _alertHint; } }
    }

    /// <summary>Redraws the hint row if the standing hint changed (a state change with no transcript line).</summary>
    public void RefreshHint()
    {
        if (!Enabled)
        {
            return;
        }

        lock (_gate)
        {
            if (_busyLabel is null && HintChanged())
            {
                RedrawHint();
            }

            if (ToolbarChanged())
            {
                RedrawToolbar();
            }

            if (PerfChanged())
            {
                RedrawPerf();
            }
        }
    }

    /// <summary>
    /// The screen's end: the cursor under the pane, and the alternate buffer left (the shell's
    /// screen comes back as it was, the transcript gone with the buffer), for whatever follows the app.
    /// </summary>
    public void Close()
    {
        if (!Enabled)
        {
            return;
        }

        lock (_gate)
        {
            if (_drawn)
            {
                // From the cursor's row to the pane's last row (the hint row, or the toolbar under it): over the input rows below it and the lower rule, or the overlay's rows and it.
                _inner.Cursor.Move(CursorDirection.Down, LastRowBelowCursor);
                _inner.WriteLine();
                _drawn = false;
                _drawnScrolled = false;
                if (_overlay is not null)
                {
                    if (!_drawnInput)
                    {
                        _inner.Cursor.Show(true);
                    }

                    _overlay = null;
                    _overlayRows = 0;
                    _drawnOverlay = false;
                    _drawnInput = false;
                    _drawnClose = false;
                    Dismissed = false;
                    _dismissHit = null;
                    _dismissChord = null;
                }
            }

            LeaveAlternateLocked();
        }
    }

    /// <summary>The alternate buffer left, once (<see cref="Close"/>, or a dispose that never saw a close — the crash path).</summary>
    private void LeaveAlternateLocked()
    {
        if (!_inAlternate)
        {
            return;
        }

        if (_pageBackgroundSent is not null)
        {
            _inner.Write(PageBackgroundReset);
            _pageBackgroundSent = null;
        }

        _inner.Write(LeaveAlternate);
        _inAlternate = false;
        DiagnosticLog.Debug(Category, AlternateLeftLogLine);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            while (_syncDepth > 0)
            {
                EndSync();   // nothing held past the pane's end
            }

            LeaveAlternateLocked();
        }

        _timer?.Dispose();
    }

    // ── The input row ───────────────────────────────────────────────────────

    /// <summary>
    /// The start of a read: the prompt glyph is written where the cursor is (disabled), or the
    /// row is already on the screen (enabled). Either way the row's bookkeeping starts over.
    /// </summary>
    public void BeginInput()
    {
        if (!Enabled)
        {
            _inner.Write(new RawText(InputLine.PromptGlyph, Theme.User));
            _renderedCells = 0;
            _cursorCell = 0;
            return;
        }

        lock (_gate)
        {
            if (!_drawn && _batch == 0 && _modal == 0)
            {
                Draw();
            }
        }
    }

    /// <summary>
    /// The input area shows the draft <paramref name="text"/> with the cursor at the UTF-16 index
    /// <paramref name="cursor"/>. Enabled, the draft is word-wrapped over the pane's rows
    /// (<see cref="InputLayout.Wrap"/>): the same row count is rewritten in place, a different one
    /// lifts and draws the pane again; under an overlay without an input slot, or while the pane is
    /// lifted, the draft is only remembered. Disabled, the single row shows the slice <see cref="InputLine.Layout"/>
    /// chooses, cells the previous slice used beyond it blanked, the cursor on the row on entry.
    /// <paramref name="anchor"/> is the other end of the line's selection (−1, or the cursor itself,
    /// = none): the stretch between it and the cursor is drawn in <see cref="Theme.SelectedText"/>
    /// on the enabled pane's rows; the disabled row never shows it (its writes stay as they were).
    /// <paramref name="labels"/> are the stretches of <paramref name="text"/> that stand for pasted
    /// blocks (<see cref="PasteBlocks.LabelRanges"/>), drawn in <see cref="Theme.PasteLabel"/> where
    /// the selection does not cover them; null = none.
    /// </summary>
    public void ShowInput(string text, int cursor, int anchor = -1, IReadOnlyList<(int Start, int Length)>? labels = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_gate)
        {
            ShowInputLocked(text, cursor, anchor, labels);
        }
    }

    /// <summary>The keys typed ahead during a turn, shown on the input rows as they will read when the turn ends (enabled only); <paramref name="labels"/> as in <see cref="ShowInput"/>.</summary>
    public void PreviewInput(string text, IReadOnlyList<(int Start, int Length)>? labels = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!Enabled)
        {
            return;
        }

        lock (_gate)
        {
            if (!_drawn || _modal > 0 || _batch > 0 || _overlay is not null)
            {
                return;
            }

            ShowInputLocked(text, text.Length, -1, labels);
        }
    }

    /// <summary>
    /// Enter: the row becomes the transcript's <c>› text</c> line. Disabled, the row is rewritten
    /// from its first cell as a normal markup line (it may wrap now) and ended; enabled, the line
    /// is written into the flow (the lift steps up from the cursor's row over the whole area) and
    /// the pane comes back with one empty row. Under an overlay with an input slot the submission
    /// is the overlay's (a settings value): nothing is written, the slot is emptied.
    /// <paramref name="preview"/>, when not empty, is the start of the line's collapsed pastes
    /// (<see cref="InputLine.PreviewText"/>), written under the line in the same write, dim
    /// (<see cref="InputLine.PreviewMarkup"/>).
    /// </summary>
    public void CommitInput(string submitted, string preview = "")
    {
        ArgumentNullException.ThrowIfNull(submitted);
        ArgumentNullException.ThrowIfNull(preview);
        string under = preview.Length == 0 ? "" : InputLine.PreviewMarkup(preview) + Environment.NewLine;
        if (!Enabled)
        {
            _inner.Cursor.Move(CursorDirection.Left, TextCells.Width(InputLine.PromptGlyph) + _cursorCell);
            _inner.Write(new Markup(InputLine.SubmittedMarkup(submitted, _userLineStyle())));
            int stale = _renderedCells - TextCells.Width(submitted);
            if (stale > 0)
            {
                _inner.Write(new RawText(new string(' ', stale)));
            }

            _inner.WriteLine();
            if (under.Length > 0)
            {
                _inner.Write(new Markup(under));
            }

            ResetInputRow();
            return;
        }

        lock (_gate)
        {
            if (_overlay is { Input: true })
            {
                ShowInputLocked("", 0);
                return;
            }

            ResetInputRow();
            // A sent line is the bottom: the flow comes back under the window before the line joins it.
            if (_top >= 0)
            {
                _top = -1;
                Redraw();
            }
        }

        Write(new Markup(InputLine.SubmittedMarkup(submitted, _userLineStyle()) + Environment.NewLine + under));
    }

    /// <summary>
    /// The read ended without a submission (ESC, the wake word, an alert, no keyboard). Disabled,
    /// the cursor steps past the row and the row stays in the transcript; enabled, the area is
    /// simply emptied (back to one row).
    /// </summary>
    public void ClearInput()
    {
        if (!Enabled)
        {
            _inner.Cursor.Move(CursorDirection.Right, _renderedCells - _cursorCell);
            _inner.WriteLine();
            ResetInputRow();
            return;
        }

        lock (_gate)
        {
            ShowInputLocked("", 0);
        }
    }

    // ── Measuring ───────────────────────────────────────────────────────────

    /// <summary>The rows <paramref name="renderable"/> takes at <paramref name="width"/> on <paramref name="console"/>.</summary>
    public static int MeasureRows(IRenderable renderable, IAnsiConsole console, int width) =>
        RenderLines(renderable, console, width).Count;

    /// <summary>
    /// <paramref name="renderable"/> laid out at <paramref name="width"/> on <paramref name="console"/>, one
    /// <see cref="SegmentLine"/> per row: the layout the overlay draw does, for a caller that shows a
    /// window of it (the info pane's viewport).
    /// </summary>
    public static List<SegmentLine> RenderLines(IRenderable renderable, IAnsiConsole console, int width)
    {
        ArgumentNullException.ThrowIfNull(renderable);
        ArgumentNullException.ThrowIfNull(console);
        var options = RenderOptions.Create(console, console.Profile.Capabilities);
        return Segment.SplitLines(renderable.Render(options, width));
    }

    // ── Internals (all under _gate) ─────────────────────────────────────────

    private void ShowInputLocked(string text, int cursor, int anchor = -1, IReadOnlyList<(int Start, int Length)>? labels = null)
    {
        if (!Enabled)
        {
            ShowSingleRow(text, cursor);
            return;
        }

        _text = text;
        _cursor = Math.Clamp(cursor, 0, text.Length);
        _anchor = anchor < 0 || anchor == _cursor ? -1 : Math.Min(anchor, text.Length);
        _labels = labels ?? Array.Empty<(int, int)>();
        if (!_drawn || _batch > 0 || _modal > 0 || _overlay is { Input: false })
        {
            // Under an overlay without a slot, or lifted for a menu or a batch: remembered, drawn when the pane comes back.
            return;
        }

        int firstRow = _firstRow;
        var shown = LayoutInput(Width, Height - BarRows - _stripRows - _scrollRows);
        if (_stripRows > 0 && (text.Length == 0) != _drawnStripHighlight)
        {
            // The draft emptied or filled under a highlighted strip: the highlight follows it, the whole pane again.
            Redraw();
        }
        else if (shown.Rows.Count == _inputRows && firstRow == _firstRow)
        {
            RewriteInputRows(shown);
            // A hint that reads the draft (the splash hint, 2026-09-20) follows it on the same
            // key, not at the next tick; under an overlay's slot the standing hint is the overlay's.
            if (_busyLabel is null && HintChanged())
            {
                RedrawHint();
            }
        }
        else
        {
            // The area grows or shrinks: the padding and the flow row are Draw's business.
            Redraw();
        }
    }

    /// <summary>The disabled pane's row, where the cursor is: the slice around the cursor, the stale cells blanked.</summary>
    private void ShowSingleRow(string text, int cursor)
    {
        var (visible, cursorCell) = InputLine.Layout(text, cursor, InputLine.AvailableCells(Width));
        int visibleCells = TextCells.Width(visible);
        int available = InputLine.AvailableCells(Width);
        int pad = Math.Clamp(_renderedCells - visibleCells, 0, Math.Max(0, available - visibleCells));

        _inner.Cursor.Move(CursorDirection.Left, _cursorCell);
        _inner.Write(new RawText(pad > 0 ? visible + new string(' ', pad) : visible, Theme.User));
        _inner.Cursor.Move(CursorDirection.Left, visibleCells + pad - cursorCell);

        _renderedCells = visibleCells;
        _cursorCell = cursorCell;
    }

    /// <summary>
    /// The same number of rows again, in place: from the cursor's row up to the area's first row,
    /// each row's text rewritten after its prefix with the cells the previous text used beyond it
    /// blanked, then the cursor to its new row and cell. Nothing above the area moves.
    /// </summary>
    private void RewriteInputRows(ShownInput shown)
    {
        var (rows, cursorRow, cursorCol) = (shown.Rows, shown.CursorRow, shown.CursorCol);
        int available = InputLine.AvailableCells(Width);
        int prefix = TextCells.Width(InputLine.PromptGlyph);
        bool sync = rows.Count > 1;
        if (sync)
        {
            BeginSync();
            _inner.Cursor.Show(false);
        }

        _inner.Cursor.Move(CursorDirection.Up, _cursorRow);
        _inner.Cursor.Move(CursorDirection.Left, _cursorCell);
        int endCol = 0;
        int ghostCells = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0)
            {
                _inner.WriteLine();
                _inner.Write(new RawText(InputLine.ContinuationIndent, Theme.User));
            }

            // The first row of an empty draft carries the placeholder instead of the draft's
            // text; the cells the last draw gave the ghost count as the row's previous cells,
            // so the first key blanks them and a Backspace to nothing writes the ghost again.
            string row = rows[i];
            string? ghost = i == 0 && row.Length == 0 && GhostApplies() ? PlaceholderRow(_placeholder, Width) : null;
            int cells = ghost is null ? TextCells.Width(row) : TextCells.Width(ghost);
            int previous = (i < _shownRows.Count ? TextCells.Width(_shownRows[i]) : 0) + (i == 0 ? _shownGhostCells : 0);
            int pad = Math.Clamp(previous - cells, 0, Math.Max(0, available - cells));
            if (ghost is null)
            {
                WriteRowText(row, shown.Starts[i], pad);
            }
            else
            {
                _inner.Write(new RawText(pad > 0 ? ghost + new string(' ', pad) : ghost, Theme.Placeholder));
                ghostCells = cells;
            }

            endCol = cells + pad;
        }

        int up = rows.Count - 1 - cursorRow;
        if (up > 0)
        {
            _inner.Cursor.Move(CursorDirection.Up, up);
            ColumnZero();
            _inner.Cursor.Move(CursorDirection.Right, prefix + cursorCol);
        }
        else
        {
            _inner.Cursor.Move(CursorDirection.Left, endCol - cursorCol);
        }

        if (sync)
        {
            _inner.Cursor.Show(true);
            EndSync();
        }

        SetShown(shown, ghostCells);
    }

    /// <summary>The drawn state: what the rows show, where each starts in the draft, where the cursor is, and the cells the placeholder took on the first row (0 = none).</summary>
    private void SetShown(ShownInput shown, int ghostCells)
    {
        _shownRows = shown.Rows;
        _shownStarts = shown.Starts;
        _shownNext = shown.Next;
        _inputRows = shown.Rows.Count;
        _cursorRow = shown.CursorRow;
        _cursorCell = shown.CursorCol;
        _shownGhostCells = ghostCells;
    }

    /// <summary>The rows the area shows, each row's first index in the draft and the next row's start (−1 for the draft's last row), and the cursor among them.</summary>
    private sealed record ShownInput(List<string> Rows, List<int> Starts, List<int> Next, int CursorRow, int CursorCol);

    /// <summary>
    /// The draft laid out at <paramref name="width"/>, cut to the rows the window allows with the
    /// viewport moved just far enough to keep the cursor's row in view: the rows to draw, and the
    /// cursor's row and cell among them.
    /// </summary>
    private ShownInput LayoutInput(int width, int height)
    {
        var layout = InputLayout.Wrap(_text, _cursor, InputLine.AvailableCells(width));
        int shown = Math.Min(layout.Rows.Count, MaxInputRows(height));
        if (layout.CursorRow < _firstRow)
        {
            _firstRow = layout.CursorRow;
        }
        else if (layout.CursorRow >= _firstRow + shown)
        {
            _firstRow = layout.CursorRow - shown + 1;
        }

        _firstRow = Math.Clamp(_firstRow, 0, layout.Rows.Count - shown);
        var rows = new List<string>(shown);
        var starts = new List<int>(shown);
        var next = new List<int>(shown);
        for (int i = 0; i < shown; i++)
        {
            int r = _firstRow + i;
            rows.Add(layout.Rows[r]);
            starts.Add(layout.Starts[r]);
            next.Add(r + 1 < layout.Starts.Count ? layout.Starts[r + 1] : -1);
        }

        return new ShownInput(rows, starts, next, layout.CursorRow - _firstRow, layout.CursorCol);
    }

    /// <summary>
    /// Where in the draft a click at buffer cell (<paramref name="x"/>, <paramref name="y"/>)
    /// lands: the UTF-16 index of the element under it, the row's end past its last character.
    /// False when the click is not on a drawn input row (the transcript, a rule, the hint), when
    /// the pane is lifted or under an overlay without an input slot (an overlay's own rows answer
    /// to <see cref="TryHitOverlay"/>), or when the console cannot say where the cursor is.
    /// The click's row is measured from <see cref="ScreenGeometry.CursorTop"/>: the terminal's
    /// cursor is on the area's row <c>_cursorRow</c>, so the area starts <c>_cursorRow</c> rows above it.
    /// </summary>
    public bool TryHitInput(int x, int y, out int index) => TryHitInput(x, y, out index, out _);

    /// <summary>
    /// <see cref="TryHitInput(int, int, out int)"/>, and the display index of the element the pointer is on
    /// (<paramref name="under"/>, 2026-09-30, the double-click's word): the same as <paramref name="index"/> on a
    /// character, the row's last element past its end — never the space a word wrap dropped there, nor the next row's
    /// start — and the row's end on an empty row.
    /// </summary>
    public bool TryHitInput(int x, int y, out int index, out int under)
    {
        index = 0;
        under = 0;
        if (!Enabled)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_drawn || _batch > 0 || _modal > 0 || _overlay is { Input: false } || _geometry?.CursorTop() is not int top)
            {
                return false;
            }

            int r = y - (top - _cursorRow);
            if (r < 0 || r >= _inputRows)
            {
                return false;
            }

            string row = _shownRows[r];
            int col = Math.Max(0, x - TextCells.Width(InputLine.PromptGlyph));
            // The walk is InputLayout's (shared with the Up/Down row moves since 2026-09-21): past a
            // row broken by cells, the last character of this row is what the click meant.
            index = _shownStarts[r] + InputLayout.IndexInRow(row, col, _shownNext[r] >= 0 && _shownStarts[r] + row.Length == _shownNext[r]);
            under = row.Length > 0 && col >= TextCells.Width(row)
                ? _shownStarts[r] + row.Length - TextCells.ElementLengthBefore(row, row.Length)
                : index;
            return true;
        }
    }

    /// <summary>
    /// The draft's caret one row up or down (2026-09-21, <paramref name="delta"/> −1 / +1) at cell
    /// column <paramref name="preferredCol"/> (−1 = the caret's own column): the display index it lands
    /// on (<see cref="InputLayout.IndexAt"/>) and the column used, which the line keeps as its goal
    /// column across a run of arrows. False — the key is history — when the pane is off, the draft
    /// is one row, or the target row is outside it. Laid out from the held text at the current width
    /// under the gate, as the draw does; nothing drawn.
    /// </summary>
    public bool TryStepInputRow(int delta, int preferredCol, out int index, out int col)
    {
        index = 0;
        col = 0;
        if (!Enabled)
        {
            return false;
        }

        lock (_gate)
        {
            var layout = InputLayout.Wrap(_text, _cursor, InputLine.AvailableCells(Width));
            int target = layout.CursorRow + delta;
            if (layout.Rows.Count < 2 || target < 0 || target >= layout.Rows.Count)
            {
                return false;
            }

            col = preferredCol < 0 ? layout.CursorCol : preferredCol;
            index = layout.IndexAt(target, col);
            return true;
        }
    }

    /// <summary>
    /// Which drawn overlay content row a click at buffer cell (<paramref name="x"/>, <paramref name="y"/>)
    /// lands on (0 = the overlay's first line; the column does not matter). False when no overlay
    /// is drawn, the click is off its rows (the transcript, a rule, an input slot, the hint), the
    /// pane is lifted, or the console cannot say where the cursor is. The overlay's first row is
    /// <see cref="CursorDepth"/> rows above the terminal's cursor: 0 without a slot (the hidden
    /// cursor rests on that very row), the overlay's rows plus the cursor's input row with one.
    /// </summary>
    public bool TryHitOverlay(int x, int y, out int row)
    {
        row = 0;
        if (!Enabled)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_drawn || !_drawnOverlay || _batch > 0 || _modal > 0 || _geometry?.CursorTop() is not int top)
            {
                return false;
            }

            int r = y - (top - CursorDepth);
            if (r < 0 || r >= _overlayRows)
            {
                return false;
            }

            row = r;
            return true;
        }
    }

    /// <summary>
    /// Whether a click at buffer cell (<paramref name="x"/>, <paramref name="y"/>) lands on the
    /// drawn overlay's <see cref="CloseGlyph"/> (2026-09-18): its first row, from the cell before
    /// the glyph to the screen's last column — three cells, a target for a mouse. False when no
    /// glyph was drawn (no overlay, one shown without <c>close</c>, no room), when the pane is lifted, or when the console cannot say where the cursor is.
    /// The readers treat a hit as the ESC key.
    /// </summary>
    public bool TryHitClose(int x, int y)
    {
        if (!Enabled)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_drawn || !_drawnOverlay || _closeColumn < 0 || _batch > 0 || _modal > 0 || _geometry?.CursorTop() is not int top)
            {
                return false;
            }

            return y == top - CursorDepth && x >= _closeColumn - 1 && x <= Math.Max(_closeColumn, _lastWidth - 1);
        }
    }

    /// <summary>
    /// Whether a click at buffer cell (<paramref name="x"/>, <paramref name="y"/>) lands off the
    /// drawn pane — the transcript above it, either rule, the hint row — while an overlay shown
    /// with <c>close</c> is drawn (2026-09-18): a menu or an info pane, never the input line's
    /// completion list. False on the overlay's rows, on its input slot, when the pane is lifted,
    /// or when the console cannot say where the cursor is. Two such clicks within
    /// <see cref="DoubleClick.Interval"/> are the readers' <see cref="Dismiss"/>.
    /// </summary>
    public bool TryHitOutside(int x, int y)
    {
        if (!Enabled)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_drawn || !_drawnOverlay || !_drawnClose || _batch > 0 || _modal > 0 || _geometry?.CursorTop() is not int top)
            {
                return false;
            }

            int first = top - CursorDepth;
            int last = first + _overlayRows + (_drawnInput ? _inputRows : 0) - 1;
            return y < first || y > last;
        }
    }

    /// <summary>
    /// Whether a click at buffer cell (<paramref name="x"/>, <paramref name="y"/>) lands on the
    /// drawn hint row — the pane's last row, any column (2026-09-18: the input line's double-click
    /// there opens the settings). False under an overlay (that hint row is the overlay's, and the
    /// overlay reads the click), when the pane is lifted, or when the console cannot say where the
    /// cursor is. The hint row is <see cref="HintRowBelowCursor"/> rows under the terminal's
    /// cursor, the offset <see cref="RedrawHint"/> writes it at.
    /// </summary>
    public bool TryHitHint(int x, int y) => TryHitHint(x, y, out _);

    /// <summary>
    /// <see cref="TryHitHint(int, int)"/> naming the part of the row under the click
    /// (2026-09-18): <see cref="HintZone.Strip"/> with the speech glyph and its first column,
    /// <see cref="HintZone.Trailer"/> over the model name at the right edge and <see cref="HintZone.Mark"/>
    /// over its reasoning mark on the last cells (2026-09-21), <see cref="HintZone.Usage"/> over the token tally (2026-09-21), <see cref="HintZone.Row"/>
    /// anywhere else — the separators between the glyphs included.
    /// The zones are those of the standing row as last drawn; under the busy row the strip keeps its
    /// glyphs (2026-09-24, the brain's mid-turn cancel), the spinner and its label are
    /// <see cref="HintZone.Usage"/> and every other hit is the row.
    /// While the transcript is scrolled the scroll's own row over the upper rule (<see cref="ScrolledRow"/>, 2026-10-01)
    /// answers too, <see cref="HintZone.Scrolled"/> at any column, so a double-click on the scroll's hint is the bottom
    /// again; the hint row keeps its zones then (until that day it was the scroll's, its row <see cref="HintZone.Scrolled"/>).
    /// </summary>
    public bool TryHitHint(int x, int y, out HintHit hit)
    {
        hit = default;
        if (!Enabled)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_drawn || _drawnOverlay || _batch > 0 || _modal > 0 || _geometry?.CursorTop() is not int top)
            {
                return false;
            }

            if (_scrollRows > 0 && y == top - ScrollRowAboveCursor)
            {
                hit = new HintHit(HintZone.Scrolled, "", -1);
                return true;
            }

            if (y != top + HintRowBelowCursor)
            {
                return false;
            }

            hit = HintHitAt(_hintStrip, _trailerColumn, _markColumn, _busyLabel is null ? _queuedColumn : -1, _queuedCells, _usageColumn, _usageCells, _labelColumn, _labelCells, x);
            return true;
        }
    }

    /// <summary>
    /// Whether a click at buffer cell (<paramref name="x"/>, <paramref name="y"/>) lands on the
    /// queued part of the drawn hint row (<see cref="Queued"/>, 2026-09-18) — the standing row's or
    /// the busy row's, the one hit test that answers under a turn, so the watcher can open
    /// <c>/queue</c> from a double-click there. <see cref="TryHitHint(int, int)"/>'s guards otherwise.
    /// </summary>
    public bool TryHitQueued(int x, int y)
    {
        if (!Enabled)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_drawn || _drawnOverlay || _batch > 0 || _modal > 0 || _geometry?.CursorTop() is not int top)
            {
                return false;
            }

            return y == top + HintRowBelowCursor && _queuedColumn >= 0 && x >= _queuedColumn && x < _queuedColumn + _queuedCells;
        }
    }

    /// <summary>
    /// The zone of column <paramref name="x"/> on a standing row whose strip is <paramref name="strip"/>
    /// (from column 0) and whose trailer starts at <paramref name="trailerColumn"/> (−1 for none). Pinned.
    /// </summary>
    public static HintHit HintHitAt(string strip, int trailerColumn, int x) => HintHitAt(strip, trailerColumn, -1, 0, x);

    /// <summary>
    /// <see cref="HintHitAt(string, int, int)"/> with the queued part's place (2026-09-18): the
    /// <paramref name="queuedCells"/> from <paramref name="queuedColumn"/> (−1 for none) are
    /// <see cref="HintZone.Queued"/>, its first column the hit's. Until 2026-10-01 a <c>scrolled</c> flag made the row
    /// <see cref="HintZone.Scrolled"/>; the scroll's hint has its own row since (<see cref="ScrolledRow"/>). Pinned.
    /// </summary>
    public static HintHit HintHitAt(string strip, int trailerColumn, int queuedColumn, int queuedCells, int x) =>
        HintHitAt(strip, trailerColumn, queuedColumn, queuedCells, -1, 0, x);

    /// <summary>
    /// <see cref="HintHitAt(string, int, int, int, int)"/> with the usage zone's place
    /// (2026-09-21): the <paramref name="usageCells"/> from <paramref name="usageColumn"/> (−1 for
    /// none) are <see cref="HintZone.Usage"/>, its first column the hit's — the token tally on the
    /// standing row, the spinner and its label on the busy row; behind the trailer and the queued
    /// part, ahead of the strip. Pinned.
    /// </summary>
    public static HintHit HintHitAt(string strip, int trailerColumn, int queuedColumn, int queuedCells, int usageColumn, int usageCells, int x) =>
        HintHitAt(strip, trailerColumn, -1, queuedColumn, queuedCells, usageColumn, usageCells, x);

    /// <summary>
    /// <see cref="HintHitAt(string, int, int, int, int, int, int)"/> with the mark's place
    /// (2026-09-21): from <paramref name="markColumn"/> (−1 for none) to the row's end is
    /// <see cref="HintZone.Mark"/>, its first column the hit's, ahead of the trailer — which is
    /// then the name and the separator before the mark. Pinned.
    /// </summary>
    public static HintHit HintHitAt(string strip, int trailerColumn, int markColumn, int queuedColumn, int queuedCells, int usageColumn, int usageCells, int x) =>
        HintHitAt(strip, trailerColumn, markColumn, queuedColumn, queuedCells, usageColumn, usageCells, -1, 0, x);

    /// <summary>
    /// <see cref="HintHitAt(string, int, int, int, int, int, int, int)"/> with the label's place (2026-09-28): the
    /// <paramref name="labelCells"/> from <paramref name="labelColumn"/> (−1 for none) are <see cref="HintZone.Label"/>, its
    /// first column the hit's — inside the usage zone on the busy row, so ahead of it. Pinned.
    /// </summary>
    public static HintHit HintHitAt(string strip, int trailerColumn, int markColumn, int queuedColumn, int queuedCells, int usageColumn, int usageCells, int labelColumn, int labelCells, int x)
    {
        ArgumentNullException.ThrowIfNull(strip);
        if (markColumn >= 0 && x >= markColumn)
        {
            return new HintHit(HintZone.Mark, "", markColumn);
        }

        if (trailerColumn >= 0 && x >= trailerColumn)
        {
            return new HintHit(HintZone.Trailer, "", trailerColumn);
        }

        if (queuedColumn >= 0 && x >= queuedColumn && x < queuedColumn + queuedCells)
        {
            return new HintHit(HintZone.Queued, "", queuedColumn);
        }

        if (labelColumn >= 0 && x >= labelColumn && x < labelColumn + labelCells)
        {
            return new HintHit(HintZone.Label, "", labelColumn);
        }

        if (usageColumn >= 0 && x >= usageColumn && x < usageColumn + usageCells)
        {
            return new HintHit(HintZone.Usage, "", usageColumn);
        }

        if (TryStripGlyphAt(strip, x, out string glyph, out int column))
        {
            return new HintHit(HintZone.Strip, glyph, column);
        }

        return new HintHit(HintZone.Row, "", -1);
    }

    /// <summary>
    /// The glyph of <paramref name="strip"/> (from column 0) under column <paramref name="x"/> and
    /// its first column, walking the strip's elements by their cell width; false on a blank, a
    /// separator, or past the strip. The hint row's strip and the toolbar's share it. A variation
    /// selector (U+FE0F/U+FE0E) after a glyph belongs to it — <c>⚙️</c> is one two-cell glyph, not a
    /// gear and a selector cell (later on 2026-09-21, for the toolbar's gear, tools and detective),
    /// so the glyph handed back is the whole string a caller compares against.
    /// </summary>
    private static bool TryStripGlyphAt(string strip, int x, out string glyph, out int column)
    {
        column = 0;
        int i = 0;
        while (i < strip.Length && column <= x)
        {
            int width = TextCells.ElementWidth(strip, i, out int length);
            length = Math.Max(1, length);
            if (i + length < strip.Length && strip[i + length] is '\uFE0F' or '\uFE0E')
            {
                width += TextCells.ElementWidth(strip, i + length, out int selector);
                length += selector;
            }

            if (x < column + width && !string.IsNullOrWhiteSpace(strip.AsSpan(i, length).ToString()))
            {
                glyph = strip.Substring(i, length);
                return true;
            }

            column += width;
            i += length;
        }

        glyph = "";
        column = -1;
        return false;
    }

    /// <summary>
    /// Whether a click at buffer cell (<paramref name="x"/>, <paramref name="y"/>) lands on the
    /// drawn toolbar (2026-09-21) — the pane's last row while one is drawn — naming the part under
    /// it (<see cref="ToolbarHitAt"/>). It answers under the busy row (the pane glyphs work under
    /// a reply, as the tally does) and while scrolled; false under an overlay (the row is off the
    /// pane there: a double-click is the overlay's dismiss, <see cref="TryHitOutside"/>, and
    /// <see cref="OffPaneHitAt"/> names the part for the screen's switch), when neither the
    /// toolbar nor the performance bar is drawn, when the pane is lifted, or when the console cannot say where the cursor is.
    /// The performance bar is the toolbar's blanks anywhere on it (<see cref="PerfBarHit"/>; 2026-09-29, the user's ask),
    /// with the toolbar or without it.
    /// </summary>
    public bool TryHitToolbar(int x, int y, out ToolbarHit hit)
    {
        hit = default;
        if (!Enabled)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_drawn || _drawnOverlay || _toolbarRows + _perfRows == 0 || _batch > 0 || _modal > 0 || _geometry?.CursorTop() is not int top)
            {
                return false;
            }

            if (_perfRows > 0 && y == top + LastRowBelowCursor)
            {
                hit = PerfBarHit;
                return true;
            }

            if (_toolbarRows == 0 || y != top + ToolbarRowBelowCursor)
            {
                return false;
            }

            hit = ToolbarHitAt(_toolbarStrip, _toolbarPathColumn, _toolbarPathCells, x);
            return true;
        }
    }

    /// <summary>
    /// The zone of column <paramref name="x"/> on a toolbar whose strip is <paramref name="strip"/>
    /// (from column 0) and whose path takes <paramref name="pathCells"/> from
    /// <paramref name="pathColumn"/> (−1 for none): a strip glyph with its first column, else the
    /// path with its first, else the row. Pinned.
    /// </summary>
    public static ToolbarHit ToolbarHitAt(string strip, int pathColumn, int pathCells, int x)
    {
        ArgumentNullException.ThrowIfNull(strip);
        if (TryStripGlyphAt(strip, x, out string glyph, out int column))
        {
            return new ToolbarHit(ToolbarZone.Glyph, glyph, column);
        }

        if (pathColumn >= 0 && x >= pathColumn && x < pathColumn + pathCells)
        {
            return new ToolbarHit(ToolbarZone.Path, "", pathColumn);
        }

        return new ToolbarHit(ToolbarZone.Row, "", -1);
    }

    /// <summary>
    /// A console write that changes nothing on the screen, for the moment the console's input
    /// mode was changed: ConPTY applies a mode change with the next write, so without one the
    /// mouse would not be taken or handed back until the next keystroke drew something. The hint
    /// row rewritten in place when the pane is drawn, else the two synchronized-output codes.
    /// </summary>
    public void Touch()
    {
        if (!Enabled)
        {
            return;
        }

        lock (_gate)
        {
            if (_drawn && _batch == 0 && _modal == 0)
            {
                RedrawHint();
            }
            else
            {
                BeginSync();
                EndSync();
            }
        }
    }

    /// <summary>What a drawn row starts with: the glyph on the draft's first row, the indent on every other.</summary>
    private static string Prefix(int textRow) => textRow == 0 ? InputLine.PromptGlyph : InputLine.ContinuationIndent;

    /// <summary>
    /// One input row's text after its prefix, <paramref name="pad"/> blanks after it: one write in
    /// <see cref="Theme.User"/> when neither the selection nor a paste label touches the row (byte
    /// for byte what it always was), else the runs <see cref="RowRuns"/> gives, the blanks on the last.
    /// <paramref name="rowStart"/> is the row's first index in the draft.
    /// </summary>
    private void WriteRowText(string row, int rowStart, int pad)
    {
        string blanks = pad > 0 ? new string(' ', pad) : "";
        var runs = RowRuns(row, rowStart, _anchor < 0 ? -1 : Math.Min(_anchor, _cursor), _anchor < 0 ? -1 : Math.Max(_anchor, _cursor), _labels);
        for (int i = 0; i < runs.Count; i++)
        {
            var (text, style) = runs[i];
            if (i == runs.Count - 1)
            {
                text += blanks;
            }

            if (text.Length > 0)
            {
                _inner.Write(new RawText(text, style));
            }
        }
    }

    /// <summary>
    /// A drawn row as styled runs: the stretch of the draft's selection <c>[start, end)</c>
    /// (−1 = none) in <see cref="Theme.SelectedText"/>, the paste <paramref name="labels"/> (draft
    /// ranges) outside it in <see cref="Theme.PasteLabel"/>, everything else in <see cref="Theme.User"/>;
    /// a row nothing touches is one run. <paramref name="rowStart"/> is the row's first index in
    /// the draft. Empty runs are left out except that an empty row is one empty run. Pure, pinned.
    /// </summary>
    public static IReadOnlyList<(string Text, Style Style)> RowRuns(string row, int rowStart, int start, int end, IReadOnlyList<(int Start, int Length)> labels)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(labels);
        var runs = new List<(string, Style)>();
        if (row.Length == 0)
        {
            runs.Add(("", Theme.User));
            return runs;
        }

        var styles = new Style[row.Length];
        Array.Fill(styles, Theme.User);
        foreach (var (labelStart, length) in labels)
        {
            for (int i = Math.Max(0, labelStart - rowStart); i < Math.Min(row.Length, labelStart + length - rowStart); i++)
            {
                styles[i] = Theme.PasteLabel;
            }
        }

        if (start >= 0 && end > start)
        {
            for (int i = Math.Max(0, start - rowStart); i < Math.Min(row.Length, end - rowStart); i++)
            {
                styles[i] = Theme.SelectedText;
            }
        }

        int from = 0;
        for (int i = 1; i <= row.Length; i++)
        {
            if (i == row.Length || !styles[i].Equals(styles[from]))
            {
                runs.Add((row[from..i], styles[from]));
                from = i;
            }
        }

        return runs;
    }

    /// <summary>The draft forgotten. The drawn fields stay: the next lift steps up from where the cursor is.</summary>
    private void ResetInputRow()
    {
        _text = "";
        _cursor = 0;
        _anchor = -1;
        _labels = Array.Empty<(int, int)>();
        _firstRow = 0;
        _renderedCells = 0;
        if (!Enabled)
        {
            _cursorCell = 0;
        }
    }

    /// <summary>Segments written to the console at the flow cursor: counted, and kept in the store.</summary>
    private void Track(List<Segment> segments)
    {
        Count(segments);
        Store(segments);
    }

    /// <summary>Counts the rows and cells the segments took, with the terminal's deferred wrap (the store wraps by the same rule).</summary>
    private void Count(List<Segment> segments)
    {
        int w = Width;
        foreach (var segment in segments)
        {
            if (segment.IsControlCode)
            {
                continue;
            }

            if (segment.IsLineBreak)
            {
                _row++;
                _col = 0;
                _lineFull = false;
                continue;
            }

            string text = segment.Text;
            int i = 0;
            while (i < text.Length)
            {
                if (text[i] is '\r' or '\n')
                {
                    if (text[i] == '\n')
                    {
                        _row++;
                        _col = 0;
                        _lineFull = false;
                    }

                    i++;
                    continue;
                }

                int cells = TextCells.ElementWidth(text, i, out int length);
                if (_lineFull || _col + cells > w)
                {
                    _row++;
                    _col = cells;
                }
                else
                {
                    _col += cells;
                }

                _lineFull = _col >= w;
                i += Math.Max(1, length);
            }
        }

        _row = Math.Min(_row, Height - 1);
    }

    /// <summary>
    /// From the flow cursor: the padding, the rule, the overlay's rows if any, the input rows unless
    /// the overlay has no slot, the lower rule, the hint, then the cursor back into the area — on the
    /// cursor's input row, shown; or the overlay's first row, hidden. Either way it is
    /// <see cref="CursorDepth"/> rows under the upper rule, so <see cref="Lift"/> is one computation
    /// — over the drawn fields this draw sets last, so the next lift steps up from THIS shape even
    /// when a different one has already been requested.
    /// </summary>
    private void Draw()
    {
        int w = Width;
        int h = Height;

        // The overlay, and the draft, are laid out again on every draw, at the window's width; the
        // draft to MaxInputRows around the cursor, the overlay cut to what the window leaves over
        // one transcript row and the input rows it keeps.
        // The toolbar (2026-09-21) takes the screen's last row when the provider answers and the
        // window keeps a transcript row over the smallest pane; the caps below count over the
        // rows it leaves, and the offsets under the cursor add it (HintRowBelowCursor).
        var toolbar = _toolbar();
        int toolbarRows = toolbar is not null && h >= PaneRows + 2 ? 1 : 0;

        // The performance bar (2026-09-29) under the toolbar, the last row, when the window keeps a transcript row over
        // the smallest pane and the toolbar; from here on the bars count as one (barRows) wherever the toolbar did alone.
        var perf = _perf(Math.Max(1, w - 1));
        int perfRows = perf is not null && h >= PaneRows + 2 + toolbarRows ? 1 : 0;
        int barRows = toolbarRows + perfRows;

        // The picture strip (later still on 2026-09-24) over the upper rule, under a rule of its own (2026-09-25): its rows,
        // the rule's with them, count into the pane's, so the
        // padding, the lift and the region leave room for it; the input rows are capped over what it leaves.
        int stripRows = StripRowsFor(w, h, barRows);
        var strip = stripRows > 0 ? _pictureStrip() : null;
        List<SegmentLine>? stripLines = null;
        bool stripHighlight = _text.Length == 0;
        if (strip is not null)
        {
            var (lines, spans) = strip.Render(RenderOptions.Create(_inner, _inner.Profile.Capabilities), w - 1, stripHighlight);
            stripLines = lines;
            _stripSpans = spans;
            _drawnStripVersion = strip.Version;
        }
        else
        {
            stripRows = 0;
            _stripSpans = [];
            _stripButtonColumn = -1;
            _stripCloseColumn = -1;
        }

        _drawnStripHighlight = stripHighlight;

        // The scroll's row (2026-10-01) over the rest while scrolled, when the window keeps a transcript row over the
        // smallest pane, the bars, the strip and it; the input rows and the overlay are capped over what it leaves.
        int scrollRows = _top >= 0 && h >= PaneRows + barRows + stripRows + 2 ? 1 : 0;

        List<SegmentLine>? overlayLines = null;
        int overlayRows = 0;
        ShownInput? shown = null;
        if (_overlay is null || _overlay.Input)
        {
            shown = LayoutInput(w, h - barRows - stripRows - scrollRows);
        }

        int closeColumn = -1;
        if (_overlay is { } overlay)
        {
            overlayLines = Segment.SplitLines(overlay.Content.GetSegments(_inner), w);
            overlayRows = Math.Clamp(overlayLines.Count, 0, MaxOverlayRows(h - barRows - scrollRows, shown?.Rows.Count ?? 0));
            if (overlay.Close && overlayRows > 0)
            {
                // The close glyph in column w − 2 of the first row, TrailerGap cells clear of the
                // title or the strip; a first row that leaves no room (a strip wider than the
                // window) goes without.
                // TextCells, not Spectre's CellCount (later on 2026-09-21, the title glyphs): Spectre
                // counts a gear or a screen with its selector (⚙️ 🛠️ 🖥️) as one cell, the terminal two.
                int cells = TextCells.Width(string.Concat(overlayLines[0].Select(s => s.Text)));
                int glyph = TextCells.Width(CloseGlyph);
                if (cells + TrailerGap + glyph <= w - 1)
                {
                    closeColumn = w - 1 - glyph;
                    overlayLines[0].Add(new Segment(new string(' ', closeColumn - cells)));
                    overlayLines[0].Add(new Segment(CloseGlyph, Theme.DimText));
                }
            }
        }

        _paneRows = 3 + overlayRows + (shown?.Rows.Count ?? 0) + barRows + stripRows + scrollRows;
        _toolbarRows = toolbarRows;
        _perfRows = perfRows;
        _stripRows = stripRows;

        // Scrolled: the anchor against the region this pane leaves; at or past the last window it
        // is the bottom after all (a taller pane, a store that shrank) — and the scroll's row goes.
        IReadOnlyList<SegmentLine>? window = null;
        int region = RegionRows(_paneRows);
        if (_top >= 0)
        {
            window = _store.Rows(w);
            int max = Math.Max(0, window.Count - region);
            if (_top >= max)
            {
                _top = -1;
                window = null;
                _paneRows -= scrollRows;
                scrollRows = 0;
                region = RegionRows(_paneRows);
            }
        }

        _scrollRows = scrollRows;

        _inner.Cursor.Show(false);

        // A tool run folded or unfolded above the flow's end (2026-09-22): scrolled, the window is
        // painted from the store anyway; at the bottom the flow on the screen is stale and is
        // written again from the store, as a resize does.
        bool reshaped = _store.TakeReshaped();
        int liveRows = 0;
        if (window is not null)
        {
            // The region from the top row: the flow the screen held is erased (a lift from the
            // bottom shape left the cursor on it), the window's rows written in its place, the
            // live block left in its slot — its rows only counted, for the hint.
            if (!_blank)
            {
                _inner.Cursor.Move(CursorDirection.Up, _row);
                ColumnZero();
                _inner.Write(EraseDown);
                _blank = true;
            }

            for (int i = 0; i < region; i++)
            {
                _inner.Write(new SegmentList(window[_top + i]));
                _inner.WriteLine();
            }

            _liveCount = _live is null ? 0 : Math.Max(0, LiveLayout(w, region).Count - _liveCommitted);
            _pad = 0;
        }
        else
        {
            if (reshaped && !_blank)
            {
                _inner.Cursor.Move(CursorDirection.Up, _row);
                ColumnZero();
                _inner.Write(EraseDown);
                _blank = true;
            }

            RestoreFlow();
            if (_geometry is not null && _syncDepth > 0)
            {
                // The console answers for what it has received: a held frame's lift, clear and flow go out first
                // (2026-09-29, the user's report — /clear left the pane under the banner, the stale bottom row taken as
                // the flow's). The frame's synchronized-output codes keep it off the screen until its end.
                Frames?.Settle();
            }

            if (_geometry?.CursorRow() is int actual && actual >= 0 && actual < h && actual != _row)
            {
                DiagnosticLog.Debug(Category, $"Flow row corrected: counted {_row}, the console says {actual}.");
                _row = actual;
            }

            // The live block, laid out at the window's width: what does not fit above the pane is
            // committed into the flow first (the terminal scrolls it away like any transcript), the tail
            // is drawn under the flow and lifted with the pane next time.
            List<SegmentLine>? live = null;
            if (_live is ThinkingBlock { Streaming: true })
            {
                // Streaming thinking shows its tail (2026-09-26) and nothing of it is committed: the
                // whole block goes into the flow as one group when it folds (FlushLive).
                live = LiveLayout(w, region);
            }
            else if (_live is not null)
            {
                var lines = ReplyLayout(w, out var open, out int through, out int folding);
                _liveFolded = folding;
                int skip = Math.Min(_liveCommitted, lines.Count);

                // A code block still streaming keeps its label in the slot with its window (2026-09-27):
                // the excess commit stops at it, as a thinking block keeps its header. A closed one past
                // its keep goes in whole at once (through), and folds.
                int hold = open?.LabelRow ?? lines.Count;
                int excess = Math.Max(Math.Min(lines.Count - skip - region, hold - skip), through - skip);
                if (excess > 0)
                {
                    if (_overpaint)
                    {
                        // The flow's writes go over the old frame's rows: they are cleared first, as a lift would.
                        _inner.Write(EraseDown);
                    }

                    EndFlowRow();
                    CommitLiveRows(lines, skip, skip + excess, w, final: false);
                    if (skip + excess == through && _liveCodeSpan >= 0)
                    {
                        // The closed block is over: it folds now, not when the reply speaks again.
                        _store.EndGroup();
                        _liveCodeSpan = -1;
                    }

                    _liveCommitted += excess;
                    skip += excess;
                    if (_store.TakeReshaped())
                    {
                        // A code block the commit ended folded above the flow's end (2026-09-22):
                        // the flow is written again from the store, as a fold above does.
                        _inner.Cursor.Move(CursorDirection.Up, _row);
                        ColumnZero();
                        _inner.Write(EraseDown);
                        _blank = true;
                        RestoreFlow();
                    }
                }

                live = lines.GetRange(skip, lines.Count - skip);
                if (open is not null && live.Count > region)
                {
                    // A region under the label and the window: the window's top rows go, the label stays
                    // while there is room for it and one row more.
                    int label = Math.Max(0, open.BodyRow - skip);
                    if (region >= label + 1)
                    {
                        live.RemoveRange(label, live.Count - region);
                    }
                    else
                    {
                        live = live.GetRange(live.Count - region, region);
                    }
                }
            }

            liveRows = live?.Count ?? 0;
            _liveCount = liveRows;
            int start = _col > 0 ? _row + 1 : _row;
            _pad = Math.Max(0, h - _paneRows - start - liveRows);

            if (_col > 0)
            {
                EndRow();
            }

            if (live is not null)
            {
                foreach (var line in live)
                {
                    _inner.Write(new SegmentList(line));
                    EndRow();
                }
            }

            for (int i = 0; i < _pad; i++)
            {
                EndRow();
            }

            // Drawing past the last row scrolls the screen: the flow moves up with it.
            int overflow = start + liveRows + _paneRows - h;
            if (overflow > 0)
            {
                _row = Math.Max(0, _row - overflow);
            }
        }

        if (scrollRows > 0)
        {
            WriteScrollRow(w);
            EndRow();
        }
        else
        {
            _shownScroll = null;
        }

        if (stripLines is not null)
        {
            // Its rule first (2026-09-25), the input rows' own, with the viewer's button at its edge (2026-09-27).
            WriteStripRule(w);
            foreach (var line in stripLines)
            {
                _inner.Write(new SegmentList(line));
                EndRow();
            }
        }

        WriteUpperRule(w);
        if (overlayLines is not null)
        {
            for (int i = 0; i < overlayRows; i++)
            {
                _inner.Write(new SegmentList(overlayLines[i]));
                EndRow();
            }
        }

        int ghostCells = 0;
        if (shown is not null)
        {
            for (int i = 0; i < shown.Rows.Count; i++)
            {
                _inner.Write(new RawText(Prefix(_firstRow + i), Theme.User));
                if (i == 0 && shown.Rows[i].Length == 0 && GhostApplies())
                {
                    // The empty idle row: the placeholder after the glyph, in place of the draft.
                    string ghost = PlaceholderRow(_placeholder, w);
                    _inner.Write(new RawText(ghost, Theme.Placeholder));
                    ghostCells = TextCells.Width(ghost);
                }
                else
                {
                    WriteRowText(shown.Rows[i], shown.Starts[i], 0);
                }

                EndRow();
            }
        }

        WriteRule(w);
        WriteHintRow();
        if (toolbarRows > 0)
        {
            EndRow();
            WriteToolbarRow(toolbar!.Value, w);
        }
        else
        {
            ForgetToolbar();
        }

        if (perfRows > 0)
        {
            EndRow();
            WritePerfRow(perf!);
        }
        else
        {
            _shownPerf = null;
        }

        if (_overpaint)
        {
            // Whatever a taller old frame left under the pane's last row.
            _inner.Write(EraseDown);
        }

        _liveRows = liveRows;
        _liveDirty = false;

        // From the pane's last row (the toolbar's, when drawn) back up over the hint row and the
        // lower rule into the area: the overlay's first row when it has no slot, else the cursor's input row.
        _overlayRows = overlayRows;
        if (shown is null)
        {
            _cursorRow = 0;
            _inner.Cursor.Move(CursorDirection.Up, overlayRows + 1 + barRows);
            ColumnZero();
        }
        else
        {
            SetShown(shown, ghostCells);
            _inner.Cursor.Move(CursorDirection.Up, _inputRows - _cursorRow + 1 + barRows);
            ColumnZero();
            _inner.Cursor.Move(CursorDirection.Right, TextCells.Width(InputLine.PromptGlyph) + _cursorCell);
            _inner.Cursor.Show(true);
        }

        _lastWidth = w;
        _lastHeight = h;
        _drawnOverlay = _overlay is not null;
        _drawnInput = _overlay?.Input ?? false;
        _drawnClose = _overlay?.Close ?? false;
        _drawnScrolled = window is not null;
        _closeColumn = closeColumn;
        _drawn = true;
    }

    /// <summary>
    /// From the cursor's input row: back to the flow cursor, and everything from there to the end
    /// of the screen erased — or, drawn scrolled, back to the top row and the whole screen erased
    /// (the flow is not on it; the next draw at the bottom writes it back). Without
    /// <paramref name="erase"/> the cursor goes back all the same and the old frame stays for an
    /// over-painting draw to cover (<see cref="Overpaint"/>).
    /// </summary>
    private void Lift(bool erase = true)
    {
        if (!_drawn)
        {
            return;
        }

        if (_drawnScrolled)
        {
            LiftToTop();
            return;
        }

        // The area's first row is at start + pad + 1 and the cursor _cursorRow rows under it. A
        // full flow row (wrap pending) continues on the next row, at its first cell: the terminal
        // would have wrapped the next character there.
        int up;
        if (_lineFull)
        {
            up = _pad + 1;
            _row++;
            _col = 0;
            _lineFull = false;
        }
        else
        {
            up = _pad + 1 + (_col > 0 ? 1 : 0);
        }

        // The live rows sit between the flow and the padding: over them too, and they are gone
        // with the erase — the next draw lays the block out again.
        up += _liveRows;
        _liveRows = 0;

        // The strip's rows sit between the padding and the upper rule.
        up += _stripRows;

        _inner.Cursor.Show(false);
        _inner.Cursor.Move(CursorDirection.Up, up + CursorDepth);
        ColumnZero();
        if (_col > 0)
        {
            _inner.Cursor.Move(CursorDirection.Right, _col);
        }

        if (erase)
        {
            _inner.Write(EraseDown);
        }

        _drawn = false;
    }

    // An over-painting draw is under way (Overpaint): every row it writes clears what is left of the
    // old frame's row after it.
    private bool _overpaint;

    /// <summary>
    /// The pane drawn again over the frame on the screen, not after erasing it (2026-09-26, the user's
    /// report: the pane flickered under a streaming thinking block — each tick erased everything from
    /// the flow's end down and the rows were blank until the draw wrote them, which a terminal not
    /// holding synchronized output shows). Each row ends in an erase to its end, the screen below the
    /// last row is erased once, and no row is ever blank on the screen. Only where the new frame
    /// stands where the old one did: at the bottom, drawn, the flow not to be written again — else
    /// the erasing lift and draw.
    /// </summary>
    private void Overpaint()
    {
        if (_drawnScrolled || _top >= 0 || _blank || _store.Reshaped)
        {
            Lift();
            Draw();
            return;
        }

        Lift(erase: false);
        _overpaint = true;
        try
        {
            Draw();
        }
        finally
        {
            _overpaint = false;
        }
    }

    /// <summary>The end of a drawn row: the rest of the old frame's row cleared first when over-painting.</summary>
    private void EndRow()
    {
        if (_overpaint)
        {
            _inner.Write(EraseLineEnd);
        }

        _inner.WriteLine();
    }

    /// <summary>
    /// Drawn scrolled (the pane's last row on the screen's, the cursor <see cref="LastRowBelowCursor"/>
    /// rows above it): the cursor to the top row and the whole screen erased. Also the resize: the
    /// buffer is not reflowed, so the screen is rebuilt from the store.
    /// </summary>
    private void LiftToTop()
    {
        int up = Math.Max(0, _lastHeight - 1 - LastRowBelowCursor);
        _inner.Cursor.Show(false);
        _inner.Cursor.Move(CursorDirection.Up, up);
        ColumnZero();
        _inner.Write(EraseDown);
        _liveRows = 0;
        _blank = true;
        _drawn = false;
    }

    /// <summary>
    /// Lifted (or scrolled): the slot's lines not yet in the flow are written there (counted, or
    /// stored alone while scrolled), and the slot is empty. Nothing to write for an empty document.
    /// </summary>
    private void FlushLive()
    {
        if (_live is null)
        {
            return;
        }

        var lines = RenderLines(_live, _inner, Width);
        if (lines.Count > _liveCommitted)
        {
            EndFlowRow();
            CommitLiveRows(lines, _liveCommitted, lines.Count, Width, final: true);
        }
        else if (_liveCodeSpan >= 0)
        {
            _store.EndGroup();
        }
        else if (_live is ThinkingBlock thinking)
        {
            EndThinkingGroup(thinking);
        }

        ForgetLive();
    }

    /// <summary>
    /// The live block laid out at <paramref name="width"/> as the slot draws it: a streaming thinking
    /// block's <see cref="ThinkingBlock.Tail"/> for <paramref name="region"/> rows, anything else whole.
    /// </summary>
    private List<SegmentLine> LiveLayout(int width, int region)
    {
        return _live is ThinkingBlock { Streaming: true } thinking
            ? ThinkingBlock.Tail(RenderLines(thinking.LiveView(), _inner, width), region)
            : ReplyLayout(width, out _, out _, out _);
    }

    /// <summary>
    /// The live block laid out at <paramref name="width"/> for the slot (the thinking aside). A reply
    /// streaming a code block past its keep (2026-09-27, the user's ask: a long block scrolled the screen
    /// and flickered as it came) is laid out over its <see cref="ReplyBlock.LiveView"/> and cut to its
    /// <see cref="ReplyBlock.Tail"/>, <paramref name="open"/> that block's span; the rows above it are
    /// the whole reply's rows. <paramref name="through"/>: the end of the last closed code block past
    /// its keep not yet counted — to be committed whole this draw so it folds — or 0; <paramref name="folding"/>
    /// how many such blocks the reply has, the count the draw that commits them keeps.
    /// </summary>
    private List<SegmentLine> ReplyLayout(int width, out CodeSpan? open, out int through, out int folding)
    {
        open = null;
        through = 0;
        folding = _liveFolded;
        if (_live is not ReplyBlock { CodeKeep: > 0 } reply)
        {
            return RenderLines(_live!, _inner, width);
        }

        var view = reply.LiveView();
        var lines = RenderLines(view, _inner, width);
        var options = RenderOptions.Create(_inner, _inner.Profile.Capabilities);
        folding = reply.FoldingCode;
        if (folding > _liveFolded)
        {
            // Only the draw a block closes lays the blocks out again for their spans.
            var closed = view.CodeSpans(options, width).Where(s => !s.Open && s.SourceLines > reply.CodeKeep).ToList();
            if (closed.Count > 0)
            {
                through = Math.Min(closed[^1].End, lines.Count);
            }

        }

        open = view.OpenCodeSpan(options, width, lines.Count);
        return open is null ? lines : ReplyBlock.Tail(lines, open, reply.CodeKeep);
    }

    private void ForgetLive()
    {
        _live = null;
        _liveCommitted = 0;
        _liveDirty = false;
        _liveCount = 0;
        _liveCodeSpan = -1;
        _liveFolded = 0;
    }

    // The live reply's closed code blocks past their keep already committed whole (ReplyLayout).
    private int _liveFolded;

    // The live block's code block whose group is open in the store (its label row), −1 for none: a
    // block the excess commit cut in two carries on in its group at the next commit.
    private int _liveCodeSpan = -1;

    /// <summary>
    /// Rows <paramref name="from"/> to <paramref name="to"/> of the live block laid out at
    /// <paramref name="width"/> (<paramref name="lines"/>) into the flow. A reply with a
    /// <c>Code collapse count</c> (<see cref="ReplyBlock.CodeKeep"/>, 2026-09-22) stores each top-level
    /// code block as a group (<see cref="Scrollback.BeginCodeGroup"/>): the label row its summary, the
    /// body rows its members, so the block folds past the count once something else follows it —
    /// or here, when <paramref name="final"/> (the slot is emptied) ends it. A label row that does
    /// not read as the span's label (a reply the parse laid out differently) is a plain row.
    /// </summary>
    private void CommitLiveRows(List<SegmentLine> lines, int from, int to, int width, bool final)
    {
        if (_live is ThinkingBlock thinking)
        {
            CommitThinkingRows(thinking, lines, from, to, final);
            return;
        }

        IReadOnlyList<CodeSpan>? spans = null;
        int keep = 0;
        if (_live is ReplyBlock { CodeKeep: > 0 } reply)
        {
            keep = reply.CodeKeep;
            // Short of the end, the rows above a streaming code block: its window's spans are theirs, and cheaper.
            spans = (final ? reply : reply.LiveView()).CodeSpans(RenderOptions.Create(_inner, _inner.Profile.Capabilities), width);
        }

        for (int i = from; i < to; i++)
        {
            var span = spans?.FirstOrDefault(c => i >= c.LabelRow && i < c.End);
            if (span is not null && i == span.LabelRow && ReadsAs(lines[i], MarkdownView.CodeGlyph + span.Label))
            {
                EmitCodeLabel(lines[i], keep, span);
                _liveCodeSpan = span.LabelRow;
            }
            else if (span is not null && _liveCodeSpan == span.LabelRow && _store.CodeGroupOpen)
            {
                if (i == from)
                {
                    _store.SetCodeGroupSummary(CodeSummary(_store.CodeGroupLabel, span, expanded: false), CodeSummary(_store.CodeGroupLabel, span, expanded: true), span.SourceLines);
                }

                var segments = new List<Segment>(lines[i].Count + 1);
                segments.AddRange(lines[i]);
                segments.Add(Segment.LineBreak);
                EmitAs(segments, member: true);
            }
            else
            {
                WriteFlowLine(lines[i]);
                _liveCodeSpan = -1;
            }
        }

        if (final && _liveCodeSpan >= 0)
        {
            _store.EndGroup();
            _liveCodeSpan = -1;
        }
    }

    /// <summary>
    /// Rows <paramref name="from"/> to <paramref name="to"/> of a thinking block (2026-09-26) into the
    /// flow as a thinking group (<see cref="Scrollback.BeginThinkingGroup"/>): its header row the
    /// summary, written as a code label is, every other row a member. The excess commit of a long
    /// block carries on in the same group at the next commit; <paramref name="final"/> (the slot is
    /// emptied: the answer started) folds it to <see cref="ThinkingFoldText.Summary"/>.
    /// </summary>
    private void CommitThinkingRows(ThinkingBlock thinking, List<SegmentLine> lines, int from, int to, bool final)
    {
        for (int i = from; i < to; i++)
        {
            if (i == 0)
            {
                var segments = new List<Segment>(lines[0]);
                _store.BeginThinkingGroup(segments);
                if (_top < 0)
                {
                    segments.Add(Segment.LineBreak);
                    _inner.Write(new SegmentList(segments));
                    Count(segments);
                }
            }
            else if (_store.ThinkingGroupOpen)
            {
                var segments = new List<Segment>(lines[i].Count + 1);
                segments.AddRange(lines[i]);
                segments.Add(Segment.LineBreak);
                EmitAs(segments, member: true);
            }
            else
            {
                WriteFlowLine(lines[i]);
            }
        }

        if (final)
        {
            EndThinkingGroup(thinking);
        }
    }

    /// <summary>The open thinking group given its summary, from the block's <see cref="ThinkingBlock.Elapsed"/>, and ended: it folds. Nothing without one.</summary>
    private void EndThinkingGroup(ThinkingBlock thinking)
    {
        if (!_store.ThinkingGroupOpen)
        {
            return;
        }

        var elapsed = thinking.Elapsed ?? TimeSpan.Zero;
        _store.SetThinkingGroupSummary(
            [new Segment(ThinkingFoldText.Summary(elapsed, expanded: false), Theme.DimText)],
            [new Segment(ThinkingFoldText.Summary(elapsed, expanded: true), Theme.DimText)]);
        _store.EndGroup();
    }

    /// <summary>The label row of a code block into the flow as its group's summary: stored by <see cref="Scrollback.BeginCodeGroup"/>, written and counted at the bottom.</summary>
    private void EmitCodeLabel(SegmentLine row, int keep, CodeSpan span)
    {
        var segments = new List<Segment>(row);
        _store.BeginCodeGroup(keep, segments);
        _store.SetCodeGroupSummary(CodeSummary(segments, span, expanded: false), CodeSummary(segments, span, expanded: true), span.SourceLines);
        if (_top < 0)
        {
            segments.Add(Segment.LineBreak);
            _inner.Write(new SegmentList(segments));
            Count(segments);
        }
    }

    /// <summary>The label row with its label's text replaced by <see cref="CodeFoldText.Summary"/>: the indent or the reply's glyph ahead of it kept.</summary>
    private static List<Segment> CodeSummary(IReadOnlyList<Segment> label, CodeSpan span, bool expanded)
    {
        var result = label.TakeWhile(s => !s.Style.Equals(Theme.MarkdownCodeLabel)).ToList();
        result.Add(new Segment(CodeFoldText.Summary(span.Label, span.SourceLines, expanded), Theme.MarkdownCodeLabel));
        return result;
    }

    /// <summary>Whether <paramref name="row"/> is a code label reading <paramref name="label"/>: its label-coloured text starts it.</summary>
    private static bool ReadsAs(SegmentLine row, string label)
    {
        string text = string.Concat(row.Where(s => s.Style.Equals(Theme.MarkdownCodeLabel)).Select(s => s.Text));
        return text.Length > 0 && label.StartsWith(text.TrimEnd(), StringComparison.Ordinal);
    }

    /// <summary>A partial flow row is ended (the block starts at column 0), the flow cursor with it — the store's open line too.</summary>
    private void EndFlowRow()
    {
        if (_top >= 0 ? _store.LastLineOpen : _col > 0)
        {
            Emit(new List<Segment> { Segment.LineBreak });
        }
    }

    /// <summary>One laid-out line into the flow, at the flow cursor (column 0), ended and counted.</summary>
    private void WriteFlowLine(SegmentLine line)
    {
        var segments = new List<Segment>(line.Count + 1);
        segments.AddRange(line);
        segments.Add(Segment.LineBreak);
        Emit(segments);
    }

    /// <summary>
    /// How many rows under the upper rule the terminal's cursor sits, as the LAST draw left it (the
    /// drawn overlay, never the requested one): the overlay's first row (0) without a slot, else the
    /// cursor's input row under the overlay's rows. What <see cref="Lift"/>, <see cref="Close"/> and
    /// <see cref="RedrawHint"/> count from.
    /// </summary>
    private int CursorDepth => !_drawnOverlay ? _cursorRow : _drawnInput ? _overlayRows + _cursorRow : 0;

    /// <summary>How many rows under the terminal's cursor the hint row sits, as last drawn: over the rows under the cursor and the lower rule; the toolbar, when drawn, is one further.</summary>
    private int HintRowBelowCursor => _paneRows - 2 - _toolbarRows - _perfRows - _stripRows - _scrollRows - CursorDepth;

    /// <summary>How many rows under the terminal's cursor the pane's last row sits, as last drawn: the hint row, or the toolbar under it (2026-09-21), or the performance bar under that (2026-09-29).</summary>
    private int LastRowBelowCursor => _paneRows - 2 - _stripRows - _scrollRows - CursorDepth;

    /// <summary>How many rows over the terminal's cursor the scroll's row sits (2026-10-01), as last drawn: over the upper rule, and the picture strip's rows with their rule when drawn.</summary>
    private int ScrollRowAboveCursor => CursorDepth + 1 + _stripRows + 1;

    /// <summary>How many rows under the terminal's cursor the toolbar sits, as last drawn: the last row, or the one over the performance bar.</summary>
    private int ToolbarRowBelowCursor => LastRowBelowCursor - _perfRows;

    private void WriteRule(int width)
    {
        _inner.Write(new RawText(new string(RuleGlyph, width), Theme.PaneRule));
        _inner.WriteLine();
    }

    /// <summary>
    /// The picture strip's rule (<see cref="StripRule"/>): <see cref="StripButton"/>'s label at its left in its own style,
    /// the close × at its right in <see cref="Theme.DimText"/> as an overlay's; where each landed is kept for the hit tests.
    /// </summary>
    private void WriteStripRule(int width)
    {
        var parts = StripRule(_stripButton(), width);
        _stripButtonColumn = parts.ButtonColumn;
        _stripButtonCells = TextCells.Width(parts.Button);
        _stripCloseColumn = parts.CloseColumn;
        foreach (var (text, style) in new[] { (parts.Lead, Theme.PaneRule), (parts.Button, Theme.AccentSecondary), (parts.Fill, Theme.PaneRule), (parts.Close, Theme.DimText), (parts.Tail, Theme.PaneRule) })
        {
            if (text.Length > 0)
            {
                _inner.Write(new RawText(text, style));
            }
        }

        _inner.WriteLine();
    }

    /// <summary>
    /// The picture strip's rule cut in the pieces <see cref="StripRule"/> draws in their own styles, left to right — the
    /// rule's <see cref="Lead"/>, the <see cref="Button"/>, the rule's <see cref="Fill"/>, the <see cref="Close"/> glyph and
    /// the rule's <see cref="Tail"/> — and the columns the button and the × start at (−1 = not drawn).
    /// </summary>
    public readonly record struct StripRuleParts(string Lead, string Button, string Fill, string Close, string Tail, int ButtonColumn, int CloseColumn)
    {
        /// <summary>The whole rule as one string.</summary>
        public string Text => Lead + Button + Fill + Close + Tail;
    }

    /// <summary>
    /// The picture strip's rule of <paramref name="width"/> cells (2026-09-28, the user's layout): the viewer's
    /// <paramref name="button"/> at the left, right after the rule's first glyph with a space at its right, and the close ×
    /// (<see cref="CloseGlyph"/>) at the right with a space either side — <c>─ 🎞️ ────────── × ─</c>. The space left of
    /// the button went later that day (a screenshot read the gap as two) and came back later still (the user's call: "I
    /// was wrong about this"). With no button, <c>────── × ─</c>; a width that leaves no rule between them drops the
    /// button, one that cannot hold even the × is the bare rule. Pure, pinned.
    /// </summary>
    public static StripRuleParts StripRule(string? button, int width)
    {
        width = Math.Max(0, width);
        string rule = RuleGlyph.ToString();
        string tail = " " + rule;
        int closeCells = TextCells.Width(CloseGlyph);
        int right = 1 + closeCells + TextCells.Width(tail);   // " × ─"
        button ??= "";
        int buttonCells = TextCells.Width(button);
        if (button.Length > 0 && width - (2 + buttonCells + 1) - right >= 1)
        {
            // "─ " + button + " " + the rule + " × ─"
            string lead = rule + " ";
            int fill = width - TextCells.Width(lead) - buttonCells - 1 - right;
            return new StripRuleParts(lead, button, " " + new string(RuleGlyph, fill) + " ", CloseGlyph, tail, TextCells.Width(lead), width - closeCells - TextCells.Width(tail));
        }

        if (width - right >= 1)
        {
            return new StripRuleParts("", "", new string(RuleGlyph, width - right) + " ", CloseGlyph, tail, -1, width - closeCells - TextCells.Width(tail));
        }

        return new StripRuleParts("", "", new string(RuleGlyph, width), "", "", -1, -1);
    }

    /// <summary>
    /// The upper rule with <see cref="RuleTitle"/> at its right edge and, while something in the transcript folds and no
    /// overlay is up, ⤡ at its left (<see cref="UpperRule"/>) in the strip button's style; the title, whether the button
    /// was wanted and where it landed are remembered for the tick and <see cref="TryHitFoldButton"/>. The closed strip's 🎞️
    /// (<see cref="StripReopen"/>, 2026-10-03) the same, for <see cref="TryHitStripReopen"/>.
    /// </summary>
    private void WriteUpperRule(int width)
    {
        _drawnRuleTitle = _ruleTitle();
        _drawnFolds = FoldsWanted();
        _drawnReopen = ReopenWanted();
        var parts = UpperRule(_drawnRuleTitle, _drawnFolds, width, _drawnReopen);
        _foldColumn = parts.FoldColumn;
        _reopenColumn = parts.ReopenColumn;
        _ruleTitleColumn = parts.TitleColumn;
        _ruleTitleCells = parts.TitleCells;
        foreach (var (text, style) in new[] { (parts.Lead, Theme.PaneRule), (parts.Button, Theme.AccentSecondary), (parts.Reopen, Theme.AccentSecondary), (parts.Rest, Theme.PaneRule) })
        {
            if (text.Length > 0)
            {
                _inner.Write(new RawText(text, style));
            }
        }

        _inner.WriteLine();
    }

    /// <summary>The upper rule's ⤡ is wanted: something in the store folds (<see cref="Scrollback.AnyFolds"/>) and no overlay is up — under one they go, as the strip does.</summary>
    private bool FoldsWanted() => _overlay is null && _store.AnyFolds;

    /// <summary>The upper rule's <see cref="StripReopen"/> label, or null: one answered, no overlay up and room in the window for the strip it brings back.</summary>
    private string? ReopenWanted() =>
        _overlay is null && _stripReopen() is { Length: > 0 } label && StripFits(Width, Height, BarRowsFor(Height)) ? label : null;

    /// <summary>
    /// Whether the upper rule's buttons wanted now are not what the drawn rule shows: a run came to fold, or the last fold
    /// went; the strip was closed or opened again, the setting flipped, the window's room for the strip changed (2026-10-03).
    /// </summary>
    private bool RuleButtonsChanged() => FoldsWanted() != _drawnFolds || !string.Equals(ReopenWanted(), _drawnReopen, StringComparison.Ordinal);

    /// <summary>
    /// The upper rule cut in the pieces <see cref="UpperRule"/> draws in their own styles, left to right — the rule's
    /// <see cref="Lead"/>, the <see cref="Button"/> (⤡ and the space after it) and the <see cref="Rest"/>
    /// (<see cref="RuleWithTitle"/>'s) — and the column ⤡ is at (−1 = not drawn); the column the title starts at (−1 = none
    /// drawn) and its cells (2026-09-28, for <see cref="TryHitRuleTitle"/>).
    /// </summary>
    /// <remarks>
    /// <see cref="Reopen"/> is the strip's 🎞️ and the space after it (2026-10-03, <see cref="StripReopen"/>), drawn between
    /// the <see cref="Button"/> and the <see cref="Rest"/>, at <see cref="ReopenColumn"/> (−1 = not drawn).
    /// </remarks>
    public readonly record struct UpperRuleParts(string Lead, string Button, string Rest, int FoldColumn, int TitleColumn = -1, int TitleCells = 0, string Reopen = "", int ReopenColumn = -1)
    {
        /// <summary>The whole rule as one string.</summary>
        public string Text => Lead + Button + Reopen + Rest;
    }

    /// <summary>
    /// The upper rule of <paramref name="width"/> cells (2026-09-28, the user's ask): with <paramref name="folds"/>,
    /// <see cref="FoldGlyph"/> after the rule's first glyph with a space either side, then <see cref="RuleWithTitle"/> over the
    /// rest — <c>─ ⤡ ──────── title ─</c>. Until 2026-09-29 it was the ↘️ ↖️ pair with the space at their left alone (Windows
    /// Terminal drew each emoji with room of its own at its right); the one-cell ⤡ has no such room, so it is spaced as the
    /// title is. With a <paramref name="reopen"/> label (2026-10-03, the user's ask: the closed picture strip's way back) it
    /// follows ⤡ with a space after it, or takes ⤡'s place without folds — <c>─ ⤡ 🎞️ ──────── title ─</c>,
    /// <c>─ 🎞️ ──────── title ─</c>. A width that cannot keep <see cref="RuleTitleMinRule"/> glyphs after the buttons drops
    /// the reopen first, then ⤡; with neither it is <see cref="RuleWithTitle"/> alone. Pure, pinned.
    /// </summary>
    public static UpperRuleParts UpperRule(string title, bool folds, int width, string? reopen = null)
    {
        ArgumentNullException.ThrowIfNull(title);
        width = Math.Max(0, width);
        string lead = RuleGlyph + " ";
        string button = folds ? FoldGlyph + " " : "";
        string again = string.IsNullOrEmpty(reopen) ? "" : reopen + " ";
        if (again.Length > 0 && width - TextCells.Width(lead + button + again) < RuleTitleMinRule)
        {
            again = "";
        }

        if (button.Length > 0 && width - TextCells.Width(lead + button) < RuleTitleMinRule)
        {
            button = "";
        }

        if (button.Length == 0 && again.Length == 0)
        {
            var (plainColumn, plainCells) = TitleSpan(title, width, 0);
            return new UpperRuleParts("", "", RuleWithTitle(title, width), -1, plainColumn, plainCells);
        }

        int leadCells = TextCells.Width(lead);
        int used = leadCells + TextCells.Width(button) + TextCells.Width(again);
        var (titleColumn, titleCells) = TitleSpan(title, width - used, used);
        return new UpperRuleParts(
            lead,
            button,
            RuleWithTitle(title, width - used),
            button.Length > 0 ? leadCells : -1,
            titleColumn,
            titleCells,
            again,
            again.Length > 0 ? leadCells + TextCells.Width(button) : -1);
    }

    /// <summary>Where <see cref="RuleWithTitle"/> of <paramref name="width"/> cells, drawn from column <paramref name="offset"/>, puts its title: the column and the cells, (−1, 0) with none.</summary>
    private static (int Column, int Cells) TitleSpan(string title, int width, int offset) =>
        FitRuleTitle(title, width) is var (fitted, left) ? (offset + left + 1, TextCells.Width(fitted)) : (-1, 0);

    /// <summary>
    /// <see cref="RuleWithTitle"/>'s fit: the title cut to the room left after <see cref="RuleTitleMinRule"/> glyphs, and the rule
    /// glyphs at its left; null for an empty title or a width with no room for a one-cell title.
    /// </summary>
    private static (string Fitted, int Left)? FitRuleTitle(string title, int width)
    {
        int room = width - RuleTitleMinRule - 3;   // the space either side and the last glyph
        if (title.Length == 0 || room < 1)
        {
            return null;
        }

        string fitted = Fit(title, room);
        return (fitted, width - TextCells.Width(fitted) - 3);
    }

    /// <summary>The least rule glyphs kept at the left of a titled upper rule; a title that would leave fewer is cut, a width that cannot hold even a cut title gets the bare rule.</summary>
    public const int RuleTitleMinRule = 8;

    /// <summary>
    /// The upper rule of <paramref name="width"/> cells: bare for an empty <paramref name="title"/>,
    /// else the rule, a space, the title, a space and one last rule glyph at the edge —
    /// <c>──────── settings-layout-reorder ─</c> (2026-09-18, the user's picture). The title is cut
    /// with <see cref="Fit"/> so <see cref="RuleTitleMinRule"/> glyphs stay on the left; a width
    /// with no room for a one-cell title after them is the bare rule. Pinned.
    /// </summary>
    public static string RuleWithTitle(string title, int width)
    {
        ArgumentNullException.ThrowIfNull(title);
        return FitRuleTitle(title, width) is var (fitted, left)
            ? new string(RuleGlyph, left) + " " + fitted + " " + RuleGlyph
            : new string(RuleGlyph, Math.Max(0, width));
    }

    /// <summary>The title <see cref="RuleTitle"/> answers now is not the one on the drawn upper rule.</summary>
    private bool RuleTitleChanged() => !string.Equals(_ruleTitle(), _drawnRuleTitle, StringComparison.Ordinal);

    private void WriteHintRow()
    {
        int max = Math.Max(1, Width - 1);
        if ((_dragHint ?? _alertHint) is { } whole)
        {
            // A picture on its way to the line (2026-09-28): the row is the drag's alone, in the strip button's style, no zones.
            // An alert (2026-10-01, the kill switch's first press) the same, in the warning's style.
            string shown = Fit(whole, max);
            _inner.Write(new RawText(shown, _dragHint is not null ? Theme.AccentSecondary : _alertFlash ? Theme.Hint : Theme.WarnText));
            _inner.Write(EraseLineEnd);
            _shownHint = shown;
            _hintStrip = "";
            _trailerColumn = -1;
            _markColumn = -1;
            _queuedColumn = -1;
            _queuedCells = 0;
            _usageColumn = -1;
            _usageCells = 0;
            _labelColumn = -1;
            _labelCells = 0;
            return;
        }

        string mark = _trailerMark();
        if (_busyLabel is { } label)
        {
            // The strip, then the frame in its own style, then the label and its count in what is
            // left ahead of the trailer — the same cut PinRight makes, in four styles; the trailer
            // gives way to the uncut left as PinRight's does (2026-09-28).
            string frame = Theme.SpinnerFrames[_frame % Theme.SpinnerFrames.Length];
            string queued = StandingQueued();
            // The tally beside the label (2026-09-25), where the standing row would have it: not under an overlay's hint (nor
            // under the scroll's until 2026-10-01, when it went to a row of its own).
            string usage = _overlay is null ? _busyUsage() : "";
            var elapsed = _time.GetElapsedTime(_busySince);
            TimeSpan? step = _stageSince != _busySince && _stepClockShown() ? _time.GetElapsedTime(_stageSince) : null;
            // A label that trails the tally (LabelAfterUsage, the ComfyUI generation's) swaps the two; nothing else moves.
            bool after = usage.Length > 0 && _labelAfterUsage(label);
            // The way out named at the row's end (2026-10-04) while the screen's turn runs and no pane's keys stand there instead.
            string hint = _overlay?.Hint ?? (_stopHintShown() ? StopHint : "");
            string unfitted = " " + HintRow(HintRow(Labelled(BusyText(label, elapsed, step), usage, after), queued), hint);
            string labelled = " " + Labelled(BusyText(label, elapsed, step), usage, after);
            // Read once (2026-10-01): an embedded download's strip turns with the clock, and two reads astride a frame
            // would leave _hintStrip blank below — its 📥 dead to a click for the draw.
            string glyphs = _strip();
            string strip = StripPrefix(glyphs);
            string right = Trail(_trailer(), mark, max, TextCells.Width(strip) + TextCells.Width(frame) + TextCells.Width(unfitted));
            int leftMax = right.Length == 0 ? max : max - TextCells.Width(right) - TrailerGap;
            string prefix = Fit(strip, leftMax - TextCells.Width(frame));
            int restMax = leftMax - TextCells.Width(prefix) - TextCells.Width(frame);
            string rest = Fit(unfitted, restMax);
            string left = prefix + frame + rest;
            string tail = right.Length == 0 ? "" : new string(' ', max - TextCells.Width(left) - TextCells.Width(right)) + right;
            _inner.Write(new RawText(prefix, Theme.Hint));
            _inner.Write(new RawText(frame, Theme.SpinnerStyle));
            WriteTrailed(rest + tail, mark);
            _shownHint = left + tail;
            // The strip's place (2026-09-24): column 0, as on the standing row — when the fit left it whole, a cut glyph being no button.
            _hintStrip = prefix == strip ? glyphs : "";
            // The queued part's place: after the prefix, the frame, the blank, the label, the tally and a separator — when the fit left it whole.
            RecordQueued(queued, TextCells.Width(prefix) + TextCells.Width(frame), TextCells.Width(labelled + HintSeparator), unfitted, restMax);
            // The usage zone: the frame, the label and the tally after the prefix — when the fit kept them
            // whole. A tally the fit cut leaves the frame and whichever comes first — the label, or the tally
            // ahead of a trailing one.
            int zoneCells = restMax + TextCells.Width(frame);
            string zone = TextCells.Width(frame + unfitted) <= zoneCells || TextCells.Width(frame + labelled) < zoneCells ? frame + labelled : frame + " " + (after ? usage : BusyText(label, elapsed));
            RecordUsage(zone, TextCells.Width(prefix), 0, frame + unfitted, zoneCells);
            // The label's own place (2026-09-28, HintZone.Label): a label LabelAfterUsage takes — after the tally and its
            // separator, or right after the frame's blank with none — when the fit kept it whole.
            // A label LabelZone takes (2026-10-01) stays where it stands, and its zone takes the frame and the blank before it.
            string busyText = BusyText(label, elapsed);
            bool trailing = _labelAfterUsage(label);
            bool leading = !trailing && _labelZone(label);
            int labelAhead = after ? TextCells.Width(" " + usage + HintSeparator) : 1;
            int labelWidth = TextCells.Width(busyText);
            bool labelWhole = (trailing || leading) && (TextCells.Width(unfitted) <= restMax || labelAhead + labelWidth <= restMax);
            _labelColumn = !labelWhole ? -1 : leading ? TextCells.Width(prefix) : TextCells.Width(prefix) + TextCells.Width(frame) + labelAhead;
            _labelCells = !labelWhole ? 0 : leading ? TextCells.Width(frame) + labelAhead + labelWidth : labelWidth;
        }
        else
        {
            string row = StandingRow();
            string right = Trail(_trailer(), mark, max, TextCells.Width(row));
            string hint = PinRight(row, _trailer(), mark, max);
            WriteTrailed(hint, mark);
            _shownHint = hint;
            _hintStrip = _strip();
            _trailerColumn = right.Length == 0 ? -1 : max - TextCells.Width(right);
            // The mark's place (2026-09-21): the row's last cells, since Trail never cuts it.
            _markColumn = right.Length == 0 || mark.Length == 0 ? -1 : max - TextCells.Width(mark);
            // The queued part's place: after the strip and its separator — when the fit left it whole.
            int cells = right.Length == 0 ? max : max - TextCells.Width(right) - TrailerGap;
            RecordQueued(StandingQueued(), 0, _hintStrip.Length == 0 ? 0 : TextCells.Width(_hintStrip) + HintSeparator.Length, row, cells);
            // The usage zone: the tally where the screen's hint put it — nowhere under an overlay's
            // hint, or when the timers or the exit hint stand in its place.
            string usage = _overlay is null ? _usage() : "";
            int at = usage.Length == 0 ? -1 : row.IndexOf(usage, StringComparison.Ordinal);
            RecordUsage(at < 0 ? "" : usage, 0, at < 0 ? 0 : TextCells.Width(row[..at]), row, cells);
            _labelColumn = -1;
            _labelCells = 0;
        }

        if (_busyLabel is not null)
        {
            _trailerColumn = -1;
            _markColumn = -1;
        }

        _inner.Write(EraseLineEnd);
    }

    /// <summary>
    /// Remembers where the queued part landed for the hit tests: <paramref name="ahead"/> cells into
    /// the row after <paramref name="offset"/> (what was written before the fitted text), or nowhere
    /// when there is none or <see cref="Fit"/> cut <paramref name="text"/> (of which it is a part) at
    /// <paramref name="cells"/> before its end — a cut part is no button.
    /// </summary>
    private void RecordQueued(string queued, int offset, int ahead, string text, int cells)
    {
        int width = TextCells.Width(queued);
        bool whole = width > 0 && (TextCells.Width(text) <= cells || ahead + width < cells);
        _queuedColumn = whole ? offset + ahead : -1;
        _queuedCells = whole ? width : 0;
    }

    /// <summary><see cref="RecordQueued"/> for the usage zone (2026-09-21): <paramref name="usage"/> is the tally or the spinner with its label.</summary>
    private void RecordUsage(string usage, int offset, int ahead, string text, int cells)
    {
        int width = TextCells.Width(usage);
        bool whole = width > 0 && (TextCells.Width(text) <= cells || ahead + width < cells);
        _usageColumn = whole ? offset + ahead : -1;
        _usageCells = whole ? width : 0;
    }

    /// <summary>
    /// The toolbar (2026-09-21) on the cursor's row, at the hint row's width (the last column left
    /// empty, as every row leaves it — a full row would leave the terminal a wrap pending): the
    /// strip in the hint's style, the path dim after it; the strip as cut and the path's place
    /// remembered for <see cref="TryHitToolbar"/> — the path's column −1 when the row had no room for it.
    /// </summary>
    private void WriteToolbarRow(ToolbarParts toolbar, int width)
    {
        int cells = Math.Max(1, width - 1);
        string row = ToolbarRow(toolbar.Strip, toolbar.Path, cells);
        string strip = Fit(toolbar.Strip, cells);   // as ToolbarRow placed it: the path is what follows the blanks
        string path = row[strip.Length..].TrimStart(' ');
        foreach (var (text, off) in ToolbarStripRuns(strip, toolbar.Off))
        {
            _inner.Write(new RawText(text, off ? Theme.ToolbarOff : Theme.Hint));
        }

        _inner.Write(new RawText(row[strip.Length..], Theme.DimText));
        _inner.Write(EraseLineEnd);
        _shownToolbar = row;
        _shownToolbarOff = OffKey(toolbar.Off);
        _toolbarStrip = strip;
        _toolbarPathCells = TextCells.Width(path);
        _toolbarPathColumn = path.Length == 0 ? -1 : cells - _toolbarPathCells;
    }

    /// <summary>
    /// The toolbar's strip as drawn (2026-10-03, the user's ask: a tool switch that is off sits on a dark slab, the one look
    /// that reads on a colour emoji — faint and a grey colour leave it as it was): runs of <paramref name="strip"/> in order,
    /// each glyph <paramref name="off"/> names (by its place among the glyphs, from 0) a run of its own on the slab, its
    /// variation selector with it (<see cref="TryStripGlyphAt"/>'s walk); the separators and every other glyph in the plain
    /// runs between. The cut's <c>…</c> is never on the slab. Pinned.
    /// </summary>
    public static IReadOnlyList<(string Text, bool Off)> ToolbarStripRuns(string strip, IReadOnlyList<int>? off)
    {
        ArgumentNullException.ThrowIfNull(strip);
        var runs = new List<(string Text, bool Off)>();
        if (off is null || off.Count == 0)
        {
            if (strip.Length > 0)
            {
                runs.Add((strip, false));
            }

            return runs;
        }

        int plainStart = 0;
        int glyph = 0;
        int i = 0;
        while (i < strip.Length)
        {
            TextCells.ElementWidth(strip, i, out int length);
            length = Math.Max(1, length);
            if (i + length < strip.Length && strip[i + length] is '\uFE0F' or '\uFE0E')
            {
                TextCells.ElementWidth(strip, i + length, out int selector);
                length += Math.Max(1, selector);
            }

            string element = strip.Substring(i, length);
            if (!string.IsNullOrWhiteSpace(element))
            {
                if (off.Contains(glyph) && element != "…")
                {
                    if (i > plainStart)
                    {
                        runs.Add((strip[plainStart..i], false));
                    }

                    runs.Add((element, true));
                    plainStart = i + length;
                }

                glyph++;
            }

            i += length;
        }

        if (plainStart < strip.Length)
        {
            runs.Add((strip[plainStart..], false));
        }

        return runs;
    }

    /// <summary>The off glyphs as one comparable word, so the tick redraws a flip that leaves the row's text as it was.</summary>
    private static string OffKey(IReadOnlyList<int>? off) => off is null || off.Count == 0 ? "" : string.Join(',', off);

    /// <summary>No toolbar drawn: nothing for the tick to compare, no zones for the hit test.</summary>
    private void ForgetToolbar()
    {
        _shownToolbar = null;
        _shownToolbarOff = "";
        _toolbarStrip = "";
        _toolbarPathColumn = -1;
        _toolbarPathCells = 0;
    }

    /// <summary>
    /// <paramref name="text"/> in the hint style, its trailing <paramref name="mark"/> — the row
    /// ends with the mark whenever there is one (<see cref="Trail"/>) — in the mark's own.
    /// </summary>
    private void WriteTrailed(string text, string mark)
    {
        if (mark.Length == 0 || !text.EndsWith(mark, StringComparison.Ordinal))
        {
            _inner.Write(new RawText(text, Theme.Hint));
            return;
        }

        _inner.Write(new RawText(text[..^mark.Length], Theme.Hint));
        _inner.Write(new RawText(mark, Theme.TrailerMark));
    }

    /// <summary>The overlay's hint while one is open, else the screen's — scrolled too since 2026-10-01, the scroll's hint on a row of its own (<see cref="ScrolledRow"/>).</summary>
    private string StandingHint() => _overlay is { } overlay ? overlay.Hint : _hint();

    /// <summary>The queued part while the row is the screen's own — nothing under an overlay's hint (2026-09-18; under the scroll's until 2026-10-01).</summary>
    private string StandingQueued() => _overlay is null ? _queued() : "";

    /// <summary>The scroll's row (2026-10-01) on the cursor's row: <see cref="ScrolledRow"/> in the hint's style, the rest of the row erased; the text remembered for the tick.</summary>
    private void WriteScrollRow(int width)
    {
        string row = ScrolledRow(RowsBelowLocked(), width - 1);
        _inner.Write(new RawText(row, Theme.Hint));
        _inner.Write(EraseLineEnd);
        _shownScroll = row;
    }

    /// <summary><see cref="RedrawHint"/> for the scroll's row (2026-10-01): its row again, in place, over the upper rule; nothing while none is drawn.</summary>
    private void RedrawScrollRow()
    {
        if (!_drawn || _batch > 0 || _modal > 0 || _scrollRows == 0)
        {
            return;
        }

        RedrawRow(-ScrollRowAboveCursor, () => WriteScrollRow(Width));
    }

    /// <summary>The count on the scroll's row is not the drawn one (a reply streaming on below, a write stored while scrolled).</summary>
    private bool ScrollRowChanged() => _scrollRows > 0 && !string.Equals(ScrolledRow(RowsBelowLocked(), Width - 1), _shownScroll, StringComparison.Ordinal);

    /// <summary>The standing hint behind the strip and the queued part (<see cref="HintRow"/>: each alone when the others are empty).</summary>
    private string StandingRow() => HintRow(HintRow(_strip(), StandingQueued()), StandingHint());

    /// <summary>
    /// The hint row again, in place: down from the cursor's row over the rows under it and the
    /// lower rule, and back — to the cell on the input row when one is shown, else to the overlay's
    /// first row with the cursor left hidden (a menu lists voices under its own spinner).
    /// </summary>
    private void RedrawHint()
    {
        if (!_drawn || _batch > 0 || _modal > 0)
        {
            return;
        }

        RedrawRow(HintRowBelowCursor, WriteHintRow);
    }

    /// <summary><see cref="RedrawHint"/> for the toolbar (2026-09-21): its row again, in place, one under the hint row; nothing while none is drawn.</summary>
    private void RedrawToolbar()
    {
        if (!_drawn || _batch > 0 || _modal > 0 || _toolbarRows == 0 || _toolbar() is not { } toolbar)
        {
            return;
        }

        RedrawRow(ToolbarRowBelowCursor, () => WriteToolbarRow(toolbar, Width));
    }

    /// <summary><see cref="RedrawHint"/> for the performance bar (2026-09-29): its row again, in place, the pane's last; nothing while none is drawn.</summary>
    private void RedrawPerf()
    {
        if (!_drawn || _batch > 0 || _modal > 0 || _perfRows == 0 || _perf(Math.Max(1, Width - 1)) is not { } row)
        {
            return;
        }

        RedrawRow(LastRowBelowCursor, () => WritePerfRow(row));
    }

    /// <summary>The performance bar's row on the cursor's row: its runs in their styles, the rest of the row erased; the text remembered for the tick.</summary>
    private void WritePerfRow(UI.PerfRow row)
    {
        foreach (var segment in row.Segments)
        {
            _inner.Write(new RawText(segment.Text, segment.Style));
        }

        _inner.Write(EraseLineEnd);
        _shownPerf = row.Text;
    }

    /// <summary>The row <paramref name="down"/> rows under the cursor's (over it when negative: the scroll's row, 2026-10-01) written again by <paramref name="write"/>, the cursor back where it was.</summary>
    private void RedrawRow(int down, Action write)
    {
        // One frame (2026-09-29): the cursor's hide, the trip down and back and the row itself.
        BeginSync();
        try
        {
            _inner.Cursor.Show(false);
            _inner.Cursor.Move(down < 0 ? CursorDirection.Up : CursorDirection.Down, Math.Abs(down));
            ColumnZero();
            write();
            _inner.Cursor.Move(down < 0 ? CursorDirection.Down : CursorDirection.Up, Math.Abs(down));
            ColumnZero();
            if (_drawnOverlay && !_drawnInput)
            {
                return;
            }

            _inner.Cursor.Move(CursorDirection.Right, TextCells.Width(InputLine.PromptGlyph) + _cursorCell);
            _inner.Cursor.Show(true);
        }
        finally
        {
            EndSync();
        }
    }

    private void OnTick()
    {
        lock (_gate)
        {
            CloseLeakedFrames();
            if (_disposed || !_drawn || _batch > 0 || _modal > 0)
            {
                return;
            }

            if (_alertHint is not null && _time.GetUtcNow().UtcTicks >= _alertUntil)
            {
                // An alert that has lapsed (2026-10-01): the row is its own again; the branches below may draw over it.
                _alertHint = null;
                _alertUntil = 0;
                RedrawHint();
            }

            int w = Width;
            int h = Height;
            if (w != _lastWidth || h != _lastHeight)
            {
                // The alternate buffer is not reflowed: the screen is rebuilt from the store — the
                // window again, or the flow's tail — from the top row (a move past it stops there).
                DiagnosticLog.Debug(Category, ResizedLogLine(_lastWidth, _lastHeight, w, h));
                BeginSync();
                Lift();
                _inner.Cursor.Move(CursorDirection.Up, Math.Max(h, _lastHeight));
                ColumnZero();
                _inner.Write(EraseDown);
                _blank = true;
                _row = 0;
                _col = 0;
                _lineFull = false;
                Draw();
                EndSync();
                return;
            }

            if (_liveDirty)
            {
                if (_busyLabel is not null)
                {
                    _frame++;
                }

                if (_drawnScrolled)
                {
                    // Scrolled: the block is not drawn; its rows count on the scroll's row (the busy row's frame moves on its own).
                    _liveCount = _live is null ? 0 : Math.Max(0, LiveLayout(w, RegionRows(_paneRows)).Count - _liveCommitted);
                    _liveDirty = false;
                    RedrawScrollRow();
                    if (_busyLabel is not null)
                    {
                        RedrawHint();
                    }

                    return;
                }

                // The reply grew since the last layout: the whole pane again, the spinner's frame with
                // it — painted over the old frame, never erased first (Overpaint).
                BeginSync();
                Overpaint();
                EndSync();
                return;
            }

            if (RuleTitleChanged() || RuleButtonsChanged())
            {
                // The session's name landed or went (the model's title arrives off-thread), or the
                // rule's ⤡ came or went (a run came to fold, the last fold went; 2026-09-28), or its
                // 🎞️ (the strip closed or back, the setting flipped; 2026-10-03): the whole pane
                // again, as a grown reply gets — the rule is drawn in every state.
                if (_busyLabel is not null)
                {
                    _frame++;
                }

                BeginSync();
                Lift();
                Draw();
                EndSync();
                return;
            }

            if (ToolbarRowsFor(h) != _toolbarRows || PerfRowsFor(h) != _perfRows)
            {
                // The toolbar or the performance bar came or went (the screen's settings, a window at the
                // edge): the whole pane again — its shape changed.
                if (_busyLabel is not null)
                {
                    _frame++;
                }

                BeginSync();
                Lift();
                Draw();
                EndSync();
                return;
            }

            if (StripChanged())
            {
                // A picture came, the highlight moved, the setting flipped: the whole pane again — the strip's rows
                // are part of its shape.
                if (_busyLabel is not null)
                {
                    _frame++;
                }

                BeginSync();
                Lift();
                Draw();
                EndSync();
                return;
            }

            if (ToolbarChanged())
            {
                // The working directory changed under it: the row again, in place.
                RedrawToolbar();
            }

            if (PerfChanged())
            {
                // A new reading: the row again, in place.
                RedrawPerf();
            }

            if (ScrollRowChanged())
            {
                // The count below moved with nothing redrawing it (a safety net: the writes that change it redraw it).
                RedrawScrollRow();
            }

            if (_busyLabel is not null)
            {
                _frame++;
                RedrawHint();
                return;
            }

            if (HintChanged())
            {
                RedrawHint();
            }
        }
    }

    /// <summary>The toolbar <see cref="Toolbar"/> answers now is not the drawn one (its text, or its off slabs since 2026-10-03 — a presence change is <see cref="ToolbarRowsFor"/> against the drawn rows).</summary>
    private bool ToolbarChanged() => _toolbarRows > 0 && _toolbar() is { } toolbar
        && (!string.Equals(ToolbarRow(toolbar.Strip, toolbar.Path, Math.Max(1, Width - 1)), _shownToolbar, StringComparison.Ordinal)
            || !string.Equals(OffKey(toolbar.Off), _shownToolbarOff, StringComparison.Ordinal));

    /// <summary>The performance bar <see cref="Perf"/> answers now is not the drawn one (its text — a presence change is <see cref="PerfRowsFor"/> against the drawn rows).</summary>
    private bool PerfChanged() => _perfRows > 0 && _perf(Math.Max(1, Width - 1)) is { } row && !string.Equals(row.Text, _shownPerf, StringComparison.Ordinal);

    /// <summary>The standing hint (the overlay's while one is open) with the trailer is not what the hint row shows.</summary>
    private bool HintChanged() =>
        !string.Equals((_dragHint ?? _alertHint) is { } whole ? Fit(whole, Math.Max(1, Width - 1)) : PinRight(StandingRow(), _trailer(), _trailerMark(), Math.Max(1, Width - 1)), _shownHint, StringComparison.Ordinal);

    private void ColumnZero() => _inner.Cursor.Move(CursorDirection.Left, Width);

    /// <summary>
    /// Where the pane's frames are held back and let go as one write (2026-09-29, the user's report of the rows flickering
    /// at a turn's end): <see cref="FrameWriter"/> in the app, null (no holding) in the tests.
    /// </summary>
    public IFrameHold? Frames { get; set; }

    // How many synchronized frames are open (BeginSync/EndSync nest: a row rewritten inside a batch's frame).
    private int _syncDepth;

    /// <summary>
    /// A synchronized frame's start: the output held (<see cref="Frames"/>) and, on the outermost, the terminal told to
    /// hold its screen — a nested frame writes no code, since the terminal's end is not counted.
    /// </summary>
    private void BeginSync()
    {
        Frames?.Hold();
        if (_syncDepth++ == 0)
        {
            _inner.Write(SyncBegin);
            SyncPageBackgroundLocked();
        }
    }

    /// <summary>
    /// The terminal's default background brought in step with <see cref="PageBackground"/> (a theme change, a profile's load
    /// or a flip of <c>Themed background</c> shows on the next frame): OSC 11 for a new colour, OSC 111 when it turns null.
    /// Only in the alternate buffer; nothing when the answer is the one last written.
    /// </summary>
    private void SyncPageBackgroundLocked()
    {
        if (!_inAlternate)
        {
            return;
        }

        Color? wanted = _pageBackground();
        if (wanted == _pageBackgroundSent)
        {
            return;
        }

        _inner.Write(wanted is { } color ? new ControlCode(PageBackgroundSequence(color)) : PageBackgroundReset);
        _pageBackgroundSent = wanted;
    }

    /// <summary>The matching end: the terminal's end on the outermost, then the held output let go.</summary>
    private void EndSync()
    {
        if (_syncDepth == 0)
        {
            return;
        }

        if (--_syncDepth == 0)
        {
            _inner.Write(SyncEnd);
        }

        Frames?.Release();
    }

    /// <summary>
    /// A frame left open by a draw that threw (2026-09-29): with no batch or modal running none should be, and a held
    /// writer would keep everything from the screen from then on — closed here, on the tick.
    /// </summary>
    private void CloseLeakedFrames()
    {
        if (_syncDepth == 0 || _batch > 0 || _modal > 0)
        {
            return;
        }

        DiagnosticLog.Debug(Category, $"{_syncDepth} synchronized frame(s) left open; closed.");
        while (_syncDepth > 0)
        {
            EndSync();
        }
    }

    /// <summary>At most <paramref name="cells"/> cells of <paramref name="text"/>, an ellipsis when cut. Pinned.</summary>
    public static string Fit(string text, int cells)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (cells <= 0)
        {
            return "";
        }

        if (TextCells.Width(text) <= cells)
        {
            return text;
        }

        if (cells == 1)
        {
            return "…";
        }

        int end = 0;
        int used = 0;
        while (end < text.Length)
        {
            int w = TextCells.ElementWidth(text, end, out int length);
            if (used + w > cells - 1)
            {
                break;
            }

            used += w;
            end += Math.Max(1, length);
        }

        return text[..end] + "…";
    }

    /// <summary>
    /// <see cref="Fit"/>'s mirror: at most <paramref name="cells"/> cells of the END of
    /// <paramref name="text"/>, an ellipsis ahead of it when cut — a path keeps its innermost
    /// folders (the banner's working directory, 2026-09-18). Pinned.
    /// </summary>
    public static string FitTail(string text, int cells)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (cells <= 0)
        {
            return "";
        }

        if (TextCells.Width(text) <= cells)
        {
            return text;
        }

        if (cells == 1)
        {
            return "…";
        }

        int start = text.Length;
        int used = 0;
        while (start > 0)
        {
            int length = TextCells.ElementLengthBefore(text, start);
            int w = TextCells.ElementWidth(text, start - length, out _);
            if (used + w > cells - 1)
            {
                break;
            }

            used += w;
            start -= length;
        }

        return "…" + text[start..];
    }

    /// <summary>Pre-rendered segments written as they are.</summary>
    private sealed class SegmentList : IRenderable
    {
        private readonly List<Segment> _segments;

        public SegmentList(List<Segment> segments)
        {
            _segments = segments;
        }

        public Measurement Measure(RenderOptions options, int maxWidth) => new(0, maxWidth);

        public IEnumerable<Segment> Render(RenderOptions options, int maxWidth) => _segments;
    }

    /// <summary>A batch's end (<see cref="Batch"/>), once.</summary>
    private sealed class Scope : IDisposable
    {
        public static readonly Scope None = new(null);

        private Action? _onDispose;

        public Scope(Action? onDispose)
        {
            _onDispose = onDispose;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _onDispose, null)?.Invoke();
        }
    }
}
