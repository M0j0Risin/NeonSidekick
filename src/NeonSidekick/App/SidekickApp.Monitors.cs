using NeonSidekick.Monitors;

namespace NeonSidekick.App;

// ── Headless /monitor (2026-10-23, the monitors plan) ─────────────────────

public sealed partial class SidekickApp
{
    /// <summary>
    /// Headless's <c>/monitor</c>: <c>list</c> and every change (the user's own line), run over the bare system through <see cref="MonitorActions"/>
    /// (no board, no clock) and unasked — headless has no yes/no of its own, and its user is on a pipe or SSH, not looking at that screen. Bare
    /// <c>/monitor</c> and <c>/monitor &lt;n&gt;</c> answer <see cref="MonitorText.NeedsScreen"/>; no monitor system, <see cref="MonitorText.NoneHere"/>.
    /// <c>&lt;n&gt; pc …</c> (later on 2026-10-23) saves this PC's input through <paramref name="update"/>, the settings' own <c>Update</c>.
    /// </summary>
    internal static async Task<(IReadOnlyList<string> Lines, bool Failed)> MonitorLinesAsync(MonitorActions? actions, Settings.AppSettingsData effective, string args,
        Action<Action<Settings.AppSettingsData>> update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (actions is null)
        {
            return ([MonitorText.NoneHere], true);
        }

        if (MonitorCommand.Parse(args) is not { } line)
        {
            return ([MonitorText.Usage], true);
        }

        if (line.Verb is MonitorVerb.Pane or MonitorVerb.Show)
        {
            return ([MonitorText.NeedsScreen], true);
        }

        try
        {
            if (line.Verb == MonitorVerb.List)
            {
                var all = await actions.ReadAllAsync(cancellationToken).ConfigureAwait(false);
                return (MonitorText.ListLines(all, MonitorsMenu.WordsFor(effective, all.Topology)), false);
            }

            var state = await actions.ReadAllAsync(cancellationToken).ConfigureAwait(false);
            var snapshot = state.Topology;
            var words = MonitorsMenu.WordsFor(effective, snapshot);
            MonitorInfo? monitor = null;
            if (line.Monitor is { } typed && (monitor = MonitorCommand.Find(snapshot, typed, out string? error)) is null)
            {
                return ([error!], true);
            }

            if (line.Verb == MonitorVerb.Pc)
            {
                var pc = await MonitorPc.RunAsync(actions, state, monitor!, words, line.Input, cancellationToken).ConfigureAwait(false);
                if (pc.Save is { } save)
                {
                    update(d => d.MonitorPcInputs = save(d.MonitorPcInputs));
                }

                return (pc.Lines, pc.Failed);
            }

            MonitorChange change;
            if (line.Verb == MonitorVerb.Input)
            {
                var inputs = state.InputsOf(monitor!);
                var rows = words.Rows(monitor!, inputs.Offered, inputs.Current, null, all: true);
                var shown = rows.Where(r => !r.Hidden).Select(r => r.Words).ToList();
                if (line.Input is not { } input)
                {
                    return ([MonitorText.InputUsage(MonitorCommand.Spelled(monitor!), shown)], true);
                }

                if (MonitorWords.Resolve(rows, input) is not { } code)
                {
                    return ([MonitorText.UnknownInput(monitor!.Label, input, shown)], true);
                }

                change = state.SwitchTo(monitor!, code, words);
            }
            else
            {
                change = MonitorChange.Arrange(line.Verb switch
                {
                    MonitorVerb.Only => MonitorLayout.Only,
                    MonitorVerb.Primary => MonitorLayout.Primary,
                    MonitorVerb.Duplicate => MonitorLayout.Duplicate,
                    _ => MonitorLayout.Extend,
                }, monitor);
            }

            var outcome = await actions.ApplyAsync(change, words, cancellationToken).ConfigureAwait(false);
            return ([outcome.Text], outcome.Failed);
        }
        catch (MonitorException e)
        {
            return ([e.Message], true);
        }
    }
}
