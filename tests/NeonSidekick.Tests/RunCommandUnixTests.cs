using Microsoft.Extensions.AI;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using NeonSidekick.Shell;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>run_command</c> over a temp sandbox and real <c>zsh</c> children (2026-10-06, the macOS build): the Unix twins of
/// <see cref="RunCommandToolTests"/>, whose lines are <c>cmd.exe</c>'s (<c>exit /b</c>, <c>ping -n</c>, <c>for /l</c>, <c>C:\</c>)
/// and are Windows-only. The schema's shells and default, the refusals' words, the header, the police on Unix paths, the timeouts,
/// the cut and its spill, the background. Skipped on Windows.
/// </summary>
public sealed class RunCommandUnixTests : IDisposable
{
    private const string Sleeper = "sleep 30";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly ManualTimeProvider _time = new();
    private readonly AppSettingsData _settings = new() { ShellCommandPolicy = "yolo", ShellDefault = "zsh" };
    private readonly List<CommandRequest> _asked = new();
    private readonly ProcessRegistry _registry;
    private readonly RunCommandTool _tool;
    private readonly CommandGate _gate;
    private CommandChoice? _answer = CommandChoice.Deny;

    public RunCommandUnixTests()
    {
        _root = Path.Combine(_dir, "files");
        Directory.CreateDirectory(_root);
        var files = new Files.WorkingDirectory(() => _root, _time);
        var list = new CommandAllowList(() => _settings.ShellCommandAllowed, merged => _settings.ShellCommandAllowed = [.. merged]);
        _gate = new CommandGate(() => _settings, list, (request, _) => { _asked.Add(request); return Task.FromResult(_answer); });
        var runner = new ShellRunner(_time);
        _registry = new ProcessRegistry(runner, new Random(1), () => { });
        _tool = App.ChatScreen.ShellTools(runner, _registry, files, _gate, new Interpreters(_ => null), () => _settings, new Random(1), () => []).OfType<RunCommandTool>().Single();
    }

    public void Dispose()
    {
        _registry.Dispose();
        GitAccessTests.DeleteTree(_dir);
    }

    private static AIFunctionArguments Args(params (string Name, object? Value)[] pairs) => new(pairs.ToDictionary(p => p.Name, p => p.Value));

    private async Task<string> Invoke(params (string Name, object? Value)[] pairs) => (string)(await _tool.InvokeAsync(Args(pairs)))!;

    [UnixFact]
    public void TheSchema_OffersTheUnixShells_AndNamesTheDefault()
    {
        var shell = _tool.JsonSchema.GetProperty("properties").GetProperty("shell");
        Assert.Equal(["zsh", "bash"], shell.GetProperty("enum").EnumerateArray().Select(e => e.GetString()));   // no PATH: the system's own in /bin, no pwsh
        Assert.Equal("The shell that runs it; leave it out for the user's default (zsh).", shell.GetProperty("description").GetString());
        Assert.Equal("zsh", _tool.DefaultShell);
    }

    [UnixFact]
    public async Task Refusals_NameTheUnixShells()
    {
        Assert.Equal("Error: 'fish' is not one of zsh, bash, powershell for 'shell'", await Invoke(("command", "ls"), ("shell", "fish")));
        Assert.Equal("Error: powershell is not installed (no pwsh found)", await Invoke(("command", "ls"), ("shell", "powershell")));
    }

    [UnixFact]
    public async Task Echo_ExitCode_Stderr_AndTheShellArgument_ComeThrough()
    {
        Assert.Equal("exit 0 in 0.0 s (zsh): echo hi\nhi", await Invoke(("command", "echo hi")));
        Assert.Equal("exit 4 in 0.0 s (zsh): echo out; echo err >&2; exit 4\nout\n\n--- stderr ---\nerr", await Invoke(("command", "echo out; echo err >&2; exit 4")));
        Assert.Equal("exit 5 in 0.0 s (bash): exit 5\n(no output)", await Invoke(("command", "exit 5"), ("shell", " BASH ")));
        Assert.Empty(_asked);
    }

    [UnixFact]
    public async Task Workdir_IsUnderTheSandbox_AndSetsWhereTheCommandStarts()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        string sub = await Invoke(("command", "pwd -P"), ("workdir", "sub"));
        Assert.Equal("exit 0 in 0.0 s (zsh): pwd -P\n" + Real(Path.Combine(_root, "sub")), sub);
        Assert.Equal("Error: workdir '..' is outside the working directory", await Invoke(("command", "pwd"), ("workdir", "..")));
        Assert.Equal("Error: workdir 'nope' is not a folder", await Invoke(("command", "pwd"), ("workdir", "nope")));

        // The temp folder lies under /var, a link to /private/var: pwd -P names where the child really is.
        static string Real(string path) => new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName
            ?? (path.StartsWith("/var/", StringComparison.Ordinal) && OperatingSystem.IsMacOS() ? "/private" + path : path);
    }

    [UnixFact]
    public async Task Police_RefusesAnOutsidePath_BeforeTheGate_AndOffLetsItThrough()
    {
        _settings.ShellCommandPolicy = "ask";
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        Assert.Equal("Error: outside the working directory: '/etc/hosts' — a command or a script may only name paths under it", await Invoke(("command", "cat /etc/hosts")));
        Assert.Equal("Error: outside the working directory: '../..' — a command or a script may only name paths under it", await Invoke(("command", "cd ../.."), ("workdir", "sub")));
        Assert.Equal("Error: outside the working directory: '~' — a command or a script may only name paths under it", await Invoke(("command", "ls ~")));
        Assert.Equal("Error: outside the working directory: '$HOME' — a command or a script may only name paths under it", await Invoke(("command", "ls $HOME/Desktop")));
        Assert.Empty(_asked);
        Assert.Equal("Error: the command was denied by the user: ls " + _root + "; do not retry it or work around the refusal", await Invoke(("command", "ls " + _root)));
        Assert.Single(_asked);

        _settings.ShellPolice = false;
        Assert.Equal("Error: the command was denied by the user: cat /etc/hosts; do not retry it or work around the refusal", await Invoke(("command", "cat /etc/hosts")));
        Assert.Equal(["cat /etc/hosts", "cd ../..", "ls ~", "ls $HOME/Desktop", "ls " + _root, "cat /etc/hosts"], _gate.Refusals);
    }

    /// <summary>
    /// The Unix twin of <see cref="RunCommandToolTests.PreferNative_SendsALineBackToItsTool_OnceATurn_BeforeTheGate"/> (2026-10-06):
    /// zsh's lines, and its <c>type</c> — which describes a command there — never sent to <c>read_file</c> on a Mac.
    /// </summary>
    [UnixFact]
    public async Task PreferNative_SendsALineBackToItsTool_OnceATurn_BeforeTheGate_Unix()
    {
        _settings.ShellCommandPolicy = "ask";
        _tool.BeginTurn([ReadFileTool.ToolName, SearchFilesTool.ToolName, GitStatusTool.ToolName, RunCommandTool.ToolName]);
        string back = "Not run: 'cat' has a tool of its own — call read_file instead. If read_file cannot do this, say why and call run_command again with the same command; the user will be asked.";
        Assert.Equal(back, await Invoke(("command", "cat notes.txt")));
        Assert.Equal(ShellText.UseNative("ls", SearchFilesTool.ToolName), await Invoke(("command", "ls -R")));
        Assert.Equal(ShellText.UseNative("git status", GitStatusTool.ToolName), await Invoke(("command", "git status --short")));
        Assert.Empty(_asked);
        Assert.Empty(_gate.Refusals);

        // The same line again in the turn goes on to the gate.
        Assert.StartsWith("Error: the command was denied by the user: cat notes.txt", await Invoke(("command", "cat notes.txt")));
        Assert.Single(_asked);
        // A compound line, a verb with no tool: the gate, as before.
        await Invoke(("command", "cat a.txt | grep x"));
        await Invoke(("command", "git push"));
        Assert.Equal(3, _asked.Count);
        if (OperatingSystem.IsMacOS())
        {
            // zsh's type describes a command: the shell's own, on to the gate.
            Assert.StartsWith("Error: the command was denied by the user: type python3", await Invoke(("command", "type python3")));
            Assert.Equal(4, _asked.Count);
        }
    }

    [UnixFact]
    public async Task PreferNative_Off_OrAnOutsidePathWithThePoliceOff_GoesToTheGate()
    {
        _settings.ShellCommandPolicy = "ask";
        _tool.BeginTurn([ReadFileTool.ToolName]);
        _settings.ShellPreferNative = false;
        Assert.StartsWith("Error: the command was denied by the user", await Invoke(("command", "cat notes.txt")));
        _settings.ShellPreferNative = true;
        _settings.ShellPolice = false;
        Assert.StartsWith("Error: the command was denied by the user", await Invoke(("command", "cat /etc/hosts")));
        Assert.Equal(2, _asked.Count);
        Assert.Equal(ShellText.UseNative("cat", ReadFileTool.ToolName), await Invoke(("command", "cat notes.txt")));
        Assert.Equal(2, _asked.Count);
    }

    [UnixFact]
    public async Task Ask_TheGateDecides_TheRequestNamingZsh()
    {
        _settings.ShellCommandPolicy = "ask";
        Assert.StartsWith("Error: the command was denied by the user: echo no", await Invoke(("command", "echo no")));
        Assert.Equal("zsh", Assert.Single(_asked).Kind);
        _answer = CommandChoice.Permanent;
        Assert.Equal("exit 0 in 0.0 s (zsh): echo yes\nyes", await Invoke(("command", "echo yes")));
        Assert.Equal(["echo"], _settings.ShellCommandAllowed);
    }

    [UnixFact]
    public async Task Timeouts_KillTheChild_AndCancellationThrows()
    {
        var run = _tool.InvokeAsync(Args(("command", Sleeper), ("timeout", 2)));
        await Task.Delay(200);
        _time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal("timed out after 2.0 s (zsh, killed): " + Sleeper + "\n(no output)", (string)(await run.AsTask().WaitAsync(TimeSpan.FromSeconds(60)))!);

        _settings.ShellTimeoutSeconds = 5000;
        _settings.ShellForegroundCapSeconds = 1;
        var capped = _tool.InvokeAsync(Args(("command", Sleeper)));
        await Task.Delay(200);
        _time.Advance(TimeSpan.FromSeconds(11));
        Assert.StartsWith("timed out after 10.0 s (zsh, killed): sleep", (string)(await capped.AsTask().WaitAsync(TimeSpan.FromSeconds(60)))!);

        using var cts = new CancellationTokenSource();
        var cancelled = _tool.InvokeAsync(Args(("command", Sleeper)), cts.Token);
        await Task.Delay(200);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.AsTask().WaitAsync(TimeSpan.FromSeconds(60)));
    }

    [UnixFact]
    public async Task OutputOverTheCap_KeepsHeadAndTail_AndSpillsTheWholeText()
    {
        _settings.ShellOutputMaxChars = 2000;
        const string Lines = "for i in {1..400}; do echo line $i; done";
        string result = await Invoke(("command", Lines));

        Assert.StartsWith("exit 0 in 0.0 s (zsh): " + Lines + " — output cut\nline 1\nline 2\n", result);
        Assert.EndsWith("\nline 399\nline 400", result);
        Assert.Contains(" characters cut; the whole output is in .shell/run_", result);
        string whole = File.ReadAllText(Assert.Single(Directory.GetFiles(Path.Combine(_root, ".shell"), "run_*.log")));
        Assert.Equal(400, whole.Split('\n').Length);
    }

    [UnixFact]
    public async Task Background_AnswersTheIdAtOnce_AndATimeoutOverTheCapPromotesToIt()
    {
        Assert.Matches("^started proc_[0-9a-f]{6} \\(zsh, pid [0-9]+\\): echo bg\npoll it with process", await Invoke(("command", "echo bg"), ("background", true)));
        var session = Assert.Single(_registry.List());
        await session.Exited.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(["bg"], session.Output.Lines().Select(l => l.Text));

        _settings.ShellForegroundCapSeconds = 10;
        Assert.Matches("^started proc_[0-9a-f]{6} in the background \\(timeout 11 s is over the 10 s foreground cap; zsh, pid [0-9]+\\): echo long\n", await Invoke(("command", "echo long"), ("timeout", 11)));
    }
}
