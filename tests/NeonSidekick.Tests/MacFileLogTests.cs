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
/// A Mac's logs that are files (2026-10-18, the Mac event-log plan's phase 3): the crash and diagnostic reports (<see cref="CrashReports"/>:
/// the header, the kinds, a crash's exception, termination and faulting thread, a jetsam's ended processes, a text report's headings, the
/// names a handle may be) and install.log (<see cref="InstallLog"/>: newest first across read edges, continued entries, the handle's time
/// checked again), then the four tools over both under <c>Fixtures/maclog</c> copied into a temporary home, and <c>/eventlog</c>'s words and
/// printed lines over all three logs (phase 4, <see cref="MacLogCommand"/>, <see cref="MacLogReads"/>). Portable: plain files and the fake.
/// </summary>
public sealed class MacFileLogTests : IDisposable
{
    private const string Home = "/Users/tester";
    private readonly ManualTimeProvider _time = new();
    private readonly string _root = Directory.CreateTempSubdirectory("neon-maclog-files-").FullName;
    private readonly AppSettingsData _settings = new() { EventLogTools = true };
    private readonly MacLogPlaces _places;
    private bool _full;

    public MacFileLogTests()
    {
        string user = Path.Combine(_root, "user");
        string system = Path.Combine(_root, "system");
        Directory.CreateDirectory(Path.Combine(user, CrashReports.Retired));
        Directory.CreateDirectory(system);
        Copy("Widget-2026-09-11-150000.ips", user);
        Copy("ExcUserFault_lsd-2026-09-11-120000.ips", Path.Combine(user, CrashReports.Retired));
        Copy("JetsamEvent-2026-09-11-160500.ips", system);
        Copy("logd_2026-09-11-140000_host.cpu_resource.diag", system);
        File.WriteAllText(Path.Combine(user, "notes.txt"), "not a report");
        File.WriteAllText(Path.Combine(user, ".hidden.ips"), "{}\n{}");
        Copy("install.log", _root);
        _places = new MacLogPlaces([new MacCrashFolder(user, "user"), new MacCrashFolder(system, "system")], Path.Combine(_root, "install.log"), Home);
    }

    public void Dispose()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(_places.CrashFolders[1].Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "maclog", name);

    private static void Copy(string name, string folder) => File.Copy(Fixture(name), Path.Combine(folder, name));

    private static CrashReportInfo Report(string name) => CrashReports.Info(Fixture(name), name, DateTimeOffset.UnixEpoch, "user");

    private readonly FakeUnifiedLog _log = new();

    private MacLogAccess Access() =>
        new(_log, () => _full, new WorkingDirectory(() => _root, _time), _time, _places);

    private AIFunction Tool(string name, bool forceRedacted = false) =>
        ChatScreen.MacLogTools(Access(), () => _settings, forceRedacted).Single(t => t.Name == name);

    private static async Task<string> Call(AIFunction tool, params (string Name, object? Value)[] arguments)
    {
        var args = new AIFunctionArguments();
        foreach (var (name, value) in arguments)
        {
            args[name] = value;
        }

        return (string)(await tool.InvokeAsync(args))!;
    }

    private static string? NextBefore(string answer) =>
        answer.Split('\n').FirstOrDefault(l => l.StartsWith("next: ", StringComparison.Ordinal)) is { } next
            ? next.Split(' ').Single(p => p.StartsWith("before=", StringComparison.Ordinal))["before=".Length..]
            : null;

    // ── The reports ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Widget-2026-09-11-150000.ips", "crash", "Widget", "2026-09-11T20:00:00Z")]
    [InlineData("JetsamEvent-2026-09-11-160500.ips", "jetsam", "JetsamEvent", "2026-09-11T21:05:00Z")]
    [InlineData("ExcUserFault_lsd-2026-09-11-120000.ips", "fault", "lsd", "2026-09-11T17:00:00Z")]
    [InlineData("logd_2026-09-11-140000_host.cpu_resource.diag", "resource", "logd", "2026-09-11T19:00:00.084Z")]
    public void AReportsHeader_GivesItsKindProcessAndTime(string name, string kind, string process, string time)
    {
        var report = Report(name);

        Assert.Equal(kind, report.Kind);
        Assert.Equal(process, report.Process);
        Assert.Equal(DateTimeOffset.Parse(time, System.Globalization.CultureInfo.InvariantCulture), report.Time);
    }

    [Fact]
    public void ACrash_SaysItsExceptionTermination_AndTheFaultingThreadsTopFrames()
    {
        var detail = CrashReports.Describe(Report("Widget-2026-09-11-150000.ips"), text => text);
        var lines = detail.Text.Split('\n');

        Assert.Equal("EXC_CRASH (SIGABRT) — Abort trap: 6 — libsystem_c.dylib: abort() called", detail.Summary);
        Assert.Equal(4242, detail.Pid);
        Assert.Contains("process: Widget[4242]  /Users/tester/Applications/Widget.app/Contents/MacOS/Widget", lines);
        Assert.Contains("parent: launchd[1]", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("responsible:", StringComparison.Ordinal));   // the same process
        Assert.Contains("system: macOS 15.7.9 (24G830)", lines);
        Assert.Contains("termination: SIGNAL  code 6  Abort trap: 6  by Widget", lines);
        Assert.Contains("faulting thread 1 (com.apple.main-thread):", lines);
        Assert.Contains("  0  libsystem_kernel.dylib  __pthread_kill + 8", lines);
        Assert.Contains("  2  libsystem_c.dylib  abort", lines);
        Assert.Contains("  3  Widget  0x1000", lines);
        Assert.Equal(CrashReports.Frames, lines.Count(l => l.StartsWith("  ", StringComparison.Ordinal) && char.IsDigit(l[2])));
        Assert.Contains("  " + MacLogText.MoreFrames(8), lines);
        Assert.DoesNotContain("mach_msg2_trap", detail.Text, StringComparison.Ordinal);   // the other thread's
    }

    [Fact]
    public void Redacted_TheHomeIsATilde_AndSecretsAreHidden()
    {
        var detail = CrashReports.Describe(Report("Widget-2026-09-11-150000.ips"), MacFileLogs.Redactor(MacLogPlaces.Under(Home), redacted: true));

        Assert.Contains("process: Widget[4242]  ~/Applications/Widget.app/Contents/MacOS/Widget", detail.Text, StringComparison.Ordinal);
        Assert.Contains("password=<redacted>", detail.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", detail.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(Home, detail.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/Users/tester/x", "~/x")]
    [InlineData("/Users/tester", "~")]
    [InlineData("\"/Users/tester\"", "\"~\"")]
    [InlineData("/Users/testerx/y", "/Users/testerx/y")]
    public void Tilde_ReplacesOnlyTheWholeHomeFolder(string text, string expected) => Assert.Equal(expected, MacLogPlaces.Under(Home).Tilde(text));

    [Fact]
    public void AJetsam_NamesTheProcessesEnded_AndTheLargest()
    {
        var detail = CrashReports.Describe(Report("JetsamEvent-2026-09-11-160500.ips"), text => text);

        Assert.Equal("low memory: 1 process ended, first knowledgeconstructiond[44469] (per-process-limit); largest Virtualization", detail.Summary);
        Assert.Contains("resident: Virtualization[900]  3,125 MB", detail.Text, StringComparison.Ordinal);
        Assert.Contains("ended: knowledgeconstructiond[44469] (per-process-limit)", detail.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ATextReport_IsSummedUpByItsHeadings()
    {
        var detail = CrashReports.Describe(Report("logd_2026-09-11-140000_host.cpu_resource.diag"), text => text);

        Assert.Equal("cpu usage — action taken: none — 90 seconds cpu time over 123 seconds (73% cpu average), exceeding limit of 50% cpu over 180 seconds", detail.Summary);
        Assert.Equal(319, detail.Pid);
        Assert.Contains("Heaviest stack for the target process:", detail.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Widget-2026-09-11-150000.ips", true)]
    [InlineData("x.diag", true)]
    [InlineData("../x.ips", false)]
    [InlineData("a/x.ips", false)]
    [InlineData("a\\x.ips", false)]
    [InlineData(".hidden.ips", false)]
    [InlineData("notes.txt", false)]
    [InlineData("", false)]
    public void AHandle_IsAPlainReportName(string name, bool ok) => Assert.Equal(ok, CrashReports.IsReportName(name));

    [Theory]
    [InlineData("2026-10-18 01:14:40.00 -0500", "2026-10-18T06:14:40Z")]
    [InlineData("2026-10-18 01:14:40 +0530", "2026-10-17T19:44:40Z")]
    [InlineData("2026-10-18 01:14:40.25 -05:00", "2026-10-18T06:14:40.25Z")]
    public void ReportTimes_Parse(string text, string expected) =>
        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), CrashReports.ParseTime(text));

    [Theory]
    [InlineData("not a time")]
    [InlineData("2026-13-40 01:14:40 -0500")]
    [InlineData(null)]
    public void NotATime_IsNull(string? text) => Assert.Null(CrashReports.ParseTime(text));

    // ── install.log ─────────────────────────────────────────────────────────

    [Fact]
    public void InstallLog_ReadsNewestFirst_JoiningContinuedLines()
    {
        using var stream = File.OpenRead(Fixture("install.log"));
        var entries = InstallLog.Backward(stream).ToList();

        Assert.Equal(["softwareupdated", "package_script_service", "package_script_service", "Installer Progress", "softwareupdated", "installd"], entries.Select(e => e.Process));
        Assert.Equal("Products found:\n\t122-15866\t | Safari 26.5\n\t122-15867\t | Xcode Tools", entries[4].Message);
        Assert.Null(entries[3].Pid);
        Assert.Equal(534, entries[0].Pid);
        Assert.Equal("testmac", entries[0].Host);
        Assert.Equal(0, entries[^1].Offset);
        foreach (var entry in entries)
        {
            Assert.Equal(entry, InstallLog.At(stream, entry.Offset));
        }
    }

    /// <summary>Read in 64 KB pieces, every entry comes back whole across the edges, multi-byte characters and long continued entries included.</summary>
    [Fact]
    public void InstallLog_AcrossReadEdges_MatchesAForwardRead()
    {
        var text = new StringBuilder();
        var start = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.FromHours(-5));
        for (int i = 0; i < 3_000; i++)
        {
            text.Append(start.AddSeconds(i).ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)).Append("-05 mac installd[")
                .Append(i).Append("]: package ").Append(i).Append(" — café ✓ ").Append(new string('x', i % 97)).Append('\n');
            if (i % 50 == 0)
            {
                text.Append("\tcontinued ").Append(i).Append(" ünïcode\n");
            }
        }

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text.ToString()));
        var entries = InstallLog.Backward(stream).ToList();

        Assert.Equal(3_000, entries.Count);
        Assert.Equal(Enumerable.Range(0, 3_000).Reverse(), entries.Select(e => e.Pid!.Value));
        Assert.All(entries, e => Assert.Contains("café ✓", e.Message, StringComparison.Ordinal));
        Assert.Equal("package 50 — café ✓ " + new string('x', 50) + "\n\tcontinued 50 ünïcode", entries[^51].Message);
        Assert.Equal(entries[1234], InstallLog.At(stream, entries[1234].Offset));
        Assert.Equal(entries.Skip(101), InstallLog.Backward(stream, entries[100].Offset));
    }

    [Fact]
    public void InstallLog_Handles_RoundTrip_AndAStaleTimeIsGone()
    {
        using var stream = File.OpenRead(Fixture("install.log"));
        var newest = InstallLog.Backward(stream).First();
        string handle = InstallLog.Handle(newest);

        Assert.True(InstallLog.TryParseHandle(handle, out long offset, out long seconds));
        Assert.Equal(newest.Offset, offset);
        Assert.NotNull(MacFileLogs.InstallEvent(stream, _places, handle, redacted: false));
        Assert.Null(MacFileLogs.InstallEvent(stream, _places, "i" + offset + "." + (seconds + 1), redacted: false));
        Assert.Null(MacFileLogs.InstallEvent(stream, _places, "i" + (offset + 3) + "." + seconds, redacted: false));
        Assert.False(InstallLog.TryParseHandle("u1.2.3.0", out _, out _));
    }

    // ── The tools ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_Crashes_ByDefault_ShowsTheWeeksReports_NewestFirst()
    {
        string answer = await Call(Tool(EventLogSearchTool.ToolName), ("log", "crashes"));
        var lines = answer.Split('\n');

        Assert.StartsWith("crashes on this Mac: 4 reports, newest first (read 4 reports since ", lines[0], StringComparison.Ordinal);
        Assert.Contains(EventLogText.RedactedNote, lines[0], StringComparison.Ordinal);
        Assert.Equal(EventLogText.QuoteNote, lines[1]);
        Assert.StartsWith("JetsamEvent-2026-09-11-160500.ips  ", lines[2], StringComparison.Ordinal);
        Assert.Contains("  jetsam  ⟪JetsamEvent⟫  ⟪low memory: 1 process ended", lines[2], StringComparison.Ordinal);
        Assert.StartsWith("Widget-2026-09-11-150000.ips  ", lines[3], StringComparison.Ordinal);
        Assert.EndsWith("  crash  ⟪Widget⟫[4242]  ⟪EXC_CRASH (SIGABRT) — Abort trap: 6 — libsystem_c.dylib: abort() called⟫", lines[3], StringComparison.Ordinal);
        Assert.StartsWith("logd_2026-09-11-140000_host.cpu_resource.diag  ", lines[4], StringComparison.Ordinal);
        Assert.StartsWith("ExcUserFault_lsd-2026-09-11-120000.ips  ", lines[5], StringComparison.Ordinal);   // from Retired
        Assert.DoesNotContain(".hidden", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("notes.txt", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_Crashes_Pages_ByTheReportsName()
    {
        var tool = Tool(EventLogSearchTool.ToolName);

        string one = await Call(tool, ("log", "crashes"), ("limit", 3));
        string? before = NextBefore(one);
        string two = await Call(tool, ("log", "crashes"), ("limit", 3), ("before", before));
        string gone = await Call(tool, ("log", "crashes"), ("before", "Gone-2026-09-11-000000.ips"));
        string bad = await Call(tool, ("log", "crashes"), ("before", "../../etc/x.ips"));

        Assert.Equal("logd_2026-09-11-140000_host.cpu_resource.diag", before);
        Assert.Contains("next: log=crashes since=", one, StringComparison.Ordinal);
        Assert.StartsWith("crashes on this Mac: 1 report,", two, StringComparison.Ordinal);
        Assert.Contains("ExcUserFault_lsd-2026-09-11-120000.ips", two, StringComparison.Ordinal);
        Assert.Null(NextBefore(two));
        Assert.Equal(MacLogText.CursorGone("Gone-2026-09-11-000000.ips"), gone);
        Assert.Equal(MacLogText.BadFileHandle("../../etc/x.ips", "crashes"), bad);
    }

    [Fact]
    public async Task Search_Crashes_ProcessAndText_AndTermsTheyLackAreNoted()
    {
        string widget = await Call(Tool(EventLogSearchTool.ToolName), ("log", "crash"), ("process", "widget"), ("level", "error"), ("pid", 4242));
        string redacted = await Call(Tool(EventLogSearchTool.ToolName), ("log", "crashes"), ("text", "hunter2"));
        _full = true;
        string full = await Call(Tool(EventLogSearchTool.ToolName), ("log", "crashes"), ("text", "hunter2"));
        string frames = await Call(Tool(EventLogSearchTool.ToolName), ("log", "crashes"), ("text", "__pthread_kill"));

        Assert.StartsWith("crashes on this Mac: 1 report,", widget, StringComparison.Ordinal);
        Assert.Contains(MacLogText.NotApplied("crashes", ["level", "pid"]), widget, StringComparison.Ordinal);
        Assert.Contains("no crash or diagnostic reports since", redacted, StringComparison.Ordinal);
        Assert.Contains("Widget-2026-09-11-150000.ips", full, StringComparison.Ordinal);
        Assert.Contains("Widget-2026-09-11-150000.ips", frames, StringComparison.Ordinal);
        Assert.DoesNotContain("JetsamEvent", frames, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Event_ACrashReport_InFull_Redacted()
    {
        string answer = await Call(Tool(EventLogEventTool.ToolName), ("log", "crashes"), ("record", "Widget-2026-09-11-150000.ips"));
        string gone = await Call(Tool(EventLogEventTool.ToolName), ("log", "crashes"), ("record", "Gone-2026-09-11-000000.ips"));
        string outside = await Call(Tool(EventLogEventTool.ToolName), ("log", "crashes"), ("record", "../install.log"));

        Assert.StartsWith("crashes on this Mac: report Widget-2026-09-11-150000.ips; " + EventLogText.RedactedNote + "\n" + EventLogText.QuoteNote + "\ntime: ", answer, StringComparison.Ordinal);
        Assert.Contains("\nkind: crash\nprocess: ⟪Widget⟫ (pid 4242)\nfolder: user\nsummary: ⟪EXC_CRASH (SIGABRT) — Abort trap: 6 — libsystem_c.dylib: abort() called⟫\nreport:\n⟪process: Widget[4242]  ~/Applications", answer, StringComparison.Ordinal);
        Assert.Contains("  0  libsystem_kernel.dylib  __pthread_kill + 8", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", answer, StringComparison.Ordinal);
        Assert.Equal(MacLogText.NoEntry("Gone-2026-09-11-000000.ips", "crashes"), gone);
        Assert.Equal(MacLogText.BadFileHandle("../install.log", "crashes"), outside);
    }

    [Fact]
    public async Task Search_Install_ByDefault_ShowsTheLastDay_AndPages()
    {
        var tool = Tool(EventLogSearchTool.ToolName);

        string one = await Call(tool, ("log", "install"), ("limit", 2), ("level", "fault"));
        string two = await Call(tool, ("log", "install.log"), ("limit", 2), ("before", NextBefore(one)));
        string three = await Call(tool, ("log", "install"), ("limit", 2), ("before", NextBefore(two)));

        Assert.StartsWith("install on this Mac: 2 entries, newest first (read 2 entries since ", one, StringComparison.Ordinal);
        Assert.Contains(MacLogText.NotApplied("install", ["level"]), one, StringComparison.Ordinal);
        Assert.Contains("  ⟪softwareupdated⟫[534]  ⟪SUOSUServiceDaemon: idle⟫", one, StringComparison.Ordinal);
        Assert.Contains("  ⟪package_script_service⟫[812]  ⟪./postinstall: done⟫", one, StringComparison.Ordinal);
        Assert.Contains("  ⟪package_script_service⟫[812]  ⟪./postinstall: password=<redacted> written to ~/.config/widget⟫", two, StringComparison.Ordinal);
        Assert.Contains("  ⟪Installer Progress⟫  ⟪Install succeeded⟫", two, StringComparison.Ordinal);
        Assert.Contains("  ⟪softwareupdated⟫[534]  ⟪Products found:⟫", three, StringComparison.Ordinal);
        Assert.DoesNotContain("Old Thing", one + two + three, StringComparison.Ordinal);   // more than a day back
        Assert.Null(NextBefore(three));
    }

    [Fact]
    public async Task Event_AnInstallEntry_InFull_AndSummaryCountsByProcess()
    {
        string search = await Call(Tool(EventLogSearchTool.ToolName), ("log", "install"), ("text", "Products"));
        string handle = search.Split('\n').Single(l => l.Length > 1 && l[0] == 'i' && char.IsDigit(l[1])).Split("  ")[0];

        string answer = await Call(Tool(EventLogEventTool.ToolName), ("log", "install"), ("record", handle));
        string summary = await Call(Tool(EventLogSummaryTool.ToolName), ("log", "install"));
        string crashes = await Call(Tool(EventLogSummaryTool.ToolName), ("log", "crashes"));
        string bad = await Call(Tool(EventLogEventTool.ToolName), ("log", "install"), ("record", "u1.2.3.0"));

        Assert.Contains("\nprocess: ⟪softwareupdated⟫ (pid 534)\nmessage:\n⟪Products found:\n\t122-15866\t | Safari 26.5\n\t122-15867\t | Xcode Tools⟫", answer, StringComparison.Ordinal);
        Assert.Contains(": 5 entries in 3 groups (read 5 entries)", summary, StringComparison.Ordinal);
        Assert.Contains("\n2×  ⟪package_script_service⟫  newest ", summary, StringComparison.Ordinal);
        Assert.Contains(": 4 reports in 4 groups (read 4 reports)", crashes, StringComparison.Ordinal);
        Assert.Contains("1×  crash  ⟪Widget⟫  newest ", crashes, StringComparison.Ordinal);
        Assert.Equal(MacLogText.BadFileHandle("u1.2.3.0", "install"), bad);
    }

    [Fact]
    public async Task Logs_NameTheCrashesAndInstall()
    {
        string answer = await Call(Tool(EventLogLogsTool.ToolName));
        var lines = answer.Split('\n');

        Assert.Equal("crashes  " + MacLogText.CrashesLine(4, [("user", null), ("system", null)]), lines[2]);
        Assert.StartsWith("install  install.log: software installs and updates, newest entry ", lines[3], StringComparison.Ordinal);
    }

    [UnixFact]
    public async Task AFolderThisAccountMayNotRead_IsNamed_AndTheOthersStillRead()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string system = _places.CrashFolders[1].Path;
        File.SetUnixFileMode(system, UnixFileMode.None);
        try
        {
            if (Directory.EnumerateFiles(system).Any())
            {
                return;   // run as root: nothing is refused
            }
        }
        catch (UnauthorizedAccessException)
        {
        }

        string logs = await Call(Tool(EventLogLogsTool.ToolName));
        string search = await Call(Tool(EventLogSearchTool.ToolName), ("log", "crashes"));

        Assert.Contains(MacLogText.CrashesLine(2, [("user", null), ("system", MacLogText.FolderRefused)]), logs, StringComparison.Ordinal);
        Assert.StartsWith("crashes on this Mac: 2 reports,", search, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingInstallLog_IsASentence()
    {
        File.Delete(_places.InstallLog);

        string answer = await Call(Tool(EventLogSearchTool.ToolName), ("log", "install"));
        string logs = await Call(Tool(EventLogLogsTool.ToolName));

        Assert.StartsWith("Error: install could not be read: ", answer, StringComparison.Ordinal);
        Assert.Contains("\ninstall  install.log, not readable here: install could not be read: ", logs, StringComparison.Ordinal);
    }

    // ── /eventlog (phase 4) ─────────────────────────────────────────────────

    [Fact]
    public void Command_ReadsTheLogAndEveryTerm_TheRestIsText()
    {
        var (args, error) = MacLogCommand.Parse("crashes level:fault,error process:Widget subsystem:com.x category:Cat pid:42 exclude:a,b since:2h until:30m frozen app", ManualTimeProvider.DefaultUtcNow, _time.LocalTimeZone);

        Assert.Null(error);
        Assert.Equal("crashes", args!.Log);
        Assert.True(args.LogGiven);
        Assert.Equal("fault,error", args.Levels);
        Assert.Equal("Widget", args.Process);
        Assert.Equal(42, args.Pid);
        Assert.Equal(["a", "b"], args.Exclude);
        Assert.Equal(ManualTimeProvider.DefaultUtcNow.AddHours(-2), args.Since);
        Assert.Equal(ManualTimeProvider.DefaultUtcNow.AddMinutes(-30), args.Until);
        Assert.Equal("frozen app", args.Text);
        Assert.Equal(MacLogCommand.DefaultLog, MacLogCommand.Parse("", ManualTimeProvider.DefaultUtcNow, _time.LocalTimeZone).Args!.Log);
    }

    [Theory]
    [InlineData("system level:loud", "Error: level 'loud' is not one of fault, error, default, info, debug or all.")]
    [InlineData("system pid:x", "Error: pid 'x' is not a process id.")]
    [InlineData("system since:soon", "Error: since 'soon' is not an age (30m, 2h, 1d, 1w) or a moment (2026-10-14T21:00).")]
    public void Command_RefusesABadTerm(string line, string expected) =>
        Assert.Equal(expected, MacLogCommand.Parse(line, ManualTimeProvider.DefaultUtcNow, _time.LocalTimeZone).Error);

    [Fact]
    public void Command_Completes_TheLogsArchivesAndKeys()
    {
        var first = MacLogCommand.Complete("", ["my logs/a.logarchive"]).Select(i => i.Text).ToList();
        var levels = MacLogCommand.Complete("system level:f", []).Select(i => i.Text).ToList();

        Assert.Equal(["system", "crashes", "install", "\"my logs/a.logarchive\""], first.Take(4));
        Assert.Contains("process:", first);
        Assert.Equal(["system level:fault"], levels);
    }

    [Fact]
    public async Task Command_PrintsEachLog_InFull_WithTheTermsALogLacksNoted()
    {
        _log.Entries.Add(FakeUnifiedLog.Entry(ManualTimeProvider.DefaultUtcNow.AddMinutes(-2), message: "login failed Password=hunter2;"));
        var access = Access();

        var system = await MacLogCommand.RunAsync(access, "", 20, 20_000, CancellationToken.None);
        var crashes = await MacLogCommand.RunAsync(access, "crashes process:widget level:error", 20, 20_000, CancellationToken.None);
        var install = await MacLogCommand.RunAsync(access, "install Products", 20, 20_000, CancellationToken.None);
        var none = await MacLogCommand.RunAsync(access, "install since:10m", 20, 20_000, CancellationToken.None);
        var bad = await MacLogCommand.RunAsync(access, "x.evtx", 20, 20_000, CancellationToken.None);
        var away = await MacLogCommand.RunAsync(new MacLogAccess(null, () => false, new WorkingDirectory(() => _root, _time), _time, _places), "", 20, 20_000, CancellationToken.None);

        Assert.False(system.Failed);
        Assert.Equal("system on this Mac: 1 entry, newest first", system.Lines[0]);
        Assert.EndsWith("  error  loginwindow[395]  com.apple.loginwindow.logging/TAL  login failed Password=hunter2;", system.Lines[1], StringComparison.Ordinal);
        Assert.Equal(["crashes on this Mac: 1 report, newest first", MacLogText.NotApplied("crashes", ["level"])], crashes.Lines.Take(2));
        Assert.StartsWith("Widget-2026-09-11-150000.ips  ", crashes.Lines[2], StringComparison.Ordinal);
        Assert.Contains("  crash  Widget[4242]  EXC_CRASH (SIGABRT)", crashes.Lines[2], StringComparison.Ordinal);
        Assert.Equal("install on this Mac: 1 entry, newest first", install.Lines[0]);
        Assert.Contains("  softwareupdated[534]  Products found:", install.Lines[1], StringComparison.Ordinal);
        Assert.StartsWith("no install.log entries since ", none.Lines[0], StringComparison.Ordinal);
        Assert.True(bad.Failed);
        Assert.Equal([MacLogText.EvtxIsWindows], bad.Lines);
        Assert.Equal([EventLogText.NoneHere], away.Lines);
    }

    [Fact]
    public async Task Reads_AnEntrysPage_ForEachLog_AndNullWhenGone()
    {
        _log.Entries.Add(FakeUnifiedLog.Entry(ManualTimeProvider.DefaultUtcNow.AddMinutes(-2), message: "line one\nline two"));
        var access = Access();
        var system = access.Resolve("system", false).Source! with { Redacted = false };
        var crashes = access.Resolve("crashes", false).Source! with { Redacted = false };
        var install = access.Resolve("install", false).Source! with { Redacted = false };
        var unifiedRow = (await MacLogReads.SearchAsync(access, system, new MacLogQuery("system", [], ManualTimeProvider.DefaultUtcNow.AddHours(-1)), 20_000, null, CancellationToken.None)).Value!.Rows.Single();
        var installRow = (await MacLogReads.SearchAsync(access, install, new MacLogQuery("install", [], ManualTimeProvider.DefaultUtcNow.AddHours(-1)), 20_000, null, CancellationToken.None)).Value!.Rows.Single();

        string? unified = (await MacLogReads.PageAsync(access, system, unifiedRow.Handle, CancellationToken.None)).Value;
        string? crash = (await MacLogReads.PageAsync(access, crashes, "Widget-2026-09-11-150000.ips", CancellationToken.None)).Value;
        string? entry = (await MacLogReads.PageAsync(access, install, installRow.Handle, CancellationToken.None)).Value;
        string? gone = (await MacLogReads.PageAsync(access, crashes, "Gone-2026-09-11-000000.ips", CancellationToken.None)).Value;

        Assert.Equal("system entry " + unifiedRow.Handle + " on this Mac\ntime:       2026-09-11 14:03:30 -07:00\nlevel:      error\nprocess:    loginwindow   pid 395   thread 7\n"
            + "subsystem:  com.apple.loginwindow.logging\ncategory:   TAL\n\nline one\nline two", unified);
        Assert.StartsWith("crashes report Widget-2026-09-11-150000.ips on this Mac\ntime:       2026-09-11 13:00:00 -07:00\nkind:       crash\nprocess:    Widget   pid 4242\nfolder:     user   ", crash, StringComparison.Ordinal);
        Assert.Contains("password=hunter2", crash, StringComparison.Ordinal);   // the user's own page: full text
        Assert.Equal("install entry " + installRow.Handle + " on this Mac\ntime:       2026-09-11 13:30:00 -07:00\nprocess:    softwareupdated   pid 534\n\nSUOSUServiceDaemon: idle", entry);
        Assert.Null(gone);
    }

    // ── --eventlog-check local and the smoke's probes (phase 5) ─────────────

    [Fact]
    public void Check_Local_PassesEveryLine_OverTheFakeAndTheFixtures()
    {
        _log.Entries.Add(FakeUnifiedLog.Entry(ManualTimeProvider.DefaultUtcNow.AddMinutes(-1)));
        var console = new Spectre.Console.Testing.TestConsole { EmitAnsiSequences = false };
        console.Profile.Width = 200;

        int exit = EventLogCheck.RunMac(console, _log, _places, "local", _time);

        Assert.Equal(0, exit);
        foreach (string check in new[] { "eventlog:store", "eventlog:read", "eventlog:predicates", "eventlog:stderr", "eventlog:crashes-user", "eventlog:crashes-system", "eventlog:crash-report", "eventlog:install", "eventlog:archive-error" })
        {
            Assert.Contains("PASS  " + check + "  ", console.Output, StringComparison.Ordinal);
        }

        Assert.Contains("eventlog:archive-error  refused (NSCocoaErrorDomain 260): '", console.Output, StringComparison.Ordinal);
        Assert.Contains("EVENT LOG CHECK PASS  9 checks", console.Output, StringComparison.Ordinal);
        var predicates = _log.Opens.Single(o => o.Filter.Process == "neonsidekick-smoke");
        Assert.Equal(MacLogPredicate.Placeholders(predicates.Format), predicates.Arguments.Count);
        Assert.Contains("NOT (process IN %@)", predicates.Format, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_AnotherName_OrARefusedStore_Fails()
    {
        var other = new Spectre.Console.Testing.TestConsole { EmitAnsiSequences = false };
        other.Profile.Width = 200;
        var refused = new Spectre.Console.Testing.TestConsole { EmitAnsiSequences = false };
        refused.Profile.Width = 200;
        _log.Fail = new UnifiedLogException("OSLogErrorDomain", 9, "Operation not permitted");

        Assert.Equal(1, EventLogCheck.RunMac(other, _log, _places, "dc01", _time));
        Assert.Equal(1, EventLogCheck.RunMac(refused, _log, _places, "local", _time));
        Assert.Contains(MacLogText.OnlyLocal("dc01"), other.Output, StringComparison.Ordinal);
        Assert.Contains("FAIL  eventlog:store  " + MacLogText.NeedsAdmin, refused.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("eventlog:read", refused.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Probes_AMissingInstallLog_Fails()
    {
        File.Delete(_places.InstallLog);

        var check = MacLogProbes.Install(_places, _time);

        Assert.False(check.Passed);
        Assert.StartsWith(_places.InstallLog + ": ", check.Detail, StringComparison.Ordinal);
    }
}
