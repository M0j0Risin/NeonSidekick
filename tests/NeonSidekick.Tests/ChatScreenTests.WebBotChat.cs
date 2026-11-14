using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
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
/// ladder at the PC — the bot cut short, then the chat ended — with the PC's hint row flashed; with no chat it is a Conflict. The status row's
/// 👥 (2026-11-14) carries a stopped chat on as the PC's Ctrl+Alt+B does.
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

    /// <summary>
    /// The status row's 👥 (2026-11-14, the user's ask): nothing to resume before a chat; once the page's ⏭️ has stopped one, offered and carried
    /// on as the PC's Ctrl+Alt+B does — the chat's own notice, no "/botchat --resume" row on either screen, nothing in the history, the PC's hint
    /// row flashed — a second tap meanwhile queueing nothing; refused (and not offered) under Web UI commands off; not offered while the chat
    /// runs, and offered again once it stops.
    /// </summary>
    [Fact]
    public async Task AResumeFromThePage_CarriesTheStoppedChatOn_AsTheChordDoes_NoRowNoHistory()
    {
        BotChatFixture();
        _settings.Update(d => { d.WebUiCommands = true; d.BotChatNonTtsDelaySeconds = 5; });   // the manual clock never ends the pause
        _chat.EnqueueText("One.");
        _chat.EnqueueText("Two.");
        _chat.EnqueueText("never");
        var flashed = new ConcurrentQueue<string>();
        var tap = new WebBotResume();
        bool offeredBefore = true, offeredOff = true, offeredRunning = true, offeredAfter = false;
        WebReply before = default, refused = default, first = default, second = default;
        var skips = Task.Run(async () =>
        {
            await WaitForOutputAsync("One.");
            await Task.Delay(300);
            SkipWhenTaken();   // the first chat ends in its pause
            int resumed = await WaitForOutputAsync(BotChat.ResumeNotice(["neon", "ada"], "pizza", 1));
            await WaitForOutputAsync("Two.", Math.Max(resumed, 0));
            offeredRunning = Web.Snapshot().State.BotResume;
            await Task.Delay(300);
            SkipWhenTaken();   // and the resumed one in its
        });
        var input = Scripted();
        int step = 0;
        input.OnWait = () =>
        {
            if (step == 0)
            {
                step++;
                Web.FlashOnPc = flashed.Enqueue;
                offeredBefore = Web.Snapshot().State.BotResume;
                before = Web.ResumeBot(tap, IPAddress.Loopback);
                SendFromPage("/botchat pizza");
            }
            else if (step == 1 && Output.Contains(BotChat.StoppedNotice(1), StringComparison.Ordinal) && Web.Snapshot().State.BotResume)
            {
                step++;
                _settings.Update(d => d.WebUiCommands = false);
                offeredOff = Web.Snapshot().State.BotResume;
                refused = Web.ResumeBot(tap, IPAddress.Loopback);
                _settings.Update(d => d.WebUiCommands = true);
                first = Web.ResumeBot(tap, IPAddress.Loopback);
                second = Web.ResumeBot(tap, IPAddress.Loopback);
            }
            else if (step == 2 && Output.Contains(BotChat.StoppedNotice(2), StringComparison.Ordinal) && Web.Snapshot().State.BotResume)
            {
                step++;
                offeredAfter = true;
                PushLine(input, "/exit");
            }
        };

        string output = await RunAsync(input);
        await skips;

        Assert.False(offeredBefore);
        Assert.Equal((WebOutcome.Conflict, BotChat.NothingToResumeError), (before.Outcome, before.Message));
        Assert.False(offeredOff);
        Assert.Equal(WebOutcome.Refused, refused.Outcome);
        Assert.Equal((WebOutcome.Ok, WebOutcome.Ok), (first.Outcome, second.Outcome));
        Assert.False(offeredRunning);
        Assert.True(offeredAfter);

        // The resumed chat went on with ada, once: the second tap queued nothing.
        Assert.Equal(2, _chat.Requests.Count);
        Assert.Contains(AdaMarker, SystemText(_chat.Requests[1]));
        Assert.Single(Regex.Matches(output, Regex.Escape(BotChat.ResumeNotice(["neon", "ada"], "pizza", 1))));

        // As the chord runs it: no row of its own on either screen, nothing in the history; the PC's hint row says what the page did.
        Assert.DoesNotContain(BotChat.ResumeLine, output);
        Assert.DoesNotContain(Web.Feed.Snapshot(m => m), e => e.Kind == "user" && e.Text.Contains(BotChat.ResumeLine, StringComparison.Ordinal));
        Assert.Contains(Web.Feed.Snapshot(m => m), e => e.Kind == "user" && e.Text.Contains("/botchat pizza", StringComparison.Ordinal));
        Assert.DoesNotContain(BotChat.ResumeLine, Web.History().Lines);
        Assert.Contains("/botchat pizza", Web.History().Lines);
        Assert.Equal(1, flashed.Count(f => f == WebUiText.BotResumedFlash));
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
