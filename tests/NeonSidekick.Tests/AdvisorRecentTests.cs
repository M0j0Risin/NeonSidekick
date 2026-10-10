using Microsoft.Extensions.AI;
using NeonSidekick.Advisors;
using NeonSidekick.App;
using NeonSidekick.Settings;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>Advisor context</c>'s words and the recent messages an advisor reads (2026-10-10, out of the Claude Code advisor tool's tests
/// when that tool was retired): the two words, an unknown one read as <c>brief</c>, the last ten messages as lines, and the picker's row.
/// </summary>
public class AdvisorRecentTests
{
    [Fact]
    public void Lines_TheLastTenMessages_NoSystem_NoEmptyParts()
    {
        var messages = new List<ChatMessage> { new(ChatRole.System, "sys") };
        for (int i = 0; i < 12; i++)
        {
            messages.Add(new ChatMessage(i % 2 == 0 ? ChatRole.User : ChatRole.Assistant, "m" + i));
        }

        messages.Add(new ChatMessage(ChatRole.Assistant, " "));

        var lines = AdvisorRecent.Lines(messages);

        Assert.Equal(9, lines.Count);   // the last ten messages, one of them blank
        Assert.Equal("assistant: m3", lines[0]);
        Assert.Equal("assistant: m11", lines[^1]);
    }

    [Fact]
    public void Lines_ACallByName_AResultCut()
    {
        string body = new('x', AdvisorText.ToolResultChars + 10);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "read_file")]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", body)]),
        };

        var lines = AdvisorRecent.Lines(messages);

        Assert.Equal(["assistant: (called read_file)", "tool: " + body[..AdvisorText.ToolResultChars] + "…"], lines);
    }

    [Fact]
    public void Context_TheTwoWords()
    {
        Assert.Equal(["brief", "recent"], AdvisorContextMode.Names);
        Assert.False(AdvisorContextMode.IsRecent("brief"));
        Assert.True(AdvisorContextMode.IsRecent(" Recent "));
        Assert.False(AdvisorContextMode.IsRecent("everything"));   // warned, read as brief
        Assert.Equal("brief", new AppSettingsData().AdvisorContext);
        Assert.Equal("those and the last 10 messages", AdvisorContextMode.Describe("recent"));
    }

    [Fact]
    public void ThePickersRow_AndTheCallsValue_ArePinned()
    {
        Assert.StartsWith("recent  ", SettingsMenu.AdvisorContextLabel("recent"), StringComparison.Ordinal);   // padded to eight
        Assert.Contains("the last 10 messages", SettingsMenu.AdvisorContextLabel("recent"), StringComparison.Ordinal);
        Assert.Equal("1 call", SettingsMenu.AdvisorCalls(1));
        Assert.Equal("2 calls", SettingsMenu.AdvisorCalls(2));
    }
}
