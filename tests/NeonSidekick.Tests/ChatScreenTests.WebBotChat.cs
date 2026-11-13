using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Camera;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>/botchat</c> from the Web UI's page (2026-11-12, plan what-would-it-take-tingly-ullman): it runs, its speaker lines carry each bot's
/// palette slot, the PC's webcam stays off (a notice says so, and the bots are never told of a picture), and the status row's ⏭️ is ESC's
/// ladder at the PC — the bot cut short, then the chat ended — with the PC's hint row flashed; with no chat it is a Conflict.
/// </summary>
public partial class ChatScreenTests
{
    [Fact]
    public async Task ABotChatFromThePage_RunsWithItsSpeakersSlots_NoCamera_AndSkipIsEscsLadder()
    {
        BotChatFixture();
        _cameraSystem = new FakeCameraSystem();
        _settings.Update(d => { d.WebUiCommands = true; d.BotChatCamera = true; });
        _chat.EnqueueText("Hello from Neon.");
        _chat.EnqueueText("Hello from Ada.");
        _chat.EnqueueText("Neon ", "again");
        _chat.EnqueueText("never");
        var flashed = new ConcurrentQueue<string>();
        var skips = new ConcurrentQueue<WebReply>();
        WebReply before = default;
        WebReply after = default;
        _chat.BeforeUpdate = async (i, ct) =>
        {
            // The third reply, mid-stream: ⏭️ cuts neon short. The fourth, before a word: ⏭️ again ends the chat.
            if ((_chat.Requests.Count == 3 && i == 1) || (_chat.Requests.Count == 4 && i == 0))
            {
                skips.Enqueue(SkipWhenTaken());
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(5, CancellationToken.None);
                }
            }
        };
        var input = WebSteps(
            _ =>
            {
                Web.FlashOnPc = flashed.Enqueue;
                before = Web.SkipBot(IPAddress.Loopback);
                SendFromPage("/botchat the best pizza");
            },
            i =>
            {
                after = Web.SkipBot(IPAddress.Loopback);
                PushLine(i, "/exit");
            });

        string output = await RunAsync(input);

        Assert.Equal(4, _chat.Requests.Count);
        Assert.Equal([WebOutcome.Ok, WebOutcome.Ok], skips.Select(s => s.Outcome));
        Assert.Contains("  · " + BotChat.CutShortNotice("neon") + "\n", output);
        Assert.Equal((WebOutcome.Conflict, WebUiText.NothingToSkip), (before.Outcome, before.Message));
        Assert.Equal((WebOutcome.Conflict, WebUiText.NothingToSkip), (after.Outcome, after.Message));
        Assert.Equal(2, flashed.Count(f => f == WebUiText.BotSkippedFlash));

        // Each speaker line carries its bot's slot: neon 0, ada 1.
        var speakers = Web.Feed.Snapshot(m => m).Where(e => e.Kind == "speaker").ToList();
        Assert.Equal([0, 1, 0, 1], speakers.Select(e => e.Speaker));

        // The PC's webcam stayed off for a chat the page started, and no bot was told of a picture.
        Assert.Contains(CameraText.BotChatNotFromWeb, output);
        Assert.DoesNotContain(CameraText.BotChatOn, output);
        Assert.Equal(0, _cameraSystem.Opens);
        Assert.All(_chat.Requests, request =>
        {
            Assert.DoesNotContain(BotChat.CameraRule, request.Single(m => m.Role == ChatRole.System).Text, StringComparison.Ordinal);
            Assert.Empty(request[^1].Contents.OfType<DataContent>());
        });
        Assert.Empty(_pcOnlyPanes);
    }

    [Fact]
    public async Task ASkipFromThePage_InTheReadingPause_EndsTheChat_AsEscDoes()
    {
        BotChatFixture();
        _settings.Update(d => { d.WebUiCommands = true; d.BotChatNonTtsDelaySeconds = 5; });
        _chat.EnqueueText("Hello from Neon.");
        _chat.EnqueueText("never");
        WebReply skip = default;
        var press = Task.Run(async () =>
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!Output.Contains("Hello from Neon.", StringComparison.Ordinal) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }

            await Task.Delay(300);   // the reply has ended: the chat is in the pause, and the manual clock never ends it
            skip = SkipWhenTaken();
        });
        var input = WebSteps(_ => SendFromPage("/botchat"), i => PushLine(i, "/exit"));

        await RunAsync(input);
        await press;

        Assert.Equal(WebOutcome.Ok, skip.Outcome);
        Assert.Single(_chat.Requests);   // ada never spoke
        Assert.Empty(_pcOnlyPanes);
    }

    /// <summary>The page's ⏭️, tapped again while the chat is between two waits (Conflict, nothing pressed) until a watch takes it.</summary>
    private WebReply SkipWhenTaken()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var reply = Web.SkipBot(IPAddress.Loopback);
            if (reply.Outcome == WebOutcome.Ok || DateTime.UtcNow > deadline)
            {
                return reply;
            }

            Thread.Sleep(5);
        }
    }
}
