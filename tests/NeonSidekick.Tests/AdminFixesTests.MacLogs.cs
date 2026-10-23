using NeonSidekick.EventLog;
using NeonSidekick.Files;

namespace NeonSidekick.Tests;

/// <summary>Phase 11 of the admin review's fixes (2026-10-23): the Mac log readers' portable parts (the rest is proven on a Mac).</summary>
public sealed partial class AdminFixesTests
{
    private static MacLogEntry MacEntry(int level, string process = "kernel") =>
        new(LogNow, level, process, 1, 1, "com.example", "cat", "sender", "message");

    /// <summary>I13, X16, X18: every level asked keeps level 0; a blank exclusion is none; a level word is looked up.</summary>
    [Fact]
    public void I13_X16_X18_TheClientsMatch()
    {
        var all = new MacLogFilter(LogNow.AddHours(-1), MacLogPredicate.LevelWords);
        Assert.True(MacLogMatch.Matches(MacEntry(0), all));
        Assert.False(MacLogMatch.Matches(MacEntry(0), new MacLogFilter(LogNow.AddHours(-1), ["error"])));
        Assert.True(MacLogMatch.Matches(MacEntry(4, process: ""), new MacLogFilter(LogNow.AddHours(-1), [], ExcludeProcesses: ["  "])));
        Assert.Equal(4, MacLogPredicate.LevelOf("Error"));
        Assert.Equal(0, MacLogPredicate.LevelOf("loud"));
    }

    /// <summary>X5: the reader's mark over the whole message stands over the copy's cut one.</summary>
    [Fact]
    public void X5_TheMark_IsTheWholeMessages()
    {
        var cut = MacEntry(4);
        uint whole = MacLogHandle.MarkOf(4, cut.Subsystem, cut.Category, "message and the rest past the cut");
        Assert.NotEqual(MacLogHandle.MarkOf(cut), whole);
        Assert.Equal(whole, MacLogHandle.MarkOf(cut with { Mark = whole }));
    }

    /// <summary>X12, X4: the home by any spelling; a plain archive holds no links.</summary>
    [Fact]
    public void X12_X4_TheArchiveWalk()
    {
        Assert.Equal("/Users/ann", MacLogAccess.DataPath("/System/Volumes/Data/Users/ann"));
        Assert.Equal("/Users/ann", MacLogAccess.DataPath("/Users/ann"));
        string archive = Path.Combine(_dir, "a.logarchive");
        Directory.CreateDirectory(Path.Combine(archive, "Persist"));
        File.WriteAllText(Path.Combine(archive, "Persist", "0001.tracev3"), "x");
        Assert.False(MacLogAccess.HoldsLinks(archive));
    }

    /// <summary>N4: local's logs allow-list holds for the crashes and install.log.</summary>
    [Fact]
    public void N4_LocalsAllowList_Holds()
    {
        Directory.CreateDirectory(_dir);
        var access = new MacLogAccess(null, () => false, new WorkingDirectory(() => _dir, _time), _time, localLogs: () => ["system"]);
        Assert.NotNull(access.Resolve("system", forceRedacted: true).Source);
        Assert.Equal(EventLogText.LogNotAllowed(MacLogAccess.CrashNames[0], EventLogText.LocalName), access.Resolve("crashes", forceRedacted: true).Error);
    }

    /// <summary>X8: an .ips whose body was cut is said so, never read as text.</summary>
    [Fact]
    public void X8_ACutReport_IsSaidSo()
    {
        var report = new CrashReportInfo("x.ips", "/tmp/x.ips", LogNow, "crash", "x", "user");
        var (summary, text, _) = CrashReports.DescribeText("{\"bug_type\":\"309\"}\n{\"exception\": {\"type\": \"EXC_", report);
        Assert.Equal(MacLogText.ReportBodyUnread, text);
        Assert.DoesNotContain("{", summary);
    }

    /// <summary>X10, X13: the backward read stops on a cancel, and a line past the cap is dropped whole.</summary>
    [Fact]
    public void X10_X13_TheBackwardRead()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("2026-10-23 12:00:00-07 mac installd[1]: one\n"));
        Assert.Throws<OperationCanceledException>(() => InstallLog.Backward(stream, null, cancelled.Token).ToList());
        var huge = "2026-10-23 12:00:00-07 mac installd[1]: head\n" + new string('x', InstallLog.MaxLineBytes + 10) + "\n";
        using var big = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(huge));
        var entries = InstallLog.Backward(big).ToList();
        Assert.Single(entries);
        Assert.StartsWith("head", entries[0].Message);
    }

    /// <summary>S4: the copyable exclusion is the whole name quoted, with the query's own kept.</summary>
    [Fact]
    public void S4_TheCopyableExclusion()
    {
        string note = MacLogText.Loudest("very long process…", 9, 10, fenced: true, copy: "\"mds,very long process name\"");
        Assert.Contains("exclude_process=\"mds,very long process name\"", note);
    }
}
