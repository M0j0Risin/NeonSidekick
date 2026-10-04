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
/// <para>No confirmation per row: the row is visible and chosen deliberately, and the notice names
/// what went. <c>/memory forget</c> keeps its confirmation because it is everything at once
/// (2026-09-22: that wipe was <c>/forget</c>, its own command, until the word folded in here). A
/// console that cannot show menus gets the numbered list instead, and removes nothing.</para>
///
/// <para>On the pane the title row carries Memory's switch (2026-10-03, the user's ask: "actions at the top, similar to ...
/// Tools › Shell allowed commands' ask and yolo"): <see cref="OnButton"/> and <see cref="OffButton"/>, the lit one the setting
/// in force, a press of the other switching it (<see cref="SettingsMenu.SetMemory"/>, the Memory row's own save and notice) and
/// showing the list again. So the switch is always in reach, the pane opens with nothing remembered too, on one dim row, and
/// stays open when the last row goes.</para>
/// </summary>
internal sealed class MemoryMenu
{
    // The label and the key hints: the pane shows the label as its title and the keys in its hint
    // row; the prompt host joins them (SettingsMenu.PromptTitle). Pinned.
    public const string Title = ChatScreen.MemoryToolGlyph + " Memory";   // the glyph the toolbar wears for the pane too (2026-09-22)
    public const string Keys = "Enter = remove · ESC = back";
    public const string EmptyNotice = "(" + NoticeGlyphs.Memory + "nothing remembered)";   // the disk since 2026-09-22

    /// <summary>The pane's hints with Memory's switch on the title row (2026-10-03): the rows, then with nothing remembered. Pinned.</summary>
    public const string SwitchKeys = "Enter = remove · N = on · F = off · ESC = back";
    public const string EmptySwitchKeys = "N = on · F = off · ESC = back";

    /// <summary>Memory's switch on the pane's title row (2026-10-03, the user's ask): the ask/yolo pair's shape, the lit one in force. Pinned.</summary>
    public const string OnButton = "● on";
    public const char OnKey = 'n';
    public const string OffButton = "○ off";
    public const char OffKey = 'f';
    public const int OnIndex = 0;
    public const int OffIndex = 1;

    /// <summary>The title row's buttons with Memory <paramref name="on"/> or off: the one in force lit.</summary>
    public static IReadOnlyList<MenuButton> Buttons(bool on) =>
    [
        new(OnButton, OnKey, on),
        new(OffButton, OffKey, !on),
    ];

    /// <summary>How an entry with no saved date shows; the width of a <c>yyyy-MM-dd</c> date.</summary>
    public const string NoDate = "----------";

    private readonly IAnsiConsole _console;
    private readonly MemoryStore _store;
    private readonly INoticeSink _transcript;
    private readonly MenuPane _pane;
    private readonly Func<bool>? _memoryOn;
    private readonly Action<bool>? _setMemory;

    /// <param name="transcript">Where the lines outside the pane go: the transcript, or the screen's deferring sink when the list may open while a reply runs.</param>
    /// <param name="pane">The menu host in the bottom pane; disabled (no pane), the list is a Spectre prompt.</param>
    /// <param name="memoryOn">Whether Memory is on (2026-10-03): with <paramref name="setMemory"/>, the switch on the pane's title row; null, no switch.</param>
    /// <param name="setMemory">Switches Memory, saving and saying so (<see cref="SettingsMenu.SetMemory"/>).</param>
    public MemoryMenu(IAnsiConsole console, MemoryStore store, INoticeSink transcript, MenuPane pane, Func<bool>? memoryOn = null, Action<bool>? setMemory = null)
    {
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _transcript = transcript ?? throw new ArgumentNullException(nameof(transcript));
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
        _memoryOn = memoryOn;
        _setMemory = setMemory;
    }

    /// <summary>Whether the pane carries Memory's switch: wired, and a pane to put it on (the Spectre prompt has no buttons).</summary>
    private bool HasSwitch => _memoryOn is not null && _setMemory is not null && _pane.Enabled;

    /// <summary>Where a notice goes: the pane's status line while the list is open there, else the transcript.</summary>
    private INoticeSink Sink => _pane.IsOpen ? _pane : _transcript;

    // ── Pinned statics ──────────────────────────────────────────────────────

    public static string RemovedNotice(string text) => $"({NoticeGlyphs.Memory}removed: {text})";

    public static string RemoveFailedError(string detail) => $"Could not remove the memory: {detail}";

    /// <summary>The saved date as <c>yyyy-MM-dd</c> (invariant), or <see cref="NoDate"/> for an entry that has none.</summary>
    public static string DateLabel(MemoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.SavedAt == default ? NoDate : entry.SavedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>One menu row as markup: the date dimmed, two spaces, the text escaped.</summary>
    public static string RowMarkup(MemoryEntry entry) => Theme.DimMarkup(DateLabel(entry)) + "  " + Markup.Escape(entry.Text);

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

    public async Task ShowAsync(CancellationToken cancellationToken)
    {
        var entries = _store.EntriesSnapshot();
        if (entries.Count == 0 && !(HasSwitch && CanShowMenus()))
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
                if (HasSwitch)
                {
                    // The switch on the title row (2026-10-03): the list, or one dim row with nothing remembered.
                    bool on = _memoryOn!();
                    var page = entries.Count > 0
                        ? new MenuPage(Title, entries.Select(RowMarkup).ToList(), SwitchKeys)
                        : new MenuPage(Title, [Theme.DimMarkup(EmptyNotice)], EmptySwitchKeys);
                    pick = await _pane.PickAsync(page with { Buttons = Buttons(on) }, cursor, cancellationToken).ConfigureAwait(false);
                    if (pick is { Button: OnIndex or OffIndex } pressed)
                    {
                        cursor = pressed.Row;
                        bool wanted = pressed.Button == OnIndex;
                        if (wanted != on)
                        {
                            _setMemory!(wanted);
                        }

                        continue;
                    }

                    if (pick is not null && entries.Count == 0)
                    {
                        continue;   // Enter on the empty row: nothing to remove
                    }
                }
                else
                {
                    var page = new MenuPage(Title, entries.Select(RowMarkup).ToList(), Keys);
                    pick = await PickAsync(page, cursor, cancellationToken).ConfigureAwait(false) is { } picked ? new MenuPick(0, picked) : null;
                }

                if (pick is not { Row: var row })
                {
                    return;
                }

                cursor = row;
                string text = entries[row].Text;
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

                entries = _store.EntriesSnapshot();
                if (entries.Count == 0 && !HasSwitch)
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

    private bool CanShowMenus() =>
        _console.Profile.Capabilities.Interactive && _console.Profile.Capabilities.Ansi;
}
