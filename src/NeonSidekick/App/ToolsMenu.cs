using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.App;

/// <summary>
/// The <c>/tools</c> screen (2026-09-19, the user's ask): a tabbed <see cref="MenuPane"/> in the
/// <see cref="SkillsMenu"/> shape — the Offered tab lists every tool the app has under its group's
/// heading with <c>on</c> or <c>off</c> beside it (<see cref="ToolsText.OfferedRows"/>), and Enter or
/// Space on a tool row flips it in place: the name goes into or out of <c>ToolsDisabled</c>
/// (<see cref="ToolsText.Flip"/>), the save shows on the status line (<see cref="ToolsText.FlippedNotice"/>),
/// the list is re-read and the cursor stays. The tabs after it — Options (the pane's own
/// <c>$-mention enabled</c> switch, later on 2026-09-19, the <c>/skills</c> Options tab's shape; <c>Tool collapse count</c> under it
/// since 2026-09-22, <c>Code collapse count</c> under that later the same day), then Ask, Files, Git (2026-09-20),
/// Shell (2026-09-21), Web — are settings rows, three of them on <c>/settings</c> until that day (<see cref="SettingsMenu.ToolsTabFields"/>),
/// edited through <see cref="SettingsMenu"/>'s own seams (<see cref="SettingsMenu.FieldsTab"/>,
/// <see cref="SettingsMenu.EditAsync"/>) under this pane's strip, its pickers titled <c>Tools › …</c>
/// (<see cref="SettingsMenu.Root"/>); a group's switch stays the first row of its tab, and a group
/// whose switch is off shows dim on the Offered tab with the switch named after its heading — the
/// per-tool values still flip and save. Nothing here clears the conversation: every flip is read at the next turn
/// (<see cref="ChatScreen.PrepareTurn"/>), so the pane opens mid-turn too and edits as <c>/settings</c> does there. The
/// Claude tab's four Anthropic API rows and the Claude Code server's (2026-09-29 to 2026-10-03, when they went back to
/// <c>/settings</c>' own Claude tab, the user's call) were reconnects; the handling stays for any reconnect row
/// (<see cref="SettingsMenu.IsLlmField"/>): refused mid-turn like there, and <see cref="ShowAsync"/> returns
/// <see cref="SettingsChanges.Llm"/> when one saved, so the screen reconnects once the pane closes — <c>/mcp</c>'s shape.
/// Without the pane the tabs print as plain lines. <c>/tools expand</c> and <c>/tools collapse</c> (2026-09-22) became the
/// root <c>/expand</c> and <c>/collapse</c> later that day, the user's ask; since 2026-10-03 <c>/tools &lt;group&gt;</c> opens one
/// group's switch straight (<see cref="ShowSwitchAsync"/>, the toolbar's tool switches).
/// The Shell tab's allowed-commands row has a door of its own since later on 2026-09-21:
/// <see cref="ShowAllowedCommandsAsync"/> (the toolbar's lock glyph); its <c>Shell police</c> row since 2026-09-22:
/// <see cref="ShowPoliceAsync"/> (the toolbar's officer). Their typed words were <c>/cmdlist</c> and <c>/police</c> until
/// 2026-10-09, <c>/tools shell allowed</c> and <c>/tools shell police</c> since (<see cref="ShowShellSubAsync"/>, the user's call).
/// The Files and Integrations tabs (2026-10-10, the user's pick: the strip still ran to fourteen; ComfyUI and YouTube came off Media to
/// Integrations, the user's ask) and the Media and Database tabs (later on 2026-10-09, the user's call: the strip had grown to 22 tabs) hold the menus, every one a
/// tab of its own until then (<see cref="ToolsText.TabMenus"/>). Until 2026-10-10 each was a row per menu with its switch, Enter opening
/// the five as a page of their own under the crumb <c>Tools › Database</c>; the user found reaching a menu through its switch unintuitive,
/// so since then they are sub-tabs (<see cref="MenuTab.SubTabs"/>): a second strip under the first while the group is on show, the
/// menu's rows under it edited as a plain tab's are, ←/→ walking every menu along both strips and Tab / Shift+Tab stepping past a group,
/// which keeps the menu last shown (<see cref="MenuPane.StepLeaf"/>). A picker under a group is still titled <c>Tools › Databases › …</c>
/// (<see cref="GroupCrumb"/>). Files holds File, Image (later on 2026-10-10, the user's ask: the File menu's picture rows), UNC, GitLib and Obsidian;
/// Core (Standard until later still that day, as Devices was Media; the user's calls) Ask, Web, Shell and Options (later still on 2026-10-10, the user's ask), four tabs of their own until then. Every menu is headed since then too
/// (<see cref="SettingsMenu.ToolsMenuSections"/>, the <c>/settings</c> look): a row is found through <see cref="SettingsMenu.SectionedField"/>, never by index.
/// Since 2026-10-11 (the user's asks) Home, the strip's last, holds HomeAssistant (off Integrations) and Shopping (off Core), and Integrations,
/// left with Docker, ComfyUI and YouTube, is Apps.
/// </summary>
internal sealed class ToolsMenu
{
    private readonly Func<ToolsFacts> _facts;
    private readonly AppSettings _settings;
    private readonly SettingsMenu _menu;
    private readonly INoticeSink _transcript;
    private readonly MenuPane _pane;

    /// <param name="facts">The groups, the <c>LLM offer tools</c> switch and the disabled set; read when the list opens and again after every flip.</param>
    /// <param name="settings">The store the flips write to.</param>
    /// <param name="menu">The settings menu whose rows the Ask / Files / Web tabs are.</param>
    /// <param name="transcript">Where the lines outside the pane go: the screen's deferring sink, since the list may open while a reply runs.</param>
    /// <param name="pane">The menu host in the bottom pane.</param>
    public ToolsMenu(Func<ToolsFacts> facts, AppSettings settings, SettingsMenu menu, INoticeSink transcript, MenuPane pane)
    {
        _facts = facts ?? throw new ArgumentNullException(nameof(facts));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _menu = menu ?? throw new ArgumentNullException(nameof(menu));
        _transcript = transcript ?? throw new ArgumentNullException(nameof(transcript));
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
    }

    /// <summary>Where a notice goes: the pane's status line while the list is open there, else the transcript.</summary>
    private INoticeSink Sink => _pane.IsOpen ? _pane : _transcript;

    /// <summary>
    /// The tabbed page: the Offered rows under <see cref="ToolsText.OfferedKeys"/>, then the four
    /// settings tabs (<see cref="SettingsMenu.TabKeys"/>), Space a flip (<see cref="MenuPage.SpaceToggles"/> —
    /// page-wide, so the host ignores it on the settings tabs), the Offered cursor opening on the first
    /// tool row past its heading; the headings are rules the cursor never rests on (<see cref="MenuTab.Headings"/>, 2026-10-03).
    /// The Offered tab filters (later on 2026-10-03, <see cref="MenuFilter"/>): <paramref name="offered"/> are the rows under
    /// <paramref name="filter"/>, <paramref name="total"/> the tools there are, for the caption while a filter is typed.
    /// <paramref name="toolFooter"/> (2026-10-05, the user's ask) says under the list what the cursor's tool does, whole
    /// (<see cref="ToolsText.ToolFooter"/>); null leaves the band blank on the Offered tab, as it stood until then.
    /// <paramref name="subs"/> (2026-10-10) is the menu on show on each group tab, by tab index (<see cref="MenuPane.ShownSubs"/>); null for each one's first.
    /// </summary>
    public static MenuPage Page(IReadOnlyList<(string Markup, string? Tool, bool Heading)> offered, AppSettingsData saved, SettingsMenu menu, int tab, string filter = "", int total = 0, Func<string, MenuFooter?>? toolFooter = null, IReadOnlyList<int>? subs = null)
    {
        ArgumentNullException.ThrowIfNull(offered);
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(menu);
        ArgumentNullException.ThrowIfNull(filter);
        var tabs = new MenuTab[ToolsText.TabTitles.Count];
        tabs[0] = new MenuTab(ToolsText.OfferedTabTitle, offered.Select(r => r.Markup).ToList())
        {
            Hint = MenuFilter.Hint(ToolsText.OfferedKeys, filter),
            Headings = ToolsText.HeadingRows(offered),
            Filter = filter,
            Caption = MenuFilter.CaptionOrNull(filter, ToolsText.ToolCount(offered), total),
        };
        for (int t = 1; t < tabs.Length; t++)
        {
            // A group tab holds its menus as sub-tabs (2026-10-10); a plain tab is its one menu's rows.
            var menus = ToolsText.TabMenus[t - 1];
            tabs[t] = menus.Count > 1
                ? GroupTab(t, saved, menu, subs is not null && t < subs.Count ? subs[t] : 0)
                : menu.FieldsTab(ToolsText.TabTitles[t], SettingsMenu.ToolsTabFields[menus[0]], saved, sections: SettingsMenu.ToolsMenuSections[menus[0]]);
        }

        return MenuPage.Tabbed(ToolsText.Label, tabs, tab, SettingsMenu.TabKeys) with
        {
            SpaceToggles = true,
            TabCursors = [ToolsText.FirstToolRow(offered), .. Enumerable.Repeat(0, tabs.Length - 1)],
            // The Offered tab's band says the cursor's tool (2026-10-05): a heading, a gap, the off line and the no-match row name none.
            // A group's menus say their own rows (2026-10-10, each sub-tab's Footer).
            Footer = (t, row) => t == 0
                ? row < offered.Count && offered[row].Tool is { } name ? toolFooter?.Invoke(name) : null
                : RowField(t, 0, row) is { } field ? menu.FieldFooter(field, saved) : null,
        };
    }

    /// <summary>
    /// Group tab <paramref name="tab"/> (2026-10-10): its menus as sub-tabs, each with its rows as its tab had them
    /// (<see cref="SettingsMenu.FieldsTab"/>) and its own band, <paramref name="sub"/> the one on show; the strip's badge counts every
    /// row of the five whose value is not the default (<see cref="SettingsMenu.GroupBadge"/>).
    /// </summary>
    private static MenuTab GroupTab(int tab, AppSettingsData saved, SettingsMenu menu, int sub)
    {
        var menus = ToolsText.TabMenus[tab - 1];
        var subs = menus.Select(m =>
        {
            var fields = SettingsMenu.ToolsTabFields[m];
            var sections = SettingsMenu.ToolsMenuSections[m];
            return menu.FieldsTab(ToolsText.MenuTitles[m], fields, saved, sections: sections) with { Footer = row => SettingsMenu.SectionedField(fields, sections, row) is { } field ? menu.FieldFooter(field, saved) : null };
        }).ToList();
        return new MenuTab(ToolsText.TabTitles[tab], []) { SubTabs = subs, Sub = Math.Clamp(sub, 0, subs.Count - 1), Badge = menu.GroupBadge(menus, saved) };
    }

    /// <summary>
    /// The field behind row <paramref name="row"/> of tab <paramref name="tab"/> (after Offered): a plain tab's row, or on a group tab
    /// (2026-10-10) the row of its menu <paramref name="sub"/>; null for a heading (<see cref="SettingsMenu.ToolsMenuSections"/>, later on 2026-10-10) and past the rows.
    /// </summary>
    private static SettingsField? RowField(int tab, int sub, int row)
    {
        var menus = ToolsText.TabMenus[tab - 1];
        int m = menus[menus.Count > 1 ? Math.Clamp(sub, 0, menus.Count - 1) : 0];
        return SettingsMenu.SectionedField(SettingsMenu.ToolsTabFields[m], SettingsMenu.ToolsMenuSections[m], row);
    }

    /// <summary>The root a picker under group tab <paramref name="tab"/> is titled from (2026-10-09; the group's page's crumb until 2026-10-10): <c>🛠️ Tools › Databases</c>. Pinned.</summary>
    public static string GroupCrumb(int tab) => ToolsText.Label + " › " + ToolsText.TabTitles[tab];

    /// <summary>The tabs as plain lines, for a console without the pane: each tab's title as a heading, its content indented; a group tab's menus (2026-10-09) each a heading under it, their rows indented again.</summary>
    public static IEnumerable<string> Lines(ToolsFacts facts, AppSettingsData saved, SettingsMenu menu)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(menu);
        yield return ToolsText.OfferedTabTitle;
        foreach (var line in ToolsText.OfferedLines(facts))
        {
            yield return "  " + line;
        }

        for (int t = 1; t < ToolsText.TabTitles.Count; t++)
        {
            yield return ToolsText.TabTitles[t];
            var menus = ToolsText.TabMenus[t - 1];
            if (menus.Count == 1)
            {
                foreach (var field in SettingsMenu.ToolsTabFields[menus[0]])
                {
                    yield return "  " + menu.PlainRow(field, saved);
                }

                continue;
            }

            foreach (int m in menus)
            {
                yield return "  " + ToolsText.MenuTitles[m];
                foreach (var field in SettingsMenu.ToolsTabFields[m])
                {
                    yield return "    " + menu.PlainRow(field, saved);
                }
            }
        }
    }

    /// <param name="midTurn">The pane opened while a reply runs: the flips save and the rows edit as on <c>/settings</c>; a row refused there is refused here (none today).</param>
    /// <returns><see cref="SettingsChanges.Llm"/> when a reconnect row saved (none on the pane since 2026-10-03), else none.</returns>
    public async Task<SettingsChanges> ShowAsync(CancellationToken cancellationToken, bool midTurn = false)
    {
        var changes = SettingsChanges.None;
        if (!_pane.Enabled)
        {
            foreach (var line in Lines(_facts(), _settings.Current, _menu))
            {
                _transcript.Notice(line);
            }

            return changes;
        }

        int tab = 0;
        int cursor = -1;
        string filter = "";
        IReadOnlyList<int>? subs = null;   // each group's menu on show (2026-10-10)
        _menu.Root = ToolsText.Label;
        try
        {
            while (true)
            {
                var facts = _facts();
                var saved = _settings.Current;
                var offered = ToolsText.OfferedRows(facts, filter);
                var page = Page(offered, saved, _menu, tab, filter, facts.Groups.Sum(g => g.Tools.Count), name => ToolsText.ToolFooter(facts, name), subs);
                if (cursor < 0)
                {
                    cursor = ToolsText.FirstToolRow(offered);
                }

                if (await _pane.PickAsync(page, cursor, cancellationToken).ConfigureAwait(false) is not { } pick)
                {
                    return changes;
                }

                tab = pick.Tab;
                cursor = pick.Row;
                subs = _pane.ShownSubs;
                if (pick.Filter is { } typed)
                {
                    // The Offered tab's filter (later on 2026-10-03): the rows again under it, the cursor on the first tool left.
                    filter = typed;
                    cursor = -1;
                    continue;
                }

                if (tab == 0)
                {
                    if (cursor >= offered.Count || offered[cursor].Tool is not { } tool)
                    {
                        continue;   // the off line (a heading or a gap is no stop since 2026-10-03)
                    }

                    bool on = facts.Disabled.Contains(tool);   // off now → on
                    _settings.Update(d => d.ToolsDisabled = ToolsText.Flip(d.ToolsDisabled, tool));
                    Sink.Notice(ToolsText.FlippedNotice(tool, on));
                    continue;
                }

                if (RowField(tab, pick.Sub, cursor) is not { } field)
                {
                    continue;
                }

                // The page on the tab (and the group's menu) the pane ended on, so a typed edit under it keeps those rows (PickSettingAsync's shape).
                var shown = pick.Tab == page.Tab && pick.Sub == page.Sub ? page : Page(offered, saved, _menu, tab, filter, 0, null, subs);
                bool group = ToolsText.IsGroupTab(tab);
                _menu.Root = group ? GroupCrumb(tab) : ToolsText.Label;   // a group's pickers titled Tools › Databases › … (2026-10-09)
                try
                {
                    changes |= await RowAsync(field, pick.Toggle, saved, shown, cursor, midTurn, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _menu.Root = ToolsText.Label;
                }
            }
        }
        finally
        {
            _menu.Root = SettingsMenu.Title;
            _pane.Close();
        }
    }

    /// <summary>
    /// A settings row picked (the Tools pane's tabs, a group's menus): refused under a reply as on <c>/settings</c>, Space a flip
    /// (2026-10-04: a switch flips and saves, any other row does nothing), Enter its edit under <paramref name="shown"/>'s
    /// <paramref name="cursor"/>; <see cref="SettingsChanges.Llm"/> when a reconnect row saved.
    /// </summary>
    private async Task<SettingsChanges> RowAsync(SettingsField field, bool toggle, AppSettingsData saved, MenuPage shown, int cursor, bool midTurn, CancellationToken cancellationToken)
    {
        if (midTurn && SettingsMenu.RefusedMidTurn(field))
        {
            Sink.Notice(SettingsMenu.NotWhileReplyRunsNotice);
            return SettingsChanges.None;
        }

        bool done = toggle
            ? await _menu.FlipAsync(field, cancellationToken).ConfigureAwait(false)
            : await _menu.EditAsync(field, saved, shown, cursor, cancellationToken).ConfigureAwait(false);
        return done && SettingsMenu.IsLlmField(field) ? SettingsChanges.Llm : SettingsChanges.None;
    }

    /// <summary>
    /// The <c>Shell allowed commands</c> row alone, as plain lines for a console without the pane
    /// (later on 2026-09-21, <c>/cmdlist</c>; <c>/tools shell allowed</c> since 2026-10-09): the row's name as a heading, each prefix indented under it,
    /// <see cref="SettingsMenu.NoAllowedCommandsRow"/> while there is none. Pinned.
    /// </summary>
    public static IEnumerable<string> AllowedCommandLines(AppSettingsData saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        yield return SettingsMenu.FieldName(SettingsField.ShellCommandAllowed);
        var allowed = Shell.CommandAllowList.Merge(saved.ShellCommandAllowed, []);
        if (allowed.Count == 0)
        {
            yield return "  " + SettingsMenu.NoAllowedCommandsRow;
            yield break;
        }

        foreach (var prefix in allowed)
        {
            yield return "  " + prefix;
        }
    }

    /// <summary>
    /// <c>/tools shell allowed</c> (<c>/cmdlist</c> until 2026-10-09) and the toolbar's lock (later on 2026-09-21, the user's ask): the Shell tab's
    /// <c>Shell allowed commands</c> row opened straight — the same list under the same crumb,
    /// <c>Tools › Shell allowed commands</c>, Enter removing a prefix — with nothing of the Tools pane
    /// around it, so ESC closes the pane rather than landing on the tab (the user's call: a shortcut,
    /// not a path). Without the pane the list prints (<see cref="AllowedCommandLines"/>). Mid-turn
    /// as at idle: the row is never refused under a reply (<see cref="ShowAsync"/> edits it there too),
    /// so there is no flag to carry. The list's title row switches <c>Shell command policy</c> between off, ask and yolo
    /// (2026-10-02, <see cref="SettingsMenu.CommandPolicyButtons"/>; off since 2026-10-12), yolo after a yes.
    /// </summary>
    public async Task ShowAllowedCommandsAsync(CancellationToken cancellationToken)
    {
        if (!_pane.Enabled)
        {
            foreach (var line in AllowedCommandLines(_settings.Current))
            {
                _transcript.Notice(line);
            }

            return;
        }

        _menu.Root = ToolsText.Label;
        try
        {
            await _menu.EditAllowedCommandsAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _menu.Root = SettingsMenu.Title;
            _pane.Close();
        }
    }

    /// <summary>
    /// <c>/tools shell allowed</c> and <c>/tools shell police</c> (2026-10-09, the user's call: <c>/cmdlist</c> and <c>/police</c>
    /// folded under the shell's switch): <see cref="ShowAllowedCommandsAsync"/> or <see cref="ShowPoliceAsync"/> for a
    /// <see cref="ToolsText.ShellSubWords"/> word.
    /// </summary>
    public Task ShowShellSubAsync(string word, CancellationToken cancellationToken) =>
        string.Equals(word, ToolsText.ShellPoliceWord, StringComparison.OrdinalIgnoreCase)
            ? ShowPoliceAsync(cancellationToken)
            : ShowAllowedCommandsAsync(cancellationToken);

    /// <summary>What <c>/tools shell police</c> (<c>/police</c> until 2026-10-09) prints without the pane: the row's name and its value, <c>Shell police: on</c>. Pinned.</summary>
    public static string PoliceLine(AppSettingsData saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        return SettingsMenu.FieldName(SettingsField.ShellPolice) + ": " + (saved.ShellPolice ? "on" : "off");
    }

    /// <summary>
    /// The <c>Shell police forbidden strings</c> list as plain lines for a console without the pane (2026-10-03, after
    /// <see cref="PoliceLine"/> on <c>/police</c>, <c>/tools shell police</c> since 2026-10-09): the row's name as a heading, each string indented under it,
    /// <see cref="SettingsMenu.NoAllowedCommandsRow"/> while there is none — <see cref="AllowedCommandLines"/>' shape. Pinned.
    /// </summary>
    public static IEnumerable<string> ForbiddenStringLines(AppSettingsData saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        yield return SettingsMenu.FieldName(SettingsField.ShellPoliceForbiddenStrings);
        var forbidden = Shell.ForbiddenStrings.Sorted(saved.ShellPoliceForbiddenStrings);
        if (forbidden.Count == 0)
        {
            yield return "  " + SettingsMenu.NoAllowedCommandsRow;
            yield break;
        }

        foreach (var entry in forbidden)
        {
            yield return "  " + entry;
        }
    }

    /// <summary>
    /// <c>/tools shell police</c> (<c>/police</c> until 2026-10-09) and the toolbar's officer (2026-09-22, the user's ask): the Shell tab's
    /// <c>Shell police</c> row opened straight — its on/off page under the crumb
    /// <c>Tools › Shell police</c> — with nothing of the Tools pane around it, so ESC
    /// closes the pane, as <see cref="ShowAllowedCommandsAsync"/> does for the lock. Picking off asks first
    /// (<see cref="SettingsMenu.PoliceOffConfirmQuestion"/>, 2026-10-02) and then puts the ninja in the officer's place on
    /// the toolbar as the pane closes (the strip follows the switch at each draw). The page's strings button (S, 2026-10-03,
    /// <see cref="SettingsMenu.PoliceButtons"/>) opens the forbidden-strings list and comes back to it.
    /// Without the pane the value prints (<see cref="PoliceLine"/>), then the forbidden strings (<see cref="ForbiddenStringLines"/>).
    /// Mid-turn as at idle: the row is never refused under a reply.
    /// </summary>
    public async Task ShowPoliceAsync(CancellationToken cancellationToken)
    {
        if (!_pane.Enabled)
        {
            _transcript.Notice(PoliceLine(_settings.Current));
            foreach (var line in ForbiddenStringLines(_settings.Current))
            {
                _transcript.Notice(line);
            }

            return;
        }

        _menu.Root = ToolsText.Label;
        try
        {
            await _menu.EditToggleAsync(SettingsField.ShellPolice, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _menu.Root = SettingsMenu.Title;
            _pane.Close();
        }
    }

    /// <summary>
    /// <c>/tools &lt;group&gt;</c> and the toolbar's tool switches (2026-10-03, the user's ask): a group's switch opened straight
    /// (<see cref="ToolsText.SwitchField"/>), <see cref="ShowPoliceAsync"/>'s shape — a tool group's on/off page, or for
    /// <c>shell</c> the <c>Shell command policy</c> picker (yolo after a yes), under the crumb <c>Tools › …</c>, nothing of the
    /// Tools pane around it, so ESC closes the pane. The strip follows the switch at each draw, so the item leaves or takes
    /// its off slab as the pane closes. Without the pane the value prints (<see cref="ToolsText.SwitchStateLine"/>). Mid-turn
    /// as at idle: none of these rows is refused under a reply, and each is read at the next turn. The Web page's title row
    /// switches <c>Web browser mode</c> (2026-10-04, the user's ask: <see cref="SettingsMenu.WebModeButtons"/>, D / H / C).
    /// </summary>
    public async Task ShowSwitchAsync(SettingsField field, CancellationToken cancellationToken)
    {
        if (!_pane.Enabled)
        {
            _transcript.Notice(ToolsText.SwitchStateLine(field, _settings.Current));
            if (field == SettingsField.ShellCommandPolicy)
            {
                // The picker's two buttons as lines too (2026-10-09): the allowed commands, the police and its strings.
                foreach (var line in AllowedCommandLines(_settings.Current).Append(PoliceLine(_settings.Current)).Concat(ForbiddenStringLines(_settings.Current)))
                {
                    _transcript.Notice(line);
                }
            }

            return;
        }

        _menu.Root = ToolsText.Label;
        try
        {
            _ = field == SettingsField.ShellCommandPolicy
                ? await _menu.EditCommandPolicyAsync(cancellationToken).ConfigureAwait(false)
                : await _menu.EditToggleAsync(field, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _menu.Root = SettingsMenu.Title;
            _pane.Close();
        }
    }
}
