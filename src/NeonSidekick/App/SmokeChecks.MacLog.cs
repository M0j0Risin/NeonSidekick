using System.Runtime.Versioning;
using NeonSidekick.EventLog;

namespace NeonSidekick.App;

public static partial class SmokeChecks
{
    /// <summary>
    /// <c>eventlog:oslog</c> (2026-10-18, the Mac event-log plan's phase 5): OSLogStore over the Objective-C runtime on the published binary —
    /// the store opens, a short read comes back, a read naming every predicate fragment is accepted and ended by its time limit, and fd 2
    /// is where it was after (the quiet scope put the terminal back). A system framework: nothing joins <see cref="RequiredNativeLibraries"/>.
    /// </summary>
    [SupportedOSPlatform("macos")]
    public static SmokeCheck ProbeMacOsLog()
    {
        var log = new MacUnifiedLog();
        var time = TimeProvider.System;
        var parts = new[] { MacLogProbes.Store(log), MacLogProbes.Read(log, time), MacLogProbes.Predicates(log, time), MacLogProbes.Stderr(log, time) };
        return new SmokeCheck("eventlog:oslog", parts.All(p => p.Passed), string.Join("; ", parts.Select(p => (p.Passed ? "" : "FAILED ") + p.Detail)));
    }

    /// <summary><c>eventlog:crashes</c>: the crash folders list (a refused one named, not failed) and the newest report is described; install.log's newest entry reads.</summary>
    [SupportedOSPlatform("macos")]
    public static SmokeCheck ProbeMacCrashes()
    {
        var places = MacLogPlaces.Default;
        var parts = MacLogProbes.Crashes(places, TimeProvider.System).Append(MacLogProbes.Install(places, TimeProvider.System)).ToList();
        return new SmokeCheck("eventlog:crashes", parts.All(p => p.Passed), string.Join("; ", parts.Select(p => (p.Passed ? "" : "FAILED ") + p.Detail)));
    }

    /// <summary><c>eventlog:archive-error</c>: a missing <c>.logarchive</c> is OSLog's NSError, worded — never an Objective-C exception, which would end the process.</summary>
    [SupportedOSPlatform("macos")]
    public static SmokeCheck ProbeMacArchiveError() => MacLogProbes.ArchiveError(new MacUnifiedLog());
}
