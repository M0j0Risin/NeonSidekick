using Microsoft.Extensions.AI;
using NeonSidekick.App;
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
    public async Task ToolsOffered_ListsNeonHelp_InTheHelpGroup_InItsAlphabeticalPlace()
    {
        // Alphabetical since 2026-10-04 (the user's ask; between the timers and the files, where the turn offers it, until then).
        _settings.Update(d => d.TtsOutput = false);
        _console.Profile.Height = 260;   // 200 until the admin groups (later still on 2026-10-15) pushed Timers past the pane's foot
        _geometry = new ScreenGeometry(() => null);
        PushLine("/tools");
        _console.Input.PushKey(Keys.Escape);
        PushLine("/exit");

        string output = await RunAsync();

        int files = output.IndexOf("\n── Files · ", StringComparison.Ordinal);
        int help = output.IndexOf("\n── " + HelpText.GroupTitle + " · 1 ─", StringComparison.Ordinal);
        int timers = output.IndexOf("\n── Timers · 3 ─", StringComparison.Ordinal);
        Assert.True(files >= 0 && files < help && help < timers, $"files {files}, help {help}, timers {timers}");
        Assert.Contains("\n  " + NeonHelpTool.ToolName + " ", output);
    }

    [Fact]
    public async Task ToolsOffered_EnterOnNeonHelp_SwitchesItOff_AndTheNextTurnLeavesItOut()
    {
        _settings.Update(d => d.TtsOutput = false);
        _console.Profile.Height = 60;
        _geometry = new ScreenGeometry(() => null);
        PushLine("/tools");
        foreach (char c in NeonHelpTool.ToolName)
        {
            // The Offered tab's filter (2026-10-03): the one row left, under the cursor, wherever its group sorts.
            _console.Input.PushKey(Keys.Char(c));
        }

        _console.Input.PushKey(Keys.Enter);
        _console.Input.PushKey(Keys.Escape);   // clears the filter
        _console.Input.PushKey(Keys.Escape);
        _chat.EnqueueText("Hello.");
        PushLine("hi");
        PushLine("/exit");

        await RunAsync();

        Assert.Contains(NeonHelpTool.ToolName, _settings.Current.ToolsDisabled);
        Assert.DoesNotContain(NeonHelpTool.ToolName, OfferedNames());
        Assert.DoesNotContain(Assistant.HelpRule, _chat.Requests[0][0].Text!, StringComparison.Ordinal);
    }

    // /help <words> (2026-10-14, the user's ask): the model looks the words up with neon_help, or the manual answers itself.

    [Fact]
    public void HelpAskPrompt_IsPinned()
    {
        // What the model reads: the words first, in quotes, then what to do with them.
        Assert.Equal(
            "Look up \"claude\" in NeonSidekick's own manual with neon_help (search again with other words if the first answer misses) and answer from what it returns.",
            HelpText.AskPrompt(" claude "));
    }

    [Fact]
    public async Task HelpWithWords_AModelConnected_AsksItToLookThemUp()
    {
        _settings.Update(d => d.TtsOutput = false);
        _chat.Enqueue(FakeChatClient.Call("c1", NeonHelpTool.ToolName, new Dictionary<string, object?> { [NeonHelpTool.QueryArgument] = "claude" }));
        _chat.EnqueueText("/claude sends a message to Claude Code.");
        PushLine("/help claude");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Equal(HelpText.AskPrompt("claude"), _chat.Requests[0].Last(m => m.Role == ChatRole.User).Text);
        Assert.Contains(NeonHelpTool.ToolName, OfferedNames());
        Assert.Contains("sends a message to Claude Code", output, StringComparison.Ordinal);
        Assert.DoesNotContain(ChatScreen.NoArgumentError("/help"), output, StringComparison.Ordinal);
        Assert.DoesNotContain(HelpText.NotAskedNotice, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HelpWithWords_NeonHelpSwitchedOff_TheManualAnswers_AfterTheNotice()
    {
        _settings.Update(d => { d.TtsOutput = false; d.ToolsDisabled = [NeonHelpTool.ToolName]; });
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
    public async Task HelpWithWords_ToolsOff_TheManualAnswers_AfterTheNotice()
    {
        _settings.Update(d => { d.TtsOutput = false; d.LlmOfferTools = false; });
        PushLine("/help claude");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Empty(_chat.Requests);
        Assert.Contains(HelpText.NotAskedNotice, output, StringComparison.Ordinal);
        Assert.Contains("Command /claude:", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HelpWithWords_NoModel_TheManualAnswers_WithNoNotice()
    {
        _settings.Update(d => { d.TtsOutput = false; d.LlmScanMode = "off"; d.LlmUrl = ""; });
        PushLine("/help claude");
        PushLine("/exit");

        string output = await RunAsync();

        Assert.Empty(_chat.Requests);
        Assert.DoesNotContain(HelpText.NotAskedNotice, output, StringComparison.Ordinal);
        Assert.DoesNotContain(ChatScreen.NoAssistantError, output, StringComparison.Ordinal);
        Assert.Contains(TranscriptRenderer.HelpGlyph + "Command /claude:", output, StringComparison.Ordinal);
    }

    // The live line (2026-10-25, the neon_help plan's Step 2): a tool's answer says whether the next turn offers it, and why not.

    private async Task<string> ManualHelpAsync(string words, Action<NeonSidekick.Settings.AppSettingsData>? set = null)
    {
        _settings.Update(d => { d.TtsOutput = false; d.LlmScanMode = "off"; d.LlmUrl = ""; set?.Invoke(d); });
        _console.Profile.Width = 400;
        PushLine("/help " + words);
        PushLine("/exit");
        return await RunAsync();
    }

    [Fact]
    public async Task HelpTool_CameraToolOff_SaysSo()
    {
        string output = await ManualHelpAsync(CameraCaptureTool.ToolName, d => d.CameraTools = false);
        Assert.Contains(HelpText.LiveNotOffered(ToolsText.SwitchOffReason(SettingsField.CameraTools)), output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HelpTool_Offered_SaysSo()
    {
        string output = await ManualHelpAsync(WebFetchTool.ToolName, d => d.WebTools = true);
        Assert.Contains(HelpText.LiveOffered, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HelpTool_SwitchedOffByName_SaysWhere()
    {
        string output = await ManualHelpAsync(WebFetchTool.ToolName, d => { d.WebTools = true; d.ToolsDisabled = [WebFetchTool.ToolName]; });
        Assert.Contains(HelpText.LiveNotOffered(HelpText.LiveOffByName), output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HelpTool_ToolsOff_SaysSo()
    {
        string output = await ManualHelpAsync(WebFetchTool.ToolName, d => { d.WebTools = true; d.LlmOfferTools = false; });
        Assert.Contains(HelpText.LiveNotOffered(HelpText.LiveToolsOff), output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HelpTool_ClockToolsOff_GivesItsSwitch()
    {
        string output = await ManualHelpAsync(GetCurrentTimeTool.ToolName, d => d.ClockTools = false);
        Assert.Contains(HelpText.LiveNotOffered(ToolsText.SwitchOffReason(SettingsField.ClockTools)), output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HelpTool_FinishStep_OutsideAPass_SaysWhenItIsOffered()
    {
        string output = await ManualHelpAsync(FinishStepTool.ToolName);
        Assert.Contains("Right now: not offered (only during a Ralph loop pass", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Turn_TheModelAsksNeonHelp_AboutATool_AndReadsWhetherItIsOffered()
    {
        _settings.Update(d => { d.TtsOutput = false; d.CameraTools = false; });
        _chat.Enqueue(FakeChatClient.Call("c1", NeonHelpTool.ToolName, new Dictionary<string, object?> { [NeonHelpTool.QueryArgument] = CameraCaptureTool.ToolName }));
        _chat.EnqueueText("The camera tool is off.");
        PushLine("can you take a photo?");
        PushLine("/exit");

        await RunAsync();

        string result = Assert.Single(_chat.Requests[1].SelectMany(m => m.Contents.OfType<FunctionResultContent>()), r => r.CallId == "c1").Result!.ToString()!;
        Assert.StartsWith("Tool `" + CameraCaptureTool.ToolName + "`:", result, StringComparison.Ordinal);
        Assert.EndsWith(HelpText.LiveNotOffered(ToolsText.SwitchOffReason(SettingsField.CameraTools)), result, StringComparison.Ordinal);
    }

    /// <summary>The user's run (2026-10-25): the model asked for "camera", which is <c>/camera</c>; the answer carries the camera tool's live line too.</summary>
    [Fact]
    public async Task Turn_TheModelAsksNeonHelp_ForACommandsWord_ReadsTheGroupsLiveLineToo()
    {
        _settings.Update(d => { d.TtsOutput = false; d.CameraTools = false; });
        _chat.Enqueue(FakeChatClient.Call("c1", NeonHelpTool.ToolName, new Dictionary<string, object?> { [NeonHelpTool.QueryArgument] = "camera" }));
        _chat.EnqueueText("The camera tool is off.");
        PushLine("can you take a photo?");
        PushLine("/exit");

        await RunAsync();

        string result = Assert.Single(_chat.Requests[1].SelectMany(m => m.Contents.OfType<FunctionResultContent>()), r => r.CallId == "c1").Result!.ToString()!;
        Assert.StartsWith("Command /camera:", result, StringComparison.Ordinal);
        Assert.Contains(HelpText.LiveNotOffered(ToolsText.SwitchOffReason(SettingsField.CameraTools)), result, StringComparison.Ordinal);
    }

    [Fact]
    public void HelpLookupOffered_NeedsTheToolsAndNeonHelp()
    {
        var none = new HashSet<string>(StringComparer.Ordinal);
        Assert.True(ChatScreen.HelpLookupOffered(offerTools: true, helpTools: true, none));
        Assert.False(ChatScreen.HelpLookupOffered(offerTools: false, helpTools: true, none));
        Assert.False(ChatScreen.HelpLookupOffered(offerTools: true, helpTools: false, none));   // Help tools off (2026-10-18)
        Assert.False(ChatScreen.HelpLookupOffered(offerTools: true, helpTools: true, new HashSet<string>(StringComparer.Ordinal) { NeonHelpTool.ToolName }));
    }
}
