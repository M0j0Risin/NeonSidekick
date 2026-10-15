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

    /// <summary>
    /// The tabs this one holds (2026-10-10, the user's call: <c>/tools</c>' Database and Media, whose five menus each sat behind their
    /// switches on a page of their own): while it is the tab on show a second strip of their titles is drawn under the first
    /// (<see cref="InfoPane.SubStripRows"/>), and the rows, hint, caption, headings, filter and footer below are the shown one's
    /// (<see cref="Shown"/>); ←/→ walk them as tabs of the strip, Tab steps past them (<see cref="MenuPane.StepLeaf"/>). Its own
    /// <see cref="Rows"/> are not drawn. Null or empty for none (every other tab).
    /// </summary>
    public IReadOnlyList<MenuTab>? SubTabs { get; init; }

    /// <summary>The sub-tab on show (2026-10-10), an index into <see cref="SubTabs"/>, kept while the strip moves elsewhere so coming back finds it.</summary>
    public int Sub { get; init; }

    /// <summary>
    /// What the band under the list says for row r of this tab (2026-10-10: each of <see cref="SubTabs"/> describes its own rows),
    /// over the page's <see cref="MenuPage.Footer"/>, which still decides whether the band is drawn at all; null for the page's.
    /// </summary>
    public Func<int, MenuFooter?>? Footer { get; init; }

    /// <summary>The tab's <see cref="MenuPage.Marks"/>; null for none.</summary>
    public IReadOnlyDictionary<int, Style>? Marks { get; init; }

    /// <summary>The tab's <see cref="MenuPage.Moves"/> (2026-10-12): its rows are an order the move keys change.</summary>
    public bool Moves { get; init; }

    /// <summary>
    /// The tab's <see cref="MenuPage.Sides"/> (later on 2026-10-13, the home bar's On the bar tab pinned left, center or right): Shift/Alt+←/→
    /// pin the cursor's row there, while the bare arrows still step the tabs.
    /// </summary>
    public bool Sides { get; init; }

    /// <summary>The tab whose rows show: the sub-tab on show, or this one when it holds none (2026-10-10).</summary>
    public MenuTab Shown => SubTabs is { Count: > 0 } subs ? subs[Math.Clamp(Sub, 0, subs.Count - 1)] : this;
}

/// <summary>
/// What a page's <see cref="MenuPage.Footer"/> says under the list for the cursor's row (2026-10-04): <paramref name="Text"/>
/// word-wrapped, then <paramref name="Last"/> (null for none) on the last row of its own, so a long text never cuts it.
/// </summary>
public sealed record MenuFooter(string Text, string? Last = null)
{
    /// <summary>
    /// <see cref="Text"/> is lines, one a row, each cut to the width, not one text to wrap (2026-10-10, <c>Advisor list</c>'s band: an
    /// advisor a line); more lines than rows end on <see cref="MenuPane.MoreLines"/>. Off for every other footer, whose texts (an MCP
    /// tool's description) may carry line breaks that were always read as spaces.
    /// </summary>
    public bool KeepLines { get; init; }
}

/// <summary>
/// A button on a one-list page's title row (2026-09-21, the queue pane's <c>clear all</c>): drawn
/// as a dim tab nobody is on, the <see cref="FolderPane"/>'s shape; a click on it, or
/// <see cref="Key"/> typed (null for none), returns a <see cref="MenuPick"/> with
/// <see cref="MenuPick.Button"/> set. <paramref name="On"/> (later on 2026-09-29, the embedded model lists' filters) draws
/// it highlighted as the tab one is on, so a button that is a switch shows its state; any number may be.
/// </summary>
public sealed record MenuButton(string Title, char? Key, bool On = false)
{
    /// <summary>
    /// A key with no button drawn (2026-10-11, the <c>/shopping</c> pane's + and -: the title row ran out of room): the key still returns
    /// the button's pick, the strip neither draws nor clicks it. Hidden buttons go after the drawn ones, so a click's index is the same.
    /// </summary>
    public bool Hidden { get; init; }
}

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
    /// <see cref="Buttons"/> drawn and pressed on a tabbed page too (2026-10-15, <c>/eventlog</c>'s list: its log tabs and ⟳ refresh, more,
    /// computer on one title row): after the tabs on the strip, lit as a switch is, their keys pressing them while no filter is typed,
    /// a click on one a <see cref="MenuPick.Button"/>. Off (every other page): a tabbed page's buttons are ignored, as ever.
    /// </summary>
    public bool TabButtons { get; init; }

    /// <summary>
    /// Plain text drawn under the title or the strip, above the status line (a question over its
    /// answers): escaped by the pane, word-wrapped to at most <see cref="MenuPane.CaptionMaxRows"/>
    /// rows with the last cut by an ellipsis, or the more <see cref="CaptionGrows"/> allows. Null for none (every page but the question pane).
    /// </summary>
    public string? Caption { get; init; }

    /// <summary>
    /// The <see cref="Caption"/> may take more than <see cref="MenuPane.CaptionMaxRows"/> rows (2026-10-10, the user's ask: a long
    /// <c>ask_user</c> question was cut by the ellipsis and could not be read whole): as many as the menu's room leaves over the
    /// strip, the status row, a footer and <see cref="MenuPane.CaptionKeepRows"/> list rows (<see cref="MenuPane.CaptionRoom"/>);
    /// the list scrolls under it as ever. False (every page but the question pane): three rows.
    /// </summary>
    public bool CaptionGrows { get; init; }

    /// <summary>
    /// Space returns the cursor's row as a <see cref="MenuPick"/> with <see cref="MenuPick.Toggle"/>
    /// set, the status cleared as Enter clears it (a checkbox list); false (every other page):
    /// Space is swallowed.
    /// </summary>
    public bool SpaceToggles { get; init; }

    /// <summary>Whether Space flips here: the page's <see cref="SpaceToggles"/>, or the shown tab's own (<see cref="MenuTab.SpaceToggles"/>, 2026-10-04).</summary>
    public bool SpaceFlips => SpaceToggles || (Tabs is { } tabs && Tab >= 0 && Tab < tabs.Count && (tabs[Tab].SpaceToggles || tabs[Tab].Shown.SpaceToggles));

    /// <summary>The sub-tab on show on the active tab (2026-10-10, <see cref="MenuTab.SubTabs"/>); −1 when it holds none.</summary>
    public int Sub => Tabs is { } tabs && Tab >= 0 && Tab < tabs.Count && tabs[Tab].SubTabs is { Count: > 0 } subs ? Math.Clamp(tabs[Tab].Sub, 0, subs.Count - 1) : -1;

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
    /// The rows are cells of a grid (2026-10-10, the avatar picker's emoji, <see cref="MenuGrid"/>): laid out left to right in lines of
    /// the width, a heading or a gap a line of its own; ←/→ and ↑/↓ move in two dimensions, so on a tabbed grid page only Tab and
    /// Shift+Tab step tabs; the cursor's cell alone is highlighted, a click lands on a cell. Null for a list (every other page).
    /// </summary>
    public MenuGrid? Grid { get; init; }

    /// <summary>
    /// Rows drawn on a slab of their own style when the cursor is elsewhere (2026-10-10, the avatar picker: the profile's avatar and
    /// the ones other profiles use); a grid page's only (a list row has room for words). Taken from the tab on a tabbed page. Null for none.
    /// </summary>
    public IReadOnlyDictionary<int, Style>? Marks { get; init; }

    /// <summary>
    /// The rows are an order the user changes (2026-10-12, the user's ask: the home bar's chips, "change the order of items"):
    /// Shift+↑/↓ and Alt+↑/↓ return a <see cref="MenuPick"/> with <see cref="MenuPick.Move"/> −1 or +1 for the cursor's row,
    /// Alt+Home/End (and Shift+Home/End) <see cref="int.MinValue"/> or <see cref="int.MaxValue"/> (to the top, to the bottom),
    /// Delete one with <see cref="MenuPick.Remove"/>. Shift is there beside Alt because Windows Terminal binds Alt+arrows to
    /// moving between panes and Terminal.app sends Option+↑ as a bare ESC first. Taken from the tab on a tabbed page. False (every
    /// other page): the keys are what they were.
    /// </summary>
    public bool Moves { get; init; }

    /// <summary>
    /// The rows are an order in two groups, pinned left and pinned right (2026-10-13, the user's ask: the hint bar's and the tool
    /// bar's items on either side): Shift+←/→ and Alt+←/→ return a <see cref="MenuPick"/> with <see cref="MenuPick.Side"/> −1 or +1
    /// for the cursor's row (<see cref="MenuPane.SideOf"/>). On an untabbed page, and since later on 2026-10-13 on a tab that asks
    /// (<see cref="MenuTab.Sides"/>, taken from it), where the bare ←/→ still walk the tabs. False: the keys are what they were.
    /// </summary>
    public bool Sides { get; init; }

    /// <summary>
    /// A page over <paramref name="tabs"/> showing <paramref name="tab"/>: the strip labelled
    /// <paramref name="title"/>, that tab's rows, its caption, and its hint when it has one else
    /// <paramref name="hint"/> (kept as <see cref="BaseHint"/> for the tabs without); a tab with sub-tabs gives its shown one's
    /// (<see cref="MenuTab.Shown"/>, 2026-10-10).
    /// </summary>
    public static MenuPage Tabbed(string title, IReadOnlyList<MenuTab> tabs, int tab, string hint)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        if (tabs.Count == 0)
        {
            throw new ArgumentException("A tabbed page needs at least one tab.", nameof(tabs));
        }

        tab = Math.Clamp(tab, 0, tabs.Count - 1);
        var shown = tabs[tab].Shown;
        return new MenuPage(title, shown.Rows, shown.Hint ?? hint) { Tabs = tabs, Tab = tab, Caption = shown.Caption, BaseHint = hint, Headings = shown.Headings, Filter = shown.Filter, Marks = shown.Marks, Moves = shown.Moves, Sides = shown.Sides };
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
/// <see cref="MenuPage.Filter"/> (2026-10-03) is <see cref="Filter"/>, the text it leaves; null otherwise. <see cref="Sub"/> is the sub-tab
/// on show on a tab that holds them (2026-10-10, <see cref="MenuTab.SubTabs"/>), −1 on one that does not. <see cref="Refresh"/>
/// (2026-10-12): nothing picked — the page's facts changed under it (<see cref="MenuPane.PickAsync"/>'s <c>stale</c>), so the host builds it
/// again and shows it with the same tab, sub-tab and cursor. <see cref="Side"/> (2026-10-13, <see cref="MenuPage.Sides"/>): −1 to pin the
/// cursor's row left, +1 right, 0 for anything else.
/// </summary>
public readonly record struct MenuPick(int Tab, int Row, bool Toggle = false, int Button = -1, string? Filter = null, int Sub = -1, int Move = 0, bool Remove = false, bool Refresh = false, int Side = 0);

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
/// ESC (null); one on a title of the second strip (<see cref="MenuTab.SubTabs"/>, 2026-10-10) switches to that sub-tab; two left clicks off the pane — the transcript, a rule, the hint row — within the
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
/// tab's height on every tab (<see cref="TabBodyRows"/>, the same day; a second strip counted in, 2026-10-10). Without the pane (no geometry) there is nothing to draw: the callers
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
    /// Which way a click on a <see cref="MoreHint"/> row pages (2026-10-14, the user's ask: the row took no click), drawn at column 0
    /// after <paramref name="lead"/>: −1 left of the <c>▼</c>'s column while the <c>▲</c> is drawn (the lead and the <c>▲</c>), +1 on
    /// the <c>▼</c> or the count after it while the <c>▼</c> is drawn, null otherwise — the arrows touch, so each one's target is its cell
    /// and its outer side. The menus, the folder pane, the info panes and the input line's completion list share it. Pure.
    /// </summary>
    public static int? MoreStep(int x, string lead, int first, int shown, int count)
    {
        ArgumentNullException.ThrowIfNull(lead);
        bool up = first > 0;
        bool down = first + shown < count;
        int downAt = TextCells.Width(lead) + TextCells.Width(up ? "▲" : " ");
        return x < downAt ? (up && x >= 0 ? -1 : null) : down ? +1 : null;
    }

    /// <summary>
    /// A <see cref="MenuFooter"/> laid out for <paramref name="width"/> cells: its text wrapped (<see cref="CaptionRows"/>) to the
    /// <see cref="FooterRows"/> its last line leaves, then that line cut to the width. Pure.
    /// </summary>
    public static IReadOnlyList<string> FooterLines(MenuFooter footer, int width)
    {
        ArgumentNullException.ThrowIfNull(footer);
        int room = footer.Last is null ? FooterRows : FooterRows - 1;
        var rows = new List<string>(footer.KeepLines ? KeptLines(footer.Text, width, room) : CaptionRows(footer.Text, width, room));
        if (footer.Last is { } last && width > 0)
        {
            rows.AddRange(CaptionRows(last, width, 1));
        }

        return rows;
    }

    /// <summary>The last of a <see cref="MenuFooter.KeepLines"/> footer's rows when its lines are more than it has (2026-10-10). Pinned.</summary>
    public static string MoreLines(int count) => "+" + count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " more";

    /// <summary><paramref name="text"/>'s lines, each cut to <paramref name="width"/>, at most <paramref name="maxRows"/>, the last saying how many more. Pure.</summary>
    private static List<string> KeptLines(string text, int width, int maxRows)
    {
        var rows = new List<string>();
        if (width <= 0 || maxRows <= 0)
        {
            return rows;
        }

        var lines = text.ReplaceLineEndings("\n").Split('\n');
        int shown = lines.Length <= maxRows ? lines.Length : maxRows - 1;
        rows.AddRange(lines.Take(shown).Select(line => ScreenPane.Fit(line, width)));
        if (shown < lines.Length)
        {
            rows.Add(ScreenPane.Fit(MoreLines(lines.Length - shown), width));
        }

        return rows;
    }

    /// <summary>
    /// The rows a page's <see cref="MenuPage.Footer"/> text takes, kept whether or not the row under the cursor has a text (2026-10-04).
    /// Four since 2026-10-07 (the user's ask, with <c>/memory</c>'s footer: four rows of the list's width hold a whole 300-character
    /// memory down to about 80 columns, and the longer tool and setting descriptions stopped being cut); three until then. Five
    /// since 2026-10-10 (the user's ask: a footer with a last line, <c>Default: …</c>, left its text three rows, and the longer
    /// setting descriptions, Botchat's <c>Multi-embedded</c>, were cut again). Pinned.
    /// </summary>
    public const int FooterRows = 5;

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

    /// <summary>The list rows a <see cref="MenuPage.CaptionGrows"/> caption leaves under it (2026-10-10): three rows and the more row. Pinned.</summary>
    public const int CaptionKeepRows = 4;

    /// <summary>
    /// The most rows a <see cref="MenuPage.CaptionGrows"/> caption takes (2026-10-10): the menu's <paramref name="contentRows"/> less the
    /// strip's, the status's and the footer's rows and the list's, up to <see cref="CaptionKeepRows"/> of them; never fewer than
    /// <see cref="CaptionMaxRows"/>. Pure.
    /// </summary>
    public static int CaptionRoom(int contentRows, int stripRows, int statusRows, int footerRows, int listRows) =>
        Math.Max(CaptionMaxRows, contentRows - stripRows - statusRows - footerRows - Math.Min(Math.Max(0, listRows), CaptionKeepRows));

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
    private readonly Func<string?>? _clipboard;
    private readonly DoubleClick _clicks;
    private readonly List<(NoticeKind Kind, string Text)> _status = new();
    private MenuPage? _page;
    private int _cursor;
    private int _first;
    private int _shown;
    // Whether the last draw put the more row under the shown rows (2026-10-14: a click on it pages).
    private bool _moreShown;
    private int _captionRows;
    private int _stripRows = 1;
    private int _subRows;
    private int _inputRows;
    private int _sideWidth;
    private int _listWidth;
    private bool _open;

    // A grid page's lines as last drawn (2026-10-10, MenuGrid): the viewport (_first, _shown) counts these, not rows, and the
    // arrows and clicks find cells by them.
    private IReadOnlyList<MenuGridLine> _lines = [];

    /// <param name="mouse">Takes (true) or hands back (false) the console's mouse; null when the screen has none to take.</param>
    /// <param name="clipboard">The clipboard's text a right-click pastes into a page's filter (2026-10-14, the user's call: Ctrl+V's paste already
    /// landed there); null = a right-click is nothing.</param>
    public MenuPane(ScreenPane pane, KeySource keys, Action<bool>? mouse = null, Func<string?>? clipboard = null)
    {
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _mouse = mouse;
        _clipboard = clipboard;
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

    /// <summary>
    /// The title of the page on show while a visit is in progress, else null (2026-10-14, the Web UI's "waiting for an answer at the PC":
    /// a pane the page cannot answer, named). Plain text, as the page was given it; read from any thread.
    /// </summary>
    public string? OpenTitle => Volatile.Read(ref _open) ? Volatile.Read(ref _page)?.Title : null;

    /// <summary>Told each page's title as it is drawn (later on 2026-10-14: the screen watches for a pane a command from the Web UI opened with no ask on the page).</summary>
    public Action<string>? Opening { get; set; }

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

    /// <summary>
    /// Where a key takes a grid page's cursor from <paramref name="from"/> (2026-10-10, <see cref="MenuGrid"/>): ←/→ the cell before or
    /// after, on across lines and wrapping; ↑/↓ the same column a cell line up or down, wrapping; the page keys a view's lines,
    /// stopping at the ends. Null for any other key (Home, End and the rest are the list's).
    /// </summary>
    private int? GridStep(MenuPage page, int from, ConsoleKeyInfo key) => key.Key switch
    {
        ConsoleKey.LeftArrow => page.StopFrom(from - 1, -1, wrap: true),
        ConsoleKey.RightArrow => page.StopFrom(from + 1, +1, wrap: true),
        ConsoleKey.UpArrow => MenuGrid.Vertical(_lines, from, -1, wrap: true),
        ConsoleKey.DownArrow => MenuGrid.Vertical(_lines, from, +1, wrap: true),
        ConsoleKey.PageUp => PagedFrom(page, from, -1),
        ConsoleKey.PageDown => PagedFrom(page, from, +1),
        _ => null,
    };

    /// <summary>
    /// Where a page up (<paramref name="dir"/> −1) or down (+1) takes the cursor from <paramref name="from"/>: a grid's view of lines, the
    /// same column, stopping at the ends; a list's view of rows onto a stop the way it went. PgUp/PgDn and the more row's arrows (2026-10-14).
    /// </summary>
    private int PagedFrom(MenuPage page, int from, int dir) =>
        page.Grid is not null ? MenuGrid.Vertical(_lines, from, dir, wrap: false, Math.Max(1, _shown - 1))
        : dir > 0 ? page.StopFrom(Math.Min(Math.Max(0, page.Rows.Count - 1), from + Math.Max(1, _shown)), +1)
        : page.StopFrom(Math.Max(0, from - Math.Max(1, _shown)), -1);

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
    /// Each tab's sub-tab on show as the last <see cref="PickAsync"/> returned (2026-10-10, <see cref="MenuTab.Sub"/>): ←/→ move them with no
    /// pick, so a host that builds the page again after one carries them over; 0 for a tab without, empty before any.
    /// </summary>
    public IReadOnlyList<int> ShownSubs => _page?.Tabs?.Select(t => t.Sub).ToList() ?? [];

    /// <summary>
    /// Shows <paramref name="page"/> with the cursor on row <paramref name="cursor"/> and reads
    /// keys until Enter (the tab shown and the row's index within it) or ESC, the token or no
    /// keyboard (null). The pane stays open either way: the caller shows the next page or calls
    /// <see cref="Close"/>. The mouse is taken here every time (an <see cref="EditAsync"/> in
    /// between hands it back with its line). <paramref name="highlighted"/>, when given, hears
    /// the cursor's new row after every move that changed it — the arrow, Home / End and page
    /// keys, a click on another shown row — never the opening row, Enter, ESC or a tab switch.
    /// <paramref name="stale"/> (2026-10-12, the user's ask: <c>/skills</c> › Offered › loaded keeping up with the skills a reply loads) makes
    /// the page a live one: when it fires before a pick, the read ends with no key lost and a <see cref="MenuPick.Refresh"/> comes back
    /// with the tab, sub-tab and cursor on show, for the host to build the page again and show it; the same tab keeps its viewport.
    /// </summary>
    public async Task<MenuPick?> PickAsync(MenuPage page, int cursor, CancellationToken cancellationToken, Action<int>? highlighted = null, CancellationToken stale = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        Require();
        if (_pane.Dismissed)
        {
            // A pane dismissed by a double-click off it: every host on the way up gets null without a draw, until the top one's Close clears it.
            return null;
        }

        if (_page is null || _page.Tab != page.Tab || _page.Sub != page.Sub)
        {
            // Another tab (a host that moved on) starts at the top of its list; the same one re-shown keeps its viewport.
            _first = 0;
        }

        _page = page;
        _open = true;
        Opening?.Invoke(page.Title);
        int count = page.Rows.Count;
        // On a heading or a gap (2026-10-03): the next stop down, else up.
        _cursor = count == 0 ? 0 : page.StopFrom(cursor, +1);
        _inputRows = 0;
        _clicks.Reset();
        _mouse?.Invoke(true);
        Show();
        using var live = stale.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stale) : null;
        try
        {
            while (true)
            {
                page = _page!;
                // A command chord (2026-10-01, the user's ask: as everywhere else) is done in place, ignored under a tool's
                // question, or every level backs out as on ESC and the screen runs it (KeySource.ReadPaneInputAsync).
                var input = await _keys.ReadPaneInputAsync(_pane, live?.Token ?? cancellationToken).ConfigureAwait(false);
                if (input is null && stale.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    // The page went stale (2026-10-12): the host shows it again, built afresh, where it stood.
                    return new MenuPick(page.Tab, _cursor, Sub: page.Sub, Refresh: true);
                }

                if (input is InputEvent.Click click)
                {
                    // A left click on a shown row is the arrow keys' move, a second on the same row
                    // within DoubleClick.Interval is Enter (2026-09-18), one on a tab's title the
                    // tab keys', one on the more row's ▲ or ▼ PgUp's or PgDn's (2026-10-14); the label, a gap in the strip, the status, a
                    // slot-less miss and a drag are nothing, and a right click is too but on a page with a filter, where it pastes the
                    // clipboard's text as Ctrl+V does (2026-10-14) — but two left clicks off the pane
                    // (the transcript, a rule, the hint row) within the interval dismiss every level.
                    if (click.Button != MouseButton.Left)
                    {
                        _clicks.Reset();
                        if (click.Button == MouseButton.Right && page.Filter is { } clipFilter && _clipboard?.Invoke() is { } clip
                            && MenuFilter.Pasted(clipFilter, clip) is { } clipped)
                        {
                            _status.Clear();
                            return new MenuPick(page.Tab, _cursor, Filter: clipped, Sub: page.Sub);
                        }

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
                        // Any row of the strip (2026-09-27): a strip wider than the window takes more than one. The second strip's rows
                        // (2026-10-10) are the last ones, a click there the shown tab's sub-tab.
                        _clicks.Reset();
                        int main = _stripRows - _subRows;
                        if (at >= main && page.Tabs is { } held && held[page.Tab].SubTabs is { Count: > 0 } subs
                            && InfoPane.SubTabAt(page.Title, Titles(subs), Width, click.X, at - main, Badges(subs)) is int sub && sub != page.Sub)
                        {
                            count = SwitchTab(page, held, page.Tab, sub);
                        }
                        else if (at < main && page.Tabs is { } buttoned && page.TabButtons && page.Buttons is { Count: > 0 } stripButtons
                            && InfoPane.TabAt(page.Title, [.. Titles(buttoned), .. Titles(stripButtons)], Width, click.X, at) is int struck)
                        {
                            // A tabbed page with buttons (2026-10-15): a tab's title switches, a button's presses it.
                            if (struck >= buttoned.Count)
                            {
                                _status.Clear();
                                return new MenuPick(page.Tab, _cursor, Button: struck - buttoned.Count, Sub: page.Sub);
                            }

                            if (struck != page.Tab)
                            {
                                count = SwitchTab(page, buttoned, struck);
                            }
                        }
                        else if (at < main && page.Tabs is { Count: > 1 } strip && InfoPane.TabAt(page.Title, Titles(strip), Width, click.X, at, Badges(strip)) is int hit && hit != page.Tab)
                        {
                            count = SwitchTab(page, strip, hit);
                        }
                        else if (page.Tabs is null && page.Buttons is { Count: > 0 } buttons && InfoPane.TabAt(page.Title, Titles(buttons), Width, click.X, at) is int button)
                        {
                            _status.Clear();
                            return new MenuPick(page.Tab, _cursor, Button: button, Sub: page.Sub);
                        }
                    }
                    else if (_sideWidth > 0 && click.X >= _listWidth)
                    {
                        // The side column (2026-10-02): it shows, it picks nothing.
                        _clicks.Reset();
                    }
                    else if (_moreShown && count > 0 && at - Header == _shown)
                    {
                        // The more row (2026-10-14, the user's ask): its ▲ a page up, its ▼ (or the count after it) a page down, as the keys.
                        _clicks.Reset();
                        var (moreFirst, moreShown, moreCount) = page.Grid is not null ? MenuGrid.CellSpan(_lines, _first, _shown) : (_first, _shown, count);
                        if (MoreStep(click.X, NoPointer, moreFirst, moreShown, moreCount) is int dir)
                        {
                            MoveTo(PagedFrom(page, _cursor, dir));
                        }
                    }
                    else if (page.Grid is { } grid)
                    {
                        // A grid (2026-10-10): the line under the click, the cell under its column; a heading, a gap or past
                        // the line's end is a miss.
                        if (at - Header is int g && g >= 0 && g < _shown && _first + g < _lines.Count
                            && grid.CellAt(_lines[_first + g], click.X) is int cell && page.IsStop(cell))
                        {
                            MoveTo(cell);
                            if (_clicks.Second(cell))
                            {
                                _status.Clear();
                                return new MenuPick(page.Tab, _cursor, Sub: page.Sub);
                            }
                        }
                        else
                        {
                            _clicks.Reset();
                        }
                    }
                    else if (at - Header is int i && i >= 0 && i < _shown && page.IsStop(_first + i))
                    {
                        // A heading or a gap (2026-10-03) is no row to land on: the else below, as a miss.
                        int hitRow = _first + i;
                        MoveTo(hitRow);
                        if (count > 0 && _clicks.Second(hitRow))
                        {
                            _status.Clear();
                            return new MenuPick(page.Tab, _cursor, Sub: page.Sub);
                        }
                    }
                    else
                    {
                        _clicks.Reset();
                    }

                    continue;
                }

                if (input is InputEvent.Paste paste && page.Filter is { } pasteFilter && MenuFilter.Pasted(pasteFilter, paste.Text) is { } pasted)
                {
                    // Into the filter (2026-10-10, the avatar picker: an emoji from Windows' emoji panel or the clipboard is a search).
                    _clicks.Reset();
                    _status.Clear();
                    return new MenuPick(page.Tab, _cursor, Filter: pasted, Sub: page.Sub);
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
                    if (count > 0 && page.Grid is not null)
                    {
                        // A grid (2026-10-10): a line a notch.
                        MoveTo(MenuGrid.Vertical(_lines, _cursor, -Math.Sign(wheel.Notches), wrap: false, Math.Abs(wheel.Notches)));
                    }
                    else if (count > 0)
                    {
                        MoveTo(page.StopFrom(Math.Clamp(_cursor - wheel.Notches, 0, count - 1), -Math.Sign(wheel.Notches)));
                    }

                    continue;
                }

                if (input is InputEvent.Key { Info: var escape } && Keys.IsCancel(escape) && page.Filter is { Length: > 0 })
                {
                    // A filter typed (2026-10-03): the first ESC clears it, the next backs out.
                    _status.Clear();
                    return new MenuPick(page.Tab, _cursor, Filter: "", Sub: page.Sub);
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
                    return new MenuPick(page.Tab, _cursor, Sub: page.Sub);
                }

                if (page.SpaceFlips && k.KeyChar == ' ')
                {
                    // The character, not ConsoleKey.Spacebar: a scripted key carries the one without the other.
                    if (count == 0)
                    {
                        continue;
                    }

                    _status.Clear();
                    return new MenuPick(page.Tab, _cursor, Toggle: true, Sub: page.Sub);
                }

                if (page.Moves && count > 0 && MoveOf(k) is { } move)
                {
                    // An order the user changes (2026-10-12, the home bar's picker): the host moves or removes the row and shows the page again.
                    _status.Clear();
                    return move == 0
                        ? new MenuPick(page.Tab, _cursor, Sub: page.Sub, Remove: true)
                        : new MenuPick(page.Tab, _cursor, Sub: page.Sub, Move: move);
                }

                if (page.Sides && count > 0 && SideOf(k) is { } side)
                {
                    // Pinned left or right (2026-10-13; a tab's too since later that day, ahead of the tab step): the host moves the row
                    // to that group and shows the page again.
                    _status.Clear();
                    return new MenuPick(page.Tab, _cursor, Sub: page.Sub, Side: side);
                }

                if (page.Tabs is { } tabs && (tabs.Count > 1 || page.Sub >= 0) && (page.Grid is null || k.Key == ConsoleKey.Tab) && InfoPane.TabStep(k) is int step)
                {
                    // The arrows walk every sub-tab, Tab and Shift+Tab whole tabs (2026-10-10); the same on a strip without sub-tabs.
                    var (tab, sub) = StepLeaf(tabs, page.Tab, step, whole: k.Key == ConsoleKey.Tab);
                    count = SwitchTab(page, tabs, tab, sub);
                    continue;
                }

                // A button's key wins while nothing is typed (2026-10-07: a list with keyed buttons that filters too, /docker's r = refresh,
                // /youtube list's d = remove and c = clear all); once a filter is under way every character goes on it. A letter typed with
                // Shift always goes on the filter (2026-10-13, the user's pick, with the Browser window's saved links and their five lettered
                // buttons): a filter may start with a button's letter that way. The modifier, not the case, so Caps Lock leaves the buttons
                // be on Windows; a Unix terminal sends the character alone (VtInputParser reads A–Z as Shift), so there Caps Lock filters.
                bool shiftedLetter = (k.Modifiers & ConsoleModifiers.Shift) != 0 && char.IsLetter(k.KeyChar);
                bool buttonKey = page.Filter is { Length: 0 } && (page.Tabs is null || page.TabButtons) && page.Buttons is { Count: > 0 } offered && !shiftedLetter
                    && k.KeyChar is not '\0' && !char.IsControl(k.KeyChar) && ButtonFor(offered, k.KeyChar) is not null;
                if (!buttonKey && page.Filter is { } filter && MenuFilter.Edit(filter, k, page.SpaceFlips) is { } typed)
                {
                    // Typed into the filter (2026-10-03): the keys already waiting go on it too, so a fast typist's word is one rebuild.
                    while (_keys.TakeQueued(e => e is InputEvent.Key { Info: var q } && !Keys.IsCancel(q) && MenuFilter.Edit(typed, q, page.SpaceFlips) is not null) is InputEvent.Key { Info: var more })
                    {
                        typed = MenuFilter.Edit(typed, more, page.SpaceFlips)!;
                    }

                    _status.Clear();
                    return new MenuPick(page.Tab, _cursor, Filter: typed, Sub: page.Sub);
                }

                if ((page.Tabs is null || page.TabButtons) && page.Buttons is { Count: > 0 } keyed && k.KeyChar is not '\0' && !char.IsControl(k.KeyChar)
                    && ButtonFor(keyed, k.KeyChar) is int pressed)
                {
                    _status.Clear();
                    return new MenuPick(page.Tab, _cursor, Button: pressed, Sub: page.Sub);
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

                if (page.Grid is not null && count > 0 && GridStep(page, _cursor, k) is int gridNext)
                {
                    // A grid (2026-10-10): a held arrow's queued presses walked as the list's are, drawn once.
                    next = gridNext;
                    while (_keys.TakeQueued(e => e is InputEvent.Key { Info: var q } && q.Modifiers == 0 && q.Key is ConsoleKey.LeftArrow or ConsoleKey.RightArrow or ConsoleKey.UpArrow or ConsoleKey.DownArrow) is InputEvent.Key { Info: var more })
                    {
                        next = GridStep(page, next, more) ?? next;
                    }

                    MoveTo(next);
                    continue;
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
                    case ConsoleKey.PageDown: next = PagedFrom(page, _cursor, +1); break;
                    case ConsoleKey.PageUp: next = PagedFrom(page, _cursor, -1); break;
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
        Opening?.Invoke(page.Title);
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
    /// Where a tab key goes from <paramref name="tab"/> (2026-10-10, <see cref="MenuTab.SubTabs"/>): by <paramref name="step"/> (±1) through
    /// every sub-tab and on to the next tab, wrapping — a tab with sub-tabs entered at its first going right, its last going left — or,
    /// <paramref name="whole"/> (Tab, Shift+Tab), to the next tab, the one it holds on show kept. Returns the tab and the sub-tab to
    /// show, null to keep the tab's own <see cref="MenuTab.Sub"/>. On a strip without sub-tabs both are the next tab. Pure.
    /// </summary>
    public static (int Tab, int? Sub) StepLeaf(IReadOnlyList<MenuTab> tabs, int tab, int step, bool whole)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        step = step < 0 ? -1 : 1;
        if (!whole && tabs[tab].SubTabs is { Count: > 0 } subs && Math.Clamp(tabs[tab].Sub, 0, subs.Count - 1) + step is var within && within >= 0 && within < subs.Count)
        {
            return (tab, within);
        }

        int next = (tab + step + tabs.Count) % tabs.Count;
        return !whole && tabs[next].SubTabs is { Count: > 0 } entered ? (next, step > 0 ? 0 : entered.Count - 1) : (next, null);
    }

    /// <summary>
    /// A switch to <paramref name="tab"/>: the other tab's rows, caption and hint (the page's hint
    /// when the tab has none), the cursor on the page's <see cref="MenuPage.TabCursors"/> row for
    /// it else the first, the status dropped (what the last action did belongs to the tab it ran on)
    /// and every other page setting kept. <paramref name="sub"/> (2026-10-10) is the sub-tab to show on a tab that holds them,
    /// kept on it for the next visit; null keeps the one it had. Returns the row count shown.
    /// </summary>
    private int SwitchTab(MenuPage page, IReadOnlyList<MenuTab> tabs, int tab, int? sub = null)
    {
        if (sub is int s && tabs[tab].SubTabs is { Count: > 0 } subs)
        {
            var held = tabs.ToArray();
            held[tab] = held[tab] with { Sub = Math.Clamp(s, 0, subs.Count - 1) };
            tabs = held;
        }

        // A `with`, never Tabbed() again: the hotkeys, the toggle and the cursors would go with a rebuild.
        var shown = tabs[tab].Shown;
        _page = page with { Tabs = tabs, Rows = shown.Rows, Tab = tab, Caption = shown.Caption, Hint = shown.Hint ?? page.BaseHint ?? page.Hint, Headings = shown.Headings, Filter = shown.Filter, Marks = shown.Marks, Moves = shown.Moves, Sides = shown.Sides };
        int count = _page.Rows.Count;
        _cursor = _page.StopFrom(page.TabCursors is { } cursors && tab < cursors.Count && count > 0 ? cursors[tab] : 0, +1);
        _first = 0;
        _status.Clear();
        Show();
        return count;
    }

    /// <summary>
    /// What a key does on a <see cref="MenuPage.Moves"/> page (2026-10-12): Shift/Alt+↑ −1, Shift/Alt+↓ +1, Shift/Alt+Home
    /// <see cref="int.MinValue"/>, Shift/Alt+End <see cref="int.MaxValue"/>, Delete 0 (remove); null for any other key. Pure.
    /// </summary>
    public static int? MoveOf(ConsoleKeyInfo key)
    {
        bool held = (key.Modifiers & (ConsoleModifiers.Shift | ConsoleModifiers.Alt)) != 0 && (key.Modifiers & ConsoleModifiers.Control) == 0;
        return key.Key switch
        {
            ConsoleKey.UpArrow when held => -1,
            ConsoleKey.DownArrow when held => 1,
            ConsoleKey.Home when held => int.MinValue,
            ConsoleKey.End when held => int.MaxValue,
            ConsoleKey.Delete when key.Modifiers == 0 => 0,
            _ => null,
        };
    }

    /// <summary>
    /// What a key does on a <see cref="MenuPage.Sides"/> page (2026-10-13): Shift/Alt+← −1 (pin left), Shift/Alt+→ +1 (pin right),
    /// Ctrl not held; null for any other key. Pure.
    /// </summary>
    public static int? SideOf(ConsoleKeyInfo key)
    {
        bool held = (key.Modifiers & (ConsoleModifiers.Shift | ConsoleModifiers.Alt)) != 0 && (key.Modifiers & ConsoleModifiers.Control) == 0;
        return key.Key switch
        {
            ConsoleKey.LeftArrow when held => -1,
            ConsoleKey.RightArrow when held => 1,
            _ => null,
        };
    }

    private static List<string> Titles(IReadOnlyList<MenuTab> tabs) => tabs.Select(t => t.Title).ToList();

    private static List<string> Titles(IReadOnlyList<MenuButton> buttons) => buttons.Where(b => !b.Hidden).Select(b => b.Title).ToList();

    private static List<string?> Badges(IReadOnlyList<MenuTab> tabs) => tabs.Select(t => t.Badge).ToList();

    /// <summary>The indices of the buttons that are <see cref="MenuButton.On"/>: the strip draws them highlighted (later on 2026-09-29).</summary>
    private static HashSet<int> Lit(IReadOnlyList<MenuButton> buttons) => Enumerable.Range(0, buttons.Count).Where(i => buttons[i].On && !buttons[i].Hidden).ToHashSet();

    /// <summary>A tabbed page's strip with buttons (2026-10-15, <see cref="MenuPage.TabButtons"/>): the tab on show and the lit buttons, these counted after the tabs.</summary>
    private static HashSet<int> TabAndLit(int tab, int tabs, IReadOnlyList<MenuButton> buttons)
    {
        var lit = Lit(buttons).Select(i => tabs + i).ToHashSet();
        lit.Add(tab);
        return lit;
    }

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
    /// <c>/youtube list</c> printed a line and never opened, while <c>/memory</c> opened on one dim row): <paramref name="title"/> over
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
        return page.Tabs is { } withButtons && page.TabButtons && page.Buttons is { Count: > 0 } tabButtons
                ? InfoPane.TabStripMarkup(page.Title, [.. Titles(withButtons), .. Titles(tabButtons)], TabAndLit(page.Tab, withButtons.Count, tabButtons))
            : page.Tabs is { } tabs ? InfoPane.TabStripMarkup(page.Title, Titles(tabs), page.Tab, Badges(tabs))
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
        return page.Tabs is { } withButtons && page.TabButtons && page.Buttons is { Count: > 0 } tabButtons
                ? InfoPane.TabStripRows(page.Title, [.. Titles(withButtons), .. Titles(tabButtons)], TabAndLit(page.Tab, withButtons.Count, tabButtons), width)
            : page.Tabs is { } tabs ? [.. InfoPane.TabStripRows(page.Title, Titles(tabs), page.Tab, width, Badges(tabs)), .. SubStripRows(page, width)]
            : page.Buttons is { Count: > 0 } buttons ? InfoPane.TabStripRows(page.Title, Titles(buttons), Lit(buttons), width)
            : [TitleMarkup(page.Title)];
    }

    /// <summary>The second strip's rows under <paramref name="page"/>'s first (2026-10-10, <see cref="MenuTab.SubTabs"/>): none when the tab on show holds no sub-tabs. Pure.</summary>
    public static IReadOnlyList<string> SubStripRows(MenuPage page, int width)
    {
        ArgumentNullException.ThrowIfNull(page);
        return page.Tabs is { } tabs && page.Tab >= 0 && page.Tab < tabs.Count && tabs[page.Tab].SubTabs is { Count: > 0 } subs
            ? InfoPane.SubStripRows(page.Title, Titles(subs), page.Sub, width, Badges(subs))
            : [];
    }

    /// <summary>
    /// The rows under the strip of <paramref name="page"/>'s tallest tab laid out for <paramref name="width"/> cells: its
    /// caption's rows (<see cref="CaptionRows"/>) and its list rows; 0 on a one-list page. What every tab of the page is
    /// padded to (2026-10-01, the user's ask), so the pane keeps its height as one tabs through it. A tab with sub-tabs
    /// (2026-10-10) counts its second strip's rows and its tallest sub-tab's, so arriving on it moves nothing either. The captions
    /// are counted to <paramref name="captionMaxRows"/>, what <see cref="MenuPage.CaptionGrows"/> lets them take (later on 2026-10-10). Pure.
    /// </summary>
    public static int TabBodyRows(MenuPage page, int width, int captionMaxRows = CaptionMaxRows)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (page.Tabs is not { } tabs)
        {
            return 0;
        }

        int tallest = 0;
        foreach (var tab in tabs)
        {
            if (tab.SubTabs is { Count: > 0 } subs)
            {
                int strip = InfoPane.SubStripLayout(page.Title, Titles(subs), width, Badges(subs)).Rows;
                tallest = Math.Max(tallest, strip + subs.Max(s => Body(s)));
                continue;
            }

            tallest = Math.Max(tallest, Body(tab));
        }

        return tallest;

        // A grid tab (2026-10-10) is as tall as its lines.
        int Body(MenuTab tab) => (tab.Caption is { } text ? CaptionRows(text, width, captionMaxRows).Count : 0)
            + (page.Grid is { } grid ? MenuGrid.Lay(tab.Rows.Count, tab.Headings, r => tab.Rows[r].Length == 0, grid.Columns(width)).Count : tab.Rows.Count);
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
        var top = TopRows(page, Width);
        _stripRows = top.Count;
        _subRows = SubStripRows(page, Width).Count;
        int footer = page.Footer is null ? 0 : FooterRuleRows + FooterRows;
        int content = _pane.MenuContentRows(Height, _inputRows);
        // A question's caption (2026-10-10, CaptionGrows) takes what the room leaves over a few list rows, not three.
        int captionMax = page.CaptionGrows ? CaptionRoom(content, _stripRows, Math.Max(1, _status.Count), footer, page.Rows.Count) : CaptionMaxRows;
        IReadOnlyList<string> caption = page.Caption is { } captionText ? CaptionRows(captionText, _listWidth, captionMax) : [];
        _captionRows = caption.Count;
        int header = Header;
        int capacity = content - header - footer;
        bool more;
        if (page.Grid is { } grid)
        {
            // A grid (2026-10-10): the viewport counts lines; the cursor's line at the top of a section brings its heading too.
            _lines = MenuGrid.Lay(page.Rows.Count, page.Headings, r => page.Rows[r].Length == 0, grid.Columns(_listWidth));
            int line = MenuGrid.LineOf(_lines, _cursor);
            int leadLine = line;
            while (leadLine > 0 && !_lines[leadLine - 1].IsCells)
            {
                leadLine--;
            }

            if (leadLine < _first && line - leadLine < capacity - 1)
            {
                _first = leadLine;
            }

            (_first, _shown) = Viewport(_lines.Count, line, capacity, _first);
            more = _shown < _lines.Count;
        }
        else
        {
            _lines = [];
            if (page.LeadRow(_cursor) is int lead && lead < _first && _cursor - lead < capacity - 1)
            {
                // The cursor at the top of a section (2026-10-03): its heading and gap come into view with it.
                _first = lead;
            }

            (_first, _shown) = Viewport(page.Rows.Count, _cursor, capacity, _first);
            more = _shown < page.Rows.Count;
        }
        _moreShown = more;
        // The second strip (2026-10-10) is drawn in the header but counts in the tab's body height: the padding is the less by it.
        int pad = Math.Min(TabBodyRows(page, Width, captionMax) - _subRows, _captionRows + Math.Max(0, capacity)) - (_captionRows + _shown + (more ? 1 : 0));
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
            if (page.Grid is { } cells)
            {
                // A grid's line (2026-10-10): a heading's rule, a gap, or a run of cells, the cursor's highlighted.
                var line = _lines[r];
                lines.Add(line.Heading >= 0 ? new SectionRule(page.Rows[line.Heading])
                    : line.Gap ? new Text(" ")
                    : new FittedMarkup(cells.LineMarkup(line, page.Rows, _cursor, page.Marks)));
                continue;
            }

            // One line per row whatever its length (FittedMarkup, 2026-09-18): the viewport's count holds. A heading
            // (2026-10-03) is a rule to the list's edge with no pointer: the cursor never rests there.
            lines.Add(page.Headings is { } headings && headings.Contains(r)
                ? new SectionRule(page.Rows[r])
                : new FittedMarkup(RowMarkup(page.Rows[r], r == _cursor)));
        }

        if (more && page.Grid is not null)
        {
            // Counted in cells, the grid's rows (2026-10-10).
            var (before, inView, total) = MenuGrid.CellSpan(_lines, _first, _shown);
            lines.Add(new Markup(Theme.DimMarkup(NoPointer + MoreHint(before, inView, total))));
        }
        else if (more)
        {
            lines.Add(new Markup(Theme.DimMarkup(NoPointer + MoreHint(_first, _shown, page.Rows.Count))));
        }

        if (footer > 0)
        {
            // The cursor's row described under the list (2026-10-04), its rows kept, right under the rows (the padding to the
            // tallest tab goes below it); a click there lands on no row. Since 2026-10-05 a rule over it and the text on a slab
            // to the list's edge, a row with nothing to say too, so the shape holds as the cursor moves.
            var text = page.Rows.Count > 0 ? FooterFor(page, _cursor) : null;
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

    /// <summary>What the band says for <paramref name="row"/>: the shown tab's own footer (2026-10-10, <see cref="MenuTab.Footer"/>), else the page's.</summary>
    private static MenuFooter? FooterFor(MenuPage page, int row) =>
        page.Tabs is { } tabs && page.Tab >= 0 && page.Tab < tabs.Count && tabs[page.Tab].Shown.Footer is { } own ? own(row) : page.Footer?.Invoke(page.Tab, row);

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
