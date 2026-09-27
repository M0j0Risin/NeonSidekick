using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.UI;

/// <summary>Where a menu's notices go: the transcript, or the status line of an open <see cref="MenuPane"/>.</summary>
public interface INoticeSink
{
    void Notice(string text);

    void Warning(string text);

    void Error(string text);
}

/// <summary>
/// One tab of a tabbed <see cref="MenuPage"/>: its strip title and its rows as markup (escaped by
/// the caller); with a <see cref="Caption"/> and a <see cref="Hint"/> of its own when the tabs
/// differ (the question pane: the question text and the keys of its kind), null for the page's.
/// </summary>
public sealed record MenuTab(string Title, IReadOnlyList<string> Rows)
{
    /// <summary>The tab's <see cref="MenuPage.Caption"/>; null for none.</summary>
    public string? Caption { get; init; }

    /// <summary>The tab's hint row; null for the page's.</summary>
    public string? Hint { get; init; }
}

/// <summary>
/// A button on a one-list page's title row (2026-09-21, the queue pane's <c>clear all</c>): drawn
/// as a dim tab nobody is on, the <see cref="FolderPane"/>'s shape; a click on it, or
/// <see cref="Key"/> typed (null for none), returns a <see cref="MenuPick"/> with
/// <see cref="MenuPick.Button"/> set.
/// </summary>
public sealed record MenuButton(string Title, char? Key);

/// <summary>
/// One level of a menu in the pane: a title, the rows as markup (escaped by the caller), and the
/// hint row's text. A page built by <see cref="Tabbed"/> carries <see cref="Tabs"/>: the title row
/// becomes the <see cref="InfoPane.TabStripMarkup"/> strip and <see cref="Rows"/> are the active
/// tab's (<see cref="Tab"/>), so everything below the strip is the flat page's.
/// </summary>
public sealed record MenuPage(string Title, IReadOnlyList<string> Rows, string Hint)
{
    /// <summary>The tabs when the page has them; null for a one-list page.</summary>
    public IReadOnlyList<MenuTab>? Tabs { get; init; }

    /// <summary>
    /// The buttons on a one-list page's title row (2026-09-21), drawn after the title as the tab
    /// strip draws its titles, none active; null or empty for none (every page but the queue's).
    /// Ignored on a tabbed page: its strip is the tabs'.
    /// </summary>
    public IReadOnlyList<MenuButton>? Buttons { get; init; }

    /// <summary>The active tab's index into <see cref="Tabs"/>; 0 on a one-list page.</summary>
    public int Tab { get; init; }

    /// <summary>
    /// Plain text drawn under the title or the strip, above the status line (a question over its
    /// answers): escaped by the pane, word-wrapped to at most <see cref="MenuPane.CaptionMaxRows"/>
    /// rows with the last cut by an ellipsis. Null for none (every page but the question pane).
    /// </summary>
    public string? Caption { get; init; }

    /// <summary>
    /// Space returns the cursor's row as a <see cref="MenuPick"/> with <see cref="MenuPick.Toggle"/>
    /// set, the status cleared as Enter clears it (a checkbox list); false (every other page):
    /// Space is swallowed.
    /// </summary>
    public bool SpaceToggles { get; init; }

    /// <summary>
    /// The row the cursor lands on after a switch to each tab, by tab index (clamped to the tab's
    /// rows); null (every page but the question pane): the first row.
    /// </summary>
    public IReadOnlyList<int>? TabCursors { get; init; }

    /// <summary>
    /// Typed characters (lowercase) that move the cursor to a row of <see cref="Rows"/>, exactly as
    /// the arrows would — Enter still picks; null for none (every page but the yes/no one).
    /// </summary>
    public IReadOnlyDictionary<char, int>? Hotkeys { get; init; }

    /// <summary>
    /// The row Backspace moves the cursor to, exactly as the arrows would — Enter still picks; the
    /// <c>(none)</c> row of a picker that has one. Null for none (every other page).
    /// </summary>
    public int? BackspaceRow { get; init; }

    /// <summary>The hint a tab without one of its own shows, set by <see cref="Tabbed"/>; null on a one-list page (<see cref="Hint"/> is the one).</summary>
    public string? BaseHint { get; init; }

    /// <summary>
    /// A page over <paramref name="tabs"/> showing <paramref name="tab"/>: the strip labelled
    /// <paramref name="title"/>, that tab's rows, its caption, and its hint when it has one else
    /// <paramref name="hint"/> (kept as <see cref="BaseHint"/> for the tabs without).
    /// </summary>
    public static MenuPage Tabbed(string title, IReadOnlyList<MenuTab> tabs, int tab, string hint)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        if (tabs.Count == 0)
        {
            throw new ArgumentException("A tabbed page needs at least one tab.", nameof(tabs));
        }

        tab = Math.Clamp(tab, 0, tabs.Count - 1);
        return new MenuPage(title, tabs[tab].Rows, tabs[tab].Hint ?? hint) { Tabs = tabs, Tab = tab, Caption = tabs[tab].Caption, BaseHint = hint };
    }
}

/// <summary>
/// What <see cref="MenuPane.PickAsync"/> returns on Enter — or on Space over a page whose
/// <see cref="MenuPage.SpaceToggles"/> (<see cref="Toggle"/> true): the tab shown (0 on a one-list
/// page) and the cursor's row within it. A press on one of the page's <see cref="MenuPage.Buttons"/>
/// (2026-09-21) is <see cref="Button"/> at its index, the row still the cursor's; −1 otherwise.
/// </summary>
public readonly record struct MenuPick(int Tab, int Row, bool Toggle = false, int Button = -1);

/// <summary>
/// A menu in the bottom pane: the <see cref="InfoPane"/> shape for a list — a title, a status line
/// for what the last action did, the rows with the cursor's one highlighted, a hint row — over the
/// <see cref="ScreenPane"/> overlay, the keys through the <see cref="KeySource"/>. <see cref="PickAsync"/>
/// reads a level and returns the pick (the tab and the row within it; null for ESC) with the pane still open, so a
/// caller can show the next level or the same one again; <see cref="EditAsync"/> shows a page with
/// the input row under it (an overlay with a slot) and runs the <see cref="InputLine"/> there;
/// <see cref="Close"/> ends the visit. As an <see cref="INoticeSink"/> it puts a menu's notices on
/// the status line while it is open — drawn with the next page, which always follows (the settings
/// loop shows the list again, a typed fallback its slot), so a save never shows on a stale row.
///
/// <para>The wheel over the list moves the cursor a row a notch (never wrapping); over the transcript
/// above the pane it scrolls the transcript (2026-09-26), the cursor left where it was.</para>
///
/// <para>Keys: Up/Down move (wrapping), Home/End, PageUp/PageDown by a page, Enter picks (and
/// clears the status — the action's result fills it), ESC backs out; on a tabbed page ←/→ and
/// Tab/Shift+Tab switch tabs (wrapping, the cursor back on the first row, the status dropped —
/// a switch ends the action that wrote it, 2026-09-20, the user's ask) exactly as the info pane's do; a character in the page's <see cref="MenuPage.Hotkeys"/> moves the cursor
/// to its row as the arrows would (Enter still picks; the yes/no pane's <c>y</c> / <c>n</c>), and
/// Backspace to the page's <see cref="MenuPage.BackspaceRow"/> the same way (a picker's <c>(none)</c> row);
/// on a page whose <see cref="MenuPage.SpaceToggles"/>, Space returns the row like Enter with
/// <see cref="MenuPick.Toggle"/> set (a checkbox list); a switch to a tab lands on its
/// <see cref="MenuPage.TabCursors"/> row when the page has them, and a tab's own caption and hint
/// (<see cref="MenuTab.Caption"/>, <see cref="MenuTab.Hint"/>) follow it;
/// every other key is swallowed.
/// A left click on a row moves the cursor there, exactly as the arrows would, and a second on the
/// same row within <see cref="DoubleClick.Interval"/> picks it exactly as Enter would (2026-09-18);
/// one on a tab's title switches to that tab exactly as the tab keys would, a double-click there
/// switching once; one on a one-list page's button (<see cref="MenuPage.Buttons"/>, 2026-09-21) is
/// the button's pick, as its key is (the status cleared as Enter clears it: the press's result
/// fills it); one on the <see cref="ScreenPane.CloseGlyph"/> at the first row's right edge is
/// ESC (null); two left clicks off the pane — the transcript, a rule, the hint row — within the
/// interval close every level at once (<see cref="ScreenPane.Dismiss"/>, later on 2026-09-18: the
/// hosts above find <see cref="ScreenPane.Dismissed"/> and back out without a draw), where the × is
/// one level back; a single click there, a right click and a drag do nothing. The pane takes the mouse for the
/// list (the injected hook, the same one the input line uses for a draft) on every
/// <see cref="PickAsync"/> and hands it back in <see cref="Close"/>, so the terminal's own
/// selection needs Shift while a menu is open, as it does over a draft.
/// A row longer than the width is cut at the edge with an ellipsis, never wrapped
/// (<see cref="FittedMarkup"/>, 2026-09-18), so the viewport's count holds. A list longer than the
/// room the window leaves scrolls behind a viewport, the last row saying so
/// (<see cref="MoreHint"/>). Without the pane (no geometry) there is nothing to draw: the callers
/// branch to a Spectre prompt, and a call here throws.</para>
/// </summary>
public sealed class MenuPane : INoticeSink
{
    /// <summary>The dim last row when the list is cut by the window. Pinned.</summary>
    public const string MoreHint = "↑/↓ for more";

    /// <summary>What the cursor's row starts with; every other row gets the same width of spaces. Pinned.</summary>
    public const string Pointer = "▸ ";
    public const string NoPointer = "  ";

    /// <summary>The <see cref="DoubleClick"/> key of a click off the pane (2026-09-18): no list row, and never the pairing's own −1.</summary>
    public const int OutsideRow = -2;

    /// <summary>The most rows a page's <see cref="MenuPage.Caption"/> takes; the last is cut with an ellipsis. Pinned.</summary>
    public const int CaptionMaxRows = 3;

    /// <summary>The window height assumed when the console reports none (the mention list sizes by it too).</summary>
    public const int DefaultHeight = 24;

    private readonly ScreenPane _pane;
    private readonly KeySource _keys;
    private readonly Action<bool>? _mouse;
    private readonly DoubleClick _clicks;
    private readonly List<(NoticeKind Kind, string Text)> _status = new();
    private MenuPage? _page;
    private int _cursor;
    private int _first;
    private int _shown;
    private int _captionRows;
    private int _inputRows;
    private bool _open;

    /// <param name="mouse">Takes (true) or hands back (false) the console's mouse; null when the screen has none to take.</param>
    public MenuPane(ScreenPane pane, KeySource keys, Action<bool>? mouse = null)
    {
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _mouse = mouse;
        _clicks = new DoubleClick(pane.Time);
    }

    /// <summary>The kinds of line the status shows, in the transcript's colours.</summary>
    public enum NoticeKind
    {
        Notice,
        Warning,
        Error,
    }

    /// <summary>The pane can host a menu: the screen has the bottom pane.</summary>
    public bool Enabled => _pane.Enabled;

    /// <summary>A visit is in progress: between the first <see cref="PickAsync"/> and <see cref="Close"/>.</summary>
    public bool IsOpen => _open;

    /// <summary>The status lines shown under the title (tests).</summary>
    public IReadOnlyList<(NoticeKind Kind, string Text)> Status => _status;

    // ── Pinned statics ──────────────────────────────────────────────────────

    /// <summary>The title row: the label style, like the info pane's strip label. Escaped.</summary>
    public static string TitleMarkup(string title) =>
        $"[{Theme.Label.ToMarkup()}]{Markup.Escape(title)}[/]";

    /// <summary>A row: the pointer and the menu highlight on the cursor's row, an indent on the others. <paramref name="row"/> is markup already.</summary>
    public static string RowMarkup(string row, bool active) =>
        active ? $"[{Theme.MenuHighlight.ToMarkup()}]{Pointer}{row}[/]" : NoPointer + row;

    /// <summary>A status line: the transcript's glyph and colour for the kind. Escaped.</summary>
    public static string StatusMarkup(NoticeKind kind, string text) => kind switch
    {
        NoticeKind.Warning => TranscriptRenderer.WarningMarkup(text),
        NoticeKind.Error => TranscriptRenderer.ErrorMarkup(text),
        _ => TranscriptRenderer.NoticeMarkup(text),
    };

    /// <summary>
    /// Which rows to show: all of them when <paramref name="count"/> fits <paramref name="capacity"/>;
    /// otherwise one fewer than the capacity (the <see cref="MoreHint"/> row takes the last slot),
    /// starting at <paramref name="first"/> moved just far enough to keep <paramref name="cursor"/>
    /// in view. Pure.
    /// </summary>
    public static (int First, int Shown) Viewport(int count, int cursor, int capacity, int first)
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
        cursor = Math.Clamp(cursor, 0, count - 1);
        if (cursor < first)
        {
            first = cursor;
        }
        else if (cursor >= first + shown)
        {
            first = cursor - shown + 1;
        }

        return (Math.Clamp(first, 0, count - shown), shown);
    }

    /// <summary>
    /// A <see cref="MenuPage.Caption"/> laid out for a row of <paramref name="width"/> cells: the
    /// words (any whitespace between them, a line break included, reads as one space) wrapped
    /// greedily by <see cref="TextCells"/>, a word wider than the row cut with an ellipsis, and at
    /// most <paramref name="maxRows"/> rows — what would not fit joins the last row, cut with an
    /// ellipsis. Empty for a blank caption or no room. Pure.
    /// </summary>
    public static IReadOnlyList<string> CaptionRows(string caption, int width, int maxRows)
    {
        ArgumentNullException.ThrowIfNull(caption);
        var rows = new List<string>();
        if (width <= 0 || maxRows <= 0)
        {
            return rows;
        }

        var line = new System.Text.StringBuilder();
        int used = 0;
        foreach (var word in caption.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            int cells = TextCells.Width(word);
            if (line.Length > 0 && used + 1 + cells <= width)
            {
                line.Append(' ').Append(word);
                used += 1 + cells;
                continue;
            }

            if (line.Length > 0)
            {
                rows.Add(line.ToString());
                line.Clear();
            }

            string fitted = cells > width ? ScreenPane.Fit(word, width) : word;
            line.Append(fitted);
            used = TextCells.Width(fitted);
        }

        if (line.Length > 0)
        {
            rows.Add(line.ToString());
        }

        if (rows.Count > maxRows)
        {
            string rest = string.Join(' ', rows.Skip(maxRows - 1));
            rows.RemoveRange(maxRows - 1, rows.Count - (maxRows - 1));
            rows.Add(ScreenPane.Fit(rest, width));
        }

        return rows;
    }

    // ── The visit ───────────────────────────────────────────────────────────

    /// <summary>
    /// Shows <paramref name="page"/> with the cursor on row <paramref name="cursor"/> and reads
    /// keys until Enter (the tab shown and the row's index within it) or ESC, the token or no
    /// keyboard (null). The pane stays open either way: the caller shows the next page or calls
    /// <see cref="Close"/>. The mouse is taken here every time (an <see cref="EditAsync"/> in
    /// between hands it back with its line). <paramref name="highlighted"/>, when given, hears
    /// the cursor's new row after every move that changed it — the arrow, Home / End and page
    /// keys, a click on another shown row — never the opening row, Enter, ESC or a tab switch.
    /// </summary>
    public async Task<MenuPick?> PickAsync(MenuPage page, int cursor, CancellationToken cancellationToken, Action<int>? highlighted = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        Require();
        if (_pane.Dismissed)
        {
            // A pane dismissed by a double-click off it: every host on the way up gets null without a draw, until the top one's Close clears it.
            return null;
        }

        if (_page is null || _page.Tab != page.Tab)
        {
            // Another tab (a host that moved on) starts at the top of its list; the same one re-shown keeps its viewport.
            _first = 0;
        }

        _page = page;
        _open = true;
        int count = page.Rows.Count;
        _cursor = count == 0 ? 0 : Math.Clamp(cursor, 0, count - 1);
        _inputRows = 0;
        _clicks.Reset();
        _mouse?.Invoke(true);
        Show();
        try
        {
            while (true)
            {
                page = _page!;
                var input = await _keys.ReadInputAsync(cancellationToken).ConfigureAwait(false);
                if (input is InputEvent.Click click)
                {
                    // A left click on a shown row is the arrow keys' move, a second on the same row
                    // within DoubleClick.Interval is Enter (2026-09-18), one on a tab's title the
                    // tab keys'; the label, a gap in the strip, the status, the more row, a slot-less
                    // miss, a right click and a drag are nothing — but two left clicks off the pane
                    // (the transcript, a rule, the hint row) within the interval dismiss every level.
                    if (click.Button != MouseButton.Left)
                    {
                        _clicks.Reset();
                        continue;
                    }

                    if (!_pane.TryHitOverlay(click.X, click.Y, out int at))
                    {
                        if (!_pane.TryHitOutside(click.X, click.Y))
                        {
                            _clicks.Reset();
                            continue;
                        }

                        if (_clicks.Second(_pane.OutsideKey(click.X, click.Y)))
                        {
                            // The whole stack, not one level: the hosts above read ScreenPane.Dismissed.
                            // The click goes with it (later on 2026-09-21): the screen reads which
                            // toolbar glyph or hint-row part it was, and closes or switches panes.
                            _pane.Dismiss(click.X, click.Y);
                            return null;
                        }

                        continue;
                    }

                    if (_pane.TryHitClose(click.X, click.Y))
                    {
                        // The × at the corner is the ESC key (2026-09-18): nothing picked, one level back.
                        _clicks.Reset();
                        return null;
                    }

                    if (at == 0)
                    {
                        _clicks.Reset();
                        if (page.Tabs is { Count: > 1 } strip && InfoPane.TabAt(page.Title, Titles(strip), click.X) is int hit && hit != page.Tab)
                        {
                            count = SwitchTab(page, strip, hit);
                        }
                        else if (page.Tabs is null && page.Buttons is { Count: > 0 } buttons && InfoPane.TabAt(page.Title, Titles(buttons), click.X) is int button)
                        {
                            _status.Clear();
                            return new MenuPick(page.Tab, _cursor, Button: button);
                        }
                    }
                    else if (at - Header is int i && i >= 0 && i < _shown)
                    {
                        int hitRow = _first + i;
                        MoveTo(hitRow);
                        if (count > 0 && _clicks.Second(hitRow))
                        {
                            _status.Clear();
                            return new MenuPick(page.Tab, _cursor);
                        }
                    }
                    else
                    {
                        _clicks.Reset();
                    }

                    continue;
                }

                if (input is InputEvent.Drag or InputEvent.Paste)
                {
                    // A drag is the line's business (a jiggle between the two presses of a
                    // double-click keeps the pair), a paste has no slot to land in here.
                    continue;
                }

                // Anything but a click or a drag ends a pair: the next click is a first again.
                _clicks.Reset();
                if (input is InputEvent.Wheel wheel)
                {
                    if (_pane.TryHitOutside(wheel.X, wheel.Y))
                    {
                        // Off the pane (2026-09-26, the user's ask: scroll back through the chat while the plan's
                        // approval waits): the transcript scrolls as it does at the idle line and under a reply;
                        // the list keeps its cursor.
                        _pane.ScrollWheel(wheel.Notches);
                        continue;
                    }

                    // On the pane (or where the console cannot say): the arrow key's move — a row per
                    // notch, away from the user is up — that never wraps: the list's end is where the wheel stops.
                    if (count > 0)
                    {
                        MoveTo(Math.Clamp(_cursor - wheel.Notches, 0, count - 1));
                    }

                    continue;
                }

                if ((input as InputEvent.Key)?.Info is not { } k || Keys.IsCancel(k) || Keys.IsInterrupt(k))
                {
                    // ESC, and Ctrl+C the same (2026-09-17): nothing picked, the menu backs out.
                    return null;
                }

                if (k.Key == ConsoleKey.Enter)
                {
                    if (count == 0)
                    {
                        continue;
                    }

                    _status.Clear();
                    return new MenuPick(page.Tab, _cursor);
                }

                if (page.SpaceToggles && k.KeyChar == ' ')
                {
                    // The character, not ConsoleKey.Spacebar: a scripted key carries the one without the other.
                    if (count == 0)
                    {
                        continue;
                    }

                    _status.Clear();
                    return new MenuPick(page.Tab, _cursor, Toggle: true);
                }

                if (page.Tabs is { Count: > 1 } tabs && InfoPane.TabStep(k) is int step)
                {
                    count = SwitchTab(page, tabs, (page.Tab + step + tabs.Count) % tabs.Count);
                    continue;
                }

                if (page.Tabs is null && page.Buttons is { Count: > 0 } keyed && k.KeyChar is not '\0' && !char.IsControl(k.KeyChar)
                    && ButtonFor(keyed, k.KeyChar) is int pressed)
                {
                    _status.Clear();
                    return new MenuPick(page.Tab, _cursor, Button: pressed);
                }

                int next = _cursor;
                if (page.Hotkeys is { } hotkeys && k.KeyChar is not '\0' && !char.IsControl(k.KeyChar)
                    && hotkeys.TryGetValue(char.ToLowerInvariant(k.KeyChar), out int row) && row >= 0 && row < count)
                {
                    next = row;
                }

                switch (k.Key)
                {
                    case ConsoleKey.DownArrow: next = count == 0 ? 0 : (_cursor + 1) % count; break;
                    case ConsoleKey.UpArrow: next = count == 0 ? 0 : (_cursor + count - 1) % count; break;
                    case ConsoleKey.Home: next = 0; break;
                    case ConsoleKey.End: next = Math.Max(0, count - 1); break;
                    case ConsoleKey.PageDown: next = Math.Min(Math.Max(0, count - 1), _cursor + Math.Max(1, _shown)); break;
                    case ConsoleKey.PageUp: next = Math.Max(0, _cursor - Math.Max(1, _shown)); break;
                    case ConsoleKey.Backspace when page.BackspaceRow is { } none && none >= 0 && none < count: next = none; break;
                }

                MoveTo(next);
            }
        }
        catch (InvalidOperationException)
        {
            // No keyboard: the read ran dry.
            return null;
        }

        // The cursor onto another row: redrawn and reported; the same row is nothing.
        void MoveTo(int next)
        {
            if (next == _cursor)
            {
                return;
            }

            _cursor = next;
            Show();
            highlighted?.Invoke(_cursor);
        }
    }

    /// <summary>
    /// Shows <paramref name="page"/> with row <paramref name="highlighted"/> marked and the input
    /// row under it, then reads a line there through <paramref name="input"/>: pre-filled with
    /// <paramref name="initial"/>, one ESC cancels (the caller keeps the saved value), nothing it
    /// submits reaches the transcript. The pane stays open with the slot emptied.
    /// </summary>
    public async Task<InputResult> EditAsync(MenuPage page, int highlighted, InputLine input, string initial, bool allowEmpty, CancellationToken cancellationToken, bool mask = false)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(initial);
        Require();
        _page = page;
        _open = true;
        _cursor = page.Rows.Count == 0 ? 0 : Math.Clamp(highlighted, 0, page.Rows.Count - 1);
        // The slot's rows for the pre-filled value, so the list leaves room for them from the start.
        _inputRows = Math.Clamp(InputLayout.Wrap(initial, initial.Length, InputLine.AvailableCells(Width)).Rows.Count, 1, ScreenPane.MaxInputRows(Height));
        Show();
        try
        {
            return await input.ReadAsync(initial, remember: false, allowEmpty: allowEmpty, cancellationToken: cancellationToken, escapeCancels: true, mask: mask).ConfigureAwait(false);
        }
        finally
        {
            _inputRows = 0;
        }
    }

    /// <summary>
    /// A switch to <paramref name="tab"/>: the other tab's rows, caption and hint (the page's hint
    /// when the tab has none), the cursor on the page's <see cref="MenuPage.TabCursors"/> row for
    /// it else the first, the status dropped (what the last action did belongs to the tab it ran on)
    /// and every other page setting kept. Returns the row count shown.
    /// </summary>
    private int SwitchTab(MenuPage page, IReadOnlyList<MenuTab> tabs, int tab)
    {
        // A `with`, never Tabbed() again: the hotkeys, the toggle and the cursors would go with a rebuild.
        _page = page with { Rows = tabs[tab].Rows, Tab = tab, Caption = tabs[tab].Caption, Hint = tabs[tab].Hint ?? page.BaseHint ?? page.Hint };
        int count = _page.Rows.Count;
        _cursor = page.TabCursors is { } cursors && tab < cursors.Count && count > 0 ? Math.Clamp(cursors[tab], 0, count - 1) : 0;
        _first = 0;
        _status.Clear();
        Show();
        return count;
    }

    private static List<string> Titles(IReadOnlyList<MenuTab> tabs) => tabs.Select(t => t.Title).ToList();

    private static List<string> Titles(IReadOnlyList<MenuButton> buttons) => buttons.Select(b => b.Title).ToList();

    /// <summary>The index of the button whose key is <paramref name="key"/> (ignoring case), null for none. Pure.</summary>
    public static int? ButtonFor(IReadOnlyList<MenuButton> buttons, char key)
    {
        ArgumentNullException.ThrowIfNull(buttons);
        for (int i = 0; i < buttons.Count; i++)
        {
            if (buttons[i].Key is { } k && char.ToLowerInvariant(k) == char.ToLowerInvariant(key))
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>The overlay closed and the status forgotten; nothing when no visit is open.</summary>
    public void Close()
    {
        if (!_open)
        {
            return;
        }

        _open = false;
        _page = null;
        _status.Clear();
        _first = 0;
        _captionRows = 0;
        _inputRows = 0;
        _pane.CloseOverlay();
        _mouse?.Invoke(false);
    }

    // ── INoticeSink ─────────────────────────────────────────────────────────

    public void Notice(string text) => Add(NoticeKind.Notice, text);

    public void Warning(string text) => Add(NoticeKind.Warning, text);

    public void Error(string text) => Add(NoticeKind.Error, text);

    private void Add(NoticeKind kind, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _status.Add((kind, text));
    }

    // ── Drawing ─────────────────────────────────────────────────────────────

    private void Require()
    {
        if (!Enabled)
        {
            throw new InvalidOperationException("The menu pane needs the bottom pane; show a prompt instead.");
        }
    }

    private int Width => Math.Max(1, _pane.Profile.Width);

    /// <summary>The window less the toolbar's row (<see cref="ScreenPane.LayoutHeight"/>, 2026-09-21): what the pane's caps are counted over.</summary>
    private int Height => _pane.Profile.Height > 0 ? _pane.LayoutHeight : DefaultHeight;

    /// <summary>The overlay rows above the first list row: the title (or the tab strip), the caption's rows, then the status lines or the one spacer.</summary>
    private int Header => 1 + _captionRows + Math.Max(1, _status.Count);

    /// <summary>The title row of <paramref name="page"/>: the tab strip on a tabbed page, the title with its buttons as a strip nobody is on (2026-09-21), else the title alone. Pinned.</summary>
    public static string TopMarkup(MenuPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return page.Tabs is { } tabs ? InfoPane.TabStripMarkup(page.Title, Titles(tabs), page.Tab)
            : page.Buttons is { Count: > 0 } buttons ? InfoPane.TabStripMarkup(page.Title, Titles(buttons), -1)
            : TitleMarkup(page.Title);
    }

    /// <summary>The page laid out for the window: the title or the tab strip, the caption, the status (or a spacer row), the rows in view, the more row.</summary>
    private void Show()
    {
        var page = _page!;
        IReadOnlyList<string> caption = page.Caption is { } captionText ? CaptionRows(captionText, Width, CaptionMaxRows) : [];
        _captionRows = caption.Count;
        int header = Header;
        int capacity = ScreenPane.MaxOverlayRows(Height, _inputRows) - header;
        (_first, _shown) = Viewport(page.Rows.Count, _cursor, capacity, _first);

        var lines = new List<IRenderable>(header + _shown + 1) { new Markup(TopMarkup(page)) };
        foreach (var row in caption)
        {
            lines.Add(new Markup(Markup.Escape(row), Theme.Body).Overflow(Overflow.Ellipsis));
        }

        if (_status.Count == 0)
        {
            // A space, not an empty Text: Rows adds a line break only after a child that rendered something.
            lines.Add(new Text(" "));
        }
        else
        {
            foreach (var (kind, text) in _status)
            {
                lines.Add(new Markup(StatusMarkup(kind, text)).Overflow(Overflow.Ellipsis));
            }
        }

        for (int i = 0; i < _shown; i++)
        {
            int r = _first + i;
            // One line per row whatever its length (FittedMarkup, 2026-09-18): the viewport's count holds.
            lines.Add(new FittedMarkup(RowMarkup(page.Rows[r], r == _cursor)));
        }

        if (_shown < page.Rows.Count)
        {
            lines.Add(new Markup(Theme.DimMarkup(NoPointer + MoreHint)));
        }

        _pane.ShowOverlay(new Rows(lines), page.Hint, input: _inputRows > 0, close: true);
    }
}
