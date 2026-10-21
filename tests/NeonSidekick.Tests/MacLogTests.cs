using NeonSidekick.EventLog;

namespace NeonSidekick.Tests;

/// <summary>
/// The Mac event logs' pure parts (2026-10-18, the Mac event-log plan's phase 1): the unified log's predicate — constant fragments,
/// every value an argument, the time limit always last — the client-side match, the entry handles and the error sentences. Run
/// everywhere; the live read is <see cref="MacUnifiedLogTests"/>.
/// </summary>
public sealed class MacLogTests
{
    private static readonly DateTimeOffset Since = new(2026, 10, 18, 5, 0, 0, TimeSpan.Zero);

    private static MacLogEntry Entry(int level = 4, string process = "loginwindow", int pid = 395, string subsystem = "com.apple.loginwindow.logging",
        string category = "TAL", string message = "applicationQuit: for app:Single Sign-On", int minutesAfter = 30) =>
        new(Since.AddMinutes(minutesAfter), level, process, pid, 7, subsystem, category, "loginwindow", message);

    [Fact]
    public void AFilterWithNoTerms_IsEverythingBeforeTheTimeLimit()
    {
        var (format, arguments) = MacLogPredicate.Build(new MacLogFilter(Since, []));

        Assert.Equal("(TRUEPREDICATE) OR date < %@", format);
        Assert.Equal([MacPredicateArgument.Of(Since)], arguments);
    }

    [Fact]
    public void EveryTerm_IsAConstantFragment_ItsValueAnArgument_AndTheTimeLimitComesLast()
    {
        var until = Since.AddMinutes(50);
        var filter = new MacLogFilter(Since, ["error", "FAULT"], until, Process: "loginwindow", Pid: 395, Subsystem: "com.apple", Category: "TAL",
            Text: "quit", ExcludeProcesses: ["mediaanalysisd", "mediaanalysisd", " "]);

        var (format, arguments) = MacLogPredicate.Build(filter);

        Assert.Equal("((logType == %@ OR logType == %@) AND process == %@ AND processIdentifier == %@ AND subsystem BEGINSWITH %@ AND category == %@"
            + " AND composedMessage CONTAINS[c] %@ AND NOT (process IN %@) AND date <= %@) OR date < %@", format);
        Assert.Equal(MacLogPredicate.Placeholders(format), arguments.Count);
        Assert.Equal(MacPredicateArgument.Of("error"), arguments[0]);
        Assert.Equal(MacPredicateArgument.Of("fault"), arguments[1]);
        Assert.Equal(MacPredicateArgument.Of("loginwindow"), arguments[2]);
        Assert.Equal(MacPredicateArgument.Of(395), arguments[3]);
        Assert.Equal(["mediaanalysisd"], arguments[7].List);
        Assert.Equal(MacPredicateArgument.Of(until), arguments[8]);
        Assert.Equal(MacPredicateArgument.Of(Since), arguments[^1]);
    }

    [Fact]
    public void AForwardRead_IsHeldBetweenItsTimes_AndEndedPastItsUntil()
    {
        var until = Since.AddSeconds(1);
        var (format, arguments) = MacLogPredicate.Build(new MacLogFilter(Since, [], until, Pid: 395, Forward: true));

        Assert.Equal("(processIdentifier == %@ AND date >= %@ AND date <= %@) OR date > %@", format);
        Assert.Equal([MacPredicateArgument.Of(395), MacPredicateArgument.Of(Since), MacPredicateArgument.Of(until), MacPredicateArgument.Of(until)], arguments);
        Assert.Throws<ArgumentException>(() => MacLogPredicate.Build(new MacLogFilter(Since, [], Forward: true)));
    }

    /// <summary>Whatever is typed stays a value: the format is the same with or without the hostile words, and only NULs are taken out.</summary>
    [Theory]
    [InlineData("\" OR 1==1")]
    [InlineData("%@ %K %d")]
    [InlineData("') OR TRUEPREDICATE OR ('")]
    [InlineData("a\0b")]
    public void AHostileValue_NeverChangesTheFormat(string value)
    {
        var (plain, _) = MacLogPredicate.Build(new MacLogFilter(Since, [], Process: "x", Text: "y"));
        var (format, arguments) = MacLogPredicate.Build(new MacLogFilter(Since, [], Process: value, Text: value));

        Assert.Equal(plain, format);
        Assert.Equal(MacLogPredicate.Placeholders(format), arguments.Count);
        Assert.Equal(value.Replace("\0", "", StringComparison.Ordinal).Trim(), arguments[0].Text);
        Assert.DoesNotContain('\0', arguments[1].Text!);
    }

    [Fact]
    public void AllFiveLevels_AreNoTerm_AndAnUnknownLevelIsRefused()
    {
        var (format, _) = MacLogPredicate.Build(new MacLogFilter(Since, ["debug", "info", "default", "error", "fault"]));
        Assert.Equal("(TRUEPREDICATE) OR date < %@", format);
        Assert.Throws<ArgumentException>(() => MacLogPredicate.Build(new MacLogFilter(Since, ["notice"])));
        Assert.Equal(3, MacLogPredicate.LevelOf("default"));
        Assert.Equal(0, MacLogPredicate.LevelOf("notice"));
    }

    [Fact]
    public void TheMatch_ChecksEveryTermAgain_OnTheEntryItself()
    {
        var filter = new MacLogFilter(Since, ["error"], Process: "loginwindow", Subsystem: "com.apple", Category: "TAL", Text: "SINGLE SIGN");
        Assert.True(MacLogMatch.Matches(Entry(), filter));
        Assert.False(MacLogMatch.Matches(Entry(level: 5), filter));
        Assert.False(MacLogMatch.Matches(Entry(process: "LoginWindow"), filter));
        Assert.False(MacLogMatch.Matches(Entry(subsystem: "org.example"), filter));
        Assert.False(MacLogMatch.Matches(Entry(category: "tal"), filter));
        Assert.False(MacLogMatch.Matches(Entry(message: "nothing"), filter));
        Assert.False(MacLogMatch.Matches(Entry(minutesAfter: -1), filter));                                       // the time limit's own entry
        Assert.False(MacLogMatch.Matches(Entry(minutesAfter: 61), filter with { Until = Since.AddHours(1) }));
        Assert.False(MacLogMatch.Matches(Entry(), new MacLogFilter(Since, [], Pid: 1)));
        Assert.False(MacLogMatch.Matches(Entry(), new MacLogFilter(Since, [], ExcludeProcesses: ["loginwindow"])));
        Assert.True(MacLogMatch.Matches(Entry(level: 1), new MacLogFilter(Since, [])));
    }

    [Fact]
    public void AHandle_RoundTrips_NamesItsEntry_AndRefusesWhatItDidNotWrite()
    {
        var entry = Entry() with { Time = Since.AddTicks(1_234_567) };   // 123,456.7 µs past the hour
        var handle = MacLogHandle.Of(entry, 2);

        Assert.Equal("u" + MacLogHandle.MicrosecondsOf(entry.Time) + ".395.7.2." + MacLogHandle.MarkOf(entry).ToString("x8", System.Globalization.CultureInfo.InvariantCulture), handle.ToString());
        Assert.True(MacLogHandle.TryParse(" " + handle + " ", out var back));
        Assert.Equal(handle, back);
        Assert.True(back.Names(entry));
        Assert.False(back.Names(entry with { Pid = 396 }));
        Assert.Equal(MacLogHandle.MicrosecondsOf(entry.Time), MacLogHandle.MicrosecondsOf(back.Time));
        foreach (string bad in (string[])["", "1792.395.7.0", "u1792.395.7", "u1792.395.7.0.1", "u-5.395.7.0", "u1792.x.7.0", "u0.1.1.0", "eventlog"])
        {
            Assert.False(MacLogHandle.TryParse(bad, out _), bad);
        }
    }

    [Fact]
    public void TheErrors_ReadOSLogsWords()
    {
        Assert.Equal(MacLogText.NeedsAdmin, MacLogErrors.Sentence(new UnifiedLogException("OSLogErrorDomain", 9, "Operation not permitted")));
        Assert.Equal(MacLogText.NeedsAdmin, MacLogErrors.Sentence(new UnifiedLogException("NSCocoaErrorDomain", 257, "You don't have permission to read it.")));
        Assert.Equal(MacLogText.NoArchive("old.logarchive"),
            MacLogErrors.Sentence(new UnifiedLogException("NSCocoaErrorDomain", 4, "The file “old.logarchive” couldn’t be opened because there is no such file."), "/w/old.logarchive/"));
        Assert.Equal(MacLogText.FilterRefused, MacLogErrors.Sentence(new UnifiedLogException("OSLogErrorDomain", -1, "issue with predicate: no such field: level")));
        Assert.Equal("the unified log could not be read: something else", MacLogErrors.Sentence(new UnifiedLogException("X", 1, "something else")));
        Assert.Equal("the unified log can be read only from an administrator account on this Mac.", MacLogText.NeedsAdmin);
    }
}
