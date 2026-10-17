using System.Net;
using System.Text.Json;
using NeonSidekick.App;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The reflection's 🧠 on the Web UI's page (2026-10-17, the user's ask: a reflection could hold the server with no sign on the page, and
/// no way to stop it from there): the status row carries it while one runs, the page's cancel ends it as Ctrl+L does — one canceled notice,
/// the PC's hint row flashed — and with none running the cancel is a Conflict that writes nothing.
/// </summary>
public partial class ChatScreenTests
{
    [Fact]
    public async Task ThePagesBrain_ShowsWhileAReflectionRuns_AndItsCancelEndsIt_AsCtrlLDoes()
    {
        _settings.Update(d => d.TtsOutput = false);
        _console.Profile.Height = 40;
        _chat.EnqueueText("Hello.");
        _chat.Enqueue(FakeChatClient.Call("r1", SkillEditorTool.ToolName, CreateSkillArgs("greeting")));   // request 1: the reflection, held until cancelled
        _chat.BeforeUpdateOf = async (r, i, token) => { if (r == 1 && i == 0) await Task.Delay(Timeout.Infinite, token); };
        var flashed = new System.Collections.Concurrent.ConcurrentQueue<string>();
        WebEventHub.Subscription? page = null;
        WebReply cancel = default;
        WebReply again = default;
        var input = Scripted();
        int step = 0;
        input.OnWait = () =>
        {
            switch (step)
            {
                case 0:
                    step++;
                    page = Web.Events.Subscribe();
                    Web.FlashOnPc = flashed.Enqueue;
                    PushLine(input, "hi");
                    break;
                case 1: step++; PushLine(input, "/learn keep the greeting"); break;
                case 2:
                    step++;
                    WaitForRequests(2);
                    Assert.True(_session.IsLearning);
                    _time.Advance(WebUiSession.StateInterval);   // the sampler's tick: the row with the 🧠
                    cancel = Web.CancelLearning(IPAddress.Loopback);
                    break;
                case 3:
                    if (_session.Learning is { IsCompleted: true } && Output.Contains(ChatScreen.LearnCancelledNotice, StringComparison.Ordinal))
                    {
                        step++;
                        _time.Advance(WebUiSession.StateInterval);   // the row without it
                        again = Web.CancelLearning(IPAddress.Loopback);   // nothing runs now
                        SpinWait.SpinUntil(() => !flashed.IsEmpty, TimeSpan.FromSeconds(10));
                        PushLine(input, "/exit");
                    }

                    break;
            }
        };

        string output = await RunAsync();

        Assert.Equal(WebOutcome.Ok, cancel.Outcome);
        Assert.Equal((WebOutcome.Conflict, WebUiText.NoReflection), (again.Outcome, again.Message));
        Assert.Equal(1, CountOf(output, ChatScreen.LearnCancelledNotice));
        Assert.DoesNotContain("learned:", output);
        Assert.Contains(WebUiText.LearnCancelledFlash, flashed);
        var statuses = new List<WebStatus>();
        while (page!.Reader.TryRead(out var item))
        {
            if (item.Name == "status")
            {
                statuses.Add(JsonSerializer.Deserialize(item.Json.Value, WebUiJsonContext.Default.WebStatus)!);
            }
        }

        int on = statuses.FindIndex(s => s.Learning);
        Assert.True(on >= 0, "no status carried the 🧠");
        Assert.Contains(statuses.Skip(on + 1), s => !s.Learning);
        page.Dispose();
    }
}
