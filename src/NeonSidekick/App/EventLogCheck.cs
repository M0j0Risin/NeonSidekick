using System.Globalization;
using NeonSidekick.EventLog;
using NeonSidekick.Files;
using NeonSidekick.UI;
using Spectre.Console;

namespace NeonSidekick.App;

/// <summary>
/// <c>--eventlog-check &lt;computer&gt;</c> (2026-10-15, the event-log plan's phase 3): the event-log tools' proof on the published binary
/// against a computer of the loaded profile's <c>eventlog.json</c> (or <c>local</c>), nothing written: the computer found (whether ticked
/// or not, as the database checks read the whole catalog); its session and sign-in (the channel list, the session's first real call);
/// the newest System event read and formatted; and the Security log read or refused (5 passes: it says what the account may read). Exit 0
/// only when every line passes. Not part of the build gate: a remote one needs a computer.
/// </summary>
internal static class EventLogCheck
{
    public static string IntroLine(string name) => $"Event log check: computer '{name}' — nothing is written.";

    public static async Task<int> RunAsync(IAnsiConsole console, IEventLogSystem? system, EventLogCatalog loaded, string name, WorkingDirectory files, TimeProvider time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(name);
        console.MarkupLine(Markup.Escape(IntroLine(name)));
        var checks = new List<SmokeCheck>();
        if (system is null)
        {
            checks.Add(new SmokeCheck("eventlog:computer", false, EventLogText.NeedsWindows));
            return Report(console, checks);
        }

        var computer = loaded.Named(name);
        if (computer is null)
        {
            checks.Add(new SmokeCheck("eventlog:computer", false, EventLogText.UnknownComputer(name.Trim(), string.Join(", ", loaded.Computers.Select(c => c.Name)))));
            return Report(console, checks);
        }

        checks.Add(new SmokeCheck("eventlog:computer", true, $"{computer.Where} as {computer.Account}, {computer.Config.DetailWord}"));
        var access = new EventLogAccess(system, () => new EventLogCatalog([computer], []), () => [computer.Name], files, time);
        var (source, error) = access.Computer(computer.Name, forceRedacted: true);
        if (source is null)
        {
            checks.Add(new SmokeCheck("eventlog:session", false, error!));
            return Report(console, checks);
        }

        var channels = await access.RunAsync(source, (reader, token) => reader.Channels(source.Target, token).Count, cancellationToken).ConfigureAwait(false);
        checks.Add(channels.Error is { } refused
            ? new SmokeCheck("eventlog:session", false, refused)
            : new SmokeCheck("eventlog:session", true, $"{channels.Value.ToString(CultureInfo.InvariantCulture)} channels listed"));
        if (channels.Error is not null)
        {
            return Report(console, checks);
        }

        var (ok, text) = await EventLogAccess.TestAsync(system, computer, files, time, cancellationToken).ConfigureAwait(false);
        checks.Add(new SmokeCheck("eventlog:read", ok, text));
        var security = await access.RunAsync(source with { Log = "Security" }, (reader, token) =>
        {
            using var query = reader.Query(source.Target, "Security", "*", token);
            return query.Next()?.Count ?? 0;
        }, cancellationToken).ConfigureAwait(false);
        checks.Add(security.Error is { } denied
            ? new SmokeCheck("eventlog:security", denied.Contains("Event Log Readers", StringComparison.Ordinal), denied)
            : new SmokeCheck("eventlog:security", true, "the Security log is readable as this account"));
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
            ? Theme.ColorMarkup(Theme.Good, $"EVENT LOG CHECK PASS  {checks.Count.ToString(CultureInfo.InvariantCulture)} checks")
            : Theme.ColorMarkup(Theme.Bad, $"EVENT LOG CHECK FAIL  {failed.ToString(CultureInfo.InvariantCulture)} of {checks.Count.ToString(CultureInfo.InvariantCulture)} checks failed"));
        return failed == 0 ? 0 : 1;
    }
}
