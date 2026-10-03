using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using NeonSidekick.Shell;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary><c>run_command</c> over a temp sandbox and real <c>cmd.exe</c> children (2026-09-21): the pinned schema, every refusal, the gate in front, the result text, the cut and its spill file.</summary>
public sealed class RunCommandToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly ManualTimeProvider _time = new();
    private readonly AppSettingsData _settings = new() { ShellCommandPolicy = "yolo", ShellDefault = "cmd" };
    private readonly Files.WorkingDirectory _files;
    private readonly Interpreters _interpreters;
    private readonly List<CommandRequest> _asked = new();
    private CommandChoice? _answer = CommandChoice.Deny;
    private readonly ProcessRegistry _registry;
    private int _signals;
    private readonly RunCommandTool _tool;
    private readonly CommandGate _gate;

    public RunCommandToolTests()
    {
        _root = Path.Combine(_dir, "files");
        Directory.CreateDirectory(_root);
        _files = new Files.WorkingDirectory(() => _root, _time);
        _interpreters = new Interpreters(_ => null);
        var list = new CommandAllowList(() => _settings.ShellCommandAllowed, merged => _settings.ShellCommandAllowed = [.. merged]);
        _gate = new CommandGate(() => _settings, list, (request, _) => { _asked.Add(request); return Task.FromResult(_answer); });
        var runner = new ShellRunner(_time);
        _registry = new ProcessRegistry(runner, new Random(1), () => Interlocked.Increment(ref _signals));
        _tool = App.ChatScreen.ShellTools(runner, _registry, _files, _gate, _interpreters, () => _settings, new Random(1), () => []).OfType<RunCommandTool>().Single();
    }

    public void Dispose()
    {
        _registry.Dispose();
        GitAccessTests.DeleteTree(_dir);
    }

    private static AIFunctionArguments Args(params (string Name, object? Value)[] pairs) => new(pairs.ToDictionary(p => p.Name, p => p.Value));

    private async Task<string> Invoke(params (string Name, object? Value)[] pairs) => (string)(await _tool.InvokeAsync(Args(pairs)))!;

    [Fact]
    public void Name_Schema_AndDescription_ArePinned()
    {
        Assert.Equal("run_command", _tool.Name);
        Assert.Equal(["run_command", "process", "execute_code"], ShellToolNames.All);
        // The description follows Shell police outside paths (2026-09-22): on, the command stays under the working directory; off, it starts there and nothing more — neither says "not confined".
        Assert.Equal(
            "Runs a command line in a shell on the user's computer and returns its exit code and output. " +
            "It runs in the working directory and may only name paths under it (relative, or absolute under it); the user approves a command before it runs and may deny it. " +
            "Use it for a program, a build, a test or a script the user asks for, never for what another tool does (files, git, the web, SQL); a denied or refused command must not be retried or worked around. " +
            "Use background for a server or a long job and the process tool to read it.",
            _tool.Description);
        Assert.Equal(RunCommandTool.DescriptionPoliced, _tool.Description);
        _settings.ShellPoliceOutsidePaths = false;
        Assert.Equal(
            "Runs a command line in a shell on the user's computer and returns its exit code and output. " +
            "It starts in the working directory; the user approves a command before it runs and may deny it. " +
            "Use it for a program, a build, a test or a script the user asks for, never for what another tool does (files, git, the web, SQL); a denied command must not be retried or worked around. " +
            "Use background for a server or a long job and the process tool to read it.",
            _tool.Description);
        Assert.Equal(RunCommandTool.DescriptionUnpoliced, _tool.Description);
        Assert.DoesNotContain("confined", RunCommandTool.DescriptionUnpoliced);
        Assert.DoesNotContain("under it", RunCommandTool.DescriptionUnpoliced);
        _settings.ShellPoliceOutsidePaths = true;
        var schema = _tool.JsonSchema;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Equal(["command", "shell", "workdir", "timeout", "background", "notify"], schema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        Assert.Equal("boolean", schema.GetProperty("properties").GetProperty("background").GetProperty("type").GetString());
        Assert.Equal("boolean", schema.GetProperty("properties").GetProperty("notify").GetProperty("type").GetString());
        Assert.Equal(["command"], schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["powershell", "cmd"], schema.GetProperty("properties").GetProperty("shell").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));   // no PATH: bash is not found
        Assert.Equal("The shell that runs it; leave it out for the user's default (cmd).", schema.GetProperty("properties").GetProperty("shell").GetProperty("description").GetString());
        Assert.Equal("integer", schema.GetProperty("properties").GetProperty("timeout").GetProperty("type").GetString());
        Assert.Equal("Seconds to wait before the command is stopped, 1 to 3600; leave it out for the user's default. Over 600 the command runs in the background instead.", schema.GetProperty("properties").GetProperty("timeout").GetProperty("description").GetString());
        foreach (var property in schema.GetProperty("properties").EnumerateObject())
        {
            Assert.False(string.IsNullOrWhiteSpace(property.Value.GetProperty("description").GetString()), property.Name + " has a description");
        }

        // The schema follows the settings: the default named, the cap quoted, one parse per change.
        _settings.ShellDefault = "powershell";
        _settings.ShellForegroundCapSeconds = 60;
        Assert.Equal("The shell that runs it; leave it out for the user's default (powershell).", _tool.JsonSchema.GetProperty("properties").GetProperty("shell").GetProperty("description").GetString());
        Assert.Contains("Over 60 the command", _tool.JsonSchema.GetProperty("properties").GetProperty("timeout").GetProperty("description").GetString());
        Assert.Equal(["powershell", "cmd"], _tool.AvailableShells);
        Assert.Equal("powershell", _tool.DefaultShell);
        Assert.Equal(600, RunCommandTool.ForegroundCap(new AppSettingsData()));
        Assert.Equal(3600, RunCommandTool.ForegroundCap(new AppSettingsData { ShellForegroundCapSeconds = 99999 }));
    }

    [Fact]
    public async Task Police_RefusesAnOutsidePath_BeforeTheGate_AndOffLetsItThrough()
    {
        // Shell police outside paths (2026-09-22): under ask, a line naming a path outside the sandbox is refused with the 👮 sentence and the asker is never called.
        _settings.ShellCommandPolicy = "ask";
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        Assert.Equal(@"Error: outside the working directory: 'C:\Windows\win.ini' — a command or a script may only name paths under it", await Invoke(("command", @"type C:\Windows\win.ini")));
        Assert.Equal(@"Error: outside the working directory: '..\..' — a command or a script may only name paths under it", await Invoke(("command", @"cd ..\.."), ("workdir", "sub")));   // relative to the workdir: sub\..\.. leaves the root
        Assert.Equal("Error: outside the working directory: '~' — a command or a script may only name paths under it", await Invoke(("command", "dir ~")));
        Assert.Equal("Error: outside the working directory: '%USERPROFILE%' — a command or a script may only name paths under it", await Invoke(("command", "dir %USERPROFILE%\\Desktop")));
        Assert.Empty(_asked);
        // A line under the root is put to the gate as before (the asker denies here).
        Assert.Equal("Error: the command was denied by the user: dir " + _root + "; do not retry it or work around the refusal", await Invoke(("command", "dir " + _root)));
        Assert.Equal("Error: the command was denied by the user: cd ..; do not retry it or work around the refusal", await Invoke(("command", "cd .."), ("workdir", "sub")));   // sub\.. is the root
        Assert.Equal(2, _asked.Count);
        // Off: the same line reaches the gate.
        _settings.ShellPoliceOutsidePaths = false;
        Assert.Equal(@"Error: the command was denied by the user: type C:\Windows\win.ini; do not retry it or work around the refusal", await Invoke(("command", @"type C:\Windows\win.ini")));
        Assert.Equal(3, _asked.Count);

        // Every one of them is in the gate's record (2026-09-26): the police's four, then the three denials.
        Assert.Equal([@"type C:\Windows\win.ini", @"cd ..\..", "dir ~", "dir %USERPROFILE%\\Desktop", "dir " + _root, "cd ..", @"type C:\Windows\win.ini"], _gate.Refusals);
    }

    [Fact]
    public async Task ForbiddenStrings_AreRefusedBeforeTheGate_EvenUnderYolo_TheModelNeverToldWhich()
    {
        // Shell police forbidden strings (2026-10-03): case and spacing ignored, refused before the gate (yolo here, so nothing would ask anyway).
        _settings.ShellPoliceForbiddenStrings = ["rd /s", "format"];
        var answer = await _tool.InvokeAsync(Args(("command", "RD   /S /Q build")));
        var shown = Assert.IsType<ToolShownResult>(answer);
        Assert.Equal(ShellText.Forbidden, shown.Text);
        Assert.Equal("forbidden string 'rd /s' — not run", shown.Shown);
        Assert.DoesNotContain("rd", shown.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["RD   /S /Q build"], _gate.Refusals);   // counted: headless's exit 3

        // Under ask the pane is never put to it; a line with none of them still is (denied here).
        _settings.ShellCommandPolicy = "ask";
        Assert.IsType<ToolShownResult>(await _tool.InvokeAsync(Args(("command", "echo x & format build"))));
        Assert.Empty(_asked);
        Assert.StartsWith("Error: the command was denied by the user", await Invoke(("command", "echo fine")));
        Assert.Single(_asked);

        // Under the police's own switch (the user's call): off, the line goes to the gate.
        _settings.ShellPoliceOutsidePaths = false;
        Assert.StartsWith("Error: the command was denied by the user", await Invoke(("command", "format build")));
        Assert.Equal(2, _asked.Count);

        // An empty list refuses nothing.
        _settings.ShellPoliceOutsidePaths = true;
        _settings.ShellPoliceForbiddenStrings = [];
        Assert.StartsWith("Error: the command was denied by the user", await Invoke(("command", "format build")));
        Assert.Equal(3, _asked.Count);
    }

    [Fact]
    public async Task PreferNative_SendsALineBackToItsTool_OnceATurn_BeforeTheGate()
    {
        // Shell prefer native tools (2026-09-26): under ask, a line a tool the turn offers covers comes back not run, and the asker is never called.
        _settings.ShellCommandPolicy = "ask";
        _tool.BeginTurn([ReadFileTool.ToolName, SearchFilesTool.ToolName, GitStatusTool.ToolName, RunCommandTool.ToolName]);
        string back = "Not run: 'type' has a tool of its own — call read_file instead. If read_file cannot do this, say why and call run_command again with the same command; the user will be asked.";
        Assert.Equal(back, await Invoke(("command", "type notes.txt")));
        Assert.Equal(ShellText.UseNative("dir", SearchFilesTool.ToolName), await Invoke(("command", "dir /s *.cs")));
        Assert.Equal(ShellText.UseNative("git status", GitStatusTool.ToolName), await Invoke(("command", "git status --short")));
        Assert.Empty(_asked);
        Assert.Empty(_gate.Refusals);   // not a refusal: headless's exit 3 does not count it

        // The same line again in the turn goes on to the gate: a real need still reaches the user.
        Assert.StartsWith("Error: the command was denied by the user: type notes.txt", await Invoke(("command", "type notes.txt")));
        Assert.Single(_asked);
        // A tool not offered, a compound line, a verb with no tool: the gate, as before.
        await Invoke(("command", "curl http://example.com"));
        await Invoke(("command", "type a.txt | find \"x\""));
        await Invoke(("command", "git push"));
        Assert.Equal(4, _asked.Count);

        // A new turn starts over.
        _tool.BeginTurn([ReadFileTool.ToolName]);
        Assert.Equal(back, await Invoke(("command", "type notes.txt")));
        Assert.Equal(4, _asked.Count);
    }

    [Fact]
    public async Task PreferNative_Off_OrAnOutsidePathWithThePoliceOff_GoesToTheGate()
    {
        _settings.ShellCommandPolicy = "ask";
        _tool.BeginTurn([ReadFileTool.ToolName]);
        _settings.ShellPreferNative = false;
        Assert.StartsWith("Error: the command was denied by the user", await Invoke(("command", "type notes.txt")));
        // On, but the line names a path no native tool reaches (the police off): the shell's alone.
        _settings.ShellPreferNative = true;
        _settings.ShellPoliceOutsidePaths = false;
        Assert.StartsWith("Error: the command was denied by the user", await Invoke(("command", @"type C:\Windows\win.ini")));
        Assert.Equal(2, _asked.Count);
        // … and a line under the root still goes back to read_file.
        Assert.Equal(ShellText.UseNative("type", ReadFileTool.ToolName), await Invoke(("command", "type notes.txt")));
        Assert.Equal(2, _asked.Count);
    }

    [Fact]
    public async Task Echo_UnderYolo_IsTheHeaderAndTheOutput()
    {
        string result = await Invoke(("command", "echo hi"));

        Assert.Equal("exit 0 in 0.0 s (cmd): echo hi\nhi", result);   // the manual clock: no time passes
        Assert.Empty(_asked);
    }

    [Fact]
    public async Task ExitCode_Stderr_AndTheShellArgument_ComeThrough()
    {
        Assert.Equal("exit 4 in 0.0 s (cmd): echo out & echo err 1>&2 & exit /b 4\nout \n\n--- stderr ---\nerr  ", await Invoke(("command", "echo out & echo err 1>&2 & exit /b 4"), ("shell", "cmd")));
        Assert.Equal("exit 0 in 0.0 s (cmd): exit /b 0\n(no output)", await Invoke(("command", "exit /b 0"), ("shell", " CMD ")));
        Assert.Equal("exit 5 in 0.0 s (powershell): exit 5\n(no output)", await Invoke(("command", "exit 5"), ("shell", "powershell")));
    }

    [Fact]
    public async Task Workdir_IsUnderTheSandbox_AndSetsWhereTheCommandStarts()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        Assert.Equal("exit 0 in 0.0 s (cmd): cd\n" + Path.Combine(_root, "sub"), await Invoke(("command", "cd"), ("workdir", "sub")));
        Assert.Equal("exit 0 in 0.0 s (cmd): cd\n" + _root, await Invoke(("command", "cd")));
        Assert.Equal("Error: workdir '..' is outside the working directory", await Invoke(("command", "cd"), ("workdir", "..")));
        Assert.Equal("Error: workdir 'nope' is not a folder", await Invoke(("command", "cd"), ("workdir", "nope")));
    }

    [Fact]
    public async Task Refusals_ArePinned()
    {
        Assert.Equal("Error: command is required", await Invoke());
        Assert.Equal("Error: command is required", await Invoke(("command", "   ")));
        Assert.Equal("Error: 'fish' is not one of powershell, cmd, bash for 'shell'", await Invoke(("command", "ls"), ("shell", "fish")));
        Assert.Equal("Error: bash is not installed (no bash.exe found)", await Invoke(("command", "ls"), ("shell", "bash")));
        Assert.Equal("Error: timeout must be 1 to 3600", await Invoke(("command", "dir"), ("timeout", 0)));
        Assert.Equal("Error: timeout must be 1 to 3600", await Invoke(("command", "dir"), ("timeout", 3601)));
        Assert.Equal("Error: timeout must be 1 to 3600", await Invoke(("command", "dir"), ("timeout", "soon")));
        Assert.Equal("Error: 'maybe' is not one of true, false for 'background'", await Invoke(("command", "dir"), ("background", "maybe")));
        Assert.Equal("Error: '2' is not one of true, false for 'notify'", await Invoke(("command", "dir"), ("notify", 2)));
        Assert.Equal("Error: the command is longer than 8,000 chars; put it in a script file and run that", await Invoke(("command", new string('x', 8001)), ("shell", "powershell")));
        _settings.ShellCommandPolicy = "off";
        Assert.Equal("Error: Shell command policy is off: no command runs", await Invoke(("command", "dir")));
    }

    [Fact]
    public async Task Ask_TheGateDecides_AndADenialIsTheSentence()
    {
        _settings.ShellCommandPolicy = "ask";
        _answer = CommandChoice.Deny;
        Assert.Equal("Error: the command was denied by the user: echo no; do not retry it or work around the refusal", await Invoke(("command", "echo no")));
        var request = Assert.Single(_asked);
        Assert.Equal("cmd", request.Kind);
        Assert.Equal("echo no", request.Command);
        Assert.Equal(["echo"], request.Prefixes);
        Assert.False(request.IsScript);

        _answer = CommandChoice.Permanent;
        Assert.Equal("exit 0 in 0.0 s (cmd): echo yes\nyes", await Invoke(("command", "echo yes")));
        Assert.Equal(["echo"], _settings.ShellCommandAllowed);
        Assert.Equal("exit 0 in 0.0 s (cmd): echo again\nagain", await Invoke(("command", "echo again")));
        Assert.Equal(2, _asked.Count);   // the second echo was on the list

        _answer = null;   // never asked
        Assert.StartsWith("Error: the command was not approved: no screen to ask on", await Invoke(("command", "dir")));
    }

    [Fact]
    public async Task Timeout_KillsTheChild_AndSaysSo()
    {
        var run = _tool.InvokeAsync(Args(("command", "ping -n 30 127.0.0.1 >nul"), ("timeout", 2)));
        await Task.Delay(200);   // the child is up and waiting
        _time.Advance(TimeSpan.FromSeconds(3));

        string result = (string)(await run.AsTask().WaitAsync(TimeSpan.FromSeconds(60)))!;
        Assert.Equal("timed out after 2.0 s (cmd, killed): ping -n 30 127.0.0.1 >nul\n(no output)", result);
    }

    [Fact]
    public async Task TheSettingTimeout_IsTheDefault_CappedByTheForegroundCap()
    {
        _settings.ShellTimeoutSeconds = 5000;   // hand-edited over the range: clamped, then capped
        _settings.ShellForegroundCapSeconds = 1;
        var run = _tool.InvokeAsync(Args(("command", "ping -n 30 127.0.0.1 >nul")));
        await Task.Delay(200);
        _time.Advance(TimeSpan.FromSeconds(11));

        string result = (string)(await run.AsTask().WaitAsync(TimeSpan.FromSeconds(60)))!;
        Assert.StartsWith("timed out after 10.0 s (cmd, killed): ping", result);   // the cap's own floor is 10
    }

    [Fact]
    public async Task Cancellation_KillsTheChild_AndThrows()
    {
        using var cts = new CancellationTokenSource();
        var run = _tool.InvokeAsync(Args(("command", "ping -n 30 127.0.0.1 >nul")), cts.Token);
        await Task.Delay(200);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.AsTask().WaitAsync(TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public async Task OutputOverTheCap_KeepsHeadAndTail_AndSpillsTheWholeText()
    {
        _settings.ShellOutputMaxChars = 2000;
        string result = await Invoke(("command", "for /l %i in (1,1,400) do @echo line %i"));

        Assert.StartsWith("exit 0 in 0.0 s (cmd): for /l %i in (1,1,400) do @echo line %i — output cut\nline 1\nline 2\n", result);
        Assert.EndsWith("\nline 399\nline 400", result);
        Assert.Contains("\n… (", result);
        Assert.Contains(@" chars cut; the whole output is in .shell\run_", result);
        Assert.True(result.Length < 2400);
        string spill = Assert.Single(Directory.GetFiles(Path.Combine(_root, ".shell"), "run_*.log"));
        string whole = File.ReadAllText(spill);
        Assert.StartsWith("line 1\nline 2\n", whole);
        Assert.EndsWith("line 400", whole);
        Assert.Equal(400, whole.Split('\n').Length);
    }

    [Fact]
    public void Schema_Parses_ForAnyShellSet()
    {
        var schema = RunCommandTool.SchemaFor(["cmd"], "cmd", 1, 3600, 30);
        Assert.Equal(JsonValueKind.Object, schema.ValueKind);
        Assert.Equal(["cmd"], schema.GetProperty("properties").GetProperty("shell").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["powershell", "cmd", "bash"], RunCommandTool.SchemaFor(["powershell", "cmd", "bash"], "bash", 1, 3600, 600).GetProperty("properties").GetProperty("shell").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
    }

    /// <summary>Background (2026-09-21): the gate first, then the registry's id at once, the poll hint; with notify the exit is an alert and a note.</summary>
    [Fact]
    public async Task Background_StartsUnderTheRegistry_AndAnswersTheIdAtOnce()
    {
        string result = await Invoke(("command", "echo bg"), ("background", true));

        Assert.Matches("^started proc_[0-9a-f]{6} \\(cmd, pid [0-9]+\\): echo bg\n" + "poll it with process\\(action: \"poll\", session_id: \"proc_[0-9a-f]{6}\"\\)\\.$", result);
        var session = Assert.Single(_registry.List());
        Assert.False(session.Notify);
        await session.Exited.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(["bg"], session.Output.Lines().Select(l => l.Text));
        Assert.False(_registry.TryTakeAlert(out _));
        Assert.Empty(_registry.TakeNotes());

        string notified = await Invoke(("command", "echo done"), ("background", "true"), ("notify", true));
        Assert.EndsWith("; you will be told when it exits.", notified);
        var second = _registry.List().Single(s => s.Label == "echo done");
        Assert.True(second.Notify);
        await second.Exited.WaitAsync(TimeSpan.FromSeconds(60));
        await Task.Delay(50);   // the registry's watcher runs after the exit
        Assert.True(_registry.TryTakeAlert(out var alert));
        Assert.Equal((second.Id, "echo done", "cmd", 0, false), (alert.Id, alert.Label, alert.Kind, alert.ExitCode, alert.Killed));
        Assert.Equal([second.Id], _registry.TakeNotes());
        Assert.Equal(1, _signals);
    }

    [Fact]
    public async Task ATimeoutOverTheCap_PromotesToTheBackground()
    {
        _settings.ShellForegroundCapSeconds = 10;
        string result = await Invoke(("command", "echo long"), ("timeout", 11));

        Assert.Matches("^started proc_[0-9a-f]{6} in the background \\(timeout 11 s is over the 10 s foreground cap; cmd, pid [0-9]+\\): echo long\n", result);
        Assert.Single(_registry.List());
    }

    [Fact]
    public async Task Background_TheGateStillDecides()
    {
        _settings.ShellCommandPolicy = "ask";
        _answer = CommandChoice.Deny;
        Assert.StartsWith("Error: the command was denied by the user: echo bg", await Invoke(("command", "echo bg"), ("background", true)));
        Assert.Empty(_registry.List());
    }
}
