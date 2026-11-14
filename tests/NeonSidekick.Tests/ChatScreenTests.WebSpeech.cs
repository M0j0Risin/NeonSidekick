using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using NeonSidekick.App;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The Chat tab's spoken replies on the screen (2026-11-13, the webui-voice plan): a reply speaks where its message came from. A page's line
/// with its 🔊 on speaks on that page (clips announced to it alone, never the PC's device); with 🔊 off it's silent, never moved to the PC; a
/// PC line speaks at the PC under TTS output. A <c>/botchat</c> the page started speaks there, each bot in its own voice, the next bot waiting
/// for the page to play the last one out.
/// </summary>
public partial class ChatScreenTests
{
    private WebEventHub.Subscription? _phoneStream;

    private void WaitSpeakReady() =>
        Assert.True(SpinWait.SpinUntil(() => Web.Snapshot().State.VoiceSpeak == "", TimeSpan.FromSeconds(20)), "the page's speech never got ready");

    private void SendFromPhone(string text, bool speak) =>
        Assert.Equal(WebOutcome.Ok, Web.Send(new WebSendRequest { Text = text, Page = PhonePage, Speak = speak }, IPAddress.Loopback).Outcome);

    private static List<WebVoiceClip> ClipsOf(IEnumerable<WebEvent> seen) =>
        [.. seen.Where(e => e.Name == "voice-clip").Select(e => JsonSerializer.Deserialize(e.Json.Value, WebUiJsonContext.Default.WebVoiceClip)!)];

    private static void DrainInto(WebEventHub.Subscription stream, List<WebEvent> seen)
    {
        while (stream.Reader.TryRead(out var item))
        {
            seen.Add(item);
        }
    }

    [Fact]
    public async Task APagesLine_WithItsSpeakerOn_SpeaksOnThatPage_NeverAtThePc()
    {
        WebVoiceOn();
        _chat.EnqueueText("One.");
        var seen = new List<WebEvent>();
        var input = WebSteps(
            _ =>
            {
                WaitSpeakReady();
                _phoneStream = Web.Events.Subscribe(PhonePage, PhoneLoad);
                SendFromPhone("hi", speak: true);
            },
            i =>
            {
                // The manual clock cuts the voice's last clip (150 ms after the last write).
                Assert.True(SpinWait.SpinUntil(() =>
                {
                    _time.Advance(TimeSpan.FromMilliseconds(50));
                    DrainInto(_phoneStream!, seen);
                    return ClipsOf(seen).Count > 0;
                }, TimeSpan.FromSeconds(20)), "no clip reached the page");
                PushLine(i, "/exit");
            });

        await RunAsync(input);
        _phoneStream?.Dispose();

        Assert.Equal(["One."], _synth.SpokenText);
        Assert.Empty(_playback.Writes);
        Assert.Equal(0, _playback.Started);
        var clip = Assert.Single(ClipsOf(seen));
        Assert.True(WebVideoPlayer.IsId(clip.Turn));
        Assert.All(seen.Where(e => e.Name == "voice-clip"), e => Assert.Equal(PhonePage, e.Page));
    }

    [Fact]
    public async Task APagesLine_WithItsSpeakerOff_IsSilent_EvenWithTtsOutputOn()
    {
        WebVoiceOn();
        _settings.Update(d => d.TtsOutput = true);
        _chat.EnqueueText("One.");
        var input = WebSteps(
            _ =>
            {
                WaitSpeakReady();
                SendFromPhone("hi", speak: false);
            },
            i => PushLine(i, "/exit"));

        await RunAsync(input);

        Assert.Empty(_synth.SpokenText);   // never moved to the PC
        Assert.Empty(_playback.Writes);
    }

    [Fact]
    public async Task APcLine_UnderWebUiVoice_SpeaksAtThePc_AndNothingReachesThePage()
    {
        WebVoiceOn();
        _settings.Update(d => d.TtsOutput = true);
        _chat.EnqueueText("One.");
        var seen = new List<WebEvent>();
        var input = WebSteps(
            i =>
            {
                WaitSpeakReady();
                _phoneStream = Web.Events.Subscribe(PhonePage, PhoneLoad);
                Web.Voice!.Saw(PhonePage, true);   // the page's 🔊 is on: it still never takes a PC line's voice
                PushLine(i, "hi");
            },
            i =>
            {
                Assert.True(SpinWait.SpinUntil(() => _playback.Writes.Count > 0, TimeSpan.FromSeconds(20)), "the PC never spoke");
                _time.Advance(TimeSpan.FromSeconds(1));
                DrainInto(_phoneStream!, seen);
                PushLine(i, "/exit");
            });

        await RunAsync(input);
        _phoneStream?.Dispose();

        Assert.Equal(["One."], _synth.SpokenText);
        Assert.Empty(ClipsOf(seen));
    }

    [Fact]
    public async Task ABotChatFromThePage_SpeaksThere_EachBotInItsOwnVoice_TheNextWaitingForThePage()
    {
        BotChatFixture();
        WebVoiceOn();
        _settings.Update(d => d.WebUiCommands = true);
        _synth.PcmBytesPerChunk = WebAudioPlayback.FirstClipBytes;   // one clip per bot's line, cut at once
        _chat.EnqueueText("Hello from Neon.");
        _chat.EnqueueText("Hello from Ada.");
        _chat.EnqueueText("never");
        _chat.EnqueueText("never");
        var clips = new ConcurrentQueue<WebVoiceClip>();
        using var stop = new CancellationTokenSource();
        Task? page = null;
        _chat.BeforeUpdate = async (i, ct) =>
        {
            // The third bot's turn, before a word: ⏭️ cuts it short; the fourth's: ⏭️ again ends the chat.
            if ((_chat.Requests.Count == 3 || _chat.Requests.Count == 4) && i == 0)
            {
                SkipWhenTaken();
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(5, CancellationToken.None);
                }
            }
        };
        var input = WebSteps(
            _ =>
            {
                WaitSpeakReady();
                _phoneStream = Web.Events.Subscribe(PhonePage, PhoneLoad);
                var stream = _phoneStream;
                var voice = Web.Voice!;
                // The page: fetches each clip and reports it played out, so the next bot's voice can begin.
                page = Task.Run(async () =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        while (stream.Reader.TryRead(out var item))
                        {
                            if (item.Name != "voice-clip")
                            {
                                continue;
                            }

                            var clip = JsonSerializer.Deserialize(item.Json.Value, WebUiJsonContext.Default.WebVoiceClip)!;
                            clips.Enqueue(clip);
                            Assert.NotNull(voice.Clip(PhonePage, clip.Turn, clip.N));
                            voice.Played(new WebVoicePlayed { Page = PhonePage, Turn = clip.Turn, Clip = clip.N, Offset = clip.Bytes / 2 }, IPAddress.Loopback);
                        }

                        await Task.Delay(5);
                    }
                });
                SendFromPhone("/botchat", speak: true);
            },
            i => PushLine(i, "/exit"));

        await RunAsync(input);
        await stop.CancelAsync();
        if (page is not null)
        {
            await page;
        }

        _phoneStream?.Dispose();
        Assert.Equal(4, _chat.Requests.Count);
        Assert.Empty(_playback.Writes);
        Assert.Equal(["Hello from Neon.", "Hello from Ada."], _synth.SpokenText);
        Assert.StartsWith("bm_george", _synth.Spoken[1].Voice, StringComparison.Ordinal);   // ada's own voice (its blend)
        Assert.NotEqual(_synth.Spoken[0].Voice, _synth.Spoken[1].Voice);
        Assert.Equal(2, clips.Select(c => c.Turn).Distinct().Count());   // a speaker (and turn id) per bot
    }
    [Fact]
    public async Task APhoneListen_UnderABotChat_HoldsTheChat_AndWhatItHeardJoinsIt()
    {
        BotChatFixture();
        WebVoiceOn();
        _recognizer.Text = "hello bots";
        _vad.EndAfterBuffers = 1;
        _chat.EnqueueText("Hello from Neon.");
        _chat.EnqueueText("cut short");
        _chat.EnqueueText("Answering ", "you.");
        _chat.EnqueueText("never");
        WebReply start = default;
        _chat.BeforeUpdate = async (i, ct) =>
        {
            if (_chat.Requests.Count == 2 && i == 0)
            {
                // The phone's 🎤 under ada's reply: the reply is cut short, and the chat waits for the listen.
                var voice = Web.Voice!;
                start = voice.Start(new WebVoiceStart { Page = PhonePage, Listen = PhoneListen, Speak = false }, IPAddress.Loopback);
                voice.Chunk(new WebVoiceChunk { Page = PhonePage, Listen = PhoneListen, Seq = 0, Pcm = Convert.ToBase64String(new byte[3200]) }, IPAddress.Loopback);
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(5, CancellationToken.None);
                }
            }
            else if ((_chat.Requests.Count == 3 && i == 1) || (_chat.Requests.Count == 4 && i == 0))
            {
                SkipWhenTaken();   // ends the chat: the third bot cut short, then the fourth before a word
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(5, CancellationToken.None);
                }
            }
        };
        var input = WebSteps(
            i =>
            {
                Assert.True(SpinWait.SpinUntil(() => Web.Snapshot().State.VoiceListen == "", TimeSpan.FromSeconds(20)), "the voice models never got ready");
                _phoneStream = Web.Events.Subscribe(PhonePage, PhoneLoad);
                PushLine(i, "/botchat");
            },
            i => PushLine(i, "/exit"));

        await RunAsync(input);
        _phoneStream?.Dispose();

        Assert.Equal((WebOutcome.Ok, "listening"), (start.Outcome, start.Listen));
        Assert.Equal(4, _chat.Requests.Count);
        Assert.Contains(BotChat.UserName + ": hello bots", _chat.Requests[2][^1].Text);   // the next bot answers what the phone heard
    }

    /// <summary>
    /// A phone's 🎤 under a botchat that loses the listen's lock to another listen (2026-11-14, the review's #13/#17): its capture never
    /// starts, so the bot replying is never cut short and the chat goes on without a hold.
    /// </summary>
    [Fact]
    public async Task APhoneListen_RefusedBeforeItStarted_CutsNoBotShort()
    {
        BotChatFixture();
        WebVoiceOn();
        _chat.EnqueueText("Hello from Neon.");
        _chat.EnqueueText("Ada goes on.");
        _chat.EnqueueText("Answering ", "you.");
        _chat.EnqueueText("never");
        WebReply start = default;
        bool cut = false;
        string? ended = null;
        using var occupy = new CancellationTokenSource();
        _chat.BeforeUpdate = async (i, ct) =>
        {
            if (_chat.Requests.Count == 2 && i == 0)
            {
                // Another listen takes the lock between the start's checks and the page's listen: the page's is refused as busy.
                _running!.VoiceStartRaceForTests = () =>
                {
                    var blocker = new FakeAudioCapture { OnStart = async (_, token) => await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false) };
                    _ = _voice.ListenOnAsync(blocker, CancellationToken.None, null, occupy.Token, VoiceSession.PageOptions with { DeliveryWatchdog = TimeSpan.FromMinutes(1) });
                };
                start = Web.Voice!.Start(new WebVoiceStart { Page = PhonePage, Listen = PhoneListen, Speak = false }, IPAddress.Loopback);
                var seen = new List<WebEvent>();
                SpinWait.SpinUntil(() =>
                {
                    DrainInto(_phoneStream!, seen);
                    ended = VoicePhasesOf(seen).LastOrDefault(p => p.Phase != "listening")?.Phase;
                    return ended is not null;
                }, TimeSpan.FromSeconds(20));
                occupy.Cancel();
                await Task.Delay(50, CancellationToken.None);
                cut = ct.IsCancellationRequested;
            }
            else if ((_chat.Requests.Count == 3 && i == 1) || (_chat.Requests.Count == 4 && i == 0))
            {
                SkipWhenTaken();   // ends the chat: the third bot cut short, then the fourth before a word
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(5, CancellationToken.None);
                }
            }
        };
        var input = WebSteps(
            i =>
            {
                Assert.True(SpinWait.SpinUntil(() => Web.Snapshot().State.VoiceListen == "", TimeSpan.FromSeconds(20)), "the voice models never got ready");
                _phoneStream = Web.Events.Subscribe(PhonePage, PhoneLoad);
                PushLine(i, "/botchat");
            },
            i => PushLine(i, "/exit"));

        await RunAsync(input);
        _phoneStream?.Dispose();

        Assert.Equal((WebOutcome.Ok, "listening"), (start.Outcome, start.Listen));
        Assert.Equal("failed", ended);
        Assert.False(cut);
        Assert.Contains("Ada goes on.", string.Join('\n', _chat.Requests[2].Select(m => m.Text)));
    }
}
