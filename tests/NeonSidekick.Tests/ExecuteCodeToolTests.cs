using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using NeonSidekick.Shell;
using NeonSidekick.Shell.Bridge;
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
/// <c>execute_code</c> and its bridge (2026-09-21): the pinned schema over the languages found, the
/// gate under the <c>code:</c> prefix, the run folder and its files, the wire (a raw <c>TcpClient</c>
/// against the server), and a real PowerShell script calling a tool through <c>Invoke-NeonTool</c> —
/// python and node the same, where they are installed.
/// </summary>
public sealed class ExecuteCodeToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly ManualTimeProvider _time = new();
    // The bridge on (later on 2026-09-21 it starts off): these tests are about the bridge; the ones with it off flip it.
    private readonly AppSettingsData _settings = new() { ShellCommandPolicy = "yolo", ShellToolBridge = true };
    private readonly Files.WorkingDirectory _files;
    private readonly Interpreters _interpreters;
    private readonly EchoTool _echo = new();
    private readonly List<CommandRequest> _asked = new();
    private CommandChoice? _answer = CommandChoice.Deny;
    private readonly ProcessRegistry _registry;
    private readonly IReadOnlyList<AIFunction> _tools;
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
        IReadOnlyList<AIFunction> tools = [];
        var shell = App.ChatScreen.ShellTools(runner, _registry, _files, _gate, _interpreters, () => _settings, new Random(1), () => tools, Path.Combine(_dir, "runs"));
        tools = [_echo, new GetWorkingDirectoryTool(_files, () => false), .. shell, new AskUserTool((_, _) => Task.FromResult<IReadOnlyList<AskAnswer>?>(null), () => _settings)];
        _tools = tools;
        _tool = shell.OfType<ExecuteCodeTool>().Single();
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
        Assert.Equal("execute_code", _tool.Name);
        Assert.Equal(
            "Runs a script (python, node or powershell) in a fresh process and returns what it printed. " +
            "The script can call this app's other tools by name through the neon_tools module, so several steps can be done in one call; " +
            "it runs in the working directory and may only name paths under it, the same approval as run_command applies, and a denied or refused script must not be retried or worked around.",
            _tool.Description);
        Assert.Equal(ExecuteCodeTool.DescriptionWithBridge, _tool.Description);
        var schema = _tool.JsonSchema;
        Assert.Equal(["language", "code", "timeout"], schema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        Assert.Equal(["language", "code"], schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        var languages = schema.GetProperty("properties").GetProperty("language").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Contains("powershell", languages);   // always installed; python and node as the machine has them
        Assert.Equal(_tool.AvailableLanguages, languages);
        Assert.Equal("Seconds before the script is stopped, 1 to 3600 (default 300).", schema.GetProperty("properties").GetProperty("timeout").GetProperty("description").GetString());
        Assert.Contains("from neon_tools import", schema.GetProperty("properties").GetProperty("code").GetProperty("description").GetString());
        Assert.EndsWith(" Every path in it must stay under the working directory.", schema.GetProperty("properties").GetProperty("code").GetProperty("description").GetString());   // the police on (2026-09-22)

        // The setting narrows the enum; a language switched off is refused by name.
        _settings.ShellCodeLanguages = ["python"];
        Assert.DoesNotContain("powershell", _tool.AvailableLanguages);
        _settings.ShellCodeLanguages = ["nope"];
        Assert.Contains("powershell", _tool.AvailableLanguages);   // nothing usable named: the default set
        Assert.Equal(["powershell", "python", "node"], CodeLanguages.Names);
        Assert.Equal("python.exe", CodeLanguages.FileName(CodeLanguage.Python));
        Assert.Equal("a .py through python.exe; from neon_tools import …", CodeLanguages.Describe("python"));
        Assert.True(CodeLanguages.TryParse(" Node ", out var node) && node == CodeLanguage.Node);
        Assert.False(CodeLanguages.TryParse("ruby", out _));
        Assert.Equal([CodeLanguage.Python], CodeLanguages.Resolve(new AppSettingsData { ShellCodeLanguages = ["python", "ruby"] }));
        Assert.Equal([CodeLanguage.PowerShell, CodeLanguage.Python, CodeLanguage.Node], CodeLanguages.Resolve(new AppSettingsData { ShellCodeLanguages = [] }));
        Assert.Equal(["execute_code", "ask_user"], ExecuteCodeTool.Excluded);
    }

    [Fact]
    public async Task Refusals_ArePinned()
    {
        Assert.Equal("Error: code is required", await Invoke(("language", "powershell")));
        Assert.Equal("Error: 'ruby' is not one of powershell, python, node for 'language'", await Invoke(("language", "ruby"), ("code", "x")));
        _settings.ShellCodeLanguages = ["python"];
        Assert.Equal("Error: powershell is not enabled (the Shell tab of /tools, Shell code languages)", await Invoke(("language", "powershell"), ("code", "x")));
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
    public void BridgeOff_TheDescriptionAndSchema_NeverMentionNeonTools()
    {
        // Shell tool bridge off (later on 2026-09-21, the default): the model is not told a script can call tools, so it never tries.
        _settings.ShellToolBridge = false;
        Assert.Equal(
            "Runs a script (python, node or powershell) in a fresh process and returns what it printed; " +
            "it runs in the working directory and may only name paths under it, the same approval as run_command applies, and a denied or refused script must not be retried or worked around.",
            _tool.Description);
        Assert.Equal(ExecuteCodeTool.DescriptionWithoutBridge, _tool.Description);
        var schema = _tool.JsonSchema;
        Assert.Equal("The script; print what you want back. Every path in it must stay under the working directory.", schema.GetProperty("properties").GetProperty("code").GetProperty("description").GetString());
        Assert.DoesNotContain("neon_tools", schema.GetRawText());
        Assert.Equal(["language", "code", "timeout"], schema.GetProperty("properties").EnumerateObject().Select(p => p.Name));

        // The cached schema follows the setting both ways.
        _settings.ShellToolBridge = true;
        Assert.Contains("from neon_tools import", _tool.JsonSchema.GetProperty("properties").GetProperty("code").GetProperty("description").GetString());
        _settings.ShellToolBridge = false;
        Assert.DoesNotContain("neon_tools", _tool.JsonSchema.GetRawText());
    }

    [Fact]
    public void PoliceOff_TheDescriptionAndSchema_NeverMentionTheWorkingDirectory()
    {
        // Shell police off (2026-09-22): the text until that day — nothing tells the model where a script may reach, so it does not try to leave.
        _settings.ShellPolice = false;
        Assert.Equal(
            "Runs a script (python, node or powershell) in a fresh process and returns what it printed. " +
            "The script can call this app's other tools by name through the neon_tools module, so several steps can be done in one call; " +
            "the same approval as run_command applies, and a denied script must not be retried or worked around.",
            _tool.Description);
        Assert.Equal(ExecuteCodeTool.DescriptionWithBridgeUnpoliced, _tool.Description);
        Assert.DoesNotContain("working directory", _tool.JsonSchema.GetRawText());
        _settings.ShellToolBridge = false;
        Assert.Equal(
            "Runs a script (python, node or powershell) in a fresh process and returns what it printed; " +
            "the same approval as run_command applies, and a denied script must not be retried or worked around.",
            _tool.Description);
        Assert.Equal(ExecuteCodeTool.DescriptionWithoutBridgeUnpoliced, _tool.Description);
        Assert.Equal("The script; print what you want back.", _tool.JsonSchema.GetProperty("properties").GetProperty("code").GetProperty("description").GetString());
        Assert.Equal(ExecuteCodeTool.DescriptionWithBridge, ExecuteCodeTool.DescribeTool(bridge: true, police: true));
        Assert.Equal(ExecuteCodeTool.DescriptionWithoutBridge, ExecuteCodeTool.DescribeTool(bridge: false, police: true));
        Assert.Equal(ExecuteCodeTool.DescriptionWithBridgeUnpoliced, ExecuteCodeTool.DescribeTool(bridge: true, police: false));
        Assert.Equal(ExecuteCodeTool.DescriptionWithoutBridgeUnpoliced, ExecuteCodeTool.DescribeTool(bridge: false, police: false));

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
    public async Task BridgeOff_PowerShell_HasNoModule_NoBridge_AndAPlainHeader()
    {
        // With the bridge off no module is written and no server listens: Invoke-NeonTool is an unknown command, the environment carries nothing, the header has no tool-call clause.
        _settings.ShellToolBridge = false;
        string code = "Write-Output \"addr: [$env:NEONSIDEKICK_BRIDGE_ADDRESS] tok: [$env:NEONSIDEKICK_BRIDGE_TOKEN]\"\nWrite-Output (Get-Command Invoke-NeonTool -ErrorAction SilentlyContinue).Count\nWrite-Output 'done'";
        string result = await Invoke(("language", "powershell"), ("code", code));

        Assert.StartsWith("exit 0 in 0.0 s (powershell): Write-Output \"addr: [$env:NEONSIDEKICK_BRIDGE_ADDRESS] tok: [$env:NEONSIDEKICK_BRIDGE_TOKEN]\"\n", result);
        Assert.Contains("\naddr: [] tok: []\n0\ndone", result);
        Assert.Empty(_echo.Received);
        Assert.Empty(Directory.Exists(Path.Combine(_dir, "runs")) ? Directory.GetDirectories(Path.Combine(_dir, "runs")) : []);
    }

    [Fact]
    public async Task PowerShell_Script_CallsAToolThroughTheBridge_AndTheHeaderCountsTheCalls()
    {
        string code = "$cwd = Invoke-NeonTool get_working_directory\nWrite-Output \"cwd: $cwd\"\nWrite-Output (Invoke-NeonTool echo @{ text = 'hé' })\nInvoke-NeonTool echo @{ text = 'two' } | Out-Null";
        string result = await Invoke(("language", "powershell"), ("code", code));

        Assert.StartsWith("exit 0 in 0.0 s (powershell, 3 tool calls): $cwd = Invoke-NeonTool get_working_directory\n", result);
        Assert.Contains("\ncwd: The working directory is '" + _root + "'", result);
        Assert.Contains("\necho: hé", result);
        Assert.Equal(["hé", "two"], _echo.Received);
        Assert.Empty(Directory.Exists(Path.Combine(_dir, "runs")) ? Directory.GetDirectories(Path.Combine(_dir, "runs")) : []);   // the run folder went with the run
    }

    [Fact]
    public async Task PowerShell_AnErrorInTheScript_IsExit1_OnStderr_AndAToolError_Throws()
    {
        string result = await Invoke(("language", "powershell"), ("code", "try { Invoke-NeonTool nope @{} } catch { Write-Output \"caught: $_\" }\nInvoke-NeonTool ask_user @{}"));

        Assert.StartsWith("exit 1 in 0.0 s (powershell, 2 tool calls): try { Invoke-NeonTool nope @{} }", result);   // every request the bridge dispatched counts, an unknown tool's too
        Assert.Contains("\ncaught: Error: unknown tool nope\n", result);
        // Both shells put the uncaught throw on stderr with exit 1, but frame it differently: Windows
        // PowerShell 5.1 prints the message first, pwsh 7's ConciseView (plain under NO_COLOR) leads
        // with "Exception: <script>:<line>" and a caret block and puts the message last (2026-09-21).
        int stderr = result.IndexOf(ShellText.StderrSeparator, StringComparison.Ordinal);
        Assert.True(stderr >= 0, result);
        Assert.Contains("Error: unknown tool ask_user", result[stderr..]);
    }

    [Fact]
    public async Task ANestedRunCommand_GoesThroughTheGate_NeverInTheBackground()
    {
        string result = await Invoke(("language", "powershell"), ("code", "Write-Output (Invoke-NeonTool run_command @{ command = 'echo nested'; shell = 'cmd' })\ntry { Invoke-NeonTool run_command @{ command = 'echo bg'; shell = 'cmd'; background = $true } } catch { Write-Output \"refused: $_\" }"));

        Assert.StartsWith("exit 0 in 0.0 s (powershell, 2 tool calls): ", result);
        Assert.Contains("\nexit 0 in 0.0 s (cmd): echo nested\nnested\n", result);
        Assert.Contains("\nrefused: Error: background is not available from a script", result);
        Assert.Empty(_registry.List());
    }

    [Fact]
    public async Task TheToolCallCap_IsAnswered_NotEnforcedByForce()
    {
        _settings.ShellCodeMaxToolCalls = 2;
        string result = await Invoke(("language", "powershell"), ("code", "1..3 | ForEach-Object { try { Invoke-NeonTool echo @{ text = \"$_\" } } catch { \"stop: $_\" } }"));

        Assert.StartsWith("exit 0 in 0.0 s (powershell, 2 tool calls): ", result);
        Assert.Contains("\necho: 1\necho: 2\nstop: Error: this run's tool call limit (2) is reached", result);
    }

    [Fact]
    public async Task Timeout_KillsTheScript()
    {
        var run = _tool.InvokeAsync(Args(("language", "powershell"), ("code", "Start-Sleep -Seconds 60"), ("timeout", 2)));
        await Task.Delay(500);
        _time.Advance(TimeSpan.FromSeconds(3));

        string result = (string)(await run.AsTask().WaitAsync(TimeSpan.FromSeconds(60)))!;
        Assert.Equal("timed out after 2.0 s (powershell, killed, 0 tool calls): Start-Sleep -Seconds 60\n(no output)", result);
    }

    [InterpreterFact("python")]
    public async Task Python_Script_CallsAToolThroughTheBridge()
    {
        string code = "from neon_tools import call, read_file, ToolError\nimport neon_tools\nprint(call('echo', text='hi'))\nprint(neon_tools.get_working_directory())\ntry:\n    read_file(path='missing.txt')\nexcept ToolError as e:\n    print('caught', str(e)[:6])\nprint('ünïcode')";
        string result = await Invoke(("language", "python"), ("code", code));

        Assert.StartsWith("exit 0 in 0.0 s (python, 3 tool calls): from neon_tools import call, read_file, ToolError\n", result);
        Assert.Contains("\necho: hi\n", result);
        Assert.Contains("\nThe working directory is '" + _root + "'", result);
        Assert.Contains("\ncaught Error:\n", result);
        Assert.EndsWith("\nünïcode", result);
    }

    [InterpreterFact("node")]
    public async Task Node_Script_CallsAToolThroughTheBridge()
    {
        string code = "const neon = require('neon_tools');\nneon.run(async () => {\n  console.log(await neon.call('echo', { text: 'hi' }));\n  console.log(await neon.getWorkingDirectory());\n  try { await neon.readFile('missing.txt'); } catch (e) { console.log('caught', e.message.slice(0, 6)); }\n  console.log('ünïcode');\n});";
        string result = await Invoke(("language", "node"), ("code", code));

        Assert.StartsWith("exit 0 in 0.0 s (node, 3 tool calls): const neon = require('neon_tools');\n", result);
        Assert.Contains("\necho: hi\n", result);
        Assert.Contains("\nThe working directory is '" + _root + "'", result);
        Assert.Contains("\ncaught Error:\n", result);
        Assert.EndsWith("\nünïcode", result);
    }

    // ── The wire ─────────────────────────────────────────────────────────────

    private static async Task<string> SendAsync(BridgeServer bridge, string line)
    {
        int port = int.Parse(bridge.Address.Split(':')[1], System.Globalization.CultureInfo.InvariantCulture);
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port);
        using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return (await reader.ReadLineAsync())!;
    }

    [Fact]
    public async Task Bridge_AnswersOneLinePerConnection_ChecksTheToken_AndCountsTheCalls()
    {
        var seen = new List<string>();
        await using var bridge = BridgeServer.Start((tool, args, _) => { seen.Add(tool + ":" + string.Join(",", args.Select(a => a.Key + "=" + a.Value))); return Task.FromResult(tool == "boom" ? "Error: boom failed" : "ok " + tool); }, maxCalls: 2);

        Assert.StartsWith("127.0.0.1:", bridge.Address);
        Assert.Equal(64, bridge.Token.Length);
        Assert.Equal("{\"result\":\"ok echo\"}", await SendAsync(bridge, "{\"token\":\"" + bridge.Token + "\",\"tool\":\"echo\",\"arguments\":{\"text\":\"hi\",\"n\":2}}"));
        Assert.Equal(["echo:text=hi,n=2"], seen);
        Assert.Equal("{\"error\":\"Error: boom failed\"}", await SendAsync(bridge, "{\"token\":\"" + bridge.Token + "\",\"tool\":\"boom\"}"));
        Assert.Equal("{\"error\":\"Error: this run's tool call limit (2) is reached\"}", await SendAsync(bridge, "{\"token\":\"" + bridge.Token + "\",\"tool\":\"echo\"}"));
        Assert.Equal(2, bridge.Calls);
        Assert.Equal("{\"error\":\"Error: the bridge token did not match\"}", await SendAsync(bridge, "{\"token\":\"nope\",\"tool\":\"echo\"}"));
        Assert.Equal("{\"error\":\"Error: the request is not {\\\"token\\\", \\\"tool\\\", \\\"arguments\\\"} on one line\"}", await SendAsync(bridge, "not json"));
        Assert.Equal("{\"error\":\"Error: the request is not {\\\"token\\\", \\\"tool\\\", \\\"arguments\\\"} on one line\"}", await SendAsync(bridge, "{\"token\":\"" + bridge.Token + "\"}"));
        Assert.Equal("{\"result\":\"it's \\\"quoted\\\"\"}", BridgeServer.Serialize(new BridgeResponse { Result = "it's \"quoted\"" }));   // readable on the wire and in the log
        Assert.Equal(2, bridge.Calls);   // the refused ones never counted
    }

    [Fact]
    public async Task Bridge_AThrowingDispatch_IsTheFailedSentence_AndTheArgumentsAreJsonElements()
    {
        await using var bridge = BridgeServer.Start((tool, args, _) => throw new InvalidOperationException("kaboom"), maxCalls: 5);
        Assert.Equal("{\"error\":\"Error: x failed: kaboom\"}", await SendAsync(bridge, "{\"token\":\"" + bridge.Token + "\",\"tool\":\"x\",\"arguments\":{}}"));

        var response = await bridge.AnswerAsync("{\"token\":\"" + bridge.Token + "\",\"tool\":\" y \",\"arguments\":{\"a\":[1,2]}}", CancellationToken.None);
        Assert.Equal("Error: y failed: kaboom", response.Error);
        Assert.Null(response.Result);
    }

    [Fact]
    public void CodeLaunch_LaysOutEachLanguage()
    {
        var python = CodeLaunch.Files(CodeLanguage.Python, "print(1)", @"C:\runs\code_1");
        Assert.Equal(("script.py", "print(1)", "neon_tools.py"), (python.ScriptName, python.ScriptText, python.ModulePath));
        Assert.Contains("def call(tool, **arguments):", python.ModuleText);
        var node = CodeLaunch.Files(CodeLanguage.Node, "x", @"C:\runs\code_1");
        Assert.Equal(("script.js", @"node_modules\neon_tools\index.js"), (node.ScriptName, node.ModulePath));
        Assert.Contains("module.exports", node.ModuleText);
        var ps = CodeLaunch.Files(CodeLanguage.PowerShell, "Write-Output 1", @"C:\runs\it's");
        Assert.Equal(("script.ps1", "NeonTools.psm1"), (ps.ScriptName, ps.ModulePath));
        Assert.Contains("Import-Module -Force 'C:\\runs\\it''s\\NeonTools.psm1'\nWrite-Output 1\n", ps.ScriptText);
        Assert.StartsWith("$ProgressPreference = 'SilentlyContinue'\n", ps.ScriptText);
        Assert.Contains("function Invoke-NeonTool", ps.ModuleText);

        var launch = CodeLaunch.For(CodeLanguage.Python, @"C:\py\python.exe", @"C:\runs\code_1", "script.py", @"D:\files", "127.0.0.1:5", "tok", "print(1)");
        Assert.Equal(["-X", "utf8", @"C:\runs\code_1\script.py"], launch.ArgumentList);
        Assert.Equal(@"D:\files", launch.WorkingDirectory);
        Assert.Equal("python", launch.Kind);
        Assert.Equal("print(1)", launch.Label);
        Assert.Equal(new Dictionary<string, string> { ["NEONSIDEKICK_BRIDGE_ADDRESS"] = "127.0.0.1:5", ["NEONSIDEKICK_BRIDGE_TOKEN"] = "tok" }, launch.Environment);
        Assert.Equal([@"C:\runs\code_1\script.js"], CodeLaunch.For(CodeLanguage.Node, "node.exe", @"C:\runs\code_1", "script.js", ".", "a", "t", "x").ArgumentList);
        Assert.Equal(["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", @"C:\runs\code_1\script.ps1"], CodeLaunch.For(CodeLanguage.PowerShell, "pwsh.exe", @"C:\runs\code_1", "script.ps1", ".", "a", "t", "x").ArgumentList);
    }

    [Fact]
    public void CodeLaunch_BridgeOff_NoModule_NoImport_NoEnvironment()
    {
        // Shell tool bridge off (later on 2026-09-21): the script alone, PowerShell's under the bare wrapper, and nothing in the environment.
        var python = CodeLaunch.Files(CodeLanguage.Python, "print(1)", @"C:\runs\code_1", bridge: false);
        Assert.Equal(("script.py", "print(1)"), (python.ScriptName, python.ScriptText));
        Assert.Null(python.ModulePath);
        Assert.Null(python.ModuleText);
        var node = CodeLaunch.Files(CodeLanguage.Node, "x", @"C:\runs\code_1", bridge: false);
        Assert.Equal(("script.js", "x"), (node.ScriptName, node.ScriptText));
        Assert.Null(node.ModulePath);
        var ps = CodeLaunch.Files(CodeLanguage.PowerShell, "Write-Output 1", @"C:\runs\it's", bridge: false);
        Assert.Equal("script.ps1", ps.ScriptName);
        Assert.Null(ps.ModulePath);
        Assert.Equal(ShellCommandLine.PowerShellScript("Write-Output 1"), ps.ScriptText);
        Assert.DoesNotContain("Import-Module", ps.ScriptText);

        var launch = CodeLaunch.For(CodeLanguage.Python, @"C:\py\python.exe", @"C:\runs\code_1", "script.py", @"D:\files", null, null, "print(1)");
        Assert.Empty(launch.Environment!);   // a dictionary, empty: no bridge variable
        Assert.Equal(["-X", "utf8", @"C:\runs\code_1\script.py"], launch.ArgumentList);
    }

    [Fact]
    public void ScriptText_Helpers_ArePinned()
    {
        Assert.Equal("exit 0 in 2.3 s (python, 3 tool calls): import os", ShellText.ScriptExitHeader("python", 0, TimeSpan.FromSeconds(2.34), 3, "import os"));
        Assert.Equal("timed out after 5 m 0 s (python, killed, 1 tool call): x", ShellText.ScriptTimedOutHeader("python", TimeSpan.FromMinutes(5), 1, "x"));
        // No clause with the bridge off (later on 2026-09-21): null, not zero.
        Assert.Equal("exit 0 in 2.3 s (python): import os", ShellText.ScriptExitHeader("python", 0, TimeSpan.FromSeconds(2.34), null, "import os"));
        Assert.Equal("exit 0 in 2.3 s (python, 0 tool calls): import os", ShellText.ScriptExitHeader("python", 0, TimeSpan.FromSeconds(2.34), 0, "import os"));
        Assert.Equal("timed out after 5 m 0 s (python, killed): x", ShellText.ScriptTimedOutHeader("python", TimeSpan.FromMinutes(5), null, "x"));
        Assert.Equal("execute_code: python \"import os\" → exit 0 in 2.3 s (2,340 chars)", ShellText.ScriptLogLine("python", "import os", "exit 0 in 2.3 s", null, 2340));
        Assert.Equal("import os", ShellText.FirstLine("\n  \n  import os  \nprint(1)"));
        Assert.Equal("(empty)", ShellText.FirstLine("  \n"));
        Assert.Equal("code:python", ShellText.ScriptPrefix("python"));
        Assert.Equal("Error: python is not installed (no python.exe found)", ShellText.LanguageNotInstalled(CodeLanguage.Python));
        Assert.Equal("bridge: read_file → 1,234 chars", ShellText.BridgeLogLine("read_file", new string('x', 1234)));
        Assert.Equal("bridge: nope → Error: unknown tool nope", ShellText.BridgeLogLine("nope", "Error: unknown tool nope"));
        Assert.Equal("execute_code: python \"import os\" → exit 0 in 2.3 s, 3 tool calls (2,340 chars)", ShellText.ScriptLogLine("python", "import os", "exit 0 in 2.3 s", 3, 2340));
    }
}
