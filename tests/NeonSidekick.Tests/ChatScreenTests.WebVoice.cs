using System.Net;
using System.Text.Json;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The Chat tab's 🎤 on the screen (2026-11-13, the webui-voice plan): refused with voice off; with it on, the PC readies the models for the
/// page alone, a page's listen runs over its own audio (the PC microphone untouched), and what it heard joins the page's queue as a message or
/// goes back to that page's box, its phases told to that page alone.
/// </summary>
public partial class ChatScreenTests
{
    private const string PhonePage = "phone-0123456789abcdef";
    private const string PhoneLoad = "load-0123456789abcdef";
    private const string PhoneListen = "listen-0123456789abcdef";

    private void WebVoiceOn(string destination = "chat")
    {
        FakeModelFiles.WriteBoth(ModelsDir, "ggml-base.en.bin");
        _webUiHost = _ => new RecordingHost();
        _settings.Update(d =>
        {
            d.TtsOutput = false;
            d.SttInput = false;
            d.WebUi = true;
            d.WebUiReach = "local";
            d.WebUiVoice = true;
            d.SttWhisperModel = "ggml-base.en.bin";
            d.SttDestination = destination;
        });
    }

    private static List<WebVoicePhase> VoicePhasesOf(List<WebEvent> seen) =>
        [.. seen.Where(e => e.Name == "voice").Select(e => JsonSerializer.Deserialize(e.Json.Value, WebUiJsonContext.Default.WebVoicePhase)!)];

    /// <summary>Waits for the PC's models, starts a listen from the page, sends one chunk, and waits for the listen's end on the page's stream.</summary>
    private List<WebVoicePhase> ListenFromThePhone() => ListenFromThePhoneUntil();

    /// <summary><see cref="ListenFromThePhone"/>, also ending at <paramref name="alsoEnd"/> when given (a canceled listen).</summary>
    private List<WebVoicePhase> ListenFromThePhoneUntil(string? alsoEnd = null)
    {
        Assert.True(SpinWait.SpinUntil(() => Web.Snapshot().State.VoiceListen == "", TimeSpan.FromSeconds(20)), "the voice models never got ready");
        var voice = Web.Voice!;
        var seen = new List<WebEvent>();
        using var stream = Web.Events.Subscribe(PhonePage, PhoneLoad);
        _vad.EndAfterBuffers = 1;

        var start = voice.Start(new WebVoiceStart { Page = PhonePage, Listen = PhoneListen, Speak = false }, IPAddress.Loopback);
        Assert.Equal((WebOutcome.Ok, "listening"), (start.Outcome, start.Listen));
        var chunk = voice.Chunk(new WebVoiceChunk { Page = PhonePage, Listen = PhoneListen, Seq = 0, Pcm = Convert.ToBase64String(new byte[3200]) }, IPAddress.Loopback);
        Assert.Equal(WebOutcome.Ok, chunk.Outcome);

        Assert.True(SpinWait.SpinUntil(() =>
        {
            while (stream.Reader.TryRead(out var item))
            {
                seen.Add(item);
            }

            return VoicePhasesOf(seen).Any(p => p.Phase is "sent" or "heard" or "failed" or "nothing" || p.Phase == alsoEnd);
        }, TimeSpan.FromSeconds(20)), "the listen never ended");
        return VoicePhasesOf(seen);
    }

    [Fact]
    public async Task WithWebUiVoiceOff_ThePagesMic_IsHidden_AndAListenIsRefused()
    {
        _webUiHost = _ => new RecordingHost();
        _settings.Update(d => { d.TtsOutput = false; d.WebUi = true; d.WebUiReach = "local"; });
        WebReply refused = default;
        string? listenState = "unset";
        var input = WhenIdle(
            i =>
            {
                listenState = Web.Snapshot().State.VoiceListen;
                refused = Web.Voice!.Start(new WebVoiceStart { Page = PhonePage, Listen = PhoneListen }, IPAddress.Loopback);
                PushLine(i, "/exit");   // nothing else wakes the read
            });

        await RunAsync(input);

        Assert.Null(listenState);
        Assert.Equal((WebOutcome.Refused, WebUiText.VoiceOff), (refused.Outcome, refused.Message));
        Assert.Empty(_recognizer.Received);
    }

    [Fact]
    public async Task APhoneListen_UnderChat_SendsWhatItHeard_AsAMessage_WithThePcMicrophoneUntouched()
    {
        WebVoiceOn();
        _recognizer.Text = "/clear what the phone heard";
        _chat.EnqueueText("Heard you.");
        List<WebVoicePhase>? phases = null;
        var input = WhenIdle(
            _ => phases = ListenFromThePhone(),
            i => PushLine(i, "/exit"));

        await RunAsync(input);

        Assert.Equal(["listening", "transcribing", "sent"], phases!.Select(p => p.Phase));
        Assert.All(phases!, p => Assert.Equal(PhoneListen, p.Listen));
        Assert.Equal("/clear what the phone heard", LastUser(_chat.Requests[0]));   // a message, never a command
        Assert.Equal(0, _capture.Started);
    }

    [Fact]
    public async Task APhoneListen_UnderDraft_HandsTheTextBackToThatPage()
    {
        WebVoiceOn(destination: "draft");
        _recognizer.Text = "buy milk";
        List<WebVoicePhase>? phases = null;
        var input = WhenIdle(
            i =>
            {
                phases = ListenFromThePhone();
                PushLine(i, "/exit");   // the text went to the page: nothing wakes the read
            });

        await RunAsync(input);

        var heard = Assert.Single(phases!, p => p.Phase == "heard");
        Assert.Equal("buy milk", heard.Text);
        Assert.Empty(_chat.Requests);
    }

    /// <summary>
    /// A page that joined while voice was off (2026-11-14, the review's #52): its entry, made at the listen, still learns the stream's run,
    /// so the 3 s stall rule ends a listen whose chunks stopped, rather than leaving it to run on.
    /// </summary>
    [Fact]
    public async Task APageThatJoinedBeforeVoiceWasOn_StillHasItsStalledListenFinished()
    {
        WebVoiceOn();
        _recognizer.Text = "after the stall";
        _chat.EnqueueText("Heard you.");
        var phases = new List<WebVoicePhase>();
        var input = WhenIdle(
            _ =>
            {
                Assert.True(SpinWait.SpinUntil(() => Web.Snapshot().State.VoiceListen == "", TimeSpan.FromSeconds(20)), "the voice models never got ready");
                _settings.Update(d => d.WebUiVoice = false);
                var seen = new List<WebEvent>();
                using var stream = Web.Events.Subscribe(PhonePage, PhoneLoad);   // joined with voice off: no entry made
                _settings.Update(d => d.WebUiVoice = true);
                var voice = Web.Voice!;
                Assert.Equal(WebOutcome.Ok, voice.Start(new WebVoiceStart { Page = PhonePage, Listen = PhoneListen }, IPAddress.Loopback).Outcome);
                Assert.Equal(WebOutcome.Ok, voice.Chunk(new WebVoiceChunk { Page = PhonePage, Listen = PhoneListen, Seq = 0, Pcm = Convert.ToBase64String(new byte[3200]) }, IPAddress.Loopback).Outcome);
                Assert.True(SpinWait.SpinUntil(() =>
                {
                    while (stream.Reader.TryRead(out var item))
                    {
                        seen.Add(item);
                    }

                    if (VoicePhasesOf(seen).Any(p => p.Phase is "sent" or "heard" or "failed" or "nothing" or "canceled"))
                    {
                        return true;
                    }

                    _time.Advance(TimeSpan.FromSeconds(1));   // the stall timer runs on the screen's clock; the chunks have stopped
                    Thread.Sleep(50);
                    return false;
                }, TimeSpan.FromSeconds(15)), "the stalled listen never finished");
                phases.AddRange(VoicePhasesOf(seen));
            },
            i => PushLine(i, "/exit"));

        await RunAsync(input);

        Assert.Equal("sent", phases[^1].Phase);
        Assert.Equal("after the stall", LastUser(_chat.Requests[0]));
    }

    /// <summary>
    /// Web UI voice turned off while Whisper ran, and Whisper answered anyway (2026-11-14, the review's #12): the canceled listen sends
    /// nothing and ends as canceled.
    /// </summary>
    [Fact]
    public async Task ACancelThatLosesTheRaceWithWhisper_SendsNothing()
    {
        WebVoiceOn();
        _recognizer.Text = "too late";
        _recognizer.IgnoreCancellation = true;
        _recognizer.OnTranscribe = async (_, ct) =>
        {
            _settings.Update(d => d.WebUiVoice = false);
            for (int i = 0; i < 200 && !ct.IsCancellationRequested; i++)
            {
                _time.Advance(TimeSpan.FromSeconds(1));   // the session's tick sees voice off and stops the pages' voice
                await Task.Delay(25, CancellationToken.None);
            }

            Assert.True(ct.IsCancellationRequested);
        };
        List<WebVoicePhase>? phases = null;
        var input = WhenIdle(
            i =>
            {
                phases = ListenFromThePhoneUntil("canceled");
                PushLine(i, "/exit");
            });

        await RunAsync(input);

        Assert.Equal("canceled", phases![^1].Phase);
        Assert.DoesNotContain(phases!, p => p.Phase is "sent" or "heard");
        Assert.Empty(_chat.Requests);
    }

    [Fact]
    public async Task APageWithoutAStream_IsToldToTryAgain_AndAnIdOfTheWrongShape_IsABadRequest()
    {
        WebVoiceOn();
        WebReply noStream = default;
        WebReply badId = default;
        var input = WhenIdle(
            i =>
            {
                Assert.True(SpinWait.SpinUntil(() => Web.Snapshot().State.VoiceListen == "", TimeSpan.FromSeconds(20)));
                noStream = Web.Voice!.Start(new WebVoiceStart { Page = PhonePage, Listen = PhoneListen }, IPAddress.Loopback);
                badId = Web.Voice!.Start(new WebVoiceStart { Page = "short", Listen = PhoneListen }, IPAddress.Loopback);
                PushLine(i, "/exit");
            });

        await RunAsync(input);

        Assert.Equal((WebOutcome.Conflict, WebUiText.VoiceNoStream), (noStream.Outcome, noStream.Message));
        Assert.Equal(WebOutcome.Refused, badId.Outcome);
    }
}
