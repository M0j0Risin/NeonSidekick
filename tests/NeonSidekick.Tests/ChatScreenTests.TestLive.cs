using NeonSidekick.App;
using NeonSidekick.Bench;
using NeonSidekick.Sessions;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary><c>/test</c>'s clean slate and its live row (2026-09-30, the user's asks).</summary>
public partial class ChatScreenTests
{
    /// <summary>A run starts as <c>/clear</c> leaves things: the next message opens a new conversation and a new session row.</summary>
    [Fact]
    public async Task Test_StartsFromACleanSlate_TheConversationAndItsSessionForgotten()
    {
        _settings.Update(d => d.TtsOutput = false);
        _geometry = new ScreenGeometry(() => null);
        _chat.Enqueue(FakeChatClient.Text("Hi."), FakeChatClient.Usage(50, 5)).EnqueueText("Drawer.").EnqueueText("Ok.");
        LinesWhenIdle("hello", "/test mind", "after", "/exit");

        string output = await RunAsync();

        Assert.Contains("1/1 passed", output);
        Assert.Equal(["after"], UserLines(_chat.Requests[2]));   // the turn before the run is gone
        Assert.Equal(0, _session.Usage.LastRequest.Total);   // the hint row's figures went with the reset: the first turn's 55 tokens are not the context any more
        Assert.Equal(0, _session.Usage.Conversation.Total);
        using var store = OpenSessions();
        Assert.Equal(["after", "hello"], store.List(0).Select(s => s.Title).Order());   // the run forgot the session: a new row for the next message
    }

    /// <summary>Under a run the row is a reply's: <c>/tools</c> opens its pane over the run, a flip saves, and the run finishes and saves.</summary>
    [Fact]
    public async Task Test_UnderTheRun_APaneCommandOpensItsPane_AndTheRunFinishes()
    {
        _settings.Update(d => d.TtsOutput = false);
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        _chat.EnqueueText("Dra", "wer", ".");
        _chat.BeforeUpdate = async (i, _) =>
        {
            if (_chat.Requests.Count == 1)
            {
                if (i == 0)
                {
                    PushLine("/tools");
                }
                else if (i == 1)
                {
                    Scripted().Push(Keys.Enter);    // camera_capture off: the first row (the groups alphabetical since 2026-10-04, Camera first)
                    Scripted().Push(Keys.Escape);
                }

                await Task.Delay(40, CancellationToken.None);
            }
        };
        LinesWhenIdle("/test mind", "/exit");

        string output = await RunAsync();

        Assert.Contains(ToolsText.Label + "   Offered    Ask", output);
        Assert.Equal([NeonSidekick.Llm.Tools.CameraCaptureTool.ToolName], _settings.Current.ToolsDisabled);
        Assert.Contains("✓ mind", output);
        var run = Assert.Single(new BenchHistory(_settings.ProfileDirectory).Runs());
        Assert.False(run.Cancelled);
    }

    /// <summary><c>/clear</c> typed under a run stops it as ESC does: the finished tests shown and saved.</summary>
    [Fact]
    public async Task Test_ClearUnderTheRun_StopsIt_TheFinishedOnesKept()
    {
        _settings.Update(d => d.TtsOutput = false);
        _geometry = new ScreenGeometry(() => null);
        _chat.EnqueueText("German").EnqueueText("40");
        _chat.BeforeUpdate = async (i, ct) =>
        {
            if (_chat.Requests.Count == 2 && i == 0)
            {
                PushLine("/clear");   // typed while the second test runs
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(5, CancellationToken.None);
                }
            }
        };
        LinesWhenIdle("/test reasoning", "/exit");

        string output = await RunAsync();

        Assert.Contains(BenchText.Cancelled(1, 4), output);
        var run = Assert.Single(new BenchHistory(_settings.ProfileDirectory).Runs());
        Assert.True(run.Cancelled);
        Assert.Equal("grid", Assert.Single(run.Results).Test);
    }
}
