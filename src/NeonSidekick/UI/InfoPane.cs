using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.UI;

/// <summary>
/// One tab of the info pane: a title in the strip and the content it shows, built on every show (live state) — and, for
/// a tab not shown, once when the pane opens (and again on a width change) to measure it, every tab padded to the
/// tallest one's height (2026-10-01).
/// </summary>
public sealed record InfoTab(string Title, Func<IRenderable> Content)
{
    /// <summary>The tab's toggle on the strip (2026-10-12, <see cref="InfoToggle"/>); null for none (every tab but <c>/help</c>'s Keys).</summary>
    public InfoToggle? Toggle { get; init; }
}

/// <summary>
/// A tab's toggle (2026-10-12, the user's ask: a "key check" action at the top of <c>/help</c>'s Keys tab that shows
/// <c>/keycheck</c>'s list and, pressed again, the Keys tab as it was): <paramref name="Title"/> on the strip after the tabs' titles,
/// <paramref name="Key"/> the key that presses it, <paramref name="Word"/> what the hint calls it, and <paramref name="Content"/> what
/// the tab shows while it is on — built each time it is switched on, never to measure the pane.
/// </summary>
public sealed record InfoToggle(string Title, char Key, string Word, Func<IRenderable> Content);

/// <summary>
/// The info pane: a box that rises from the bottom of the window over the input row — a tab strip,
/// the active tab's content, a hint row — and closes on ESC. The Claude Code <c>/help</c> shape,
/// kept out of the transcript. It is an overlay of the <see cref="ScreenPane"/>, which draws it and
/// keeps it in place under flow writes and a resize; this class owns the tabs, the viewport and the keys.
///
/// <para>Keys: ESC closes; → and Tab go to the next tab, ← and Shift+Tab to the previous one,
/// wrapping; ↑/↓ scroll the content by a line, PageUp/PageDown by a page, Home/End to either end;
/// a typed character starts a find over the shown tab's lines (2026-10-07, the user's ask, phase 4 of the UI round): the
/// matches marked, the view on the first from its top down, the hint row the find's (<see cref="FindHint"/>); Enter or F3 goes
/// to the next, Shift with either back, Backspace erases, and the first ESC clears it. Every other key is swallowed, so nothing
/// typed by accident lands on the input line.
/// The keys come through the <see cref="KeySource"/> like the input line's, never from the console
/// directly. Without the pane on the screen (no geometry) there is nothing to show and nothing is
/// read: the caller prints instead.</para>
///
/// <para>Mouse: the pane takes the console's mouse for the visit (the injected hook, the one the
/// menu pane uses) and hands it back
/// when it closes; a left click on a tab's title (<see cref="TabAt"/>) switches to that tab exactly
/// as the keys would; one on the × at the corner is ESC; two left clicks off the pane — the
/// transcript, a rule, the hint row — within <see cref="DoubleClick.Interval"/> close it too (later
/// on 2026-09-18, the menu's rule); a single click anywhere else, a right click and a drag do
/// nothing. The terminal's own selection needs Shift while the pane is up, as it does over a draft.</para>
///
/// <para>The viewport is the <see cref="MenuPane"/>'s: the content is laid out at the window's width
/// once per draw, and when it outgrows the rows the window leaves over one transcript row, the strip
/// and the spacer, one fewer row is shown and a dim <see cref="MenuPane.MoreHint"/> row takes the last
/// slot (<see cref="Viewport"/>, pure). Switching tabs starts at the top again. The rows are the
/// <c>Menus max height</c> share of the window (<see cref="ScreenPane.MenuContentRows"/>, 2026-10-01), and
/// every tab is padded with blank rows under its content to the tallest tab's height (the same day, the
/// user's ask: the pane jumped as one tabbed through <c>/help</c> and <c>/usage</c>).</para>
///
/// <para>One tab (2026-10-04, the user's report: the one-tab list panes read <c>/screen list   /screen list</c>): the strip is the
/// label alone, no chip, and the hint is <see cref="SingleTabHintText"/>, with nothing to switch to.</para>
///
/// <para>A tab's toggle (2026-10-12, the user's ask, <see cref="InfoToggle"/>): while that tab is shown its title follows the tabs'
/// after a dim <c>│</c>, lit (<see cref="Theme.TabLit"/>, a switch, so no underline) while on. A click on it or its key — while no find
/// is typed, the menus' rule for a button's key — swaps the tab's content for the toggle's and back, the view from the top as a tab
/// switch starts it; the tab keeps it on for the visit, and the hint names the key (<see cref="ToggleHint"/>).</para>
/// </summary>
public sealed class InfoPane
{
    /// <summary>The strip's label for <c>/help</c> (the glyph since 2026-09-28, the user's ask: every other pane's label wore one).</summary>
    public const string Title = "❓ Help";

    /// <summary>The hint row under the pane (the find and PgUp/PgDn since 2026-10-07, in the <c>key = what</c> form of the menus' hints). Pinned.</summary>
    public const string HintText = "↑/↓ PgUp/PgDn = scroll · ←/→ = tabs · type = find · ESC = close";

    /// <summary>The hint row under a pane of one tab (2026-10-04): no tabs to switch. Pinned.</summary>
    public const string SingleTabHintText = "↑/↓ PgUp/PgDn = scroll · type = find · ESC = close";

    /// <summary>
    /// The hint row on a tab with a toggle (2026-10-12): <see cref="HintText"/> (or <see cref="SingleTabHintText"/> on a pane of one
    /// tab) with the toggle's key before the find, <c>k = key check</c>. Pinned.
    /// </summary>
    public static string ToggleHint(char key, string word, bool tabs = true) =>
        (tabs ? "↑/↓ PgUp/PgDn = scroll · ←/→ = tabs · " : "↑/↓ PgUp/PgDn = scroll · ") + key + " = " + word + " · type = find · ESC = close";

    /// <summary>What sits between the tabs' last title and a toggle's (2026-10-12): a dim bar, one cell as <see cref="TitleSeparator"/>, so the layout is the strip's.</summary>
    public const string ToggleSeparator = "│";

    /// <summary>The longest find (the menus' filter's cap).</summary>
    public const int FindMaxLength = MenuFilter.MaxLength;

    /// <summary>
    /// The hint row while a find is typed (2026-10-07, phase 4 of the UI round): the text, which match of how many the view is
    /// on and the keys; with none, the keys that get out of it. Pinned.
    /// </summary>
    public static string FindHint(string find, int index, int count) =>
        count > 0
            ? $"find: {find} · {(index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)} of {count.ToString(System.Globalization.CultureInfo.InvariantCulture)} · Enter = next · {FindBack} = back · ESC = clear"
            : $"find: {find} · no match · Backspace = erase · ESC = clear";

    /// <summary>The rows above the content when the strip fits one row: the strip and the spacer; a strip that takes more rows (<see cref="TabStripLayout"/>) adds them.</summary>
    public const int HeaderRows = 2;

    /// <summary>Content lines one wheel notch scrolls (Windows' own lines-per-notch default); a notch lands anywhere, the pane is modal.</summary>
    public const int WheelLines = 3;

    private const int DefaultHeight = 24;

    private readonly ScreenPane _pane;
    private readonly KeySource _keys;
    private readonly Action<bool>? _mouse;
    private readonly DoubleClick _clicks;

    private int _first;
    private int _shown;
    private int _count;
    private int _stripRows = 1;
    // Whether the last draw put the more row under the content (2026-10-14: a click on it pages).
    private bool _moreShown;
    private readonly Func<string?>? _clipboard;
    private int _width = int.MaxValue;

    // The tallest tab's content lines and the width they were laid out at (2026-10-01): every tab is padded to it, so the
    // pane keeps its height as one tabs through it; −1 = not measured this visit.
    private int _tallest;
    private int _tallestWidth = -1;

    // The shown tab's laid-out lines, its index and the width (2026-10-03, the user's ask: a held arrow in /sys lagged, every
    // press building the tab afresh — the prompt's facts, the skill scan — and wrapping all of it): a scroll draws from these;
    // a tab switch, a new width or a new visit builds again. −1 = none this visit.
    private List<SegmentLine>? _content;
    private int _contentTab = -1;
    private int _contentWidth = -1;
    private bool _contentToggled;

    // The tabs whose toggle is on this visit (2026-10-12, InfoToggle): kept across a tab switch, cleared when the pane opens.
    private readonly HashSet<int> _toggled = [];

    // The find (2026-10-07, phase 4 of the UI round): the text typed this visit, where it occurs in the shown tab's lines and
    // which of those the view is on. The places are worked out again when the text, the tab or the width moves; _findMoved says
    // the text or the tab did, so the find starts again from the view's top; _seek scrolls the one it is on into view.
    private string _find = "";
    private List<TextFind.Hit> _hits = [];
    private int _hit;
    private string _hitsFind = "";
    private int _hitsTab = -1;
    private int _hitsWidth = -1;
    private bool _findMoved;
    private bool _seek;

    /// <param name="mouse">Takes (true) or hands back (false) the console's mouse; null when the screen has none to take.</param>
    /// <param name="clipboard">The clipboard's text a right-click pastes into the find (2026-10-14, the user's call); null = a right-click is nothing.</param>
    public InfoPane(ScreenPane pane, KeySource keys, Action<bool>? mouse = null, Func<string?>? clipboard = null)
    {
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _mouse = mouse;
        _clipboard = clipboard;
        _clicks = new DoubleClick(pane.Time);
    }

    /// <summary>
    /// What follows the label when there are titles (2026-10-07, the user's pick): a space and a dim bar, two cells as the
    /// two-space gap it replaced, so the first title's column is where it was. A later row of a wrapped strip carries the bar
    /// on under it, a column of its own between the label and the titles.
    /// </summary>
    public const string LabelSeparator = " │";

    /// <summary>What sits between two titles (2026-10-07): one dim dot, where two blank cells were, so the strip is a cell shorter per title and reads as one row of choices.</summary>
    public const string TitleSeparator = "·";

    /// <summary>The cells between the label and the first title's highlight (<see cref="LabelSeparator"/>).</summary>
    private const int LabelGap = 2;

    /// <summary>The cells between one title's highlight and the next's (<see cref="TitleSeparator"/>).</summary>
    private const int TitleGap = 1;

    /// <summary>
    /// The strip: the label, the <see cref="LabelSeparator"/>, then every title with a space either side (so the strip does not
    /// shift when the mark moves) and a <see cref="TitleSeparator"/> between them, the active one <see cref="Theme.TabActive"/>
    /// (underlined, 2026-10-07; highlighted as the cursor's row before), the others <see cref="Theme.TabIdle"/>. A title with a
    /// badge (<paramref name="badges"/>, by index; null or a null entry for none) wears it right after, inside its click span. Pinned.
    /// </summary>
    public static string TabStripMarkup(string label, IReadOnlyList<string> titles, int active, IReadOnlyList<string?>? badges = null) =>
        TabStripRows(label, titles, Set(active), Theme.TabActive, int.MaxValue, badges)[0];

    /// <summary>
    /// <see cref="TabStripMarkup(string, IReadOnlyList{string}, int)"/> with any number of titles lit (later on
    /// 2026-09-29: a button strip whose buttons are switches, the embedded model filters), each <see cref="Theme.TabLit"/>:
    /// a switch is on, not the tab one is on, so no underline. Pinned.
    /// </summary>
    public static string TabStripMarkup(string label, IReadOnlyList<string> titles, IReadOnlySet<int> active) =>
        TabStripRows(label, titles, active, Theme.TabLit, int.MaxValue, null)[0];

    /// <summary>
    /// Where each title of the strip sits at <paramref name="width"/> columns: its row and the column
    /// its highlight starts in. A strip wider than the row's budget breaks between titles, and every
    /// row after the first starts at the column of the first title, so a title that does not fit lines
    /// up under the one above instead of dropping to column 0 under the label (2026-09-27, the user's
    /// ask: Spectre's word-wrap had put /tools' Options under the 🛠️ label, where no click reached it).
    /// The budget is the one <see cref="ScreenPane"/> tests before drawing the
    /// <see cref="ScreenPane.CloseGlyph"/>, so the first row always keeps the ×; the later rows take the
    /// same budget so the columns stay even. A title too wide for any row sits alone on its own. A badge counts in its
    /// title's cells (<see cref="TitleCells"/>). Pure.
    /// </summary>
    public static (int Rows, (int Row, int Column)[] Places) TabStripLayout(string label, IReadOnlyList<string> titles, int width, IReadOnlyList<string?>? badges = null)
    {
        ArgumentNullException.ThrowIfNull(label);
        return Layout(StripIndent(label), titles, width, badges);
    }

    /// <summary>
    /// The second strip under a tab that holds sub-tabs (2026-10-10, the user's call: <c>/tools</c>' Database and Media), laid out as
    /// <see cref="TabStripLayout"/> lays out the first's later rows, its titles starting <see cref="SubStripLead"/>'s cells further in,
    /// under the strip of <paramref name="label"/>. Pure.
    /// </summary>
    public static (int Rows, (int Row, int Column)[] Places) SubStripLayout(string label, IReadOnlyList<string> titles, int width, IReadOnlyList<string?>? badges = null)
    {
        ArgumentNullException.ThrowIfNull(label);
        return Layout(StripIndent(label) + TextCells.Width(SubStripLead), titles, width, badges);
    }

    /// <summary>
    /// What leads the second strip's first row, after the bar (2026-10-10): a space and a hook, so its titles read as the active tab's
    /// own; a later row of it is indented by as many cells.
    /// </summary>
    public const string SubStripLead = " ↳";

    /// <summary>The column the first title's highlight starts in, after <paramref name="label"/> and the <see cref="LabelSeparator"/>.</summary>
    private static int StripIndent(string label) => TextCells.Width(TextCells.Spaced(label)) + LabelGap;

    private static (int Rows, (int Row, int Column)[] Places) Layout(int indent, IReadOnlyList<string> titles, int width, IReadOnlyList<string?>? badges)
    {
        ArgumentNullException.ThrowIfNull(titles);
        int budget = width - 1 - ScreenPane.TrailerGap - TextCells.Width(ScreenPane.CloseGlyph);
        var places = new (int Row, int Column)[titles.Count];
        int row = 0;
        int column = indent;
        bool first = true;
        for (int i = 0; i < titles.Count; i++)
        {
            int cells = TitleCells(titles, badges, i);
            if (!first && column + cells > budget)
            {
                row++;
                column = indent;
            }

            places[i] = (row, column);
            column += cells + TitleGap;
            first = false;
        }

        return (row + 1, places);
    }

    /// <summary>
    /// The strip laid out for <paramref name="width"/> columns (<see cref="TabStripLayout"/>): one markup
    /// line per row, the first as <see cref="TabStripMarkup"/> draws it, the later ones indented to the
    /// first title's column, the <see cref="LabelSeparator"/>'s bar carried down, with the same separators and styles.
    /// </summary>
    public static IReadOnlyList<string> TabStripRows(string label, IReadOnlyList<string> titles, int active, int width, IReadOnlyList<string?>? badges = null) =>
        TabStripRows(label, titles, Set(active), Theme.TabActive, width, badges);

    /// <summary>
    /// The strip with a toggle after the titles (2026-10-12, <see cref="InfoToggle"/>): <paramref name="toggle"/>'s title laid out as one
    /// more title (so <see cref="TabAt(string, IReadOnlyList{string}, int, int, int, IReadOnlyList{string?}?)"/> over the titles and it
    /// answers <c>titles.Count</c> on it), after a dim <see cref="ToggleSeparator"/>, <see cref="Theme.TabLit"/> while on. Pinned.
    /// </summary>
    public static IReadOnlyList<string> TabStripRows(string label, IReadOnlyList<string> titles, int active, int width, (string Title, bool On) toggle)
    {
        ArgumentNullException.ThrowIfNull(titles);
        return TabStripRows(label, [.. titles, toggle.Title], Set(active), Theme.TabActive, width, null, toggle: toggle.On);
    }

    /// <summary><see cref="TabStripRows(string, IReadOnlyList{string}, int, int)"/> with any number of titles lit (later on 2026-09-29).</summary>
    public static IReadOnlyList<string> TabStripRows(string label, IReadOnlyList<string> titles, IReadOnlySet<int> active, int width) =>
        TabStripRows(label, titles, active, Theme.TabLit, width, null);

    /// <summary>
    /// The second strip (2026-10-10, <see cref="SubStripLayout"/>): every row the label's width of blanks and the bar carried down, the
    /// first then <see cref="SubStripLead"/>, the titles drawn as the first strip draws them, <paramref name="active"/> underlined. Pinned.
    /// </summary>
    public static IReadOnlyList<string> SubStripRows(string label, IReadOnlyList<string> titles, int active, int width, IReadOnlyList<string?>? badges = null) =>
        TabStripRows(label, titles, Set(active), Theme.TabActive, width, badges, sub: true);

    private static IReadOnlySet<int> Set(int active) => active < 0 ? new HashSet<int>() : new HashSet<int> { active };

    /// <summary>The badge of title <paramref name="i"/>: null for none, and for an empty one.</summary>
    private static string? BadgeOf(IReadOnlyList<string?>? badges, int i) =>
        badges is not null && i < badges.Count && !string.IsNullOrEmpty(badges[i]) ? badges[i] : null;

    /// <summary>The cells title <paramref name="i"/> takes, its click span: a space either side, the title, and its badge right after it.</summary>
    private static int TitleCells(IReadOnlyList<string> titles, IReadOnlyList<string?>? badges, int i) =>
        TextCells.Width(titles[i]) + 2 + (BadgeOf(badges, i) is { } badge ? TextCells.Width(badge) : 0);

    // toggle: null for a strip of titles alone; else the last title is a toggle's (2026-10-12), on or off.
    private static IReadOnlyList<string> TabStripRows(string label, IReadOnlyList<string> titles, IReadOnlySet<int> active, Style lit, int width, IReadOnlyList<string?>? badges, bool sub = false, bool? toggle = null)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(titles);
        ArgumentNullException.ThrowIfNull(active);
        var (rows, places) = sub ? SubStripLayout(label, titles, width, badges) : TabStripLayout(label, titles, width, badges);
        string separator = Theme.TabSeparator.ToMarkup();
        string bar = $"[{separator}]{Markup.Escape(LabelSeparator)}[/]";
        var lines = new System.Text.StringBuilder[rows];
        for (int r = 0; r < rows; r++)
        {
            // A later row starts with the label's width of spaces and the bar, so its first title lands in the first title's column.
            lines[r] = new System.Text.StringBuilder(r == 0 && !sub ? $"[{Theme.Label.ToMarkup()}]{Markup.Escape(TextCells.Spaced(label))}[/]" : new string(' ', TextCells.Width(TextCells.Spaced(label))));
            if (titles.Count > 0)
            {
                lines[r].Append(bar);
                if (sub)
                {
                    // The second strip's hook on its first row, blanks as wide under it (2026-10-10).
                    lines[r].Append(r == 0 ? $"[{separator}]{Markup.Escape(SubStripLead)}[/]" : new string(' ', TextCells.Width(SubStripLead)));
                }
            }
        }

        for (int i = 0; i < titles.Count; i++)
        {
            var line = lines[places[i].Row];
            bool isToggle = toggle is not null && i == titles.Count - 1;
            if (i > 0 && places[i - 1].Row == places[i].Row)
            {
                line.Append($"[{separator}]{(isToggle ? ToggleSeparator : TitleSeparator)}[/]");
            }

            // The spaces either side are unstyled (2026-10-07): the underline sits under the title alone; the click still takes them.
            var style = isToggle ? (toggle == true ? Theme.TabLit : Theme.TabIdle) : active.Contains(i) ? lit : Theme.TabIdle;
            line.Append($" [{style.ToMarkup()}]{Markup.Escape(titles[i])}[/]");
            if (BadgeOf(badges, i) is { } badge)
            {
                // Outside the title's style (2026-10-07): never underlined, its own colour on every tab.
                line.Append($"[{Theme.TabBadge.ToMarkup()}]{Markup.Escape(badge)}[/]");
            }

            line.Append(' ');
        }

        var result = new string[rows];
        for (int r = 0; r < rows; r++)
        {
            result[r] = lines[r].ToString();
        }

        return result;
    }

    /// <summary>
    /// Which tab a click at column <paramref name="x"/> of the strip lands on: the title's
    /// highlighted cells (the title and its one space either side), laid out as
    /// <see cref="TabStripMarkup"/> draws them from column 0; null on the label, a gap, past the
    /// end or before the start. Shared with the <see cref="MenuPane"/>. Pure.
    /// </summary>
    public static int? TabAt(string label, IReadOnlyList<string> titles, int x) => TabAt(label, titles, int.MaxValue, x, 0);

    /// <summary>
    /// <see cref="TabAt(string, IReadOnlyList{string}, int)"/> on the strip laid out for
    /// <paramref name="width"/> columns (<see cref="TabStripLayout"/>): the click's
    /// <paramref name="row"/> of the strip as well as its column; null on the indent of a later row. A click on a badge is
    /// its tab's. Pure.
    /// </summary>
    public static int? TabAt(string label, IReadOnlyList<string> titles, int width, int x, int row, IReadOnlyList<string?>? badges = null) =>
        At(TabStripLayout(label, titles, width, badges).Places, titles, badges, x, row);

    /// <summary>Which sub-tab a click at column <paramref name="x"/> of the second strip's <paramref name="row"/> lands on (2026-10-10, <see cref="SubStripLayout"/>); null off every title. Pure.</summary>
    public static int? SubTabAt(string label, IReadOnlyList<string> titles, int width, int x, int row, IReadOnlyList<string?>? badges = null) =>
        At(SubStripLayout(label, titles, width, badges).Places, titles, badges, x, row);

    private static int? At((int Row, int Column)[] places, IReadOnlyList<string> titles, IReadOnlyList<string?>? badges, int x, int row)
    {
        for (int i = 0; i < titles.Count; i++)
        {
            int cells = TitleCells(titles, badges, i);
            if (places[i].Row == row && x >= places[i].Column && x < places[i].Column + cells)
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>
    /// The tab keys, shared with the <see cref="MenuPane"/>: +1 for → or Tab, -1 for ← or
    /// Shift+Tab, null for any other key. On a strip with sub-tabs (2026-10-10) the arrows walk every sub-tab and Tab a whole
    /// tab (<see cref="MenuPane.StepLeaf"/>); elsewhere the two are the same. Pure.
    /// </summary>
    public static int? TabStep(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.RightArrow || (key.Key == ConsoleKey.Tab && (key.Modifiers & ConsoleModifiers.Shift) == 0))
        {
            return 1;
        }

        return key.Key == ConsoleKey.LeftArrow || key.Key == ConsoleKey.Tab ? -1 : null;
    }

    /// <summary>
    /// Which content lines to show: all of them when <paramref name="count"/> fits <paramref name="capacity"/>;
    /// otherwise one fewer than the capacity (the more row takes the last slot), starting at
    /// <paramref name="first"/> clamped so the last page is full. Pure.
    /// </summary>
    public static (int First, int Shown) Viewport(int count, int capacity, int first)
    {
        if (count <= 0 || capacity <= 0)
        {
            return (0, 0);
        }

        if (count <= capacity)
        {
            return (0, count);
        }

        int shown = Math.Max(1, capacity - 1);
        return (Math.Clamp(first, 0, count - shown), shown);
    }

    /// <summary>
    /// Shows the pane under <paramref name="label"/> on <paramref name="tabs"/>[<paramref name="initial"/>]
    /// and reads keys until ESC, the token, or the end of input; on every path the pane is closed, or the one it opened over
    /// handed back as it was (2026-10-14, <see cref="ScreenPane.RestoreOverlay"/>).
    /// </summary>
    public async Task ShowAsync(string label, IReadOnlyList<InfoTab> tabs, int initial, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(tabs);
        if (tabs.Count == 0)
        {
            throw new ArgumentException("At least one tab.", nameof(tabs));
        }

        if (!_pane.Enabled)
        {
            return;
        }

        int active = Math.Clamp(initial, 0, tabs.Count - 1);
        _first = 0;
        _tallestWidth = -1;
        _content = null;
        _contentTab = -1;
        _contentWidth = -1;
        _toggled.Clear();
        _find = "";
        _hits = [];
        _hit = 0;
        _hitsTab = -1;
        _findMoved = false;
        _seek = false;
        _clicks.Reset();
        // The pane under this one, if any (2026-10-14: a command's details over its approval, a menu's): handed back as it was
        // when this closes, its height and all, instead of the input line for a frame.
        var under = _pane.CurrentOverlay;
        _mouse?.Invoke(true);
        try
        {
            Show(label, tabs, active);
            while (true)
            {
                // A command chord (2026-10-01, the user's ask: as everywhere else) is done in place, or the pane backs out as
                // on ESC and the screen runs it (KeySource.ReadPaneInputAsync).
                var input = await _keys.ReadPaneInputAsync(_pane, cancellationToken).ConfigureAwait(false);
                if (input is null)
                {
                    return;
                }

                int next = active;
                int first = _first;
                bool redraw = false;
                bool flip = false;
                if (input is InputEvent.Click click)
                {
                    // A left click on the × at the corner is ESC (2026-09-18), one on a tab's title
                    // that tab, a second off the pane within the interval the close too, one on the more row's ▲ or ▼ a page
                    // (2026-10-14); a right click pastes the clipboard's text into the find (2026-10-14, Ctrl+V's paste too); the
                    // label, a gap, the content and a drag are nothing.
                    if (click.Button == MouseButton.Left && _pane.TryHitClose(click.X, click.Y))
                    {
                        return;
                    }

                    if (click.Button == MouseButton.Left && _pane.TryHitOutside(click.X, click.Y))
                    {
                        // Per part (later on 2026-09-21): two on the same toolbar glyph or hint-row
                        // part, and the dismiss keeps it for the screen's close-or-switch.
                        if (_clicks.Second(_pane.OutsideKey(click.X, click.Y)))
                        {
                            _pane.Dismiss(click.X, click.Y);
                            return;
                        }

                        continue;
                    }

                    _clicks.Reset();
                    var shown = Shown(tabs);
                    var toggle = tabs[active].Toggle;
                    if (click.Button == MouseButton.Right && _clipboard?.Invoke() is { } clip && MenuFilter.Pasted(_find, clip) is { } clipped)
                    {
                        _find = clipped;
                        _findMoved = true;
                        redraw = true;
                    }
                    else if (click.Button == MouseButton.Left && _pane.TryHitOverlay(click.X, click.Y, out int moreAt) && _moreShown && moreAt == _stripRows + 1 + _shown
                        && MenuPane.MoreStep(click.X, "", _first, _shown, _count) is int dir)
                    {
                        // The more row (2026-10-14, the user's ask): its ▲ a page up, its ▼ (or the count after it) a page down, as the keys.
                        first = Math.Clamp(_first + dir * Math.Max(1, _shown), 0, Math.Max(0, _count - _shown));
                    }
                    else if (click.Button == MouseButton.Left && _pane.TryHitOverlay(click.X, click.Y, out int at) && at < _stripRows
                        && TabAt(label, toggle is null ? shown : [.. shown, toggle.Title], _width, click.X, at) is int hit)
                    {
                        // The toggle's title is the one past the tabs' (2026-10-12).
                        if (hit == shown.Length)
                        {
                            flip = true;
                        }
                        else
                        {
                            next = hit;
                        }
                    }
                }
                else if (input is InputEvent.Drag or InputEvent.Release)
                {
                    // A jiggle between the two presses of a double-click keeps the pair, as does the release between them.
                    continue;
                }
                else if (input is InputEvent.Wheel wheel)
                {
                    // A notch away from the user scrolls up, like ↑ three times; clamped like the keys.
                    _clicks.Reset();
                    first = Math.Clamp(_first - wheel.Notches * WheelLines, 0, Math.Max(0, _count - _shown));
                }
                else if (input is InputEvent.Paste paste)
                {
                    // Into the find (2026-10-14, the user's call: dropped until then): its first line, the typed find's cap.
                    _clicks.Reset();
                    if (MenuFilter.Pasted(_find, paste.Text) is not { } pasted)
                    {
                        continue;
                    }

                    _find = pasted;
                    _findMoved = true;
                    redraw = true;
                }
                else if (input is not InputEvent.Key { Info: var k })
                {
                    _clicks.Reset();
                    continue;
                }
                else if (Keys.IsCancel(k) && _find.Length > 0)
                {
                    // The first ESC clears a find (2026-10-07), the view staying where the find took it; the next one closes.
                    _clicks.Reset();
                    _find = "";
                    redraw = true;
                }
                else if (Keys.IsCancel(k) || Keys.IsInterrupt(k))
                {
                    // ESC, and Ctrl+C the same (2026-09-17): the pane backs out.
                    return;
                }
                else if (TabStep(k) is int step)
                {
                    _clicks.Reset();
                    next = (active + step + tabs.Count) % tabs.Count;
                }
                else if (_find.Length == 0 && tabs[active].Toggle is { } keyed && FindChar(k) is char pressed
                    && char.ToLowerInvariant(pressed) == char.ToLowerInvariant(keyed.Key))
                {
                    // The toggle's key wins while no find is typed (2026-10-12), as a menu button's does; once one is, it goes on the find.
                    _clicks.Reset();
                    flip = true;
                }
                else if (FindStep(k) is int along)
                {
                    // Enter or F3 the next match, with Shift the one before, wrapping; nothing until something is found.
                    _clicks.Reset();
                    if (_hits.Count > 0)
                    {
                        _hit = (_hit + along + _hits.Count) % _hits.Count;
                        _seek = true;
                        redraw = true;
                    }
                }
                else if (k.Key == ConsoleKey.Backspace)
                {
                    _clicks.Reset();
                    if (_find.Length > 0)
                    {
                        _find = _find[..^1];
                        _findMoved = true;
                        redraw = true;
                    }
                }
                else if (FindChar(k) is char typed)
                {
                    // Typed: the find grows (2026-10-07; every other key was swallowed before), the keys already waiting with it.
                    _clicks.Reset();
                    var find = new System.Text.StringBuilder(_find).Append(typed);
                    while (_keys.TakeQueued(e => e is InputEvent.Key { Info: var q } && FindChar(q) is not null) is InputEvent.Key { Info: var more })
                    {
                        find.Append(more.KeyChar);
                    }

                    string grown = find.ToString();
                    _find = grown.Length > FindMaxLength ? grown[..FindMaxLength] : grown;
                    _findMoved = true;
                    redraw = true;
                }
                else
                {
                    _clicks.Reset();
                    first = k.Key switch
                    {
                        ConsoleKey.End => int.MaxValue,
                        ConsoleKey.Home => 0,
                        _ => _first + (ScrollStep(k) ?? 0),
                    };
                    first = Math.Clamp(first, 0, Math.Max(0, _count - _shown));

                    // A held arrow's queued presses (2026-10-03, the user's ask): folded into this one move and drawn once, so
                    // the pane stops where the key was let go instead of working off a backlog of redraws. Only bare keys are
                    // taken: a chord stays for the pane read, which knows the command chords.
                    if (ScrollStep(k) is not null)
                    {
                        while (_keys.TakeQueued(e => e is InputEvent.Key { Info: var q } && q.Modifiers == 0 && ScrollStep(q) is not null) is InputEvent.Key { Info: var more })
                        {
                            first = Math.Clamp(first + ScrollStep(more)!.Value, 0, Math.Max(0, _count - _shown));
                        }
                    }
                }

                if (flip)
                {
                    // The toggle swaps the tab's content (2026-10-12): from the top, as a tab switch.
                    if (!_toggled.Remove(active))
                    {
                        _toggled.Add(active);
                    }

                    _first = 0;
                    _findMoved = _find.Length > 0;
                }
                else if (next != active)
                {
                    active = next;
                    _first = 0;
                    _findMoved = _find.Length > 0;
                }
                else if (first != _first)
                {
                    _first = first;
                }
                else if (!redraw)
                {
                    continue;
                }

                Show(label, tabs, active);
            }
        }
        catch (InvalidOperationException)
        {
            // No keyboard: the read ran dry, the pane closes.
        }
        finally
        {
            _pane.RestoreOverlay(under);
            _mouse?.Invoke(false);
        }
    }

    /// <summary>The lines ↑, ↓, PgUp and PgDn move the view; null for every other key.</summary>
    private int? ScrollStep(ConsoleKeyInfo key) => key.Key switch
    {
        ConsoleKey.DownArrow => 1,
        ConsoleKey.UpArrow => -1,
        ConsoleKey.PageDown => Math.Max(1, _shown),
        ConsoleKey.PageUp => -Math.Max(1, _shown),
        _ => null,
    };

    /// <summary>
    /// The key the finds' hints name for a step back (<see cref="FindHint"/>, <see cref="TranscriptFind.Hint"/>): Shift+Enter,
    /// but a Mac terminal sends Shift+Enter as a plain CR, so there it is Shift+F3, which <see cref="FindStep"/> reads too
    /// (2026-10-07, the user's pick after the docs pass; no new key). Pure, so both spellings are pinned on any OS.
    /// </summary>
    internal static string FindBackKey(bool mac) => mac ? "Shift+F3" : "Shift+Enter";

    /// <summary><see cref="FindBackKey"/> for this machine.</summary>
    internal static string FindBack => FindBackKey(OperatingSystem.IsMacOS());

    /// <summary>The find's step: +1 for Enter or F3, −1 with Shift; null for every other key (Ctrl+Enter, a line break elsewhere, among them).</summary>
    internal static int? FindStep(ConsoleKeyInfo key) =>
        (key.Key is ConsoleKey.Enter or ConsoleKey.F3) && (key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt)) == 0
            ? (key.Modifiers & ConsoleModifiers.Shift) != 0 ? -1 : 1
            : null;

    /// <summary>
    /// The character a key adds to the find: a printable one, typed bare or with Shift (Ctrl+Alt too, which is AltGr on some
    /// keyboards); null for a chord and for the keys with jobs of their own (Tab, Enter, Backspace).
    /// </summary>
    internal static char? FindChar(ConsoleKeyInfo key)
    {
        var chord = key.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt);
        bool bare = chord == 0 || chord == (ConsoleModifiers.Control | ConsoleModifiers.Alt);
        return bare && key.KeyChar != '\0' && !char.IsControl(key.KeyChar) ? key.KeyChar : null;
    }

    private static string[] Titles(IReadOnlyList<InfoTab> tabs)
    {
        var titles = new string[tabs.Count];
        for (int i = 0; i < tabs.Count; i++)
        {
            titles[i] = tabs[i].Title;
        }

        return titles;
    }

    /// <summary>The titles the strip draws: none for a pane of one tab, whose label says it all (2026-10-04).</summary>
    private static string[] Shown(IReadOnlyList<InfoTab> tabs) => tabs.Count == 1 ? [] : Titles(tabs);

    private void Show(string label, IReadOnlyList<InfoTab> tabs, int active)
    {
        var titles = Shown(tabs);
        int width = Math.Max(1, _pane.Profile.Width);
        int height = _pane.Profile.Height > 0 ? _pane.LayoutHeight : DefaultHeight;   // less the toolbar's row (2026-09-21)
        var toggle = tabs[active].Toggle;
        bool toggled = toggle is not null && _toggled.Contains(active);
        var strip = toggle is null ? TabStripRows(label, titles, active, width) : TabStripRows(label, titles, active, width, (toggle.Title, toggled));
        _width = width;
        _stripRows = strip.Count;
        int capacity = _pane.MenuContentRows(height, 0) - HeaderRows - (_stripRows - 1);
        if (_content is null || _contentTab != active || _contentWidth != width || _contentToggled != toggled)
        {
            _content = ScreenPane.RenderLines(toggled ? toggle!.Content() : tabs[active].Content(), _pane, width);
            _contentTab = active;
            _contentWidth = width;
            _contentToggled = toggled;
        }

        var content = _content;
        _count = content.Count;
        (_first, _shown) = Viewport(_count, capacity, _first);
        if (_find.Length == 0)
        {
            _hits = [];
        }
        else if (_findMoved || _hitsFind != _find || _hitsTab != active || _hitsWidth != width)
        {
            // A new text or tab starts at the first match from the view's top down (the first of all when none is below);
            // a new width keeps the match it was on as near as the count allows.
            _hits = TextFind.Matches(content, _find);
            if (_findMoved)
            {
                int below = _hits.FindIndex(hit => hit.Line >= _first);
                _hit = below >= 0 ? below : 0;
                _seek = true;
            }
            else
            {
                _hit = Math.Clamp(_hit, 0, Math.Max(0, _hits.Count - 1));
            }

            (_hitsFind, _hitsTab, _hitsWidth) = (_find, active, width);
        }

        _findMoved = false;
        if (_seek && _hits.Count > 0 && (_hits[_hit].Line < _first || _hits[_hit].Line >= _first + _shown))
        {
            // Into view a third of the way down, so the lines before it read as its context.
            (_first, _shown) = Viewport(_count, capacity, _hits[_hit].Line - _shown / 3);
        }

        _seek = false;
        if (_tallestWidth != width)
        {
            // Every other tab built and laid out once a visit (the shown one is above), and again only when the width
            // moves: the scroll keys redraw on every press.
            _tallest = _count;
            for (int i = 0; i < tabs.Count; i++)
            {
                if (i != active)
                {
                    _tallest = Math.Max(_tallest, ScreenPane.RenderLines(tabs[i].Content(), _pane, width).Count);
                }
            }

            _tallestWidth = width;
        }

        bool more = _shown < _count;
        _moreShown = more;
        int pad = Math.Min(Math.Max(_tallest, _count), Math.Max(0, capacity)) - (_shown + (more ? 1 : 0));

        // The spacer is a space, not an empty Text: Rows adds a line break only after a child that
        // rendered something, so an empty one would collapse the blank line.
        var lines = new List<IRenderable>(HeaderRows + _stripRows + _shown + Math.Max(0, pad));
        foreach (var row in strip)
        {
            lines.Add(new Markup(row).Overflow(Overflow.Ellipsis));
        }

        lines.Add(new Text(" "));
        int h = _hits.FindIndex(hit => hit.Line >= _first);
        for (int i = 0; i < _shown; i++)
        {
            int at = _first + i;
            var starts = new List<int>();
            int current = -1;
            for (; h >= 0 && h < _hits.Count && _hits[h].Line == at; h++)
            {
                starts.Add(_hits[h].Start);
                if (h == _hit)
                {
                    current = _hits[h].Start;
                }
            }

            lines.Add(new SegmentLines(starts.Count == 0 ? content[at] : TextFind.Mark(content[at], starts, _find.Length, current)));
        }

        if (more)
        {
            lines.Add(new Markup(Theme.DimMarkup(MenuPane.MoreHint(_first, _shown, content.Count))));
        }

        for (int i = 0; i < pad; i++)
        {
            lines.Add(new Text(" "));
        }

        string hint = _find.Length > 0 ? FindHint(_find, _hit, _hits.Count)
            : toggle is not null ? ToggleHint(toggle.Key, toggle.Word, tabs: tabs.Count > 1)
            : tabs.Count == 1 ? SingleTabHintText : HintText;
        _pane.ShowOverlay(new Rows(lines), hint, close: true);
    }
}
