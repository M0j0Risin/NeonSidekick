using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Diagnostics;
using NeonSidekick.Mcp;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

// ── Headless --allow (2026-11-19, feature ideas #28 and #45) ──────────────

public partial class SidekickAppTests
{
    private void HeadlessPipe()
    {
        ServerOn1234("llama");
        Directory.CreateDirectory(NeonSidekick.Settings.Profiles.ConfigsDirectory(_settings.ProfileDirectory));
        File.WriteAllText(McpConfigFile.ProfilePath(_settings.ProfileDirectory), """{ "mcpServers": { "pipe": { "command": "pipe-server" } } }""");
        _settings.Update(d => d.McpServers = true);
    }

    private static List<string> CaptureLog(out Action<DiagnosticEvent> handler)
    {
        var lines = new List<string>();
        handler = e => { lock (lines) { lines.Add(e.Message); } };
        DiagnosticLog.Emitted += handler;
        return lines;
    }

    /// <summary>Without <c>--allow</c> an unmarked MCP call is refused (offered, never left out): the model's text, the summary line, exit 3.</summary>
    [Fact]
    public async Task Headless_AnUnmarkedMcpCall_IsRefused_InTheSummary_AndExits3()
    {
        HeadlessPipe();
        _chat.Enqueue(FakeChatClient.Call("c1", "pipe__echo", new Dictionary<string, object?> { ["text"] = "ping" }));
        _chat.EnqueueText("Refused, then.");

        string output = await Headless("echo ping\n", exitCode: SidekickApp.HeadlessRefusedExitCode);

        var offered = _chat.Options[0]!.Tools!.Cast<AIFunction>().Select(t => t.Name).ToArray();
        Assert.Contains("pipe__echo", offered);
        Assert.Contains("[tool] pipe__echo -> " + McpText.ErrorPrefix + McpText.NotAskedError("pipe__echo", McpText.HeadlessAllowTail("pipe")), output);
        Assert.Contains("[notice] " + AllowText.McpRefusedSummary(1, ["pipe__echo"], ["mcp:pipe"]), output);
        Assert.Empty(_mcpServers.Server("pipe").Calls);
    }

    /// <summary>
    /// <c>--allow mcp:pipe</c> runs it with an audit line; <c>--allow mcp:other</c> names no server, an error before anything connects, exit 2
    /// (2026-11-20, the mcp-ask review's #124). The first fails if the session's approver held the empty starting allowances, so it also
    /// guards the build order.
    /// </summary>
    [Fact]
    public async Task Headless_AllowMcpServer_RunsItsCalls_AndAnotherServersEntryDoesnt()
    {
        HeadlessPipe();
        _chat.Enqueue(FakeChatClient.Call("c1", "pipe__echo", new Dictionary<string, object?> { ["text"] = "ping" }));
        _chat.EnqueueText("It said ping.");
        var lines = CaptureLog(out var handler);
        string output;
        try
        {
            output = await Headless("echo ping\n", options: SidekickOptions.None with { AllowList = ["mcp:PIPE"] });
        }
        finally
        {
            DiagnosticLog.Emitted -= handler;
        }

        Assert.Equal([("echo", """{"text":"ping"}""")], _mcpServers.Server("pipe").Calls);
        Assert.DoesNotContain(AllowText.NamesNoServer("PIPE"), output);
        lock (lines)
        {
            Assert.Contains(lines, l => l.StartsWith(McpText.AuditPrefix("pipe__echo", AllowText.AllowedBy("mcp:PIPE")) + McpText.OutcomeRan + " · ", StringComparison.Ordinal));
        }

        int asked = _chat.Requests.Count;
        string other = await Headless("echo pong\n", options: SidekickOptions.None with { AllowList = ["mcp:other"] }, exitCode: SidekickApp.HeadlessBadAllowExitCode);
        Assert.Contains("[error] " + AllowText.NamesNoServer("other"), other);
        Assert.Single(_mcpServers.Server("pipe").Calls);   // the first run's call alone: the second never connected
        Assert.Equal(asked, _chat.Requests.Count);   // the second run never asked the model: it ended before the first line
    }

    /// <summary>#75 (2026-11-20): <c>mcp:PIPE</c> over <c>pipe</c> and <c>Pipe</c> (the home's) matches both without regard to case: exit 2, naming them.</summary>
    [Fact]
    public async Task Headless_AnAmbiguousMcpName_Exits2_NamingTheServers()
    {
        HeadlessPipe();
        Directory.CreateDirectory(NeonSidekick.Settings.Profiles.ConfigsDirectory(_settings.StorageDirectory));
        File.WriteAllText(McpConfigFile.GlobalPath(_settings.StorageDirectory), """{ "mcpServers": { "Pipe": { "command": "pipe-server" } } }""");

        string output = await Headless("echo\n", options: SidekickOptions.None with { AllowList = ["mcp:PIPE"] }, exitCode: SidekickApp.HeadlessBadAllowExitCode);

        Assert.Contains("[error] " + AllowText.Ambiguous("PIPE", ["pipe", "Pipe"]), output);
        Assert.Empty(_chat.Requests);
    }

    /// <summary>#124 (2026-11-20): a server skipped for a problem in its entry is still a name <c>--allow</c> knows, so its typo is the file's, not the flag's.</summary>
    [Fact]
    public async Task Headless_AllowNamingABrokenEntry_IsNoError()
    {
        HeadlessPipe();
        File.WriteAllText(McpConfigFile.ProfilePath(_settings.ProfileDirectory), """{ "mcpServers": { "pipe": { "command": "pipe-server" }, "broken": { "args": 7 } } }""");
        _chat.EnqueueText("Hello.");

        string output = await Headless("hi\n", options: SidekickOptions.None with { AllowList = ["mcp:broken"] });

        Assert.DoesNotContain(AllowText.NamesNoServer("broken"), output);
    }

    /// <summary>#110 (2026-11-20): an entry that lets nothing through under the run's settings says so at the start, and in the log.</summary>
    [Fact]
    public async Task Headless_AnAllowThatChangesNothing_IsANotice()
    {
        ServerOn1234("llama");
        _settings.Update(d => { d.PrintTools = true; d.PrintActionPolicy = NeonSidekick.Printing.PrintPolicy.Allow; d.DockerTools = false; });
        _chat.EnqueueText("Hello.");

        string output = await Headless("hi\n", options: SidekickOptions.None with { AllowList = ["print", "docker"] });

        Assert.Contains("[notice] " + AllowText.ChangesNothing("print", AllowText.PrintNotAsk), output);
        Assert.Contains("[notice] " + AllowText.ChangesNothing("docker", AllowText.DockerOff), output);
    }

    /// <summary>
    /// <c>--allow code:powershell</c> (2026-11-24, the user's choice): a script of that language runs under <c>ask</c>, the start logs why, and the
    /// profile's allow list is untouched. PowerShell is the interpreter every Windows machine has; the Mac's run is a box in the Mac to-do note.
    /// </summary>
    [WindowsFact]
    public async Task Headless_AllowCodeLanguage_RunsItsScripts_AndSavesNothing()
    {
        ServerOn1234("llama");
        _chat.Enqueue(FakeChatClient.Call("c1", "execute_code", new Dictionary<string, object?> { ["language"] = "powershell", ["code"] = "Write-Output 7" }));
        _chat.EnqueueText("It said 7.");
        var lines = CaptureLog(out var handler);
        string output;
        try
        {
            output = await Headless("run it\n", options: SidekickOptions.None with { AllowList = ["code:powershell"] });
        }
        finally
        {
            DiagnosticLog.Emitted -= handler;
        }

        Assert.Contains("[tool] execute_code -> exit 0", output);
        Assert.DoesNotContain("[notice] ", output);
        Assert.Empty(_settings.Current.ShellCommandAllowed);
        lock (lines)
        {
            Assert.Contains(AllowText.ScriptsAllowedLogLine(["code:powershell"]), lines);
        }
    }

    /// <summary>
    /// A script another entry doesn't cover is refused with the sentence naming its own <c>--allow</c>, summed up by it, exit 3; a script the
    /// police refuse is no <c>--allow</c> refusal (2026-11-24).
    /// </summary>
    [WindowsFact]
    public async Task Headless_AScriptNoEntryCovers_IsRefused_NamingItsEntry_AndExits3()
    {
        ServerOn1234("llama");
        _chat.Enqueue(FakeChatClient.Call("c1", "execute_code", new Dictionary<string, object?> { ["language"] = "powershell", ["code"] = "Write-Output 7" }));
        _chat.EnqueueText("Then not.");

        string output = await Headless("run it\n", options: SidekickOptions.None with { AllowList = ["code:python"] }, exitCode: SidekickApp.HeadlessRefusedExitCode);

        Assert.Contains("[tool] execute_code -> " + NeonSidekick.Shell.ShellText.ScriptNotAskable("powershell", ["code:python"]), output);
        Assert.Contains("[notice] " + AllowText.ScriptsRefusedSummary(1, ["code:powershell"]), output);

        _chat.Enqueue(FakeChatClient.Call("c2", "execute_code", new Dictionary<string, object?> { ["language"] = "powershell", ["code"] = "Get-Content C:\\Windows\\win.ini" }));
        _chat.EnqueueText("Then not.");
        output = await Headless("read it\n", exitCode: SidekickApp.HeadlessRefusedExitCode);
        Assert.DoesNotContain("allows it)", output);   // the police's refusal: --allow wouldn't lift it
    }

    /// <summary><c>--allow code</c> that changes nothing says so (2026-11-24): the policy off or yolo, or the language off in <i>Shell code languages</i>.</summary>
    [Fact]
    public async Task Headless_AnAllowCodeThatChangesNothing_IsANotice()
    {
        ServerOn1234("llama");
        _settings.Update(d => d.ShellCommandPolicy = "yolo");
        _chat.EnqueueText("Hello.");
        string output = await Headless("hi\n", options: SidekickOptions.None with { AllowList = ["code", "code:python"] });
        Assert.Contains("[notice] " + AllowText.ChangesNothing("code", AllowText.CodePolicyYolo), output);
        Assert.Contains("[notice] " + AllowText.ChangesNothing("code:python", AllowText.CodePolicyYolo), output);

        _settings.Update(d => d.ShellCommandPolicy = "off");
        _chat.EnqueueText("Hello.");
        output = await Headless("hi\n", options: SidekickOptions.None with { AllowList = ["code"] });
        Assert.Contains("[notice] " + AllowText.ChangesNothing("code", AllowText.CodePolicyOff), output);

        _settings.Update(d => { d.ShellCommandPolicy = "ask"; d.ShellCodeLanguages = ["powershell"]; });
        _chat.EnqueueText("Hello.");
        output = await Headless("hi\n", options: SidekickOptions.None with { AllowList = ["code:node", "code:powershell"] });
        Assert.Contains("[notice] " + AllowText.ChangesNothing("code:node", AllowText.LanguageOff("node")), output);
        Assert.DoesNotContain("code:powershell changes nothing", output);
        Assert.Equal("--allow code:node changes nothing: node isn't on in Shell code languages", AllowText.ChangesNothing("code:node", AllowText.LanguageOff("node")));
    }

    /// <summary>A Docker change headless: refused and counted without <c>--allow docker</c>; with it, the change goes on to the engine.</summary>
    [DockerHereFact]
    public async Task Headless_ADockerChange_IsCountedWithoutAllowDocker_AndRunsWithIt()
    {
        ServerOn1234("llama");
        _settings.Update(d => { d.DockerTools = true; d.DockerWrites = true; });
        _chat.Enqueue(FakeChatClient.Call("c1", NeonSidekick.Llm.Tools.DockerPullTool.ToolName, new Dictionary<string, object?> { ["image"] = "alpine" }));
        _chat.EnqueueText("Refused.");

        string refused = await Headless("pull alpine\n", exitCode: SidekickApp.HeadlessRefusedExitCode);
        Assert.Contains("[tool] docker_pull -> " + NeonSidekick.Docker.DockerText.NotAsked + "." + AllowText.NotAskedTail(AllowKind.Docker), refused);
        Assert.Contains("[notice] " + AllowText.RefusedSummary(AllowKind.Docker, 1), refused);

        _chat.Enqueue(FakeChatClient.Call("c2", NeonSidekick.Llm.Tools.DockerPullTool.ToolName, new Dictionary<string, object?> { ["image"] = "alpine" }));
        _chat.EnqueueText("The engine is away.");
        string allowed = await Headless("pull alpine\n", options: SidekickOptions.None with { AllowList = ["docker"] });
        Assert.DoesNotContain(NeonSidekick.Docker.DockerText.NotAsked, allowed);
        Assert.DoesNotContain(AllowText.RefusedSummary(AllowKind.Docker, 1), allowed);
    }

    /// <summary>A call a policy refuses outright never reaches the confirm: nothing counted, exit 0 (here Docker writes off).</summary>
    [DockerHereFact]
    public async Task Headless_AChangeAPolicyRefuses_IsNotCounted()
    {
        ServerOn1234("llama");
        _settings.Update(d => { d.DockerTools = true; d.DockerWrites = true; d.ToolsDisabled = []; });
        _chat.Enqueue(FakeChatClient.Call("c1", NeonSidekick.Llm.Tools.DockerPullTool.ToolName, new Dictionary<string, object?> { ["image"] = "alpine" }));
        _chat.EnqueueText("Off.");
        _settings.Update(d => d.DockerWrites = false);   // the tool isn't offered then: the call is an unknown tool, refused by the loop, never counted

        string output = await Headless("pull alpine\n");

        Assert.DoesNotContain("[notice] " + AllowText.RefusedSummary(AllowKind.Docker, 1), output);
    }
}
