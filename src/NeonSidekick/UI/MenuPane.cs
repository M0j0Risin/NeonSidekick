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

    /// <summary>
    /// A few cells drawn right after the title in the strip (2026-10-07, the user's ask): a settings tab's count of rows that
    /// differ from the default (<c>SettingsMenu.ChangedBadge</c>); null for none.
    /// </summary>
    public string? Badge { get; init; }

    /// <summary>The tab's hint row; null for the page's.</summary>
    public string? Hint { get; init; }

    /// <summary>The tab's <see cref="MenuPage.Headings"/>; null for none.</summary>
    public IReadOnlySet<int>? Headings { get; init; }

    /// <summary>The tab's <see cref="MenuPage.Filter"/>; null for a tab that does not filter.</summary>
    public string? Filter { get; init; }

    /// <summary>
    /// Space flips on this tab (2026-10-04: every settings tab, <see cref="App.SettingsMenu.FieldsTab"/>), whatever the page's
    /// <see cref="MenuPage.SpaceToggles"/> says (<see cref="MenuPage.SpaceFlips"/>); false leaves it to the page.
    /// </summary>
    public bool SpaceToggles { get; init; }
}

/// <summary>
/// What a page's <see cref="MenuPage.Footer"/> says under the list for the cursor's row (2026-10-04): <paramref name="Text"/>
/// word-wrapped, then <paramref name="Last"/> (null for none) on the last row of its own, so a long text never cuts it.
/// </summary>
public sealed record MenuFooter(string Text, string? Last = null);

/// <summary>
/// A button on a one-list page's title row (2026-09-21, the queue pane's <c>clear all</c>): drawn
/// as a dim tab nobody is on, the <see cref="FolderPane"/>'s shape; a click on it, or
/// <see cref="Key"/> typed (null for none), returns a <see cref="MenuPick"/> with
/// <see cref="MenuPick.Button"/> set. <paramref name="On"/> (later on 2026-09-29, the embedded model lists' filters) draws
/// it highlighted as the tab one is on, so a button that is a switch shows its state; any number may be.
/// </summary>
public sealed record MenuButton(string Title, char? Key, bool On = false);

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

    /// <summary>Whether Space flips here: the page's <see cref="SpaceToggles"/>, or the shown tab's own (<see cref="MenuTab.SpaceToggles"/>, 2026-10-04).</summary>
    public bool SpaceFlips => SpaceToggles || (Tabs is { } tabs && Tab >= 0 && Tab < tabs.Count && tabs[Tab].SpaceToggles);

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
    /// The name each row of <see cref="Rows"/> jumps by (2026-10-03, the user's ask: the theme pickers): a typed letter or digit
    /// moves the cursor to the next stop after it whose name starts with that character, case folded, wrapping at the end
    /// (<see cref="TypeAhead"/>, the folder picker's type-ahead), so a second press goes on to the next. Enter still picks; a
    /// character no other row starts with does nothing. Null for none (every page but the themes').
    /// </summary>
    public IReadOnlyList<string>? JumpNames { get; init; }

    /// <summary>
    /// The row Backspace moves the cursor to, exactly as the arrows would — Enter still picks; the
    /// <c>(none)</c> row of a picker that has one. Null for none (every other page).
    /// </summary>
    public int? BackspaceRow { get; init; }

    /// <summary>The hint a tab without one of its own shows, set by <see cref="Tabbed"/>; null on a one-list page (<see cref="Hint"/> is the one).</summary>
    public string? BaseHint { get; init; }

    /// <summary>
    /// A column drawn to the right of the list (2026-10-02, the user's ask: the theme pickers' preview, <see cref="ThemePreview"/>),
    /// asked for afresh at every draw as (the cursor's row, the column's width, its rows): one renderable per row, each drawn one
    /// row high at that width, from the row under the title down. The pane draws it only while the window leaves the list
    /// <see cref="MenuPane.SideMinListWidth"/> cells and the column <see cref="MenuPane.SideMinWidth"/> (<see cref="MenuPane.SideWidth"/>);
    /// narrower, the page is drawn as one without. A click on it does nothing. Null for none (every page but the themes').
    /// </summary>
    public Func<int, int, int, IReadOnlyList<IRenderable>>? Side { get; init; }

    /// <summary>
    /// The rows of <see cref="Rows"/> that are section headings (2026-10-03, the user's call: <c>/tools</c>' Offered
    /// tab and <c>/mcp</c>'s Tools tab): each one's markup is a <see cref="SectionRule.Markup(string, string?, string?)"/>,
    /// drawn as a <see cref="SectionRule"/> to the list's edge with no pointer, and never a cursor stop — nor is an empty
    /// row (<c>""</c>, the gap before a heading) on such a page (<see cref="IsStop"/>). Null for none: every row a stop,
    /// an empty one too, as every page was before.
    /// </summary>
    public IReadOnlySet<int>? Headings { get; init; }

    /// <summary>
    /// The text under the list for the cursor's row, as (tab, row) → text (2026-10-04, the UI review: <c>/settings</c> said nothing
    /// about a row, while <c>neon_help</c> had every one's description): drawn dim, word-wrapped to <see cref="MenuPane.FooterRows"/>
    /// rows, and those rows kept whatever the row returns (null for nothing), so the pane holds its height as the cursor moves.
    /// Null for none: no rows kept.
    /// </summary>
    public Func<int, int, MenuFooter?>? Footer { get; init; }

    /// <summary>
    /// The text typed to filter the rows (2026-10-03, the user's ask: <c>/tools</c>' and <c>/skills</c>' Offered tabs and
    /// <c>/mcp</c>'s Tools tab, <see cref="MenuFilter"/>): a character that types, and Backspace while there is text, return a
    /// <see cref="MenuPick"/> with the new text as <see cref="MenuPick.Filter"/> for the menu to rebuild the rows by; ESC with
    /// text returns <c>""</c>, so the first ESC clears the filter and the next closes. Null for a page that does not filter
    /// (every page but those three tabs): the keys are what they were. Taken from the tab on a tabbed page.
    /// </summary>
    public string? Filter { get; init; }

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
        return new MenuPage(title, tabs[tab].Rows, tabs[tab].Hint ?? hint) { Tabs = tabs, Tab = tab, Caption = tabs[tab].Caption, BaseHint = hint, Headings = tabs[tab].Headings, Filter = tabs[tab].Filter };
    }

    /// <summary>Whether the cursor may rest on <paramref name="row"/>: any row of a page without <see cref="Headings"/>; on one with them, a row neither a heading nor empty. Pure.</summary>
    public bool IsStop(int row) =>
        Headings is not { } headings || (row >= 0 && row < Rows.Count && !headings.Contains(row) && Rows[row].Length > 0);

    /// <summary>
    /// The first stop (<see cref="IsStop"/>) from <paramref name="row"/> on in <paramref name="step"/>'s direction (+1 down,
    /// −1 up), <paramref name="row"/> itself included, wrapping past the ends when <paramref name="wrap"/>, else the first
    /// stop the other way; <paramref name="row"/> clamped when the page has none. Pure.
    /// </summary>
    public int StopFrom(int row, int step, bool wrap = false)
    {
        int count = Rows.Count;
        if (count == 0)
        {
            return 0;
        }

        step = step < 0 ? -1 : 1;
        row = wrap ? ((row % count) + count) % count : Math.Clamp(row, 0, count - 1);
        for (int i = 0, r = row; i < count; i++)
        {
            if (IsStop(r))
            {
                return r;
            }

            r += step;
            if (r < 0 || r >= count)
            {
                if (!wrap)
                {
                    break;
                }

                r = (r + count) % count;
            }
        }

        if (!wrap)
        {
            for (int r = row - step; r >= 0 && r < count; r -= step)
            {
                if (IsStop(r))
                {
                    return r;
                }
            }
        }

        return row;
    }

    /// <summary>
    /// The row a view showing <paramref name="cursor"/> should start at or above: the first of the rows directly over it
    /// that are no stop — the gap and the heading of its section — so reaching the top of a section shows its heading
    /// (2026-10-03). <paramref name="cursor"/> itself on a page without <see cref="Headings"/>. Pure.
    /// </summary>
    public int LeadRow(int cursor)
    {
        int top = cursor;
        while (Headings is not null && top > 0 && top - 1 < Rows.Count && !IsStop(top - 1))
        {
            top--;
        }

        return top;
    }
}

/// <summary>
/// What <see cref="MenuPane.PickAsync"/> returns on Enter — or on Space over a page whose
/// <see cref="MenuPage.SpaceToggles"/> (<see cref="Toggle"/> true): the tab shown (0 on a one-list
/// page) and the cursor's row within it. A press on one of the page's <see cref="MenuPage.Buttons"/>
/// (2026-09-21) is <see cref="Button"/> at its index, the row still the cursor's; −1 otherwise. A key that edits the page's
/// <see cref="MenuPage.Filter"/> (2026-10-03) is <see cref="Filter"/>, the text it leaves; null otherwise.
/// </summary>
public readonly record struct MenuPick(int Tab, int Row, bool Toggle = false, int Button = -1, string? Filter = null);

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
/// every other key is swallowed. On a page with <see cref="MenuPage.Headings"/> (2026-10-03) every move lands on a
/// stop (<see cref="MenuPage.StopFrom"/>): the arrows step past a heading and its gap, Home / End and the page keys
/// and the wheel take the nearest stop the way they went, a click on a heading or a gap is a miss, and reaching the
/// top of a section brings its heading into view.
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
/// (<see cref="MoreHint"/>); the room is the <c>Menus max height</c> share of the window
/// (<see cref="ScreenPane.MenuContentRows"/>, 2026-10-01), and a tabbed page keeps its tallest
/// tab's height on every tab (<see cref="TabBodyRows"/>, the same day). Without the pane (no geometry) there is nothing to draw: the callers
/// branch to a Spectre prompt, and a call here throws.</para>
/// </summary>
public sealed class MenuPane : INoticeSink
{
    /// <summary>
    /// The dim last row when the list is cut by the window: where the view is and how much there is, <c>▲▼ 13–25 of 58</c>, each
    /// arrow only while there is more that way, a space in its place otherwise (2026-10-04, the UI review: <c>↑/↓ for more</c>
    /// until then said neither). <paramref name="first"/> is the first row shown (from 0), <paramref name="shown"/> how many. Pinned.
    /// </summary>
    public static string MoreHint(int first, int shown, int count) =>
        string.Concat(first > 0 ? "▲" : " ", first + shown < count ? "▼" : " ", " ",
            (first + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), "–",
            (first + shown).ToString(System.Globalization.CultureInfo.InvariantCulture), " of ",
            count.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// A <see cref="MenuFooter"/> laid out for <paramref name="width"/> cells: its text wrapped (<see cref="CaptionRows"/>) to the
    /// <see cref="FooterRows"/> its last line leaves, then that line cut to the width. Pure.
    /// </summary>
    public static IReadOnlyList<string> FooterLines(MenuFooter footer, int width)
    {
        ArgumentNullException.ThrowIfNull(footer);
        var rows = new List<string>(CaptionRows(footer.Text, width, footer.Last is null ? FooterRows : FooterRows - 1));
        if (footer.Last is { } last && width > 0)
        {
            rows.AddRange(CaptionRows(last, width, 1));
        }

        return rows;
    }

    /// <summary>
    /// The rows a page's <see cref="MenuPage.Footer"/> text takes, kept whether or not the row under the cursor has a text (2026-10-04).
    /// Four since 2026-10-07 (the user's ask, with <c>/memory</c>'s footer: four rows of the list's width hold a whole 300-character
    /// memory down to about 80 columns, and the longer tool and setting descriptions stopped being cut); three until then. Pinned.
    /// </summary>
    public const int FooterRows = 4;

    /// <summary>
    /// The rule over a page's <see cref="MenuPage.Footer"/> (2026-10-05, the user's ask: with a short list the description read as
    /// more of its rows): one row of <see cref="ScreenPane.RuleGlyph"/> in <see cref="Theme.PaneRule"/>, the text's rows on the
    /// <see cref="Theme.MenuFooter"/> slab under it. Pinned.
    /// </summary>
    public const int FooterRuleRows = 1;

    /// <summary>What the cursor's row starts with; every other row gets the same width of spaces. Pinned.</summary>
    public const string Pointer = "▸ ";
    public const string NoPointer = "  ";

    /// <summary>The <see cref="DoubleClick"/> key of a click off the pane (2026-09-18): no list row, and never the pairing's own −1.</summary>
    public const int OutsideRow = -2;

    /// <summary>The most rows a page's <see cref="MenuPage.Caption"/> takes; the last is cut with an ellipsis. Pinned.</summary>
    public const int CaptionMaxRows = 3;

    /// <summary>The window height assumed when the console reports none (the mention list sizes by it too).</summary>
    public const int DefaultHeight = 24;

    /// <summary>The fewest cells the list keeps beside a <see cref="MenuPage.Side"/> column (2026-10-02).</summary>
    public const int SideMinListWidth = 36;

    /// <summary>The blank cells between the list and a <see cref="MenuPage.Side"/> column.</summary>
    public const int SideGap = 2;

    /// <summary>The narrowest <see cref="MenuPage.Side"/> column drawn; a window that leaves less draws the page without it.</summary>
    public const int SideMinWidth = 40;

    /// <summary>The widest <see cref="MenuPage.Side"/> column: past it the list takes the room (the Theme Atlas's screen is 66 cells and its margins).</summary>
    public const int SideMaxWidth = 72;

    /// <summary>The fewest rows a <see cref="MenuPage.Side"/> column gets under the title: a short list is padded to it, within the pane's cap.</summary>
    public const int SideMinRows = 8;

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
    private int _stripRows = 1;
    private int _inputRows;
    private int _sideWidth;
    private int _listWidth;
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
        $"[{Theme.Label.ToMarkup()}]{Markup.Escape(TextCells.Spaced(title))}[/]";

    /// <summary>A row: the pointer and the menu highlight on the cursor's row, an indent on the others. <paramref name="row"/> is markup already.</summary>
    public static string RowMarkup(string row, bool active) =>
        active ? $"[{Theme.MenuHighlight.ToMarkup()}]{Pointer}{row}[/]" : NoPointer + row;

    /// <summary>
    /// The width of a <see cref="MenuPage.Side"/> column in a window <paramref name="width"/> cells wide (2026-10-02): what the
    /// list's <see cref="SideMinListWidth"/>, the <see cref="SideGap"/> and the last column (never written, as the bars leave it)
    /// leave, up to <see cref="SideMaxWidth"/>; 0, no column, under <see cref="SideMinWidth"/>. Pure.
    /// </summary>
    public static int SideWidth(int width)
    {
        int side = Math.Min(SideMaxWidth, width - 1 - SideMinListWidth - SideGap);
        return side >= SideMinWidth ? side : 0;
    }

    /// <summary>The row ↓ (+1) or ↑ (−1) moves the cursor; 0 for every other key. Pure.</summary>
    private static int ArrowStep(ConsoleKeyInfo key) => key.Key switch
    {
        ConsoleKey.DownArrow => 1,
        ConsoleKey.UpArrow => -1,
        _ => 0,
    };

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
        // On a heading or a gap (2026-10-03): the next stop down, else up.
        _cursor = count == 0 ? 0 : page.StopFrom(cursor, +1);
        _inputRows = 0;
        _clicks.Reset();
        _mouse?.Invoke(true);
        Show();
        try
        {
            while (true)
            {
                page = _page!;
                // A command chord (2026-10-01, the user's ask: as everywhere else) is done in place, ignored under a tool's
                // question, or every level backs out as on ESC and the screen runs it (KeySource.ReadPaneInputAsync).
                var input = await _keys.ReadPaneInputAsync(_pane, cancellationToken).ConfigureAwait(false);
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

                    if (at < _stripRows)
                    {
                        // Any row of the strip (2026-09-27): a strip wider than the window takes more than one.
                        _clicks.Reset();
                        if (page.Tabs is { Count: > 1 } strip && InfoPane.TabAt(page.Title, Titles(strip), Width, click.X, at, Badges(strip)) is int hit && hit != page.Tab)
                        {
                            count = SwitchTab(page, strip, hit);
                        }
                        else if (page.Tabs is null && page.Buttons is { Count: > 0 } buttons && InfoPane.TabAt(page.Title, Titles(buttons), Width, click.X, at) is int button)
                        {
                            _status.Clear();
                            return new MenuPick(page.Tab, _cursor, Button: button);
                        }
                    }
                    else if (_sideWidth > 0 && click.X >= _listWidth)
                    {
                        // The side column (2026-10-02): it shows, it picks nothing.
                        _clicks.Reset();
                    }
                    else if (at - Header is int i && i >= 0 && i < _shown && page.IsStop(_first + i))
                    {
                        // A heading or a gap (2026-10-03) is no row to land on: the else below, as a miss.
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

                if (input is InputEvent.Drag or InputEvent.Release or InputEvent.Paste)
                {
                    // A drag is the line's business (a jiggle between the two presses of a
                    // double-click keeps the pair, as does the release between them — an event
                    // since 2026-09-28), a paste has no slot to land in here.
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
                        MoveTo(page.StopFrom(Math.Clamp(_cursor - wheel.Notches, 0, count - 1), -Math.Sign(wheel.Notches)));
                    }

                    continue;
                }

                if (input is InputEvent.Key { Info: var escape } && Keys.IsCancel(escape) && page.Filter is { Length: > 0 })
                {
                    // A filter typed (2026-10-03): the first ESC clears it, the next backs out.
                    _status.Clear();
                    return new MenuPick(page.Tab, _cursor, Filter: "");
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

                if (page.SpaceFlips && k.KeyChar == ' ')
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

                // A button's key wins while nothing is typed (2026-10-07: a list with keyed buttons that filters too, /docker's r = refresh,
                // /youtube saved's c = clear all); once a filter is under way every character goes on it.
                bool buttonKey = page.Filter is { Length: 0 } && page.Tabs is null && page.Buttons is { Count: > 0 } offered
                    && k.KeyChar is not '\0' && !char.IsControl(k.KeyChar) && ButtonFor(offered, k.KeyChar) is not null;
                if (!buttonKey && page.Filter is { } filter && MenuFilter.Edit(filter, k, page.SpaceFlips) is { } typed)
                {
                    // Typed into the filter (2026-10-03): the keys already waiting go on it too, so a fast typist's word is one rebuild.
                    while (_keys.TakeQueued(e => e is InputEvent.Key { Info: var q } && !Keys.IsCancel(q) && MenuFilter.Edit(typed, q, page.SpaceFlips) is not null) is InputEvent.Key { Info: var more })
                    {
                        typed = MenuFilter.Edit(typed, more, page.SpaceFlips)!;
                    }

                    _status.Clear();
                    return new MenuPick(page.Tab, _cursor, Filter: typed);
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
                else if (page.JumpNames is { } names && char.IsLetterOrDigit(k.KeyChar)
                    && TypeAhead.Next(Math.Min(count, names.Count), i => page.IsStop(i) ? names[i] : "", _cursor, k.KeyChar) is int jump && jump >= 0)
                {
                    next = jump;
                }

                switch (k.Key)
                {
                    // Each move lands on a stop (2026-10-03): past a heading and its gap, the way it was going. A held arrow's
                    // queued presses (later that day, the user's ask) are walked here too and drawn once, at the row they end on.
                    case ConsoleKey.DownArrow or ConsoleKey.UpArrow when count > 0:
                        next = page.StopFrom(_cursor + ArrowStep(k), ArrowStep(k), wrap: true);
                        while (_keys.TakeQueued(e => e is InputEvent.Key { Info: var q } && q.Modifiers == 0 && ArrowStep(q) != 0) is InputEvent.Key { Info: var more })
                        {
                            next = page.StopFrom(next + ArrowStep(more), ArrowStep(more), wrap: true);
                        }

                        break;
                    case ConsoleKey.DownArrow or ConsoleKey.UpArrow: next = 0; break;
                    case ConsoleKey.Home: next = page.StopFrom(0, +1); break;
                    case ConsoleKey.End: next = page.StopFrom(count - 1, -1); break;
                    case ConsoleKey.PageDown: next = page.StopFrom(Math.Min(Math.Max(0, count - 1), _cursor + Math.Max(1, _shown)), +1); break;
                    case ConsoleKey.PageUp: next = page.StopFrom(Math.Max(0, _cursor - Math.Max(1, _shown)), -1); break;
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
        _page = page with { Rows = tabs[tab].Rows, Tab = tab, Caption = tabs[tab].Caption, Hint = tabs[tab].Hint ?? page.BaseHint ?? page.Hint, Headings = tabs[tab].Headings, Filter = tabs[tab].Filter };
        int count = _page.Rows.Count;
        _cursor = _page.StopFrom(page.TabCursors is { } cursors && tab < cursors.Count && count > 0 ? cursors[tab] : 0, +1);
        _first = 0;
        _status.Clear();
        Show();
        return count;
    }

    private static List<string> Titles(IReadOnlyList<MenuTab> tabs) => tabs.Select(t => t.Title).ToList();

    private static List<string> Titles(IReadOnlyList<MenuButton> buttons) => buttons.Select(b => b.Title).ToList();

    private static List<string?> Badges(IReadOnlyList<MenuTab> tabs) => tabs.Select(t => t.Badge).ToList();

    /// <summary>The indices of the buttons that are <see cref="MenuButton.On"/>: the strip draws them highlighted (later on 2026-09-29).</summary>
    private static HashSet<int> Lit(IReadOnlyList<MenuButton> buttons) => Enumerable.Range(0, buttons.Count).Where(i => buttons[i].On).ToHashSet();

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
    /// <summary>The hint row of an empty list's pane (<see cref="ShowEmptyAsync"/>). Pinned.</summary>
    public const string EmptyKeys = "ESC = close";

    /// <summary>
    /// An empty list's pane (2026-10-07, the user's call in the consistency pass: <c>/sessions</c>, <c>/queue</c>, <c>/process</c> and
    /// <c>/youtube saved</c> printed a line and never opened, while <c>/memory</c> opened on one dim row): <paramref name="title"/> over
    /// <paramref name="line"/> dim, until ESC; Enter on the row does nothing. The pane closes as it ends.
    /// </summary>
    public async Task ShowEmptyAsync(string title, string line, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(line);
        try
        {
            var page = new MenuPage(title, [Theme.DimMarkup(line)], EmptyKeys);
            while (await PickAsync(page, 0, cancellationToken).ConfigureAwait(false) is not null)
            {
                // Enter on the one row: nothing to do; the page again.
            }
        }
        finally
        {
            Close();
        }
    }

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

    /// <summary>The status lines forgotten (2026-10-02, the camera pane: its one status line replaced at each step); drawn at the next show.</summary>
    public void ClearStatus() => _status.Clear();

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

    /// <summary>The overlay rows above the first list row: the title (or the tab strip's rows), the caption's rows, then the status lines or the one spacer.</summary>
    private int Header => _stripRows + _captionRows + Math.Max(1, _status.Count);

    /// <summary>The title row of <paramref name="page"/>: the tab strip on a tabbed page, the title with its buttons as a strip nobody is on (2026-09-21), else the title alone. Pinned.</summary>
    public static string TopMarkup(MenuPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return page.Tabs is { } tabs ? InfoPane.TabStripMarkup(page.Title, Titles(tabs), page.Tab, Badges(tabs))
            : page.Buttons is { Count: > 0 } buttons ? InfoPane.TabStripMarkup(page.Title, Titles(buttons), Lit(buttons))
            : TitleMarkup(page.Title);
    }

    /// <summary>
    /// <see cref="TopMarkup"/> laid out for <paramref name="width"/> columns: a tab or button strip wider
    /// than the window takes more rows, each later one lined up under the first title
    /// (<see cref="InfoPane.TabStripLayout"/>, 2026-09-27); the title alone is one row.
    /// </summary>
    public static IReadOnlyList<string> TopRows(MenuPage page, int width)
    {
        ArgumentNullException.ThrowIfNull(page);
        return page.Tabs is { } tabs ? InfoPane.TabStripRows(page.Title, Titles(tabs), page.Tab, width, Badges(tabs))
            : page.Buttons is { Count: > 0 } buttons ? InfoPane.TabStripRows(page.Title, Titles(buttons), Lit(buttons), width)
            : [TitleMarkup(page.Title)];
    }

    /// <summary>
    /// The rows under the strip of <paramref name="page"/>'s tallest tab laid out for <paramref name="width"/> cells: its
    /// caption's rows (<see cref="CaptionRows"/>) and its list rows; 0 on a one-list page. What every tab of the page is
    /// padded to (2026-10-01, the user's ask), so the pane keeps its height as one tabs through it. Pure.
    /// </summary>
    public static int TabBodyRows(MenuPage page, int width)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (page.Tabs is not { } tabs)
        {
            return 0;
        }

        int tallest = 0;
        foreach (var tab in tabs)
        {
            int caption = tab.Caption is { } text ? CaptionRows(text, width, CaptionMaxRows).Count : 0;
            tallest = Math.Max(tallest, caption + tab.Rows.Count);
        }

        return tallest;
    }

    /// <summary>
    /// The page laid out for the window: the title or the tab strip, the caption, the status (or a spacer row), the rows in
    /// view, the more row, and on a tabbed page blank rows to its tallest tab's height (<see cref="TabBodyRows"/>,
    /// 2026-10-01, the user's ask: the pane jumped up and down as one tabbed through <c>/settings</c>) — under the list, so
    /// it stays under the strip and the hint row stays put. All of it within <c>Menus max height</c>
    /// (<see cref="ScreenPane.MenuContentRows"/>, the same day): a tab taller than the cap scrolls, the others fill it.
    /// </summary>
    private void Show()
    {
        var page = _page!;
        _sideWidth = page.Side is not null && page.Rows.Count > 0 ? SideWidth(Width) : 0;
        _listWidth = _sideWidth > 0 ? Width - 1 - _sideWidth - SideGap : Width;
        IReadOnlyList<string> caption = page.Caption is { } captionText ? CaptionRows(captionText, _listWidth, CaptionMaxRows) : [];
        _captionRows = caption.Count;
        var top = TopRows(page, Width);
        _stripRows = top.Count;
        int header = Header;
        int footer = page.Footer is null ? 0 : FooterRuleRows + FooterRows;
        int capacity = _pane.MenuContentRows(Height, _inputRows) - header - footer;
        if (page.LeadRow(_cursor) is int lead && lead < _first && _cursor - lead < capacity - 1)
        {
            // The cursor at the top of a section (2026-10-03): its heading and gap come into view with it.
            _first = lead;
        }

        (_first, _shown) = Viewport(page.Rows.Count, _cursor, capacity, _first);
        bool more = _shown < page.Rows.Count;
        int pad = Math.Min(TabBodyRows(page, Width), _captionRows + Math.Max(0, capacity)) - (_captionRows + _shown + (more ? 1 : 0));
        if (_sideWidth > 0)
        {
            // The side column's rows under the title, at least SideMinRows within the cap: a short list is padded to them.
            int body = header - _stripRows + _shown + (more ? 1 : 0) + Math.Max(0, pad);
            int room = header - _stripRows + Math.Max(0, capacity);
            pad = Math.Max(0, pad) + Math.Clamp(SideMinRows - body, 0, Math.Max(0, room - body));
        }

        var lines = new List<IRenderable>(header + _shown + 1 + Math.Max(0, pad));
        foreach (var row in top)
        {
            lines.Add(new Markup(row).Overflow(Overflow.Ellipsis));
        }

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
            // One line per row whatever its length (FittedMarkup, 2026-09-18): the viewport's count holds. A heading
            // (2026-10-03) is a rule to the list's edge with no pointer: the cursor never rests there.
            lines.Add(page.Headings is { } headings && headings.Contains(r)
                ? new SectionRule(page.Rows[r])
                : new FittedMarkup(RowMarkup(page.Rows[r], r == _cursor)));
        }

        if (more)
        {
            lines.Add(new Markup(Theme.DimMarkup(NoPointer + MoreHint(_first, _shown, page.Rows.Count))));
        }

        if (footer > 0)
        {
            // The cursor's row described under the list (2026-10-04), its rows kept, right under the rows (the padding to the
            // tallest tab goes below it); a click there lands on no row. Since 2026-10-05 a rule over it and the text on a slab
            // to the list's edge, a row with nothing to say too, so the shape holds as the cursor moves.
            var text = page.Rows.Count > 0 ? page.Footer!(page.Tab, _cursor) : null;
            var rows = text is null ? [] : FooterLines(text, _listWidth - TextCells.Width(NoPointer));
            lines.Add(new Markup(Theme.StyleMarkup(Theme.PaneRule, new string(ScreenPane.RuleGlyph, Math.Max(1, _listWidth)))).Overflow(Overflow.Crop));
            for (int i = 0; i < FooterRows; i++)
            {
                lines.Add(new Markup(FooterSlabMarkup(i < rows.Count ? rows[i] : "", _listWidth)).Overflow(Overflow.Ellipsis));
            }
        }

        for (int i = 0; i < pad; i++)
        {
            // A space, as the spacer: an empty Text would collapse. A click here lands on no row and does nothing.
            lines.Add(new Text(" "));
        }

        if (_sideWidth > 0)
        {
            // Under the title, each row is the list's part, the gap and the side's line (2026-10-02); the title row keeps
            // the window's width, so the × keeps its corner.
            int rows = lines.Count - top.Count;
            var side = page.Side!(_cursor, _sideWidth, rows);
            for (int i = 0; i < rows; i++)
            {
                lines[top.Count + i] = new SideBySide(lines[top.Count + i], _listWidth, SideGap, i < side.Count ? side[i] : null, _sideWidth);
            }
        }

        _pane.ShowOverlay(new Rows(lines), page.Hint, input: _inputRows > 0, close: true);
    }

    /// <summary>
    /// One footer row on its slab (2026-10-05): <see cref="NoPointer"/> and <paramref name="text"/> (already cut to the width by
    /// <see cref="FooterLines"/>) padded with blanks to <paramref name="width"/> cells, all in <see cref="Theme.MenuFooter"/>. Pure.
    /// </summary>
    public static string FooterSlabMarkup(string text, int width)
    {
        ArgumentNullException.ThrowIfNull(text);
        string row = NoPointer + text;
        return Theme.StyleMarkup(Theme.MenuFooter, row + new string(' ', Math.Max(0, width - TextCells.Width(row))));
    }

    /// <summary>
    /// One overlay row with a <see cref="MenuPage.Side"/> column (2026-10-02): <paramref name="left"/>'s first row cut to
    /// <paramref name="leftWidth"/> and padded to it, <paramref name="gap"/> blanks, then <paramref name="right"/>'s first row
    /// cut to <paramref name="rightWidth"/>.
    /// </summary>
    private sealed class SideBySide(IRenderable left, int leftWidth, int gap, IRenderable? right, int rightWidth) : IRenderable
    {
        public Measurement Measure(RenderOptions options, int maxWidth) => new(Math.Min(maxWidth, leftWidth + gap + rightWidth), maxWidth);

        public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
        {
            var row = FirstRow(left, options, leftWidth);
            int cells = Segment.CellCount(row);
            if (right is not null)
            {
                row.Add(new Segment(new string(' ', Math.Max(0, leftWidth - cells) + gap)));
                row.AddRange(FirstRow(right, options, rightWidth));
            }

            return row;
        }

        private static List<Segment> FirstRow(IRenderable renderable, RenderOptions options, int width)
        {
            var lines = Segment.SplitLines(renderable.Render(options, width), width);
            return lines.Count > 0 ? [.. lines[0]] : [];
        }
    }
}
