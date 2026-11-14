using System.Text.Json;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The Chat tab's 🎤 in a real page (2026-11-14, the v0.5.7 review's fixes): a listen that ended at the app while the stream was down ends on
/// the reconnect's snapshot (#50); a canceled listen never pastes a late heard text (#38); a cancel at the permission prompt still stops the
/// microphone once it is allowed (#51); and 🎤 over a spoken reply tells the app to stop that voice even when the listen never starts (#56).
/// Also 🔊 flipped in another tab, which this page follows (#54). A deferred <c>getUserMedia</c> stands in for the microphone: it answers when
/// the test says, with a real (silent) <c>MediaStream</c> whose track stops are counted.
/// </summary>
public sealed partial class WebUiPageTests
{
    private const string FakeMic = """
        (() => {
          const mic = { pending: [], stops: 0, asked: 0 };
          mic.allow = () => {
            const answer = mic.pending.shift();
            if (!answer) return false;
            const ctx = new (window.AudioContext || window.webkitAudioContext)();
            const stream = ctx.createMediaStreamDestination().stream;
            for (const t of stream.getTracks()) { const stop = t.stop.bind(t); t.stop = () => { mic.stops++; stop(); }; }
            answer(stream);
            return true;
          };
          if (!navigator.mediaDevices) Object.defineProperty(navigator, 'mediaDevices', { value: {} });
          navigator.mediaDevices.getUserMedia = () => new Promise(resolve => { mic.asked++; mic.pending.push(resolve); });
          window.__mic = mic;
        })();
        """;

    /// <summary>The Chat page with the fake microphone and 🎤 offered, its stream up under its page id; null with no browser.</summary>
    private async Task<(HeadlessPage Page, string Id)?> ListeningPageAsync(FakeWebVoice voice)
    {
        if (_page is null)
        {
            return null;
        }

        _backend.Voice = voice;
        _backend.Snap = new WebSnapshot([], [], State() with { VoiceListen = "", VoiceSpeak = "" }, WebUiText.Words);
        await _page.OnNewDocumentAsync(FakeMic);
        var page = (await SignedInAsync("/"))!;
        string id = await page.TextAsync("chatPage");
        Assert.True(SpinWait.SpinUntil(() => _backend.Events.HasPage(id), TimeSpan.FromSeconds(10)), "the page's stream never connected under its id");
        await page.WaitAsync("!document.getElementById('mic').classList.contains('hidden')", "🎤 offered");
        return (page, id);
    }

    /// <summary>🎤 tapped, the microphone allowed, and the app's start answered: the listen runs.</summary>
    private static async Task<string> StartedListenAsync(HeadlessPage page, FakeWebVoice voice)
    {
        await page.EvalAsync("document.getElementById('mic').click()");
        await page.WaitAsync("window.__mic.pending.length === 1", "the microphone asked for");
        await page.EvalAsync("window.__mic.allow()");
        await page.WaitAsync("listen !== null && listen.started === true", "the listen started");
        return Assert.Single(voice.Starts).Listen!;
    }

    private void PublishVoicePhase(string page, string listen, string phase, string? text = null) =>
        _backend.Events.Publish(new WebEvent("voice", () => JsonSerializer.Serialize(new WebVoicePhase(listen, phase, text, null), WebUiJsonContext.Default.WebVoicePhase)) { Page = page });

    [WindowsFact]
    public async Task Chat_AListenThatEndedWhileTheStreamWasDown_EndsOnTheReconnect_AndARunningOneStays()
    {
        var voice = new FakeWebVoice();
        if (await ListeningPageAsync(voice) is not var (page, _))
        {
            return;
        }

        string listen = await StartedListenAsync(page, voice);

        // A reconnect whose snapshot still names the listen: it goes on.
        _backend.Snap = _backend.Snap with { Listen = listen };
        int before = _backend.SnapshotPages.Count;
        await page.EvalAsync("reconnect()");
        await UntilAsync(() => _backend.SnapshotPages.Count > before, "the first reconnect's snapshot");
        await Task.Delay(300);
        Assert.Equal("true", (await page.EvalAsync("listen !== null")).ToString().ToLowerInvariant());

        // One that names none: the listen ended at the app, its end told to no one, so it ends here and 🎤 is idle again.
        _backend.Snap = _backend.Snap with { Listen = null };
        await page.EvalAsync("reconnect()");
        await page.WaitAsync("listen === null && !document.getElementById('mic').classList.contains('listening')", "the listen ended by the snapshot");
        await page.WaitAsync("window.__mic.stops === 1", "the microphone off");
    }

    [WindowsFact]
    public async Task Chat_ACanceledListen_NeverPastesALateHeardText()
    {
        var voice = new FakeWebVoice();
        if (await ListeningPageAsync(voice) is not var (page, id))
        {
            return;
        }

        string listen = await StartedListenAsync(page, voice);
        await page.EvalAsync("document.getElementById('listen-cancel').click()");
        await UntilAsync(() => voice.Ends.Any(e => e.How == "cancel"), "the cancel told to the app");

        PublishVoicePhase(id, listen, "heard", "words that lost the race");
        await Task.Delay(500);
        Assert.Equal("", await page.TextAsync("draft.value"));

        // A listen that ended without a cancel still takes its late text.
        string second = await StartedListenAsyncAgain(page, voice);
        PublishVoicePhase(id, second, "nothing");
        await page.WaitAsync("listen === null", "the second listen ended");
        PublishVoicePhase(id, second, "heard", "late but wanted");
        await page.WaitAsync("draft.value === 'late but wanted'", "the late text in the box");
    }

    /// <summary>A second listen on the same page: 🎤 again, allowed, started (the fake answers every start).</summary>
    private static async Task<string> StartedListenAsyncAgain(HeadlessPage page, FakeWebVoice voice)
    {
        int starts = voice.Starts.Count;
        await page.EvalAsync("document.getElementById('mic').click()");
        await page.WaitAsync("window.__mic.pending.length === 1", "the microphone asked for again");
        await page.EvalAsync("window.__mic.allow()");
        await page.WaitAsync("listen !== null && listen.started === true", "the second listen started");
        Assert.True(SpinWait.SpinUntil(() => voice.Starts.Count > starts, TimeSpan.FromSeconds(5)));
        return voice.Starts.Last().Listen!;
    }

    [WindowsFact]
    public async Task Chat_ACancelAtThePermissionPrompt_StopsTheMicrophoneOnceAllowed()
    {
        var voice = new FakeWebVoice();
        if (await ListeningPageAsync(voice) is not var (page, _))
        {
            return;
        }

        await page.EvalAsync("document.getElementById('mic').click()");
        await page.WaitAsync("window.__mic.pending.length === 1", "the microphone asked for");
        await page.EvalAsync("document.getElementById('listen-cancel').click()");
        await page.WaitAsync("listen === null", "the listen canceled at the prompt");

        await page.EvalAsync("window.__mic.allow()");

        await page.WaitAsync("window.__mic.stops === 1", "the late stream's track stopped");
        Assert.Empty(voice.Starts);   // the app was never asked
    }

    [WindowsFact]
    public async Task Chat_TheMicOverASpokenReply_TellsTheAppToStopThatVoice_EvenWhenTheListenNeverStarts()
    {
        var voice = new FakeWebVoice();
        if (await SpeakingPageAsync(voice) is not var (page, id))
        {
            return;
        }

        voice.Clips[(id, VoiceTurn, 0)] = new byte[WebAudioPlayback.FirstClipBytes];
        AnnounceClip(id, 0, WebAudioPlayback.FirstClipBytes);
        await page.WaitAsync("window.__voice.clips() === 1", "the clip scheduled");
        // The browser refuses the microphone: the listen never reaches the app.
        await page.EvalAsync("navigator.mediaDevices.getUserMedia = () => Promise.reject(new DOMException('no', 'NotAllowedError'))");

        await page.EvalAsync("document.getElementById('mic').click()");

        await UntilAsync(() => voice.Played.Any(p => p.Stop == true), "the voice's stop told to the app");
        Assert.Equal(VoiceTurn, voice.Played.Single(p => p.Stop == true).Turn);
        Assert.Empty(voice.Starts);
        await page.WaitAsync("window.__voice.stops === 1", "its source silenced");
    }

    [WindowsFact]
    public async Task Chat_TheSpeakerFlippedInAnotherTab_IsFollowedHere_AndToldToTheApp()
    {
        var voice = new FakeWebVoice();
        if (await SpeakingPageAsync(voice) is not var (page, id))
        {
            return;
        }

        await page.EvalAsync("paintSpeak()");   // the helper stored 🔊 on after the load: painted at the next snapshot, which may have come already
        await page.WaitAsync("document.getElementById('speak').classList.contains('on')", "🔊 on");

        // Another tab's tap: the storage changes there, and this page hears of it (a dispatched event changes no storage of its own).
        await page.EvalAsync("localStorage.setItem('neon.chat.speak', '0'); window.dispatchEvent(new StorageEvent('storage', { key: 'neon.chat.speak', newValue: '0' }))");

        await UntilAsync(() => voice.Speaks.Any(s => s.Page == id && !s.On), "🔊 off told to the app for this page");
        await page.WaitAsync("!document.getElementById('speak').classList.contains('on')", "🔊 painted off");
    }
}
