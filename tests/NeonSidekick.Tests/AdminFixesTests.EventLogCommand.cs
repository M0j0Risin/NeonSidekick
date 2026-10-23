using NeonSidekick.EventLog;

namespace NeonSidekick.Tests;

/// <summary>Phase 10b of the admin review's fixes (2026-10-23): <c>/eventlog</c>'s line, the event logs' errors and answers.</summary>
public sealed partial class AdminFixesTests
{
    /// <summary>S1: a since as near as it was.</summary>
    [Theory]
    [InlineData(30, "under a minute ago")]
    [InlineData(60, "a minute ago")]
    [InlineData(300, "5 minutes ago")]
    [InlineData(3600, "an hour ago")]
    [InlineData(86400, "24 hours ago")]
    public void S1_ASince_AsNearAsItWas(int secondsBack, string words) =>
        Assert.Equal(words, EventLogText.Since(LogNow.AddSeconds(-secondsBack), LogNow, TimeZoneInfo.Utc));

    /// <summary>S2, S5, S11: the answers say a narrowed search, restate the limit, and name a forwarded log's newest by its cursor.</summary>
    [Fact]
    public void S2_S5_S11_TheAnswers()
    {
        Assert.Equal("", EventLogText.Narrowed(new EventLogQuery { Log = "System" }));
        Assert.NotEqual("", EventLogText.Narrowed(new EventLogQuery { Log = "System", Ids = [new EventIdRange(41, 41)] }));
        var group = new EventSummaryGroup("A", 1, 2, 0, 3, LogNow, 7, "m", false, new EventCursor(7, "0123abcd", LogNow));
        Assert.Contains("#7@0123abcd@", EventLogRender.SummaryLine(group, TimeZoneInfo.Utc));
    }

    /// <summary>W6, W10, P16, H12: /eventlog's line.</summary>
    [Fact]
    public void W6_W10_P16_H12_TheLine()
    {
        var (words, _) = EventLogCommand.Parse("level:error disk failure", LogNow, TimeZoneInfo.Utc, 20);
        Assert.Equal(EventLogCommand.DefaultLog, words!.Log);
        Assert.Equal("disk failure", words.Query.Text);
        Assert.Equal(EventLogText.KeyNeedsValue("level:"), EventLogCommand.Parse("System level:", LogNow, TimeZoneInfo.Utc, 20).Error);
        Assert.Equal(EventLogText.KeyNeedsValue("computer:"), EventLogCommand.Parse("System computer: dc01", LogNow, TimeZoneInfo.Utc, 20).Error);
        Assert.Equal(EventLogText.KeyTwice("level:"), EventLogCommand.Parse("System level:error level:warning", LogNow, TimeZoneInfo.Utc, 20).Error);
        var (line, _) = EventLogCommand.Parse("System since:1h", LogNow, TimeZoneInfo.Utc, 20);
        Assert.Equal(LogNow.AddHours(-1), line!.Query.Since);
        Assert.Equal(LogNow.AddHours(1), line.At(LogNow.AddHours(2), TimeZoneInfo.Utc).Query.Since);
    }

    /// <summary>J10, J11, W16, W7: the errors' sentences.</summary>
    [Fact]
    public void J10_J11_W16_W7_TheErrors()
    {
        Assert.Equal(EventLogText.FileAccessDenied("a.evtx"), EventLogErrors.Sentence(5, new EventLogErrorContext("this PC", "a.evtx", File: true)));
        Assert.EndsWith(EventLogText.OrPasswordWrong, EventLogErrors.Sentence(5, new EventLogErrorContext("dc1", "System", Remote: true, Host: "dc1", Account: @"CORP\ops")));
        Assert.DoesNotContain("password", EventLogErrors.Sentence(5, new EventLogErrorContext("dc1", "System", Remote: true, Host: "dc1")));
        Assert.Equal(EventLogText.ResultStale("System"), EventLogErrors.Sentence(15011, new EventLogErrorContext("this PC", "System")));
        Assert.StartsWith("Error: dc1 refused the account", EventLogErrors.Sentence(1793, new EventLogErrorContext("dc1", "System", Remote: true, Host: "dc1", Account: "x")));
        Assert.True(EventLogText.IsComputerWide(EventLogText.Unreachable("dc1")));
        Assert.True(EventLogText.IsComputerWide(EventLogText.BadSignIn("dc1", "x")));
        // DP7 (the branch review, 2026-10-23): every computer-wide sentence through its builder, so a reword can't turn the shortcut off unseen.
        Assert.True(EventLogText.IsComputerWide(EventLogText.NoLogonServer("dc1")));
        Assert.True(EventLogText.IsComputerWide(EventLogText.TimedOut("dc1")));
        Assert.True(EventLogText.IsComputerWide(EventLogText.CannotSignIn("dc1", "the password is held")));
        Assert.True(EventLogText.IsComputerWide(EventLogErrors.Sentence(1793, new EventLogErrorContext("dc1", "System", Remote: true, Host: "dc1", Account: "x"))));
        Assert.False(EventLogText.IsComputerWide(EventLogText.AccessDenied("Security", "dc1")));
    }
}
