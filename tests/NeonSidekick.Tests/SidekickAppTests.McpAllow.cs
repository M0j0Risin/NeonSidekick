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
        Assert.Contains("[notice] " + SidekickOptions.McpRefusedSummary(1, ["pipe__echo"], ["mcp:pipe"]), output);
        Assert.Empty(_mcpServers.Server("pipe").Calls);
    }

    /// <summary>
    /// <c>--allow mcp:pipe</c> runs it with an audit line; <c>--allow mcp:other</c> doesn't, and says it names no server. The first fails if
    /// the session's approver held the empty starting allowances, so it also guards the build order.
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
        Assert.DoesNotContain(SidekickOptions.AllowNamesNoServer("PIPE"), output);
        lock (lines)
        {
            Assert.Contains(lines, l => l.StartsWith("pipe__echo " + McpText.AllowedBy("mcp:PIPE") + ": ", StringComparison.Ordinal));
        }

        _chat.Enqueue(FakeChatClient.Call("c2", "pipe__echo", new Dictionary<string, object?> { ["text"] = "pong" }));
        _chat.EnqueueText("Refused.");
        string other = await Headless("echo pong\n", options: SidekickOptions.None with { AllowList = ["mcp:other"] }, exitCode: SidekickApp.HeadlessRefusedExitCode);
        Assert.Contains("[notice] " + SidekickOptions.AllowNamesNoServer("other"), other);
        Assert.Empty(_mcpServers.Server("pipe").Calls);   // a fresh server for the second run, never called
    }

    /// <summary>A Docker change headless: refused and counted without <c>--allow docker</c>; with it, the change goes on to the engine.</summary>
    [Fact]
    public async Task Headless_ADockerChange_IsCountedWithoutAllowDocker_AndRunsWithIt()
    {
        if (NeonSidekick.Docker.DockerSocket.Here == NeonSidekick.Docker.DockerTransport.None)
        {
            return;
        }

        ServerOn1234("llama");
        _settings.Update(d => { d.DockerTools = true; d.DockerWrites = true; });
        _chat.Enqueue(FakeChatClient.Call("c1", NeonSidekick.Llm.Tools.DockerPullTool.ToolName, new Dictionary<string, object?> { ["image"] = "alpine" }));
        _chat.EnqueueText("Refused.");

        string refused = await Headless("pull alpine\n", exitCode: SidekickApp.HeadlessRefusedExitCode);
        Assert.Contains("[tool] docker_pull -> " + NeonSidekick.Docker.DockerText.NotAsked, refused);
        Assert.Contains("[notice] " + SidekickOptions.RefusedSummary(AllowKind.Docker, 1, "docker"), refused);

        _chat.Enqueue(FakeChatClient.Call("c2", NeonSidekick.Llm.Tools.DockerPullTool.ToolName, new Dictionary<string, object?> { ["image"] = "alpine" }));
        _chat.EnqueueText("The engine is away.");
        string allowed = await Headless("pull alpine\n", options: SidekickOptions.None with { AllowList = ["docker"] });
        Assert.DoesNotContain(NeonSidekick.Docker.DockerText.NotAsked, allowed);
        Assert.DoesNotContain(SidekickOptions.RefusedSummary(AllowKind.Docker, 1, "docker"), allowed);
    }

    /// <summary>A call a policy refuses outright never reaches the confirm: nothing counted, exit 0 (here Docker writes off).</summary>
    [Fact]
    public async Task Headless_AChangeAPolicyRefuses_IsNotCounted()
    {
        if (NeonSidekick.Docker.DockerSocket.Here == NeonSidekick.Docker.DockerTransport.None)
        {
            return;
        }

        ServerOn1234("llama");
        _settings.Update(d => { d.DockerTools = true; d.DockerWrites = true; d.ToolsDisabled = []; });
        _chat.Enqueue(FakeChatClient.Call("c1", NeonSidekick.Llm.Tools.DockerPullTool.ToolName, new Dictionary<string, object?> { ["image"] = "alpine" }));
        _chat.EnqueueText("Off.");
        _settings.Update(d => d.DockerWrites = false);   // the tool isn't offered then: the call is an unknown tool, refused by the loop, never counted

        string output = await Headless("pull alpine\n");

        Assert.DoesNotContain("[notice] " + SidekickOptions.RefusedSummary(AllowKind.Docker, 1, "docker"), output);
    }
}
