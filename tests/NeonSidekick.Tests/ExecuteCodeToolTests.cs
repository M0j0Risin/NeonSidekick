using Microsoft.Extensions.AI;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using NeonSidekick.Shell;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>A fact that runs only where the interpreter is installed (found by the real <see cref="Interpreters"/> over the process PATH): a local gate for python and node, never something CI relies on.</summary>
public sealed class InterpreterFactAttribute : FactAttribute
{
    public InterpreterFactAttribute(string language)
    {
        var interpreters = new Interpreters(name => Environment.GetEnvironmentVariable(name));
        if (!CodeLanguages.TryParse(language, out var parsed) || interpreters.Locate(parsed) is null)
        {
            Skip = language + " is not installed on this machine.";
        }
    }
}

/// <summary>
/// <c>execute_code</c> (2026-09-21): the pinned schema over the languages found, the gate under the <c>code:</c>
/// prefix, the police, the run folder and its script, and real PowerShell scripts — python and node the same,
/// where they are installed. The bridge's tests (its wire, the scripts calling tools) went with it on 2026-10-23.
/// </summary>
public sealed class ExecuteCodeToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly ManualTimeProvider _time = new();
    private readonly AppSettingsData _settings = new() { ShellCommandPolicy = "yolo" };
    private readonly Files.WorkingDirectory _files;
    private readonly Interpreters _interpreters;
    private readonly List<CommandRequest> _asked = new();
    private CommandChoice? _answer = CommandChoice.Deny;
    private readonly ProcessRegistry _registry;
    private readonly ExecuteCodeTool _tool;
    private readonly CommandGate _gate;

    public ExecuteCodeToolTests()
    {
        _root = Path.Combine(_dir, "files");
        Directory.CreateDirectory(_root);
        _files = new Files.WorkingDirectory(() => _root, _time);
        // The real PATH: python and node ride only where they are; cmd and PowerShell are always there.
        _interpreters = new Interpreters(name => Environment.GetEnvironmentVariable(name));
        var list = new CommandAllowList(() => _settings.ShellCommandAllowed, merged => _settings.ShellCommandAllowed = [.. merged]);
        _gate = new CommandGate(() => _settings, list, (request, _) => { _asked.Add(request); return Task.FromResult(_answer); });
        var runner = new ShellRunner(_time);
        _registry = new ProcessRegistry(runner, new Random(3), () => { });
        var shell = App.ChatScreen.ShellTools(runner, _registry, _files, _gate, _interpreters, () => _settings, new Random(1), Path.Combine(_dir, "runs"));
        _tool = shell.OfType<ExecuteCodeTool>().Single();
    }

    public void Dispose()
    {
        _registry.Dispose();
        GitAccessTests.DeleteTree(_dir);
    }

    private static AIFunctionArguments Args(params (string Name, object? Value)[] pairs) => new(pairs.ToDictionary(p => p.Name, p => p.Value));

    private async Task<string> Invoke(params (string Name, object? Value)[] pairs) => (string)(await _tool.InvokeAsync(Args(pairs)))!;

    [WindowsFact]
    public void Name_Schema_AndDescription_ArePinned()
    {
        Assert.Equal("execute_code", _tool.Name);
        Assert.Equal(
            "Runs a script (python, node or powershell) in a fresh process and returns what it printed; " +
            "it runs in the working directory and may only name paths under it, the same approval as run_command applies, and a denied or refused script must not be retried or worked around.",
            _tool.Description);
        Assert.Equal(ExecuteCodeTool.DescriptionPoliced, _tool.Description);
        var schema = _tool.JsonSchema;
        Assert.Equal(["language", "code", "timeout"], schema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        Assert.Equal(["language", "code"], schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        var languages = schema.GetProperty("properties").GetProperty("language").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Contains("powershell", languages);   // always installed; python and node as the machine has them
        Assert.Equal(_tool.AvailableLanguages, languages);
        Assert.Equal("Seconds before the script is stopped, 1 to 3600 (default 300).", schema.GetProperty("properties").GetProperty("timeout").GetProperty("description").GetString());
        Assert.Equal("The script; print what you want back. Every path in it must stay under the working directory.", schema.GetProperty("properties").GetProperty("code").GetProperty("description").GetString());   // the police on (2026-09-22)

        // The setting narrows the enum; a language switched off is refused by name.
        _settings.ShellCodeLanguages = ["python"];
        Assert.DoesNotContain("powershell", _tool.AvailableLanguages);
        _settings.ShellCodeLanguages = ["nope"];
        Assert.Contains("powershell", _tool.AvailableLanguages);   // nothing usable named: the default set
        Assert.Equal(["powershell", "python", "node"], CodeLanguages.Names);
        Assert.Equal("python.exe", CodeLanguages.FileName(CodeLanguage.Python));
        Assert.Equal("a .py through python.exe", CodeLanguages.Describe("python"));
        Assert.True(CodeLanguages.TryParse(" Node ", out var node) && node == CodeLanguage.Node);
        Assert.False(CodeLanguages.TryParse("ruby", out _));
        Assert.Equal([CodeLanguage.Python], CodeLanguages.Resolve(new AppSettingsData { ShellCodeLanguages = ["python", "ruby"] }));
        Assert.Equal([CodeLanguage.PowerShell, CodeLanguage.Python, CodeLanguage.Node], CodeLanguages.Resolve(new AppSettingsData { ShellCodeLanguages = [] }));
    }

    /// <summary>The leftovers (2026-10-12, the shell review): run folders older than a day go; a fresh one and anything not a run's stay.</summary>
    [Fact]
    public void SweepLeftovers_TakesOnlyOldRunFolders()
    {
        string runs = Path.Combine(_dir, "sweep");
        var now = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
        foreach (var (name, age) in new[] { ("code_000001", 2.0), ("code_000002", 0.1), ("keep_000003", 3.0) })
        {
            string folder = Path.Combine(runs, name);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "script.py"), "print(1)");
            Directory.SetLastWriteTimeUtc(folder, now.AddDays(-age));
        }

        ExecuteCodeTool.SweepLeftovers(runs, now);

        Assert.Equal(["code_000002", "keep_000003"], Directory.GetDirectories(runs).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        ExecuteCodeTool.SweepLeftovers(Path.Combine(_dir, "none"), now);   // no folder: nothing to do
    }

    /// <summary>
    /// Why the sweep goes by age alone (2026-10-20, the code review, the user's call): no run outlives its timeout's cap, so a folder
    /// past <see cref="ExecuteCodeTool.LeftoverAge"/> is never a live run's.
    /// </summary>
    [Fact]
    public void LeftoverAge_IsPastTheLongestRun() =>
        Assert.True(ExecuteCodeTool.LeftoverAge > TimeSpan.FromSeconds(AppSettingsData.MaxShellCodeTimeoutSeconds));

    [Fact]
    public async Task Refusals_ArePinned()
    {
        Assert.Equal("Error: code is required", await Invoke(("language", "powershell")));
        Assert.Equal("Error: 'ruby' is not one of powershell, python, node for 'language'", await Invoke(("language", "ruby"), ("code", "x")));
        _settings.ShellCodeLanguages = ["python"];
        Assert.Equal("Error: powershell is not enabled; the user can tick it in Shell code languages on the Shell tab of /tools", await Invoke(("language", "powershell"), ("code", "x")));
        _settings.ShellCodeLanguages = ["powershell", "python", "node"];
        Assert.Equal("Error: timeout must be 1 to 3600", await Invoke(("language", "powershell"), ("code", "x"), ("timeout", 0)));
        _settings.ShellCommandPolicy = "ask";
        _answer = CommandChoice.Deny;
        Assert.Equal("Error: the script was denied by the user (powershell); do not retry it or work around the refusal", await Invoke(("language", "powershell"), ("code", "Write-Output 1")));
        var request = Assert.Single(_asked);
        Assert.True(request.IsScript);
        Assert.Equal(["code:powershell"], request.Prefixes);
        Assert.Equal("powershell", request.Kind);
        _settings.ShellCommandPolicy = "off";
        Assert.Equal("Error: Shell command policy is off: no command runs", await Invoke(("language", "powershell"), ("code", "Write-Output 1")));
    }

    [Fact]
    public void TheDescriptionAndSchema_SayTheScriptAlone()
    {
        // The script does everything itself (the tool bridge retired 2026-10-23): the description and the code property say no more.
        Assert.Equal(ExecuteCodeTool.DescriptionPoliced, _tool.Description);
        var schema = _tool.JsonSchema;
        Assert.Equal(ExecuteCodeTool.CodeDescription + ExecuteCodeTool.CodeDescriptionPolicedSuffix, schema.GetProperty("properties").GetProperty("code").GetProperty("description").GetString());
        Assert.Equal("The script; print what you want back.", ExecuteCodeTool.CodeDescription);
        Assert.Equal(["language", "code", "timeout"], schema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void PoliceOff_TheDescriptionAndSchema_NeverMentionTheWorkingDirectory()
    {
        // Shell police off (2026-09-22): the text until that day — nothing tells the model where a script may reach, so it does not try to leave.
        _settings.ShellPolice = false;
        Assert.Equal(
            "Runs a script (python, node or powershell) in a fresh process and returns what it printed; " +
            "the same approval as run_command applies, and a denied script must not be retried or worked around.",
            _tool.Description);
        Assert.Equal(ExecuteCodeTool.DescriptionUnpoliced, _tool.Description);
        Assert.DoesNotContain("working directory", _tool.JsonSchema.GetRawText());
        Assert.Equal("The script; print what you want back.", _tool.JsonSchema.GetProperty("properties").GetProperty("code").GetProperty("description").GetString());
        Assert.Equal(ExecuteCodeTool.DescriptionPoliced, ExecuteCodeTool.DescribeTool(police: true));
        Assert.Equal(ExecuteCodeTool.DescriptionUnpoliced, ExecuteCodeTool.DescribeTool(police: false));

        // Back on: the cached schema follows.
        _settings.ShellPolice = true;
        Assert.EndsWith(ExecuteCodeTool.CodeDescriptionPolicedSuffix, _tool.JsonSchema.GetProperty("properties").GetProperty("code").GetProperty("description").GetString());
    }

    [Fact]
    public async Task SqlitePolice_AScriptReachingSqlite_IsRefusedBeforeTheGate()
    {
        // 2026-10-05: while the SQLite tools are on (the guard set), a script that reaches SQLite never runs, yolo or not.
        _files.Databases = () => Files.DatabaseGuard.ByExtension;
        var shown = Assert.IsType<ToolShownResult>(await _tool.InvokeAsync(Args(("language", "powershell"), ("code", "Add-Type -Path System.Data.SQLite.dll\n$c = New-Object System.Data.SQLite.SQLiteConnection"))));
        Assert.Equal(ShellText.SqlitePoliced, shown.Text);
        Assert.Equal("SQLite: 'System.Data.SQLite.dll' — not run", shown.Shown);
        Assert.Equal(["powershell script"], _gate.Refusals);

        shown = Assert.IsType<ToolShownResult>(await _tool.InvokeAsync(Args(("language", "powershell"), ("code", "Remove-Item 'data/app.db'"))));
        Assert.Equal("SQLite: 'app.db' — not run", shown.Shown);
        _files.Databases = null;
    }

    [Fact]
    public async Task ServerDatabasePolice_AScriptReachingAFamily_IsRefusedBeforeTheGate()
    {
        // 2026-10-05: while a server family's tools are on, a script that reaches it never runs, yolo or not.
        _files.ServerDatabases = () => new Shell.ServerDatabaseGuard([new("PostgreSQL", "postgres_query", "postgres_execute", Shell.ServerDatabasePolice.PostgresWords, ["pg01"])]);
        var shown = Assert.IsType<ToolShownResult>(await _tool.InvokeAsync(Args(("language", "python"), ("code", "import asyncpg\nawait asyncpg.connect(host='pg01')"))));
        Assert.StartsWith(ShellText.ServerDatabaseHead, shown.Text);
        Assert.Equal("PostgreSQL: 'asyncpg' — not run", shown.Shown);
        Assert.Equal(["python script"], _gate.Refusals);
        _files.ServerDatabases = null;
    }

    [Fact]
    public async Task ForbiddenStrings_AnywhereInTheScript_AreRefusedBeforeTheGate()
    {
        // Shell police forbidden strings (2026-10-03): a line break counts as one space, so the words split over two lines still match.
        _settings.ShellPoliceForbiddenStrings = ["Remove-Item -Recurse"];
        var answer = await _tool.InvokeAsync(Args(("language", "powershell"), ("code", "Write-Output 1\nremove-item\n  -recurse build")));
        var shown = Assert.IsType<ToolShownResult>(answer);
        Assert.Equal(ShellText.Forbidden, shown.Text);
        Assert.Equal(ShellText.ForbiddenShown("Remove-Item -Recurse"), shown.Shown);
        Assert.Equal(["powershell script"], _gate.Refusals);

        // Off with the police: the gate's turn (denied here).
        _settings.ShellCommandPolicy = "ask";
        _answer = CommandChoice.Deny;
        _settings.ShellPolice = false;
        Assert.Equal("Error: the script was denied by the user (powershell); do not retry it or work around the refusal", await Invoke(("language", "powershell"), ("code", "Remove-Item -Recurse build")));
        Assert.Single(_asked);
    }

    [Fact]
    public async Task Police_RefusesAScriptNamingAnOutsidePath_BeforeTheGate()
    {
        // Shell police (2026-09-22): the script's text is read before the gate; a refusal never asks, and off it goes through to the gate.
        _settings.ShellCommandPolicy = "ask";
        _answer = CommandChoice.Deny;
        Assert.Equal(@"Error: outside the working directory: 'C:\Users\x.txt' — a command or a script may only name paths under it", await Invoke(("language", "powershell"), ("code", "Get-Content 'C:\\Users\\x.txt'")));   // powershell: always installed
        Assert.Equal("Error: outside the working directory: 'GetFolderPath(' — a command or a script may only name paths under it", await Invoke(("language", "powershell"), ("code", "Write-Output ([Environment]::GetFolderPath('Desktop'))")));
        Assert.Equal("Error: outside the working directory: '$env:APPDATA' — a command or a script may only name paths under it", await Invoke(("language", "powershell"), ("code", "Get-ChildItem $env:APPDATA")));
        Assert.Empty(_asked);
        Assert.Equal("Error: the script was denied by the user (powershell); do not retry it or work around the refusal", await Invoke(("language", "powershell"), ("code", "Get-ChildItem '" + _root + "'")));   // under the root: the gate's turn
        Assert.Single(_asked);
        _settings.ShellPolice = false;
        Assert.Equal("Error: the script was denied by the user (powershell); do not retry it or work around the refusal", await Invoke(("language", "powershell"), ("code", "Get-ChildItem $env:APPDATA")));
        Assert.Equal(2, _asked.Count);

        // The gate's record (2026-09-26): three policed scripts and two denied, each as its language's script.
        Assert.Equal(Enumerable.Repeat("powershell script", 5), _gate.Refusals);
    }

    [Fact]
    public async Task PowerShell_Script_RunsOnItsOwn_UnderAPlainHeader()
    {
        // The script alone in its run folder, and the header names the language and the first line.
        string result = await Invoke(("language", "powershell"), ("code", "Write-Output 'hé'\nWrite-Output 'done'"));

        Assert.StartsWith("exit 0 in 0.0 s (powershell): Write-Output 'hé'\n", result);
        Assert.Contains("\nhé\ndone", result);
        Assert.Empty(Directory.Exists(Path.Combine(_dir, "runs")) ? Directory.GetDirectories(Path.Combine(_dir, "runs")) : []);   // the run folder went with the run
    }

    [Fact]
    public async Task PowerShell_AnErrorInTheScript_IsExit1_OnStderr()
    {
        string result = await Invoke(("language", "powershell"), ("code", "Write-Output 'before'\nthrow 'kaboom'"));

        Assert.StartsWith("exit 1 in 0.0 s (powershell): Write-Output 'before'", result);
        Assert.Contains("\nbefore\n", result);
        // Both shells put the uncaught throw on stderr with exit 1, but frame it differently: Windows
        // PowerShell 5.1 prints the message first, pwsh 7's ConciseView (plain under NO_COLOR) leads
        // with "Exception: <script>:<line>" and a caret block and puts the message last (2026-09-21).
        int stderr = result.IndexOf(ShellText.StderrSeparator, StringComparison.Ordinal);
        Assert.True(stderr >= 0, result);
        Assert.Contains("kaboom", result[stderr..]);
    }

    [Fact]
    public async Task Timeout_KillsTheScript()
    {
        var run = _tool.InvokeAsync(Args(("language", "powershell"), ("code", "Start-Sleep -Seconds 60"), ("timeout", 2)));
        await Task.Delay(500);
        _time.Advance(TimeSpan.FromSeconds(3));

        string result = (string)(await run.AsTask().WaitAsync(TimeSpan.FromSeconds(60)))!;
        Assert.Equal("timed out after 2.0 s (powershell, killed): Start-Sleep -Seconds 60\n(no output)", result);
    }

    [InterpreterFact("python")]
    public async Task Python_Script_RunsOnItsOwn_AndReadsItsWorkingDirectory()
    {
        File.WriteAllText(Path.Combine(_root, "note.txt"), "from the file");
        string code = "import os\nprint(open('note.txt', encoding='utf-8').read())\nprint('ünïcode')";
        string result = await Invoke(("language", "python"), ("code", code));

        Assert.StartsWith("exit 0 in 0.0 s (python): import os\n", result);
        Assert.Contains("\nfrom the file\n", result);
        Assert.EndsWith("\nünïcode", result);
    }

    [InterpreterFact("node")]
    public async Task Node_Script_RunsOnItsOwn_AndReadsItsWorkingDirectory()
    {
        File.WriteAllText(Path.Combine(_root, "note.txt"), "from the file");
        string code = "const fs = require('fs');\nconsole.log(fs.readFileSync('note.txt', 'utf8'));\nconsole.log('ünïcode');";
        string result = await Invoke(("language", "node"), ("code", code));

        Assert.StartsWith("exit 0 in 0.0 s (node): const fs = require('fs');\n", result);
        Assert.Contains("\nfrom the file\n", result);
        Assert.EndsWith("\nünïcode", result);
    }

    [WindowsFact]
    public void CodeLaunch_LaysOutEachLanguage()
    {
        // The script alone, PowerShell's under the bare run_command wrapper, and no environment of the launch's own.
        var python = CodeLaunch.Files(CodeLanguage.Python, "print(1)");
        Assert.Equal(("script.py", "print(1)"), (python.ScriptName, python.ScriptText));
        var node = CodeLaunch.Files(CodeLanguage.Node, "x");
        Assert.Equal(("script.js", "x"), (node.ScriptName, node.ScriptText));
        var ps = CodeLaunch.Files(CodeLanguage.PowerShell, "Write-Output 1");
        Assert.Equal("script.ps1", ps.ScriptName);
        Assert.Equal(ShellCommandLine.PowerShellScript("Write-Output 1"), ps.ScriptText);
        Assert.StartsWith("$ProgressPreference = 'SilentlyContinue'\n", ps.ScriptText);

        var launch = CodeLaunch.For(CodeLanguage.Python, @"C:\py\python.exe", @"C:\runs\code_1", "script.py", @"D:\files", "print(1)");
        Assert.Equal(["-X", "utf8", @"C:\runs\code_1\script.py"], launch.ArgumentList);
        Assert.Equal(@"D:\files", launch.WorkingDirectory);
        Assert.Equal("python", launch.Kind);
        Assert.Equal("print(1)", launch.Label);
        Assert.Null(launch.Environment);
        Assert.Equal([@"C:\runs\code_1\script.js"], CodeLaunch.For(CodeLanguage.Node, "node.exe", @"C:\runs\code_1", "script.js", ".", "x").ArgumentList);
        Assert.Equal(["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", @"C:\runs\code_1\script.ps1"], CodeLaunch.For(CodeLanguage.PowerShell, "pwsh.exe", @"C:\runs\code_1", "script.ps1", ".", "x").ArgumentList);
    }

    /// <summary>The Unix twin of <see cref="CodeLaunch_LaysOutEachLanguage"/> (2026-10-06, the macOS build): Unix folders, the script's full path its argument.</summary>
    [UnixFact]
    public void CodeLaunch_LaysOutEachLanguage_WithUnixPaths()
    {
        Assert.Equal(ShellCommandLine.PowerShellScript("Write-Output 1"), CodeLaunch.Files(CodeLanguage.PowerShell, "Write-Output 1").ScriptText);
        var launch = CodeLaunch.For(CodeLanguage.Python, "/usr/bin/python3", "/runs/code_1", "script.py", "/Users/me/files", "print(1)");
        Assert.Equal(["-X", "utf8", "/runs/code_1/script.py"], launch.ArgumentList);
        Assert.Equal("/Users/me/files", launch.WorkingDirectory);
        Assert.Null(launch.Environment);
        Assert.Equal(["/runs/code_1/script.js"], CodeLaunch.For(CodeLanguage.Node, "node", "/runs/code_1", "script.js", ".", "x").ArgumentList);
        Assert.Equal(["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", "/runs/code_1/script.ps1"], CodeLaunch.For(CodeLanguage.PowerShell, "pwsh", "/runs/code_1", "script.ps1", ".", "x").ArgumentList);
    }

    [WindowsFact]
    public void ScriptText_Helpers_ArePinned()
    {
        Assert.Equal("exit 0 in 2.3 s (python): import os", ShellText.ScriptExitHeader("python", 0, TimeSpan.FromSeconds(2.34), "import os"));
        Assert.Equal("timed out after 5 m 0 s (python, killed): x", ShellText.ScriptTimedOutHeader("python", TimeSpan.FromMinutes(5), "x"));
        Assert.Equal("execute_code: python \"import os\" → exit 0 in 2.3 s (2,340 chars)", ShellText.ScriptLogLine("python", "import os", "exit 0 in 2.3 s", 2340));
        Assert.Equal("import os", ShellText.FirstLine("\n  \n  import os  \nprint(1)"));
        Assert.Equal("(empty)", ShellText.FirstLine("  \n"));
        Assert.Equal("code:python", ShellText.ScriptPrefix("python"));
        Assert.Equal("Error: python is not installed (no python.exe found)", ShellText.LanguageNotInstalled(CodeLanguage.Python));
    }

    /// <summary>The Unix twin of the <c>python.exe</c> pins in <see cref="Name_Schema_AndDescription_ArePinned"/> and <see cref="ScriptText_Helpers_ArePinned"/> (2026-10-06): <c>python3</c>.</summary>
    [UnixFact]
    public void ThePythonWords_NamePython3_OffWindows()
    {
        Assert.Equal("python3", CodeLanguages.FileName(CodeLanguage.Python));
        Assert.Equal("a .py through python3", CodeLanguages.Describe("python"));
        Assert.Equal("Error: python is not installed (no python3 found)", ShellText.LanguageNotInstalled(CodeLanguage.Python));
    }
}
