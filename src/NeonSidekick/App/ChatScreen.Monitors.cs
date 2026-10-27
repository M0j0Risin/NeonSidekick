using Microsoft.Extensions.AI;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Monitors;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.App;

// ── The monitors (2026-10-23, the monitors plan) ──────────────────────────

internal sealed partial class ChatScreen
{
    /// <summary>This PC's monitors (the CCD API and dxva2 in the app on Windows, a fake in tests); null elsewhere: no 🖥️, no <c>/monitor</c>, no tools.</summary>
    private readonly MonitorBoard? _monitors;

    /// <summary><c>monitor_list</c>, <c>monitor_input</c> and <c>monitor_layout</c>, built once; empty without a monitor system.</summary>
    private readonly IReadOnlyList<AIFunction> _monitorTools;

    /// <summary>The last pane opened (the tests wait on its change through <see cref="MonitorsMenu.Running"/>).</summary>
    internal MonitorsMenu? LastMonitorsMenu { get; private set; }

    /// <summary>The monitor tools over <paramref name="board"/> (2026-10-23): empty without one.</summary>
    public static IReadOnlyList<AIFunction> MonitorTools(MonitorBoard? board, Func<AppSettingsData> effective, Func<string, CancellationToken, Task<bool?>>? confirm)
    {
        if (board is null)
        {
            return [];
        }

        var hooks = new MonitorToolHooks(board, effective, confirm);
        return [new MonitorListTool(hooks), new MonitorInputTool(hooks), new MonitorLayoutTool(hooks)];
    }

    /// <summary>
    /// Whether the monitor tools are offered (2026-10-23, <see cref="ScreenOffered(AppSettingsData, bool, bool, bool)"/>'s shape): the setting
    /// <c>Monitor tools</c> on, a monitor system here, and the pane to ask on (never headless). Pure.
    /// </summary>
    public static bool MonitorOffered(AppSettingsData effective, bool available, bool pane) => effective.MonitorTools && available && pane;

    private bool MonitorOffered(AppSettingsData effective) => MonitorOffered(effective, _monitors is not null, _pane.Enabled);

    /// <summary>The Monitors group of <c>/tools</c>' Offered tab, <c>/sys</c> and the botchat checklist; null off Windows (the menu is left out there).</summary>
    private AdminGroupSpec? MonitorGroup(AppSettingsData effective)
    {
        if (!ToolsText.MenuHere(ToolsText.MonitorsMenuTitle))
        {
            return null;
        }

        string reason = !effective.MonitorTools ? ToolsText.SwitchOffReason(SettingsField.MonitorTools) : _monitors is null ? ToolsText.NoMonitorReason : ToolsText.NoPaneReason;
        return new AdminGroupSpec(ToolsText.MonitorsMenuTitle, _monitorTools, MonitorOffered(effective), reason, SettingsField.MonitorTools);
    }

    /// <summary>A model's monitor change's question, on the turn task: the pane and the page at once; null (never asked) with nobody to ask.</summary>
    private Task<bool?> ConfirmMonitorAsync(string question, CancellationToken turnToken) =>
        ConfirmBothAsync(question, "The monitor confirm pane failed: ", turnToken);

    /// <summary>The input names in force for <paramref name="snapshot"/>.</summary>
    private MonitorWords MonitorWordsFor(MonitorSnapshot snapshot) => MonitorsMenu.WordsFor(_effective(), snapshot);

    /// <summary>The monitors pane on monitor <paramref name="number"/>'s tab (the Layout tab for null); the list's lines without the pane.</summary>
    private async Task ShowMonitorsAsync(int? number, INoticeSink sink, CancellationToken cancellationToken)
    {
        if (_monitors is not { } board)
        {
            sink.Error(MonitorText.NoneHere);
            return;
        }

        if (!_pane.Enabled)
        {
            await PrintMonitorListAsync(sink, cancellationToken).ConfigureAwait(false);
            return;
        }

        var menu = new MonitorsMenu(board, _menuPane, _input, _settings, _effective, ConfirmAsync, _pane.Flash);
        LastMonitorsMenu = menu;
        try
        {
            await menu.ShowAsync(number, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _menuPane.Close();
        }
    }

    /// <summary><c>/monitor list</c>: the topology and every input read, then the lines.</summary>
    private async Task PrintMonitorListAsync(INoticeSink sink, CancellationToken cancellationToken)
    {
        if (_monitors is not { } board)
        {
            sink.Error(MonitorText.NoneHere);
            return;
        }

        await board.RefreshAsync(cancellationToken).ConfigureAwait(false);
        await board.ReadInputsAsync(cancellationToken).ConfigureAwait(false);
        foreach (string line in MonitorText.ListLines(board.State, MonitorWordsFor(board.State.Topology)))
        {
            sink.Notice(line);
        }
    }

    /// <summary>
    /// <c>/monitor …</c> (2026-10-23): bare the pane, <c>&lt;n&gt;</c> on that monitor's tab, <c>list</c> the lines, the changes with their outcome
    /// as a notice line (an error line when refused); the last screen's question and the away rows' asked first (the pane, else a typed
    /// <c>y</c>). <c>&lt;n&gt; pc …</c> (later that day) sets this PC's input, asking nothing (<see cref="MonitorPc"/>). The user's own hand: the
    /// tools' switch is not asked.
    /// </summary>
    private async Task HandleMonitorAsync(string args, INoticeSink sink, CancellationToken cancellationToken)
    {
        if (_monitors is not { } board)
        {
            sink.Error(MonitorText.NoneHere);
            return;
        }

        if (MonitorCommand.Parse(args) is not { } line)
        {
            sink.Error(MonitorText.Usage);
            return;
        }

        switch (line.Verb)
        {
            case MonitorVerb.Pane:
                await ShowMonitorsAsync(null, sink, cancellationToken).ConfigureAwait(false);
                return;
            case MonitorVerb.List:
                await PrintMonitorListAsync(sink, cancellationToken).ConfigureAwait(false);
                return;
        }

        var snapshot = await board.RefreshAsync(cancellationToken).ConfigureAwait(false);
        MonitorInfo? monitor = null;
        if (line.Monitor is { } typed)
        {
            monitor = MonitorCommand.Find(snapshot, typed, out string? error);
            if (monitor is null)
            {
                sink.Error(error!);
                return;
            }
        }

        if (line.Verb == MonitorVerb.Show)
        {
            await ShowMonitorsAsync(monitor!.Number, sink, cancellationToken).ConfigureAwait(false);
            return;
        }

        var words = MonitorWordsFor(snapshot);
        if (line.Verb == MonitorVerb.Pc)
        {
            // This PC's input (later on 2026-10-23): a setting, nothing on the monitor changes, so nothing is asked.
            var pc = await MonitorPc.RunAsync(board.Actions, board.State, monitor!, words, line.Input, cancellationToken).ConfigureAwait(false);
            if (pc.Save is { } save)
            {
                _settings.Update(d => d.MonitorPcInputs = save(d.MonitorPcInputs));
            }

            foreach (string said in pc.Lines)
            {
                if (pc.Failed)
                {
                    sink.Error(said);
                }
                else
                {
                    sink.Notice(said);
                }
            }

            return;
        }

        MonitorChange change;
        if (line.Verb == MonitorVerb.Input)
        {
            var state = board.State;
            var inputs = state.InputsOf(monitor!);
            var rows = words.Rows(monitor!, inputs.Offered, inputs.Current, state.AwayOf(monitor!), all: true);
            var shown = rows.Where(r => !r.Hidden).Select(r => r.Words).ToList();
            if (line.Input is not { } input)
            {
                sink.Error(MonitorText.InputUsage(MonitorCommand.Spelled(monitor!), shown));
                return;
            }

            if (MonitorWords.Resolve(rows, input) is not { } code)
            {
                sink.Error(MonitorText.UnknownInput(monitor!.Label, input, shown));
                return;
            }

            change = state.SwitchTo(monitor!, code, words);
        }
        else
        {
            var kind = line.Verb switch
            {
                MonitorVerb.Only => MonitorLayout.Only,
                MonitorVerb.Primary => MonitorLayout.Primary,
                MonitorVerb.Duplicate => MonitorLayout.Duplicate,
                _ => MonitorLayout.Extend,
            };
            change = MonitorChange.Arrange(kind, monitor);
        }

        if (await board.Actions.WarningAsync(change, words, cancellationToken).ConfigureAwait(false) is { } warning
            && !await ConfirmAsync(warning, cancellationToken).ConfigureAwait(false))
        {
            sink.Notice(MonitorText.Declined);
            return;
        }

        var outcome = await board.Actions.ApplyAsync(change, words, cancellationToken).ConfigureAwait(false);
        board.NoteAway();
        if (outcome.Failed)
        {
            sink.Error(outcome.Text);
        }
        else
        {
            sink.Notice(outcome.Text);
        }
    }

    /// <summary><c>/monitor</c>'s completion (2026-10-23, <see cref="MonitorCommand.Complete"/>): from the board's snapshot only.</summary>
    private IReadOnlyList<CompletionItem> MonitorChoices(string argText) =>
        _monitors is { } board ? MonitorCommand.Complete(argText, board.State, MonitorWordsFor(board.State.Topology)) : [];
}
