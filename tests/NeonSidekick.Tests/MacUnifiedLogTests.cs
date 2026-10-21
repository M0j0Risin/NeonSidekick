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

    /// <summary>
    /// An entry found again by its handle (later on 2026-10-18, the phase 5 check's bug): the search's last row of a page of errors, read
    /// forward over its own microsecond, comes back as the same entry, and fast — a reverse find read everything newer than it first.
    /// </summary>
    [MacFact]
    public void AnEntry_IsFoundByItsHandle_ReadForward_Quickly()
    {
        var log = new MacUnifiedLog();
        var query = new MacLogQuery("system", ["error", "fault"], DateTimeOffset.UtcNow.AddMinutes(-30), Limit: 100);
        var scan = new EventScan(false, 20_000, EventScan.DefaultBudget, TimeProvider.System);
        MacSearchPage page;
        using (var reader = log.Open(UnifiedLogTarget.Local, query.Filter(redacted: false), CancellationToken.None))
        {
            page = MacLogSearch.Search(reader, query, scan, CancellationToken.None);
        }

        if (page.Entries.Count == 0)
        {
            return;   // a quiet half hour: nothing to find
        }

        var last = page.Entries[^1];
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        MacLogView? found;
        using (var reader = log.Open(UnifiedLogTarget.Local, MacLogSearch.FindFilter(last.Handle), CancellationToken.None))
        {
            found = MacLogSearch.Find(reader, last.Handle, redacted: false, CancellationToken.None);
        }

        Assert.NotNull(found);
        Assert.Equal(last.Entry.Message, found!.Entry.Message);
        var took = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        // 30 s, a runaway read's backstop, not 5 (2026-10-19, the Mac's CI-like run): under load the forward find took 2.5–8.5 s (0.8–1.2 s
        // unloaded), and on this Mac a reverse one over the same window took 1.0 s too, so time cannot tell them apart here; the find's
        // direction is pinned by MacLogToolsTests (its filter is Forward).
        Assert.True(took < TimeSpan.FromSeconds(30), $"the find read forward from its own microsecond: took {took.TotalSeconds:F1} s, {page.Entries.Count} rows, the last at {last.Entry.Time:O}");
    }

    /// <summary>
    /// A reverse read starts at the store's newest entry, at once, whatever its lower bound (2026-10-20, the code review's live check): put
    /// at its <c>since</c>, a read with no bound (1970: what <see cref="MacLogSearch.Anchor"/> reads an archive's newest entry with) gave
    /// nothing, and one bounded two days back began at the end of an older stretch of the store, a day and a half back, everything newer
    /// left out. The reader now puts a reverse read at its newest moment. The local store stands in for an archive (one takes
    /// <c>log collect</c>).
    /// </summary>
    [MacTheory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(60)]
    [InlineData(48 * 60)]
    public void AReverseRead_StartsAtTheNewestEntry_WhateverItsLowerBound(int minutesBack)
    {
        var log = new MacUnifiedLog();
        var since = minutesBack == 0 ? DateTimeOffset.UnixEpoch : DateTimeOffset.UtcNow.AddMinutes(-minutesBack);
        var started = Stopwatch.GetTimestamp();
        IReadOnlyList<MacLogEntry>? batch;
        using (var reader = log.Open(UnifiedLogTarget.Local, new MacLogFilter(since, []), CancellationToken.None))
        {
            while ((batch = reader.Next()) is { Count: 0 })
            {
            }
        }

        var took = Stopwatch.GetElapsedTime(started);
        Assert.NotNull(batch);
        Assert.True(DateTimeOffset.UtcNow - batch![0].Time < TimeSpan.FromMinutes(5), $"the first entry was at {batch[0].Time:O}");
        Assert.True(took < TimeSpan.FromSeconds(10), $"took {took.TotalSeconds:F1} s");
        Assert.Equal(0, MacStderr.Depth);
    }

    [MacFact]
    public void TheAnchorOfAMissingArchive_IsOSLogsRefusal()
    {
        Assert.Throws<UnifiedLogException>(() => MacLogSearch.Anchor(new MacUnifiedLog(), new MacLogSource("x.logarchive", UnifiedLogTarget.Archive("/nonexistent/x.logarchive"), false),
            new MacLogQuery("x", [], DateTimeOffset.UtcNow, SinceGiven: false), CancellationToken.None));
    }
}
