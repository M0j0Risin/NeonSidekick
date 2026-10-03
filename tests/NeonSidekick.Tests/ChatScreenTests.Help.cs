using Microsoft.Extensions.AI;
using NeonSidekick.Help;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary><c>neon_help</c> on the screen (2026-10-02): offered on every turn beside the clock, its rule with it, listed on <c>/tools</c>' Offered tab and switched off there.</summary>
public partial class ChatScreenTests
{
    private string[] OfferedNames(int request = 0) => _chat.Options[request]!.Tools!.Cast<AIFunction>().Select(t => t.Name).ToArray();

    [Fact]
    public async Task Turn_OffersNeonHelp_AfterTheTimers_AndTheRuleSaysToCallIt()
    {
        _settings.Update(d => d.TtsOutput = false);
        _chat.EnqueueText("Hello.");
        PushLine("hi");
        PushLine("/exit");

        await RunAsync();

        var offered = OfferedNames();
        Assert.Equal(Array.IndexOf(offered, ListTimersTool.ToolName) + 1, Array.IndexOf(offered, NeonHelpTool.ToolName));
        Assert.Contains(Assistant.HelpRule, _chat.Requests[0][0].Text!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Turn_NeonHelpSwitchedOffInTools_IsNotOffered_AndItsRuleGoes()
    {
        _settings.Update(d => { d.TtsOutput = false; d.ToolsDisabled = [NeonHelpTool.ToolName]; });
        _chat.EnqueueText("Hello.");
        PushLine("hi");
        PushLine("/exit");

        await RunAsync();

        Assert.DoesNotContain(NeonHelpTool.ToolName, OfferedNames());
        Assert.DoesNotContain(Assistant.HelpRule, _chat.Requests[0][0].Text!, StringComparison.Ordinal);
        Assert.Contains(Assistant.FileRule, _chat.Requests[0][0].Text!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Turn_TheModelAsksNeonHelp_AndReadsTheCommandsForms()
    {
        _settings.Update(d => d.TtsOutput = false);
        _chat.Enqueue(FakeChatClient.Call("c1", NeonHelpTool.ToolName, new Dictionary<string, object?> { [NeonHelpTool.QueryArgument] = "/camera" }));
        _chat.EnqueueText("Use /camera snap.");
        PushLine("how do I take a photo?");
        PushLine("/exit");

        await RunAsync();

        string result = Assert.Single(_chat.Requests[1].SelectMany(m => m.Contents.OfType<FunctionResultContent>()), r => r.CallId == "c1").Result!.ToString()!;
        Assert.StartsWith("Command /camera:", result, StringComparison.Ordinal);
        Assert.Contains("\n- /camera snap: ", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolsOffered_ListsNeonHelp_InTheHelpGroup_BetweenTheTimersAndTheFiles()
    {
        _settings.Update(d => d.TtsOutput = false);
        _console.Profile.Height = 60;
        _geometry = new ScreenGeometry(() => null);
        PushLine("/tools");
        _console.Input.PushKey(Keys.Escape);
        PushLine("/exit");

        string output = await RunAsync();

        int timers = output.IndexOf("\n── Timers · 3 ─", StringComparison.Ordinal);
        int help = output.IndexOf("\n── " + HelpText.GroupTitle + " · 1 ─", StringComparison.Ordinal);
        int files = output.IndexOf("\n── Files · ", StringComparison.Ordinal);
        Assert.True(timers >= 0 && timers < help && help < files, $"timers {timers}, help {help}, files {files}");
        Assert.Contains("\n  " + NeonHelpTool.ToolName + " ", output);
    }

    [Fact]
    public async Task ToolsOffered_EnterOnNeonHelp_SwitchesItOff_AndTheNextTurnLeavesItOut()
    {
        _settings.Update(d => d.TtsOutput = false);
        _console.Profile.Height = 60;
        _geometry = new ScreenGeometry(() => null);
        PushLine("/tools");
        for (int i = 0; i < 6; i++)
        {
            // From get_current_time: the two clock rows, the three timers, neon_help (the gaps and the headings no stops, 2026-10-03).
            _console.Input.PushKey(Keys.Down);
        }

        _console.Input.PushKey(Keys.Enter);
        _console.Input.PushKey(Keys.Escape);
        _chat.EnqueueText("Hello.");
        PushLine("hi");
        PushLine("/exit");

        await RunAsync();

        Assert.Contains(NeonHelpTool.ToolName, _settings.Current.ToolsDisabled);
        Assert.DoesNotContain(NeonHelpTool.ToolName, OfferedNames());
        Assert.DoesNotContain(Assistant.HelpRule, _chat.Requests[0][0].Text!, StringComparison.Ordinal);
    }
}
