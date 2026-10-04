using NeonSidekick.Hotkeys;
using NeonSidekick.UI;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.App;

// ── /keycheck (2026-10-04) ────────────────────────────────────────────────

internal sealed partial class ChatScreen
{
    /// <summary>Asks Windows whether another program holds a chord (<see cref="WindowsHotkeyProbe"/> in the app, a fake in tests); null off Windows.</summary>
    private readonly IHotkeyProbe? _hotkeyProbe;

    /// <summary>
    /// <c>/keycheck</c> (2026-10-04, the user's ask, after the NVIDIA overlay was found holding Ctrl+Alt+M and Ctrl+Alt+R): every
    /// chord of <c>/help</c>'s Keys tab (<see cref="KeyRows"/>, the push-to-talk key too while voice is on) asked of Windows — free,
    /// or held by another program — on the info pane, the held ones first; without the pane, the same as lines through
    /// <paramref name="sink"/>. Off Windows, an error. The probe is synchronous and quick (a register and a release per chord).
    /// </summary>
    private async Task ShowKeyCheckAsync(INoticeSink sink, CancellationToken cancellationToken)
    {
        if (_hotkeyProbe is not { } probe)
        {
            sink.Error(KeyCheckText.Unsupported);
            return;
        }

        var rows = KeyRows(_voice.Enabled, _voice.PushToTalk, _voice.WakeReady, _voice.WakePhrase);
        var report = KeyCheck.Run(rows, _voice.Enabled ? _voice.PushToTalk : null, probe);
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

    /// <summary>The pane's one tab: the header, the caveat dim, then chord · status · meaning, held in the warning colour. Text cells, never Markup.</summary>
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

        return new Rows(
            new Text(KeyCheckText.Header(report.Held, report.Rows.Count), report.Held > 0 ? new Style(Theme.Warn) : Theme.Body),
            new Text(KeyCheckText.Caveat, new Style(Theme.Dim)),
            new Text(""),
            grid);
    }
}
