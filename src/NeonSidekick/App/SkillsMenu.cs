using NeonSidekick.Sessions;
using NeonSidekick.Settings;
using NeonSidekick.Skills;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// The <c>/skills</c> screen (2026-09-18, the user's ask; the plural since 2026-09-19): the three tabs in <see cref="SkillsText"/>'s
/// words — Offered, Reflection, Options (the Roots tab after them until later on 2026-09-19, the user's call; the
/// scope page names each root's folder; a Project tab before Options from later on 2026-09-19 until 2026-10-01) — as one tabbed <see cref="MenuPane"/> page, so a long catalog
/// scrolls behind the menu's viewport and every skill row is a cursor stop. The Options tab (2026-09-19,
/// the user's ask) and the Reflection tab (later that day) are the settings rows that were <c>/settings</c>' Skills tab
/// (<see cref="SettingsMenu.SkillsTabFields"/>), edited through <see cref="SettingsMenu"/>'s own seams
/// (<see cref="SettingsMenu.FieldsTab"/>, <see cref="SettingsMenu.EditAsync"/>) under this pane's strip, its
/// pickers titled <c>Skills › …</c> (<see cref="SettingsMenu.Root"/>) like the scope page — the <see cref="ToolsMenu"/>
/// shape; none of its rows reconnects or is <see cref="SettingsMenu.RefusedMidTurn"/>, so they edit mid-turn too,
/// and the facts are read again after an edit (the skills switch empties the Offered tab). The <c>Project file</c> toggle
/// is an Options row since 2026-10-01 (the user's ask, <see cref="SettingsField.ProjectFile"/>): the one row of a Project tab
/// of its own, flipped by hand here, from later on 2026-09-19 until then. Enter or a double-click on
/// a skill's row (a loaded one, or a shadowed one — the duplicate is the thing to clean up) opens the
/// scope page under the list: <c>profile</c>, <c>global</c>, <c>rename</c> (2026-09-21, the user's ask), <c>edit</c>
/// (2026-09-23, the user's ask: it opens the skill's <c>SKILL.md</c> in the editor, the status line saying so, and took
/// over from <c>/skills edit &lt;name&gt;</c>, which went) and <c>delete</c> (always, since 2026-09-23, the user's call;
/// behind the <c>Allow skill delete</c> setting from 2026-09-18 until then, which went), the cursor on the scope it is in. Picking the other root moves the folder
/// (<see cref="SkillEditor.Move"/>) after a yes/no confirmation kept under the list; picking
/// <c>delete</c> removes it (<see cref="SkillEditor.Delete"/>) after one; the scope it is in already
/// is <see cref="SettingsMenu.UnchangedNotice"/>. Picking <c>rename</c> opens the typed slot under the
/// page with the name pre-filled (the <see cref="SessionsMenu"/> rename's shape): what is typed is
/// forced to a skill name (<see cref="KebabName"/>: lower case, hyphens between the words), refused
/// when it is already a skill's name in any root (<see cref="RenameExistsError"/>) or nothing survives
/// (<see cref="RenameEmptyError"/>), and the folder and its <c>name</c> line follow
/// (<see cref="SkillEditor.Rename"/>) with no confirmation — nothing is lost. A destination that already holds the folder's name
/// is refused ahead of the confirmation (<see cref="ExistsError"/>), an external skill is read-only
/// (<see cref="ExternalReadOnlyNotice"/>), and mid-turn — the pane opens on the watcher task while a
/// reply runs — the pick is refused (<see cref="SettingsMenu.NotWhileReplyRunsNotice"/>: a move under
/// a running turn could race the model's <c>load_skill</c>). After a change the facts are read again
/// (a rescan) and the list re-shown with the notice on the status line. Enter on any other row — a
/// heading, a warning, a skipped folder — does nothing, and so does Space on the Offered tab. The
/// <see cref="QueueMenu"/> shape: no prompt fallback, a console without the pane gets
/// <see cref="Lines"/> in the transcript — the three tabs as headed sections.
/// </summary>
internal sealed class SkillsMenu
{
    // The key hints. Pinned.
    public const string LoadedKeys = "Enter = move, rename, edit or delete · ←/→ tabs · ESC = close";
    public const string OtherKeys = "←/→ tabs · ESC = close";
    public const string ScopeKeys = SettingsMenu.PickKeys;

    /// <summary>The scope page's last row, always offered since 2026-09-23 (behind <c>Allow skill delete</c> until then). Pinned.</summary>
    public const string DeleteWord = "delete";

    /// <summary>The scope page's row after the two roots (2026-09-21). Pinned.</summary>
    public const string RenameWord = "rename";

    /// <summary>The scope page's row after rename (2026-09-23): the skill's <c>SKILL.md</c> opened in the editor. Pinned.</summary>
    public const string EditWord = "edit";

    /// <summary>What a declined confirmation says on the status line: the transcript's word.</summary>
    public const string KeptNotice = ChatScreen.KeptNotice;

    public const string ExternalReadOnlyNotice = "(" + NoticeGlyphs.Skill + "external skills are read only here; move the folder by hand)";

    /// <summary>The scope page's two root rows, in the roots' precedence order; rename, edit and <see cref="DeleteWord"/> after them.</summary>
    public static readonly IReadOnlyList<SkillScope> ScopeRows = [SkillScope.Profile, SkillScope.Global];

    private readonly Func<SkillsFacts> _facts;
    private readonly AppSettings _settings;
    private readonly SettingsMenu _menu;
    private readonly INoticeSink _transcript;
    private readonly MenuPane _pane;
    private readonly InputLine _input;
    private readonly Action<string> _openFile;
    private readonly Func<string, string?> _usage;
    private readonly SkillRecords? _records;

    /// <param name="facts">The catalog as of a fresh scan and the rest the tabs show; read when the list opens and again after every change.</param>
    /// <param name="settings">The store the Options tab's rows show and save to.</param>
    /// <param name="menu">The settings menu whose rows the Options tab is.</param>
    /// <param name="transcript">Where the lines outside the pane go: the screen's deferring sink, since the list may open while a reply runs.</param>
    /// <param name="pane">The menu host in the bottom pane.</param>
    /// <param name="input">The line the rename's new name is typed on, under the page (2026-09-21).</param>
    /// <param name="openFile">Opens a file in the user's editor: the <c>edit</c> row's <c>SKILL.md</c> (2026-09-23; the screen's <c>/profile edit</c> seam).</param>
    /// <param name="usage">The scope page's caption for a skill by name (<see cref="UsageCaption"/>; the session store's usage line, 2026-09-19), null for none — read when the page opens; tests pass nothing.</param>
    /// <param name="records">The skill records (2026-09-30): a move, a rename and a delete keep them in step. Null for none.</param>
    public SkillsMenu(Func<SkillsFacts> facts, AppSettings settings, SettingsMenu menu, INoticeSink transcript, MenuPane pane, InputLine input, Action<string> openFile, Func<string, string?>? usage = null, SkillRecords? records = null)
    {
        _records = records;
        _facts = facts ?? throw new ArgumentNullException(nameof(facts));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _menu = menu ?? throw new ArgumentNullException(nameof(menu));
        _transcript = transcript ?? throw new ArgumentNullException(nameof(transcript));
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _openFile = openFile ?? throw new ArgumentNullException(nameof(openFile));
        _usage = usage ?? (_ => null);
    }

    /// <summary>
    /// The scope page's caption (2026-09-19): how the stored sessions used the skill, in the store's
    /// words (<see cref="SkillText.UsageLine"/>); null while <c>Session logging</c> is off, so the
    /// page shows none. Read on open, three light queries.
    /// </summary>
    public static string? UsageCaption(SessionStore store, string name, bool logging, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(zone);
        if (!logging)
        {
            return null;
        }

        var usage = store.SkillUsageOf(name);
        var mark = store.LastReflectionOf(name);
        int writes = mark is null ? 0 : store.ReflectionWrites(name);
        return SkillText.UsageLine(usage, mark, writes, zone);
    }

    /// <summary>Where a notice goes: the pane's status line while the list is open there, else the transcript.</summary>
    private INoticeSink Sink => _pane.IsOpen ? _pane : _transcript;

    // ── Pinned statics ──────────────────────────────────────────────────────

    /// <summary>The scope page's title: <c>Skills › haiku</c>.</summary>
    public static string ScopeTitle(string name) => SkillsText.Label + " › " + name;

    /// <summary>A scope row: the name padded to nine, the root's folder dim after it.</summary>
    public static string ScopeRow(SkillScope scope, SkillRoots roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        return Markup.Escape(SkillScopes.Name(scope).PadRight(9)) + Theme.DimMarkup(roots.Of(scope));
    }

    /// <summary>The rename row (2026-09-21): the word padded to nine, what it does dim after it.</summary>
    public static string RenameRow => Markup.Escape(RenameWord.PadRight(9)) + Theme.DimMarkup("give it a new name (letters, digits and hyphens)");

    /// <summary>The edit row (2026-09-23): the word padded to nine, what it does dim after it.</summary>
    public static string EditRow => Markup.Escape(EditWord.PadRight(9)) + Theme.DimMarkup("open its SKILL.md in your editor");

    /// <summary>What the status line says once the edit row opened the file (<c>/skills edit</c>'s words until 2026-09-23). Pinned.</summary>
    public static string EditOpenedNotice(string name, string path) => $"({NoticeGlyphs.Skill}opened skill \"{name}\"'s SKILL.md in your editor: {path})";

    /// <summary>The editor could not be started for the edit row. Pinned.</summary>
    public static string EditFailedError(string detail) => $"Could not open the SKILL.md: {detail}";

    /// <summary>The delete row: the word padded to nine, what it does dim after it.</summary>
    public static string DeleteRow => Markup.Escape(DeleteWord.PadRight(9)) + Theme.DimMarkup("remove the folder and everything in it");

    public static string RenamedNotice(string name, string newName) => $"({NoticeGlyphs.Skill}renamed: {name} → {newName})";

    /// <summary>The name typed is a skill's already, in <paramref name="where"/>'s root — the catalog's or the disk's word.</summary>
    public static string RenameExistsError(string name, string newName, SkillScope where) => $"Could not rename skill '{name}' to '{newName}': the {SkillScopes.Name(where)} skills already hold it";

    public const string RenameEmptyError = "Could not rename the skill: the name needs at least one letter or digit";

    public static string RenameFailedError(string detail) => $"Could not rename the skill: {detail}";

    /// <summary>
    /// The typed name as a skill name (2026-09-21): lower case, every run of anything but a–z and 0–9
    /// one hyphen, no hyphen at either end, cut to <see cref="SkillFrontmatter.MaxNameLength"/> (and
    /// trimmed again). Empty when nothing survives; otherwise always <see cref="SkillFrontmatter.IsValidName"/>. Pure.
    /// </summary>
    public static string KebabName(string typed)
    {
        ArgumentNullException.ThrowIfNull(typed);
        var kebab = new System.Text.StringBuilder(typed.Length);
        bool gap = false;
        foreach (char c in typed.ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                if (gap && kebab.Length > 0)
                {
                    kebab.Append('-');
                }

                kebab.Append(c);
                gap = false;
            }
            else
            {
                gap = true;
            }
        }

        string name = kebab.ToString();
        return name.Length > SkillFrontmatter.MaxNameLength ? name[..SkillFrontmatter.MaxNameLength].TrimEnd('-') : name;
    }

    public static string MovePrompt(string name, SkillScope from, SkillScope to) => $"Move skill '{name}' from the {SkillScopes.Name(from)} skills to the {SkillScopes.Name(to)} skills?";

    public static string MovedNotice(string name, SkillScope to) => $"({NoticeGlyphs.Skill}moved: {name} → {SkillScopes.Name(to)} skills)";

    /// <summary>The destination root already holds the folder — by the folder's name, which a skill's name may differ from.</summary>
    public static string ExistsError(string name, string folder, SkillScope to) => $"Could not move skill '{name}': the {SkillScopes.Name(to)} skills already hold '{folder}'";

    public static string MoveFailedError(string detail) => $"Could not move the skill: {detail}";

    public static string DeletePrompt(string name, SkillScope scope) => $"Delete skill '{name}' from the {SkillScopes.Name(scope)} skills, folder and all?";

    public static string DeletedNotice(string name, SkillScope scope) => $"({NoticeGlyphs.Skill}deleted: {name} from the {SkillScopes.Name(scope)} skills)";

    public static string DeleteFailedError(string detail) => $"Could not delete the skill: {detail}";

    /// <summary>The folder went between the scan and the act (or a hand-built record points elsewhere): the list is read again.</summary>
    public static string MissingError(string name) => $"Could not find skill '{name}' on disk any more; the list was read again";

    /// <summary>The Reflection tab's index in the strip: the reflection's rows, second after Offered (third, after Options, from later on 2026-09-19 until 2026-09-22).</summary>
    public const int ReflectionTab = 1;

    /// <summary>The Options tab's index in the strip: the skill settings, last since 2026-09-22 (the user's ask; second, after Offered, from 2026-09-19), third since the Project tab went (2026-10-01).</summary>
    public const int OptionsTab = 2;

    /// <summary>The settings tabs' titles, indexed like <see cref="SettingsMenu.SkillsTabFields"/> (Options, then Reflection; the strip puts Options last).</summary>
    public static readonly IReadOnlyList<string> SettingsTabTitles = [SkillsText.OptionsTabTitle, SkillsText.ReflectionTabTitle];

    /// <summary>The <see cref="SettingsMenu.SkillsTabFields"/> list a settings tab shows: Options' first, Reflection's second.</summary>
    private static IReadOnlyList<SettingsField> SettingsFields(int tab) => SettingsMenu.SkillsTabFields[tab == OptionsTab ? 0 : 1];

    /// <summary>The tabbed page: the Offered rows first, the Reflection rows (<see cref="SettingsMenu.FieldsTab"/> under <see cref="SettingsMenu.TabKeys"/>) second, the Options rows last (2026-09-22); Space is nothing anywhere since the Project tab, its one flip, went (2026-10-01), as on <c>/settings</c>.</summary>
    public static MenuPage Page(SkillsFacts facts, IReadOnlyList<(string Markup, Skill? Skill)> loaded, AppSettingsData saved, SettingsMenu menu, int tab)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(menu);
        var tabs = new MenuTab[]
        {
            new(SkillsText.OfferedTabTitle, loaded.Select(r => r.Markup).ToList()) { Hint = LoadedKeys },
            menu.FieldsTab(SkillsText.ReflectionTabTitle, SettingsFields(ReflectionTab), saved) with { Hint = SettingsMenu.TabKeys },
            menu.FieldsTab(SkillsText.OptionsTabTitle, SettingsFields(OptionsTab), saved) with { Hint = SettingsMenu.TabKeys },
        };
        return MenuPage.Tabbed(SkillsText.Label, tabs, tab, OtherKeys) with { TabCursors = [0, 0, 0] };
    }

    /// <summary>The three tabs as plain lines, for a console without the pane, in the strip's order: <see cref="SkillsText.Lines"/>, then the Reflection rows and the Options rows (<see cref="SettingsMenu.PlainRow"/>), each headed and indented.</summary>
    public static IEnumerable<string> Lines(SkillsFacts facts, AppSettingsData saved, SettingsMenu menu)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(menu);
        foreach (var line in SkillsText.Lines(facts)
            .Concat(Section(ReflectionTab, SkillsText.ReflectionTabTitle))
            .Concat(Section(OptionsTab, SkillsText.OptionsTabTitle)))
        {
            yield return line;
        }

        IEnumerable<string> Section(int tab, string title) =>
            new[] { title }.Concat(SettingsFields(tab).Select(field => "  " + menu.PlainRow(field, saved)));
    }

    // ── Screen ──────────────────────────────────────────────────────────────

    /// <param name="midTurn">The pane opened while a reply runs: the list shows, a scope pick is refused; the Options and Reflection rows edit as on <c>/settings</c> (none is refused there; read at the next turn).</param>
    public async Task ShowAsync(CancellationToken cancellationToken, bool midTurn = false)
    {
        var facts = _facts();
        if (!_pane.Enabled)
        {
            foreach (var line in Lines(facts, _settings.Current, _menu))
            {
                _transcript.Notice(line);
            }

            return;
        }

        int tab = 0;
        int cursor = 0;
        _menu.Root = SkillsText.Label;
        try
        {
            while (true)
            {
                var loaded = SkillsText.LoadedRows(facts);
                var saved = _settings.Current;
                var page = Page(facts, loaded, saved, _menu, tab);
                var picked = await _pane.PickAsync(page, cursor, cancellationToken).ConfigureAwait(false);
                if (picked is not { } pick)
                {
                    return;
                }

                tab = pick.Tab;
                cursor = pick.Row;
                if (tab is OptionsTab or ReflectionTab)
                {
                    var fields = SettingsFields(tab);
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

                    // The page on the tab the pane ended on, so a typed edit under it keeps that tab's rows (ToolsMenu's shape).
                    var shown = pick.Tab == page.Tab ? page : MenuPage.Tabbed(SkillsText.Label, page.Tabs!, pick.Tab, OtherKeys);
                    if (await _menu.EditAsync(field, saved, shown, cursor, cancellationToken).ConfigureAwait(false))
                    {
                        facts = _facts();   // the skills switch empties the Offered tab
                    }

                    continue;
                }

                if (tab != 0 || cursor >= loaded.Count || loaded[cursor].Skill is not { } skill)
                {
                    continue;
                }

                if (midTurn)
                {
                    Sink.Notice(SettingsMenu.NotWhileReplyRunsNotice);
                    continue;
                }

                if (skill.Scope == SkillScope.External)
                {
                    Sink.Notice(ExternalReadOnlyNotice);
                    continue;
                }

                if (await PickScopeAsync(skill, facts, cancellationToken).ConfigureAwait(false))
                {
                    facts = _facts();
                    cursor = Math.Max(0, Math.Min(cursor, SkillsText.LoadedRows(facts).Count - 1));
                }
            }
        }
        finally
        {
            _menu.Root = SettingsMenu.Title;
            _pane.Close();
        }
    }

    /// <summary>The scope page under the list — the two roots, rename, edit, delete —, the confirmation or the typed slot under that, then the act; true when the folder changed (the facts are stale).</summary>
    private async Task<bool> PickScopeAsync(Skill skill, SkillsFacts facts, CancellationToken cancellationToken)
    {
        var roots = facts.Roots;
        var rows = ScopeRows.Select(scope => ScopeRow(scope, roots)).ToList();
        rows.Add(RenameRow);
        rows.Add(EditRow);
        rows.Add(DeleteRow);

        var page = new MenuPage(ScopeTitle(skill.Name), rows, ScopeKeys) { Caption = _usage(skill.Name) };
        var picked = await _pane.PickAsync(page, IndexOf(ScopeRows, skill.Scope), cancellationToken).ConfigureAwait(false);
        if (picked is not { Row: var row })
        {
            return false;
        }

        if (row == ScopeRows.Count)
        {
            return await RenameAsync(skill, facts, page, row, cancellationToken).ConfigureAwait(false);
        }

        if (row == ScopeRows.Count + 1)
        {
            Edit(skill);
            return false;
        }

        if (row > ScopeRows.Count + 1)
        {
            if (!await ConfirmAsync(DeletePrompt(skill.Name, skill.Scope), cancellationToken).ConfigureAwait(false))
            {
                Sink.Notice(KeptNotice);
                return false;
            }

            var deleted = SkillEditor.Delete(roots, skill);
            switch (deleted.Outcome)
            {
                case SkillEditOutcome.Deleted:
                    _records?.Deleted(skill.Scope, skill.FolderName);
                    Sink.Notice(DeletedNotice(skill.Name, skill.Scope));
                    return true;
                case SkillEditOutcome.Missing:
                    Sink.Error(MissingError(skill.Name));
                    return true;
                default:
                    Sink.Error(DeleteFailedError(deleted.Detail));
                    return false;
            }
        }

        var to = ScopeRows[row];
        if (to == skill.Scope)
        {
            Sink.Notice(SettingsMenu.UnchangedNotice);
            return false;
        }

        // Refused ahead of the question: nothing to confirm when the destination holds the name already.
        string destination = Path.Combine(roots.Of(to), skill.FolderName);
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            Sink.Error(ExistsError(skill.Name, skill.FolderName, to));
            return false;
        }

        if (!await ConfirmAsync(MovePrompt(skill.Name, skill.Scope, to), cancellationToken).ConfigureAwait(false))
        {
            Sink.Notice(KeptNotice);
            return false;
        }

        var moved = SkillEditor.Move(roots, skill, to);
        switch (moved.Outcome)
        {
            case SkillEditOutcome.Moved:
                _records?.Moved(skill, to);
                Sink.Notice(MovedNotice(skill.Name, to));
                return true;
            case SkillEditOutcome.Exists:
                Sink.Error(ExistsError(skill.Name, skill.FolderName, to));
                return true;
            case SkillEditOutcome.Missing:
                Sink.Error(MissingError(skill.Name));
                return true;
            default:
                Sink.Error(MoveFailedError(moved.Detail));
                return false;
        }
    }

    /// <summary>The rename's slot under the page, the name checked and the act (2026-09-21); true when the folder changed.</summary>
    private async Task<bool> RenameAsync(Skill skill, SkillsFacts facts, MenuPage page, int row, CancellationToken cancellationToken)
    {
        var typed = await _pane.EditAsync(page with { Hint = SettingsMenu.EditKeys }, row, _input, skill.Name, allowEmpty: false, cancellationToken).ConfigureAwait(false);
        if (typed is not InputResult.Submitted { Text: var text } || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string name = KebabName(text);
        if (name.Length == 0)
        {
            Sink.Error(RenameEmptyError);
            return false;
        }

        if (string.Equals(name, skill.Name, StringComparison.Ordinal) && string.Equals(name, skill.FolderName, StringComparison.Ordinal))
        {
            Sink.Notice(SettingsMenu.UnchangedNotice);
            return false;
        }

        // Refused ahead of the act when the catalog knows the name already — a loaded skill or a shadowed one, in any root.
        if (facts.Skills.Concat(facts.Shadowed).FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal)) is { } taken)
        {
            Sink.Error(RenameExistsError(skill.Name, name, taken.Scope));
            return false;
        }

        var renamed = SkillEditor.Rename(facts.Roots, skill, name);
        switch (renamed.Outcome)
        {
            case SkillEditOutcome.Renamed:
                _records?.Renamed(skill, name);
                Sink.Notice(RenamedNotice(skill.Name, name));
                return true;
            case SkillEditOutcome.Exists or SkillEditOutcome.ExistsElsewhere or SkillEditOutcome.ExternalReadOnly:
                Sink.Error(RenameExistsError(skill.Name, name, renamed.Scope));
                return true;
            case SkillEditOutcome.Missing:
                Sink.Error(MissingError(skill.Name));
                return true;
            case SkillEditOutcome.BadName:
                Sink.Error(RenameEmptyError);
                return false;
            default:
                Sink.Error(RenameFailedError(renamed.Detail));
                return renamed.Outcome != SkillEditOutcome.Unparseable;   // a failed write after the move: the list shows the renamed folder
        }
    }

    /// <summary>
    /// The edit row (2026-09-23, the user's ask; <c>/skills edit &lt;name&gt;</c> until then): the skill's <c>SKILL.md</c>
    /// opened in the editor, the status line saying so or why not. Nothing changes on disk here, so no rescan: the body
    /// is read at activation, and the edit shows on the next load.
    /// </summary>
    private void Edit(Skill skill)
    {
        try
        {
            _openFile(skill.FilePath);
            Sink.Notice(EditOpenedNotice(skill.Name, skill.FilePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Sink.Error(EditFailedError(ex.Message));
        }
    }

    private static int IndexOf(IReadOnlyList<SkillScope> scopes, SkillScope scope)
    {
        for (int i = 0; i < scopes.Count; i++)
        {
            if (scopes[i] == scope)
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>The yes/no page kept under the list (<see cref="SettingsMenu.ConfirmAsync"/>'s rows, keys and hotkeys, the cursor on No): true for Enter on Yes alone.</summary>
    private async Task<bool> ConfirmAsync(string question, CancellationToken cancellationToken)
    {
        var page = new MenuPage(question, SettingsMenu.ConfirmRows, SettingsMenu.ConfirmKeys) { Hotkeys = SettingsMenu.ConfirmHotkeys };
        var picked = await _pane.PickAsync(page, 0, cancellationToken).ConfigureAwait(false);
        return picked is { Row: 1 };
    }
}
