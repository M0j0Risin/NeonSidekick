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
/// one exception is the Claude tab's four Claude API rows (2026-09-29, off <c>/settings</c>): each is a reconnect
/// (<see cref="SettingsMenu.IsLlmField"/>), refused mid-turn like there, and <see cref="ShowAsync"/> returns
/// <see cref="SettingsChanges.Llm"/> when one saved, so the screen reconnects once the pane closes — <c>/mcp</c>'s shape.
/// Without the pane the tabs print as plain lines. <c>/tools expand</c> and <c>/tools collapse</c> (2026-09-22) became the
/// root <c>/expand</c> and <c>/collapse</c> later that day, the user's ask; since 2026-10-03 <c>/tools &lt;group&gt;</c> opens one
/// group's switch straight (<see cref="ShowSwitchAsync"/>, the toolbar's tool switches).
/// The Shell tab's allowed-commands row has a door of its own since later on 2026-09-21:
/// <see cref="ShowAllowedCommandsAsync"/> (<c>/cmdlist</c>, the toolbar's lock glyph); its <c>Shell police outside paths</c>
/// row since 2026-09-22: <see cref="ShowPoliceAsync"/> (<c>/police</c>, the toolbar's officer).
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
    /// </summary>
    public static MenuPage Page(IReadOnlyList<(string Markup, string? Tool, bool Heading)> offered, AppSettingsData saved, SettingsMenu menu, int tab, string filter = "", int total = 0)
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
            tabs[t] = menu.FieldsTab(ToolsText.TabTitles[t], SettingsMenu.ToolsTabFields[t - 1], saved);
        }

        return MenuPage.Tabbed(ToolsText.Label, tabs, tab, SettingsMenu.TabKeys) with { SpaceToggles = true, TabCursors = [ToolsText.FirstToolRow(offered), 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0] };
    }

    /// <summary>The nine tabs as plain lines, for a console without the pane: each tab's title as a heading, its content indented.</summary>
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
            foreach (var field in SettingsMenu.ToolsTabFields[t - 1])
            {
                yield return "  " + menu.PlainRow(field, saved);
            }
        }
    }

    /// <param name="midTurn">The pane opened while a reply runs: the flips save and the rows edit as on <c>/settings</c>; a row refused there is refused here (none today).</param>
    /// <returns><see cref="SettingsChanges.Llm"/> when a reconnect row (a Claude API row) saved, else none.</returns>
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
        _menu.Root = ToolsText.Label;
        try
        {
            while (true)
            {
                var facts = _facts();
                var saved = _settings.Current;
                var offered = ToolsText.OfferedRows(facts, filter);
                var page = Page(offered, saved, _menu, tab, filter, facts.Groups.Sum(g => g.Tools.Count));
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

                if (pick.Toggle)
                {
                    continue;   // Space on a settings row: nothing, as on /settings
                }

                var fields = SettingsMenu.ToolsTabFields[tab - 1];
                if (cursor >= fields.Count)
                {
                    continue;
                }

                var field = fields[cursor];
                if (midTurn && SettingsMenu.RefusedMidTurn(field))
                {
                    Sink.Notice(SettingsMenu.NotWhileReplyRunsNotice);
                    continue;
                }

                // The page on the tab the pane ended on, so a typed edit under it keeps that tab's rows (PickSettingAsync's shape).
                var shown = pick.Tab == page.Tab ? page : MenuPage.Tabbed(ToolsText.Label, page.Tabs!, pick.Tab, SettingsMenu.TabKeys);
                if (await _menu.EditAsync(field, saved, shown, cursor, cancellationToken).ConfigureAwait(false) && SettingsMenu.IsLlmField(field))
                {
                    changes |= SettingsChanges.Llm;
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
    /// The <c>Shell allowed commands</c> row alone, as plain lines for a console without the pane
    /// (later on 2026-09-21, <c>/cmdlist</c>): the row's name as a heading, each prefix indented under it,
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
    /// <c>/cmdlist</c> and the toolbar's lock (later on 2026-09-21, the user's ask): the Shell tab's
    /// <c>Shell allowed commands</c> row opened straight — the same list under the same crumb,
    /// <c>Tools › Shell allowed commands</c>, Enter removing a prefix — with nothing of the Tools pane
    /// around it, so ESC closes the pane rather than landing on the tab (the user's call: a shortcut,
    /// not a path). Without the pane the list prints (<see cref="AllowedCommandLines"/>). Mid-turn
    /// as at idle: the row is never refused under a reply (<see cref="ShowAsync"/> edits it there too),
    /// so there is no flag to carry. The list's title row switches <c>Shell command policy</c> between ask and yolo
    /// (2026-10-02, <see cref="SettingsMenu.CommandPolicyButtons"/>), yolo after a yes.
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

    /// <summary>What <c>/police</c> prints without the pane: the row's name and its value, <c>Shell police outside paths: on</c>. Pinned.</summary>
    public static string PoliceLine(AppSettingsData saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        return SettingsMenu.FieldName(SettingsField.ShellPoliceOutsidePaths) + ": " + (saved.ShellPoliceOutsidePaths ? "on" : "off");
    }

    /// <summary>
    /// The <c>Shell police forbidden strings</c> list as plain lines for a console without the pane (2026-10-03, after
    /// <see cref="PoliceLine"/> on <c>/police</c>): the row's name as a heading, each string indented under it,
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
    /// <c>/police</c> and the toolbar's officer (2026-09-22, the user's ask): the Shell tab's
    /// <c>Shell police outside paths</c> row opened straight — its on/off page under the crumb
    /// <c>Tools › Shell police outside paths</c> — with nothing of the Tools pane around it, so ESC
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
            await _menu.EditToggleAsync(SettingsField.ShellPoliceOutsidePaths, cancellationToken).ConfigureAwait(false);
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
    /// as at idle: none of these rows is refused under a reply, and each is read at the next turn.
    /// </summary>
    public async Task ShowSwitchAsync(SettingsField field, CancellationToken cancellationToken)
    {
        if (!_pane.Enabled)
        {
            _transcript.Notice(ToolsText.SwitchStateLine(field, _settings.Current));
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
