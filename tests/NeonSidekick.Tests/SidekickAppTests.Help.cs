using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Help;
using NeonSidekick.Llm.Tools;

namespace NeonSidekick.Tests;

/// <summary><c>/help &lt;words&gt;</c> headless (2026-10-14), as the screen does: the model looks the words up, or the manual answers itself.</summary>
public partial class SidekickAppTests
{
    [Fact]
    public async Task Headless_HelpWithWords_AModelConnected_AsksItToLookThemUp()
    {
        ServerOn1234("llama");
        _chat.EnqueueText("It sends a message to Claude Code.");

        string output = await Headless("/help claude\n");

        Assert.Equal(HelpText.AskPrompt("claude"), _chat.Requests[0].Last(m => m.Role == ChatRole.User).Text);
        Assert.Contains("Neon: It sends a message to Claude Code.", output);
        Assert.DoesNotContain(HelpText.NotAskedNotice, output);
    }

    [Fact]
    public async Task Headless_HelpWithWords_NoServer_TheManualAnswers()
    {
        string output = await Headless("/help claude\n");

        Assert.Contains("Neon: Command /claude:", output);
        Assert.DoesNotContain(SidekickApp.HeadlessNoAssistantReply, output);
        Assert.DoesNotContain(HelpText.NotAskedNotice, output);
        Assert.Empty(_chat.Requests);
    }

    [Fact]
    public async Task Headless_HelpWithWords_NeonHelpSwitchedOff_TheManualAnswers_AfterTheNotice()
    {
        ServerOn1234("llama");
        _settings.Update(d => d.ToolsDisabled = [NeonHelpTool.ToolName]);

        string output = await Headless("/help claude\n");

        int notice = output.IndexOf("[notice] " + HelpText.NotAskedNotice, StringComparison.Ordinal);
        int answer = output.IndexOf("Neon: Command /claude:", StringComparison.Ordinal);
        Assert.True(notice >= 0 && notice < answer, $"notice {notice}, answer {answer}");
        Assert.Empty(_chat.Requests);
    }
}
