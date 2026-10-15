using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>Timer tools</c> (later on 2026-10-15, the user's ask: a Timers tab on <c>/tools</c> › Core): off, a turn offers no timer tool and its
/// prompt no timer sentence, the clock stays; <c>/timer</c> keeps working; <c>/tools timers</c> opens the switch; the Offered group names it.
/// </summary>
public partial class ChatScreenTests
{
    [Fact]
    public async Task Turn_TimerToolsOff_OffersNoTimerTool_AndDropsTheTimerSentence_TheClockStays()
    {
        _settings.Update(d => { d.TtsOutput = false; d.TimerTools = false; });
        _chat.EnqueueText("Hello.");
        PushLine("hi");
        PushLine("/exit");

        await RunAsync();

        var offered = _chat.Options[0]!.Tools!.Cast<AIFunction>().Select(t => t.Name).ToArray();
        Assert.DoesNotContain(offered, n => n is StartTimerTool.ToolName or StopTimerTool.ToolName or ListTimersTool.ToolName);
        Assert.Contains(GetCurrentTimeTool.ToolName, offered);
        Assert.DoesNotContain(Assistant.TimerRule, _chat.Requests[0][0].Text!);
        Assert.Equal(SkilledPrompt(false, Array.Empty<string>(), web: true, timers: false), _chat.Requests[0][0].Text);
    }

    [Fact]
    public async Task Turn_TimerToolsOn_OffersTheThree()
    {
        _settings.Update(d => d.TtsOutput = false);
        Assert.True(_settings.Current.TimerTools);   // the default
        _chat.EnqueueText("Hello.");
        PushLine("hi");
        PushLine("/exit");

        await RunAsync();

        var offered = _chat.Options[0]!.Tools!.Cast<AIFunction>().Select(t => t.Name).ToArray();
        Assert.Contains(StartTimerTool.ToolName, offered);
        Assert.Contains(StopTimerTool.ToolName, offered);
        Assert.Contains(ListTimersTool.ToolName, offered);
        Assert.Contains(Assistant.TimerRule, _chat.Requests[0][0].Text!);
    }

    [Fact]
    public async Task TimerCommand_WithTimerToolsOff_StillStartsATimer()
    {
        _settings.Update(d => d.TimerTools = false);
        PushLine("/timer 10m cooking");
        PushLine("/timer");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("  · " + CookingStarted, output);
        Assert.Contains("  · ⏰ cooking: 10 minutes left of 10 minutes (done at 14:15)", output);
        Assert.Empty(_chat.Requests);
    }

    [Fact]
    public async Task ToolsTimers_WithoutThePane_PrintsTheSwitch()
    {
        _settings.Update(d => { d.TtsOutput = false; d.TimerTools = false; });
        PushLine("/tools timers");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("  · Timer tools: off\n", output);
        Assert.Equal(SettingsField.TimerTools, ToolsText.SwitchField("Timers"));
        Assert.Equal("Timer tools", ToolsText.DescribeSwitch(ToolsText.TimersSwitchWord));
    }
}
