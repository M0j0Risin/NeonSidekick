using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Help;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>Clock tools</c> and <c>Help tools</c> (2026-10-18, the user's asks: a Clock tab after Shell and a Help tab after Ask on <c>/tools</c> ›
/// Core): off, a turn offers none of that group and its prompt says nothing of it — no clock sentence and no opening clock call, no
/// manual's sentence — the "use a tool" sentence and the other groups staying; <c>/tools clock</c> and <c>/tools help</c> open the switches;
/// <c>/help &lt;words&gt;</c> answers from the manual while Help tools is off.
/// </summary>
public partial class ChatScreenTests
{
    [Fact]
    public async Task Turn_ClockToolsOff_OffersNoClockTool_NoClockSentence_AndNoOpeningClockCall()
    {
        _settings.Update(d => { d.TtsOutput = false; d.ClockTools = false; });
        _chat.EnqueueText("Hello.");
        PushLine("hi");
        PushLine("/exit");

        await RunAsync();

        var offered = OfferedNames();
        Assert.DoesNotContain(offered, n => n is GetCurrentTimeTool.ToolName or ShiftDateTool.ToolName or DateDifferenceTool.ToolName);
        Assert.Contains(StartTimerTool.ToolName, offered);
        Assert.Contains(NeonHelpTool.ToolName, offered);
        string prompt = _chat.Requests[0][0].Text!;
        Assert.DoesNotContain(Assistant.ClockRule, prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(GetCurrentTimeTool.ToolName, prompt, StringComparison.Ordinal);
        Assert.Contains(Assistant.ToolUseRule + " " + Assistant.TimerRule, prompt, StringComparison.Ordinal);
        Assert.Equal(SkilledPrompt(false, Array.Empty<string>(), web: true, clock: false), prompt);
        Assert.DoesNotContain(_chat.Requests[0], m => m.Contents.OfType<FunctionCallContent>().Any(c => c.CallId == Assistant.OpeningClockCallId));
        Assert.Contains(_chat.Requests[0], m => m.Contents.OfType<FunctionCallContent>().Any(c => c.CallId == Assistant.OpeningCwdCallId));
    }

    [Fact]
    public async Task Turn_ClockToolsOn_OffersTheThree_AndThePromptIsAsBefore()
    {
        _settings.Update(d => d.TtsOutput = false);
        Assert.True(_settings.Current.ClockTools);   // the default
        _chat.EnqueueText("Hello.");
        PushLine("hi");
        PushLine("/exit");

        await RunAsync();

        var offered = OfferedNames();
        Assert.Contains(GetCurrentTimeTool.ToolName, offered);
        Assert.Contains(ShiftDateTool.ToolName, offered);
        Assert.Contains(DateDifferenceTool.ToolName, offered);
        Assert.Contains(Assistant.ToolRules, _chat.Requests[0][0].Text!, StringComparison.Ordinal);
        Assert.Equal(SkilledPrompt(false, Array.Empty<string>(), web: true), _chat.Requests[0][0].Text);
        Assert.Contains(_chat.Requests[0], m => m.Contents.OfType<FunctionCallContent>().Any(c => c.CallId == Assistant.OpeningClockCallId));
    }

    [Fact]
    public async Task ToolsClock_WithoutThePane_PrintsTheSwitch()
    {
        _settings.Update(d => { d.TtsOutput = false; d.ClockTools = false; });
        PushLine("/tools clock");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("  · Clock tools: off\n", output);
        Assert.Equal(SettingsField.ClockTools, ToolsText.SwitchField("Clock"));
        Assert.Equal("Clock tools", ToolsText.DescribeSwitch(ToolsText.ClockSwitchWord));
    }

    [Fact]
    public async Task Turn_HelpToolsOff_OffersNoNeonHelp_AndItsRuleGoes_TheClockStays()
    {
        _settings.Update(d => { d.TtsOutput = false; d.HelpTools = false; });
        _chat.EnqueueText("Hello.");
        PushLine("hi");
        PushLine("/exit");

        await RunAsync();

        var offered = OfferedNames();
        Assert.DoesNotContain(NeonHelpTool.ToolName, offered);
        Assert.Contains(GetCurrentTimeTool.ToolName, offered);
        Assert.DoesNotContain(Assistant.HelpRule, _chat.Requests[0][0].Text!, StringComparison.Ordinal);
        Assert.Equal(SkilledPrompt(false, Array.Empty<string>(), web: true, help: false), _chat.Requests[0][0].Text);
    }

    [Fact]
    public async Task HelpWithWords_HelpToolsOff_TheManualAnswers_AfterTheNotice()
    {
        _settings.Update(d => { d.TtsOutput = false; d.HelpTools = false; });
        PushLine("/help claude");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Empty(_chat.Requests);
        int notice = output.IndexOf(HelpText.NotAskedNotice, StringComparison.Ordinal);
        int answer = output.IndexOf(TranscriptRenderer.HelpGlyph, StringComparison.Ordinal);
        Assert.True(notice >= 0 && notice < answer, $"notice {notice}, answer {answer}");
        Assert.Contains("Command /claude:", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolsHelp_WithoutThePane_PrintsTheSwitch()
    {
        _settings.Update(d => { d.TtsOutput = false; d.HelpTools = false; });
        PushLine("/tools help");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Contains("  · Help tools: off\n", output);
        Assert.Equal(SettingsField.HelpTools, ToolsText.SwitchField("Help"));
        Assert.Equal("Help tools", ToolsText.DescribeSwitch(ToolsText.HelpSwitchWord));
    }

    [Fact]
    public void ToolGroups_ClockAndHelpToolsOff_TheGroupsNameTheirSwitches_AndAreNotOffered()
    {
        var groups = SystemPromptSummary.ToolGroups(ChatScreen.ClockTools(TimeProvider.System), [], [], [], memoryEnabled: false, help: ChatScreen.HelpTools(), clockEnabled: false, helpEnabled: false);

        var clock = Assert.Single(groups, g => g.Label == ToolsText.ClockMenuTitle);
        var help = Assert.Single(groups, g => g.Label == HelpText.GroupTitle);
        Assert.False(clock.Offered);
        Assert.False(help.Offered);
        Assert.Equal(SettingsField.ClockTools, clock.Switch);
        Assert.Equal(SettingsField.HelpTools, help.Switch);
        Assert.Contains(SystemPromptSummary.ClockOffSuffix, clock.Note, StringComparison.Ordinal);
        Assert.Contains(SystemPromptSummary.HelpOffSuffix, help.Note, StringComparison.Ordinal);
        // With the timers off too, nothing is offered: the toolbar's 🛠️ goes on the slab by the switches alone, no name unticked.
        var none = new HashSet<string>(StringComparer.Ordinal);
        var all = SystemPromptSummary.ToolGroups(ChatScreen.ClockTools(TimeProvider.System), [], [], [], memoryEnabled: false, help: ChatScreen.HelpTools(), clockEnabled: false, helpEnabled: false, timersEnabled: false);
        Assert.True(ToolsText.NoneOffered(new ToolsFacts(all, true, none)));
        var on = SystemPromptSummary.ToolGroups(ChatScreen.ClockTools(TimeProvider.System), [], [], [], memoryEnabled: false, help: ChatScreen.HelpTools(), timersEnabled: false);
        Assert.False(ToolsText.NoneOffered(new ToolsFacts(on, true, none)));
    }
}
