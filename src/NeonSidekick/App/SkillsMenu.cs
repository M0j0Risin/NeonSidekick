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
/// over from <c>/skills edit &lt;name&gt;</c>, which went), <c>revert</c> (2026-10-02; since 2026-10-04, the user's call, always offered and
/// the only way in, <c>/skills revert</c> gone: it lists every kept version, newest first, and the one picked is put back, the current
/// text kept first so nothing is lost, <see cref="SkillRecords.Restore"/>) and <c>delete</c> (always, since 2026-09-23, the user's call;
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
    public const string LoadedKeys = "Enter = move, rename, edit, revert or delete · ←/→ tabs · " + MenuFilter.TypeAndCloseKeys;   // the filter since 2026-10-03
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
    private readonly Func<Skill, IReadOnlyList<SkillRevision>>? _versions;
    private readonly Func<Skill, SkillRevision, SkillRevert>? _restore;
    private readonly Func<Skill, SkillRevision?, int>? _forget;

    /// <summary>
    /// The revert list's buttons (2026-10-07, the user's pick): the highlighted version removed, or every one, each after a yes. The remove
    /// is the saved list's <c>✖  remove</c>, two spaces (later that day, the user's report: Windows Terminal draws ✖ two cells wide over
    /// one space, so it read "✖remove", as <c>/process</c>' kill did).
    /// </summary>
    public static readonly IReadOnlyList<MenuButton> VersionButtons = [new(YouTube.YouTubeText.RemoveButton, 'd'), new(QueueMenu.ClearAllButton, QueueMenu.ClearAllKey)];

    /// <summary>The revert list's hint with its buttons (2026-10-07). Pinned.</summary>
    public const string VersionsKeys = "Enter = put back · d = remove · c = clear all · ESC = back";

    /// <param name="facts">The catalog as of a fresh scan and the rest the tabs show; read when the list opens and again after every change.</param>
    /// <param name="settings">The store the Options tab's rows show and save to.</param>
    /// <param name="menu">The settings menu whose rows the Options tab is.</param>
    /// <param name="transcript">Where the lines outside the pane go: the screen's deferring sink, since the list may open while a reply runs.</param>
    /// <param name="pane">The menu host in the bottom pane.</param>
    /// <param name="input">The line the rename's new name is typed on, under the page (2026-09-21).</param>
    /// <param name="openFile">Opens a file in the user's editor: the <c>edit</c> row's <c>SKILL.md</c> (2026-09-23; the screen's <c>/profile edit</c> seam).</param>
    /// <param name="usage">The scope page's caption for a skill by name (<see cref="UsageCaption"/>; the session store's usage line, 2026-09-19), null for none — read when the page opens; tests pass nothing.</param>
    /// <param name="records">The skill records (2026-09-30): a move, a rename and a delete keep them in step. Null for none.</param>
    /// <param name="versions">The <c>revert</c> row's list (2026-10-04, the screen's: a reconcile, then <see cref="SkillRecords.Revisions"/>); null, or no <paramref name="restore"/>, = no row.</param>
    /// <param name="restore">The version picked put back (the screen's <see cref="SkillRecords.Restore"/>).</param>
    /// <param name="forget">A kept version forgotten, or every one for null (the screen's <see cref="SkillRecords.ForgetVersions"/>, 2026-10-07); null = no buttons on the list.</param>
    public SkillsMenu(Func<SkillsFacts> facts, AppSettings settings, SettingsMenu menu, INoticeSink transcript, MenuPane pane, InputLine input, Action<string> openFile, Func<string, string?>? usage = null, SkillRecords? records = null,
        Func<Skill, IReadOnlyList<SkillRevision>>? versions = null, Func<Skill, SkillRevision, SkillRevert>? restore = null, Func<Skill, SkillRevision?, int>? forget = null)
    {
        _records = records;
        _versions = versions;
        _restore = restore;
        _forget = forget;
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
    /// The scope page's caption (2026-09-19): what is known of the skill, in the records' words (<see cref="SkillText.UsageLine"/>; the
    /// session store's, and none while <c>Session logging</c> was off, until 2026-10-02); <see cref="SkillText.NeverLoaded"/> for a skill
    /// the records know nothing of, null for no skill or an external one (never recorded). Read on open, a few light queries.
    /// </summary>
    public static string? UsageCaption(SkillRecords records, Skill? skill, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(zone);
        if (skill is null || skill.Scope == SkillScope.External)
        {
            return null;
        }

        return records.UsageLine(skill, zone) ?? SkillText.NeverLoaded;
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

    /// <summary>The scope page's row before delete (2026-10-02; always since 2026-10-04, opening the version list). Pinned.</summary>
    public static string RevertRow => Markup.Escape(SkillRecordText.RevertWord.PadRight(9)) + Theme.DimMarkup("pick an earlier version to put back");

    /// <summary>The version list's title: <c>Skills › haiku › revert</c> (2026-10-04).</summary>
    public static string VersionsTitle(string name) => ScopeTitle(name) + " › " + SkillRecordText.RevertWord;

    /// <summary>
    /// A version's row (2026-10-04): its file's path padded to <paramref name="pathWidth"/>, then what it is, dim
    /// (<see cref="SkillRecordText.VersionText"/>), and <see cref="SkillRecordText.CurrentMark"/> on the one the file holds now. Pinned.
    /// </summary>
    public static string VersionRow(SkillRevision revision, bool current, int pathWidth, TimeZoneInfo zone) =>
        VersionRow(revision, current, pathWidth, zone, null, 0);

    /// <summary>
    /// <see cref="VersionRow(SkillRevision, bool, int, TimeZoneInfo)"/> led by its version number (2026-10-07, the user's ask: the Offered
    /// tab's <c>v4</c> on the revert list too): <see cref="SkillsText.VersionLabel"/> padded to <paramref name="numberWidth"/> and two
    /// spaces, blank for a revision with no number (<see cref="VersionNumbers"/>); no cell at all at width 0. Pinned.
    /// </summary>
    public static string VersionRow(SkillRevision revision, bool current, int pathWidth, TimeZoneInfo zone, int? number, int numberWidth)
    {
        ArgumentNullException.ThrowIfNull(revision);
        string text = SkillRecordText.VersionText(revision, zone) + (current ? " " + SkillRecordText.CurrentMark : "");
        string cell = numberWidth > 0 ? SkillsText.VersionLabel(number).PadRight(numberWidth) + "  " : "";
        return Markup.Escape(cell + revision.Path.PadRight(pathWidth)) + " " + Theme.DimMarkup(text);
    }

    /// <summary>
    /// Each kept revision's version number, for a list newest first (2026-10-07): the Offered tab's count told backwards. The revisions
    /// with a text are the skill's older versions, the oldest <c>v1</c> and the newest one below the text in place
    /// (<c>SkillRecords.Versions</c>' <c>1 + kept</c>); a revision with no text (the file not there yet) is no version, null. Pure.
    /// </summary>
    public static IReadOnlyList<int?> VersionNumbers(IReadOnlyList<SkillRevision> newestFirst)
    {
        ArgumentNullException.ThrowIfNull(newestFirst);
        int next = newestFirst.Count(r => r.Content is not null);
        var numbers = new int?[newestFirst.Count];
        for (int i = 0; i < newestFirst.Count; i++)
        {
            numbers[i] = newestFirst[i].Content is null ? null : next--;
        }

        return numbers;
    }

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
    /// <remarks>The Offered tab filters (2026-10-03, the user's ask, <see cref="MenuFilter"/>): <paramref name="loaded"/> are the rows under <paramref name="filter"/>, and the caption counts the skills they keep.</remarks>
    public static MenuPage Page(SkillsFacts facts, IReadOnlyList<(string Markup, Skill? Skill)> loaded, AppSettingsData saved, SettingsMenu menu, int tab, string filter = "")
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(menu);
        ArgumentNullException.ThrowIfNull(filter);
        var tabs = new MenuTab[]
        {
            new(SkillsText.OfferedTabTitle, loaded.Select(r => r.Markup).ToList())
            {
                Hint = MenuFilter.Hint(LoadedKeys, filter),
                Filter = filter,
                Caption = MenuFilter.CaptionOrNull(filter, loaded.Count(r => r.Skill is not null), facts.Skills.Count + facts.Shadowed.Count),
            },
            menu.FieldsTab(SkillsText.ReflectionTabTitle, SettingsFields(ReflectionTab), saved) with { Hint = SettingsMenu.TabKeys },
            menu.FieldsTab(SkillsText.OptionsTabTitle, SettingsFields(OptionsTab), saved) with { Hint = SettingsMenu.TabKeys },
        };
        return MenuPage.Tabbed(SkillsText.Label, tabs, tab, OtherKeys) with
        {
            TabCursors = [0, 0, 0],
            // The Offered tab's band says the cursor's skill whole, its warning on a last line (2026-10-05, the user's ask: it stood
            // blank there while every row cuts its description at the pane's edge); the off, none and problem rows name none.
            Footer = (t, row) => t is OptionsTab or ReflectionTab
                ? row < SettingsFields(t).Count ? menu.FieldFooter(SettingsFields(t)[row], saved) : null
                : row < loaded.Count && loaded[row].Skill is { } skill ? new MenuFooter(skill.Description, skill.Warning) : null,
        };
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
        string filter = "";
        _menu.Root = SkillsText.Label;
        try
        {
            while (true)
            {
                // The facts are the scan's until an act changes them, so typing into the filter rescans nothing (2026-10-03).
                var loaded = SkillsText.LoadedRows(facts, filter);
                var saved = _settings.Current;
                var page = Page(facts, loaded, saved, _menu, tab, filter);
                var picked = await _pane.PickAsync(page, cursor, cancellationToken).ConfigureAwait(false);
                if (picked is not { } pick)
                {
                    return;
                }

                tab = pick.Tab;
                cursor = pick.Row;
                if (pick.Filter is { } typed)
                {
                    // The Offered tab's filter (2026-10-03): the rows again under it, the cursor on the first.
                    filter = typed;
                    cursor = 0;
                    continue;
                }

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

                    // The page on the tab the pane ended on, so a typed edit under it keeps that tab's rows (ToolsMenu's shape);
                    // Space flips a switch there (2026-10-04, as on /settings), and does nothing on any other row.
                    var shown = pick.Tab == page.Tab ? page : MenuPage.Tabbed(SkillsText.Label, page.Tabs!, pick.Tab, OtherKeys) with { Footer = page.Footer };
                    if (pick.Toggle ? await _menu.FlipAsync(field, cancellationToken).ConfigureAwait(false) : await _menu.EditAsync(field, saved, shown, cursor, cancellationToken).ConfigureAwait(false))
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
                    cursor = Math.Max(0, Math.Min(cursor, SkillsText.LoadedRows(facts, filter).Count - 1));
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
        // The revert row (2026-10-02): always since 2026-10-04 (the user's call), a skill with nothing kept saying so when picked.
        int revertRow = _versions is not null && _restore is not null ? rows.Count : -1;
        if (revertRow >= 0)
        {
            rows.Add(RevertRow);
        }

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

        if (row == revertRow)
        {
            return await PickVersionAsync(skill, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// The revert row's list (2026-10-04, the user's ask): every kept version of the skill, newest first, the one the file holds now marked,
    /// the cursor on the newest that is not; Enter puts the pick back (<see cref="SkillRecords.Restore"/>: the current text kept first) after
    /// a yes with No on the cursor (later on 2026-10-07, the user's ask; no yes/no until then, nothing being lost — the rename's rule; a No
    /// shows the list again), ESC goes back to the list. Nothing kept is a notice on the status line. True when the skill changed (the
    /// facts are stale).
    /// <para>Since 2026-10-07 (the user's pick) the title row's <see cref="VersionButtons"/> forget kept versions: <c>d</c> the highlighted
    /// one, <c>c</c> every one, each after a yes with No on the cursor (<see cref="SkillRecords.ForgetVersions"/>; the text in place and the
    /// skill's record stay). The list shows again after a removal, and goes when nothing is left; a removal counts as a change (the
    /// Offered tab's version moves).</para>
    /// </summary>
    private async Task<bool> PickVersionAsync(Skill skill, CancellationToken cancellationToken)
    {
        var zone = _records?.Zone ?? TimeZoneInfo.Utc;
        bool forgot = false;
        int? cursor = null;
        while (true)
        {
            var versions = _versions!(skill);
            if (versions.Count == 0)
            {
                if (!forgot)
                {
                    Sink.Notice(SkillRecordText.NoVersionsNotice(skill.Name));
                }

                return forgot;
            }

            int width = versions.Max(v => v.Path.Length);
            var current = versions.Select(v => SkillRecords.IsCurrent(skill, v)).ToList();
            // The version numbers (2026-10-07): the Offered tab's v, the text in place one past the newest kept.
            var numbers = VersionNumbers(versions);
            int kept = numbers.Count(n => n is not null);
            int numberWidth = kept == 0 ? 0 : Math.Max(2, numbers.Max(n => SkillsText.VersionLabel(n).Length));
            var rows = versions.Select((v, i) => VersionRow(v, current[i], width, zone, numbers[i], numberWidth)).ToList();
            int at = Math.Clamp(cursor ?? Math.Max(0, current.IndexOf(false)), 0, versions.Count - 1);
            var page = new MenuPage(VersionsTitle(skill.Name), rows, _forget is null ? SettingsMenu.PickKeys : VersionsKeys)
            {
                Caption = SkillRecordText.VersionsCaptionAt(kept + 1),
                Buttons = _forget is null ? null : VersionButtons,
            };
            if (await _pane.PickAsync(page, at, cancellationToken).ConfigureAwait(false) is not { Row: var picked } pick || picked >= versions.Count)
            {
                return forgot;
            }

            if (pick.Button >= 0 && _forget is not null)
            {
                bool all = VersionButtons[pick.Button].Key == QueueMenu.ClearAllKey;
                string label = SkillsText.VersionLabel(numbers[picked]);
                string question = all ? SkillRecordText.ClearVersionsQuestion(skill.Name, versions.Count) : SkillRecordText.RemoveVersionQuestion(skill.Name, label);
                var asking = new MenuPage(question, SettingsMenu.ConfirmRows, SettingsMenu.ConfirmKeys)
                {
                    Hotkeys = SettingsMenu.ConfirmHotkeys,
                    Caption = all ? SkillRecordText.ClearVersionsCaption : SkillRecordText.RemoveVersionCaption(versions[picked], zone),
                };
                cursor = picked;
                if (await _pane.PickAsync(asking, 0, cancellationToken).ConfigureAwait(false) is not { Row: 1 })
                {
                    Sink.Notice(ChatScreen.KeptNotice);
                    continue;
                }

                int forgotten = _forget(skill, all ? null : versions[picked]);
                if (forgotten > 0)
                {
                    forgot = true;
                    Sink.Notice(all ? SkillRecordText.VersionsClearedNotice(skill.Name, forgotten) : SkillRecordText.VersionRemovedNotice(skill.Name, label));
                }

                continue;
            }

            var putting = new MenuPage(SkillRecordText.PutBackQuestion(skill.Name, SkillsText.VersionLabel(numbers[picked])), SettingsMenu.ConfirmRows, SettingsMenu.ConfirmKeys)
            {
                Hotkeys = SettingsMenu.ConfirmHotkeys,
                Caption = SkillRecordText.PutBackCaption(versions[picked], zone),
            };
            if (await _pane.PickAsync(putting, 0, cancellationToken).ConfigureAwait(false) is not { Row: 1 })
            {
                Sink.Notice(ChatScreen.KeptNotice);
                cursor = picked;
                continue;
            }

            var revert = _restore!(skill, versions[picked]);
            var (changed, text) = SkillRecordText.RevertText(skill.Name, revert, zone);
            if (changed || revert.Outcome == SkillRevertOutcome.Unchanged)
            {
                Sink.Notice(text);
            }
            else
            {
                Sink.Error(text);
            }

            return changed || forgot;
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
