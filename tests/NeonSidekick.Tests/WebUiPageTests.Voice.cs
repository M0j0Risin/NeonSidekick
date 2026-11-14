using System.Text.Json;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The Chat tab's spoken replies in a real page (2026-11-13, the webui-voice plan): clips fetched in order and their progress reported (the app's
/// 5 s rule stops a voice whose page stops reporting), ■ on a tail stopping that voice alone, the page going telling the app so, and a
/// duplicated tab reopening its stream on its fresh id once. A fake <c>AudioContext</c> stands in for the device: its clock moves when the test
/// says, and a source ends when the clock passes it.
/// </summary>
public sealed partial class WebUiPageTests
{
    private const string VoiceTurn = "turn-0123456789abcdef";

    private const string FakeAudio = """
        (() => {
          let now = 0;
          const voice = { sources: [], stops: 0 };
          class FakeBuffer { constructor(ch, n, rate) { this.length = n; this.duration = n / rate; } copyToChannel() {} }
          class FakeSource {
            constructor() { this.buffer = null; this.at = null; this.done = false; this.onended = null; }
            connect() {}
            start(t) { this.at = t; }
            stop() { if (!this.done) { this.done = true; voice.stops++; } }
          }
          class FakeContext extends EventTarget {
            constructor() { super(); this.state = 'running'; this.destination = {}; }
            get currentTime() { return now; }
            createBuffer(ch, n, rate) { return new FakeBuffer(ch, n, rate); }
            createBufferSource() { const s = new FakeSource(); voice.sources.push(s); return s; }
            resume() { return Promise.resolve(); }
            close() { this.state = 'closed'; return Promise.resolve(); }
          }
          voice.advance = seconds => {
            now += seconds;
            for (const s of voice.sources) {
              if (!s.done && s.at !== null && s.buffer && s.at + s.buffer.duration <= now) { s.done = true; if (s.onended) s.onended(); }
            }
          };
          voice.clips = () => voice.sources.filter(s => s.buffer && s.buffer.length > 1).length;   // not the unlock's one-sample buffer
          window.__voice = voice;
          window.AudioContext = FakeContext;
          window.webkitAudioContext = FakeContext;
        })();
        """;

    /// <summary>The Chat page with the fake audio and 🔊 on, its stream up under its page id; null with no browser.</summary>
    private async Task<(HeadlessPage Page, string Id)?> SpeakingPageAsync(FakeWebVoice voice, bool busy = false)
    {
        if (_page is null)
        {
            return null;
        }

        _backend.Voice = voice;
        _backend.Snap = new WebSnapshot([], [], State(busy) with { VoiceSpeak = "" }, WebUiText.Words);
        await _page.OnNewDocumentAsync(FakeAudio);
        var page = (await SignedInAsync("/"))!;
        await page.EvalAsync("localStorage.setItem('neon.chat.speak', '1')");
        string id = await page.TextAsync("chatPage");
        Assert.True(SpinWait.SpinUntil(() => _backend.Events.HasPage(id), TimeSpan.FromSeconds(10)), "the page's stream never connected under its id");
        await page.WaitAsync("!document.getElementById('speak').classList.contains('hidden')", "🔊 offered");
        return (page, id);
    }

    private void AnnounceClip(string page, long n, long bytes) =>
        _backend.Events.Publish(new WebEvent("voice-clip", () => JsonSerializer.Serialize(new WebVoiceClip(VoiceTurn, n, bytes), WebUiJsonContext.Default.WebVoiceClip)) { Page = page });

    [WindowsFact]
    public async Task Chat_ASpokenReply_IsFetchedInOrder_AndItsProgressReported_ToTheEnd()
    {
        var voice = new FakeWebVoice();
        if (await SpeakingPageAsync(voice) is not var (page, id))
        {
            return;
        }

        voice.Clips[(id, VoiceTurn, 0)] = new byte[WebAudioPlayback.FirstClipBytes];   // 0.4 s
        voice.Clips[(id, VoiceTurn, 1)] = new byte[WebAudioPlayback.ClipBytes];        // 1 s
        AnnounceClip(id, 0, WebAudioPlayback.FirstClipBytes);
        AnnounceClip(id, 1, WebAudioPlayback.ClipBytes);
        await page.WaitAsync("window.__voice.clips() === 2", "both clips scheduled");
        Assert.Equal([0L, 1L], voice.ClipGets.Select(g => g.N));   // one at a time, in order

        // 0.25 s in: clip 0, 0.2 s of it played (it starts 50 ms after it's scheduled).
        await page.EvalAsync("window.__voice.advance(0.25)");
        await UntilAsync(() => voice.Played.Any(p => p.Clip == 0 && p.Offset > 0), "a report inside clip 0");
        var first = voice.Played.First(p => p.Offset > 0);
        Assert.Equal((id, VoiceTurn, 4800L), (first.Page, first.Turn, first.Offset));
        await page.WaitAsync("!document.getElementById('stop').classList.contains('hidden')", "■ while audio is owed");

        // Past the end: clip 1 reported whole, and ■ goes once the last source ends.
        await page.EvalAsync("window.__voice.advance(5)");
        await UntilAsync(() => voice.Played.Any(p => p.Clip == 1 && p.Offset == 24_000), "the end reported");
        await page.WaitAsync("document.getElementById('stop').classList.contains('hidden')", "■ gone once played out");
        Assert.All(voice.Played, p => Assert.Null(p.Stop ?? p.Gone));
    }

    [WindowsFact]
    public async Task Chat_StopOnASpokenTail_StopsThatVoiceAlone_AndLeavesTheSpeakerOn()
    {
        var voice = new FakeWebVoice();
        if (await SpeakingPageAsync(voice) is not var (page, id))
        {
            return;
        }

        voice.Clips[(id, VoiceTurn, 0)] = new byte[WebAudioPlayback.FirstClipBytes];
        AnnounceClip(id, 0, WebAudioPlayback.FirstClipBytes);
        await page.WaitAsync("window.__voice.clips() === 1", "the clip scheduled");
        await page.WaitAsync("!document.getElementById('stop').classList.contains('hidden')", "■ on the tail, the PC idle");

        await page.EvalAsync("document.getElementById('stop').click()");

        await UntilAsync(() => voice.Played.Any(p => p.Stop == true), "the tail's stop");
        Assert.Equal(VoiceTurn, voice.Played.Single(p => p.Stop == true).Turn);
        Assert.Equal(0, _backend.Stops);   // never the turn's /api/stop
        await page.WaitAsync("document.getElementById('stop').classList.contains('hidden') && window.__voice.stops === 1", "the source silenced, ■ gone");
        Assert.Equal("1", await page.TextAsync("localStorage.getItem('neon.chat.speak')"));
        Assert.DoesNotContain(voice.Speaks, s => !s.On);
    }

    [WindowsFact]
    public async Task Chat_ThePageGoing_WithAudioOwed_TellsTheAppItIsGone()
    {
        var voice = new FakeWebVoice();
        if (await SpeakingPageAsync(voice) is not var (page, id))
        {
            return;
        }

        voice.Clips[(id, VoiceTurn, 0)] = new byte[WebAudioPlayback.FirstClipBytes];
        AnnounceClip(id, 0, WebAudioPlayback.FirstClipBytes);
        await page.WaitAsync("window.__voice.clips() === 1", "the clip scheduled");

        await page.EvalAsync("window.dispatchEvent(new PageTransitionEvent('pagehide', { persisted: false }))");

        await UntilAsync(() => voice.Played.Any(p => p.Gone == true), "the gone report");
        Assert.Equal((id, VoiceTurn), voice.Played.Where(p => p.Gone == true).Select(p => (p.Page, p.Turn)).Single());
        await page.WaitAsync("window.__voice.stops === 1", "its source silenced");
    }

    [WindowsFact]
    public async Task Chat_ADuplicatedTab_ReopensItsStreamOnItsFreshId_Once()
    {
        if (_page is null)
        {
            return;
        }

        const string Fresh = "fresh-0123456789abcdef";
        _backend.Voice = new FakeWebVoice();
        _backend.Snap = new WebSnapshot([], [], State() with { VoiceSpeak = "" }, WebUiText.Words);
        _backend.HandOutFreshPage(Fresh);
        var page = (await SignedInAsync("/"))!;

        await page.WaitAsync("sessionStorage.getItem('neon.chat.page') === " + JsonSerializer.Serialize(Fresh) + " && chatPage === " + JsonSerializer.Serialize(Fresh), "the fresh id kept");
        await UntilAsync(() => _backend.SnapshotPages.Contains(Fresh), "a stream reopened on the fresh id");
        Assert.True(SpinWait.SpinUntil(() => _backend.Events.HasPage(Fresh), TimeSpan.FromSeconds(10)), "the fresh id's stream is up");
        await Task.Delay(500);
        Assert.Equal(2, _backend.SnapshotPages.Count(p => p is not null));   // the first id, then the fresh one: no loop
    }
}
