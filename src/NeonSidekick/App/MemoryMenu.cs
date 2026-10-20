using System.Globalization;
using NeonSidekick.Memory;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// The <c>/memory</c> screen: one row per stored memory (date, then text), Enter removes the
/// highlighted one and shows the list again, ESC backs out. A list in the bottom pane
/// (<see cref="MenuPane"/>, the removal notice on its status line above the re-shown rows) — or,
/// on a console without the pane, the same themed <see cref="SelectionPrompt{T}"/> over
/// <see cref="PromptResult{T}"/> as the settings menu, the notices in the transcript. Rebuilt from
/// the store after every removal so the rows are always what the file holds; what ends the visit
/// (the last row removed, a failed removal) is said in the transcript once the pane has closed.
///
/// <para>A row's removal asks first (<see cref="RemovePrompt"/>, the cursor on No; 2026-10-07, the user's call in the consistency pass:
/// every list's remove asks, where this one alone once went at Enter on the grounds that the row was chosen deliberately).
/// <c>/memory forget</c> keeps its own confirmation, everything at once (2026-09-22: that wipe was <c>/forget</c>, its own command,
/// until the word folded in here). A console that cannot show menus gets the numbered list instead, and removes nothing.</para>
///
/// <para>On the pane the title row carries Memory's switch (2026-10-03, the user's ask: "actions at the top, similar to ...
/// Tools › Shell allowed commands' ask and yolo"): from 2026-10-04 <c>Memory mode</c>'s three buttons (read-write, read-only, off; w, o, x
/// from 2026-10-07), the lit one the mode in force; since later on 2026-10-13 (the user's call, <c>/queue</c>'s shape) one button,
/// <see cref="ModeButton"/>, key m, showing the mode in force and stepping to the next (<see cref="NextMode"/>) at each press — the
/// Memory mode row's own save and notice (<see cref="SettingsMenu.SetMemoryMode"/>) — the list shown again. So the switch is always in reach, the pane opens with nothing remembered too, on one dim row, and
/// stays open when the last row goes.</para>
///
/// <para>Under the list (2026-10-07, the user's ask: one cut line said little of a long memory), the highlighted memory in full: the
/// pane's footer band (<see cref="MenuPage.Footer"/>, <see cref="MenuPane.FooterRows"/> rows), word-wrapped, so a 300-character
/// memory reads whole down to about 80 columns. No last line: the date is on the row already.</para>
///
/// <para>Since 2026-10-13 (the user's ask) Enter or a double-click on a row offers a choice first (<see cref="ActionRows"/>): <b>edit</b>
/// the text on the input row under the list (<see cref="MemoryStore.Edit"/>), <b>disable</b> it — kept and listed greyed out, but not
/// offered to the model (<see cref="MemoryEntry.Disabled"/>) — or <b>enable</b> it again, or <b>forget</b> it with the confirmation as
/// before. The title row's <see cref="AddButton"/> (a) types a new memory there (<see cref="MemoryStore.Add"/>, <c>/remember</c>'s
/// outcomes). Edit and add need the input row (the screen's), so a console without one has the choice without them.</para>
///
/// <para>Delete (2026-10-16, the user's ask: every list that removes a row) goes straight to the forget question, skipping the choice
/// (<see cref="MenuPage.Removes"/> while there are rows).</para>
/// </summary>
internal sealed class MemoryMenu
{
    // The label and the key hints: the pane shows the label as its title and the keys in its hint
    // row; the prompt host joins them (SettingsMenu.PromptTitle). Pinned.
    public const string Title = ChatScreen.MemoryToolGlyph + " Memory";   // the glyph the toolbar wears for the pane too (2026-09-22)
    public const string Keys = "Enter = choose · a = add · Esc = close";   // close since 2026-10-07: the pane's top level; choose and add since 2026-10-13

    /// <summary>The list's hint on a console without the pane (no title row, so no add). Pinned.</summary>
    public const string PlainKeys = "Enter = choose · Esc = close";
    public const string EmptyNotice = "(" + NoticeGlyphs.Memory + "nothing remembered)";   // the disk since 2026-09-22

    /// <summary>The pane's hints with Memory mode's button on the title row (2026-10-03; one cycling button since later on 2026-10-13): the rows, then with nothing remembered. Pinned.</summary>
    public const string SwitchKeys = "Enter = choose · a = add · m = memory mode · Esc = close";
    public const string EmptySwitchKeys = "a = add · m = memory mode · Esc = close";

    /// <summary>The title row's add button (2026-10-13, the user's ask: a new memory from the pane): after Memory mode's. Pinned.</summary>
    public const string AddButton = "+ add";
    public const char AddKey = 'a';

    /// <summary>The choice a row opens (2026-10-13, the user's ask): edit, disable or enable, forget — in <see cref="ActionRows"/>' order.</summary>
    public enum RowAction
    {
        Edit,
        Toggle,
        Forget,
    }

    /// <summary>The words of the choice (2026-10-13). Pinned.</summary>
    public const string EditWord = "edit";
    public const string DisableWord = "disable";
    public const string EnableWord = "enable";
    public const string ForgetWord = "forget";

    /// <summary>
    /// The choice's rows for a memory (2026-10-13, the user's ask): edit, disable (enable while it is disabled), forget, each with what it
    /// does dim beside it. Pinned.
    /// </summary>
    public static IReadOnlyList<string> ActionRows(bool disabled) =>
    [
        Markup.Escape(EditWord.PadRight(9)) + Theme.DimMarkup("change its text"),
        Markup.Escape((disabled ? EnableWord : DisableWord).PadRight(9)) + Theme.DimMarkup(disabled ? "offer it to the model again" : "keep it, but don't offer it to the model"),
        Markup.Escape(ForgetWord.PadRight(9)) + Theme.DimMarkup("remove it"),
    ];

    /// <summary>The choice's title: the memory, quoted (2026-10-13). Pinned.</summary>
    public static string ActionTitle(string text) => $"{NoticeGlyphs.Memory}\"{text}\"";

    /// <summary>The choice's hint (2026-10-13). Pinned.</summary>
    public const string ActionKeys = "Enter = choose · Esc = back";

    /// <summary>The input row's hint while a memory is edited or added (2026-10-13). Pinned.</summary>
    public const string EditKeys = "Enter = save · Esc = back";

    public static string EditedNotice(string text) => $"({NoticeGlyphs.Memory}edited: {text})";

    public static string DisabledNotice(string text) => $"({NoticeGlyphs.Memory}disabled: {text})";

    public static string EnabledNotice(string text) => $"({NoticeGlyphs.Memory}enabled: {text})";

    /// <summary>An edit onto another memory's text (2026-10-13). Pinned.</summary>
    public static string DuplicateError(string text) => $"Another memory already says that: {text}";

    /// <summary>An edit or a toggle whose memory went from the file meanwhile (changed outside the app). Pinned.</summary>
    public const string MissingError = "That memory is no longer in the file.";

    public static string ChangeFailedError(string detail) => $"Could not change the memory: {detail}";

    /// <summary>The footer's last line under a disabled memory (2026-10-13). Pinned.</summary>
    public const string DisabledFooter = "disabled: not offered to the model";

    /// <summary>The key of Memory mode's button (later on 2026-10-13; w, o and x for the three before).</summary>
    public const char ModeKey = 'm';

    /// <summary>Memory mode's button (later on 2026-10-13, the user's call, <c>/queue</c>'s shape): the mode in force, <c>mode: read-write</c>, never lit; a press steps to the next. Pinned.</summary>
    public static string ModeButton(MemoryAccess mode) => "mode: " + MemoryMode.Name(mode);

    /// <summary>The Memory mode after <paramref name="mode"/> in <see cref="MemoryMode.Names"/>' order (read-write, read-only, off), the last wrapping to the first. Pure.</summary>
    public static MemoryAccess NextMode(MemoryAccess mode)
    {
        int at = Array.IndexOf(MemoryMode.Names, MemoryMode.Name(mode));
        MemoryMode.TryParse(MemoryMode.Names[(at + 1) % MemoryMode.Names.Length], out var next);
        return next;
    }

    /// <summary>The mode button's index among <see cref="Buttons(MemoryAccess, bool)"/>, and the add button's after it.</summary>
    private const int ModeIndex = 0;
    private const int AddIndex = 1;

    /// <summary>The title row's buttons with <paramref name="mode"/> in force: <see cref="ModeButton"/>, then <see cref="AddButton"/> when <paramref name="add"/> (2026-10-13: an input row to type on).</summary>
    public static IReadOnlyList<MenuButton> Buttons(MemoryAccess mode, bool add = false) =>
        [new MenuButton(ModeButton(mode), ModeKey), .. add ? new[] { new MenuButton(AddButton, AddKey) } : []];

    /// <summary>A hint without the add key (2026-10-13): no input row to type a new memory on. Pure.</summary>
    public static string WithoutAdd(string keys) => keys.Replace("a = add · ", "", StringComparison.Ordinal);

    /// <summary>How an entry with no saved date shows; the width of a <c>yyyy-MM-dd</c> date.</summary>
    public const string NoDate = "----------";

    private readonly IAnsiConsole _console;
    private readonly MemoryStore _store;
    private readonly INoticeSink _transcript;
    private readonly MenuPane _pane;
    private readonly Func<MemoryAccess>? _memoryMode;
    private readonly Func<MemoryAccess, bool>? _setMemoryMode;
    private readonly Func<InputLine?>? _input;

    /// <param name="transcript">Where the lines outside the pane go: the transcript, or the screen's deferring sink when the list may open while a reply runs.</param>
    /// <param name="pane">The menu host in the bottom pane; disabled (no pane), the list is a Spectre prompt.</param>
    /// <param name="memoryMode">The Memory mode in force (2026-10-03 as Memory's switch): with <paramref name="setMemoryMode"/>, the buttons on the pane's title row; null, none.</param>
    /// <param name="setMemoryMode">Sets Memory mode, saving and saying so (<see cref="SettingsMenu.SetMemoryMode"/>).</param>
    /// <param name="input">The input row a memory is edited or added on (2026-10-13); null, or answering null, leaves edit and add out.</param>
    public MemoryMenu(IAnsiConsole console, MemoryStore store, INoticeSink transcript, MenuPane pane, Func<MemoryAccess>? memoryMode = null, Func<MemoryAccess, bool>? setMemoryMode = null, Func<InputLine?>? input = null)
    {
        _input = input;
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _transcript = transcript ?? throw new ArgumentNullException(nameof(transcript));
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
        _memoryMode = memoryMode;
        _setMemoryMode = setMemoryMode;
    }

    /// <summary>Whether the pane carries Memory's switch: wired, and a pane to put it on (the Spectre prompt has no buttons).</summary>
    private bool HasSwitch => _memoryMode is not null && _setMemoryMode is not null && _pane.Enabled;

    /// <summary>Where a notice goes: the pane's status line while the list is open there, else the transcript.</summary>
    private INoticeSink Sink => _pane.IsOpen ? _pane : _transcript;

    // ── Pinned statics ──────────────────────────────────────────────────────

    public static string RemovedNotice(string text) => $"({NoticeGlyphs.Memory}removed: {text})";

    /// <summary>The question before a row's removal (2026-10-07): <c>💾 Forget "Their cat is named Willow."?</c> Pinned.</summary>
    public static string RemovePrompt(string text) => $"{NoticeGlyphs.Memory}Forget \"{text}\"?";

    public static string RemoveFailedError(string detail) => $"Could not remove the memory: {detail}";

    /// <summary>The saved date as <c>yyyy-MM-dd</c> (invariant), or <see cref="NoDate"/> for an entry that has none.</summary>
    public static string DateLabel(MemoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.SavedAt == default ? NoDate : entry.SavedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>One menu row as markup: the date dimmed, two spaces, the text escaped; a disabled memory's whole line greyed out (2026-10-13, the user's ask).</summary>
    public static string RowMarkup(MemoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Disabled ? Theme.DimMarkup(DateLabel(entry) + "  " + entry.Text) : Theme.DimMarkup(DateLabel(entry)) + "  " + Markup.Escape(entry.Text);
    }

    /// <summary>
    /// The footer under the list for the cursor on <paramref name="row"/> of <paramref name="entries"/> (2026-10-07): that memory's whole
    /// text, a disabled one's with <see cref="DisabledFooter"/> on its last line (2026-10-13); null past the end.
    /// </summary>
    public static MenuFooter? Footer(IReadOnlyList<MemoryEntry> entries, int row)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return row >= 0 && row < entries.Count ? new MenuFooter(entries[row].Text, entries[row].Disabled ? DisabledFooter : null) : null;
    }

    /// <summary>The plain numbered list for a console without menus: <c>1. 2026-09-11  text</c>.</summary>
    public static IReadOnlyList<string> ListLines(IReadOnlyList<MemoryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var lines = new string[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            lines[i] = $"{(i + 1).ToString(CultureInfo.InvariantCulture)}. {DateLabel(entries[i])}  {entries[i].Text}";
        }

        return lines;
    }

    // ── Screen ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether the pane has something to offer with nothing remembered: Memory's switch on the title row, or (2026-10-13, the code review)
    /// + add where the pane has its input row — so an empty store opens it, and forgetting the last memory leaves it open.
    /// </summary>
    private bool OpensEmpty => HasSwitch || (_pane.Enabled && Line is not null);

    public async Task ShowAsync(CancellationToken cancellationToken)
    {
        var entries = _store.EntriesSnapshot();
        if (entries.Count == 0 && !(CanShowMenus() && OpensEmpty))
        {
            _transcript.Notice(EmptyNotice);
            return;
        }

        if (!CanShowMenus())
        {
            foreach (var line in ListLines(entries))
            {
                _transcript.Notice(line);
            }

            return;
        }

        // What ends the visit is said after the pane has closed, so it lands in the transcript
        // rather than on a status line the close forgets.
        string? closingNotice = null;
        string? closingError = null;
        int cursor = 0;
        try
        {
            while (true)
            {
                MenuPick? pick;
                var shown = entries;
                MenuPage list;
                if (HasSwitch)
                {
                    // The buttons on the title row (2026-10-03): the list, or one dim row with nothing remembered.
                    var mode = _memoryMode!();
                    bool add = Line is not null;
                    string hint = entries.Count > 0 ? SwitchKeys : EmptySwitchKeys;
                    list = entries.Count > 0
                        ? new MenuPage(Title, entries.Select(RowMarkup).ToList(), add ? hint : WithoutAdd(hint)) { Footer = (_, row) => Footer(shown, row) }
                        : new MenuPage(Title, [Theme.DimMarkup(EmptyNotice)], add ? hint : WithoutAdd(hint));
                    list = list with { Buttons = Buttons(mode, add), Removes = entries.Count > 0 };
                    pick = await _pane.PickAsync(list, cursor, cancellationToken).ConfigureAwait(false);
                    if (pick is { Button: ModeIndex } pressed)
                    {
                        cursor = pressed.Row;
                        _setMemoryMode!(NextMode(mode));
                        continue;
                    }
                }
                else if (_pane.Enabled)
                {
                    // The add button alone on the title row (2026-10-13) where Memory's switch is not wired.
                    bool add = Line is not null;
                    list = entries.Count > 0
                        ? new MenuPage(Title, entries.Select(RowMarkup).ToList(), add ? Keys : PlainKeys) { Footer = (_, row) => Footer(shown, row) }
                        : new MenuPage(Title, [Theme.DimMarkup(EmptyNotice)], add ? Keys : PlainKeys);
                    list = list with { Buttons = add ? [new MenuButton(AddButton, AddKey)] : null, Removes = entries.Count > 0 };
                    pick = await _pane.PickAsync(list, cursor, cancellationToken).ConfigureAwait(false);
                    if (pick is { Button: >= 0 } adding)
                    {
                        pick = adding with { Button = AddIndex };
                    }
                }
                else
                {
                    list = new MenuPage(Title, entries.Select(RowMarkup).ToList(), PlainKeys) { Footer = (_, row) => Footer(shown, row) };
                    pick = await PickAsync(list, cursor, cancellationToken).ConfigureAwait(false) is { } picked ? new MenuPick(0, picked) : null;
                }

                if (pick is null)
                {
                    return;
                }

                if (pick is { Button: var button } && button == AddIndex)
                {
                    // A new memory typed on the input row (2026-10-13, the user's ask): /remember's outcomes, the cursor on it.
                    cursor = await AddAsync(list, pick.Value.Row, cancellationToken).ConfigureAwait(false) ?? pick.Value.Row;
                    entries = _store.EntriesSnapshot();
                    continue;
                }

                if (entries.Count == 0)
                {
                    continue;   // Enter on the empty row: nothing to choose
                }

                int row = pick.Value.Row;
                cursor = row;
                // The memory as the store holds it now, not as the list was drawn (2026-10-19, the code review): the Web UI may have edited,
                // turned off or forgotten it meanwhile, and a toggle read off the drawn row flipped it the wrong way and said it was gone.
                var entry = Find(_store.EntriesSnapshot(), entries[row].Text);
                string text = entry?.Text ?? "";
                try
                {
                    // Delete is forget, asked as ever; Enter offers the choice first. A memory gone meanwhile is said so, the list read again.
                    switch (entry is null ? null : pick.Value.Remove ? RowAction.Forget : await ChooseAsync(entry, cancellationToken).ConfigureAwait(false))
                    {
                        case null when entry is null:
                            Sink.Error(MissingError);
                            break;

                        case RowAction.Edit:
                            await EditAsync(list, row, text, cancellationToken).ConfigureAwait(false);
                            break;

                        case RowAction.Toggle:
                            // The way the choice offered, which a change from the page while it was up may have made already: said as done.
                            bool off = !entry!.Disabled;
                            if (_store.SetDisabled(text, off) || Find(_store.EntriesSnapshot(), text)?.Disabled == off)
                            {
                                Sink.Notice(off ? DisabledNotice(text) : EnabledNotice(text));
                            }
                            else
                            {
                                Sink.Error(MissingError);
                            }

                            break;

                        case RowAction.Forget:
                            if (!await ConfirmAsync(RemovePrompt(text), cancellationToken).ConfigureAwait(false))
                            {
                                Sink.Notice(ChatScreen.KeptNotice);
                                break;
                            }

                            try
                            {
                                if (_store.Remove(text))
                                {
                                    Sink.Notice(RemovedNotice(text));
                                }
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                            {
                                closingError = RemoveFailedError(ex.Message);
                                return;
                            }

                            break;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    closingError = ChangeFailedError(ex.Message);
                    return;
                }

                entries = _store.EntriesSnapshot();
                if (entries.Count == 0 && !OpensEmpty)
                {
                    closingNotice = EmptyNotice;
                    return;
                }
            }
        }
        finally
        {
            _pane.Close();
            if (closingNotice is not null)
            {
                _transcript.Notice(closingNotice);
            }

            if (closingError is not null)
            {
                _transcript.Error(closingError);
            }
        }
    }

    /// <summary>The entry holding <paramref name="text"/>, matched as the store matches (ignoring case); null when none does.</summary>
    private static MemoryEntry? Find(IReadOnlyList<MemoryEntry> entries, string text) =>
        entries.FirstOrDefault(e => string.Equals(e.Text, text, StringComparison.OrdinalIgnoreCase));

    /// <summary>The input row, when there is one to type on (2026-10-13): edit and add need it.</summary>
    private InputLine? Line => _input?.Invoke();

    /// <summary>
    /// The choice a row opens (2026-10-13, the user's ask): <see cref="ActionRows"/> under <see cref="ActionTitle"/>, the cursor on edit
    /// (on disable with no input row, where edit is left out); null on ESC.
    /// </summary>
    private async Task<RowAction?> ChooseAsync(MemoryEntry entry, CancellationToken cancellationToken)
    {
        var rows = ActionRows(entry.Disabled);
        var actions = new List<RowAction> { RowAction.Edit, RowAction.Toggle, RowAction.Forget };
        if (Line is null)
        {
            rows = rows.Skip(1).ToList();
            actions.RemoveAt(0);
        }

        var page = new MenuPage(ActionTitle(entry.Text), rows, ActionKeys);
        return await PickAsync(page, 0, cancellationToken).ConfigureAwait(false) is { } picked && picked >= 0 && picked < actions.Count ? actions[picked] : null;
    }

    /// <summary>
    /// A memory's text edited on the input row under the list, pre-filled (2026-10-13): <see cref="MemoryStore.Edit"/>'s outcome said on the
    /// status line; ESC keeps it.
    /// </summary>
    private async Task EditAsync(MenuPage list, int row, string text, CancellationToken cancellationToken)
    {
        if (await ReadTextAsync(list, row, text, cancellationToken).ConfigureAwait(false) is not { } typed)
        {
            Sink.Notice(ChatScreen.KeptNotice);
            return;
        }

        var result = _store.Edit(text, typed);
        switch (result.Outcome)
        {
            case MemoryEditOutcome.Edited:
                Sink.Notice(EditedNotice(result.Text));
                break;
            case MemoryEditOutcome.Duplicate:
                Sink.Error(DuplicateError(result.Text));
                break;
            case MemoryEditOutcome.Missing:
                Sink.Error(MissingError);
                break;
            default:
                Sink.Notice(ChatScreen.KeptNotice);   // unchanged or empty
                break;
        }
    }

    /// <summary>
    /// A new memory typed on the input row under the list (2026-10-13, the user's ask): <c>/remember</c>'s outcomes said on the status line;
    /// the row it landed on (the list's last), or null when nothing was added.
    /// </summary>
    private async Task<int?> AddAsync(MenuPage list, int row, CancellationToken cancellationToken)
    {
        if (Line is null || await ReadTextAsync(list, row, "", cancellationToken).ConfigureAwait(false) is not { } typed)
        {
            return null;
        }

        var result = _store.Add(typed, enableDisabled: true);   // the user's own add enables a disabled one again (2026-10-13)
        switch (result.Outcome)
        {
            case MemoryAddOutcome.Added:
                Sink.Notice(ChatScreen.RememberedNotice(result.Text));
                return _store.EntriesSnapshot().Count - 1;
            case MemoryAddOutcome.Enabled:
                Sink.Notice(ChatScreen.RememberEnabledNotice(result.Text));
                return _store.EntriesSnapshot().ToList().FindIndex(e => string.Equals(e.Text, result.Text, StringComparison.Ordinal));
            case MemoryAddOutcome.Duplicate:
                Sink.Notice(ChatScreen.AlreadyRememberedNotice(result.Text));
                return null;
            case MemoryAddOutcome.Full:
                Sink.Error(ChatScreen.MemoryFullError);
                return null;
            case MemoryAddOutcome.Failed:
                Sink.Error(ChatScreen.MemoryFailedError);
                return null;
            default:
                return null;   // nothing typed
        }
    }

    /// <summary>
    /// A line typed on the input row (2026-10-13): on the pane under <paramref name="list"/> with <paramref name="row"/> marked and
    /// <see cref="EditKeys"/> in the hint, else the keys as a notice and the ordinary input row; the text, or null for ESC or nothing typed.
    /// </summary>
    private async Task<string?> ReadTextAsync(MenuPage list, int row, string initial, CancellationToken cancellationToken)
    {
        if (Line is not { } line)
        {
            return null;
        }

        InputResult result;
        if (_pane.Enabled)
        {
            result = await _pane.EditAsync(list with { Hint = EditKeys, Buttons = null }, row, line, initial, allowEmpty: false, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _transcript.Notice(SettingsMenu.PromptTitle(Title, EditKeys));
            result = await line.ReadAsync(initial, remember: false, allowEmpty: false, cancellationToken: cancellationToken, escapeCancels: true).ConfigureAwait(false);
        }

        return result is InputResult.Submitted { Text: var text } && !string.IsNullOrWhiteSpace(text) ? text : null;
    }

    /// <summary>
    /// One showing of the list: in the pane when the screen has one, else a Spectre prompt titled
    /// <see cref="SettingsMenu.PromptTitle"/> at the flow end. The picked row's index, or null for ESC.
    /// </summary>
    private async Task<int?> PickAsync(MenuPage page, int cursor, CancellationToken cancellationToken)
    {
        if (_pane.Enabled)
        {
            return (await _pane.PickAsync(page, cursor, cancellationToken).ConfigureAwait(false))?.Row;
        }

        var rows = page.Rows;
        var prompt = Theme.Selection(new SelectionPrompt<PromptResult<int>>()
            .Title(Theme.AccentMarkup(SettingsMenu.PromptTitle(page.Title, page.Hint)))
            .AddChoices(Enumerable.Range(0, rows.Count).Select(PromptResult<int>.From))
            .AddCancelResult(() => PromptResult<int>.Canceled)
            .DefaultValue(PromptResult<int>.From(Math.Clamp(cursor, 0, rows.Count - 1)))
            .UseConverter(r => r.IsCanceled ? "" : rows[r.Value]));

        var picked = await ScreenPane.ModalAsync(_console, () => prompt.ShowAsync(_console, cancellationToken)).ConfigureAwait(false);
        return picked.IsCanceled ? null : picked.Value;
    }

    /// <summary>The yes/no before a removal (2026-10-07): <see cref="SettingsMenu.ConfirmRows"/> with the cursor on No, through <see cref="PickAsync"/> so a console without the pane asks too; true for Yes alone.</summary>
    private async Task<bool> ConfirmAsync(string question, CancellationToken cancellationToken)
    {
        var page = new MenuPage(question, SettingsMenu.ConfirmRows, SettingsMenu.ConfirmKeys) { Hotkeys = SettingsMenu.ConfirmHotkeys };
        return await PickAsync(page, 0, cancellationToken).ConfigureAwait(false) == 1;
    }

    private bool CanShowMenus() =>
        _console.Profile.Capabilities.Interactive && _console.Profile.Capabilities.Ansi;
}
