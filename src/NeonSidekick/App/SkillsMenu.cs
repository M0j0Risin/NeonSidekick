using NeonSidekick.Files;
using NeonSidekick.Sessions;
using NeonSidekick.Settings;
using NeonSidekick.Skills;
using NeonSidekick.UI;
using Spectre.Console;
using Spectre.Console.Rendering;

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
/// behind the <c>Allow skill delete</c> setting from 2026-09-18 until then, which went), the cursor on the scope it is in. Since 2026-10-13
/// (the user's ask) a profile skill's page has <c>move</c> after the two roots: the other profiles listed under it, and the one picked
/// takes the folder into its own skills after a yes (<see cref="SkillEditor.MoveToProfile"/>), the record going with it
/// (<see cref="SkillRecords.MovedToProfile"/>); a profile that holds the folder's name already is refused ahead of the question
/// (<see cref="ProfileExistsError"/>), and with no other profile the row only says so (<see cref="NoOtherProfileNotice"/>). Picking the other root moves the folder
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
/// heading, a warning, a skipped folder — does nothing, and so does Space on the Offered tab. Since 2026-10-10 (the user's ask: a
/// filter for the profile's skills or the global ones) the Offered tab holds the scopes as sub-tabs on a second strip — all, profile,
/// global, external while one is listed (<see cref="SkillsText.ScopeTabs"/>) — ←/→ walking them on the way to Reflection, Tab stepping
/// past them, every visit starting on all; the typed filter runs on each. The
/// <see cref="QueueMenu"/> shape: no prompt fallback, a console without the pane gets
/// <see cref="Lines"/> in the transcript — the three tabs as headed sections.
/// </summary>
internal sealed class SkillsMenu
{
    // The key hints. Pinned.
    public const string LoadedKeys = "Enter = move, rename, edit, revert, lock, disable, delete · ←/→ tabs · " + MenuFilter.TypeAndCloseKeys;   // the filter since 2026-10-03; lock since 2026-10-07; disable since 2026-10-09
    public const string OtherKeys = "←/→ tabs · Esc = close";
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

    /// <summary>
    /// The scope page's root rows for <paramref name="roots"/> (2026-10-13): <see cref="ScopeRows"/>, or the profile's alone while the
    /// profile has <c>Global skills</c> off — no move to global. The page counts every later row from this list.
    /// </summary>
    public static IReadOnlyList<SkillScope> ScopeRowsFor(SkillRoots roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        return roots.WritableScopes;
    }

    private readonly Func<SkillsFacts> _facts;
    private readonly AppSettings _settings;
    private readonly SettingsMenu _menu;
    private readonly INoticeSink _transcript;
    private readonly MenuPane _pane;
    private readonly InputLine _input;
    private readonly Action<string> _openFile;
    private readonly Func<string, string?> _usage;
    private readonly SkillRecords? _records;
    private readonly Llm.ConversationHistory? _session;
    private readonly Func<Skill, IReadOnlyList<SkillRevision>>? _versions;
    private readonly Func<Skill, SkillRevision, SkillRevert>? _restore;
    private readonly Func<Skill, SkillRevision?, int>? _forget;
    private readonly SkillCatalog? _catalog;
    private readonly ProjectSkillTrust? _trust;
    private readonly Func<string, IRenderable, CancellationToken, Task>? _view;
    private readonly Func<IRecycleBin?>? _bin;

    /// <summary>
    /// The revert list's buttons (2026-10-07, the user's pick): the highlighted version removed, or every one, each after a yes. The remove
    /// is the saved list's <c>✖  remove</c>, two spaces (later that day, the user's report: Windows Terminal draws ✖ two cells wide over
    /// one space, so it read "✖remove", as <c>/process</c>' kill did).
    /// </summary>
    public static readonly IReadOnlyList<MenuButton> VersionButtons = [new(YouTube.YouTubeText.RemoveButton, 'd'), new(QueueMenu.ClearAllButton, QueueMenu.ClearAllKey)];

    /// <summary>The revert list's hint with its buttons (2026-10-07). Pinned.</summary>
    public const string VersionsKeys = "Enter = put back · d = remove · c = clear all · Esc = back";

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
    /// <param name="session">The session's history (2026-10-12): the loaded sub-tab's skills, read live and watched while the pane is open; null = no loaded sub-tab.</param>
    /// <param name="catalog">The catalog (2026-11-23): the trust question reads a project folder through it, with the scan's rules. Null = no trust question.</param>
    /// <param name="trust">The profile's project trust store (2026-11-23): trust, untrust and a project skill's disable. Null = none of them.</param>
    /// <param name="view">Shows a read-only page in the info pane (2026-11-23): the trust question's View of a <c>SKILL.md</c> or its diff. Null = no view.</param>
    /// <param name="bin">The bin a project skill's delete uses (2026-11-23, the user's ask), read at each delete: the screen's while <c>File deletes to
    /// Recycle bin</c> is on. Null, or a null answer, deletes for good, and the row and the question say so.</param>
    public SkillsMenu(Func<SkillsFacts> facts, AppSettings settings, SettingsMenu menu, INoticeSink transcript, MenuPane pane, InputLine input, Action<string> openFile, Func<string, string?>? usage = null, SkillRecords? records = null,
        Func<Skill, IReadOnlyList<SkillRevision>>? versions = null, Func<Skill, SkillRevision, SkillRevert>? restore = null, Func<Skill, SkillRevision?, int>? forget = null, Llm.ConversationHistory? session = null,
        SkillCatalog? catalog = null, ProjectSkillTrust? trust = null, Func<string, IRenderable, CancellationToken, Task>? view = null, Func<IRecycleBin?>? bin = null)
    {
        _bin = bin;
        _catalog = catalog;
        _trust = trust;
        _view = view;
        _session = session;
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
        // A read-only skill is never recorded: an external one, or a repo's (2026-11-23), which "never loaded" would misstate once loaded.
        if (skill is null || SkillScopes.IsReadOnly(skill.Scope))
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

    /// <summary>The move row's word (2026-10-13, the user's ask): a profile skill sent to another profile. Pinned.</summary>
    public const string MoveWord = "move";

    /// <summary>The move row, after the two roots on a profile skill's page alone (2026-10-13): the word padded to nine, what it does dim after it.</summary>
    public static string MoveRow => Markup.Escape(MoveWord.PadRight(9)) + Theme.DimMarkup("send it to another profile's skills");

    /// <summary>The copy row's word (2026-10-25, the user's ask): a profile skill copied into another profile, the original kept. Pinned.</summary>
    public const string CopyWord = "copy";

    /// <summary>The copy row, right after move on a profile skill's page alone (2026-10-25).</summary>
    public static string CopyRow => Markup.Escape(CopyWord.PadRight(9)) + Theme.DimMarkup("copy it into another profile's skills");

    /// <summary>The copy's profile list's title: <c>Skills › haiku › copy</c> (2026-10-25).</summary>
    public static string CopyToTitle(string name) => ScopeTitle(name) + " › " + CopyWord;

    public static string CopyToPrompt(string name, string profile) => $"Copy skill '{name}' into profile '{profile}'? It starts there with no history.";

    public static string CopiedToNotice(string name, string profile) => $"({NoticeGlyphs.Skill}copied: {name} → profile {profile})";

    /// <summary>A copy refused because the other profile's skills hold the folder's name, or (<paramref name="byName"/>) a skill of that name (2026-10-25).</summary>
    public static string CopyExistsError(string name, string folder, string profile, bool byName = false) => byName
        ? $"Could not copy skill '{name}': profile '{profile}' already has a skill named '{name}'"
        : $"Could not copy skill '{name}': the skills of profile '{profile}' already hold '{folder}'";

    public static string CopyFailedError(string detail) => $"Could not copy the skill: {detail}";

    /// <summary>The copy row with no other profile to copy into (2026-10-25).</summary>
    public const string NoOtherProfileForCopyNotice = "(" + NoticeGlyphs.Skill + "no other profile to copy it to; /profile add makes one)";

    /// <summary>The profile list's title: <c>Skills › haiku › move</c> (2026-10-13).</summary>
    public static string MoveToTitle(string name) => ScopeTitle(name) + " › " + MoveWord;

    /// <summary>A profile on the move's list (2026-10-13): its name padded to two past <paramref name="width"/>, its skills folder dim after it. Pinned.</summary>
    public static string ProfileRow(string profile, string root, int width)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(root);
        return Markup.Escape(profile.PadRight(width + 2)) + Theme.DimMarkup(root);
    }

    /// <summary>The skills folder of profile <paramref name="profile"/> under <paramref name="home"/>: <c>&lt;home&gt;\profiles\&lt;name&gt;\skills</c>.</summary>
    public static string ProfileSkillsRoot(string home, string profile) => Path.Combine(Profiles.Directory(home, profile), SkillRoots.DirectoryName);

    public static string MoveToPrompt(string name, string profile) => $"Move skill '{name}' from this profile to profile '{profile}'?";

    public static string MovedToNotice(string name, string profile) => $"({NoticeGlyphs.Skill}moved: {name} → profile {profile})";

    /// <summary>The other profile already holds the folder's name (2026-10-13), the scope move's <see cref="ExistsError"/> in a profile's words.</summary>
    public static string ProfileExistsError(string name, string folder, string profile) => $"Could not move skill '{name}': the skills of profile '{profile}' already hold '{folder}'";

    /// <summary>The move row with no other profile to list (2026-10-13). Pinned.</summary>
    public const string NoOtherProfileNotice = "(" + NoticeGlyphs.Skill + "no other profile to move it to; /profile add makes one)";

    /// <summary>The edit row (2026-09-23): the word padded to nine, what it does dim after it.</summary>
    public static string EditRow => Markup.Escape(EditWord.PadRight(9)) + Theme.DimMarkup("open its SKILL.md in your editor");

    /// <summary>The scope page's row before delete (2026-10-02; always since 2026-10-04, opening the version list). Pinned.</summary>
    public static string RevertRow => Markup.Escape(SkillRecordText.RevertWord.PadRight(9)) + Theme.DimMarkup("pick an earlier version to put back");

    /// <summary>The lock row's words (2026-10-07, the user's ask: <see cref="SkillLock"/>). Pinned.</summary>
    public const string LockWord = "lock";
    public const string UnlockWord = "unlock";

    /// <summary>The skill page's lock row, between revert and delete: <c>lock</c> on an unlocked skill, <c>unlock</c> on a locked one. Pinned.</summary>
    public static string LockRow(bool locked) => locked
        ? Markup.Escape(UnlockWord.PadRight(9)) + Theme.DimMarkup("let it be changed again")
        : Markup.Escape(LockWord.PadRight(9)) + Theme.DimMarkup("keep the model, reflection and updates from changing it");

    /// <summary>The disable row's words (2026-10-09, the user's ask: <see cref="SkillDisable"/>). Pinned.</summary>
    public const string DisableWord = "disable";
    public const string EnableWord = "enable";

    /// <summary>The skill page's disable row, between lock and delete: <c>disable</c> on an offered skill, <c>enable</c> on a disabled one. Pinned.</summary>
    public static string DisableRow(bool disabled) => disabled
        ? Markup.Escape(EnableWord.PadRight(9)) + Theme.DimMarkup("offer it to the model again")
        : Markup.Escape(DisableWord.PadRight(9)) + Theme.DimMarkup("stop offering it; a reflection still sees it, and changes nothing");

    /// <summary>
    /// The Offered footer's last line for <paramref name="skill"/>: its warning, then <see cref="SkillRecordText.DisabledFooter"/> on a disabled
    /// skill (2026-10-09) and <see cref="SkillRecordText.LockedFooter"/> on a locked one (2026-10-07), those there joined by <c> · </c>; null for none.
    /// </summary>
    public static string? LockedLast(Skill skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        var parts = new List<string>(3);
        if (skill.Warning is { } warning)
        {
            parts.Add(warning);
        }

        if (skill.Disabled)
        {
            parts.Add(SkillRecordText.DisabledFooter);
        }

        if (skill.Locked)
        {
            parts.Add(SkillRecordText.LockedFooter);
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

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
        string name = SkillFrontmatter.Kebab(typed, keepUnderscore: false);
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

    /// <summary>The headings over a settings tab's rows (2026-10-10, the user's ask: the <c>/settings</c> look), <see cref="SettingsMenu.SkillsMenuSections"/> as <see cref="SettingsFields"/> picks.</summary>
    private static IReadOnlyList<(SettingsField First, string Title)> SettingsSections(int tab) => SettingsMenu.SkillsMenuSections[tab == OptionsTab ? 0 : 1];

    /// <summary>The field on row <paramref name="row"/> of settings tab <paramref name="tab"/>, headings counted; null for a heading or past the rows.</summary>
    private static SettingsField? SettingsRow(int tab, int row) => SettingsMenu.SectionedField(SettingsFields(tab), SettingsSections(tab), row);

    /// <summary>The tabbed page: the Offered rows first, the Reflection rows (<see cref="SettingsMenu.FieldsTab"/> under <see cref="SettingsMenu.TabKeys"/>) second, the Options rows last (2026-09-22); Space is nothing anywhere since the Project tab, its one flip, went (2026-10-01), as on <c>/settings</c>.</summary>
    /// <remarks>
    /// The Offered tab filters (2026-10-03, the user's ask, <see cref="MenuFilter"/>): <paramref name="loaded"/> are the rows under <paramref name="filter"/>, and the caption counts the skills they keep.
    /// Since 2026-10-10 (the user's ask: a filter for the profile's skills or the global ones) the tab holds a sub-tab per
    /// <see cref="SkillsText.ScopeTabs"/> — all, profile, global, external while one is listed — on the second strip (<see cref="MenuTab.SubTabs"/>,
    /// <c>/tools</c>' groups' shape): <paramref name="loaded"/> holds each one's rows in that order, <paramref name="sub"/> the one on show, and
    /// the one typed filter runs on each; with no scope tabs (the skills off, none installed) it holds the tab's one list, a plain tab.
    /// <para>The loaded sub-tab (2026-10-12, the user's ask) ends the strip when <paramref name="session"/> is given and the strip is there: the
    /// skills the conversation holds loaded now (<see cref="SkillsText.LoadedSkillRows"/>), its rows <paramref name="loaded"/>'s last list
    /// (<see cref="OfferedRows"/> puts it there), its badge their count. The host keeps it current (<see cref="ShowAsync"/>).</para>
    /// </remarks>
    public static MenuPage Page(SkillsFacts facts, IReadOnlyList<IReadOnlyList<(string Markup, Skill? Skill, bool Heading)>> loaded, AppSettingsData saved, SettingsMenu menu, int tab, string filter = "", int sub = 0, IReadOnlyList<Llm.LoadedSkill>? session = null)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(menu);
        ArgumentNullException.ThrowIfNull(filter);
        var scopes = SkillsText.ScopeTabs(facts);
        var subTabs = scopes.Select((scope, i) => Offered(SkillsText.ScopeTitle(scope), loaded[i], scope) with { Badge = SkillsText.ScopeBadge(facts, scope) }).ToList();
        if (scopes.Count > 0 && session is not null && loaded.Count > scopes.Count)
        {
            var rows = loaded[scopes.Count];
            subTabs.Add(new MenuTab(SkillsText.LoadedSubTitle, rows.Select(r => r.Markup).ToList())
            {
                Headings = SkillsText.HeadingRows(rows),
                Hint = MenuFilter.Hint(LoadedKeys, filter),
                Filter = filter,
                Caption = MenuFilter.CaptionOrNull(filter, rows.Count(r => r.Skill is not null), session.Count),
                Footer = row => row >= 0 && row < rows.Count && rows[row].Skill is { } skill ? new MenuFooter(skill.Description, LockedLast(skill)) : null,
                Badge = SkillsText.LoadedBadge(session),
                Placeholder = SkillsText.IsPlaceholder(rows),
            });
        }

        var offered = scopes.Count == 0
            ? Offered(SkillsText.OfferedTabTitle, loaded[0], null)
            : new MenuTab(SkillsText.OfferedTabTitle, [])
            {
                SubTabs = subTabs,
                Sub = Math.Clamp(sub, 0, subTabs.Count - 1),
            };
        var tabs = new MenuTab[]
        {
            offered,
            menu.FieldsTab(SkillsText.ReflectionTabTitle, SettingsFields(ReflectionTab), saved, sections: SettingsSections(ReflectionTab)) with { Hint = SettingsMenu.TabKeys },
            menu.FieldsTab(SkillsText.OptionsTabTitle, SettingsFields(OptionsTab), saved, sections: SettingsSections(OptionsTab)) with { Hint = SettingsMenu.TabKeys },
        };
        return MenuPage.Tabbed(SkillsText.Label, tabs, tab, OtherKeys) with
        {
            TabCursors = [0, 0, 0],
            // The settings tabs' band; the Offered tab's (and each scope's) is its own, below. The page's is what turns the band on.
            Footer = (t, row) => t is OptionsTab or ReflectionTab && SettingsRow(t, row) is { } field ? menu.FieldFooter(field, saved) : null,
        };

        // The Offered rows (or one scope's): the hint and the filter, the caption counting the skills kept of those the scope holds, and
        // the band saying the cursor's skill whole, its warning on a last line (2026-10-05, the user's ask: it stood blank there while every
        // row cuts its description at the pane's edge); the off, none and problem rows name none.
        MenuTab Offered(string title, IReadOnlyList<(string Markup, Skill? Skill, bool Heading)> rows, SkillScope? scope) => new(title, rows.Select(r => r.Markup).ToList())
        {
            Headings = SkillsText.HeadingRows(rows),   // the catalog's, the shadowed and the skipped blocks' rules (2026-10-10), never a stop
            Hint = MenuFilter.Hint(scope == SkillScope.Project ? SkillsText.ProjectKeys : LoadedKeys, filter),   // the project sub-tab's own acts (2026-11-23)
            Filter = filter,
            Caption = MenuFilter.CaptionOrNull(filter, rows.Count(r => r.Skill is not null), SkillsText.ScopeCount(facts, scope)),
            Footer = row => row >= 0 && row < rows.Count && rows[row].Skill is { } skill ? new MenuFooter(skill.Description, LockedLast(skill)) : null,
            Placeholder = SkillsText.IsPlaceholder(rows),
        };
    }

    /// <summary>
    /// The Offered tab's rows for <paramref name="facts"/> under <paramref name="filter"/>: one list per <see cref="SkillsText.ScopeTabs"/> (2026-10-10), or the one list when there are none;
    /// with <paramref name="session"/> and the scope tabs, the loaded sub-tab's rows last (2026-10-12, <see cref="SkillsText.LoadedSkillRows"/>).
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<(string Markup, Skill? Skill, bool Heading)>> OfferedRows(SkillsFacts facts, string filter, IReadOnlyList<Llm.LoadedSkill>? session = null)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var scopes = SkillsText.ScopeTabs(facts);
        if (scopes.Count == 0)
        {
            return [SkillsText.LoadedRows(facts, filter)];
        }

        var lists = scopes.Select(scope => SkillsText.LoadedRows(facts, filter, scope)).ToList();
        if (session is not null)
        {
            lists.Add(SkillsText.LoadedSkillRows(facts, session, filter));
        }

        return lists;
    }

    /// <summary>The three tabs as plain lines, for a console without the pane, in the strip's order: <see cref="SkillsText.Lines"/>, then the Reflection rows and the Options rows (<see cref="SettingsMenu.PlainRow"/>), each headed and indented.</summary>
    public static IEnumerable<string> Lines(SkillsFacts facts, AppSettingsData saved, SettingsMenu menu, IReadOnlyList<Llm.LoadedSkill>? session = null)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(menu);
        // The loaded skills after the catalog (2026-10-12) when the history is given: the sub-tab's content, plainly.
        var loaded = session is null ? [] : new[] { SkillsText.LoadedTitle }.Concat(SkillsText.LoadedSkillLines(session).Select(line => "  " + line));
        foreach (var line in SkillsText.Lines(facts)
            .Concat(loaded)
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
    public Task ShowAsync(CancellationToken cancellationToken, bool midTurn = false) => ShowAsync(cancellationToken, ReplyRun.Of(midTurn));

    /// <summary>The pane under a reply asked live (2026-10-12, <see cref="ReplyRun"/>): its picks open when the reply ends, the pane saying so.</summary>
    public async Task ShowAsync(CancellationToken cancellationToken, ReplyRun reply)
    {
        var facts = _facts();
        if (!_pane.Enabled)
        {
            foreach (var line in Lines(facts, _settings.Current, _menu, _session?.LoadedSkills))
            {
                _transcript.Notice(line);
            }

            return;
        }

        int tab = 0;
        int cursor = 0;
        int sub = 0;   // the Offered tab's scope on show (2026-10-10): all on every visit
        bool endedSaid = false;   // the reply's end said once (2026-10-12)
        string filter = "";
        _menu.Root = SkillsText.Label;
        try
        {
            while (true)
            {
                // The facts are the scan's until an act changes them, so typing into the filter rescans nothing (2026-10-03).
                // The loaded skills are the history's as it stands (2026-10-12), and the pick is a live one: a load answered, a compact or a
                // rewind under the open pane ends it with Refresh, and the page is built again where it stood — no rescan.
                var session = _session?.LoadedSkills;
                var lists = OfferedRows(facts, filter, session);
                var saved = _settings.Current;
                var page = Page(facts, lists, saved, _menu, tab, filter, sub, session);
                MenuPick? picked;
                using (var stale = new CancellationTokenSource())
                {
                    // Raised on the turn's task; a raise already under way when the pick ends finds the source disposed, and is nothing.
                    void Changed() => VoiceSession.SafeCancel(stale);
                    if (_session is { } watched)
                    {
                        watched.SkillsChanged += Changed;
                    }

                    // The reply's end is a change too (2026-10-12): the page again, its picks open.
                    using var ended = reply.Stale.Register(Changed);

                    try
                    {
                        // A change between the read above and the subscription: the next round reads it.
                        if (_session is not null && !ReferenceEquals(_session.LoadedSkills, session))
                        {
                            stale.Cancel();
                        }

                        picked = await _pane.PickAsync(page, cursor, cancellationToken, stale: stale.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (_session is { } watched2)
                        {
                            watched2.SkillsChanged -= Changed;
                        }
                    }
                }

                if (picked is not { } pick)
                {
                    return;
                }

                tab = pick.Tab;
                cursor = pick.Row;
                // ←/→ move the scope with no pick, so it is read off the pane (ToolsMenu's shape).
                sub = _pane.ShownSubs is { Count: > 0 } shownSubs ? shownSubs[0] : 0;
                if (pick.Refresh)
                {
                    if (reply.Ended.CanBeCanceled && !reply.Running && !endedSaid)
                    {
                        endedSaid = true;
                        Sink.Notice(SettingsMenu.ReplyEndedNotice);   // the reply ended under the pane (2026-10-12): every pick open now
                    }

                    continue;   // the page again, built from the history as it is now; the cursor is clamped by the pane
                }

                var loaded = lists[Math.Clamp(sub, 0, lists.Count - 1)];
                if (pick.Filter is { } typed)
                {
                    // The Offered tab's filter (2026-10-03): the rows again under it, the cursor on the first.
                    filter = typed;
                    cursor = 0;
                    continue;
                }

                if (tab is OptionsTab or ReflectionTab)
                {
                    if (SettingsRow(tab, cursor) is not { } field)
                    {
                        continue;   // a heading (2026-10-10) or past the rows
                    }

                    if (reply.Running && SettingsMenu.RefusedMidTurn(field))
                    {
                        Sink.Notice(SettingsMenu.NotWhileReplyRunsNotice);
                        continue;
                    }

                    // The page on the tab the pane ended on, so a typed edit under it keeps that tab's rows (ToolsMenu's shape);
                    // Space flips a switch there (2026-10-04, as on /settings), and does nothing on any other row.
                    var shown = pick.Tab == page.Tab ? page : MenuPage.Tabbed(SkillsText.Label, page.Tabs!, pick.Tab, OtherKeys) with { Footer = page.Footer };
                    if (pick.Toggle ? await _menu.FlipAsync(field, cancellationToken).ConfigureAwait(false) : await _menu.EditAsync(field, saved, shown, cursor, cancellationToken).ConfigureAwait(false))
                    {
                        var scopesBefore = SkillsText.ScopeTabs(facts);
                        facts = _facts();   // the skills switch empties the Offered tab
                        if (!scopesBefore.SequenceEqual(SkillsText.ScopeTabs(facts)))
                        {
                            // The strip changed (2026-10-13: Global skills flipped, the global sub-tab gone or back): the remembered
                            // sub-tab is a position, so it would land on another scope; back to all.
                            sub = 0;
                        }
                    }

                    continue;
                }

                if (tab != 0 || cursor >= loaded.Count)
                {
                    continue;
                }

                // The project sub-tab (2026-11-23): its rows carry folders and acts a tuple can't, resolved through ProjectRows.
                var shownScopes = SkillsText.ScopeTabs(facts);
                if (sub >= 0 && sub < shownScopes.Count && shownScopes[sub] == SkillScope.Project)
                {
                    var projectRows = SkillsText.ProjectRows(facts, filter);
                    if (cursor >= projectRows.Count || projectRows[cursor].Kind is ProjectRowKind.Text or ProjectRowKind.Heading)
                    {
                        continue;
                    }

                    if (reply.Running)
                    {
                        // A trust, untrust, copy or disable under a running turn could race the model's load_skill.
                        Sink.Notice(SettingsMenu.NotWhileReplyRunsNotice);
                        continue;
                    }

                    var (projectChanged, trustedLevel) = await ProjectRowAsync(projectRows[cursor], facts, cancellationToken).ConfigureAwait(false);
                    if (projectChanged)
                    {
                        facts = Refreshed(facts, ref sub, ref cursor, filter);
                        ShadowNotice(facts, trustedLevel);
                    }

                    continue;
                }

                if (loaded[cursor].Skill is not { } skill)
                {
                    continue;
                }

                if (reply.Running)
                {
                    Sink.Notice(SettingsMenu.NotWhileReplyRunsNotice);
                    continue;
                }

                if (skill.Scope == SkillScope.Project)
                {
                    // A repo's skill on the all or loaded sub-tab (2026-11-23): its own page, never the scope page's move, rename, lock or delete.
                    if (await ProjectPageAsync(skill, facts, cancellationToken).ConfigureAwait(false))
                    {
                        facts = Refreshed(facts, ref sub, ref cursor, filter);
                    }

                    continue;
                }

                if (skill.Scope == SkillScope.External)
                {
                    Sink.Notice(ExternalReadOnlyNotice);
                    continue;
                }

                if (await PickScopeAsync(skill, facts, cancellationToken).ConfigureAwait(false))
                {
                    // As the other acts do (2026-11-23, review #51): a move into a repo can add the project sub-tab, and the tab on show is kept.
                    facts = Refreshed(facts, ref sub, ref cursor, filter);
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
    /// The facts read again after a change (a rescan), the sub-tab on show kept where the strip changed (<see cref="RemapSub"/>), the cursor clamped to
    /// the rows on show.
    /// </summary>
    private SkillsFacts Refreshed(SkillsFacts before, ref int sub, ref int cursor, string filter)
    {
        var facts = _facts();
        sub = RemapSub(SkillsText.ScopeTabs(before), SkillsText.ScopeTabs(facts), sub);
        var rows = OfferedRows(facts, filter);
        cursor = Math.Max(0, Math.Min(cursor, rows[Math.Clamp(sub, 0, rows.Count - 1)].Count - 1));
        return facts;
    }

    /// <summary>
    /// The sub-tab <paramref name="sub"/> of the strip <paramref name="before"/>, found in <paramref name="after"/> (2026-11-23, review #51: a changed
    /// strip sent every act back to all): a scope's tab by its scope, the loaded sub-tab (past the scopes) to the new strip's loaded one, all when the
    /// tab is gone. Pure.
    /// </summary>
    public static int RemapSub(IReadOnlyList<SkillScope?> before, IReadOnlyList<SkillScope?> after, int sub)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (before.SequenceEqual(after))
        {
            return sub;
        }

        if (before.Count == 0 || after.Count == 0 || sub < 0)
        {
            return 0;
        }

        if (sub >= before.Count)
        {
            return after.Count;
        }

        for (int i = 0; i < after.Count; i++)
        {
            if (after[i] == before[sub])
            {
                return i;
            }
        }

        return 0;
    }

    /// <summary>After a trust, the folder's skills your own hide (2026-11-23), named in one notice; nothing when none is.</summary>
    private void ShadowNotice(SkillsFacts facts, string? level)
    {
        if (level is null)
        {
            return;
        }

        var hidden = facts.Shadowed.Where(s => s.Scope == SkillScope.Project && string.Equals(s.TrustFolder, level, StringComparison.Ordinal)).Select(s => s.Name).ToList();
        if (hidden.Count > 0)
        {
            Sink.Notice(SkillsText.ProjectShadowedNotice(SkillsText.FolderLabel(facts, level), hidden));
        }
    }

    /// <summary>The scope page under the list — the two roots, project (2026-11-23), move (a profile skill's, 2026-10-13), rename, edit, delete —, the confirmation or the typed slot under that, then the act; true when the folder changed (the facts are stale).</summary>
    private async Task<bool> PickScopeAsync(Skill skill, SkillsFacts facts, CancellationToken cancellationToken)
    {
        var roots = facts.Roots;
        if (roots.Hides(skill.Scope))
        {
            // A global skill from a list read before Global skills went off (2026-10-13): no page — every act on it is refused below
            // anyway — and the list again, without it.
            Sink.Notice(SkillRecordText.GlobalOffNotice(skill.Name));
            return true;
        }

        // The page's own root rows (2026-10-13): one while Global skills is off, and every index below counts from them.
        var scopeRows = ScopeRowsFor(roots);
        var rows = scopeRows.Select(scope => ScopeRow(scope, roots)).ToList();
        // The move into the working folder's .agents\skills (2026-11-23, the user's ask): right after the roots, while there's a level to move to and the
        // trust store to keep it trusted; every row its own index since.
        int projectRow = facts.ProjectTarget is not null && _catalog is not null && _trust is not null ? rows.Count : -1;
        if (projectRow >= 0)
        {
            rows.Add(SkillsText.ProjectMoveRow(facts.ProjectTarget!));
        }

        // A bare rule between the places and the acts (2026-11-23, the user's ask): a heading, never a stop, so key scripts keep their counts.
        int ruleRow = rows.Count;
        rows.Add(SectionRule.Bare);

        // The move to another profile (2026-10-13, the user's ask): a profile skill's alone, after the two roots; every row its own index since.
        int moveRow = skill.Scope == SkillScope.Profile ? rows.Count : -1;
        if (moveRow >= 0)
        {
            rows.Add(MoveRow);
        }

        // The copy (2026-10-25, the user's ask): right after move, a profile skill's alone.
        int copyRow = skill.Scope == SkillScope.Profile ? rows.Count : -1;
        if (copyRow >= 0)
        {
            rows.Add(CopyRow);
        }

        int renameRow = rows.Count;
        rows.Add(RenameRow);
        int editRow = rows.Count;
        rows.Add(EditRow);
        // The revert row (2026-10-02): always since 2026-10-04 (the user's call), a skill with nothing kept saying so when picked.
        int revertRow = _versions is not null && _restore is not null ? rows.Count : -1;
        if (revertRow >= 0)
        {
            rows.Add(RevertRow);
        }

        // The lock row (2026-10-07, the user's ask), between revert and delete; its own index, so the delete below is the last row only.
        int lockRow = rows.Count;
        rows.Add(LockRow(skill.Locked));
        // The disable row (2026-10-09, the user's ask), after lock.
        int disableRow = rows.Count;
        rows.Add(DisableRow(skill.Disabled));
        int deleteRow = rows.Count;
        rows.Add(DeleteRow);

        var page = new MenuPage(ScopeTitle(skill.Name), rows, ScopeKeys) { Caption = _usage(skill.Name), Headings = new HashSet<int> { ruleRow } };
        var picked = await _pane.PickAsync(page, Math.Max(0, IndexOf(scopeRows, skill.Scope)), cancellationToken).ConfigureAwait(false);
        if (picked is not { Row: var row })
        {
            return false;
        }

        if (row == lockRow)
        {
            return SetLocked(skill, !skill.Locked);
        }

        if (row == disableRow)
        {
            return SetDisabled(skill, !skill.Disabled);
        }

        // A locked skill (2026-10-07): rename, move and delete refused before any question; edit (the user's own editor) and the
        // revert list (its d and c) still open. A copy (2026-10-25) changes nothing here, so it copies a locked skill, the lock with it.
        bool changes = row == renameRow || row == moveRow || row == deleteRow || row == projectRow || (row < scopeRows.Count && scopeRows[row] != skill.Scope);
        if (changes && skill.Locked)
        {
            Sink.Notice(SkillRecordText.LockedNotice(skill.Name));
            return false;
        }

        if (row == projectRow)
        {
            return await MoveToProjectAsync(skill, facts, cancellationToken).ConfigureAwait(false);
        }

        if (row == moveRow)
        {
            return await MoveToProfileAsync(skill, roots, cancellationToken).ConfigureAwait(false);
        }

        if (row == copyRow)
        {
            return await CopyToProfileAsync(skill, roots, cancellationToken).ConfigureAwait(false);
        }

        if (row == renameRow)
        {
            return await RenameAsync(skill, facts, page, row, cancellationToken).ConfigureAwait(false);
        }

        if (row == editRow)
        {
            Edit(skill);
            return false;
        }

        if (row == revertRow)
        {
            return await PickVersionAsync(skill, cancellationToken).ConfigureAwait(false);
        }

        if (row == deleteRow)
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
                case SkillEditOutcome.GlobalOff:
                    Sink.Notice(SkillRecordText.GlobalOffNotice(skill.Name));   // 2026-10-13: the list again, without it
                    return true;
                default:
                    Sink.Error(DeleteFailedError(deleted.Detail));
                    return false;
            }
        }

        if (row >= scopeRows.Count)
        {
            return false;
        }

        var to = scopeRows[row];
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
            case SkillEditOutcome.GlobalOff:
                Sink.Notice(SkillRecordText.GlobalOffNotice(skill.Name));   // 2026-10-13
                return true;
            default:
                Sink.Error(MoveFailedError(moved.Detail));
                return false;
        }
    }

    /// <summary>
    /// The move row's list and act (2026-10-13, the user's ask): every profile but the loaded one, the cursor on the first; the one picked
    /// checked for the folder's name, then a yes (No on the cursor), then the folder moved into its skills and the record after it. ESC goes
    /// back to the list. True when the folder moved (the facts are stale).
    /// </summary>
    private async Task<bool> MoveToProfileAsync(Skill skill, SkillRoots roots, CancellationToken cancellationToken)
    {
        string home = _settings.StorageDirectory;
        var others = Profiles.List(home).Where(name => !Profiles.NameEquals(name, _settings.ProfileName)).ToList();
        if (others.Count == 0)
        {
            Sink.Notice(NoOtherProfileNotice);
            return false;
        }

        int width = others.Max(name => name.Length);
        var page = new MenuPage(MoveToTitle(skill.Name), others.Select(name => ProfileRow(name, ProfileSkillsRoot(home, name), width)).ToList(), ScopeKeys);
        if (await _pane.PickAsync(page, 0, cancellationToken).ConfigureAwait(false) is not { Row: var row } || row >= others.Count)
        {
            return false;
        }

        string profile = others[row];
        string root = ProfileSkillsRoot(home, profile);
        // Refused ahead of the question, as the scope move is.
        string destination = Path.Combine(root, skill.FolderName);
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            Sink.Error(ProfileExistsError(skill.Name, skill.FolderName, profile));
            return false;
        }

        if (!await ConfirmAsync(MoveToPrompt(skill.Name, profile), cancellationToken).ConfigureAwait(false))
        {
            Sink.Notice(KeptNotice);
            return false;
        }

        var moved = SkillEditor.MoveToProfile(roots, skill, root);
        switch (moved.Outcome)
        {
            case SkillEditOutcome.Moved:
                _records?.MovedToProfile(skill, profile);
                Sink.Notice(MovedToNotice(skill.Name, profile));
                return true;
            case SkillEditOutcome.Exists:
                Sink.Error(ProfileExistsError(skill.Name, skill.FolderName, profile));
                return false;
            case SkillEditOutcome.Missing:
                Sink.Error(MissingError(skill.Name));
                return true;
            case SkillEditOutcome.Locked:
                Sink.Notice(SkillRecordText.LockedNotice(skill.Name));
                return false;
            default:
                Sink.Error(MoveFailedError(moved.Detail));
                return false;
        }
    }

    /// <summary>
    /// The <c>project</c> row's act (2026-11-23, the user's ask): a profile or global skill moved into the working folder's <c>.agents\skills</c>, where it
    /// becomes the repo's skill. Refused ahead of the question for a twin at that level (the folder's name or the skill's, in <c>.agents</c> or
    /// <c>.claude</c>: a same-level twin would be shadowed); after a yes, copied without the app's markers and the source deleted
    /// (<see cref="SkillEditor.MoveToProject"/>), its records with it. A disabled skill stays disabled (the trust store's list, by name). The folder is
    /// trusted again only when that's safe (the user's call): it had no skills, or it was trusted and unchanged, and nothing but the moved skill changed by
    /// the save (<see cref="SameButFor"/>). True when anything changed (the facts are stale).
    /// </summary>
    private async Task<bool> MoveToProjectAsync(Skill skill, SkillsFacts facts, CancellationToken cancellationToken)
    {
        if (facts.ProjectTarget is not { } level || _catalog is not { } catalog || _trust is not { } trust)
        {
            return false;
        }

        string label = SkillsText.FolderLabel(facts, level);
        string root = Path.Combine(level, SkillsText.ProjectSkillsFolder(claude: false));
        var agents = SkillEditor.ProfileSkillNames(root);
        // The .claude level read by Claude Code's rules (2026-11-23, review #49): a nameless skill there is named by its folder.
        var claude = SkillEditor.ProfileSkillNames(Path.Combine(level, SkillsText.ProjectSkillsFolder(claude: true)), claude: true);
        string destination = Path.Combine(root, skill.FolderName);
        if (Directory.Exists(destination) || File.Exists(destination) || agents.Folders.Contains(skill.FolderName) || claude.Folders.Contains(skill.FolderName))
        {
            Sink.Error(SkillsText.ProjectExistsError(skill.Name, skill.FolderName, label, byName: false));
            return false;
        }

        if (agents.Names.Contains(skill.Name) || claude.Names.Contains(skill.Name))
        {
            Sink.Error(SkillsText.ProjectExistsError(skill.Name, skill.FolderName, label, byName: true));
            return false;
        }

        if (!await ConfirmAsync(SkillsText.MoveToProjectPrompt(skill.Name, label), cancellationToken).ConfigureAwait(false))
        {
            Sink.Notice(KeptNotice);
            return false;
        }

        var roots = facts.Roots;
        string bound = facts.RepoRoot ?? facts.WorkingFolder ?? level;
        string key = new ProjectSkillLevel(level, root, Claude: false).Kind + "/" + skill.FolderName;
        bool disabled = skill.Disabled;
        string? disableError = null;
        string? trustError = null;
        var moved = await Task.Run(() =>
        {
            // The folder as it stood before, and its trust: safe to trust again when it held nothing, or was trusted as it stands.
            var before = catalog.ReadProjectFolder(level, bound);
            var held = trust.Trusted(level);
            bool safe = before.Order.Count == 0 || (held is not null && string.Equals(held.Fingerprint, before.Fingerprint, StringComparison.Ordinal));
            var result = SkillEditor.MoveToProject(roots, skill, root, cancellationToken);
            if (result.Outcome is not (SkillEditOutcome.Moved or SkillEditOutcome.Copied))
            {
                return result;
            }

            if (disabled)
            {
                // After the copy, not before: a name left by a failed copy would disable a later repo skill of that name.
                try
                {
                    trust.SetDisabled(level, skill.Name, true);
                }
                catch (IOException ex)
                {
                    disableError = ex.Message;
                }
            }

            if (safe && catalog.ReadProjectFolder(level, bound) is var after && SameButFor(before, after, key))
            {
                try
                {
                    // The save reads the folder again inside its hold: a change since refuses it (Changed), and the folder waits.
                    trust.Trust(level, after.Fingerprint, () => catalog.ReadProjectFolder(level, bound));
                }
                catch (IOException ex)
                {
                    trustError = ex.Message;
                }
            }

            return result;
        }, cancellationToken).ConfigureAwait(false);

        switch (moved.Outcome)
        {
            case SkillEditOutcome.Moved:
                _records?.Deleted(skill.Scope, skill.FolderName);
                var fresh = _facts();
                Sink.Notice(SkillsText.MovedToProjectNotice(skill.Name, label, MovedState(fresh, level, key)));
                ShadowNotice(fresh, level);
                break;
            case SkillEditOutcome.Copied:
                Sink.Error(SkillsText.MovedButKeptError(skill.Name, skill.Scope, moved.Detail, MovedState(_facts(), level, key) is not (ProjectMoveState.Waiting or ProjectMoveState.Unlisted)));
                break;
            case SkillEditOutcome.Exists:
                Sink.Error(SkillsText.ProjectExistsError(skill.Name, skill.FolderName, label, byName: false));
                return false;
            case SkillEditOutcome.Missing:
                Sink.Error(MissingError(skill.Name));
                return true;
            case SkillEditOutcome.GlobalOff:
                Sink.Notice(SkillRecordText.GlobalOffNotice(skill.Name));
                return true;
            case SkillEditOutcome.Locked:
                Sink.Notice(SkillRecordText.LockedNotice(skill.Name));
                return false;
            default:
                Sink.Error(MoveFailedError(moved.Detail));
                return false;
        }

        if (disableError is not null)
        {
            Sink.Error(SkillRecordText.DisableFailedError(skill.Name, disableError));
        }

        if (trustError is not null)
        {
            Sink.Error(SkillsText.TrustSaveFailedError(label, trustError));
        }

        return true;
    }

    /// <summary>Where a skill moved into <paramref name="level"/> under <paramref name="key"/> stands in <paramref name="facts"/> read after the move.</summary>
    private static ProjectMoveState MovedState(SkillsFacts facts, string level, string key)
    {
        bool Here(Skill s) => s.Scope == SkillScope.Project && string.Equals(s.TrustFolder, level, StringComparison.Ordinal) && string.Equals(s.ProjectKey, key, StringComparison.Ordinal);
        if (facts.Skills.FirstOrDefault(Here) is { } offered)
        {
            return offered.Disabled ? ProjectMoveState.Disabled : ProjectMoveState.Offered;
        }

        if (facts.ProjectPending.FirstOrDefault(p => Here(p.Skill)) is { } pending)
        {
            return pending.Reason switch
            {
                ProjectPendingReason.NotTrusted => ProjectMoveState.Waiting,
                ProjectPendingReason.OverCap or ProjectPendingReason.PastFolderCap => ProjectMoveState.OverCap,
                _ => ProjectMoveState.Unlisted,
            };
        }

        return facts.Shadowed.Any(Here) ? ProjectMoveState.Shadowed : ProjectMoveState.Unlisted;
    }

    /// <summary>
    /// Whether <paramref name="after"/> is <paramref name="before"/> but for the one skill <paramref name="key"/> (2026-11-23): the same hashes apart from it, and
    /// the same keys in the scan's order as a set apart from it (past the 50 cap a skill shows in the order alone). A stranger's change that slipped in
    /// between the reads is never trusted unseen. Pure.
    /// </summary>
    public static bool SameButFor(ProjectFolderState before, ProjectFolderState after, string key)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(key);
        var was = before.Hashes.Where(p => !string.Equals(p.Key, key, StringComparison.Ordinal)).ToList();
        var now = after.Hashes.Where(p => !string.Equals(p.Key, key, StringComparison.Ordinal)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        if (was.Count != now.Count || was.Any(p => !now.TryGetValue(p.Key, out var hash) || !string.Equals(hash, p.Value, StringComparison.Ordinal)))
        {
            return false;
        }

        var wasKeys = before.Order.Where(k => !string.Equals(k, key, StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        return wasKeys.SetEquals(after.Order.Where(k => !string.Equals(k, key, StringComparison.Ordinal)));
    }

    /// <summary>
    /// The copy row's list and act (2026-10-25, the user's ask; the move's shape): every profile but the loaded one; the one picked checked
    /// for the folder's name and the skill's name (a same-scope twin would be shadowed), then a yes (No on the cursor), then the folder copied
    /// on the pool (<see cref="SkillEditor.CopyToProfile"/>). No records: the other profile reconciles its skills when it loads. False
    /// always: nothing in this profile changed, so the list's facts stand.
    /// </summary>
    private async Task<bool> CopyToProfileAsync(Skill skill, SkillRoots roots, CancellationToken cancellationToken)
    {
        string home = _settings.StorageDirectory;
        var others = Profiles.List(home).Where(name => !Profiles.NameEquals(name, _settings.ProfileName)).ToList();
        if (others.Count == 0)
        {
            Sink.Notice(NoOtherProfileForCopyNotice);
            return false;
        }

        int width = others.Max(name => name.Length);
        var page = new MenuPage(CopyToTitle(skill.Name), others.Select(name => ProfileRow(name, ProfileSkillsRoot(home, name), width)).ToList(), ScopeKeys);
        if (await _pane.PickAsync(page, 0, cancellationToken).ConfigureAwait(false) is not { Row: var row } || row >= others.Count)
        {
            return false;
        }

        string profile = others[row];
        string root = ProfileSkillsRoot(home, profile);
        // Refused ahead of the question, as the move is: the folder's name, or a skill of the same name there.
        string destination = Path.Combine(root, skill.FolderName);
        var (folders, names) = SkillEditor.ProfileSkillNames(root);
        if (Directory.Exists(destination) || File.Exists(destination) || folders.Contains(skill.FolderName))
        {
            Sink.Error(CopyExistsError(skill.Name, skill.FolderName, profile));
            return false;
        }

        if (names.Contains(skill.Name))
        {
            Sink.Error(CopyExistsError(skill.Name, skill.FolderName, profile, byName: true));
            return false;
        }

        if (!await ConfirmAsync(CopyToPrompt(skill.Name, profile), cancellationToken).ConfigureAwait(false))
        {
            Sink.Notice(KeptNotice);
            return false;
        }

        var copied = await Task.Run(() => SkillEditor.CopyToProfile(roots, skill, root, cancellationToken), cancellationToken).ConfigureAwait(false);
        switch (copied.Outcome)
        {
            case SkillEditOutcome.Copied:
                Sink.Notice(CopiedToNotice(skill.Name, profile));
                break;
            case SkillEditOutcome.Exists:
                Sink.Error(CopyExistsError(skill.Name, skill.FolderName, profile));
                break;
            case SkillEditOutcome.Missing:
                Sink.Error(MissingError(skill.Name));
                break;
            default:
                Sink.Error(CopyFailedError(copied.Detail));
                break;
        }

        return false;
    }

    /// <summary>
    /// The lock row's act (2026-10-07): <see cref="SkillRecords.SetLocked"/> (the lock file and its event), or the file alone without the
    /// records; at once, no yes/no (nothing is lost), the notice on the list's status line. True when it changed (the facts are stale).
    /// </summary>
    private bool SetLocked(Skill skill, bool locked)
    {
        string? error = _records is { } records ? records.SetLocked(skill, locked) : SkillLock.Set(skill.Directory, locked, TimeProvider.System.GetUtcNow());
        if (error is not null)
        {
            Sink.Error(SkillRecordText.LockFailedError(skill.Name, error));
            return false;
        }

        Sink.Notice(SkillRecordText.LockSetNotice(skill.Name, locked));
        return true;
    }

    /// <summary>
    /// The disable row's act (2026-10-09): <see cref="SkillRecords.SetDisabled"/> (the sidecar and its event), or the file alone without the
    /// records; at once, no yes/no (nothing is lost), the notice on the list's status line. True when it changed (the facts are stale).
    /// </summary>
    private bool SetDisabled(Skill skill, bool disabled)
    {
        string? error = _records is { } records ? records.SetDisabled(skill, disabled) : SkillDisable.Set(skill.Directory, disabled, TimeProvider.System.GetUtcNow());
        if (error is not null)
        {
            Sink.Error(SkillRecordText.DisableFailedError(skill.Name, error));
            return false;
        }

        Sink.Notice(SkillRecordText.DisableSetNotice(skill.Name, disabled));
        return true;
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
    /// Offered tab's version moves). Delete is <c>d</c> (2026-10-16, the user's ask, <see cref="MenuPage.Removes"/>), handled before Enter's
    /// put back, so it never restores a version.</para>
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
                Removes = _forget is not null,
            };
            if (await _pane.PickAsync(page, at, cancellationToken).ConfigureAwait(false) is not { Row: var picked } pick || picked >= versions.Count)
            {
                return forgot;
            }

            if ((pick.Button >= 0 || pick.Remove) && _forget is not null)
            {
                bool all = !pick.Remove && VersionButtons[pick.Button].Key == QueueMenu.ClearAllKey;
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

            if (skill.Locked || skill.Disabled)
            {
                // Locked (2026-10-07) or disabled (2026-10-09): nothing put back; the list stays, its d and c still forgetting kept versions.
                Sink.Notice(skill.Locked ? SkillRecordText.LockedNotice(skill.Name) : SkillRecordText.DisabledNotice(skill.Name));
                cursor = picked;
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
            case SkillEditOutcome.GlobalOff:
                Sink.Notice(SkillRecordText.GlobalOffNotice(skill.Name));   // 2026-10-13
                return true;
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

    // ── The repo's skills (2026-11-23, feature idea #16) ─────────────────────

    /// <summary>
    /// Enter on a project sub-tab row: a trust row, or a not-offered skill in a folder not trusted, asks to trust the folder; an untrust row takes the trust back;
    /// a trusted skill opens its page; a skill not offered for another reason says why. Changed is true when the facts are stale; Trusted names the folder a
    /// trust was saved for (the shadow notice after the rescan).
    /// </summary>
    private async Task<(bool Changed, string? Trusted)> ProjectRowAsync(ProjectRow row, SkillsFacts facts, CancellationToken cancellationToken)
    {
        switch (row.Kind)
        {
            case ProjectRowKind.Trust when row.Folder is { } folder:
                return await TrustAsync(folder, facts, cancellationToken).ConfigureAwait(false);
            case ProjectRowKind.Pending when row.Folder is { Trust: not ProjectFolderTrust.Trusted } folder:
                return await TrustAsync(folder, facts, cancellationToken).ConfigureAwait(false);
            case ProjectRowKind.Pending when row.Skill is { } skill && row.Reason is { } reason:
                Sink.Notice(SkillsText.ProjectPendingNotice(skill.Name, reason));
                return (false, null);
            case ProjectRowKind.Untrust when row.Folder is { } folder:
                return (await UntrustAsync(folder, facts, cancellationToken).ConfigureAwait(false), null);
            case ProjectRowKind.Skill when row.Skill is { } skill:
                return (await ProjectPageAsync(skill, facts, cancellationToken).ConfigureAwait(false), null);
            default:
                return (false, null);
        }
    }

    /// <summary>One skill as the trust question shows it: its key, name, description, mark (new, changed…), file count, acting mark, the text read and the kept copy for a diff.</summary>
    private sealed record TrustEntry(string Key, string Name, string Description, string Mark, string Files, bool Acting, string? Text, string? Old, string FilePath);

    /// <summary>
    /// The trust question (2026-11-23): the folder's skills as read now, each with a View of its whole <c>SKILL.md</c> (a diff against the kept copy when it
    /// changed since a trust), the removed ones named; Trust saves what was shown, read again inside the save's hold: a folder changed meanwhile saves
    /// nothing and the question opens again on the new content. The reads and the save run off the screen thread (an offline share, the hold's wait).
    /// </summary>
    private async Task<(bool Changed, string? Trusted)> TrustAsync(ProjectFolder folder, SkillsFacts facts, CancellationToken cancellationToken)
    {
        if (_catalog is not { } catalog || _trust is not { } trust)
        {
            return (false, null);
        }

        string label = SkillsText.FolderLabel(facts, folder.Level);
        string bound = facts.RepoRoot ?? facts.WorkingFolder ?? folder.Level;
        var known = facts.Skills.Concat(facts.Shadowed).Concat(facts.ProjectPending.Select(p => p.Skill))
            .Where(s => s.Scope == SkillScope.Project && s.ProjectKey is not null && string.Equals(s.TrustFolder, folder.Level, StringComparison.Ordinal))
            .GroupBy(s => s.ProjectKey!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        while (true)
        {
            var (state, held, entries, removed) = await Task.Run(() => ReadForTrust(catalog, trust, folder.Level, bound, known), cancellationToken).ConfigureAwait(false);
            int width = Math.Max(4, entries.Select(e => SkillsText.ShownName(e.Name).Length).Concat(removed.Select(r => SkillsText.ShownName(r).Length)).DefaultIfEmpty(0).Max());
            // The actions first (2026-11-23, hand check 1, the user's call): trust and cancel, then a bare rule, then the views and the
            // removed skills, so cancel never lands far down a long list. The rule is a heading, never a stop.
            var rows = new List<string> { SkillsText.TrustQuestionRow(label), SkillsText.CancelRow };
            int first = rows.Count;
            IReadOnlySet<int>? headings = null;
            if (entries.Count + removed.Count > 0)
            {
                headings = new HashSet<int> { rows.Count };
                rows.Add(UI.SectionRule.Bare);
                first = rows.Count;
            }

            rows.AddRange(entries.Select(e => SkillsText.TrustViewRow(e.Name, width, e.Mark, e.Description, e.Files, e.Acting)));
            rows.AddRange(removed.Select(r => SkillsText.TrustRemovedRow(r, width)));
            string caption = held is null ? SkillsText.TrustCaption(label)
                : SkillsText.TrustChangedCaption(label, entries.Count(e => e.Mark == SkillsText.NewMark), entries.Count(e => e.Mark is SkillsText.ChangedMark or SkillsText.FilesChangedMark), removed.Count);
            var page = new MenuPage(SkillsText.TrustTitle, rows, SkillsText.TrustKeys) { Caption = caption, CaptionGrows = true, Headings = headings };
            // On the first view, so a reflex Enter views and never trusts; on cancel with nothing to view.
            int cursor = entries.Count > 0 ? first : 1;
            while (true)
            {
                if (await _pane.PickAsync(page, cursor, cancellationToken).ConfigureAwait(false) is not { Row: var row })
                {
                    return (false, null);
                }

                if (row >= first && row < first + entries.Count)
                {
                    // View: exactly what was read, never run or rendered as markdown (a stranger's link text can't hide where it goes).
                    var entry = entries[row - first];
                    IRenderable content = entry.Text is null ? new Text(SkillsText.NothingToViewText)
                        : entry.Old is not null && FileDiff.Of(entry.FilePath, entry.Old, entry.Text) is { } diff ? new DiffView(null, diff, int.MaxValue)
                        // Only its bundled files changed (2026-11-23, hand check 1): say so over the text, which is the one trusted.
                        : entry.Mark == SkillsText.FilesChangedMark ? new Rows(new Markup(Theme.DimMarkup(SkillsText.FilesChangedViewNote)), new Text(""), new Text(entry.Text))
                        : new Text(entry.Text);
                    if (_view is not null)
                    {
                        await _view(SkillsText.ViewLabel(entry.Name), content, cancellationToken).ConfigureAwait(false);
                    }

                    cursor = row;
                    continue;
                }

                if (row >= first + entries.Count && row < first + entries.Count + removed.Count)
                {
                    // A removed skill's row has nothing to view (2026-11-23, review #75): the question stays open.
                    cursor = row;
                    continue;
                }

                if (row != 0)
                {
                    return (false, null);   // cancel
                }

                ProjectTrustSave saved;
                try
                {
                    saved = await Task.Run(() => trust.Trust(folder.Level, state.Fingerprint, () => catalog.ReadProjectFolder(folder.Level, bound)), cancellationToken).ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    Sink.Error(SkillsText.TrustSaveFailedError(label, ex.Message));
                    return (false, null);
                }

                if (saved == ProjectTrustSave.Changed)
                {
                    Sink.Notice(SkillsText.ChangedWhileReviewingNotice(label));
                    break;   // the question again, on the new content
                }

                // The skills the trust covers: the hashed ones, within the folder's cap (2026-11-23, review #6/#52).
                Sink.Notice(SkillsText.TrustedNotice(label, state.Hashes.Count));
                return (true, folder.Level);
            }
        }
    }

    // The question's read, off the screen thread: the folder now, its trust, each skill's line (the file count walks its folder) and the removed ones.
    // Each skill's name, description and acting mark come from the SKILL.md read now (2026-11-23, review #44: after "changed while reviewing" the
    // rows kept the first read's), by Claude Code's rules for a .claude one; the facts', then the folder name, when it won't parse.
    private static (ProjectFolderState State, ProjectTrustKind? Held, IReadOnlyList<TrustEntry> Entries, IReadOnlyList<string> Removed) ReadForTrust(
        SkillCatalog catalog, ProjectSkillTrust trust, string level, string bound, IReadOnlyDictionary<string, Skill> known)
    {
        var state = catalog.ReadProjectFolder(level, bound);
        var held = trust.Trusted(level);
        var entries = new List<TrustEntry>();
        foreach (string key in state.Order)
        {
            known.TryGetValue(key, out var skill);
            string directory = skill?.Directory ?? Path.Combine(level, key.Replace('/', Path.DirectorySeparatorChar));
            string? text = state.Copies.GetValueOrDefault(key);
            SkillFrontmatter? read = null;
            if (text is not null)
            {
                bool claude = key.StartsWith(new ProjectSkillLevel(level, "", Claude: true).Kind + "/", StringComparison.Ordinal);
                bool parsed = claude
                    ? SkillFrontmatter.TryParseClaude(text, Path.GetFileName(directory), out read, out _, out _)
                    : SkillFrontmatter.TryParse(text, out read, out _, out _);
                read = parsed ? read : null;
            }

            string name = read?.Name ?? skill?.Name ?? Path.GetFileName(directory);
            string description = read is not null ? SkillCatalog.Cap(read.Description) : skill?.Description ?? "";
            bool acting = read?.HasActingKeys ?? skill?.Acting ?? false;
            int files;
            bool more;
            try
            {
                files = SkillCatalog.ProjectResources(directory, out more).Count;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                files = 0;
                more = false;
            }

            string mark = "";
            string? old = null;

            // Only a skill within the folder's cap is hashed, so only one can be new or changed (2026-11-23, review #6/#52): one past it was marked
            // new on every re-ask.
            if (held is not null && state.Hashes.ContainsKey(key))
            {
                if (!held.Skills.TryGetValue(key, out var was))
                {
                    mark = SkillsText.NewMark;
                }
                else if (!string.Equals(was, state.Hashes.GetValueOrDefault(key), StringComparison.Ordinal))
                {
                    old = trust.CopyOf(level, key);
                    mark = old is not null && text is not null && string.Equals(old, text, StringComparison.Ordinal) ? SkillsText.FilesChangedMark : SkillsText.ChangedMark;
                    old = mark == SkillsText.ChangedMark ? old : null;
                }
            }

            entries.Add(new TrustEntry(key, name, description, mark, SkillsText.FilesNote(files, more), acting, text, old, Path.Combine(directory, SkillCatalog.FileName)));
        }

        var removed = held is null ? [] : held.Skills.Keys.Where(k => !state.Order.Contains(k, StringComparer.Ordinal)).Select(k => k[(k.LastIndexOf('/') + 1)..]).ToList();
        return (state, held, entries, removed);
    }

    /// <summary>The untrust (2026-11-23), after a yes: new loads stop at once; a skill this conversation already loaded stays in it, and the notice says so.</summary>
    private async Task<bool> UntrustAsync(ProjectFolder folder, SkillsFacts facts, CancellationToken cancellationToken)
    {
        if (_trust is not { } trust)
        {
            return false;
        }

        string label = SkillsText.FolderLabel(facts, folder.Level);
        if (!await ConfirmAsync(SkillsText.UntrustPrompt(label), cancellationToken).ConfigureAwait(false))
        {
            Sink.Notice(KeptNotice);
            return false;
        }

        try
        {
            await Task.Run(() => trust.Untrust(folder.Level), cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            Sink.Error(SkillsText.TrustSaveFailedError(label, ex.Message));
            return false;
        }

        // Only the folder's offered skills (2026-11-23, review #5): a shadowed one's name is another scope's skill, which stays loaded.
        var names = facts.Skills.Where(s => s.Scope == SkillScope.Project && string.Equals(s.TrustFolder, folder.Level, StringComparison.Ordinal)).Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var loaded = (_session?.LoadedSkills ?? []).Select(l => l.Name).Where(names.Contains).ToList();
        Sink.Notice(SkillsText.UntrustedNotice(label, loaded));
        return true;
    }

    /// <summary>
    /// A trusted project skill's page (2026-11-23): edit (the user's editor; a change asks for trust again), a copy into the profile or global skills,
    /// disable or enable, kept in the profile, and (later that day, the user's ask) delete, after a yes. No rename, move, lock or revert: the skill is the
    /// repo's.
    /// </summary>
    private async Task<bool> ProjectPageAsync(Skill skill, SkillsFacts facts, CancellationToken cancellationToken)
    {
        var roots = facts.Roots;
        var copies = roots.WritableScopes;
        var rows = new List<string> { SkillsText.ProjectEditRow };
        rows.AddRange(copies.Select(SkillsText.ProjectCopyRow));
        int disableRow = rows.Count;
        rows.Add(SkillsText.ProjectDisableRow(skill.Disabled));
        // The delete (2026-11-23, the user's ask): last, its own index; handled before the copy rows' fall-through below.
        int deleteRow = skill.TrustFolder is not null && skill.ProjectKey is not null ? rows.Count : -1;
        if (deleteRow >= 0)
        {
            rows.Add(SkillsText.ProjectDeleteRow(_bin?.Invoke() is not null));
        }

        string label = SkillsText.FolderLabel(facts, skill.TrustFolder ?? skill.Directory);
        var page = new MenuPage(ScopeTitle(skill.Name), rows, ScopeKeys) { Caption = SkillsText.ProjectPageCaption(label) };
        if (await _pane.PickAsync(page, 0, cancellationToken).ConfigureAwait(false) is not { Row: var row })
        {
            return false;
        }

        if (row == 0)
        {
            Edit(skill);
            return false;
        }

        if (row == disableRow)
        {
            return await SetProjectDisabledAsync(skill, cancellationToken).ConfigureAwait(false);
        }

        if (row == deleteRow)
        {
            return await DeleteProjectAsync(skill, facts, cancellationToken).ConfigureAwait(false);
        }

        return row - 1 < copies.Count && await CopyProjectAsync(skill, copies[row - 1], roots, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A project skill's delete (2026-11-23, the user's ask): the folder must sit right in its level's skills folder. The way is decided before the
    /// question: the bin when one is in use and its probe takes the folder, else for good with the reason; a link is removed alone, never what it points
    /// at. A drive that turns out to have no bin asks again, for good. After it, the folder stays trusted when that's safe (trusted and unchanged but for
    /// this skill, <see cref="SameButFor"/>), and its name leaves the disabled list unless a twin of that name remains at the level. True when anything
    /// may have changed (the facts are stale).
    /// </summary>
    private async Task<bool> DeleteProjectAsync(Skill skill, SkillsFacts facts, CancellationToken cancellationToken)
    {
        if (skill.TrustFolder is not { } level || skill.ProjectKey is not { } key)
        {
            return false;
        }

        string label = SkillsText.FolderLabel(facts, level);
        string expected = Path.Combine(level, SkillsText.ProjectSkillsFolder(skill.ClaudeRules));
        string directory = Path.TrimEndingDirectorySeparator(skill.Directory);
        bool link;
        try
        {
            var info = new DirectoryInfo(directory);
            link = info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0;
            if (!info.Exists || !string.Equals(Path.GetDirectoryName(directory), expected, CaseRule.Comparison(expected)))
            {
                Sink.Error(MissingError(skill.Name));
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Sink.Error(MissingError(skill.Name));
            return true;
        }

        // The way, decided once: the yes below is to this question, so the delete is handed this bin (or none), never the setting again.
        IRecycleBin? bin = null;
        string? why = null;
        if (!link && _bin?.Invoke() is { } recycle)
        {
            if (await Task.Run(() => recycle.Probe(directory), cancellationToken).ConfigureAwait(false) is { } refused)
            {
                why = refused.Outcome == RecycleOutcome.NoBin || refused.Message.Length == 0 ? FileText.RecycleNoBinHere : refused.Message;
            }
            else
            {
                bin = recycle;
            }
        }

        if (!await ConfirmAsync(SkillsText.ProjectDeletePrompt(skill.Name, label, skill.ClaudeRules, bin is not null, why, link), cancellationToken).ConfigureAwait(false))
        {
            Sink.Notice(KeptNotice);
            return false;
        }

        var catalog = _catalog;
        var trust = _trust;
        string bound = facts.RepoRoot ?? facts.WorkingFolder ?? level;
        ProjectFolderState? before = null;
        ProjectTrustKind? held = null;
        if (catalog is not null && trust is not null)
        {
            (before, held) = await Task.Run(() => (catalog.ReadProjectFolder(level, bound), trust.Trusted(level)), cancellationToken).ConfigureAwait(false);
        }

        var way = DeleteWay.Deleted;
        try
        {
            if (bin is not null)
            {
                var moved = await Task.Run(() => bin.Move(directory), cancellationToken).ConfigureAwait(false);
                switch (moved.Outcome)
                {
                    case RecycleOutcome.Recycled:
                        way = DeleteWay.Recycled;
                        break;
                    case RecycleOutcome.NoBin:
                        // No bin there after all: nothing went, and nothing is deleted for good behind the question's back.
                        why = FileText.RecycleNoBinHere;
                        if (!await ConfirmAsync(SkillsText.ProjectDeletePrompt(skill.Name, label, skill.ClaudeRules, binned: false, why), cancellationToken).ConfigureAwait(false))
                        {
                            Sink.Notice(KeptNotice);
                            return false;
                        }

                        break;
                    default:
                        Sink.Error(SkillsText.ProjectDeleteFailedError(skill.Name, moved.Message.Length > 0 ? moved.Message : FileText.RecycleNoAnswer));
                        return true;
                }
            }

            if (way != DeleteWay.Recycled)
            {
                await Task.Run(() => FileDeletes.Remove(directory, isDirectory: true, isLink: link, bin: null), cancellationToken).ConfigureAwait(false);
                way = why is null ? DeleteWay.Deleted : DeleteWay.DeletedNoBin;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Sink.Error(SkillsText.ProjectDeleteFailedError(skill.Name, ex.Message));
            return true;
        }

        bool trusted = false;
        string? trustError = null;
        string? disableError = null;
        bool twin = facts.Skills.Concat(facts.Shadowed).Concat(facts.ProjectPending.Select(p => p.Skill))
            .Any(s => s.Scope == SkillScope.Project && string.Equals(s.TrustFolder, level, StringComparison.Ordinal)
                && !string.Equals(s.ProjectKey, key, StringComparison.Ordinal) && string.Equals(s.Name, skill.Name, StringComparison.Ordinal));
        if (catalog is not null && trust is not null)
        {
            await Task.Run(() =>
            {
                if (held is not null && before is not null && string.Equals(held.Fingerprint, before.Fingerprint, StringComparison.Ordinal)
                    && catalog.ReadProjectFolder(level, bound) is var after && SameButFor(before, after, key))
                {
                    try
                    {
                        trusted = trust.Trust(level, after.Fingerprint, () => catalog.ReadProjectFolder(level, bound)) == ProjectTrustSave.Saved;
                    }
                    catch (IOException ex)
                    {
                        trustError = ex.Message;
                    }
                }

                if (skill.Disabled && !twin)
                {
                    // Off the list, so a later skill of that name isn't born disabled.
                    try
                    {
                        trust.SetDisabled(level, skill.Name, false);
                    }
                    catch (IOException ex)
                    {
                        disableError = ex.Message;
                    }
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        // A loaded "x" is this skill's only when it was the one offered under that name (a shadowed one's was your own skill's).
        bool offered = facts.Skills.Any(s => s.Scope == SkillScope.Project && string.Equals(s.ProjectKey, key, StringComparison.Ordinal) && string.Equals(s.TrustFolder, level, StringComparison.Ordinal));
        bool loaded = offered && (_session?.LoadedSkills ?? []).Any(l => string.Equals(l.Name, skill.Name, StringComparison.OrdinalIgnoreCase));
        Sink.Notice(SkillsText.ProjectDeletedNotice(skill.Name, label, way, why, link, trusted, loaded));
        if (trustError is not null)
        {
            Sink.Error(SkillsText.TrustSaveFailedError(label, trustError));
        }

        if (disableError is not null)
        {
            Sink.Error(SkillRecordText.DisableFailedError(skill.Name, disableError));
        }

        return true;
    }

    /// <summary>
    /// The copy (2026-11-23): a name the editor could never take refused; a root holding the folder or the name refused ahead of the yes; then the folder
    /// copied without the repo's markers, a <c>.claude</c> skill's defaults written into the copy. The copy shadows the repo's.
    /// </summary>
    private async Task<bool> CopyProjectAsync(Skill skill, SkillScope to, SkillRoots roots, CancellationToken cancellationToken)
    {
        if (!SkillFrontmatter.IsValidName(skill.Name))
        {
            Sink.Error(SkillsText.ProjectCopyBadNameError(skill.Name));
            return false;
        }

        // Only what was trusted is copied (2026-11-23, review #102): a skill edited since its trust is refused here and again just before the copy.
        if (_catalog is { } catalog && !catalog.StillTrusted(skill))
        {
            Sink.Error(SkillsText.ProjectCopyChangedError(skill.Name));
            return true;
        }

        // The copy is named by its skill (review #55): a repo folder named apart from its skill would land under a name the profile's rules refuse.
        string root = roots.Of(to);
        string destination = Path.Combine(root, skill.Name);
        if (Directory.Exists(destination) || File.Exists(destination))
        {
            Sink.Error(ExistsError(skill.Name, skill.Name, to));
            return false;
        }

        if (SkillEditor.ProfileSkillNames(root).Names.Contains(skill.Name))
        {
            Sink.Error(SkillsText.ProjectCopyNameTakenError(skill.Name, to));
            return false;
        }

        if (!await ConfirmAsync(SkillsText.ProjectCopyPrompt(skill.Name, to), cancellationToken).ConfigureAwait(false))
        {
            Sink.Notice(KeptNotice);
            return false;
        }

        Func<string, string>? rewrite = skill.ClaudeRules ? text => ClaudeDefaults(text, skill.FolderName) : null;
        var copied = await Task.Run(
            () => _catalog is { } again && !again.StillTrusted(skill)
                ? null
                : SkillEditor.CopyFolder(skill.Directory, root, cancellationToken, dropMarkers: true, rewriteSkill: rewrite, destinationName: skill.Name),
            cancellationToken).ConfigureAwait(false);
        if (copied is null)
        {
            Sink.Error(SkillsText.ProjectCopyChangedError(skill.Name));
            return true;
        }

        switch (copied.Outcome)
        {
            case SkillEditOutcome.Copied:
                Sink.Notice(SkillsText.ProjectCopiedNotice(skill.Name, to));
                return true;
            case SkillEditOutcome.Exists:
                Sink.Error(ExistsError(skill.Name, skill.Name, to));
                return false;
            case SkillEditOutcome.Missing:
                Sink.Error(MissingError(skill.Name));
                return true;
            default:
                Sink.Error(CopyFailedError(copied.Detail));
                return false;
        }
    }

    /// <summary>A <c>.claude</c> skill's text with Claude Code's defaults written in (the folder's name, the body's first line), so the profile's rules read it; unchanged when it won't parse.</summary>
    public static string ClaudeDefaults(string text, string folderName)
    {
        ArgumentNullException.ThrowIfNull(text);
        return SkillFrontmatter.TryParseClaude(text, folderName, out var frontmatter, out var body, out _) && frontmatter is not null
            ? SkillFrontmatter.Write(frontmatter.Name, frontmatter.Description, frontmatter.OtherLines, body)
            : text;
    }

    /// <summary>A project skill disabled or enabled (2026-11-23): kept in the profile's trust file, never a marker in the repo; off the screen thread.</summary>
    private async Task<bool> SetProjectDisabledAsync(Skill skill, CancellationToken cancellationToken)
    {
        if (_trust is not { } trust || skill.TrustFolder is not { } folder)
        {
            return false;
        }

        bool disabled = !skill.Disabled;
        try
        {
            await Task.Run(() => trust.SetDisabled(folder, skill.Name, disabled), cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            Sink.Error(SkillRecordText.DisableFailedError(skill.Name, ex.Message));
            return false;
        }

        Sink.Notice(SkillRecordText.DisableSetNotice(skill.Name, disabled));
        return true;
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
