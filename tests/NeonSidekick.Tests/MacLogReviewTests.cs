using System.Text;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.EventLog;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The Mac event logs' fixes of the 2026-10-20 code review, over the fake and plain files (portable): malformed reports, handles past the
/// calendar and their mark across levels, the reader's odd exceptions and an abandoned read observed, the scan's budget between empty
/// batches, the home as <c>~</c> in the unified log, the errors by domain and code, install.log's bad times, kept top and gone cursor, the
/// crash summary's scan limit, a name in two folders, a cursor kept while skipping, <c>local</c> unticked, the archive walk and its cache, an
/// archive's own window, one query builder for the tools and <c>/eventlog</c>, and the clamped limits.
/// </summary>
public sealed class MacLogReviewTests : IDisposable
{
    private static readonly DateTimeOffset Now = ManualTimeProvider.DefaultUtcNow;
    private readonly ManualTimeProvider _time = new();
    private readonly string _root = Directory.CreateTempSubdirectory("neon-maclog-review-").FullName;
    private readonly FakeUnifiedLog _log = new();
    private readonly AppSettingsData _settings = new() { EventLogTools = true };
    private readonly string _user;
    private readonly string _system;
    private bool _full;
    private bool _ticked = true;

    public MacLogReviewTests()
    {
        _user = Path.Combine(_root, "user");
        _system = Path.Combine(_root, "system");
        Directory.CreateDirectory(_user);
        Directory.CreateDirectory(_system);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private MacLogPlaces Places => new([new MacCrashFolder(_user, "user"), new MacCrashFolder(_system, "system")], Path.Combine(_root, "install.log"), _root);

    private MacLogAccess Access(IUnifiedLog? log = null) =>
        new(log ?? _log, () => _full, new WorkingDirectory(() => _root, _time), _time, Places, () => _ticked);

    private AIFunction Tool(string name, IUnifiedLog? log = null) => ChatScreen.MacLogTools(Access(log), () => _settings).Single(t => t.Name == name);

    private static async Task<string> Call(AIFunction tool, params (string Name, object? Value)[] arguments)
    {
        var args = new AIFunctionArguments();
        foreach (var (name, value) in arguments)
        {
            args[name] = value;
        }

        return (string)(await tool.InvokeAsync(args))!;
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "maclog", name);

    private static string? NextBefore(string answer) =>
        answer.Split('\n').FirstOrDefault(l => l.StartsWith("next: ", StringComparison.Ordinal)) is { } next
            ? next.Split(' ').Single(p => p.StartsWith("before=", StringComparison.Ordinal))["before=".Length..]
            : null;

    private static string[] Handles(string answer) =>
        answer.Split('\n').Where(l => l.StartsWith('u') && l.Contains("  ", StringComparison.Ordinal)).Select(l => l.Split("  ")[0]).ToArray();

    // ── D1: a malformed report ──────────────────────────────────────────────

    [Fact]
    public async Task AMalformedReport_IsReadWithLessInIt_AndNeverEndsTheListing()
    {
        File.WriteAllText(Path.Combine(_user, "Odd-2026-09-11-150000.ips"),
            "{\"bug_type\":\"309\",\"is_simulated\":1.5,\"timestamp\":\"2026-09-11 15:00:00.00 -0500\",\"name\":\"Odd\"}\n"
            + "{\"exception\":{\"type\":\"EXC_CRASH\"},\"faultingThread\":0,\"threads\":[1,{\"triggered\":true,\"frames\":[2,{\"imageOffset\":\"x\"}]}],\"lastExceptionBacktrace\":[3,{\"imageOffset\":1e400}]}");
        File.WriteAllText(Path.Combine(_user, "Jet-2026-09-11-150100.ips"),
            "{\"bug_type\":\"298\",\"timestamp\":\"2026-09-11 15:01:00.00 -0500\"}\n{\"processes\":[{\"name\":\"a\",\"rpages\":\"many\"},3],\"memoryStatus\":{\"pageSize\":\"x\"}}");
        File.Copy(Fixture("Widget-2026-09-11-150000.ips"), Path.Combine(_user, "Widget-2026-09-11-150000.ips"));

        string search = await Call(Tool(EventLogSearchTool.ToolName), ("log", "crashes"));
        string odd = await Call(Tool(EventLogEventTool.ToolName), ("log", "crashes"), ("record", "Odd-2026-09-11-150000.ips"));

        Assert.StartsWith("crashes on this Mac: 3 reports,", search, StringComparison.Ordinal);
        Assert.Contains("Widget-2026-09-11-150000.ips", search, StringComparison.Ordinal);
        Assert.Contains("\nkind: crash\n", odd, StringComparison.Ordinal);   // 1.5 is not the simulated flag
        Assert.Contains("exception: EXC_CRASH", odd, StringComparison.Ordinal);
    }

    // ── D2 and D6: the handle ───────────────────────────────────────────────

    [Fact]
    public async Task AHandlePastTheCalendar_IsNotAHandle_AndTheLatestOneStillReads()
    {
        Assert.False(MacLogHandle.TryParse("u999999999999999999.1.1.0", out _));
        Assert.True(MacLogHandle.TryParse("u" + MacLogHandle.MaxMicroseconds + ".1.1.0", out var latest));
        Assert.Equal(MacLogHandle.MaxMicroseconds, MacLogHandle.MicrosecondsOf(latest.Time));
        Assert.True(latest.Time + MacLogSearch.Microsecond > latest.Time);

        string answer = await Call(Tool(EventLogEventTool.ToolName), ("log", "system"), ("record", "u999999999999999999.1.1.0"));

        Assert.Equal(MacLogText.BadHandle("u999999999999999999.1.1.0"), answer);
    }

    /// <summary>An info entry and an error from one thread in one microsecond: the search shows the error, and its handle finds the error.</summary>
    [Fact]
    public async Task ATieAcrossLevels_FindsTheEntryTheSearchShowed()
    {
        var time = Now.AddMinutes(-3);
        _log.Entries.Add(FakeUnifiedLog.Entry(time, level: 2, message: "the info line"));
        _log.Entries.Add(FakeUnifiedLog.Entry(time, level: 4, message: "the error line"));

        string search = await Call(Tool(EventLogSearchTool.ToolName), ("log", "system"));
        string handle = Handles(search).Single();
        string answer = await Call(Tool(EventLogEventTool.ToolName), ("log", "system"), ("record", handle));
        string legacy = await Call(Tool(EventLogEventTool.ToolName), ("log", "system"), ("record", string.Join('.', handle.Split('.').Take(4))));

        Assert.Contains("⟪the error line⟫", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("the info line", answer, StringComparison.Ordinal);
        Assert.StartsWith("system on this Mac: entry ", legacy, StringComparison.Ordinal);   // a four-part handle from before the mark still reads
    }

    // ── D3 and I5: the read itself ──────────────────────────────────────────

    [Fact]
    public async Task AReaderThatThrowsAnOddException_IsAnErrorSentence()
    {
        string answer = await Call(Tool(EventLogSearchTool.ToolName, new ThrowingLog(new InvalidOperationException("no class OSLogEntryLog"))), ("log", "system"));
        string argument = await Call(Tool(EventLogSearchTool.ToolName, new ThrowingLog(new ArgumentException("bad value"))), ("log", "system"));

        Assert.Equal("Error: " + MacLogText.FileFailed("system", "no class OSLogEntryLog"), answer);
        Assert.StartsWith("Error: system could not be read: bad value", argument, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARead_GivenUpOnAtTheTimeout_FaultsLater_AndIsObserved()
    {
        var unobserved = new List<Exception>();
        void Heard(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            lock (unobserved)
            {
                unobserved.AddRange(e.Exception.InnerExceptions.Where(x => x.StackTrace?.Contains(nameof(FakeUnifiedLog), StringComparison.Ordinal) == true));
            }
        }

        TaskScheduler.UnobservedTaskException += Heard;
        try
        {
            using var gate = new SemaphoreSlim(0);
            _log.Gate = gate;
            var pending = Call(Tool(EventLogSearchTool.ToolName), ("log", "system"));
            while (_log.Opens.Count == 0)
            {
                await Task.Delay(5);
            }

            _time.Advance(MacLogAccess.Timeout);
            Assert.Equal(MacLogText.TimedOut, await pending.WaitAsync(TimeSpan.FromSeconds(10)));
            await Task.Delay(100);
            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            lock (unobserved)
            {
                Assert.Empty(unobserved);
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Heard;
        }
    }

    /// <summary>A read whose batches come back empty (OSLog slow on a sparse filter) still stops at the scan's budget, not at the timeout.</summary>
    [Fact]
    public async Task ASlowRead_StopsAtTheBudget_BetweenEmptyBatches()
    {
        _log.Entries.Add(FakeUnifiedLog.Entry(Now.AddMinutes(-1)));
        _log.EmptyBatches = 1_000;
        _log.OnNext = () => _time.Advance(TimeSpan.FromSeconds(1));

        string answer = await Call(Tool(EventLogSearchTool.ToolName), ("log", "system"));

        Assert.Contains("(stopped after reading 0 entries", answer, StringComparison.Ordinal);
    }

    // ── D4: the home in the unified log ─────────────────────────────────────

    [Fact]
    public async Task Redacted_TheHomeIsATilde_InTheMessageAndTheSender()
    {
        string home = Path.TrimEndingDirectorySeparator(_root);
        _log.Entries.Add(FakeUnifiedLog.Entry(Now.AddMinutes(-1), message: "opened " + home + "/notes.txt", sender: home + "/bin/tool"));

        string search = await Call(Tool(EventLogSearchTool.ToolName), ("log", "system"));
        string answer = await Call(Tool(EventLogEventTool.ToolName), ("log", "system"), ("record", Handles(search).Single()));
        _full = true;
        string full = await Call(Tool(EventLogEventTool.ToolName), ("log", "system"), ("record", Handles(search).Single()));

        Assert.Contains("⟪opened ~/notes.txt⟫", search, StringComparison.Ordinal);
        Assert.Contains("\nsender: ⟪~/bin/tool⟫", answer, StringComparison.Ordinal);
        Assert.DoesNotContain(home, answer, StringComparison.Ordinal);
        Assert.Contains("\nsender: ⟪" + home + "/bin/tool⟫", full, StringComparison.Ordinal);
    }

    // ── D5: the errors ──────────────────────────────────────────────────────

    [Fact]
    public void TheErrors_GoByDomainAndCode_AnArchivesFirst()
    {
        Assert.Equal(MacLogText.NeedsAdmin, MacLogErrors.Sentence(new UnifiedLogException("OSLogErrorDomain", 9, "Client lacks entitlement to perform operation")));
        Assert.Equal(MacLogText.NeedsAdmin, MacLogErrors.Sentence(new UnifiedLogException("NSCocoaErrorDomain", 257, "Sie haben keine Berechtigung.")));   // a German Mac
        Assert.Equal(MacLogText.ArchiveRefused("old.logarchive"), MacLogErrors.Sentence(new UnifiedLogException("NSCocoaErrorDomain", 257, "You don't have permission."), "/w/old.logarchive"));
        Assert.Equal(MacLogText.NoArchive("old.logarchive"), MacLogErrors.Sentence(new UnifiedLogException("OSLogErrorDomain", 9, "Client lacks entitlement"), "/w/old.logarchive"));
        Assert.Equal(MacLogText.NoArchive("old.logarchive"), MacLogErrors.Sentence(new UnifiedLogException("NSPOSIXErrorDomain", 2, "Datei nicht gefunden"), "/w/old.logarchive/"));
        Assert.Equal(MacLogText.FilterRefused, MacLogErrors.Sentence(new UnifiedLogException("NeonSidekick", 3, "the filter could not be made")));
        Assert.Equal(MacLogText.Refused("permission denied, but in no domain we know"), MacLogErrors.Sentence(new UnifiedLogException("X", 1, "permission denied, but in no domain we know")));
    }

    // ── D7, I3 and H2: install.log ──────────────────────────────────────────

    [Theory]
    [InlineData("2026-10-18 02:28:55+1430 mac installd[1]: past the zones")]
    [InlineData("0001-01-01 00:00:00+01 mac installd[1]: before the calendar")]
    public void InstallLog_ATimeThatCannotBeAMoment_IsNoEntry(string line)
    {
        Assert.Null(InstallLog.Head(line));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("2026-10-18 02:00:00-05 mac installd[2]: fine\n" + line + "\n"));

        var entry = Assert.Single(InstallLog.Backward(stream).ToList());

        Assert.StartsWith("fine\n", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallLog_AnEntryPastTheCap_KeepsItsTop_AsAtDoes()
    {
        var text = new StringBuilder("2026-10-18 02:00:00-05 mac installd[2]: older\n2026-10-18 02:28:55-05 mac installd[3]: products\n");
        for (int i = 0; i < 2_000; i++)
        {
            text.Append("\tline ").Append(i.ToString("0000", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        }

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text.ToString()));
        var newest = InstallLog.Backward(stream).First();

        Assert.StartsWith("products\n\tline 0000\n\tline 0001", newest.Message, StringComparison.Ordinal);
        Assert.Equal(MacLogLimits.MessageChars, newest.Message.Length);
        Assert.Equal(InstallLog.At(stream, newest.Offset)!.Message, newest.Message);
    }

    [Fact]
    public async Task InstallLog_ACursorWhoseEntryIsGone_IsSaid()
    {
        File.Copy(Fixture("install.log"), Places.InstallLog);
        var tool = Tool(EventLogSearchTool.ToolName);
        string one = await Call(tool, ("log", "install"), ("limit", 2));
        string before = NextBefore(one)!;
        File.WriteAllText(Places.InstallLog, "2026-09-11 13:00:00-07 testmac installd[9]: a new file after rotation\n");

        string answer = await Call(tool, ("log", "install"), ("limit", 2), ("before", before));
        var source = Access().Resolve("install", false).Source! with { Redacted = false };
        var pane = await MacLogReads.SearchAsync(Access(), source, new MacLogQuery("install", [], Now.AddDays(-1)), 20_000, before, CancellationToken.None);

        Assert.Equal(MacLogText.InstallCursorGone(before), answer);
        Assert.Equal(MacLogText.InstallCursorGone(before), pane.Error);
    }

    // ── D8, H8 and I6: the crash reports ────────────────────────────────────

    [Fact]
    public void ACrashSummary_StopsAtTheScanLimit()
    {
        foreach (string name in new[] { "Widget-2026-09-11-150000.ips", "JetsamEvent-2026-09-11-160500.ips", "logd_2026-09-11-140000_host.cpu_resource.diag" })
        {
            File.Copy(Fixture(name), Path.Combine(_user, name));
        }

        var summary = MacFileLogs.SummarizeCrashes(Places, new MacLogQuery("crashes", [], Now.AddDays(-7)), new EventScan(false, 2, EventScan.DefaultBudget, _time), CancellationToken.None);

        Assert.True(summary.BudgetStop);
        Assert.Equal(2, summary.Read);
        Assert.Equal(2, summary.Total);
    }

    [Fact]
    public async Task ANameInBothFolders_IsNamedWithItsFolder_AndReadFromIt()
    {
        File.Copy(Fixture("Widget-2026-09-11-150000.ips"), Path.Combine(_user, "Widget-2026-09-11-150000.ips"));
        File.Copy(Fixture("Widget-2026-09-11-150000.ips"), Path.Combine(_system, "Widget-2026-09-11-150000.ips"));

        string search = await Call(Tool(EventLogSearchTool.ToolName), ("log", "crashes"));
        string system = await Call(Tool(EventLogEventTool.ToolName), ("log", "crashes"), ("record", "system:Widget-2026-09-11-150000.ips"));
        string one = await Call(Tool(EventLogSearchTool.ToolName), ("log", "crashes"), ("limit", 1));
        string two = await Call(Tool(EventLogSearchTool.ToolName), ("log", "crashes"), ("limit", 1), ("before", NextBefore(one)));

        Assert.Contains("\nsystem:Widget-2026-09-11-150000.ips  ", search, StringComparison.Ordinal);
        Assert.Contains("\nuser:Widget-2026-09-11-150000.ips  ", search, StringComparison.Ordinal);
        Assert.Contains("\nfolder: system", system, StringComparison.Ordinal);
        Assert.Equal("system:Widget-2026-09-11-150000.ips", NextBefore(one));
        Assert.Contains("\nuser:Widget-2026-09-11-150000.ips  ", two, StringComparison.Ordinal);
        Assert.False(CrashReports.IsReportHandle("user:a/b.ips"));
        Assert.False(CrashReports.IsReportName("a:b.ips"));
    }

    [Fact]
    public async Task ThePanesMore_OnAReportThatIsGone_SaysSo()
    {
        var access = Access();
        var source = access.Resolve("crashes", false).Source! with { Redacted = false };

        var read = await MacLogReads.SearchAsync(access, source, new MacLogQuery("crashes", [], Now.AddDays(-7)), 20_000, "Gone-2026-09-11-000000.ips", CancellationToken.None);

        Assert.Equal(MacLogText.CursorGone("Gone-2026-09-11-000000.ips"), read.Error);
    }

    // ── H4 and H10: the policy ──────────────────────────────────────────────

    [Fact]
    public void FullDetail_TheTextPastTheCut_IsKept_RedactedIsMatchedOnWhatIsShown()
    {
        string whole = new string('a', 9_000) + " needle";
        var cut = FakeUnifiedLog.Entry(Now.AddMinutes(-1), message: whole[..MacLogLimits.MessageChars]);
        var filter = new MacLogFilter(Now.AddHours(-1), [], Text: "needle");

        Assert.True(MacLogMatch.Matches(cut, filter, whole));
        Assert.False(MacLogMatch.Matches(cut, filter));

        _log.Entries.Add(cut);
        var query = new MacLogQuery("system", [], Now.AddHours(-1), Text: "needle");
        IUnifiedLog log = _log;
        using (var reader = log.Open(UnifiedLogTarget.Local, query.Filter(false) with { Text = null }, CancellationToken.None))
        {
            Assert.Single(MacLogSearch.Search(reader, query, new EventScan(false, 20_000, EventScan.DefaultBudget, _time), CancellationToken.None).Entries);
        }

        using (var reader = log.Open(UnifiedLogTarget.Local, query.Filter(true), CancellationToken.None))
        {
            Assert.Empty(MacLogSearch.Search(reader, query, new EventScan(true, 20_000, EventScan.DefaultBudget, _time), CancellationToken.None).Entries);
        }
    }

    [Fact]
    public void ABudgetStop_WhileSkippingTowardTheCursor_KeepsTheCursor()
    {
        var time = Now.AddMinutes(-2);
        for (int i = 0; i < 3; i++)
        {
            _log.Entries.Add(FakeUnifiedLog.Entry(time, message: "tie " + i));
        }

        IUnifiedLog log = _log;
        var query = new MacLogQuery("system", [], Now.AddHours(-1));
        List<MacLogHandle> handles;
        using (var reader = log.Open(UnifiedLogTarget.Local, query.Filter(false), CancellationToken.None))
        {
            handles = MacLogSearch.Search(reader, query, new EventScan(false, 20_000, EventScan.DefaultBudget, _time), CancellationToken.None).Entries.Select(e => e.Handle).ToList();
        }

        var paged = query with { Before = handles[1] };
        using var again = log.Open(UnifiedLogTarget.Local, paged.Filter(false), CancellationToken.None);
        var page = MacLogSearch.Search(again, paged, new EventScan(false, 1, EventScan.DefaultBudget, _time), CancellationToken.None);

        Assert.True(page.BudgetStop);
        Assert.Empty(page.Entries);
        Assert.Equal(handles[1], page.Cursor);   // never tie 0's, the entry above the cursor: the next page would repeat
    }

    // ── H5: local unticked ──────────────────────────────────────────────────

    [Fact]
    public async Task LocalUnticked_RefusesTheMacsOwnLogs_ButReadsAnArchive()
    {
        _ticked = false;
        string folder = Path.Combine(_root, "saved.logarchive");
        Directory.CreateDirectory(folder);
        _log.Archives[Path.GetFullPath(folder)] = [FakeUnifiedLog.Entry(Now.AddMinutes(-1), message: "from the archive")];

        string system = await Call(Tool(EventLogSearchTool.ToolName), ("log", "system"));
        string crashes = await Call(Tool(EventLogSummaryTool.ToolName), ("log", "crashes"));
        string archive = await Call(Tool(EventLogSearchTool.ToolName), ("log", "saved.logarchive"));
        string logs = await Call(Tool(EventLogLogsTool.ToolName));

        Assert.Equal(MacLogText.LocalNotTicked, system);
        Assert.Equal(MacLogText.LocalNotTicked, crashes);
        Assert.Contains("from the archive", archive, StringComparison.Ordinal);
        Assert.Contains("\nsystem  " + MacLogText.LocalNotTickedLine + "\ncrashes  " + MacLogText.LocalNotTickedLine, logs, StringComparison.Ordinal);
        Assert.Contains("\nsaved.logarchive  " + MacLogText.ArchiveLine, logs, StringComparison.Ordinal);
        Assert.Equal(0, _log.Probes);
        Assert.True(MacLogAccess.Ticks(null));
        Assert.True(MacLogAccess.Ticks(["dc01", "LOCAL"]));
        Assert.False(MacLogAccess.Ticks([]));
        Assert.False(MacLogAccess.Ticks(["dc01"]));
    }

    // ── H6 and I7: the archive walk ─────────────────────────────────────────

    [Fact]
    public void TheArchiveWalk_SkipsTheHomesProtectedFolders_DotFoldersAndPackages_AndGoesTwoDeep()
    {
        foreach (string place in new[]
        {
            "a.logarchive", Path.Combine("x", "b.logarchive"), Path.Combine("x", "y", "c.logarchive"), Path.Combine("x", "y", "z", "d.logarchive"),
            Path.Combine("Library", "e.logarchive"), Path.Combine("Documents", "f.logarchive"), Path.Combine(".hidden", "g.logarchive"),
            Path.Combine("Tool.app", "h.logarchive"), Path.Combine("a.logarchive", "inner.logarchive"),
        })
        {
            Directory.CreateDirectory(Path.Combine(_root, place));
        }

        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(Path.Combine(_root, "link"), Path.Combine(_root, "x"));
        }

        var found = MacLogAccess.WalkArchives(_root, _root, CancellationToken.None);
        var fromElsewhere = MacLogAccess.WalkArchives(_root, Path.Combine(_root, "not-the-home"), CancellationToken.None);

        Assert.Equal(["a.logarchive", Path.Combine("x", "b.logarchive"), Path.Combine("x", "y", "c.logarchive")], found);
        Assert.Contains(Path.Combine("Library", "e.logarchive"), fromElsewhere);   // a Library that is not the home's is the user's own
    }

    [Fact]
    public async Task Completion_ReadsTheArchivesAsLastListed_NeverWalkingOnTheCaller()
    {
        Directory.CreateDirectory(Path.Combine(_root, "a.logarchive"));
        var access = Access();

        var first = access.CachedArchives();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        IReadOnlyList<string> later = [];
        while (later.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
            later = access.CachedArchives();
        }

        Assert.Empty(first);
        Assert.Equal(["a.logarchive"], later);
    }

    // ── I2 and I9: the window ───────────────────────────────────────────────

    [Fact]
    public async Task AnArchive_DefaultsToItsOwnLastHour_InTheToolsAndTheCommand()
    {
        string folder = Path.Combine(_root, "old.logarchive");
        Directory.CreateDirectory(folder);
        var newest = Now.AddDays(-3);
        _log.Archives[Path.GetFullPath(folder)] = [FakeUnifiedLog.Entry(newest, message: "three days back"), FakeUnifiedLog.Entry(newest.AddHours(-2), message: "too far back")];

        string answer = await Call(Tool(EventLogSearchTool.ToolName), ("log", "old.logarchive"));
        string summary = await Call(Tool(EventLogSummaryTool.ToolName), ("log", "old.logarchive"));
        var printed = await MacLogCommand.RunAsync(Access(), "old.logarchive", 20, 20_000, CancellationToken.None);

        Assert.Contains("⟪three days back⟫", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("too far back", answer, StringComparison.Ordinal);
        Assert.Contains(_log.Opens, o => o.Filter.Since == newest - MacLogQuery.DefaultWindow);
        Assert.Contains(": 1 entry in 1 group", summary, StringComparison.Ordinal);
        Assert.Equal("old.logarchive on this Mac: 1 entry, newest first", printed.Lines[0]);
    }

    [Fact]
    public async Task TheToolAndTheCommand_BuildOneQuery_AnUntilAloneEndsTheWindow()
    {
        string answer = await Call(Tool(EventLogSearchTool.ToolName), ("log", "system"), ("until", "2h"), ("level", "all"));
        var (args, _) = MacLogCommand.Parse("system until:2h level:all", Now, _time.LocalTimeZone);
        var source = Access().Resolve("system", false).Source!;
        var (query, _) = args!.QueryFor(source, Now, 20);

        Assert.StartsWith("system on this Mac", answer, StringComparison.Ordinal);
        var open = Assert.Single(_log.Opens);
        Assert.Equal(Now.AddHours(-3), open.Filter.Since);   // the hour before until, not the hour before now
        Assert.Equal(open.Filter.Since, query.Since);
        Assert.Equal(open.Filter.Until, query.Until);
        Assert.Equal(open.Filter.Levels, query.Levels);
    }

    // ── I4: the clamped limits ──────────────────────────────────────────────

    [Fact]
    public void TheEventLogLimits_AreClamped_ForHeadlessToo()
    {
        Assert.Equal(AppSettingsData.MinEventLogMaxEvents, ChatScreen.EventLogLimit(new AppSettingsData { EventLogMaxEvents = -1 }));
        Assert.Equal(AppSettingsData.MinEventLogScanLimit, ChatScreen.EventLogScanLimit(new AppSettingsData { EventLogScanLimit = 0 }));
        Assert.Equal(AppSettingsData.MaxEventLogMaxEvents, ChatScreen.EventLogLimit(new AppSettingsData { EventLogMaxEvents = int.MaxValue }));
    }

    /// <summary>A unified log whose every open throws what it is given (a reader's exception that is not OSLog's).</summary>
    private sealed class ThrowingLog(Exception failure) : IUnifiedLog
    {
        public IUnifiedLogReader Open(UnifiedLogTarget target, MacLogFilter filter, CancellationToken cancellationToken) => throw failure;

        public void Probe(UnifiedLogTarget target) => throw failure;
    }
}
