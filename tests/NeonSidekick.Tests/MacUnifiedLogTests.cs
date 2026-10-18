using System.Diagnostics;
using NeonSidekick.EventLog;
using NeonSidekick.Platform;

namespace NeonSidekick.Tests;

/// <summary>
/// The unified log read for real (2026-10-18, the Mac event-log plan's phase 1): this Mac's own store through
/// <see cref="MacUnifiedLog"/>, on an administrator account (Apple's local store is for one; the spike's account was). Each read is held
/// to a short window: the log is enormous (Phase 0: 20,000 errors and faults were 2.3 minutes), and the predicate's time limit is
/// what ends a read with few or no matches. Skipped off a Mac.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public sealed class MacUnifiedLogTests
{
    private static List<MacLogEntry> ReadAll(MacLogFilter filter, int most = 500, UnifiedLogTarget? target = null)
    {
        var log = new MacUnifiedLog();
        using var reader = log.Open(target ?? UnifiedLogTarget.Local, filter, CancellationToken.None);
        var entries = new List<MacLogEntry>();
        while (entries.Count < most && reader.Next() is { } batch)
        {
            entries.AddRange(batch);
        }

        return entries;
    }

    [MacFact]
    public void ErrorsAndFaults_ComeNewestFirst_WithinTheWindow_AndStderrIsBackAfter()
    {
        var since = DateTimeOffset.UtcNow.AddMinutes(-2);

        var entries = ReadAll(new MacLogFilter(since, ["error", "fault"]), most: 300);

        Assert.All(entries, e => Assert.InRange(e.Level, 4, 5));
        Assert.All(entries, e => Assert.True(e.Time >= since));
        for (int i = 1; i < entries.Count; i++)
        {
            Assert.True(entries[i].Time <= entries[i - 1].Time.AddSeconds(1), "newest first");   // a second's slack for clock adjustments
        }

        Assert.Equal(0, MacStderr.Depth);
    }

    /// <summary>A predicate with no match ends at the time limit (Phase 0: without the clause such a read ran the full 300 s).</summary>
    [MacFact]
    public void AnUnmatchableRead_EndsAtTheTimeLimit_Quickly()
    {
        var watch = Stopwatch.StartNew();

        var entries = ReadAll(new MacLogFilter(DateTimeOffset.UtcNow.AddMinutes(-2), [], Pid: 999_999_999));

        Assert.Empty(entries);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"took {watch.Elapsed}");
    }

    [MacFact]
    public void AHostileValue_IsOnlyAValue()
    {
        var entries = ReadAll(new MacLogFilter(DateTimeOffset.UtcNow.AddMinutes(-1), [], Text: "\" OR 1==1 %@ %K ') OR TRUEPREDICATE OR ('"));

        Assert.Empty(entries);
    }

    [MacFact]
    public void AProcessFilter_ReadsOnlyThatProcess()
    {
        var entries = ReadAll(new MacLogFilter(DateTimeOffset.UtcNow.AddMinutes(-5), [], Process: "kernel"), most: 50);

        Assert.All(entries, e => Assert.Equal("kernel", e.Process));
    }

    [MacFact]
    public void AMissingArchive_IsARefusal_NotACrash()
    {
        string path = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", "missing-" + Guid.NewGuid().ToString("N") + ".logarchive");
        var log = new MacUnifiedLog();

        var failure = Assert.Throws<UnifiedLogException>(() => log.Probe(UnifiedLogTarget.Archive(path)));

        Assert.NotEqual("", MacLogErrors.Sentence(failure, path));
        Assert.Equal(0, MacStderr.Depth);
    }
}
