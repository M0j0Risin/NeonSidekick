using NeonSidekick.Hotkeys;
using NeonSidekick.UI;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.App;

// ── /keycheck (2026-10-04) ────────────────────────────────────────────────

internal sealed partial class ChatScreen
{
    /// <summary>Asks the system whether another program holds a chord (<see cref="WindowsHotkeyProbe"/>, a Mac's <see cref="MacHotkeyProbe"/>, a fake in tests); null elsewhere.</summary>
    private readonly IHotkeyProbe? _hotkeyProbe;

    /// <summary>
    /// <c>/keycheck</c> (2026-10-04, the user's ask, after the NVIDIA overlay was found holding Ctrl+Alt+M and Ctrl+Alt+R): every
    /// chord of <c>/help</c>'s Keys tab (<see cref="KeyRows"/>, the push-to-talk key too while voice is on) asked of Windows — free,
    /// or held by another program — on the info pane, the held ones first; without the pane, the same as lines through
    /// <paramref name="sink"/>. On a Mac (2026-10-11) the same over <see cref="MacHotkeyProbe"/>, in a Mac's words; elsewhere, an
    /// error. The probe is synchronous and quick (a register and a release per chord; on a Mac a list read first).
    /// </summary>
    private async Task ShowKeyCheckAsync(INoticeSink sink, CancellationToken cancellationToken)
    {
        if (_hotkeyProbe is not { } probe)
        {
            sink.Error(KeyCheckText.Unsupported);
            return;
        }

        var report = RunKeyCheck(probe);
        if (_pane.Enabled)
        {
            await _info.ShowAsync(KeyCheckText.Label, [new InfoTab(KeyCheckText.TabTitle, () => KeyCheckTab(report))], 0, cancellationToken).ConfigureAwait(false);
            return;
        }

        foreach (string line in KeyCheckText.Lines(report))
        {
            sink.Notice(line);
        }
    }

    /// <summary>Every chord of the Keys tab as it stands asked of <paramref name="probe"/>: <c>/keycheck</c>'s and the Keys tab's toggle's (2026-10-12) one report.</summary>
    private KeyCheckReport RunKeyCheck(IHotkeyProbe probe)
    {
        var rows = KeyRows(_voice.Enabled, _voice.PushToTalk, _voice.WakeReady, _voice.WakePhrase);
        return KeyCheck.Run(rows.Select(r => r.Pair), _voice.Enabled ? _voice.PushToTalk : null, probe);
    }

    /// <summary>
    /// <c>/help</c>'s Keys tab's toggle (2026-10-12, the user's ask): <see cref="KeyCheckText.ToggleTitle"/> swaps the tab for
    /// <c>/keycheck</c>'s list, probed afresh each time it is switched on, and back; none where there is no probe.
    /// </summary>
    private InfoToggle? KeyCheckToggle() =>
        _hotkeyProbe is { } probe
            ? new InfoToggle(KeyCheckText.ToggleTitle, KeyCheckText.ToggleKey, KeyCheckText.ToggleWord, () => KeyCheckTab(RunKeyCheck(probe)))
            : null;

    /// <summary>The pane's one tab: the header, the caveat dim, a Mac's notes on the terminal, then chord · status · meaning, held in the warning colour. Text cells, never Markup.</summary>
    internal static IRenderable KeyCheckTab(KeyCheckReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var grid = new Grid()
            .AddColumn(new GridColumn().NoWrap().PadRight(SlashCommands.HelpColumnGap))
            .AddColumn(new GridColumn().NoWrap().PadRight(SlashCommands.HelpColumnGap))
            .AddColumn(new GridColumn().PadRight(0));
        foreach (var row in report.Rows)
        {
            var status = row.Result.Status switch
            {
                HotkeyStatus.Held => new Style(Theme.Warn),
                HotkeyStatus.Free => new Style(Theme.Good),
                _ => new Style(Theme.Dim),
            };
            grid.AddRow(new Text(row.Label, Theme.AccentSecondary), new Text(KeyCheckText.Status(row.Result), status), new Text(row.Meaning, Theme.Body));
        }

        var lines = new List<IRenderable>
        {
            new Text(KeyCheckText.HeaderFor(report), report.Held > 0 ? new Style(Theme.Warn) : Theme.Body),
            new Text(KeyCheckText.CaveatFor(report), new Style(Theme.Dim)),
        };
        lines.AddRange((report.Notes ?? []).Select(note => new Text(note, Theme.Body)));
        lines.Add(new Text(""));
        lines.Add(grid);
        return new Rows(lines);
    }
}
