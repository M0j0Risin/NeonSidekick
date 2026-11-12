using NeonSidekick.Midi;
using NeonSidekick.Settings;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// The MIDI pane (2026-11-12, the MIDI plan): <c>/midi</c> and <c>/midi learn</c>. Two tabs over the menu pane — Bindings (each button
/// and its command, the skipped entries of a hand-edited file dim, then Learn a button…; Enter edits a command, <c>l</c> learns, <c>r</c>
/// relearns the cursor's button, <c>o</c> flips <i>MIDI input</i>, Del removes after asking) and Devices (each input, which are listening,
/// which another app holds, and the last message heard, live). The board is held for the pane's life (<see cref="MidiBoard.Hold"/>), so
/// a foot on the pedal shows what it sends and never runs a binding or closes the pane mid-edit. The learn: a page that waits for the
/// first press (Esc cancels), then a picker of every command with type to filter, then the line with its arguments on the input row,
/// checked as a command, then saved; <i>MIDI input</i> is turned on with a notice if it was off. The page is built again as the board
/// changes (<see cref="MenuPane.PickAsync"/>'s <c>stale</c>).
/// </summary>
internal sealed class MidiMenu
{
    private const int Learn = 0;
    private const int Relearn = 1;
    private const int Switch = 2;

    private readonly MidiBoard _board;
    private readonly MenuPane _pane;
    private readonly InputLine _line;
    private readonly AppSettings _settings;
    private readonly Func<AppSettingsData> _effective;
    private readonly Func<IReadOnlyList<CompletionItem>> _commands;
    private readonly Func<string, bool> _isCommand;
    private readonly Func<string, CancellationToken, Task<bool>> _confirm;

    public MidiMenu(MidiBoard board, MenuPane pane, InputLine line, AppSettings settings, Func<AppSettingsData> effective,
        Func<IReadOnlyList<CompletionItem>> commands, Func<string, bool> isCommand, Func<string, CancellationToken, Task<bool>> confirm)
    {
        _board = board ?? throw new ArgumentNullException(nameof(board));
        _pane = pane ?? throw new ArgumentNullException(nameof(pane));
        _line = line ?? throw new ArgumentNullException(nameof(line));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _isCommand = isCommand ?? throw new ArgumentNullException(nameof(isCommand));
        _confirm = confirm ?? throw new ArgumentNullException(nameof(confirm));
    }

    private MidiBindings File => new(_settings.ProfileDirectory);

    // One Bindings row: its markup, its footer, the binding's index (null for the skipped and the learn rows), whether it is the learn row.
    private sealed record Row(string Markup, string? Footer, int? Index = null, bool LearnRow = false);

    /// <summary>The pane until Esc; with <paramref name="learn"/> it starts on a learn (<c>/midi learn</c>), binding <paramref name="command"/> when given.</summary>
    public async Task ShowAsync(bool learn, string? command, CancellationToken cancellationToken)
    {
        using var hold = _board.Hold();
        if (learn)
        {
            await LearnAsync(null, command, cancellationToken).ConfigureAwait(false);
            if (command is not null)
            {
                return;
            }
        }

        int tabIndex = 0;
        int cursor = 0;
        while (true)
        {
            var set = _board.Bindings;
            var rows = BindingRows(set);
            var tabs = new List<MenuTab>
            {
                new(MidiText.BindingsTab, rows.Select(r => r.Markup).ToList()) { Caption = BindingsCaption(set), Hint = MidiText.BindingsKeys },
                new(MidiText.DevicesTab, DeviceRows()) { Caption = MidiText.HeldCaption, Hint = MidiText.DevicesKeys, Placeholder = true },
            };
            var page = MenuPage.Tabbed(MidiText.Title, tabs, Math.Clamp(tabIndex, 0, tabs.Count - 1), MidiText.BindingsKeys) with
            {
                Buttons =
                [
                    new MenuButton("learn", 'l') { Hidden = true },
                    new MenuButton("relearn", 'r') { Hidden = true },
                    new MenuButton("on/off", 'o') { Hidden = true },
                ],
                TabButtons = true,
                Removes = true,
                Footer = (tab, row) => tab == 0 && row >= 0 && row < rows.Count && rows[row].Footer is { } f ? new MenuFooter(f) : null,
            };

            if (await PickFreshAsync(page, cursor, cancellationToken).ConfigureAwait(false) is not { } pick)
            {
                return;
            }

            tabIndex = pick.Tab;
            cursor = Math.Max(0, pick.Row);
            if (pick.Refresh)
            {
                continue;
            }

            if (pick.Button == Switch)
            {
                bool on = !_effective().MidiInput;
                _settings.Update(d => d.MidiInput = on);
                _pane.Notice(on ? MidiText.TurnedOn : MidiText.TurnedOff);
                continue;
            }

            if (pick.Button == Learn)
            {
                await LearnAsync(null, null, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (pick.Tab != 0 || pick.Row < 0 || pick.Row >= rows.Count)
            {
                continue;
            }

            var row = rows[pick.Row];
            if (row.LearnRow)
            {
                await LearnAsync(null, null, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (row.Index is not { } index || index >= set.Bindings.Count)
            {
                continue;
            }

            var binding = set.Bindings[index];
            if (pick.Button == Relearn)
            {
                await LearnAsync(index, binding.Command, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (pick.Remove)
            {
                if (await _confirm(MidiText.RemoveQuestion(binding), cancellationToken).ConfigureAwait(false))
                {
                    Said(File.Remove(index), MidiText.Removed(binding));
                }

                continue;
            }

            if (pick.Button < 0)
            {
                // Enter: the command edited on the input row, prefilled.
                if (await ArgsAsync(page with { Title = MidiText.ArgsTitle(binding.Trigger) }, pick.Row, binding.Command, cancellationToken).ConfigureAwait(false) is { } edited
                    && !string.Equals(edited, binding.Command, StringComparison.Ordinal))
                {
                    Said(File.SetCommand(index, edited), MidiText.Bound(binding with { Command = edited }));
                }
            }
        }
    }

    /// <summary>The pane's rows for <c>/midi list</c> too: each binding numbered, the skipped entries after.</summary>
    public static IReadOnlyList<string> ListLines(MidiBindingSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        var lines = new List<string>();
        for (int i = 0; i < set.Bindings.Count; i++)
        {
            lines.Add(MidiText.ListLine(i + 1, set.Bindings[i]));
        }

        lines.AddRange(set.Skipped.Select(MidiText.SkippedLine));
        return lines;
    }

    private string? BindingsCaption(MidiBindingSet set) =>
        MidiText.FileProblem(set.State) ?? (!_effective().MidiInput && set.Bindings.Count > 0 ? MidiText.OffCaption : null);

    private static List<Row> BindingRows(MidiBindingSet set)
    {
        var rows = new List<Row>();
        for (int i = 0; i < set.Bindings.Count; i++)
        {
            var b = set.Bindings[i];
            rows.Add(new Row(Markup.Escape(MidiText.Describe(b.Trigger)) + Theme.DimMarkup("  → ") + Markup.Escape(b.Command), MidiText.BindingFooter, i));
        }

        foreach (string skipped in set.Skipped)
        {
            rows.Add(new Row(Theme.DimMarkup(MidiText.SkippedLine(skipped)), null));
        }

        rows.Add(new Row(Markup.Escape(MidiText.LearnRow), MidiText.LearnRowFooter, LearnRow: true));
        return rows;
    }

    private List<string> DeviceRows()
    {
        var rows = new List<string>();
        var inputs = _board.Inputs;
        if (inputs.Count == 0)
        {
            rows.Add(Theme.DimMarkup(MidiText.NoDevices));
        }

        foreach (var input in inputs)
        {
            string tag = input.Open ? MidiText.OpenTag : input.InUse ? MidiText.InUseTag : input.Problem ?? "";
            rows.Add(Markup.Escape(MidiText.Device(input.Name, input.Occurrence)) + (tag.Length > 0 ? Theme.DimMarkup("  " + tag) : ""));
        }

        rows.Add("");
        rows.Add(Markup.Escape(_board.LastSeen is { } seen ? MidiText.LastHeard(seen) : MidiText.NothingHeard));
        return rows;
    }

    /// <summary>
    /// The learn: a button heard, its command picked (or <paramref name="command"/> as given) and saved. For a relearn
    /// (<paramref name="relearn"/> the binding's index) only its button changes. False when nothing was saved.
    /// </summary>
    private async Task<bool> LearnAsync(int? relearn, string? command, CancellationToken cancellationToken)
    {
        if (await CaptureAsync(cancellationToken).ConfigureAwait(false) is not { } trigger)
        {
            _pane.Notice(MidiText.LearnCancelled);
            return false;
        }

        var set = _board.Bindings;
        var existing = set.Find(trigger);
        if (relearn is { } index)
        {
            if (existing is not null && index < set.Bindings.Count && !ReferenceEquals(existing, set.Bindings[index]))
            {
                if (!await _confirm(MidiText.Replace(existing), cancellationToken).ConfigureAwait(false))
                {
                    _pane.Notice(MidiText.Kept);
                    return false;
                }
            }

            return Said(File.SetTrigger(index, trigger), MidiText.Bound(new MidiBinding(trigger, command ?? "")));
        }

        if (existing is not null && !await _confirm(MidiText.Replace(existing), cancellationToken).ConfigureAwait(false))
        {
            _pane.Notice(MidiText.Kept);
            return false;
        }

        string? line = command;
        if (line is not null && !_isCommand(line))
        {
            _pane.Error(MidiText.NotCommand(line));
            line = null;
        }

        if (line is null)
        {
            if (await PickCommandAsync(trigger, cancellationToken).ConfigureAwait(false) is not { } word)
            {
                _pane.Notice(MidiText.Kept);
                return false;
            }

            var argsPage = new MenuPage(MidiText.ArgsTitle(trigger), [Markup.Escape(word)], MidiText.EditKeys);
            line = await ArgsAsync(argsPage, 0, word + " ", cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                _pane.Notice(MidiText.Kept);
                return false;
            }
        }

        bool saved = Said(File.Bind(trigger, line), MidiText.Bound(new MidiBinding(trigger, line)));
        if (saved && !_effective().MidiInput)
        {
            _settings.Update(d => d.MidiInput = true);
            _pane.Notice(MidiText.TurnedOnToo);
        }

        return saved;
    }

    /// <summary>The first press on any input, or null for Esc; the page says when there is no device or some are another app's.</summary>
    private async Task<MidiTrigger?> CaptureAsync(CancellationToken cancellationToken)
    {
        using var learnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var learning = _board.LearnAsync(learnCts.Token);
        try
        {
            while (!learning.IsCompleted)
            {
                var inputs = _board.Inputs;
                var inUse = inputs.Where(i => i.InUse).Select(i => MidiText.Device(i.Name, i.Occurrence)).ToList();
                var page = new MenuPage(MidiText.LearnTitle, [Markup.Escape(inputs.Count == 0 ? MidiText.LearnNoDevice : MidiText.LearnWaiting)], "Esc = cancel")
                {
                    Caption = inUse.Count > 0 ? MidiText.LearnInUse(inUse) : null,
                    Placeholder = true,
                };
                if (await PickFreshAsync(page, 0, cancellationToken, learning).ConfigureAwait(false) is null)
                {
                    await learnCts.CancelAsync().ConfigureAwait(false);
                    break;
                }
            }

            return await learning.ConfigureAwait(false);
        }
        finally
        {
            await learnCts.CancelAsync().ConfigureAwait(false);
        }
    }

    /// <summary>A command picked from the <c>/</c> list (type to filter), or null for Esc.</summary>
    private async Task<string?> PickCommandAsync(MidiTrigger trigger, CancellationToken cancellationToken)
    {
        string filter = "";
        int cursor = 0;
        while (true)
        {
            var all = _commands().Where(c => !string.Equals(c.Text, MidiCommand.Word, StringComparison.OrdinalIgnoreCase)).ToList();
            var shown = all.Where(c => MenuFilter.Matches(filter, c.Text, c.Note)).ToList();
            var rows = shown.Count == 0
                ? [MenuFilter.NoMatchRow(filter)]
                : shown.Select(c => Markup.Escape(c.Text) + new string(' ', MentionCompleter.NoteGap) + Theme.DimMarkup(c.Note)).ToList();
            var page = new MenuPage(MidiText.PickTitle(trigger), rows, MenuFilter.Hint(MidiText.PickerKeys, filter))
            {
                Filter = filter,
                Placeholder = shown.Count == 0,
                Caption = filter.Length > 0 ? MenuFilter.Caption(filter, shown.Count, all.Count) : null,
            };
            if (await _pane.PickAsync(page, cursor, cancellationToken).ConfigureAwait(false) is not { } pick)
            {
                return null;
            }

            if (pick.Filter is { } typed)
            {
                filter = typed;
                cursor = 0;
                continue;
            }

            if (pick.Row >= 0 && pick.Row < shown.Count)
            {
                return shown[pick.Row].Text;
            }
        }
    }

    /// <summary>The command line with its arguments typed on the input row, checked as a command; null for Esc or one that isn't.</summary>
    private async Task<string?> ArgsAsync(MenuPage page, int row, string initial, CancellationToken cancellationToken)
    {
        while (true)
        {
            var result = await _pane.EditAsync(page with { Hint = MidiText.EditKeys, Buttons = null }, row, _line, initial, allowEmpty: false, cancellationToken).ConfigureAwait(false);
            if (result is not InputResult.Submitted { Text: var text } || string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            string line = text.Trim();
            if (_isCommand(line))
            {
                return line;
            }

            _pane.Error(MidiText.NotCommand(line));
            initial = line;
        }
    }

    // A save's outcome on the status line, the board told; true when it saved.
    private bool Said(MidiSaveOutcome outcome, string saved)
    {
        switch (outcome)
        {
            case MidiSaveOutcome.Saved:
                _pane.Notice(saved);
                _board.Reload();
                return true;
            case MidiSaveOutcome.Newer:
                _pane.Error(MidiText.NewerFile);
                return false;
            default:
                _pane.Error(MidiText.SaveFailed);
                return false;
        }
    }

    /// <summary>A pick on <paramref name="page"/>, shown again (a <c>Refresh</c> pick) as the board changes or <paramref name="until"/> ends.</summary>
    private async Task<MenuPick?> PickFreshAsync(MenuPage page, int cursor, CancellationToken cancellationToken, Task? until = null)
    {
        using var stale = new CancellationTokenSource();
        long drawn = _board.Version;
        void OnChanged()
        {
            if (_board.Version != drawn || until?.IsCompleted == true)
            {
                try
                {
                    stale.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // The pick already ended.
                }
            }
        }

        _board.Changed += OnChanged;
        try
        {
            OnChanged();
            return await _pane.PickAsync(page, cursor, cancellationToken, stale: stale.Token).ConfigureAwait(false);
        }
        finally
        {
            _board.Changed -= OnChanged;
        }
    }
}
