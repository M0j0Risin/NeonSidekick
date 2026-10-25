using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Help;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;

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

    // The live line headless (2026-10-25, the neon_help plan's Step 2), no server: the manual answers with headless's own reasons.

    [Fact]
    public async Task Headless_HelpTool_AToolThatNeedsTheScreen_SaysAHeadlessRunNeverOffersIt()
    {
        _settings.Update(d => d.CameraTools = true);
        string output = await Headless("/help " + CameraCaptureTool.ToolName + "\n");
        Assert.Contains(HelpText.LiveNotOffered(HelpText.LiveNotHeadless), output);
    }

    [Fact]
    public async Task Headless_HelpTool_ServiceControl_IsNeverHeadless()
    {
        _settings.Update(d => { d.ServiceTools = true; d.ServiceWrites = true; });
        string output = await Headless("/help " + ServiceControlTool.ToolName + "\n");
        Assert.Contains(OperatingSystem.IsWindows() ? HelpText.LiveNotOffered(HelpText.LiveNotHeadless) : HelpText.NothingFound(ServiceControlTool.ToolName), output);
    }

    [Fact]
    public async Task Headless_HelpTool_EventLogs_TheSwitchFirst_ThenAReader()
    {
        _settings.Update(d => d.EventLogTools = false);
        string off = await Headless("/help eventlog_search\n");
        Assert.Contains(HelpText.LiveNotOffered(ToolsText.SwitchOffReason(SettingsField.EventLogTools)), off);

        _settings.Update(d => d.EventLogTools = true);
        string on = await Headless("/help eventlog_search\n");
        Assert.Contains(HelpText.LiveNotOffered(ToolsText.NoEventLogsReason), on);
    }

    [Fact]
    public async Task Headless_HelpTool_SqlWithNoConnection_NamesTheFile()
    {
        _settings.Update(d => d.SqlTools = true);
        string output = await Headless("/help sql_query\n");
        Assert.Contains(HelpText.LiveNotOffered(ToolsText.NoneOfferedReason("connection", "sql.json")), output);
    }

    [Fact]
    public async Task Headless_HelpTool_PingWithNoPinger_SaysSo()
    {
        _settings.Update(d => d.NetworkTools = true);
        string output = await Headless("/help " + NetPingTool.ToolName + "\n");
        Assert.Contains(HelpText.LiveNotOffered(ToolsText.NoPingReason), output);
    }

    /// <summary>The variables <c>neon_help</c> names a setting for are exactly those <c>OverriddenBy</c> names (2026-10-25).</summary>
    [Fact]
    public void EnvironmentFields_AreExactlyWhatOverriddenByNames()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [EnvironmentOverrides.LlmUrlVariable] = "http://x:1/v1", [EnvironmentOverrides.LlmModelVariable] = "m", [EnvironmentOverrides.LlmApiKeyVariable] = "k",
            [EnvironmentOverrides.RequestTimeoutVariable] = "10", [EnvironmentOverrides.TurnTimeoutVariable] = "20", [EnvironmentOverrides.TtsUrlVariable] = "http://t",
            [EnvironmentOverrides.TtsVoiceVariable] = "af_heart", [EnvironmentOverrides.TtsSpeedVariable] = "1.5", [EnvironmentOverrides.WhisperModelVariable] = "ggml-tiny.en.bin",
            [EnvironmentOverrides.LlmReasoningVariable] = "high", [EnvironmentOverrides.TtsVoice2Variable] = "af_sky", [EnvironmentOverrides.TtsMixVariable] = "70",
            [EnvironmentOverrides.InterruptEchoVariable] = "80", [EnvironmentOverrides.InterruptConfirmVariable] = "600", [EnvironmentOverrides.LlmContextVariable] = "32768",
            [EnvironmentOverrides.SearxngUrlVariable] = "http://box:8080", [EnvironmentOverrides.CommandPolicyVariable] = "yolo", [EnvironmentOverrides.ShellPoliceVariable] = "off",
            [EnvironmentOverrides.ShellNativeVariable] = "no", [EnvironmentOverrides.ObsidianVaultVariable] = @"D:\Notes", [EnvironmentOverrides.ComfyUrlVariable] = "http://gpu:8188",
            [EnvironmentOverrides.ClaudeCodeExeVariable] = @"D:\bin\claude.exe", [EnvironmentOverrides.ClaudeCodePermissionsVariable] = "read-write",
            [EnvironmentOverrides.AnthropicApiVariable] = "on", [EnvironmentOverrides.AnthropicApiKeyVariable] = "sk-ant", [EnvironmentOverrides.LlmSamplingVariable] = "{\"temperature\":0.3}",
            [EnvironmentOverrides.ClaudeCodeServerVariable] = "on", [EnvironmentOverrides.DockerPipeVariable] = "npipe:////./pipe/x", [EnvironmentOverrides.OpenAIApiVariable] = "yes",
            [EnvironmentOverrides.OpenAIApiKeyVariable] = "sk-o", [EnvironmentOverrides.YouTubeApiKeyVariable] = "AIza", [EnvironmentOverrides.AdvisorsVariable] = "on",
        };
        var app = App(env: new EnvironmentOverrides(name => values.GetValueOrDefault(name)));

        foreach (var (variable, field) in EnvironmentOverrides.Fields)
        {
            Assert.Equal(variable, app.OverriddenBy(field));
        }

        foreach (var field in Enum.GetValues<SettingsField>().Where(f => f is not (SettingsField.EmbeddedLlmServer or SettingsField.TtsVoicePreset)))
        {
            if (app.OverriddenBy(field) is { } by && by.StartsWith(EnvironmentOverrides.Prefix, StringComparison.Ordinal))
            {
                Assert.Equal(field, EnvironmentOverrides.Fields[by]);
            }
        }
    }
}
