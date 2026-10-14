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
/// terminal), a sent line and a pane that shrinks after flow rows went off the top (a tall menu
/// closed, 2026-10-09) write the store's tail back as the flow and draw the pane as before.
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

    // The transcript's find while one is shown (2026-10-07, ShowFind): its text, and the store row and character of the match it
    // is on; null when none is. The scroll is then held on the store, even at its last window.
    private string? _find;
    private int _findRow = -1;
    private int _findStart = -1;
    private bool _drawnScrolled;
    private bool _blank = true;
    private int _liveCount;

    // Flow rows went off the screen's top since the flow was last written from the store (2026-10-09, the user's report: a
    // tall pane — /tools, /skills — pushed a short transcript off, and once it closed nothing brought it back: the store fit
    // the region, so PgUp and the wheel had nothing to scroll). Set where the terminal scrolls the flow (Count past the last
    // row, Draw's overflow); a draw at the bottom that leaves room over the pane then writes the store's tail back, as a
    // resize does. A flag, not a count against the store: a cursor report that corrects the row must not rewrite every tick.
    private bool _flowCut;

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

    // The draft row's emoji button (2026-10-11, EmojiButton): whether the last layout made room for it (the wrap two cells
    // narrower) and so the draw put it at the first input row's end, for TryHitEmojiButton, the rewrites and the row moves.
    private Func<bool> _emojiButton = static () => false;
    private bool _emojiShown;

    // The upper rule's title (2026-09-28): the column it landed at (-1 = none drawn) and its cells, for TryHitRuleTitle.
    private int _ruleTitleColumn = -1;
    private int _ruleTitleCells;

    // The lower rule's title (2026-10-12, the running processes' count): its provider, the one last drawn for the tick, and the
    // column it landed at (-1 = none drawn) and its cells, for TryHitLowerRuleTitle.
    private Func<string> _lowerRuleTitle = static () => "";
    private string _drawnLowerRuleTitle = "";
    private int _lowerTitleColumn = -1;
    private int _lowerTitleCells;

    // The hint row.
    private Func<string> _hint = static () => "";
    private Func<string> _strip = static () => "";
    private Func<string> _trailer = static () => "";
    private Func<string> _trailerMark = static () => "";
    private Func<IReadOnlyList<HintSlot>>? _hintLayout;
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

    // The Hints bar (2026-10-14, the user's ask: the hint row split in two, the keys on a row of their own over the Status bar):
    // its saved word (HintsBarMode; off here, the pane as it was, until the screen says), the screen's two rungs of the ladder (the
    // armed prompt, the keys' lead), the rows the last draw gave it (0 or 1, Draw the only writer, as _toolbarRows), its text as
    // drawn (null = no row; the tick's comparison), whether a collapse is due (a send, a turn's end, a pane opened or closed, a
    // resize, the idle timeout: the held row goes at the next draw) and the UTC tick its row's quiet began — no hint, no key, at
    // rest — 0 while it has a hint.
    private Func<string> _hintsBar = static () => HintsBarMode.Off;
    private Func<string?> _armedHint = static () => null;
    private Func<string?> _keysHint = static () => null;
    private int _hintsRows;
    private string? _shownHints;
    private bool _hintsCollapseDue;
    private long _hintsQuietSince;

    // When the top busy label last changed (2026-10-04): the step's own clock beside the turn's.
    private long _stageSince;

    private Func<bool> _stopHintShown = () => false;

    private Func<bool> _stepClockShown = () => false;

    private Func<string> _userLineStyle = () => UI.UserLineStyle.Default;
    private Func<string> _userGlyph = () => InputLine.PromptGlyph;
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

    // The pane's held height (2026-10-14, the user's ask: /tools › Core › Ask user's two-row picker dropped the title from the
    // window's top to its bottom, and ESC threw it back up): the tallest pane a titled overlay has drawn since it opened, and
    // the Menus max height word it was drawn under. Draw pads a shorter page with blank rows under its content up to it, so
    // the top rule stays where the pane that opened the picker had it. CloseOverlay, a height change and a new Menus max
    // height reset it; RestoreOverlay puts back the one a nested pane's mark took (OverlayMark).
    private int _paneFloor;
    private string? _floorMenuHeight;

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
    // Where the strip starts and where the model's name and its mark end (2026-10-12, Hint bar's order): 0 and open ends while the
    // model keeps the right edge; the model's own cells when HintLayout puts it in the flow.
    private int _hintStripColumn;
    private int _trailerEnd = int.MaxValue;
    private int _markEnd = int.MaxValue;
    // The working directory on the hint row (later on 2026-10-12, Hint bar's Working directory): its first column and cells, −1 / 0 for none drawn whole.
    private int _hintPathColumn = -1;
    private int _hintPathCells;
    private int _hintTimersColumn = -1;   // 2026-10-13: the standing row's timers, the double-click's zone
    private int _hintTimersCells;

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

    // The busy row's tally where it stands apart from the spinner (2026-10-13, the sides: the Usage section in its own place): −1 for none.
    private int _tallyColumn = -1;
    private int _tallyCells;

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
    // The toolbar's runs as drawn, each with its first column (2026-10-13: the two groups, pinned left and pinned right), for the hit test.
    private IReadOnlyList<ToolbarPlaced> _toolbarRuns = [];

    // The performance bar (2026-09-29): the provider (asked with the row's cells; null = no row), the rows the last draw
    // gave it (0 or 1, Draw the only writer) and the row's text as drawn (null = no row; the tick's comparison). It is the
    // pane's last row when drawn, under the toolbar.
    private Func<int, UI.PerfRow?> _perf = static _ => null;
    private int _perfRows;
    private string? _shownPerf;

    // The home bar (2026-10-12): the provider (asked with the row's cells; null = no row), the rows the last draw gave it
    // (0 or 1, Draw the only writer), the row's key as drawn (its text and styles: a light changing colour changes no text;
    // null = no row) and its clickable spans as drawn. It is the pane's last row when drawn, under the performance bar.
    private Func<int, UI.HomeRow?> _home = static _ => null;
    private int _homeRows;
    private string? _shownHome;
    private IReadOnlyList<UI.HomeSpan> _homeSpans = [];

    // The rows' order under the lower rule (2026-10-12, Toolbar order): the provider, and the order the last draw wrote them in
    // (Draw the only writer) — what the rows' offsets under the cursor count over, and the tick's comparison.
    private Func<IReadOnlyList<PaneBar>> _barOrder = static () => DefaultBarOrder;
    private IReadOnlyList<PaneBar> _drawnBars = DefaultBarOrder;

    // The menus' height cap (2026-10-01): the saved word of Menus max height, read at every menu draw; full-screen (the
    // pane as it grew before the setting) until the screen sets it.
    private Func<string> _menuHeight = static () => UI.MenuHeight.Default;

    // The picture strip (later still on 2026-09-24): the provider, the rows the last draw gave it (0 or
    // PictureStrip.Rows — Draw is the only writer), the strip's version and highlight as drawn (the tick's
    // comparison), and where its tiles landed (TryHitStrip); the columns of its ‹ and › (2026-10-10, -1 = not drawn), for
    // TryHitStripArrow.
    private Func<PictureStrip?> _pictureStrip = static () => null;
    private int _stripRows;
    private int _drawnStripVersion;
    private bool _drawnStripHighlight;
    private List<PictureSpan> _stripSpans = [];
    private (int Left, int Right) _stripArrows = (-1, -1);

    // The button on the strip's rule (2026-09-27, the picture viewer): its label's provider, and the columns the last
    // draw put the label at (-1 = none drawn), for TryHitStripButton; the close × at the rule's right (2026-09-28), for
    // TryHitStripClose.
    private Func<string?> _stripButton = static () => null;
    private int _stripButtonColumn = -1;
    private int _stripButtonCells;
    private int _stripCloseColumn = -1;

    private sealed record Overlay(IRenderable Content, string Hint, bool Input, bool Close);

    /// <summary>
    /// The overlay on show, taken by a pane that opens over it (2026-10-14: <see cref="InfoPane"/>, <see cref="FolderPane"/>) and
    /// handed to <see cref="RestoreOverlay"/> when it closes: the overlay itself, the pane's held height, the Menus max height
    /// word it was held under and the window's height then. The default is none (no overlay was up). Opaque.
    /// </summary>
    public readonly record struct OverlayMark
    {
        internal OverlayMark(object overlay, int floor, string? menuHeight, int height)
        {
            Shown = overlay;
            Floor = floor;
            MenuHeight = menuHeight;
            Height = height;
        }

        /// <summary>An overlay was up when the mark was taken.</summary>
        public bool IsSome => Shown is not null;

        internal object? Shown { get; }

        internal int Floor { get; }

        internal string? MenuHeight { get; }

        internal int Height { get; }
    }

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

        /// <summary>
        /// The working directory on the standing row (later on 2026-10-12, the user's ask: Hint bar's Working directory): a double-click
        /// there opens <c>/cwd browse</c>, as the tool bar's path does. Last, for the same reason; its pairing key is <see cref="HintPathKey"/>,
        /// since its number would be the strip's first column's.
        /// </summary>
        Path,

        /// <summary>
        /// The timers on the standing row (2026-10-13, the user's ask: a double-click there asks to cancel them all). Last, for the same
        /// reason; its pairing key is <see cref="HintTimersKey"/>, since its number would be a strip column's.
        /// </summary>
        Timers,
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
    /// <remarks>
    /// The model's name (2026-10-12, the user's ask: the Tool bar's <c>Model name</c>) and its reasoning mark (later that day) in the
    /// mark's colour and a zone of its own, as on the hint row. Since 2026-10-13 (the user's ask: any item pinned left or right, the sort
    /// free within each side) the parts are two groups of items, <paramref name="Left"/> drawn from column 0 and <paramref name="Right"/>
    /// ending on the last cell, each in its order; the strip/path constructor is the row as it was (the glyphs left, the path and the
    /// name right, or the name among the glyphs before the <c>modelAt</c>-th).
    /// </remarks>
    public readonly record struct ToolbarParts(IReadOnlyList<ToolbarItem> Left, IReadOnlyList<ToolbarItem> Right)
    {
        /// <summary>
        /// The row as it was until 2026-10-13: <paramref name="strip"/>'s glyphs at the left (<paramref name="off"/> naming those on the
        /// off slab by their place, from 0), <paramref name="path"/> and <paramref name="model"/> (with <paramref name="modelMark"/>) at
        /// the right — or the name among the glyphs, before the <paramref name="modelAt"/>-th, when it is 0 or more.
        /// </summary>
        public ToolbarParts(string strip, string path, IReadOnlyList<int>? off = null, string model = "", int modelAt = -1, string modelMark = "")
            : this(OldLeft(strip, off, model, modelAt, modelMark), OldRight(path, model, modelAt, modelMark))
        {
        }

        private static List<ToolbarItem> OldLeft(string strip, IReadOnlyList<int>? off, string model, int modelAt, string mark)
        {
            ArgumentNullException.ThrowIfNull(strip);
            var items = strip.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select((g, i) => ToolbarItem.GlyphOf(g, off?.Contains(i) == true)).ToList();
            if (!string.IsNullOrEmpty(model) && modelAt >= 0)
            {
                items.Insert(Math.Min(modelAt, items.Count), ToolbarItem.ModelOf(model, mark ?? ""));
            }

            return items;
        }

        private static List<ToolbarItem> OldRight(string path, string model, int modelAt, string mark)
        {
            var items = new List<ToolbarItem>(2);
            if (!string.IsNullOrEmpty(path))
            {
                items.Add(ToolbarItem.PathOf(path));
            }

            if (!string.IsNullOrEmpty(model) && modelAt < 0)
            {
                items.Add(ToolbarItem.ModelOf(model, mark ?? ""));
            }

            return items;
        }

        /// <summary>The glyphs on the off slab as one comparable word, by their place among all the items, so the tick redraws a flip that leaves the row's text as it was.</summary>
        public string OffKey => string.Join(',', (Left ?? []).Concat(Right ?? []).Select((item, i) => item.Off ? i : -1).Where(i => i >= 0));
    }

    /// <summary>What an item of the toolbar is (2026-10-13): a glyph, the working directory's path, the model's name or (later that day) a divider.</summary>
    public enum ToolbarItemKind
    {
        Glyph,
        Path,
        Model,
        Divider,
    }

    /// <summary>What a divider draws on the tool bar and the home bar (2026-10-13, the user's pick: a dim bar). Pinned.</summary>
    public const string DividerGlyph = "│";

    /// <summary>
    /// One item of the toolbar (2026-10-13, <see cref="ToolbarParts"/>): a glyph — <paramref name="Off"/> on the off slab
    /// (<see cref="Theme.ToolbarOff"/>) —, the path, or the model's name with its reasoning <paramref name="Mark"/>.
    /// </summary>
    public readonly record struct ToolbarItem(ToolbarItemKind Kind, string Text, bool Off = false, string Mark = "")
    {
        public static ToolbarItem GlyphOf(string glyph, bool off = false) => new(ToolbarItemKind.Glyph, glyph, off);

        public static ToolbarItem PathOf(string path) => new(ToolbarItemKind.Path, path);

        public static ToolbarItem ModelOf(string name, string mark = "") => new(ToolbarItemKind.Model, name, false, mark);

        public static ToolbarItem DividerOf() => new(ToolbarItemKind.Divider, DividerGlyph);
    }

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

        /// <summary>The model's name, wherever it is drawn (2026-10-12) — the screen opens <c>/server</c> on a pair there, as the hint row's.</summary>
        Model,

        /// <summary>The model's reasoning mark after it (later on 2026-10-12) — the screen opens <c>/reasoning</c> on a pair there, as the hint row's.</summary>
        Mark,
    }

    /// <summary>Where on the toolbar a click landed: the zone, the strip glyph under it (<c>""</c> elsewhere) and the zone's first column (−1 for the row).</summary>
    public readonly record struct ToolbarHit(ToolbarZone Zone, string Glyph, int Column);

    /// <summary>A click anywhere on the performance bar (2026-09-29), or on the home bar's blanks (2026-10-12): the toolbar's blanks, so every reader answers it with <c>/settings</c>. Pinned.</summary>
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
    /// Whether a chord in a pane is nobody's just now: a tool-asked pane is open (<see cref="SuppressChords"/>), or there is no pane
    /// (2026-10-11 review: <see cref="Chord"/>'s true says this and "done in place" alike, so a host asks first).
    /// </summary>
    public bool ChordsOff => Volatile.Read(ref _chordsSuppressed) > 0 || !Enabled;

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
        if (ChordsOff)
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

            if ((_perfRows > 0 && y == top + PerfRowBelowCursor) || HomeBlankAt(x, y, top))
            {
                return new OffPaneHit(null, PerfBarHit);
            }

            if (_toolbarRows > 0 && y == top + ToolbarRowBelowCursor)
            {
                return new OffPaneHit(null, ToolbarHitNow(x));
            }

            if (_busyLabel is null && _hintsRows > 0 && y == top + HintsRowBelowCursor)
            {
                return new OffPaneHit(new HintHit(HintZone.Row, "", -1), null);   // the Hints bar's text (2026-10-14): /settings, as the Status bar's
            }

            if (_busyLabel is null && y == top + HintRowBelowCursor)
            {
                return new OffPaneHit(HintHitAt(_hintStrip, _hintStripColumn, _trailerColumn, _trailerEnd, _markColumn, _markEnd, _hintPathColumn, _hintPathCells, -1, 0, -1, 0, -1, 0, x), null);
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
            ToolbarZone.Model => ToolbarModelKey,
            ToolbarZone.Mark => ToolbarMarkKey,
            _ => -5 - tool.Column,
        },
        { Hint: { Zone: HintZone.Path } } => HintPathKey,
        { Hint: { Zone: HintZone.Timers } } => HintTimersKey,
        { Hint: { } row } => row.Zone == HintZone.Strip ? -200 - row.Column : -100 - (int)row.Zone,
        _ => MenuPane.OutsideRow,
    };

    /// <summary>The toolbar's model name in the off-pane and pairing keys (2026-10-12): far from every column's key. Pinned.</summary>
    public const int ToolbarModelKey = -500_002;

    /// <summary>The toolbar's reasoning mark in the off-pane and pairing keys (later on 2026-10-12). Pinned.</summary>
    public const int ToolbarMarkKey = -500_004;

    /// <summary>The clock the tick and the busy row run on; the overlays share it (a menu's double-click).</summary>
    public TimeProvider Time => _time;

    /// <summary>The standing hint (the state line under the input row), read on every redraw and on the tick.</summary>
    public Func<string> Hint
    {
        get => _hint;
        set => _hint = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>What a <see cref="HintSlot"/> stands for (2026-10-12): the pane's strip, its queued part, the model's name with its mark, or the slot's own text.</summary>
    public enum HintSlotKind
    {
        Strip,
        Queued,
        Model,
        Text,
        Path,   // later on 2026-10-12: the working directory, its text the slot's
        Usage,  // 2026-10-13: the token tally, its text the slot's (empty while it stands aside); the busy row puts its own tally there
        Spinner,   // later on 2026-10-13: the busy row's spinner, label and stop hint stand here; nothing on the standing row
        Timers,    // later still on 2026-10-13: the timers, its text the slot's; a double-click there asks to cancel them all
    }

    /// <summary>
    /// One part of the standing hint row in <see cref="HintLayout"/>'s order (2026-10-12): a kind, for <see cref="HintSlotKind.Text"/>
    /// and <see cref="HintSlotKind.Path"/> its text, and (later that day) whether it stands at the right edge rather than in the flow.
    /// </summary>
    public readonly record struct HintSlot(HintSlotKind Kind, string Text = "", bool Right = false)
    {
        public static HintSlot Strip { get; } = new(HintSlotKind.Strip);
        public static HintSlot Queued { get; } = new(HintSlotKind.Queued);
        public static HintSlot Model { get; } = new(HintSlotKind.Model);
        public static HintSlot Of(string text) => new(HintSlotKind.Text, text);
        public static HintSlot Path(string text) => new(HintSlotKind.Path, text);
        public static HintSlot UsageOf(string text) => new(HintSlotKind.Usage, text);
        public static HintSlot Spinner { get; } = new(HintSlotKind.Spinner);
        public static HintSlot TimersOf(string text) => new(HintSlotKind.Timers, text);

        /// <summary>This slot at the right edge (later on 2026-10-12: the model and the working directory while they ended the row; any slot pinned right since 2026-10-13).</summary>
        public HintSlot AtRight() => this with { Right = true };
    }

    /// <summary>The working directory on the hint row in the off-pane and pairing keys (later on 2026-10-12): far from every column's key. Pinned.</summary>
    public const int HintPathKey = -500_003;

    /// <summary>The timers on the hint row in the pairing keys (2026-10-13): far from every column's key. Pinned.</summary>
    public const int HintTimersKey = -500_004;

    /// <summary>
    /// The standing hint row's parts in order (2026-10-12, the user's ask: the Hint bar setting, its sections in any order), each
    /// non-empty one behind <see cref="HintSeparator"/>: <see cref="HintSlot.Strip"/> is <see cref="Strip"/>, <see cref="HintSlot.Queued"/>
    /// <see cref="Queued"/>, a text slot its text; <see cref="HintSlot.Model"/> puts <see cref="Trailer"/> and its mark in the flow there
    /// in place of the right edge, which keeps them while no slot names the model. Slots marked <see cref="HintSlot.AtRight"/> (later
    /// that day: the model and the working directory while they end the row) stand at the right edge in their order, the model's text
    /// cut as ever, a path from the front, <see cref="ToolbarModelSeparator"/> between. Null (the default) is the row as it always was:
    /// the strip, the queued part and <see cref="Hint"/>. Under an overlay the row is the strip and the overlay's hint either way, and
    /// the busy row keeps its own order. Read on every draw and on the tick.
    /// </summary>
    public Func<IReadOnlyList<HintSlot>>? HintLayout
    {
        get => _hintLayout;
        set => _hintLayout = value;
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
    /// The title at the right edge of the lower rule — the one under the input row — in every state (2026-10-12, the user's ask:
    /// the running processes' count, <c>▶ 3</c>, as the session's name sits on the rule above), read like <see cref="RuleTitle"/>
    /// on every draw and on the tick, which repaints the pane when it changes; empty = the bare rule, as it was until then.
    /// <see cref="RuleWithTitle"/> is the row, so a window too narrow for it keeps the bare rule.
    /// </summary>
    public Func<string> LowerRuleTitle
    {
        get => _lowerRuleTitle;
        set => _lowerRuleTitle = value ?? throw new ArgumentNullException(nameof(value));
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
    /// comes or goes (the screen's <c>Toolbar</c> switch). A window under
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
    /// when it comes or goes (the screen's <c>Performance bar</c> setting).
    /// </summary>
    public Func<int, UI.PerfRow?> Perf
    {
        get => _perf;
        set => _perf = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// The home bar (2026-10-12, the user's ask: Home Assistant's rooms and devices under the perf bar): asked with the cells
    /// the row has on every draw and on the tick; null draws no row. It takes the screen's last row, under the performance
    /// bar, when the window keeps a transcript row over the smallest pane and the other bars — so a short window drops it
    /// first; the tick rewrites it in place when its key (text and styles) changes and repaints the pane when it comes or goes.
    /// </summary>
    public Func<int, UI.HomeRow?> Home
    {
        get => _home;
        set => _home = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>The rows the home bar took in the last draw: 1 while drawn, else 0.</summary>
    public int HomeRows
    {
        get { lock (_gate) { return _homeRows; } }
    }

    /// <summary>
    /// The order of the rows under the lower rule, top to bottom (2026-10-12, the user's ask: <c>Toolbar order</c>, the hint bar
    /// under the performance bar for one): read on every draw and on the tick, which repaints the pane when it changes. Each of
    /// the four once (<see cref="Bars"/> mends a list that is not); <see cref="DefaultBarOrder"/> (the default here) is the pane as
    /// it always was. Only where the rows sit: which ones show, and which a short window drops first (the home bar, then the
    /// performance bar, then the toolbar), stay as they were.
    /// </summary>
    public Func<IReadOnlyList<PaneBar>> BarOrder
    {
        get => _barOrder;
        set => _barOrder = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>The Hints bar, the Status bar, the toolbar, the performance bar, the home bar: the default (the hint row, the toolbar, the performance bar, the home bar until 2026-10-14).</summary>
    public static readonly IReadOnlyList<PaneBar> DefaultBarOrder = [PaneBar.Hints, PaneBar.Status, PaneBar.Toolbar, PaneBar.Perf, PaneBar.Home];

    /// <summary>
    /// <see cref="BarOrder"/>'s answer as the draw takes it: each bar once, the first of a repeat, a missing one at the end in
    /// <see cref="DefaultBarOrder"/>'s order (the screen's order comes mended already, <see cref="App.PaneBars.Bars"/>).
    /// </summary>
    private static IReadOnlyList<PaneBar> Bars(IReadOnlyList<PaneBar>? order)
    {
        var bars = (order ?? DefaultBarOrder).Distinct().Where(DefaultBarOrder.Contains).ToList();
        bars.AddRange(DefaultBarOrder.Where(b => !bars.Contains(b)));
        return bars;
    }

    /// <summary>The rows every bar under the lower rule but the Status bar took in the last draw (the Hints bar, the toolbar, the performance bar, the home bar).</summary>
    public int BarRowsDrawn
    {
        get { lock (_gate) { return BarRows; } }
    }

    /// <summary>
    /// The rows the bars under the lower rule but the Status bar take with the Hints bar's row counted whenever it may have one
    /// (2026-10-14): what a picture sized ahead of its draw leaves room for (the splash, <c>/imagine</c>'s), so the bar coming
    /// with its hint never pushes the picture's top row off. <see cref="LayoutHeight"/> counts the same rows.
    /// </summary>
    public int ReservedBarRows
    {
        get { lock (_gate) { return ReservedBarRowsFor(Height); } }
    }

    /// <summary>
    /// The setting <c>Hints bar</c>'s saved word (2026-10-14, <see cref="HintsBarMode"/>): read on every draw and on the tick.
    /// <see cref="HintsBarMode.Off"/> by default, the pane as it was: no Hints bar, the hints on the Status bar.
    /// </summary>
    public Func<string> HintsBar
    {
        get => _hintsBar;
        set => _hintsBar = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// The screen's armed prompt (2026-10-14: the second Ctrl+C's exit, a rewind, a splash picture's delete): the Hints bar's second
    /// rung, after the kill switch's alert. Read at rest only; null (the default) for none.
    /// </summary>
    public Func<string?> ArmedHint
    {
        get => _armedHint;
        set => _armedHint = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// The screen's keys' lead (2026-10-14: the <c>/read</c> keys, the splash's or the picture strip's arrows): the Hints bar's last
    /// rung, after an open pane's keys. Read at rest only, as the hint row had it; null (the default) for none.
    /// </summary>
    public Func<string?> KeysHint
    {
        get => _keysHint;
        set => _keysHint = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>How long a held Hints bar row stays blank with no hint and no key pressed, at rest, before it goes (2026-10-14). Pinned.</summary>
    public static readonly TimeSpan HintsIdle = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Whether the hints go to the Hints bar rather than the Status bar (2026-10-14): the pane is enabled, <see cref="HintsBar"/> is not
    /// <c>off</c> and the window has a row for it. The screen leaves its armed prompt and keys off the Status bar's layout while true.
    /// </summary>
    public bool HintsBarShown
    {
        get { lock (_gate) { return HintsReservedFor(Height) > 0; } }
    }

    /// <summary>The rows the Hints bar took in the last draw: 1 while drawn, else 0.</summary>
    public int HintsRows
    {
        get { lock (_gate) { return _hintsRows; } }
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
    /// The draft row's emoji button (2026-10-11, the user's ask; the screen answers its <c>Emoji picker</c> setting):
    /// <see cref="EmojiGlyph"/> in <see cref="Theme.Hint"/> at the first input row's end, one blank before it and the last column
    /// left empty as every row's is, the draft wrapped <see cref="EmojiButtonCells"/> narrower to keep clear of it. Drawn only at
    /// the idle line: not under an overlay, nor while a spinner runs (a reply's clicks are the watcher's), nor in a window too
    /// narrow to spare the cells. False = no button (the default). Read at each layout; a click on it is
    /// <see cref="TryHitEmojiButton"/>.
    /// </summary>
    public Func<bool> EmojiButton
    {
        get => _emojiButton;
        set => _emojiButton = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// The emoji button's glyph (2026-10-11, the user's pick of four): the CP437 smiley, a text glyph the terminal's font draws, so
    /// <see cref="Theme.Hint"/>'s grey dims it as no colour emoji can be dimmed. One cell.
    /// </summary>
    public const string EmojiGlyph = "☺";

    /// <summary>The cells the emoji button takes from the draft's wrap: the blank before <see cref="EmojiGlyph"/> and the glyph.</summary>
    public const int EmojiButtonCells = 2;

    // The fewest cells the draft keeps for itself before the button gives way (a very narrow window).
    private const int EmojiButtonMinCells = 10;

    /// <summary>Whether a layout at <paramref name="width"/> makes room for the emoji button now (see <see cref="EmojiButton"/>).</summary>
    private bool EmojiButtonApplies(int width) =>
        _overlay is null && _busyLabel is null && InputLine.AvailableCells(width) - EmojiButtonCells >= EmojiButtonMinCells && _emojiButton();

    /// <summary>The cells a draft row wraps at, at <paramref name="width"/>: <see cref="InputLine.AvailableCells"/>, less the emoji button's while the last layout made room for it.</summary>
    private int InputCells(int width) =>
        _emojiShown ? InputLine.AvailableCells(width) - EmojiButtonCells : InputLine.AvailableCells(width);

    /// <summary>
    /// Whether a click at buffer cell (<paramref name="x"/>, <paramref name="y"/>) landed on the draft row's emoji button
    /// (2026-10-11, <see cref="EmojiButton"/>), or the cell either side of it — three cells, as the strip's × gives. False with no
    /// button drawn, the pane lifted, disabled or under an overlay, or no geometry.
    /// </summary>
    public bool TryHitEmojiButton(int x, int y)
    {
        if (!Enabled)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_drawn || _drawnOverlay || !_emojiShown || _batch > 0 || _modal > 0 || _geometry?.CursorTop() is not int top)
            {
                return false;
            }

            // The terminal's cursor is on the area's row _cursorRow; the button is on its first row.
            int column = Width - 2;
            return y == top - _cursorRow && x >= column - 1 && x <= column + 1;
        }
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
    /// overlay's. The screen puts the strip away until the 🎞️ brings it back (<see cref="UI.PictureStrip.Close"/>). False as
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

    /// <summary>
    /// Whether a click at buffer cell (<paramref name="x"/>, <paramref name="y"/>) landed on the lower rule's title (2026-10-12,
    /// the running processes' count: a double-click there opens <c>/process</c>), the space either side of it included. False
    /// with no title drawn, an overlay drawn, the pane lifted or disabled, or no geometry — <see cref="TryHitRuleTitle"/>'s rules.
    /// </summary>
    public bool TryHitLowerRuleTitle(int x, int y)
    {
        if (!Enabled)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_drawn || _drawnOverlay || _lowerTitleColumn < 0 || _batch > 0 || _modal > 0 || _geometry?.CursorTop() is not int top)
            {
                return false;
            }

            return y == top + FirstBarRowBelowCursor - 1 && x >= _lowerTitleColumn - 1 && x <= _lowerTitleColumn + _lowerTitleCells;
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
        if (StripRowsFor(Width, Height, ReservedBarRowsFor(Height)) != _stripRows)
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
    /// Whether a click at buffer cell (<paramref name="x"/>, <paramref name="y"/>) landed on the drawn strip's
    /// <see cref="UI.PictureStrip.MoreLeft"/> or <see cref="UI.PictureStrip.MoreRight"/> (2026-10-10, the user's ask: the arrows
    /// step through the pictures): <paramref name="step"/> −1 for ‹, +1 for ›, what ← / → on an empty line pass. The glyph is on
    /// the middle row; its column on any of the strip's rows counts, so the one cell is a target as tall as the strip. False as
    /// <see cref="TryHitStrip"/> is, and where no arrow is drawn.
    /// </summary>
    public bool TryHitStripArrow(int x, int y, out int step)
    {
        step = 0;
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

            int rule = top - CursorDepth - 1;
            if (y < rule - _stripRows + StripRuleRows || y >= rule)
            {
                return false;
            }

            step = x == _stripArrows.Left ? -1 : x == _stripArrows.Right ? 1 : 0;
            return step != 0 && x >= 0;
        }
    }

    /// <summary>
    /// The window height less the toolbar's row when <see cref="Toolbar"/> answers one: what an
    /// overlay host lays out against (the menus, the info and folder panes, the completion list),
    /// since <see cref="MaxOverlayRows"/> and <see cref="MaxInputRows"/> are counted over the rows
    /// the toolbar leaves. The provider, not the drawn count: the host lays out BEFORE the draw. The Hints bar's row is counted
    /// whenever it may have one (2026-10-14, <see cref="ReservedBarRows"/>): every pane brings its keys, so the bar is up with it.
    /// </summary>
    public int LayoutHeight => Height - ReservedBarRowsFor(Height);

    /// <summary>
    /// The rows the Hints bar takes in a window of <paramref name="height"/> (2026-10-14): one while <see cref="HintsBar"/> is not
    /// <c>off</c>, the window keeps a transcript row over the smallest pane, and the bar is <c>always</c>, has a hint, or holds the
    /// row it was drawn with and no collapse is due; else none. First in the short window's chain: the other bars give way to it.
    /// </summary>
    private int HintsRowsFor(int height)
    {
        if (HintsReservedFor(height) == 0)
        {
            return 0;
        }

        return HintsBarMode.Resolve(_hintsBar()) == HintsBarStyle.Always || HintsText() is not null || (_hintsRows > 0 && !_hintsCollapseDue) ? 1 : 0;
    }

    /// <summary>The row the Hints bar may take in a window of <paramref name="height"/> (2026-10-14): one while enabled, not <c>off</c>, and the window keeps a transcript row over the smallest pane.</summary>
    private int HintsReservedFor(int height) =>
        Enabled && HintsBarMode.Resolve(_hintsBar()) != HintsBarStyle.Off && height >= PaneRows + 2 ? 1 : 0;

    /// <summary>The rows the toolbar takes in a window of <paramref name="height"/> over <paramref name="hints"/> Hints bar rows: one when <see cref="Toolbar"/> answers and the window keeps a transcript row over the smallest pane and them, else none.</summary>
    private int ToolbarRowsFor(int height, int hints) => _toolbar() is not null && height >= PaneRows + 2 + hints ? 1 : 0;

    /// <summary>The rows the performance bar takes in a window of <paramref name="height"/>: one when <see cref="Perf"/> answers and the window keeps a transcript row over the smallest pane, the Hints bar and the toolbar, else none.</summary>
    private int PerfRowsFor(int height, int hints) => _perf(Math.Max(1, Width - 1)) is not null && height >= PaneRows + 2 + hints + ToolbarRowsFor(height, hints) ? 1 : 0;

    /// <summary>The rows the home bar takes in a window of <paramref name="height"/> (2026-10-12): one when <see cref="Home"/> answers and the window keeps a transcript row over the smallest pane and the other bars, else none.</summary>
    private int HomeRowsFor(int height, int hints) => _home(Math.Max(1, Width - 1)) is not null && height >= PaneRows + 2 + hints + ToolbarRowsFor(height, hints) + PerfRowsFor(height, hints) ? 1 : 0;

    /// <summary>The rows under the lower rule but the Status bar in a window of <paramref name="height"/>, the Hints bar's counted while it may have one.</summary>
    private int ReservedBarRowsFor(int height)
    {
        int hints = HintsReservedFor(height);
        return hints + ToolbarRowsFor(height, hints) + PerfRowsFor(height, hints) + HomeRowsFor(height, hints);
    }

    /// <summary>The rows under the lower rule but the Status bar as last drawn.</summary>
    private int BarRows => _hintsRows + _toolbarRows + _perfRows + _homeRows;

    /// <summary>
    /// The Hints bar's text now (2026-10-14, the ladder, <see cref="HintsBarMode.Pick"/>): the kill switch's alert (a flash stays on the
    /// Status bar), then at rest the armed prompt, then an open pane's keys, then at rest the keys' lead; null for none.
    /// </summary>
    private string? HintsText() => HintsBarMode.Pick(
        _alertHint is { } alert && !_alertFlash ? alert : null,
        _busyLabel is null ? _armedHint() : null,
        _overlay?.Hint,
        _busyLabel is null ? _keysHint() : null);

    /// <summary>An open pane's keys stand on the Status bar (the hint row's way): a pane is open and the Hints bar has no row (2026-10-14).</summary>
    private bool OverlayOnStatus => _overlay is not null && _hintsRows == 0;

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
    /// The least cells the model's name is cut to (2026-09-28, the user's call, sixteen; eight since later on 2026-10-12, the user's
    /// ask: the model's name and the working directory collapse to make room for anything else on the hint row and the toolbar): a
    /// name that does not fit is cut down to this, and with less room it goes, its reasoning mark with it (<see cref="FitName"/>).
    /// Until then the name kept sixteen and the text on its left was cut instead.
    /// </summary>
    public const int TrailerMinCells = 8;

    /// <summary>
    /// <paramref name="name"/> in <paramref name="room"/> cells (later on 2026-10-12): whole when it fits, cut (<see cref="Fit"/>) when
    /// the room is <see cref="TrailerMinCells"/> or more, else nothing — it collapses for what stands beside it. Pure.
    /// </summary>
    public static string FitName(string name, int room) =>
        TextCells.Width(name) <= room ? name : room >= TrailerMinCells ? Fit(name, room) : "";

    /// <summary>
    /// <paramref name="path"/> in <paramref name="room"/> cells (later on 2026-10-12, <see cref="FitName"/>'s rule): whole when it fits
    /// (a short one too, under <see cref="ToolbarPathMinCells"/>), cut from the front (<see cref="FitTail"/>) when the room is
    /// <see cref="ToolbarPathMinCells"/> or more, else nothing. Pure.
    /// </summary>
    public static string FitPath(string path, int room) =>
        path.Length == 0 || room < 1 ? "" : TextCells.Width(path) <= room ? path : room >= ToolbarPathMinCells ? FitTail(path, room) : "";

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
    public static string ToolbarRow(string strip, string path, int cells) => ToolbarRow(strip, path, "", cells);

    /// <summary>
    /// <see cref="ToolbarRow(string, string, int)"/> with the model's name at the right edge (2026-10-12, the user's ask: the Tool bar's
    /// <c>Model name</c>, after the path by default): the name on the last cells, cut to half the row and to what the strip leaves,
    /// the path ahead of it behind <see cref="ToolbarModelSeparator"/>, cut from the front to what is left; the name alone without a
    /// path. Pinned.
    /// </summary>
    public static string ToolbarRow(string strip, string path, string model, int cells) => ToolbarRow(strip, path, model, "", cells);

    /// <summary><see cref="ToolbarRow(string, string, string, int)"/> with the model's reasoning <paramref name="mark"/> after its name (later on 2026-10-12), never cut while the name has a cell. Pinned.</summary>
    public static string ToolbarRow(string strip, string path, string model, string mark, int cells)
    {
        ArgumentNullException.ThrowIfNull(strip);
        return LayToolbar(new ToolbarParts(strip, path, null, model, -1, mark), cells, spaced: true).Row(cells);
    }

    /// <summary>
    /// The model's name and its <paramref name="mark"/> in <paramref name="room"/> cells (later on 2026-10-12): the name fitted ahead of
    /// the mark as <see cref="FitName"/> fits it, as <see cref="Trail"/> fits the hint row's; empty — the mark gone too — when the name
    /// collapses, or for no name.
    /// </summary>
    private static string NameAndMark(string model, string mark, int room)
    {
        if (room < 1 || model.Length == 0)
        {
            return "";
        }

        int markCells = mark.Length == 0 ? 0 : TextCells.Width(mark) + MarkSeparator.Length;
        string name = FitName(model, room - markCells);
        return name.Length == 0 ? "" : TrailerText(name, mark);
    }

    /// <summary>Between the toolbar's path and the model's name after it (2026-10-12). Pinned.</summary>
    public const string ToolbarModelSeparator = " · ";

    /// <summary>
    /// A run of the laid toolbar (2026-10-13): glyphs side by side (<paramref name="Off"/> their places on the slab, from 0), the path,
    /// or the model's name and mark. (A right group's glyphs cut short had a <c>+N</c> lead until later that day, when the user had the
    /// glyphs a narrow row leaves off simply go, as the home bar's chips do.)
    /// </summary>
    private readonly record struct ToolbarRun(ToolbarItemKind Kind, string Text, IReadOnlyList<int> Off, string Mark = "");

    /// <summary>A run where it was drawn (2026-10-13): its text's first column and its cells.</summary>
    private readonly record struct ToolbarPlaced(ToolbarItemKind Kind, string Text, int Column, int Cells, string Mark);

    /// <summary>
    /// The toolbar laid out in its cells (2026-10-12; two groups since 2026-10-13): the runs pinned left from column 0 and the ones
    /// pinned right ending on the last cell, <see cref="ToolbarGap"/> at least between.
    /// </summary>
    private readonly record struct ToolbarLaid(IReadOnlyList<ToolbarRun> Left, IReadOnlyList<ToolbarRun> Right)
    {
        /// <summary>The left group's text.</summary>
        public string LeftText => JoinRuns(Left);

        /// <summary>The right group's text.</summary>
        public string RightText => JoinRuns(Right);

        /// <summary>The whole row of <paramref name="cells"/>: the left group, blanks, the right group ending on the last cell.</summary>
        public string Row(int cells)
        {
            string left = LeftText;
            string right = RightText;
            return right.Length == 0 ? left : left + new string(' ', Math.Max(0, cells - TextCells.Width(left) - TextCells.Width(right))) + right;
        }

        /// <summary>Each run with its first column in a row of <paramref name="cells"/>, the left group's then the right's.</summary>
        public IEnumerable<(ToolbarRun Run, int Column, string Before)> Placed(int cells)
        {
            int column = 0;
            for (int i = 0; i < Left.Count; i++)
            {
                string before = i == 0 ? "" : RunSeparator(Left[i - 1], Left[i]);
                column += TextCells.Width(before);
                yield return (Left[i], column, before);
                column += TextCells.Width(Left[i].Text);
            }

            string right = RightText;
            int start = right.Length == 0 ? column : Math.Max(column, cells - TextCells.Width(right));
            for (int i = 0; i < Right.Count; i++)
            {
                string before = i == 0 ? new string(' ', start - column) : RunSeparator(Right[i - 1], Right[i]);
                column += TextCells.Width(before);
                yield return (Right[i], column, before);
                column += TextCells.Width(Right[i].Text);
            }
        }
    }

    /// <summary>Between two runs of a group: <see cref="ToolbarModelSeparator"/> between two texts (the path and the name), else one blank (2026-10-13); a blank either side of a divider (later that day).</summary>
    private static string RunSeparator(ToolbarRun before, ToolbarRun after) =>
        before.Kind is ToolbarItemKind.Path or ToolbarItemKind.Model && after.Kind is ToolbarItemKind.Path or ToolbarItemKind.Model ? ToolbarModelSeparator : " ";

    /// <summary>
    /// <paramref name="runs"/> without a divider the squeeze left at either end or beside another (later on 2026-10-13, the shared
    /// rule: a divider stands only between two drawn items).
    /// </summary>
    private static List<ToolbarRun> TrimDividers(List<ToolbarRun> runs)
    {
        var kept = new List<ToolbarRun>(runs.Count);
        foreach (var run in runs)
        {
            if (run.Kind == ToolbarItemKind.Divider && (kept.Count == 0 || kept[^1].Kind == ToolbarItemKind.Divider))
            {
                continue;
            }

            kept.Add(run);
        }

        while (kept.Count > 0 && kept[^1].Kind == ToolbarItemKind.Divider)
        {
            kept.RemoveAt(kept.Count - 1);
        }

        return kept;
    }

    private static string JoinRuns(IReadOnlyList<ToolbarRun> runs)
    {
        var text = new System.Text.StringBuilder();
        for (int i = 0; i < runs.Count; i++)
        {
            text.Append(i == 0 ? "" : RunSeparator(runs[i - 1], runs[i])).Append(runs[i].Text);
        }

        return text.ToString();
    }

    /// <summary>
    /// <paramref name="toolbar"/> laid out in <paramref name="cells"/> (2026-10-12; two groups since 2026-10-13, the user's ask). What
    /// gives way when the row is too narrow, in the user's order: the model's name and the path first, wherever they are pinned — the
    /// name fitted (<see cref="NameAndMark"/>, half the row at most at the right) to what everything but the path leaves, then the path
    /// (<see cref="FitPath"/>) to what is left, each collapsing under eight cells; then the right group, from its inner end (whole runs
    /// go, a glyph run keeping its outer glyphs); then the left group from its inner end (<see cref="FitStrip"/>). What gives way leaves
    /// nothing behind (later on 2026-10-13, the user's call: no <c>+N</c>).
    /// <paramref name="spaced"/>: the glyphs already carry <see cref="TextCells.SpaceSelectorSequences"/>' blanks.
    /// </summary>
    private static ToolbarLaid LayToolbar(ToolbarParts toolbar, int cells, bool spaced = false)
    {
        string Space(string s) => spaced ? s : TextCells.SpaceSelectorSequences(s, TextCells.NarrowSelectorSequences);
        var left = Runs(toolbar.Left ?? [], Space);
        var right = Runs(toolbar.Right ?? [], Space);

        // The name first, in what all but the path leaves; then the path in what is left (the path collapses first, as it always did).
        bool nameRight = right.Any(r => r.Kind == ToolbarItemKind.Model);
        FitText(ToolbarItemKind.Model, (run, room) => NameAndMark(run.Text, run.Mark, nameRight ? Math.Min(Math.Max(1, cells / 2), room) : room), withoutPath: true);
        FitText(ToolbarItemKind.Path, (run, room) => FitPath(run.Text, room), withoutPath: false);
        left = TrimDividers(left);
        right = TrimDividers(right);

        int leftCells = TextCells.Width(JoinRuns(left));
        if (right.Count > 0)
        {
            int room = cells - leftCells - (left.Count == 0 ? 0 : ToolbarGap);
            right = room < 1 ? [] : TrimDividers(FitRight(right, room));
        }

        if (right.Count == 0 && leftCells > cells)
        {
            left = TrimDividers(FitLeft(left, cells));
        }

        return new ToolbarLaid(left, right);

        // A text run fitted to the room the rest of the row leaves it (a stand-in of one cell measures where it goes); gone when empty.
        void FitText(ToolbarItemKind kind, Func<ToolbarRun, int, string> fit, bool withoutPath)
        {
            foreach (var group in new[] { left, right })
            {
                int at = group.FindIndex(r => r.Kind == kind);
                if (at < 0)
                {
                    continue;
                }

                var run = group[at];
                group[at] = run with { Text = "x", Mark = "" };
                int rest = Measure(withoutPath) - 1;
                string text = fit(run, cells - rest);
                if (text.Length == 0)
                {
                    group.RemoveAt(at);
                }
                else
                {
                    group[at] = run with { Text = text };
                }
            }
        }

        int Measure(bool withoutPath)
        {
            var l = withoutPath ? left.Where(r => r.Kind != ToolbarItemKind.Path).ToList() : left;
            var r = withoutPath ? right.Where(r => r.Kind != ToolbarItemKind.Path).ToList() : right;
            int width = TextCells.Width(JoinRuns(l)) + TextCells.Width(JoinRuns(r));
            return width + (l.Count > 0 && r.Count > 0 ? ToolbarGap : 0);
        }
    }

    /// <summary>The items as runs: glyphs side by side one run (spaced, their slabs counted from 0 within it), the path and the name one each, empties dropped.</summary>
    private static List<ToolbarRun> Runs(IReadOnlyList<ToolbarItem> items, Func<string, string> space)
    {
        var runs = new List<ToolbarRun>();
        var glyphs = new List<string>();
        var off = new List<int>();
        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.Text))
            {
                continue;
            }

            if (item.Kind == ToolbarItemKind.Glyph)
            {
                if (item.Off)
                {
                    off.Add(glyphs.Count);
                }

                glyphs.Add(item.Text);
                continue;
            }

            Flush();
            runs.Add(new ToolbarRun(item.Kind, item.Text, [], item.Mark ?? ""));   // a divider a run of its own (later on 2026-10-13)
        }

        Flush();
        return runs;

        void Flush()
        {
            if (glyphs.Count > 0)
            {
                runs.Add(new ToolbarRun(ToolbarItemKind.Glyph, space(string.Join(' ', glyphs)), off.ToList()));
            }

            glyphs.Clear();
            off.Clear();
        }
    }

    /// <summary>
    /// The right group in <paramref name="room"/> cells (2026-10-13): whole runs kept from its outer (right) end; the first that does not
    /// fit is a glyph run's outer glyphs, a path cut from the front, or gone — and everything before it goes.
    /// </summary>
    private static List<ToolbarRun> FitRight(List<ToolbarRun> runs, int room)
    {
        if (TextCells.Width(JoinRuns(runs)) <= room)
        {
            return runs;
        }

        var kept = new List<ToolbarRun>();
        for (int i = runs.Count - 1; i >= 0; i--)
        {
            var candidate = new List<ToolbarRun> { runs[i] };
            candidate.AddRange(kept);
            if (TextCells.Width(JoinRuns(candidate)) <= room)
            {
                kept = candidate;
                continue;
            }

            int left = room - (kept.Count == 0 ? 0 : TextCells.Width(JoinRuns(kept)) + TextCells.Width(RunSeparator(runs[i], kept[0])));
            var cut = runs[i].Kind switch
            {
                ToolbarItemKind.Glyph => FitStripTail(runs[i], left),
                ToolbarItemKind.Path when FitPath(runs[i].Text, left) is { Length: > 0 } path => runs[i] with { Text = path },
                _ => (ToolbarRun?)null,
            };
            if (cut is { } shown)
            {
                kept.Insert(0, shown);
            }

            break;
        }

        return kept;
    }

    /// <summary>The left group alone in <paramref name="cells"/> (2026-10-13): whole runs from column 0; the first that does not fit a glyph run's first glyphs (<see cref="FitStrip"/>), a path or a name cut, or gone; nothing after it.</summary>
    private static List<ToolbarRun> FitLeft(List<ToolbarRun> runs, int cells)
    {
        var kept = new List<ToolbarRun>();
        foreach (var run in runs)
        {
            var candidate = new List<ToolbarRun>(kept) { run };
            if (TextCells.Width(JoinRuns(candidate)) <= cells)
            {
                kept = candidate;
                continue;
            }

            int room = cells - (kept.Count == 0 ? 0 : TextCells.Width(JoinRuns(kept)) + TextCells.Width(RunSeparator(kept[^1], run)));
            string text = room < 1 ? "" : run.Kind switch
            {
                ToolbarItemKind.Glyph => FitStrip(run.Text, room),
                ToolbarItemKind.Path => FitPath(run.Text, room),
                ToolbarItemKind.Divider => "",
                _ => NameAndMark(run.Text, "", room),
            };
            if (text.Length > 0)
            {
                kept.Add(run with { Text = text, Mark = text.EndsWith(run.Mark, StringComparison.Ordinal) ? run.Mark : "" });
            }

            break;
        }

        return kept;
    }

    /// <summary>
    /// A glyph run of the right group in <paramref name="cells"/> (2026-10-13): its last glyphs, the ones left off gone (behind a <c>+N</c>
    /// until later that day, the user's call), the slabs counted on from the first kept; null when not even one glyph fits.
    /// </summary>
    private static ToolbarRun? FitStripTail(ToolbarRun run, int cells)
    {
        string[] glyphs = run.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int keep = glyphs.Length - 1; keep > 0; keep--)
        {
            string text = string.Join(' ', glyphs, glyphs.Length - keep, keep);
            if (TextCells.Width(text) <= cells)
            {
                int dropped = glyphs.Length - keep;
                return run with { Text = text, Off = run.Off.Where(i => i >= dropped).Select(i => i - dropped).ToList() };
            }
        }

        return null;
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
    /// The right part of a hint row of <paramref name="cells"/>: <paramref name="trailer"/> and its <paramref name="mark"/> in half the
    /// row at most, giving way to <paramref name="leftCells"/>, the uncut width of what stands on its left (2026-09-28, the user's
    /// picture: an overlay's hint cut while a long model name kept its half), and <see cref="TrailerGap"/>. The name is fitted ahead of
    /// the mark as <see cref="FitName"/> fits it: whole, cut to <see cref="TrailerMinCells"/> at least, or gone with its mark — since
    /// later on 2026-10-12 (the user's ask) it collapses for the left rather than cutting it. None on the left (the default) is the
    /// half alone. Pinned.
    /// </summary>
    public static string Trail(string trailer, string mark, int cells, int leftCells = 0)
    {
        ArgumentNullException.ThrowIfNull(trailer);
        ArgumentNullException.ThrowIfNull(mark);
        int markCells = mark.Length == 0 ? 0 : TextCells.Width(mark) + MarkSeparator.Length;
        int room = Math.Min(cells / 2, cells - leftCells - TrailerGap);
        if (trailer.Length == 0)
        {
            return TextCells.Width(mark) <= room ? mark : "";   // a mark with no name, where it fits
        }

        string name = FitName(trailer, room - markCells);
        return name.Length == 0 ? "" : TrailerText(name, mark);
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

    /// <summary>The pane's rows as the last redraw laid them out, rules, bars and all: the window's height less it is the upper rule's row, the scroll's row over it aside (tests, 2026-10-14).</summary>
    public int DrawnRows
    {
        get
        {
            lock (_gate)
            {
                return _paneRows;
            }
        }
    }

    /// <summary>The overlay's rows drawn by the last redraw — its content and the held height's blank rows under it (2026-10-14) — 0 without one (tests).</summary>
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
            int next = top >= max && _find is null ? -1 : top;   // a find holds the store's last window (2026-10-07)
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

    /// <summary>
    /// The transcript's rows at the window's width, a copy (2026-10-07, phase 5 of the UI round: the find looks through them);
    /// none while disabled.
    /// </summary>
    public IReadOnlyList<SegmentLine> TranscriptRows()
    {
        if (!Enabled)
        {
            return [];
        }

        lock (_gate)
        {
            return _store.Rows(Width).ToList();
        }
    }

    /// <summary>
    /// The folds that hide a line holding <paramref name="find"/> opened (<see cref="Scrollback.UnfoldMatching"/>), the screen
    /// rebuilt when anything opened; what opened, for <see cref="Refold"/>. Disabled: nothing.
    /// </summary>
    public IReadOnlyList<Scrollback.Unfolded> UnfoldMatching(string find)
    {
        if (!Enabled)
        {
            return [];
        }

        lock (_gate)
        {
            var opened = _store.UnfoldMatching(find);
            RedrawIfReshaped();
            return opened;
        }
    }

    /// <summary>The folds <see cref="UnfoldMatching"/> opened, put back as they were. Disabled: nothing.</summary>
    public void Refold(IReadOnlyList<Scrollback.Unfolded> opened)
    {
        if (!Enabled || opened.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            _store.Refold(opened);
            RedrawIfReshaped();
        }
    }

    /// <summary>
    /// The transcript's find shown (2026-10-07, phase 5 of the UI round): every place <paramref name="find"/> occurs in the rows the
    /// region paints is marked (<see cref="TextFind.Mark"/>), the one at store row <paramref name="row"/> from character
    /// <paramref name="start"/> as the current one, and the region scrolled to put that row a third of the way down. While a find
    /// is shown the region is painted from the store even at its last window (where it would be the bottom's flow, unmarked); a
    /// <paramref name="row"/> of −1 leaves the view where it is. Disabled: nothing.
    /// </summary>
    public void ShowFind(string find, int row, int start)
    {
        ArgumentNullException.ThrowIfNull(find);
        if (!Enabled)
        {
            return;
        }

        lock (_gate)
        {
            _find = find;
            _findRow = row;
            _findStart = start;
            if (row >= 0)
            {
                int region = ScrolledRegionRows;
                int max = Math.Max(0, _store.Rows(Width).Count - region);
                _top = Math.Clamp(row - region / 3, 0, max);
            }

            Redraw();
        }
    }

    /// <summary>The find gone (<see cref="ShowFind"/>): the marks off and the region at the bottom again. Disabled, or no find: nothing.</summary>
    public void EndFind()
    {
        if (!Enabled)
        {
            return;
        }

        lock (_gate)
        {
            if (_find is null)
            {
                return;
            }

            _find = null;
            _findRow = -1;
            _findStart = -1;
            _top = -1;
            Redraw();
        }
    }

    /// <summary>A store row as the region paints it: with the find's places marked while one is shown (<see cref="ShowFind"/>).</summary>
    private List<Segment> FindMarked(IReadOnlyList<SegmentLine> rows, int row)
    {
        var line = rows[row];
        if (_find is not { Length: > 0 } find)
        {
            return line;
        }

        var starts = TextFind.Matches([line], find).Select(hit => hit.Start).ToList();
        return starts.Count == 0 ? line : TextFind.Mark(line, starts, find.Length, row == _findRow ? _findStart : -1).ToList();
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
            var segments = lead is null ? null : SpacedSegments(lead.GetSegments(_inner));
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
            // The run's header, ▸ 🛠️ 3 tool calls, spaced as the lines under it are (later on 2026-10-06, the user's report).
            _store.SetGroupSummary(SpacedSegments(collapsed.GetSegments(_inner)), SpacedSegments(expanded.GetSegments(_inner)));
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

            var segments = SpacedSegments(renderable.GetSegments(_inner));
            // A picture's spans are known once it is rendered (later on 2026-09-24): the store tags its lines with them.
            var spans = pictures?.Spans;
            // A diff's fold (2026-10-04): the store makes its lines a group that folds once the run is over.
            var fold = renderable is IFoldLayout { Fold: { } layout }
                ? new Scrollback.FoldSpec(layout.Head, layout.Keep, layout.Size, SpacedSegments(layout.Collapsed.GetSegments(_inner)), SpacedSegments(layout.Expanded.GetSegments(_inner)))
                : null;
            FlowLocked(segments, member, spans, fold);
        }
    }

    /// <summary>
    /// Where the transcript's next write starts (2026-10-11): a mark for <see cref="LinesSince"/>, read right after the welcome splash
    /// is drawn first so the lines the startup writes under it can be put back under a refitted picture.
    /// </summary>
    public Scrollback.Place StoreMark
    {
        get
        {
            lock (_gate)
            {
                return _store.Mark;
            }
        }
    }

    /// <summary>The lines written since <paramref name="mark"/> (<see cref="StoreMark"/>), copied at the window's width; null with nothing or a line that folds or holds a picture (<see cref="Scrollback.Since"/>).</summary>
    public Scrollback.Copied? LinesSince(Scrollback.Place mark)
    {
        if (!Enabled)
        {
            return null;
        }

        lock (_gate)
        {
            return _store.Since(mark, Width);
        }
    }

    /// <summary>
    /// <paramref name="lines"/> (<see cref="LinesSince"/>) written to the flow again, as they were, their pictures where they were:
    /// after a wipe, under what was drawn since.
    /// </summary>
    public void Rewrite(Scrollback.Copied lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (!Enabled)
        {
            return;
        }

        lock (_gate)
        {
            if (_modal > 0)
            {
                return;
            }

            FlowLocked([.. lines.Segments], member: false, spans: null, fold: null);
        }
    }

    /// <summary>One flow write's segments out, under the gate: <see cref="WriteFlow"/>'s and <see cref="Rewrite"/>'s.</summary>
    private void FlowLocked(List<Segment> segments, bool member, IReadOnlyList<IReadOnlyList<PictureSpan>>? spans, Scrollback.FoldSpec? fold)
    {
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
        _flowCut = first > 0;
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

    /// <summary>
    /// Flow rows went off the top (<see cref="_flowCut"/>) and the pane this draw lays out leaves room over it for more than the
    /// flow on the screen (2026-10-09): a tall pane closed, a long draft sent, the strip put away. Read after the draw set
    /// <see cref="_paneRows"/>. A reply streaming counts its rows as the last draw laid them out (<see cref="_liveCount"/>), so a
    /// tick under a stream, the live block filling the room, never rewrites the screen.
    /// </summary>
    private bool FlowCutWithRoom() =>
        _flowCut && (_col > 0 ? _row + 1 : _row) + (_live is null ? 0 : _liveCount) + _paneRows < Height;

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
            _flowCut = false;
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
    /// <para>A titled overlay holds the pane's height until it closes (2026-10-14, the user's ask): a shorter one shown in
    /// its place (a menu's value picker, a yes/no, a page with fewer rows) gets blank rows under its content up to the
    /// tallest pane drawn since it opened, so the top rule stays put; a taller one grows the pane and is held from then on.
    /// <see cref="CloseOverlay"/> ends the hold, as do a change of the window's height and a new Menus max height.</para>
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
            // A pane opening is a natural redraw (2026-10-14): a Hints bar row held blank may go with it. A pane shown again (a
            // menu's key, the completion list's next letter) is not.
            _hintsCollapseDue |= _overlay is null;
            if (_overlay is null)
            {
                // A visit starts from its own height (2026-10-14).
                _paneFloor = 0;
                _floorMenuHeight = null;
            }

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
            _paneFloor = 0;   // the visit's held height goes with it (2026-10-14), a stray close with nothing open too
            _floorMenuHeight = null;
            if (_overlay is null)
            {
                return;
            }

            // The drawn rows stay until the redraw: the lift steps up over them (Draw resets them). A pane closing is a natural
            // redraw (2026-10-14): the Hints bar's row goes with its keys unless another hint stands.
            _overlay = null;
            _hintsCollapseDue = true;
            Redraw();
        }
    }

    /// <summary>
    /// The overlay on show and the pane's held height, for a pane that opens over it to hand back with
    /// <see cref="RestoreOverlay"/> (2026-10-14); none without one, or without the pane.
    /// </summary>
    public OverlayMark CurrentOverlay
    {
        get
        {
            if (!Enabled)
            {
                return default;
            }

            lock (_gate)
            {
                return _overlay is { } shown ? new OverlayMark(shown, _paneFloor, _floorMenuHeight, Height) : default;
            }
        }
    }

    /// <summary>
    /// The pane as <paramref name="mark"/> found it (2026-10-14, the user's ask: a pane opened over a menu — the approval's
    /// details, <c>/settings</c>' folder browse — collapsed it to the input line for a frame as it closed, and the menu then
    /// grew back): with none, <see cref="CloseOverlay"/>; otherwise the overlay it took, drawn again at the height the
    /// mark held (the user's pick: a taller details pane gives the height back, the opener's title on its own row again),
    /// unless the window's height changed meanwhile, when the page sets it afresh. A dismissing double-click is cleared as
    /// <see cref="CloseOverlay"/> clears it, so it closes the nested pane alone — never answering the pane under it (the
    /// approval's ESC is Deny). The Hints bar's row and the dismissing click's place are left as they are. Disabled: nothing.
    /// </summary>
    public void RestoreOverlay(OverlayMark mark)
    {
        if (!mark.IsSome)
        {
            CloseOverlay();
            return;
        }

        if (!Enabled)
        {
            return;
        }

        lock (_gate)
        {
            Dismissed = false;
            _overlay = (Overlay)mark.Shown!;
            bool same = mark.Height == Height;
            _paneFloor = same ? mark.Floor : 0;
            _floorMenuHeight = same ? mark.MenuHeight : null;
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
            RefreshHintsLocked();   // the keys' lead and an armed prompt are the rest's (2026-10-14): the held row goes blank
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
            // A turn's end is a natural redraw (2026-10-14): a Hints bar row held blank through it goes, unless the keys come back.
            _hintsCollapseDue |= _busyLabel is null;
            RedrawHint();
            RefreshGhost();
            RefreshHintsLocked();
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

        bool emoji = _emojiShown;
        if (GhostApplies() == _shownGhostCells > 0 && EmojiButtonApplies(Width) == emoji)
        {
            return;
        }

        var shown = LayoutInput(Width, Height - BarRows - _stripRows - _scrollRows);
        if (shown.Rows.Count == _inputRows && _emojiShown == emoji)
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
    public static string PlaceholderRow(string placeholder, int width) => PlaceholderRow(placeholder, width, emojiButton: false);

    /// <summary><see cref="PlaceholderRow(string, int)"/>, kept clear of the emoji button while <paramref name="emojiButton"/> (2026-10-11, <see cref="EmojiButton"/>).</summary>
    public static string PlaceholderRow(string placeholder, int width, bool emojiButton)
    {
        ArgumentNullException.ThrowIfNull(placeholder);
        return Fit(placeholder, InputLine.AvailableCells(width) - (emojiButton ? EmojiButtonCells : 0));
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

    /// <summary>
    /// What a sent line opens with in the transcript (2026-10-12, the setting <c>User avatar</c>, <see cref="InputLine.UserGlyph"/>):
    /// read at each <see cref="CommitInput"/> and each voiced or replayed line. <see cref="InputLine.PromptGlyph"/> by default.
    /// </summary>
    public Func<string> UserGlyph
    {
        get => _userGlyph;
        set => _userGlyph = value ?? throw new ArgumentNullException(nameof(value));
    }

    public Func<bool> StepClockShown
    {
        get => _stepClockShown;
        set => _stepClockShown = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// The busy row's text past the frame: <see cref="BusyText"/>, and behind <see cref="HintSeparator"/>
    /// the overlay's own hint when one is open under the spinner (<c>thinking 00:12 · ↑/↓ PgUp/PgDn = scroll · ←/→ = tabs · type = find · ESC = close</c>:
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
            RefreshHintsLocked();   // the kill switch's alert is the Hints bar's (2026-10-14), a flash the Status bar's
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
            // The Hints bar first (2026-10-14): an armed Ctrl+C shows at once, the bar's row coming with it if none stands.
            if (RefreshHintsLocked())
            {
                return;
            }

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

            if (HomeChanged())
            {
                RedrawHome();
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
            // The row was typed behind the prompt glyph; the sent line may open with the user avatar, a cell wider.
            string glyph = _userGlyph();
            _inner.Cursor.Move(CursorDirection.Left, TextCells.Width(InputLine.PromptGlyph) + _cursorCell);
            _inner.Write(new Markup(InputLine.SubmittedMarkup(submitted, _userLineStyle(), glyph)));
            int stale = _renderedCells - TextCells.Width(submitted) - (TextCells.Width(glyph) - TextCells.Width(InputLine.PromptGlyph));
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
            // A sent line is a natural redraw (2026-10-14): a Hints bar row held blank goes with the write that follows.
            _hintsCollapseDue = true;
            // A sent line is the bottom: the flow comes back under the window before the line joins it.
            if (_top >= 0)
            {
                _top = -1;
                Redraw();
            }
        }

        // The preview folds past its first lines once the next thing is said (2026-10-04, SentLineView).
        Write(new SentLineView(InputLine.SubmittedMarkup(submitted, _userLineStyle(), _userGlyph()), preview.Length == 0 ? "" : InputLine.PreviewMarkup(preview)));
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
        _hintsQuietSince = _time.GetUtcNow().UtcTicks;   // a key: the Hints bar's idle clock starts again (2026-10-14)
        _labels = labels ?? Array.Empty<(int, int)>();
        if (!_drawn || _batch > 0 || _modal > 0 || _overlay is { Input: false })
        {
            // Under an overlay without a slot, or lifted for a menu or a batch: remembered, drawn when the pane comes back.
            return;
        }

        int firstRow = _firstRow;
        bool emoji = _emojiShown;
        var shown = LayoutInput(Width, Height - BarRows - _stripRows - _scrollRows);
        if (_stripRows > 0 && (text.Length == 0) != _drawnStripHighlight)
        {
            // The draft emptied or filled under a highlighted strip: the highlight follows it, the whole pane again.
            Redraw();
        }
        else if (shown.Rows.Count == _inputRows && firstRow == _firstRow && _emojiShown == emoji)
        {
            RewriteInputRows(shown);
            // A hint that reads the draft (the splash hint, 2026-09-20) follows it on the same
            // key, not at the next tick; under an overlay's slot the standing hint is the overlay's.
            // The Hints bar's first (2026-10-14): a row that comes with the hint reshapes the pane.
            if (RefreshHintsLocked())
            {
                return;
            }

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
        // Kept to the wrap's cells, the pad never reaches the emoji button's: it stands through every rewrite.
        int available = InputCells(Width);
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
            string? ghost = i == 0 && row.Length == 0 && GhostApplies() ? PlaceholderRow(_placeholder, Width, _emojiShown) : null;
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
        // The emoji button's room is decided here, so the draw, the rewrites and the hits all wrap as this layout did.
        _emojiShown = EmojiButtonApplies(width);
        var layout = InputLayout.Wrap(_text, _cursor, InputCells(width));
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
            var layout = InputLayout.Wrap(_text, _cursor, InputCells(Width));
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

            if (_hintsRows > 0 && y == top + HintsRowBelowCursor)
            {
                // The Hints bar (2026-10-14): plain text from edge to edge, as the Status bar's own text — no glyph, tally or timers.
                hit = new HintHit(HintZone.Row, "", -1);
                return true;
            }

            if (y != top + HintRowBelowCursor)
            {
                return false;
            }

            hit = HintHitAt(_hintStrip, _hintStripColumn, _trailerColumn, _trailerEnd, _markColumn, _markEnd, _hintPathColumn, _hintPathCells, _busyLabel is null ? _queuedColumn : -1, _queuedCells, _usageColumn, _usageCells, _labelColumn, _labelCells, x);
            if (hit.Zone == HintZone.Row && _tallyColumn >= 0 && x >= _tallyColumn && x < _tallyColumn + _tallyCells)
            {
                hit = new HintHit(HintZone.Usage, "", _tallyColumn);   // the busy row's tally apart from the spinner (2026-10-13)
            }

            if (hit.Zone == HintZone.Row && _busyLabel is null && _hintTimersColumn >= 0 && x >= _hintTimersColumn && x < _hintTimersColumn + _hintTimersCells)
            {
                hit = new HintHit(HintZone.Timers, "", _hintTimersColumn);   // the standing row's timers (later still on 2026-10-13)
            }

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
    public static HintHit HintHitAt(string strip, int trailerColumn, int markColumn, int queuedColumn, int queuedCells, int usageColumn, int usageCells, int labelColumn, int labelCells, int x) =>
        HintHitAt(strip, 0, trailerColumn, int.MaxValue, markColumn, int.MaxValue, queuedColumn, queuedCells, usageColumn, usageCells, labelColumn, labelCells, x);

    /// <summary>
    /// <see cref="HintHitAt(string, int, int, int, int, int, int, int, int, int)"/> with the strip at <paramref name="stripColumn"/> and
    /// the model's name and its mark ending before <paramref name="trailerEnd"/> and <paramref name="markEnd"/> (2026-10-12, Hint bar's
    /// order: the strip and the model anywhere on the row; 0 and open ends are the row as it always was). Pinned.
    /// </summary>
    public static HintHit HintHitAt(string strip, int stripColumn, int trailerColumn, int trailerEnd, int markColumn, int markEnd, int queuedColumn, int queuedCells, int usageColumn, int usageCells, int labelColumn, int labelCells, int x) =>
        HintHitAt(strip, stripColumn, trailerColumn, trailerEnd, markColumn, markEnd, -1, 0, queuedColumn, queuedCells, usageColumn, usageCells, labelColumn, labelCells, x);

    /// <summary>
    /// <see cref="HintHitAt(string, int, int, int, int, int, int, int, int, int, int, int, int)"/> with the working directory's
    /// <paramref name="pathCells"/> from <paramref name="pathColumn"/> (−1 for none; later on 2026-10-12): <see cref="HintZone.Path"/>. Pinned.
    /// </summary>
    public static HintHit HintHitAt(string strip, int stripColumn, int trailerColumn, int trailerEnd, int markColumn, int markEnd, int pathColumn, int pathCells, int queuedColumn, int queuedCells, int usageColumn, int usageCells, int labelColumn, int labelCells, int x)
    {
        ArgumentNullException.ThrowIfNull(strip);
        if (markColumn >= 0 && x >= markColumn && x < markEnd)
        {
            return new HintHit(HintZone.Mark, "", markColumn);
        }

        if (trailerColumn >= 0 && x >= trailerColumn && x < trailerEnd)
        {
            return new HintHit(HintZone.Trailer, "", trailerColumn);
        }

        if (pathColumn >= 0 && x >= pathColumn && x < pathColumn + pathCells)
        {
            return new HintHit(HintZone.Path, "", pathColumn);
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

        if (x >= stripColumn && TryStripGlyphAt(strip, x - stripColumn, out string glyph, out int column))
        {
            return new HintHit(HintZone.Strip, glyph, column + stripColumn);
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
    private static bool TryStripGlyphAt(string strip, int x, out string glyph, out int column) =>
        TryStripGlyphAt(strip, x, TextCells.NarrowSelectorSequences, out glyph, out column);

    /// <summary>
    /// <see cref="TryStripGlyphAt(string, int, out string, out int)"/> for a terminal that draws a selector sequence one cell
    /// wide (<paramref name="narrow"/>, <see cref="TextCells.NarrowSelectorSequences"/>): Terminal.app moves one cell for
    /// <c>⚙️</c> but paints the picture two wide, over the blank after it (2026-10-06, the user's report: the right half of the
    /// gear and the tools opened /settings as a blank does), so such a glyph also takes a click on the blank cell it covers.
    /// </summary>
    private static bool TryStripGlyphAt(string strip, int x, bool narrow, out string glyph, out int column)
    {
        column = 0;
        int i = 0;
        while (i < strip.Length && column <= x)
        {
            int width = TextCells.ElementWidth(strip, i, out int length, narrow);
            length = Math.Max(1, length);
            bool selected = false;
            if (i + length < strip.Length && strip[i + length] is '\uFE0F' or '\uFE0E')
            {
                width += TextCells.ElementWidth(strip, i + length, out int selector, narrow);
                length += selector;
                selected = true;
            }

            int reach = narrow && selected && width == 1 && i + length < strip.Length && strip[i + length] == ' ' ? 2 : width;
            if (x < column + reach && !string.IsNullOrWhiteSpace(strip.AsSpan(i, length).ToString()))
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
    /// drawn toolbar (2026-09-21) — wherever <see cref="BarOrder"/> put it — naming the part under
    /// it (<see cref="ToolbarHitAt"/>). It answers under the busy row (the pane glyphs work under
    /// a reply, as the tally does) and while scrolled; false under an overlay (the row is off the
    /// pane there: a double-click is the overlay's dismiss, <see cref="TryHitOutside"/>, and
    /// <see cref="OffPaneHitAt"/> names the part for the screen's switch), when no bar is drawn,
    /// when the pane is lifted, or when the console cannot say where the cursor is.
    /// The performance bar is the toolbar's blanks anywhere on it (<see cref="PerfBarHit"/>; 2026-09-29, the user's ask),
    /// with the toolbar or without it, and so are the home bar's blanks (2026-10-12, the user's ask: a double-click on any free
    /// space of a bar opens the settings); its chips, page arrows and status glyph are its own (<see cref="TryHitHomeBar"/>).
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
            if (!_drawn || _drawnOverlay || _toolbarRows + _perfRows + _homeRows == 0 || _batch > 0 || _modal > 0 || _geometry?.CursorTop() is not int top)
            {
                return false;
            }

            if ((_perfRows > 0 && y == top + PerfRowBelowCursor) || HomeBlankAt(x, y, top))
            {
                hit = PerfBarHit;
                return true;
            }

            if (_toolbarRows == 0 || y != top + ToolbarRowBelowCursor)
            {
                return false;
            }

            hit = ToolbarHitNow(x);
            return true;
        }
    }

    /// <summary>
    /// The drawn toolbar's zone at column <paramref name="x"/> (2026-10-12): the model's name where it stands, a glyph after it among
    /// the strip's (its column the row's), else <see cref="ToolbarHitAt(string, int, int, int)"/> over the glyphs ahead of it and the path. Under the gate.
    /// </summary>
    private ToolbarHit ToolbarHitNow(int x)
    {
        // Each run where it was drawn (2026-10-13: on either side): the name's mark, the name, the path, a glyph.
        foreach (var run in _toolbarRuns)
        {
            if (x < run.Column || x >= run.Column + run.Cells)
            {
                continue;
            }

            switch (run.Kind)
            {
                case ToolbarItemKind.Model:
                    int mark = run.Mark.Length > 0 && run.Text.EndsWith(run.Mark, StringComparison.Ordinal) ? run.Column + run.Cells - TextCells.Width(run.Mark) : -1;
                    return mark >= 0 && x >= mark ? new ToolbarHit(ToolbarZone.Mark, "", mark) : new ToolbarHit(ToolbarZone.Model, "", run.Column);
                case ToolbarItemKind.Path:
                    return new ToolbarHit(ToolbarZone.Path, "", run.Column);
                case ToolbarItemKind.Divider:
                    return new ToolbarHit(ToolbarZone.Row, "", -1);   // a divider is a blank's (later on 2026-10-13)
                default:
                    if (TryStripGlyphAt(run.Text, x - run.Column, TextCells.NarrowSelectorSequences, out string glyph, out int column))
                    {
                        return new ToolbarHit(ToolbarZone.Glyph, glyph, column + run.Column);
                    }

                    break;
            }
        }

        return new ToolbarHit(ToolbarZone.Row, "", -1);
    }

    /// <summary>
    /// The home bar's span under buffer cell (<paramref name="x"/>, <paramref name="y"/>) (2026-10-12): a chip, a page arrow or
    /// the status glyph. It answers under an overlay too (the bar stays drawn under every pane, and a chip acts without
    /// closing it), under the busy row and while scrolled; false on a blank of the bar, off it, when it is not drawn, when the
    /// pane is lifted, batched or modal, or when the console cannot say where the cursor is.
    /// </summary>
    public bool TryHitHomeBar(int x, int y, out UI.HomeSpan hit)
    {
        hit = default;
        if (!Enabled)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_drawn || _homeRows == 0 || _batch > 0 || _modal > 0 || _geometry?.CursorTop() is not int top || y != top + HomeRowBelowCursor)
            {
                return false;
            }

            if (UI.HomeBar.HitAt(_homeSpans, x) is not { } span)
            {
                return false;
            }

            hit = span;
            return true;
        }
    }

    /// <summary>
    /// Whether buffer cell (<paramref name="x"/>, <paramref name="y"/>) is a blank of the home bar as drawn, under a cursor at
    /// buffer row <paramref name="top"/> (2026-10-12, the user's ask: a double-click on any free space of a bar opens the settings,
    /// as the hint row's, the toolbar's and the performance bar's do): no chip, page arrow or status glyph there. Under the gate.
    /// </summary>
    private bool HomeBlankAt(int x, int y, int top) =>
        _homeRows > 0 && y == top + HomeRowBelowCursor && UI.HomeBar.HitAt(_homeSpans, x) is null;

    /// <summary>
    /// The zone of column <paramref name="x"/> on a toolbar whose strip is <paramref name="strip"/>
    /// (from column 0) and whose path takes <paramref name="pathCells"/> from
    /// <paramref name="pathColumn"/> (−1 for none): a strip glyph with its first column, else the
    /// path with its first, else the row. Pinned.
    /// </summary>
    public static ToolbarHit ToolbarHitAt(string strip, int pathColumn, int pathCells, int x) =>
        ToolbarHitAt(strip, pathColumn, pathCells, x, TextCells.NarrowSelectorSequences);

    /// <summary><see cref="ToolbarHitAt(string, int, int, int)"/> with the terminal's selector widths given (<paramref name="narrow"/>: Terminal.app). Pinned.</summary>
    public static ToolbarHit ToolbarHitAt(string strip, int pathColumn, int pathCells, int x, bool narrow)
    {
        ArgumentNullException.ThrowIfNull(strip);
        if (TryStripGlyphAt(strip, x, narrow, out string glyph, out int column))
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
                if (!RefreshHintsLocked())   // the Hints bar too (2026-10-14)
                {
                    RedrawHint();
                }
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

        if (_row > Height - 1)
        {
            // Written past the last row: the terminal scrolled the flow's top rows away.
            _flowCut = true;
            _row = Height - 1;
        }
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
        // The Hints bar (2026-10-14) first of the bars in a short window's chain: its row while it has a hint, is always, or holds
        // the row it was drawn with and no collapse is due — a due one is spent by this draw.
        int hintsRows = HintsRowsFor(h);
        _hintsCollapseDue = false;
        var toolbar = _toolbar();
        int toolbarRows = toolbar is not null && h >= PaneRows + 2 + hintsRows ? 1 : 0;

        // The performance bar (2026-09-29) under the toolbar, the last row, when the window keeps a transcript row over
        // the smallest pane and the toolbar; from here on the bars count as one (barRows) wherever the toolbar did alone.
        var perf = _perf(Math.Max(1, w - 1));
        int perfRows = perf is not null && h >= PaneRows + 2 + hintsRows + toolbarRows ? 1 : 0;

        // The home bar (2026-10-12) under the performance bar, the last row, when the window keeps a transcript row over the
        // smallest pane and the other bars: in a short window it goes first.
        var home = _home(Math.Max(1, w - 1));
        int homeRows = home is not null && h >= PaneRows + 2 + hintsRows + toolbarRows + perfRows ? 1 : 0;
        int barRows = hintsRows + toolbarRows + perfRows + homeRows;

        // The picture strip (later still on 2026-09-24) over the upper rule, under a rule of its own (2026-09-25): its rows,
        // the rule's with them, count into the pane's, so the
        // padding, the lift and the region leave room for it; the input rows are capped over what it leaves.
        int stripRows = StripRowsFor(w, h, ReservedBarRowsFor(h));   // the Hints bar counted while it may come (2026-10-14): the strip's own arrows bring it
        var strip = stripRows > 0 ? _pictureStrip() : null;
        List<SegmentLine>? stripLines = null;
        bool stripHighlight = _text.Length == 0;
        if (strip is not null)
        {
            var (lines, spans) = strip.Render(RenderOptions.Create(_inner, _inner.Profile.Capabilities), w - 1, stripHighlight, out var arrows);
            stripLines = lines;
            _stripSpans = spans;
            _stripArrows = arrows;
            _drawnStripVersion = strip.Version;
        }
        else
        {
            stripRows = 0;
            _stripSpans = [];
            _stripArrows = (-1, -1);
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
        _hintsRows = hintsRows;
        _toolbarRows = toolbarRows;
        _perfRows = perfRows;
        _homeRows = homeRows;
        _stripRows = stripRows;

        // The held height (2026-10-14, the user's ask): a titled overlay shorter than the tallest pane drawn since it opened gets
        // blank rows under its content (over the input slot of a typed edit) up to it, within the overlay's cap, so the top rule
        // stays where it was. The whole pane is counted, not the overlay alone: a bar switched off meanwhile, or the scroll's row
        // going below, is taken up by one more blank row. A new Menus max height starts the hold afresh.
        int heldRows = 0;
        bool held = _overlay is { Close: true } && overlayLines is not null;
        if (held)
        {
            string word = _menuHeight();
            if (_floorMenuHeight is not null && !string.Equals(word, _floorMenuHeight, StringComparison.Ordinal))
            {
                _paneFloor = 0;
            }

            _floorMenuHeight = word;
        }

        HoldHeight();

        // Scrolled: the anchor against the region this pane leaves; at or past the last window it
        // is the bottom after all (a taller pane, a store that shrank) — and the scroll's row goes.
        IReadOnlyList<SegmentLine>? window = null;
        int region = RegionRows(_paneRows);
        if (_top >= 0)
        {
            window = _store.Rows(w);
            int max = Math.Max(0, window.Count - region);
            if (_find is not null)
            {
                // A find (2026-10-07) paints the store at its last window too, so its marks show there; a short store from its top.
                _top = Math.Min(_top, max);
            }
            else if (_top >= max)
            {
                _top = -1;
                window = null;
                _paneRows -= scrollRows;
                scrollRows = 0;
                HoldHeight();
                region = RegionRows(_paneRows);
            }
        }

        _scrollRows = scrollRows;
        if (held)
        {
            for (int i = 0; i < heldRows; i++)
            {
                overlayLines!.Insert(overlayRows - heldRows + i, new SegmentLine());
            }

            _paneFloor = Math.Max(_paneFloor, _paneRows);
        }

        // The blank rows the hold adds: as many as reach the floor, within what the overlay may take over the input rows.
        void HoldHeight()
        {
            if (!held)
            {
                return;
            }

            int room = MaxOverlayRows(h - barRows - scrollRows, shown?.Rows.Count ?? 0) - overlayRows;
            int more = Math.Min(_paneFloor - _paneRows, room);
            if (more > 0)
            {
                overlayRows += more;
                heldRows += more;
                _paneRows += more;
            }
        }

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
                if (_top + i < window.Count)
                {
                    _inner.Write(new SegmentList(FindMarked(window, _top + i)));
                }

                _inner.WriteLine();
            }

            _liveCount = _live is null ? 0 : Math.Max(0, LiveLayout(w, region).Count - _liveCommitted);
            _pad = 0;
        }
        else
        {
            // A pane that shrank after flow rows went off the top (2026-10-09, FlowCutWithRoom) writes them back the same way.
            if ((reshaped || FlowCutWithRoom()) && !_blank)
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
                _flowCut = true;
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
                int rowCells;
                if (i == 0 && shown.Rows[i].Length == 0 && GhostApplies())
                {
                    // The empty idle row: the placeholder after the glyph, in place of the draft.
                    string ghost = PlaceholderRow(_placeholder, w, _emojiShown);
                    _inner.Write(new RawText(ghost, Theme.Placeholder));
                    ghostCells = TextCells.Width(ghost);
                    rowCells = ghostCells;
                }
                else
                {
                    WriteRowText(shown.Rows[i], shown.Starts[i], 0);
                    rowCells = TextCells.Width(shown.Rows[i]);
                }

                if (i == 0 && _emojiShown)
                {
                    // The emoji button (2026-10-11) past the wrap's cells and one blank, dim, the last column left empty.
                    _inner.Write(new RawText(new string(' ', Math.Max(0, InputCells(w) - rowCells) + 1)));
                    _inner.Write(new RawText(EmojiGlyph, Theme.Hint));
                }

                EndRow();
            }
        }

        WriteLowerRule(w);

        // The rows under the lower rule in the user's order (2026-10-12, Toolbar order): the hint row always, each bar while it
        // has its row; a row break before every one but the first. A bar with no row is forgotten wherever it would sit.
        var bars = Bars(_barOrder());
        _drawnBars = bars;
        bool first = true;
        foreach (var bar in bars)
        {
            int rows = bar switch
            {
                PaneBar.Hints => hintsRows,
                PaneBar.Toolbar => toolbarRows,
                PaneBar.Perf => perfRows,
                PaneBar.Home => homeRows,
                _ => 1,
            };
            if (rows == 0)
            {
                continue;
            }

            if (!first)
            {
                EndRow();
            }

            first = false;
            switch (bar)
            {
                case PaneBar.Hints:
                    WriteHintsRow();
                    break;
                case PaneBar.Toolbar:
                    WriteToolbarRow(toolbar!.Value, w);
                    break;
                case PaneBar.Perf:
                    WritePerfRow(perf!);
                    break;
                case PaneBar.Home:
                    WriteHomeRow(home!);
                    break;
                default:
                    WriteHintRow();
                    break;
            }
        }

        if (hintsRows == 0)
        {
            _shownHints = null;
        }

        if (toolbarRows == 0)
        {
            ForgetToolbar();
        }

        if (perfRows == 0)
        {
            _shownPerf = null;
        }

        if (homeRows == 0)
        {
            _shownHome = null;
            _homeSpans = [];
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
        // A Hints bar row coming or going under a reply (2026-10-14: the kill switch's alert, a held row's collapse) is a shape
        // change: lifted and drawn, never painted over.
        if (_drawnScrolled || _top >= 0 || _blank || _store.Reshaped || HintsRowsFor(Height) != _hintsRows)
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
            [new Segment(ThinkingFoldText.Summary(elapsed, expanded: false, thinking.Text), Theme.DimText)],
            [new Segment(ThinkingFoldText.Summary(elapsed, expanded: true, thinking.Text), Theme.DimText)]);
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

    /// <summary>How many rows under the terminal's cursor the first row under the lower rule sits, as last drawn: over the rows under the cursor and the rule; the hint row there by default.</summary>
    private int FirstBarRowBelowCursor => _paneRows - 2 - BarRows - _stripRows - _scrollRows - CursorDepth;

    /// <summary>How many rows under the terminal's cursor the Status bar (the hint row until 2026-10-14) sits, as last drawn: under the Hints bar's row, or wherever <see cref="BarOrder"/> put it (2026-10-12).</summary>
    private int HintRowBelowCursor => RowBelowCursor(PaneBar.Status);

    /// <summary>How many rows under the terminal's cursor the Hints bar sits, as last drawn (2026-10-14): the first under the lower rule by default.</summary>
    private int HintsRowBelowCursor => RowBelowCursor(PaneBar.Hints);

    /// <summary>How many rows under the terminal's cursor the pane's last row sits, as last drawn: whichever row the order put last (the home bar by default, when drawn).</summary>
    private int LastRowBelowCursor => _paneRows - 2 - _stripRows - _scrollRows - CursorDepth;

    /// <summary>How many rows over the terminal's cursor the scroll's row sits (2026-10-01), as last drawn: over the upper rule, and the picture strip's rows with their rule when drawn.</summary>
    private int ScrollRowAboveCursor => CursorDepth + 1 + _stripRows + 1;

    /// <summary>How many rows under the terminal's cursor the performance bar sits, as last drawn (2026-10-12).</summary>
    private int PerfRowBelowCursor => RowBelowCursor(PaneBar.Perf);

    /// <summary>How many rows under the terminal's cursor the toolbar sits, as last drawn.</summary>
    private int ToolbarRowBelowCursor => RowBelowCursor(PaneBar.Toolbar);

    /// <summary>How many rows under the terminal's cursor the home bar sits, as last drawn (2026-10-12).</summary>
    private int HomeRowBelowCursor => RowBelowCursor(PaneBar.Home);

    /// <summary>
    /// How many rows under the terminal's cursor <paramref name="bar"/> sits, as last drawn (2026-10-12, Toolbar order): the first
    /// row under the lower rule and one more for each row drawn over it in <see cref="_drawnBars"/>.
    /// </summary>
    private int RowBelowCursor(PaneBar bar)
    {
        int down = FirstBarRowBelowCursor;
        foreach (var drawn in _drawnBars)
        {
            if (drawn == bar)
            {
                break;
            }

            down += drawn switch
            {
                PaneBar.Hints => _hintsRows,
                PaneBar.Toolbar => _toolbarRows,
                PaneBar.Perf => _perfRows,
                PaneBar.Home => _homeRows,
                _ => 1,
            };
        }

        return down;
    }

    /// <summary>
    /// The lower rule with <see cref="LowerRuleTitle"/> at its right edge (2026-10-12; the bare rule until then, and while none is
    /// answered): the title remembered for the tick and where it landed for <see cref="TryHitLowerRuleTitle"/>. The title is in
    /// the upper rule's ⤡ style, <see cref="Theme.AccentSecondary"/> (later on 2026-10-12, the user's ask), the rule around it
    /// in <see cref="Theme.PaneRule"/>.
    /// </summary>
    private void WriteLowerRule(int width)
    {
        _drawnLowerRuleTitle = _lowerRuleTitle();
        (_lowerTitleColumn, _lowerTitleCells) = TitleSpan(_drawnLowerRuleTitle, width, 0);
        if (FitRuleTitle(_drawnLowerRuleTitle, width) is var (fitted, left))
        {
            _inner.Write(new RawText(new string(RuleGlyph, left) + " ", Theme.PaneRule));
            _inner.Write(new RawText(fitted, Theme.AccentSecondary));
            _inner.Write(new RawText(" " + RuleGlyph, Theme.PaneRule));
        }
        else
        {
            _inner.Write(new RawText(new string(RuleGlyph, Math.Max(0, width)), Theme.PaneRule));
        }

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
        _overlay is null && _stripReopen() is { Length: > 0 } label && StripFits(Width, Height, ReservedBarRowsFor(Height)) ? label : null;

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

    /// <summary>The title <see cref="RuleTitle"/> answers now is not the one on the drawn upper rule, or <see cref="LowerRuleTitle"/>'s not the lower one's (2026-10-12).</summary>
    private bool RuleTitleChanged() =>
        !string.Equals(_ruleTitle(), _drawnRuleTitle, StringComparison.Ordinal) || !string.Equals(_lowerRuleTitle(), _drawnLowerRuleTitle, StringComparison.Ordinal);

    private void WriteHintRow()
    {
        int max = Math.Max(1, Width - 1);
        if (StatusWhole() is { } whole)
        {
            // A picture on its way to the line (2026-09-28): the row is the drag's alone, in the strip button's style, no zones.
            // An alert (2026-10-01, the kill switch's first press) the same, in the warning's style.
            string shown = Fit(Spaced(whole), max);
            _inner.Write(new RawText(shown, _dragHint is not null ? Theme.AccentSecondary : _alertFlash ? Theme.Hint : Theme.WarnText));
            _inner.Write(EraseLineEnd);
            _shownHint = shown;
            _hintStrip = "";
            _hintStripColumn = 0;
            _trailerColumn = -1;
            _markColumn = -1;
            _trailerEnd = int.MaxValue;
            _markEnd = int.MaxValue;
            _hintPathColumn = -1;
            _hintPathCells = 0;
            _hintTimersColumn = -1;
            _hintTimersCells = 0;
            _queuedColumn = -1;
            _queuedCells = 0;
            _usageColumn = -1;
            _usageCells = 0;
            _labelColumn = -1;
            _labelCells = 0;
            _tallyColumn = -1;
            _tallyCells = 0;
            return;
        }

        string mark = _trailerMark();
        _tallyColumn = -1;
        _tallyCells = 0;
        if (_busyLabel is { } sided && _hintLayout is { } busyLayout)
        {
            WriteBusySided(sided, busyLayout, max, mark);
        }
        else if (_busyLabel is { } label)
        {
            // The strip, then the frame in its own style, then the label and its count in what is
            // left ahead of the trailer — the same cut PinRight makes, in four styles; the trailer
            // gives way to the uncut left as PinRight's does (2026-09-28).
            string frame = Theme.SpinnerFrames[_frame % Theme.SpinnerFrames.Length];
            string queued = StandingQueued();
            // The tally beside the label (2026-09-25), where the standing row would have it: not under an overlay's hint (nor
            // under the scroll's until 2026-10-01, when it went to a row of its own).
            string usage = !OverlayOnStatus ? _busyUsage() : "";
            var elapsed = _time.GetElapsedTime(_busySince);
            TimeSpan? step = _stageSince != _busySince && _stepClockShown() ? _time.GetElapsedTime(_stageSince) : null;
            // A label that trails the tally (LabelAfterUsage, the ComfyUI generation's) swaps the two; nothing else moves.
            bool after = usage.Length > 0 && _labelAfterUsage(label);
            // The way out named at the row's end (2026-10-04) while the screen's turn runs and no pane's keys stand there instead
            // (a pane's keys are the Hints bar's while it has a row, 2026-10-14).
            string hint = Spaced((OverlayOnStatus ? _overlay!.Hint : null) ?? (_stopHintShown() ? StopHint : ""));
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
            _hintStripColumn = 0;
            _hintPathColumn = -1;   // the working directory is the standing row's alone
            _hintPathCells = 0;
            _hintTimersColumn = -1;
            _hintTimersCells = 0;
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
            var laid = PlaceStanding(max);
            WriteHintText(laid, "");
            _shownHint = laid.Text;
            // Each part's zone where it was drawn whole, on either side (2026-10-13, the sides).
            ApplyHintZones(laid, max);
            // The usage zone: the tally where the screen's hint put it — nowhere under an overlay's hint, or when the exit hint
            // stands in its place (the timers too until 2026-10-13); a layout of text slots (the tests') has it found by its text.
            string usage = !OverlayOnStatus ? _usage() : "";
            if (laid.Find(HintPartKind.Usage) is null && usage.Length > 0 && laid.Parts.FirstOrDefault(part => part.Kind == HintPartKind.Text && part.Text.Contains(Spaced(usage), StringComparison.Ordinal)) is { Text.Length: > 0 } holder)
            {
                int at = holder.Text.IndexOf(Spaced(usage), StringComparison.Ordinal);
                bool drawn = holder.Whole;
                _usageColumn = drawn ? holder.Column + TextCells.Width(holder.Text[..at]) : -1;
                _usageCells = drawn ? TextCells.Width(Spaced(usage)) : 0;
            }

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
        // Terminal.app paints ⚙️ and 🛠️ over the cell after them: a space of their own keeps the strip's gaps (2026-10-06) — LayToolbar spaces it.
        var laid = LayToolbar(toolbar, cells);
        var placed = new List<ToolbarPlaced>();
        foreach (var (run, column, before) in laid.Placed(cells))
        {
            // The separators and the blanks between the groups dim, as the path's were (2026-10-13: either group may hold anything).
            _inner.Write(new RawText(before, Theme.DimText));
            int textColumn = column;
            switch (run.Kind)
            {
                case ToolbarItemKind.Glyph:
                    foreach (var (text, off) in ToolbarStripRuns(run.Text, run.Off))
                    {
                        _inner.Write(new RawText(text, off ? Theme.ToolbarOff : Theme.Hint));
                    }

                    break;
                case ToolbarItemKind.Path:
                    _inner.Write(new RawText(run.Text, Theme.DimText));
                    break;
                case ToolbarItemKind.Divider:
                    _inner.Write(new RawText(run.Text, Theme.TabSeparator));   // the tab strips' │ (later on 2026-10-13)
                    break;
                default:
                    WriteNamed(run.Text, run.Mark);
                    break;
            }

            placed.Add(new ToolbarPlaced(run.Kind, run.Text, textColumn, TextCells.Width(run.Text), run.Mark));
        }

        _inner.Write(EraseLineEnd);
        _shownToolbar = laid.Row(cells);
        _shownToolbarOff = toolbar.OffKey;
        _toolbarRuns = placed;
    }

    /// <summary>The model's name on the toolbar in the hint's style, its reasoning <paramref name="mark"/> at its end in the mark's own (later on 2026-10-12).</summary>
    private void WriteNamed(string named, string mark)
    {
        if (mark.Length == 0 || !named.EndsWith(mark, StringComparison.Ordinal))
        {
            _inner.Write(new RawText(named, Theme.Hint));
            return;
        }

        _inner.Write(new RawText(named[..^mark.Length], Theme.Hint));
        _inner.Write(new RawText(mark, Theme.TrailerMark));
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
                if (off.Contains(glyph) && element != "…" && !(element.Length == 1 && char.IsAscii(element[0])))   // nor the +N (2026-10-04)
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

    /// <summary>No toolbar drawn: nothing for the tick to compare, no zones for the hit test.</summary>
    private void ForgetToolbar()
    {
        _shownToolbar = null;
        _shownToolbarOff = "";
        _toolbarRuns = [];
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
    private string StandingHint() => Spaced(OverlayOnStatus ? _overlay!.Hint : _hint());

    /// <summary>
    /// A hint's text with a space after each emoji the terminal paints over the cell after it (<see cref="TextCells.SpaceSelectorSequences"/>;
    /// Terminal.app and iTerm2, 2026-10-06, the user's report: <c>⚙️Settings · double-click to open</c> ran together), measured
    /// after, so the zones read the row as drawn. Unchanged elsewhere.
    /// </summary>
    private static string Spaced(string text) => TextCells.SpaceSelectorSequences(text, TextCells.NarrowSelectorSequences);

    /// <summary>
    /// A flow write's segments with <see cref="Spaced"/> text (later on 2026-10-06, the user's report: the transcript's <c>🛠️</c>
    /// ran into the tool's name in Terminal.app and iTerm2), before the store keeps them or the screen draws them, so a page
    /// back and a redraw show the same; control codes and pictures' segments pass as they are. The list itself where nothing is narrow.
    /// </summary>
    private static List<Segment> SpacedSegments(IEnumerable<Segment> segments)
    {
        var list = segments.ToList();
        if (!TextCells.NarrowSelectorSequences)
        {
            return list;
        }

        for (int i = 0; i < list.Count; i++)
        {
            var segment = list[i];
            if (!segment.IsControlCode && !segment.IsLineBreak && segment.Text.Contains('\uFE0F', StringComparison.Ordinal))
            {
                list[i] = new Segment(Spaced(segment.Text), segment.Style);
            }
        }

        return list;
    }

    /// <summary>The queued part while the row is the screen's own — nothing under an overlay's hint (2026-09-18; under the scroll's until 2026-10-01).</summary>
    private string StandingQueued() => !OverlayOnStatus ? _queued() : "";

    /// <summary>The scroll's row (2026-10-01) on the cursor's row: <see cref="ScrolledRow"/> in the hint's style, the rest of the row erased; the text remembered for the tick.</summary>
    private void WriteScrollRow(int width)
    {
        string row = ScrollRowText(width - 1);
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
    private bool ScrollRowChanged() => _scrollRows > 0 && !string.Equals(ScrollRowText(Width - 1), _shownScroll, StringComparison.Ordinal);

    /// <summary>
    /// The scroll's row: <see cref="ScrolledRow"/>, or under a find with nothing below (2026-10-07: a find holds the store's last
    /// window, where the row read "0 rows below") <see cref="FindAtTheEnd"/>, centered the same way.
    /// </summary>
    private string ScrollRowText(int cells)
    {
        int below = RowsBelowLocked();
        if (_find is null || below > 0)
        {
            return ScrolledRow(below, cells);
        }

        string end = Fit(FindAtTheEnd, Math.Max(1, cells));
        return new string(' ', (Math.Max(1, cells) - TextCells.Width(end)) / 2) + end;
    }

    /// <summary>The scroll's row under a find at the transcript's last window (2026-10-07). Pinned.</summary>
    public const string FindAtTheEnd = "the end of the transcript";

    /// <summary>What a part of the laid hint row is (2026-10-13): a slot's kind, or the busy row's spinner with its label.</summary>
    private enum HintPartKind
    {
        Strip,
        Queued,
        Usage,
        Model,
        Path,
        Text,
        Spinner,
        Timers,   // 2026-10-13: laid out as text, its place kept for the double-click's zone
    }

    /// <summary>A part of the hint row before it is placed (2026-10-13): its text — the model's name, its <paramref name="Mark"/> apart until it is fitted.</summary>
    private readonly record struct HintPart(HintPartKind Kind, string Text, string Mark = "");

    /// <summary>A part where it was placed (2026-10-13): its first column and cells, its char index in the row's text, whether it was drawn whole, whether it ends the row.</summary>
    private readonly record struct HintPlacedPart(HintPartKind Kind, string Text, int Column, int Cells, int Index, bool Whole, bool Last, string Mark);

    /// <summary>The hint row laid out (2026-10-13): its text and its parts where they stand.</summary>
    private readonly record struct HintRowLaid(string Text, IReadOnlyList<HintPlacedPart> Parts)
    {
        /// <summary>The first part of <paramref name="kind"/>, or null.</summary>
        public HintPlacedPart? Find(HintPartKind kind)
        {
            foreach (var part in Parts)
            {
                if (part.Kind == kind)
                {
                    return part;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// The standing row's parts in their two groups (2026-10-12, Hint bar's order; the sides since 2026-10-13, the user's ask): each slot of
    /// <see cref="HintLayout"/> in its order, on the side it is pinned to (<see cref="HintSlot.Right"/>); the model at the right edge,
    /// ahead of anything else there, while no slot names it. Under an overlay or with no layout the strip, the queued part and the hint,
    /// the model at the right, as the row always was.
    /// </summary>
    private (List<HintPart> Left, List<HintPart> Right) StandingParts()
    {
        string strip = _strip();
        var model = new HintPart(HintPartKind.Model, _trailer(), _trailerMark());
        if (OverlayOnStatus || _hintLayout is not { } layout)
        {
            return ([new(HintPartKind.Strip, strip), new(HintPartKind.Queued, StandingQueued()), new(HintPartKind.Text, StandingHint())], [model]);
        }

        var left = new List<HintPart>();
        var right = new List<HintPart>();
        bool modelSlot = false;
        foreach (var slot in layout())
        {
            if (slot.Kind == HintSlotKind.Spinner)
            {
                continue;   // the busy row's alone (later on 2026-10-13)
            }

            modelSlot |= slot.Kind == HintSlotKind.Model;
            var part = slot.Kind switch
            {
                HintSlotKind.Strip => new HintPart(HintPartKind.Strip, strip),
                HintSlotKind.Queued => new HintPart(HintPartKind.Queued, _queued()),
                HintSlotKind.Model => model,
                HintSlotKind.Path => new HintPart(HintPartKind.Path, slot.Text ?? ""),
                HintSlotKind.Usage => new HintPart(HintPartKind.Usage, Spaced(slot.Text ?? "")),
                HintSlotKind.Timers => new HintPart(HintPartKind.Timers, Spaced(slot.Text ?? "")),   // a text part with a zone of its own (2026-10-13)
                _ => new HintPart(HintPartKind.Text, Spaced(slot.Text ?? "")),
            };
            (slot.Right ? right : left).Add(part);
        }

        if (!modelSlot)
        {
            right.Insert(0, model);
        }

        return (left, right);
    }

    /// <summary>The standing row in <paramref name="max"/> cells (<see cref="LayParts"/> over <see cref="StandingParts"/>).</summary>
    private HintRowLaid PlaceStanding(int max)
    {
        var (left, right) = StandingParts();
        return LayParts(left, right, max);
    }

    /// <summary>
    /// The busy row's parts in their two groups (2026-10-13, the user's pick: the busy row follows the sides): each slot of
    /// <paramref name="layout"/> the busy row draws (the strip, the queued part, the tally, the model, the working directory) on its side
    /// in its order. The spinner's frame and its label — the tally with them, the label after it, while the label trails the tally
    /// (<see cref="LabelAfterUsage"/>) — stand where the <see cref="HintSlotKind.Spinner"/> slot does (later that day, the user's ask: the
    /// spinner a section of its own, movable), the way out (the overlay's keys or the stop hint) right after them (the user's pick); a
    /// layout without that slot has them lead the left, as before.
    /// </summary>
    private (List<HintPart> Left, List<HintPart> Right, string Unit, bool After, string Usage, string BusyLabel) BusyParts(string label, Func<IReadOnlyList<HintSlot>> layout, string frame)
    {
        string usage = !OverlayOnStatus ? _busyUsage() : "";
        var elapsed = _time.GetElapsedTime(_busySince);
        TimeSpan? step = _stageSince != _busySince && _stepClockShown() ? _time.GetElapsedTime(_stageSince) : null;
        bool after = usage.Length > 0 && _labelAfterUsage(label);
        string busyText = BusyText(label, elapsed, step);
        string unit = frame + " " + Labelled(busyText, after ? usage : "", after);
        var spinner = new HintPart(HintPartKind.Spinner, unit);
        // An open pane's keys while the Hints bar has no row; the stop hint otherwise (2026-10-14: the keys are the bar's).
        var hint = new HintPart(HintPartKind.Text, Spaced((OverlayOnStatus ? _overlay!.Hint : null) ?? (_stopHintShown() ? StopHint : "")));
        var left = new List<HintPart>();
        var right = new List<HintPart>();
        var model = new HintPart(HintPartKind.Model, _trailer(), _trailerMark());
        bool modelSlot = false;
        bool spinnerSlot = false;
        foreach (var slot in layout())
        {
            modelSlot |= slot.Kind == HintSlotKind.Model;
            if (slot.Kind == HintSlotKind.Spinner && !spinnerSlot)
            {
                spinnerSlot = true;
                (slot.Right ? right : left).AddRange([spinner, hint]);
                continue;
            }

            HintPart? part = slot.Kind switch
            {
                HintSlotKind.Strip => new HintPart(HintPartKind.Strip, _strip()),
                HintSlotKind.Queued => new HintPart(HintPartKind.Queued, StandingQueued()),
                HintSlotKind.Usage when !after => new HintPart(HintPartKind.Usage, usage),
                HintSlotKind.Model => model,
                HintSlotKind.Path => new HintPart(HintPartKind.Path, slot.Text ?? ""),
                _ => null,
            };
            if (part is { } shown)
            {
                (slot.Right ? right : left).Add(shown);
            }
        }

        if (!modelSlot)
        {
            right.Insert(0, model);
        }

        if (!spinnerSlot)
        {
            left.InsertRange(0, [spinner, hint]);
        }

        return (left, right, unit, after, usage, busyText);
    }

    /// <summary>
    /// The busy row with a layout (2026-10-13, the user's pick: it follows the sides): <see cref="BusyParts"/> laid as the standing row is,
    /// the frame in the spinner's style; the strip and the queued part answer where they were drawn whole, the spinner and its label are
    /// the usage zone (or the label's, <see cref="HintZone.Label"/>), the tally apart from them a usage zone of its own; the model and the
    /// working directory are no buttons under a reply.
    /// </summary>
    private void WriteBusySided(string label, Func<IReadOnlyList<HintSlot>> layout, int max, string mark)
    {
        string frame = Theme.SpinnerFrames[_frame % Theme.SpinnerFrames.Length];
        var (left, right, unit, after, usage, busyText) = BusyParts(label, layout, frame);
        var laid = LayParts(left, right, max);
        WriteHintText(laid, frame);
        _shownHint = laid.Text;
        ApplyHintZones(laid, max);
        _hintPathColumn = -1;   // no button under a reply, as the tool bar's path
        _hintPathCells = 0;
        _hintTimersColumn = -1;
        _hintTimersCells = 0;
        _usageColumn = -1;
        _usageCells = 0;
        _labelColumn = -1;
        _labelCells = 0;
        if (laid.Find(HintPartKind.Spinner) is { Whole: true } spinner)
        {
            _usageColumn = spinner.Column;
            _usageCells = spinner.Cells;
            bool trailing = _labelAfterUsage(label);
            bool leading = !trailing && _labelZone(label);
            if (trailing)
            {
                // The label after the tally and its separator, or right after the frame's blank with none.
                _labelColumn = spinner.Column + TextCells.Width(frame + " " + (after ? usage + HintSeparator : ""));
                _labelCells = TextCells.Width(busyText);
            }
            else if (leading)
            {
                _labelColumn = spinner.Column;
                _labelCells = spinner.Cells;
            }
        }

        if (laid.Find(HintPartKind.Usage) is { Whole: true } tally)
        {
            _tallyColumn = tally.Column;
            _tallyCells = tally.Cells;
        }
    }

    /// <summary>The strip, the queued part, the model and its mark and the working directory's zones where <paramref name="laid"/> drew them whole (2026-10-13: on either side).</summary>
    private void ApplyHintZones(HintRowLaid laid, int max)
    {
        var strip = laid.Find(HintPartKind.Strip);
        _hintStrip = strip is { Whole: true } s ? s.Text : "";
        _hintStripColumn = strip?.Column ?? 0;
        var queued = laid.Find(HintPartKind.Queued);
        _queuedColumn = queued is { Whole: true } q ? q.Column : -1;
        _queuedCells = queued is { Whole: true } ? queued.Value.Cells : 0;
        var tally = laid.Find(HintPartKind.Usage);
        _usageColumn = tally is { Whole: true } u ? u.Column : -1;
        _usageCells = tally is { Whole: true } ? tally.Value.Cells : 0;
        _trailerColumn = -1;
        _trailerEnd = int.MaxValue;
        _markColumn = -1;
        _markEnd = int.MaxValue;
        if (laid.Find(HintPartKind.Model) is { Whole: true } model)
        {
            // The last part runs to the edge (the row's last cell is never drawn), as the trailer always did.
            _trailerColumn = model.Column;
            _trailerEnd = model.Last ? int.MaxValue : model.Column + model.Cells;
            if (model.Mark.Length > 0 && model.Text.EndsWith(model.Mark, StringComparison.Ordinal))
            {
                _markColumn = model.Column + model.Cells - TextCells.Width(model.Mark);
                _markEnd = _trailerEnd;
            }
        }

        var path = laid.Find(HintPartKind.Path);
        _hintPathColumn = path is { Whole: true } pa ? pa.Column : -1;
        _hintPathCells = path is { Whole: true } ? (path.Value.Last ? Math.Max(path.Value.Cells, max - path.Value.Column) : path.Value.Cells) : 0;
        var timers = laid.Find(HintPartKind.Timers);
        _hintTimersColumn = timers is { Whole: true, Cells: > 0 } ti ? ti.Column : -1;
        _hintTimersCells = timers is { Whole: true } ? timers.Value.Cells : 0;
    }

    /// <summary>
    /// <paramref name="laid"/>'s text in the hint style (2026-10-13): <paramref name="frame"/> at the spinner's start in the spinner's
    /// style, the model's reasoning mark in the mark's own, wherever they stand.
    /// </summary>
    private void WriteHintText(HintRowLaid laid, string frame)
    {
        string text = laid.Text;
        var spans = new List<(int Index, int Length, Style Style)>();
        foreach (var part in laid.Parts)
        {
            if (part.Kind == HintPartKind.Spinner && frame.Length > 0 && Holds(part.Index, frame))
            {
                spans.Add((part.Index, frame.Length, Theme.SpinnerStyle));
            }
            else if (part.Kind == HintPartKind.Model && part.Mark.Length > 0 && part.Text.EndsWith(part.Mark, StringComparison.Ordinal)
                && Holds(part.Index + part.Text.Length - part.Mark.Length, part.Mark))
            {
                spans.Add((part.Index + part.Text.Length - part.Mark.Length, part.Mark.Length, Theme.TrailerMark));
            }
        }

        int at = 0;
        foreach (var (index, length, style) in spans.OrderBy(s => s.Index))
        {
            if (index < at)
            {
                continue;
            }

            if (index > at)
            {
                _inner.Write(new RawText(text[at..index], Theme.Hint));
            }

            _inner.Write(new RawText(text.Substring(index, length), style));
            at = index + length;
        }

        if (at < text.Length)
        {
            _inner.Write(new RawText(text[at..], Theme.Hint));
        }

        bool Holds(int index, string what) =>
            index >= 0 && index + what.Length <= text.Length && string.CompareOrdinal(text, index, what, 0, what.Length) == 0;
    }

    /// <summary>
    /// The hint row's two groups in <paramref name="max"/> cells (later on 2026-10-12; the sides since 2026-10-13, the user's ask): the left
    /// group from column 0 and the right ending on the last cell, <see cref="HintSeparator"/> between the parts of a group and
    /// <see cref="TrailerGap"/> at least between the groups. What gives way when the row is too narrow, in the user's order: the model's
    /// name and the working directory first, wherever they are pinned — the name (its mark never cut) fitted to what all but the path
    /// leaves, half the row at most at the right as ever (<see cref="Trail"/>), then the path to what is left, each cut to eight cells
    /// and then gone (<see cref="FitName"/>, <see cref="FitPath"/>); then the right group from its inner end (whole parts go, the first kept
    /// one maybe cut from the front, no button then); then the left group from its end (<see cref="Fit"/>). Pure.
    /// </summary>
    private static HintRowLaid LayParts(List<HintPart> leftParts, List<HintPart> rightParts, int max)
    {
        var left = leftParts.Where(Present).ToList();
        var right = rightParts.Where(Present).ToList();
        bool nameRight = right.Any(part => part.Kind == HintPartKind.Model);
        FitText(HintPartKind.Model, (part, room) => FitTrailer(part.Text, part.Mark, nameRight ? Math.Min(max / 2, room) : room), withoutPath: true);
        FitText(HintPartKind.Path, (part, room) => FitPath(part.Text, room), withoutPath: false);

        bool cutFirst = false;
        bool spinnerRight = right.Any(part => part.Kind == HintPartKind.Spinner);
        if (spinnerRight)
        {
            // The spinner kept last (later on 2026-10-13, the user's pick): the right group's other parts go first from its inner
            // end, then the left gives way (below, as Fit cuts it); the spinner itself is cut from its end only when it alone is too wide.
            while (right.Count > 1 && Measure(withoutPath: false) > max)
            {
                right.RemoveAt(right[0].Kind == HintPartKind.Spinner ? 1 : 0);
            }

            if (TextCells.Width(JoinParts(right)) > max)
            {
                var only = right.Single(part => part.Kind == HintPartKind.Spinner);
                right = [only with { Text = Fit(only.Text, max) }];
                left.Clear();
                cutFirst = true;
            }
        }

        string leftText = JoinParts(left);
        if (right.Count > 0 && !spinnerRight)
        {
            int room = max - TextCells.Width(leftText) - (left.Count == 0 ? 0 : TrailerGap);
            right = room < 1 ? [] : FitRightParts(right, room, out cutFirst);
        }

        string rightText = JoinParts(right);
        int leftCells = right.Count == 0 ? max : max - TextCells.Width(rightText) - (left.Count == 0 ? 0 : TrailerGap);
        if (left.FindIndex(part => part.Kind == HintPartKind.Spinner) is var at0 and > 0 && TextCells.Width(JoinParts(left.Take(at0 + 1))) >= leftCells)
        {
            // A spinner pinned late in the left group (later on 2026-10-13) is kept last there too: the cut, from the end, would reach
            // it, so the parts after it go whole, then those before it from the nearest, until it ends within the room.
            int spinnerAt = at0;
            left.RemoveRange(spinnerAt + 1, left.Count - spinnerAt - 1);
            while (spinnerAt > 0 && TextCells.Width(JoinParts(left)) > leftCells)
            {
                left.RemoveAt(--spinnerAt);
            }

            leftText = JoinParts(left);
        }
        string fitted = Fit(leftText, Math.Max(0, leftCells));
        string text = rightText.Length == 0 ? fitted : fitted + new string(' ', Math.Max(0, max - TextCells.Width(fitted) - TextCells.Width(rightText))) + rightText;

        var placed = new List<HintPlacedPart>(left.Count + right.Count);
        int ahead = 0;
        int index = 0;
        for (int i = 0; i < left.Count; i++)
        {
            if (i > 0)
            {
                ahead += TextCells.Width(HintSeparator);
                index += HintSeparator.Length;
            }

            var part = left[i];
            int cells = TextCells.Width(part.Text);
            placed.Add(new HintPlacedPart(part.Kind, part.Text, ahead, cells, index, Whole(ahead, part.Text, leftText, leftCells), right.Count == 0 && i == left.Count - 1, part.Mark));
            ahead += cells;
            index += part.Text.Length;
        }

        int column = max - TextCells.Width(rightText);
        int at = text.Length - rightText.Length;
        for (int i = 0; i < right.Count; i++)
        {
            if (i > 0)
            {
                column += TextCells.Width(HintSeparator);
                at += HintSeparator.Length;
            }

            var part = right[i];
            int cells = TextCells.Width(part.Text);
            placed.Add(new HintPlacedPart(part.Kind, part.Text, column, cells, at, !(i == 0 && cutFirst), i == right.Count - 1, part.Mark));
            column += cells;
            at += part.Text.Length;
        }

        return new HintRowLaid(text, placed);

        static bool Present(HintPart part) => part.Text.Length > 0 || (part.Kind == HintPartKind.Model && part.Mark.Length > 0);

        // A text part fitted to the room the rest of the row leaves it (a stand-in of one cell measures where it goes); gone when empty.
        void FitText(HintPartKind kind, Func<HintPart, int, string> fit, bool withoutPath)
        {
            foreach (var group in new[] { left, right })
            {
                int i = group.FindIndex(part => part.Kind == kind);
                if (i < 0)
                {
                    continue;
                }

                var part = group[i];
                group[i] = part with { Text = "x" };
                int rest = Measure(withoutPath) - 1;
                string shown = fit(part, max - rest);
                if (shown.Length == 0)
                {
                    group.RemoveAt(i);
                }
                else
                {
                    group[i] = part with { Text = shown };
                }
            }
        }

        int Measure(bool withoutPath)
        {
            var l = withoutPath ? left.Where(part => part.Kind != HintPartKind.Path).ToList() : left;
            var r = withoutPath ? right.Where(part => part.Kind != HintPartKind.Path).ToList() : right;
            return TextCells.Width(JoinParts(l)) + TextCells.Width(JoinParts(r)) + (l.Count > 0 && r.Count > 0 ? TrailerGap : 0);
        }
    }

    private static string JoinParts(IEnumerable<HintPart> parts) => string.Join(HintSeparator, parts.Select(part => part.Text));

    /// <summary>
    /// The right group in <paramref name="room"/> cells (2026-10-13): whole parts kept from its outer (right) end; the first that does not
    /// fit is cut from the front (a path as <see cref="FitPath"/> cuts it, the model gone) — <paramref name="cutFirst"/> — and everything before it goes.
    /// </summary>
    private static List<HintPart> FitRightParts(List<HintPart> parts, int room, out bool cutFirst)
    {
        cutFirst = false;
        if (TextCells.Width(JoinParts(parts)) <= room)
        {
            return parts;
        }

        var kept = new List<HintPart>();
        for (int i = parts.Count - 1; i >= 0; i--)
        {
            var candidate = new List<HintPart> { parts[i] };
            candidate.AddRange(kept);
            if (TextCells.Width(JoinParts(candidate)) <= room)
            {
                kept = candidate;
                continue;
            }

            int left = room - (kept.Count == 0 ? 0 : TextCells.Width(JoinParts(kept)) + TextCells.Width(HintSeparator));
            string cut = left < 1 ? "" : parts[i].Kind switch
            {
                HintPartKind.Path => FitPath(parts[i].Text, left),
                HintPartKind.Model => "",
                _ => FitTail(parts[i].Text, left),
            };
            if (cut.Length > 0)
            {
                kept.Insert(0, parts[i] with { Text = cut });
                cutFirst = true;
            }

            break;
        }

        return kept;
    }

    /// <summary>
    /// The model's name and its <paramref name="mark"/> in <paramref name="room"/> cells (2026-10-13, <see cref="Trail"/>'s fit with its room
    /// given): the name as <see cref="FitName"/> fits it ahead of the mark, which is never cut; the mark alone with no name, where it fits.
    /// </summary>
    private static string FitTrailer(string trailer, string mark, int room)
    {
        if (trailer.Length == 0)
        {
            return TextCells.Width(mark) <= room ? mark : "";
        }

        int markCells = mark.Length == 0 ? 0 : TextCells.Width(mark) + MarkSeparator.Length;
        string name = FitName(trailer, room - markCells);
        return name.Length == 0 ? "" : TrailerText(name, mark);
    }

    /// <summary>Whether <paramref name="text"/>, <paramref name="ahead"/> cells into <paramref name="row"/>, is whole once the row is cut to <paramref name="cells"/> (<see cref="RecordQueued"/>'s rule).</summary>
    private static bool Whole(int ahead, string text, string row, int cells) =>
        ahead >= 0 && (TextCells.Width(row) <= cells || ahead + TextCells.Width(text) < cells);

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

    /// <summary><see cref="RedrawHint"/> for the Hints bar (2026-10-14): its row again, in place; nothing while none is drawn.</summary>
    private void RedrawHints()
    {
        if (!_drawn || _batch > 0 || _modal > 0 || _hintsRows == 0)
        {
            return;
        }

        RedrawRow(HintsRowBelowCursor, WriteHintsRow);
    }

    /// <summary>
    /// The Hints bar after its text may have changed (2026-10-14), under the gate: the whole pane again when its row comes or goes
    /// (true), else its row again in place when the text moved. Nothing while lifted, batched or modal: the draw that ends them reads it.
    /// </summary>
    private bool RefreshHintsLocked()
    {
        if (!_drawn || _batch > 0 || _modal > 0)
        {
            return false;
        }

        if (HintsRowsFor(Height) != _hintsRows)
        {
            Redraw();
            return true;
        }

        // A collapse due with the row still wanted (a turn's end with the keys back) is spent here: the natural moment passed,
        // and a flag left standing would take the row at some later, unnatural one — when the hint goes mid-typing.
        _hintsCollapseDue = false;
        if (HintsChanged())
        {
            RedrawHints();
        }

        return false;
    }

    /// <summary>
    /// The Hints bar's row (2026-10-14, the user's picks: one hint, the ladder's, plain and dim, cut at the edge; the kill switch's
    /// alert in the warning's style): blank while the row is held with nothing to say. The text remembered for the tick.
    /// </summary>
    private void WriteHintsRow()
    {
        string? text = HintsText();
        string shown = Fit(Spaced(text ?? ""), Math.Max(1, Width - 1));
        bool alert = text is not null && _alertHint is not null && !_alertFlash && string.Equals(text, _alertHint, StringComparison.Ordinal);
        _inner.Write(new RawText(shown, alert ? Theme.WarnText : Theme.Hint));
        _inner.Write(EraseLineEnd);
        _shownHints = shown;
        if (text is not null)
        {
            _hintsQuietSince = 0;   // a hint: no quiet to count
        }
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

        RedrawRow(PerfRowBelowCursor, () => WritePerfRow(row));
    }

    /// <summary><see cref="RedrawHint"/> for the home bar (2026-10-12): its row again, in place, the pane's last; nothing while none is drawn.</summary>
    private void RedrawHome()
    {
        if (!_drawn || _batch > 0 || _modal > 0 || _homeRows == 0 || _home(Math.Max(1, Width - 1)) is not { } row)
        {
            return;
        }

        RedrawRow(HomeRowBelowCursor, () => WriteHomeRow(row));
    }

    /// <summary>The home bar's row on the cursor's row: its runs in their styles, the rest of the row erased; the key and the spans remembered for the tick and the clicks.</summary>
    private void WriteHomeRow(UI.HomeRow row)
    {
        foreach (var segment in row.Segments)
        {
            _inner.Write(new RawText(segment.Text, segment.Style));
        }

        _inner.Write(EraseLineEnd);
        _shownHome = row.Key;
        _homeSpans = row.Spans;
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
                _hintsCollapseDue = true;   // a resize redraws all: a Hints bar row held blank goes with it (2026-10-14)
                if (h != _lastHeight)
                {
                    // The held height was counted over the old height (2026-10-14): the page on show sets it afresh.
                    _paneFloor = 0;
                }

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
                // The session's name landed or went (the model's title arrives off-thread), the running processes' count on
                // the lower rule moved (2026-10-12), or the
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

            HintsIdleCheck();
            int hints = HintsRowsFor(h);
            if (hints != _hintsRows || ToolbarRowsFor(h, hints) != _toolbarRows || PerfRowsFor(h, hints) != _perfRows || HomeRowsFor(h, hints) != _homeRows || !Bars(_barOrder()).SequenceEqual(_drawnBars))
            {
                // The Hints bar (2026-10-14), the toolbar, the performance bar or the home bar came or went (a hint, the screen's
                // settings, a window at the edge), or their order changed (Toolbar order, 2026-10-12): the whole pane again — its
                // shape changed.
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

            if (HomeChanged())
            {
                // A device changed, a click's mark came or went: the row again, in place.
                RedrawHome();
            }

            if (ScrollRowChanged())
            {
                // The count below moved with nothing redrawing it (a safety net: the writes that change it redraw it).
                RedrawScrollRow();
            }

            if (HintsChanged())
            {
                // Another hint on the row the Hints bar holds (2026-10-14): the row again, in place.
                RedrawHints();
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

    /// <summary>
    /// The Hints bar's idle timeout (2026-10-14): while it has a hint, or a key was pressed, the clock starts again; at rest, with
    /// neither for <see cref="HintsIdle"/>, a held blank row is due to go — the tick's shape check then lifts and draws without it.
    /// Never under a reply (the row held blank through a turn comes back to the keys at its end in place) nor under <c>always</c>.
    /// </summary>
    private void HintsIdleCheck()
    {
        long now = _time.GetUtcNow().UtcTicks;
        if (_hintsRows == 0 || HintsText() is not null || _busyLabel is not null)
        {
            _hintsQuietSince = 0;
            return;
        }

        // The quiet starts at the first tick that sees it (a lifted or modal stretch skips the tick, so a time the hint was
        // last seen could be long past by then and the row would go the moment its hint did).
        if (_hintsQuietSince == 0)
        {
            _hintsQuietSince = now;
        }

        if (HintsBarMode.Resolve(_hintsBar()) == HintsBarStyle.Auto && now - _hintsQuietSince >= HintsIdle.Ticks)
        {
            _hintsCollapseDue = true;
        }
    }

    /// <summary>The Hints bar's text now is not the drawn one, its row standing (a presence change is <see cref="HintsRowsFor"/> against the drawn rows).</summary>
    private bool HintsChanged() =>
        _hintsRows > 0 && !string.Equals(Fit(Spaced(HintsText() ?? ""), Math.Max(1, Width - 1)), _shownHints, StringComparison.Ordinal);

    /// <summary>
    /// The Status bar's whole text while it stands in for the sections (2026-10-14: the drag, a flash, and the kill switch's alert
    /// while the Hints bar has no row to put it on), or null.
    /// </summary>
    private string? StatusWhole() => _dragHint ?? (_alertHint is { } alert && (_alertFlash || _hintsRows == 0) ? alert : null);

    /// <summary>The toolbar <see cref="Toolbar"/> answers now is not the drawn one (its text, or its off slabs since 2026-10-03 — a presence change is <see cref="ToolbarRowsFor"/> against the drawn rows).</summary>
    private bool ToolbarChanged() => _toolbarRows > 0 && _toolbar() is { } toolbar
        && (!string.Equals(LayToolbar(toolbar, Math.Max(1, Width - 1)).Row(Math.Max(1, Width - 1)), _shownToolbar, StringComparison.Ordinal)
            || !string.Equals(toolbar.OffKey, _shownToolbarOff, StringComparison.Ordinal));

    /// <summary>The performance bar <see cref="Perf"/> answers now is not the drawn one (its text — a presence change is <see cref="PerfRowsFor"/> against the drawn rows).</summary>
    private bool PerfChanged() => _perfRows > 0 && _perf(Math.Max(1, Width - 1)) is { } row && !string.Equals(row.Text, _shownPerf, StringComparison.Ordinal);

    /// <summary>The home bar <see cref="Home"/> answers now is not the drawn one (its key — a presence change is <see cref="HomeRowsFor"/> against the drawn rows).</summary>
    private bool HomeChanged() => _homeRows > 0 && _home(Math.Max(1, Width - 1)) is { } row && !string.Equals(row.Key, _shownHome, StringComparison.Ordinal);

    /// <summary>The standing hint (the overlay's while one is open) with the trailer is not what the hint row shows.</summary>
    private bool HintChanged() =>
        !string.Equals(StatusWhole() is { } whole ? Fit(Spaced(whole), Math.Max(1, Width - 1)) : PlaceStanding(Math.Max(1, Width - 1)).Text, _shownHint, StringComparison.Ordinal);

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
    /// The toolbar's strip in <paramref name="cells"/>: whole when it fits, else as many whole glyphs as fit and nothing after them; empty
    /// where not even one fits. The glyphs are the strip's, one blank between. (A cut strip ended on <c>…</c> until 2026-10-04, then on a
    /// <c>+N</c> for the N left off until later on 2026-10-13, when the user had the glyphs a narrow row leaves off simply go, as the home
    /// bar's chips do.) Pinned.
    /// </summary>
    public static string FitStrip(string strip, int cells)
    {
        ArgumentNullException.ThrowIfNull(strip);
        if (TextCells.Width(strip) <= cells)
        {
            return strip;
        }

        string[] glyphs = strip.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int keep = glyphs.Length - 1; keep > 0; keep--)
        {
            string shown = string.Join(' ', glyphs, 0, keep);
            if (TextCells.Width(shown) <= cells)
            {
                return shown;
            }
        }

        return "";
    }

    /// <summary>
    /// <paramref name="path"/> with <paramref name="home"/> (the user's profile folder) as <c>~</c> (2026-10-04, the UI review: the
    /// profile's <c>C:\Users\name</c> took the toolbar's room and said nothing): <c>~\Repo\app</c>; the path as it is outside it or
    /// with no home. Ignores case, as Windows paths do. Pure; pinned.
    /// </summary>
    public static string HomeTilde(string path, string? home)
    {
        ArgumentNullException.ThrowIfNull(path);
        string root = (home ?? "").TrimEnd('\\', '/');
        if (root.Length == 0 || !path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        if (path.Length == root.Length)
        {
            return "~";
        }

        return path[root.Length] is '\\' or '/' ? "~" + path[root.Length..] : path;
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
