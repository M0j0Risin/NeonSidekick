using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using NeonSidekick.Shell;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>The registry and the <c>process</c> tool over real <c>cmd.exe</c> children (2026-09-21): every action's header, the cursor, the log window and its eviction note, the caps, the alerts and the notes, the pinned refusals.</summary>
public sealed class ProcessToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly AppSettingsData _settings = new();
    private readonly Interpreters _interpreters = new(_ => null);
    private readonly ShellRunner _runner;
    private readonly ProcessRegistry _registry;
    private readonly ProcessTool _tool;
    private readonly CommandGate _gate;
    private readonly Files.WorkingDirectory _files;
    private int _signals;

    public ProcessToolTests()
    {
        Directory.CreateDirectory(_dir);
        _gate = new CommandGate(() => _settings, new CommandAllowList(() => [], _ => { }), null);
        _runner = new ShellRunner(_time);
        _registry = new ProcessRegistry(_runner, new Random(7), () => Interlocked.Increment(ref _signals));
        _files = new Files.WorkingDirectory(() => _dir, _time);
        _tool = new ProcessTool(_registry, _files, () => _settings, _gate);
    }

    public void Dispose()
    {
        _registry.Dispose();
        GitAccessTests.DeleteTree(_dir);
    }

    private ProcessSession Start(string command, bool notify = false) => _registry.Start(ShellCommandLine.For(ShellKind.Cmd, command, _interpreters.Locate(ShellKind.Cmd)!, _dir), notify);

    private static AIFunctionArguments Args(params (string Name, object? Value)[] pairs) => new(pairs.ToDictionary(p => p.Name, p => p.Value));

    private async Task<string> Invoke(params (string Name, object? Value)[] pairs) => (string)(await _tool.InvokeAsync(Args(pairs)))!;

    private static async Task Exit(ProcessSession session)
    {
        await session.Exited.WaitAsync(TimeSpan.FromSeconds(60));
        await Task.Delay(50);   // the registry's watcher runs after the exit
    }

    [Fact]
    public void Name_Schema_AndDescription_ArePinned()
    {
        Assert.Equal("process", _tool.Name);
        Assert.Equal("The background processes run_command started: list them, poll or wait for one, read its log, send it input, kill it or forget it. poll returns only what arrived since your last poll; log a numbered window of its lines.", _tool.Description);
        var schema = _tool.JsonSchema;
        Assert.Equal(["action", "session_id", "data", "timeout", "offset", "limit"], schema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        Assert.Equal(["action"], schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["list", "poll", "log", "wait", "kill", "write", "submit", "close"], schema.GetProperty("properties").GetProperty("action").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["list", "poll", "log", "wait", "kill", "write", "submit", "close"], ProcessTool.Actions);
        Assert.Equal("For wait: seconds to block, 1 to 600 (default 60); what arrived so far comes back on a timeout.", schema.GetProperty("properties").GetProperty("timeout").GetProperty("description").GetString());
        Assert.Equal("For log: how many lines, 1 to 2000 (default 200).", schema.GetProperty("properties").GetProperty("limit").GetProperty("description").GetString());
        foreach (var property in schema.GetProperty("properties").EnumerateObject())
        {
            Assert.False(string.IsNullOrWhiteSpace(property.Value.GetProperty("description").GetString()), property.Name);
        }

        Assert.Equal(new Dictionary<string, object?> { ["action"] = "poll", ["session_id"] = "proc_1" }, ProcessTool.PollArguments("proc_1"));
    }

    [WindowsFact]
    public async Task List_Poll_AndClose_FollowAChild()
    {
        Assert.Equal("0 processes", await Invoke(("action", "list")));
        var session = Start("echo one & echo two");
        Assert.Matches("^proc_[0-9a-f]{6}$", session.Id);
        await Exit(session);

        string list = await Invoke(("action", "LIST"));
        Assert.Equal("1 process (0 running)\n" + session.Id + "  exit 0    0.0 s     cmd        echo one & echo two", list);

        string poll = await Invoke(("action", "poll"), ("session_id", session.Id[..7]));   // a unique prefix
        Assert.Equal(session.Id + " exited 0 after 0.0 s (cmd): echo one & echo two — 2 new lines\none \ntwo", poll);
        Assert.Equal(session.Id + " exited 0 after 0.0 s (cmd): echo one & echo two — no new output\n(no output)", await Invoke(("action", "poll"), ("session_id", session.Id)));

        Assert.Equal("closed " + session.Id + " (exit 0, 2 lines forgotten)", await Invoke(("action", "close"), ("session_id", session.Id)));
        Assert.Equal("Error: no process matches '" + session.Id + "'", await Invoke(("action", "poll"), ("session_id", session.Id)));
        Assert.Empty(_registry.List());
    }

    [WindowsFact]
    public async Task Log_IsANumberedWindow_AndSaysWhenTheOldestAreGone()
    {
        var session = Start("for /l %i in (1,1,6000) do @echo line %i");
        await Exit(session);

        string tail = await Invoke(("action", "log"), ("session_id", session.Id), ("limit", 2));
        Assert.Equal(session.Id + " lines 5,999-6,000 of 6,000 (exit 0): for /l %i in (1,1,6000) do @echo line %i\n(lines 1-1,000 are gone: the log keeps the last 5,000)\nline 5999\nline 6000", tail);
        string window = await Invoke(("action", "log"), ("session_id", session.Id), ("offset", 1001), ("limit", 3));
        Assert.Equal(session.Id + " lines 1,001-1,003 of 6,000 (exit 0): for /l %i in (1,1,6000) do @echo line %i\nline 1001\nline 1002\nline 1003", window);
        string early = await Invoke(("action", "log"), ("session_id", session.Id), ("offset", 5), ("limit", 1));
        Assert.StartsWith(session.Id + " lines 1,001-1,001 of 6,000 (exit 0): for /l %i in (1,1,6000) do @echo line %i\n(lines 1-1,000 are gone: the log keeps the last 5,000)\nline 1001", early);
        string past = await Invoke(("action", "log"), ("session_id", session.Id), ("offset", 9000));
        Assert.Equal(session.Id + " no lines (exit 0): for /l %i in (1,1,6000) do @echo line %i\n(no output)", past);
        Assert.Equal("Error: limit must be 1 to 2000", await Invoke(("action", "log"), ("session_id", session.Id), ("limit", 0)));
        Assert.Equal("Error: offset must be 1 or more", await Invoke(("action", "log"), ("session_id", session.Id), ("offset", 0)));
    }

    [WindowsFact]
    public async Task Poll_OverTheCap_KeepsHeadAndTail_WithoutASpill()
    {
        _settings.ShellOutputMaxChars = 2000;
        var session = Start("for /l %i in (1,1,400) do @echo line %i");
        await Exit(session);

        string poll = await Invoke(("action", "poll"), ("session_id", session.Id));
        Assert.StartsWith(session.Id + " exited 0 after 0.0 s (cmd): for /l %i in (1,1,400) do @echo line %i — 400 new lines — output cut\nline 1\n", poll);
        Assert.Contains("\n… (", poll);
        Assert.Contains(" characters cut) …\n", poll);
        Assert.EndsWith("\nline 400", poll);
        Assert.False(Directory.Exists(Path.Combine(_dir, ".shell")));
    }

    [WindowsFact]
    public async Task Wait_Kill_Write_AndSubmit_OverARunningChild()
    {
        // A write and a submit split one line, so the child must read a whole line (2026-09-22, the v0.3.2 release run): cmd's set /p takes
        // whatever one ReadFile returns, and on the runner it was already blocked when "wor" was flushed and echoed "hello wor".
        const string ReadLine = "powershell -NoProfile -Command \"'hello ' + [Console]::In.ReadLine()\"";
        var session = Start(ReadLine);
        Assert.Equal("sent 3 characters to " + session.Id, await Invoke(("action", "write"), ("session_id", session.Id), ("data", "wor")));
        Assert.Equal("sent a line to " + session.Id, await Invoke(("action", "submit"), ("session_id", session.Id), ("data", "ld")));
        string waited = await Invoke(("action", "wait"), ("session_id", session.Id), ("timeout", 30));
        Assert.Equal(session.Id + " exited 0 after 0.0 s (cmd): " + ReadLine + " — 1 new line\nhello world", waited);
        Assert.Equal("Error: " + session.Id + " has exited; kill needs a running process", await Invoke(("action", "kill"), ("session_id", session.Id)));
        Assert.Equal("Error: " + session.Id + " has exited; write needs a running process", await Invoke(("action", "write"), ("session_id", session.Id), ("data", "x")));

        // The police reads what goes to stdin (Shell police, 2026-09-22): a line naming an outside path is refused and nothing is sent; off, it goes.
        var typed = Start("set /p name=&& call echo hello %name%");
        Assert.Equal(@"Error: outside the working directory: 'C:\' — a command or a script may only name paths under it", await Invoke(("action", "submit"), ("session_id", typed.Id), ("data", @"cd C:\")));
        Assert.Equal("Error: outside the working directory: '..' — a command or a script may only name paths under it", await Invoke(("action", "write"), ("session_id", typed.Id), ("data", "cd ..")));   // relative to where it started: the root
        Assert.Equal("sent a line to " + typed.Id, await Invoke(("action", "submit"), ("session_id", typed.Id), ("data", "sub")));
        Assert.Equal(typed.Id + " exited 0 after 0.0 s (cmd): set /p name=&& call echo hello %name% — 1 new line\nhello sub", await Invoke(("action", "wait"), ("session_id", typed.Id), ("timeout", 30)));
        _settings.ShellPolice = false;
        var loose = Start("set /p name=&& call echo hello %name%");
        Assert.Equal("sent a line to " + loose.Id, await Invoke(("action", "submit"), ("session_id", loose.Id), ("data", @"C:\")));
        Assert.Equal(loose.Id + " exited 0 after 0.0 s (cmd): set /p name=&& call echo hello %name% — 1 new line\nhello C:\\", await Invoke(("action", "wait"), ("session_id", loose.Id), ("timeout", 30)));
        _settings.ShellPolice = true;
        Assert.Equal([@"cd C:\", "cd .."], _gate.Refusals);   // the two policed writes, noted on the gate (2026-09-26)

        // A forbidden string in what goes to stdin (2026-10-03): refused, nothing sent, noted on the gate; the user's line names it.
        _settings.ShellPoliceForbiddenStrings = ["shutdown"];
        var guarded = Start("set /p name=&& call echo hello %name%");
        var shown = Assert.IsType<ToolShownResult>(await _tool.InvokeAsync(Args(("action", "submit"), ("session_id", guarded.Id), ("data", "SHUTDOWN /s"))));
        Assert.Equal(ShellText.Forbidden, shown.Text);
        Assert.Equal(ShellText.ForbiddenShown("shutdown"), shown.Shown);

        // The SQLite police (2026-10-05): what goes to stdin reaching SQLite is refused while the SQLite tools are on, nothing sent.
        _files.Databases = () => Files.DatabaseGuard.ByExtension;
        var policed = Assert.IsType<ToolShownResult>(await _tool.InvokeAsync(Args(("action", "submit"), ("session_id", guarded.Id), ("data", "sqlite3 shop.db"))));
        Assert.Equal(ShellText.SqlitePoliced, policed.Text);
        Assert.Equal("SQLite: 'sqlite3' — not run", policed.Shown);
        _files.Databases = null;

        // The server-database police (2026-10-05): stdin that reaches a family whose tools are on.
        _files.ServerDatabases = () => new Shell.ServerDatabaseGuard([new("PostgreSQL", "postgres_query", "postgres_execute", Shell.ServerDatabasePolice.PostgresWords, ["pg01"])]);
        policed = Assert.IsType<ToolShownResult>(await _tool.InvokeAsync(Args(("action", "submit"), ("session_id", guarded.Id), ("data", "psql -h pg01"))));
        Assert.StartsWith(ShellText.ServerDatabaseHead, policed.Text);
        Assert.Equal("PostgreSQL: 'psql' — not run", policed.Shown);
        _files.ServerDatabases = null;

        Assert.Equal("sent a line to " + guarded.Id, await Invoke(("action", "submit"), ("session_id", guarded.Id), ("data", "sub")));
        Assert.Equal(guarded.Id + " exited 0 after 0.0 s (cmd): set /p name=&& call echo hello %name% — 1 new line\nhello sub", await Invoke(("action", "wait"), ("session_id", guarded.Id), ("timeout", 30)));
        Assert.Equal([@"cd C:\", "cd ..", "SHUTDOWN /s", "sqlite3 shop.db", "psql -h pg01"], _gate.Refusals);
        _settings.ShellPoliceForbiddenStrings = [];

        var sleeper = Start("ping -n 30 127.0.0.1 >nul");
        var wait = _tool.InvokeAsync(Args(("action", "wait"), ("session_id", sleeper.Id), ("timeout", 5)));
        await Task.Delay(100);
        _time.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(sleeper.Id + " still running after 5 s (cmd): ping -n 30 127.0.0.1 >nul — no new output\n(no output)", (string)(await wait.AsTask().WaitAsync(TimeSpan.FromSeconds(30)))!);
        Assert.Equal("Error: " + sleeper.Id + " is still running; kill it first", await Invoke(("action", "close"), ("session_id", sleeper.Id)));
        Assert.Equal("Error: data is required for write and submit", await Invoke(("action", "write"), ("session_id", sleeper.Id)));
        Assert.Equal("Error: timeout must be 1 to 600 for wait", await Invoke(("action", "wait"), ("session_id", sleeper.Id), ("timeout", 601)));
        string killed = await Invoke(("action", "kill"), ("session_id", sleeper.Id));
        Assert.Matches("^killed " + sleeper.Id + " \\(cmd, pid [0-9]+\\) after 6\\.0 s: ping -n 30 127\\.0\\.0\\.1 >nul$", killed);
        Assert.True(sleeper.Killed);
        Assert.Contains("5 processes (0 running)", await Invoke(("action", "list")));   // the two typed-at ones above too (2026-09-22), and the forbidden string's (2026-10-03)
    }

    [WindowsFact]
    public async Task Refusals_ArePinned()
    {
        Assert.Equal("Error: 'dance' is not one of list, poll, log, wait, kill, write, submit, close for 'action'", await Invoke(("action", "dance")));
        Assert.Equal("Error: session_id is required for every action but list", await Invoke(("action", "poll")));
        Assert.Equal("Error: no process matches 'proc_9'", await Invoke(("action", "poll"), ("session_id", "proc_9")));
        var a = Start("echo a");
        var b = Start("echo b");
        await Exit(a);
        await Exit(b);
        Assert.Equal("Error: 'proc_' matches 2 processes: " + a.Id + ", " + b.Id, await Invoke(("action", "poll"), ("session_id", "proc_")));
    }

    [WindowsFact]
    public async Task Notify_QueuesAnAlertAndANote_OncePerExit_AndTheCapsHold()
    {
        var quiet = Start("echo q");
        var loud = Start("echo l & exit /b 3", notify: true);
        await Exit(quiet);
        await Exit(loud);

        Assert.True(_registry.HasAlerts);
        Assert.True(_registry.TryTakeAlert(out var alert));
        Assert.Equal(loud.Id + " exited 3 after 0.0 s: echo l & exit /b 3", ShellText.AlertLine(alert));
        Assert.False(_registry.TryTakeAlert(out _));
        Assert.Equal(1, _signals);
        Assert.Equal([loud.Id], _registry.TakeNotes());
        Assert.Empty(_registry.TakeNotes());

        var killed = Start("ping -n 30 127.0.0.1 >nul", notify: true);
        killed.Kill();
        await Exit(killed);
        Assert.True(_registry.TryTakeAlert(out var killedAlert));
        Assert.Equal(killed.Id + " was killed after 0.0 s: ping -n 30 127.0.0.1 >nul", ShellText.AlertLine(killedAlert));
        Assert.Equal([killed.Id], _registry.TakeNotes());

        // Closed before the note was taken: the note goes with it.
        var closed = Start("echo c", notify: true);
        await Exit(closed);
        Assert.True(_registry.Close(closed));
        Assert.Empty(_registry.TakeNotes());
        Assert.True(_registry.TryTakeAlert(out _));   // the alert was already queued at the exit

        Assert.Equal(16, ProcessRegistry.MaxRunning);
        Assert.Equal(64, ProcessRegistry.MaxKept);
        Assert.Equal(5, ProcessRegistry.MaxNotesPerTurn);
    }

    [WindowsFact]
    public async Task Eviction_KeepsTheNewestFinished()
    {
        var first = Start("echo first");
        await Exit(first);
        for (int i = 0; i < ProcessRegistry.MaxKept; i++)
        {
            await Exit(Start("echo " + i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        Assert.Equal(ProcessRegistry.MaxKept, _registry.List().Count);
        Assert.DoesNotContain(first, _registry.List());
        Assert.Equal(FindOutcome.None, _registry.Find(first.Id, out _, out _));
    }

    [WindowsFact]
    public async Task TooManyRunning_IsRefused_AndDisposeKillsThemAll()
    {
        var sleepers = new List<ProcessSession>();
        for (int i = 0; i < ProcessRegistry.MaxRunning; i++)
        {
            sleepers.Add(Start("ping -n 30 127.0.0.1 >nul"));
        }

        var ex = Assert.Throws<ShellStartException>(() => Start("echo no"));
        Assert.Equal("Error: too many background processes (16); kill or close one", ex.Message);
        Assert.Equal(16, _registry.Running);

        _registry.Dispose();
        foreach (var sleeper in sleepers)
        {
            Assert.True(sleeper.Killed);
            await sleeper.Exited.WaitAsync(TimeSpan.FromSeconds(15));   // Dispose waited for each kill to land (2026-09-22): the exits are in, or nearly
        }

        Assert.Empty(_registry.List());
        Assert.Throws<ObjectDisposedException>(() => Start("echo after"));
    }

    [Fact]
    public void PendingCallId_IsNineChars_AndOpeningByRule()
    {
        Assert.Equal("neonp2a1b", Llm.Assistant.PendingCallId("proc_3f2a1b"));
        Assert.Equal("neonp00ab", Llm.Assistant.PendingCallId("ab"));
        Assert.True(Llm.Assistant.IsOpeningCallId("neonp2a1b"));
        Assert.False(Llm.Assistant.IsOpeningCallId("neonp2a1bx"));
        Assert.False(Llm.Assistant.IsOpeningCallId("call_1"));
        Assert.True(Llm.Assistant.IsOpeningCallId(Llm.Assistant.OpeningClockCallId));
    }
}
