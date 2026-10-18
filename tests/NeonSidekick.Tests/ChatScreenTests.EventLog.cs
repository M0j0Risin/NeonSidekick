using NeonSidekick.App;
using NeonSidekick.EventLog;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The event logs on the screen (2026-10-15, the event-log plan): <c>/eventlog</c>'s pane — its tabs, rows, buttons and the event's page
/// with copy and ask —, its refusals (no reader, nothing ticked), what it draws of hostile text, the tools' transcript note, and the
/// switch page's offered button.
/// </summary>
public partial class ChatScreenTests
{
    /// <summary>The fake event logs the chat screen gets; null (the default) is a screen with no event-log reader.</summary>
    private FakeEventLogSystem? _eventLogSystem;

    /// <summary>The fake unified log the chat screen gets (2026-10-18, the Mac event-log plan); null (the default) is a screen with none.</summary>
    private FakeUnifiedLog? _unifiedLog;

    /// <summary>Where the screen's Mac logs that are files are (2026-10-18); null is this account's real places, which no test should read.</summary>
    private MacLogPlaces? _macLogPlaces;

    /// <summary>
    /// A Mac's logs for the screen (2026-10-18, the Mac event-log plan's phase 4): the fake unified log with one error, and a temporary home
    /// holding the Widget crash and the fixture install.log.
    /// </summary>
    private void MacLogFixture(bool pane = true)
    {
        _unifiedLog = new FakeUnifiedLog();
        _unifiedLog.Entries.Add(FakeUnifiedLog.Entry(EventNow.AddMinutes(-2), message: "applicationQuit: for app:Single Sign-On"));
        string reports = Path.Combine(_dir, "maclog", "reports");
        Directory.CreateDirectory(reports);
        string fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "maclog");
        File.Copy(Path.Combine(fixtures, "Widget-2026-09-11-150000.ips"), Path.Combine(reports, "Widget-2026-09-11-150000.ips"));
        File.Copy(Path.Combine(fixtures, "install.log"), Path.Combine(_dir, "maclog", "install.log"));
        _macLogPlaces = new MacLogPlaces([new MacCrashFolder(reports, "user")], Path.Combine(_dir, "maclog", "install.log"), "/Users/tester");
        _settings.Update(d => d.TtsOutput = false);
        _console.Profile.Height = 40;
        _console.Profile.Width = 160;
        if (pane)
        {
            _geometry = new ScreenGeometry(() => null);
        }
    }

    [Fact]
    public async Task MacLog_WithoutThePane_PrintsTheLines()
    {
        MacLogFixture(pane: false);
        StepsWhenIdle(Line("/eventlog crashes"), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains("crashes on this Mac: 1 report, newest first", output);
        Assert.Contains("Widget-2026-09-11-150000.ips  2026-09-11 13:00:00 -07:00  crash  Widget[4242]  EXC_CRASH (SIGABRT)", output);
        Assert.DoesNotContain(MacLogMenu.Title, output);
    }

    [Fact]
    public async Task MacLog_ThePane_ShowsThreeLogsAsTabs_AndEnterOpensTheEntrysPage()
    {
        MacLogFixture();
        StepsWhenIdle(Line("/eventlog"), Key(Keys.Enter), Key(Keys.Escape), Key(Keys.Escape), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(MacLogMenu.Title, output);
        Assert.Contains("crashes", output);
        Assert.Contains("install", output);
        Assert.Contains("▸ 14:03:30 ● " + "loginwindow[395]".PadRight(MacLogMenu.ProcessCells) + " applicationQuit: for app:Single Sign-On", output);
        Assert.Contains("Friday 11 September 2026", output);
        Assert.Contains("system entry u", output);
        Assert.Contains("subsystem:  com.apple.loginwindow.logging", output);
        Assert.Empty(_chat.Requests);
    }

    [Fact]
    public async Task MacLog_ACrash_OpensOnItsTab_AndAskDraftsTheQuestion_WhichSends()
    {
        MacLogFixture();
        _settings.Update(d => d.EventLogTools = true);
        _chat.EnqueueText("Widget aborted.");
        StepsWhenIdle(Line("/eventlog crashes"), Key(Keys.Enter), Key(Keys.Char('a')), Key(Keys.Enter), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains("▸ 13:00:00 ● Widget[4242]", output);
        Assert.Contains("crashes report Widget-2026-09-11-150000.ips on this Mac", output);
        Assert.Single(_chat.Requests);
        Assert.Equal(MacLogMenu.AskDraft("crashes", "Widget-2026-09-11-150000.ips"), UserText(_chat.Requests[0]));
    }

    [Fact]
    public async Task MacLog_Copy_CopiesTheInstallEntry()
    {
        MacLogFixture();
        StepsWhenIdle(Line("/eventlog install"), Key(Keys.Enter), Key(Keys.Char('c')), Key(Keys.Escape), Key(Keys.Escape), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains("▸ 13:30:00 · softwareupdated[534]", output);
        Assert.Contains(_copied, c => c.StartsWith("install entry i", StringComparison.Ordinal) && c.EndsWith("SUOSUServiceDaemon: idle", StringComparison.Ordinal));
        Assert.Contains("(" + EventLogText.Glyph + " copied i", output);
    }

    [Fact]
    public async Task MacLog_ABadTerm_IsAnError_AndThePaneNeverOpens()
    {
        MacLogFixture();
        StepsWhenIdle(Line("/eventlog system level:loud"), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(MacLogText.BadLevel("loud"), output);
        Assert.DoesNotContain(MacLogMenu.Title, output);
    }


    private static readonly DateTimeOffset EventNow = ManualTimeProvider.DefaultUtcNow;

    /// <summary>A reader whose System log holds three Service Control Manager errors (records 100, 99, 98) and Application one warning.</summary>
    private FakeEventLogSystem EventLogFixture()
    {
        _eventLogSystem = new FakeEventLogSystem();
        for (int i = 0; i < 3; i++)
        {
            _eventLogSystem.Add("System", FakeEventLogSystem.Xml("Service Control Manager", 7000, 2, 100 - i, EventNow.AddMinutes(-10 * i), data: ("param1", "Svc" + i)), "The %1 service failed to start.");
        }

        _eventLogSystem.Add("Application", FakeEventLogSystem.Xml("Application Error", 1000, 3, 7, EventNow, "Application", data: ("app", "game.exe")), "Faulting application name: %1");
        _eventLogSystem.Denied.Add("Security");
        _settings.Update(d => d.TtsOutput = false);
        _console.Profile.Height = 40;
        _console.Profile.Width = 160;
        _geometry = new ScreenGeometry(() => null);
        return _eventLogSystem;
    }

    [WindowsFact]
    public async Task EventLog_ThePane_ListsTheLogsAsTabs_TheEventsNewestFirst_AndEnterOpensTheEventsPage()
    {
        EventLogFixture();
        StepsWhenIdle(Line("/eventlog"), Key(Keys.Enter), Key(Keys.Escape), Key(Keys.Escape), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(EventLogMenu.TitleFor("local"), output);
        Assert.Contains("Application", output);
        Assert.Contains("Security (no access)", output);   // the fake holds no Security log: refused, so marked
        Assert.Contains("▸ 14:05:30 ● Service Control Manager       7000  The Svc0 service failed to start.", output);
        Assert.Contains("Friday 11 September 2026", output);   // the day's line
        Assert.Contains("System record 100 on local", output);   // the event's page
        Assert.Contains("param1: Svc0", output);
        Assert.Empty(_chat.Requests);
    }

    [WindowsFact]
    public async Task EventLog_Ask_DraftsTheQuestion_WhichSends()
    {
        EventLogFixture();
        _settings.Update(d => d.EventLogTools = true);
        _chat.EnqueueText("It failed to start because…");
        StepsWhenIdle(Line("/eventlog"), Key(Keys.Enter), Key(Keys.Char('a')), Key(Keys.Enter), Line("/exit"));

        await RunAsync();

        Assert.Single(_chat.Requests);
        Assert.Equal("Explain event System record 100 on local", UserText(_chat.Requests[0]));
    }

    [WindowsFact]
    public async Task EventLog_Ask_WithTheToolsOff_SaysSo_AndDraftsNothing()
    {
        EventLogFixture();
        StepsWhenIdle(Line("/eventlog"), Key(Keys.Enter), Key(Keys.Char('a')), Key(Keys.Escape), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(EventLogMenu.AskOffNotice, output);
        Assert.Empty(_chat.Requests);
    }

    [WindowsFact]
    public async Task EventLog_Copy_CopiesThePage_AndShowsItAgain()
    {
        EventLogFixture();
        StepsWhenIdle(Line("/eventlog"), Key(Keys.Enter), Key(Keys.Char('c')), Key(Keys.Escape), Key(Keys.Escape), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(EventLogMenu.CopiedNotice(100), output);
        Assert.Contains(_copied, c => c.StartsWith("System record 100 on local", StringComparison.Ordinal));
    }

    [WindowsFact]
    public async Task EventLog_HostileText_IsDrawnAsText()
    {
        EventLogFixture();
        _eventLogSystem!.Logs["System"].Insert(0, new FakeEventLogSystem.FakeEvent(FakeEventLogSystem.Xml("[red]x[/]", 1, 2, 200, EventNow.AddSeconds(1)), "[b]bold[/b] \u001b[31mred"));
        StepsWhenIdle(Line("/eventlog"), Key(Keys.Escape), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains("[red]x[/]", output);
        Assert.Contains("[b]bold[/b] �[31mred", output);
        Assert.DoesNotContain("\u001b[31mred", output, StringComparison.Ordinal);   // ordinal: a culture-aware compare ignores the control character
    }

    /// <summary>
    /// A screen given a unified-log reader (2026-10-18, the Mac event-log plan; any OS, the fake standing in): the switch offers the four
    /// tools, the rules carry this machine's event-log sentence, a model's call reads the log redacted, and the transcript keeps the note.
    /// </summary>
    [Fact]
    public async Task MacLog_WithAReader_TheGroupIsOffered_AndAModelsCallReadsTheLog()
    {
        _unifiedLog = new FakeUnifiedLog();
        _unifiedLog.Entries.Add(FakeUnifiedLog.Entry(EventNow.AddMinutes(-2), message: "login failed Password=hunter2;"));
        _settings.Update(d => { d.TtsOutput = false; d.EventLogTools = true; });
        _chat.Enqueue(FakeChatClient.Call("c0", EventLogSearchTool.ToolName, new Dictionary<string, object?> { ["log"] = "system" }));
        _chat.EnqueueText("One login failure.");
        StepsWhenIdle(Line("what went wrong?"), Line("/exit"));

        await RunAsync();

        var offered = _chat.Options[0]!.Tools!.Select(t => t.Name).ToList();
        Assert.All(ChatScreen.EventLogToolNames, name => Assert.Contains(name, offered));
        Assert.Contains(Llm.Assistant.EventLogRuleHere, _chat.Requests[0][0].Text, StringComparison.Ordinal);
        string result = _chat.Requests[1].SelectMany(m => m.Contents).OfType<Microsoft.Extensions.AI.FunctionResultContent>().Single(r => r.CallId == "c0").Result!.ToString()!;
        Assert.StartsWith("system on this Mac: 1 entry, newest first", result, StringComparison.Ordinal);
        Assert.Contains("Password=<redacted>;", result, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MacLog_WithTheSwitchOff_TheToolsAreNotOffered()
    {
        _unifiedLog = new FakeUnifiedLog();
        _settings.Update(d => { d.TtsOutput = false; d.EventLogTools = false; });
        _chat.EnqueueText("Hello.");
        StepsWhenIdle(Line("hi"), Line("/exit"));

        await RunAsync();

        var offered = _chat.Options[0]?.Tools?.Select(t => t.Name).ToList() ?? [];
        Assert.DoesNotContain(EventLogSearchTool.ToolName, offered);
        Assert.DoesNotContain(Llm.Assistant.EventLogRuleHere, _chat.Requests[0][0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EventLog_WithNoReader_SaysThereAreNone()
    {
        _settings.Update(d => d.TtsOutput = false);
        StepsWhenIdle(Line("/eventlog"), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(EventLogText.NoneHere, output);
    }

    [WindowsFact]
    public async Task EventLog_NothingTicked_SaysSo_AndAnUntickedComputerIsRefused()
    {
        EventLogFixture();
        _settings.Update(d => d.EventLogComputersOffered = []);
        StepsWhenIdle(Line("/eventlog"), Line("/eventlog sys computer:local"), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains(EventLogText.NoComputerTicked, output);
        Assert.Contains(EventLogText.UnknownComputer("local", ""), output);
    }

    [WindowsFact]
    public async Task EventLog_ToolResult_IsATranscriptNote()
    {
        EventLogFixture();
        _settings.Update(d => d.EventLogTools = true);
        _chat.Enqueue(FakeChatClient.Call("e1", EventLogSearchTool.ToolName, new Dictionary<string, object?> { ["log"] = "System" }));
        _chat.EnqueueText("Three services failed.");
        StepsWhenIdle(Line("what failed?"), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains("(" + EventLogText.Glyph + " System on local: 3 events, newest first", output);
    }

    [WindowsFact]
    public async Task ToolsEventLog_TheOfferedButton_OpensTheComputersChecklist_LocalFirst()
    {
        EventLogFixture();
        StepsWhenIdle(Line("/tools eventlog"), Key(Keys.Char('o')), Key(Keys.Escape), Key(Keys.Escape), Line("/exit"));

        string output = await RunAsync();

        Assert.Contains("Event log computers offered", output);
        Assert.Contains("[x] local", output);
    }
}
