using NeonSidekick.EventLog;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>Phase 10a of the admin review's fixes (2026-10-23): the Windows event logs' query, search and reading.</summary>
public sealed partial class AdminFixesTests
{
    private static readonly DateTimeOffset LogNow = ManualTimeProvider.DefaultUtcNow;

    /// <summary>A5: a tagged cursor carries its time, reads back, and its page starts at that moment.</summary>
    [Fact]
    public void A5_ATaggedCursor_CarriesItsTime()
    {
        var at = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero).AddTicks(9);
        var cursor = new EventCursor(42, "0123abcd", at);
        Assert.Equal(cursor, EventCursor.Parse(cursor.ToString()));
        Assert.Equal(new EventCursor(42, "0123abcd"), EventCursor.Parse("42@0123abcd"));
        var (xpath, _) = EventLogXPath.Build(new EventLogQuery { Log = "ForwardedEvents", BeforeRecord = cursor }, serverFields: false);
        Assert.Contains("TimeCreated[@SystemTime<='2026-10-23T12:00:00.001Z']", xpath);
        Assert.DoesNotContain("EventRecordID", xpath);
    }

    /// <summary>W14: an until's part of a millisecond rounds up, a since's down.</summary>
    [Fact]
    public void W14_TheMillisecondRoundsTowardTheClient()
    {
        var moment = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero).AddTicks(9);
        Assert.Equal("2026-10-23T12:00:00.001Z", EventLogXPath.Moment(moment, up: true));
        Assert.Equal("2026-10-23T12:00:00.000Z", EventLogXPath.Moment(moment));
        Assert.Equal("2026-10-23T12:00:00.000Z", EventLogXPath.Moment(moment.AddTicks(-9), up: true));
    }

    /// <summary>K1, P1: a control character is no log's name; a file pages by tagged cursors.</summary>
    [Fact]
    public void K1_P1_TheLogsName_AndAFile()
    {
        Assert.NotNull(EventLogQuery.NameProblem("Security\u0000/Operational"));
        Assert.Null(EventLogQuery.NameProblem("Microsoft-Windows-PowerShell/Operational"));
        Assert.True(EventLogQuery.IsForwarded(@"exports\collector.evtx"));
        Assert.False(EventLogQuery.IsForwarded("System"));
    }

    /// <summary>B1: on a channel that cannot repeat a record, the first match ends the read.</summary>
    [Fact]
    public void B1_TheFirstMatch_EndsTheRead()
    {
        var system = new FakeEventLogSystem();
        system.Logs["ForwardedEvents"] =
        [
            new(FakeEventLogSystem.Xml("A", 1, 4, 7, LogNow, "System", computer: "pc1")),
            new(FakeEventLogSystem.Xml("A", 1, 4, 7, LogNow.AddMinutes(-1), "System", computer: "pc2")),
        ];
        using (var reader = system.Query(EventLogTarget.Local, "ForwardedEvents", "*", CancellationToken.None))
        {
            Assert.Equal(2, EventLogSearch.Find(reader, new EventCursor(7), redacted: false, CancellationToken.None).Several.Count);
        }

        using (var reader = system.Query(EventLogTarget.Local, "ForwardedEvents", "*", CancellationToken.None))
        {
            var found = EventLogSearch.Find(reader, new EventCursor(7), redacted: false, CancellationToken.None, mayRepeat: false);
            Assert.NotNull(found.View);
            Assert.Empty(found.Several);
        }
    }

    /// <summary>W1: a first batch that took past the budget is still counted, and the page has a cursor to go on from.</summary>
    [Fact]
    public void W1_ASlowFirstBatch_IsCounted()
    {
        var system = new FakeEventLogSystem { BatchSize = 2 };
        system.Logs["System"] = [.. Enumerable.Range(1, 6).Select(i => new FakeEventLogSystem.FakeEvent(FakeEventLogSystem.Xml("A", 1, 2, 100 - i, LogNow.AddMinutes(-i), "System")))];
        using var reader = system.Query(EventLogTarget.Local, "System", "*", CancellationToken.None);
        var scan = new EventScan(false, 1000, TimeSpan.Zero, _time);   // the budget already spent before the first event
        var page = EventLogSearch.Search(reader, new EventLogQuery { Log = "System", Limit = 50 }, scan, CancellationToken.None);
        Assert.Equal(1, page.Scanned);
        Assert.NotNull(page.Cursor);
    }

    /// <summary>J2: a Windows sign-in's method reaches the target.</summary>
    [Fact]
    public void J2_TheWindowsSignInsMethod_ReachesTheTarget() =>
        Assert.Equal("kerberos", EventLogTarget.Remote("dc1", null, "kerberos").AuthMethod);
}
