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
/// per-tool values still flip and save. Nothing here reconnects or clears the conversation: every
/// flip is read at the next turn (<see cref="ChatScreen.PrepareTurn"/>), so the pane opens mid-turn
/// too and edits as <c>/settings</c> does there (none of its rows is <see cref="SettingsMenu.RefusedMidTurn"/>).
/// Without the pane the tabs print as plain lines. <c>/tools</c> takes no argument: <c>/tools expand</c> and
/// <c>/tools collapse</c> (2026-09-22) became the root <c>/expand</c> and <c>/collapse</c> later that day, the user's ask.
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
    /// tool row past its heading.
    /// </summary>
    public static MenuPage Page(IReadOnlyList<(string Markup, string? Tool)> offered, AppSettingsData saved, SettingsMenu menu, int tab)
    {
        ArgumentNullException.ThrowIfNull(offered);
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(menu);
        var tabs = new MenuTab[ToolsText.TabTitles.Count];
        tabs[0] = new MenuTab(ToolsText.OfferedTabTitle, offered.Select(r => r.Markup).ToList()) { Hint = ToolsText.OfferedKeys };
        for (int t = 1; t < tabs.Length; t++)
        {
            tabs[t] = menu.FieldsTab(ToolsText.TabTitles[t], SettingsMenu.ToolsTabFields[t - 1], saved);
        }

        return MenuPage.Tabbed(ToolsText.Label, tabs, tab, SettingsMenu.TabKeys) with { SpaceToggles = true, TabCursors = [ToolsText.FirstToolRow(offered), 0, 0, 0, 0, 0, 0, 0, 0, 0, 0] };
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
    public async Task ShowAsync(CancellationToken cancellationToken, bool midTurn = false)
    {
        if (!_pane.Enabled)
        {
            foreach (var line in Lines(_facts(), _settings.Current, _menu))
            {
                _transcript.Notice(line);
            }

            return;
        }

        int tab = 0;
        int cursor = -1;
        _menu.Root = ToolsText.Label;
        try
        {
            while (true)
            {
                var facts = _facts();
                var saved = _settings.Current;
                var offered = ToolsText.OfferedRows(facts);
                var page = Page(offered, saved, _menu, tab);
                if (cursor < 0)
                {
                    cursor = ToolsText.FirstToolRow(offered);
                }

                if (await _pane.PickAsync(page, cursor, cancellationToken).ConfigureAwait(false) is not { } pick)
                {
                    return;
                }

                tab = pick.Tab;
                cursor = pick.Row;
                if (tab == 0)
                {
                    if (cursor >= offered.Count || offered[cursor].Tool is not { } tool)
                    {
                        continue;   // a heading, the off line
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
                await _menu.EditAsync(field, saved, shown, cursor, cancellationToken).ConfigureAwait(false);
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
    /// so there is no flag to carry.
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
    /// <c>/police</c> and the toolbar's officer (2026-09-22, the user's ask): the Shell tab's
    /// <c>Shell police outside paths</c> row opened straight — its on/off page under the crumb
    /// <c>Tools › Shell police outside paths</c> — with nothing of the Tools pane around it, so ESC
    /// closes the pane, as <see cref="ShowAllowedCommandsAsync"/> does for the lock. Picking off takes
    /// the officer off the toolbar as the pane closes (the strip follows the switch at each draw).
    /// Without the pane the value prints (<see cref="PoliceLine"/>). Mid-turn as at idle: the row is
    /// never refused under a reply.
    /// </summary>
    public async Task ShowPoliceAsync(CancellationToken cancellationToken)
    {
        if (!_pane.Enabled)
        {
            _transcript.Notice(PoliceLine(_settings.Current));
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
}
