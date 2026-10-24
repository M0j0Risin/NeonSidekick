using System.Diagnostics;
using System.Globalization;
using NeonSidekick.Monitors;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// <c>--monitor-check</c> (2026-10-23, the monitors plan, <see cref="CameraCheck"/>'s shape): the CCD and DDC/CI layers' proof on the published
/// binary against this PC's monitors — the system there (<see cref="MonitorText.NoneHere"/> fails where there is none, as the admin checks do),
/// the list, then per monitor on the desktop its capabilities (the raw string shown here, and only here: it is hardware text) and its current
/// input, with the time each read took. Read-only: no input switched, no layout changed. A monitor without DDC/CI is a passing "no DDC/CI"
/// line. Exit 0 only when every line passes. Not part of the build gate: it needs monitors.
/// </summary>
internal static class MonitorCheck
{
    public const string IntroLine = "Monitor check: the monitors read, nothing changed.";

    public static async Task<int> RunAsync(IAnsiConsole console, IMonitorSystem? system, TimeProvider time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(console);
        console.MarkupLine(Markup.Escape(IntroLine));
        var checks = new List<SmokeCheck>();
        if (system is null)
        {
            checks.Add(new SmokeCheck("monitor:system", false, MonitorText.NoneHere));
            return Report(console, checks);
        }

        checks.Add(new SmokeCheck("monitor:system", true, system.GetType().Name));
        var calls = new MonitorCalls(system, time);
        MonitorSnapshot snapshot;
        try
        {
            snapshot = await calls.RunAsync("query", s => s.Query(), retry: false, cancellationToken).ConfigureAwait(false);
        }
        catch (MonitorException e)
        {
            checks.Add(new SmokeCheck("monitor:list", false, e.Message));
            return Report(console, checks);
        }

        checks.Add(new SmokeCheck("monitor:list", snapshot.Monitors.Count > 0,
            snapshot.Monitors.Count == 0 ? MonitorText.NoMonitors
                : MonitorText.LayoutWord(snapshot.Layout) + " · " + string.Join(" · ", snapshot.Monitors.Select(m => m.Label + " (" + m.Connector + (m.Active ? m.Mode is { } mode ? ", " + MonitorText.Mode(mode) : "" : ", off the desktop") + (m.Primary ? ", primary" : "") + ")"))));
        foreach (var m in snapshot.Monitors.Where(m => m.Active))
        {
            if (m.Shared)
            {
                checks.Add(new SmokeCheck("monitor:caps " + m.Number.ToString(CultureInfo.InvariantCulture), true, MonitorText.SharedSource));
                continue;
            }

            var clock = Stopwatch.StartNew();
            try
            {
                byte input = await calls.RunAsync("read", s => s.CurrentInput(m.Target), retry: true, cancellationToken).ConfigureAwait(false);
                long read = clock.ElapsedMilliseconds;
                clock.Restart();
                try
                {
                    var caps = await calls.RunAsync("caps", s => s.Capabilities(m.Target), retry: true, cancellationToken).ConfigureAwait(false);
                    checks.Add(new SmokeCheck("monitor:caps " + m.Number.ToString(CultureInfo.InvariantCulture), true,
                        $"{m.Name}: {clock.ElapsedMilliseconds} ms, inputs {string.Join(", ", caps.Inputs.Select(c => (Mccs.StandardName(c) ?? "vendor") + " " + Mccs.Hex(c)))}{(caps.Reported ? "" : " (" + MonitorText.NotReported + ")")}; {caps.Raw}"));
                }
                catch (MonitorException e)
                {
                    checks.Add(new SmokeCheck("monitor:caps " + m.Number.ToString(CultureInfo.InvariantCulture), true, $"{m.Name}: no capabilities ({e.Message}); the common four would be offered"));
                }

                // The cable's view alone (later on 2026-10-23): the check reads no settings, so no pick.
                string guess = MonitorShowing.Judge(m, input, null, null) is { Kind: MonitorShowingKind.Elsewhere, Basis: MonitorShowingBasis.Cable }
                    ? " · " + MonitorText.CheckElsewhere(m.Connector) : "";
                checks.Add(new SmokeCheck("monitor:input " + m.Number.ToString(CultureInfo.InvariantCulture), true, $"{m.Name}: {Mccs.StandardName(input) ?? "vendor"} {Mccs.Hex(input)} in {read} ms{guess}"));
            }
            catch (MonitorException e) when (e.Message is MonitorText.NoDdc or MonitorText.NoInputSwitch)
            {
                checks.Add(new SmokeCheck("monitor:input " + m.Number.ToString(CultureInfo.InvariantCulture), true, $"{m.Name}: no DDC/CI — {e.Message}"));
            }
            catch (MonitorException e)
            {
                checks.Add(new SmokeCheck("monitor:input " + m.Number.ToString(CultureInfo.InvariantCulture), false, $"{m.Name}: {e.Message}"));
            }
        }

        return Report(console, checks);
    }

    private static int Report(IAnsiConsole console, IReadOnlyList<SmokeCheck> checks)
    {
        int failed = 0;
        foreach (var check in checks)
        {
            failed += check.Passed ? 0 : 1;
            string verdict = check.Passed ? Theme.ColorMarkup(Theme.Good, "PASS") : Theme.ColorMarkup(Theme.Bad, "FAIL");
            console.MarkupLine($"  {verdict}  {Markup.Escape(check.Name)}  {Theme.DimMarkup(check.Detail)}");
        }

        console.WriteLine();
        console.MarkupLine(failed == 0
            ? Theme.ColorMarkup(Theme.Good, $"MONITOR CHECK PASS  {checks.Count.ToString(CultureInfo.InvariantCulture)} checks")
            : Theme.ColorMarkup(Theme.Bad, $"MONITOR CHECK FAIL  {failed.ToString(CultureInfo.InvariantCulture)} of {checks.Count.ToString(CultureInfo.InvariantCulture)} checks failed"));
        return failed == 0 ? 0 : 1;
    }
}
