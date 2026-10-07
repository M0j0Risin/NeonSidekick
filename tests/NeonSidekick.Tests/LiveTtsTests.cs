using System.Net;
using NeonSidekick.App;
using NeonSidekick.Audio;
using NeonSidekick.Llm;
using NeonSidekick.Settings;
using NeonSidekick.Speech;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

[Collection("LiveTts")]
public class LiveTtsTests
{
    [LiveTtsFact]
    public void ListVoices_IncludesTheDefaultVoice()
    {
        Assert.Contains(new AppSettingsData().TtsVoice, LiveTtsServer.Voices);
    }

    [LiveTtsFact]
    public async Task Synthesize_ShortPhrase_YieldsAlignedNonEmptyPcm()
    {
        using var synth = new KokoroHttpSynthesizer(LiveTtsServer.BaseUrl!);
        var counts = new List<int>();
        long total = 0;

        var result = await synth.SynthesizeAsync("Hello from Neon.", new AppSettingsData().TtsVoice, 1.0, (_, count) => { counts.Add(count); total += count; }, CancellationToken.None);

        Assert.True(result.Ok, result.Detail);
        Assert.Equal(total, result.PcmBytes);
        Assert.True(total > 4800, $"only {total} bytes for a whole sentence");   // more than 100 ms of audio
        Assert.All(counts, c => Assert.Equal(0, c % 2));
    }

    /// <summary>
    /// The only proof that Kokoro-FastAPI accepts the weighted <c>name(w)+name(w)</c> form
    /// <see cref="VoiceMix.Spec"/> builds: the default voice blended with the first other voice
    /// the server lists.
    /// </summary>
    [LiveTtsFact]
    public async Task Synthesize_MixedVoices_YieldsPcm()
    {
        string primary = new AppSettingsData().TtsVoice;
        string? secondary = LiveTtsServer.Voices.FirstOrDefault(v => v != primary);
        Assert.False(secondary is null, "the server lists only one voice; nothing to mix");

        using var synth = new KokoroHttpSynthesizer(LiveTtsServer.BaseUrl!);
        long total = 0;
        string spec = VoiceMix.Spec(primary, secondary, 70);

        var result = await synth.SynthesizeAsync("Hello from a blended Neon.", spec, 1.0, (_, count) => total += count, CancellationToken.None);

        Assert.True(result.Ok, $"{spec}: {result.Detail}");
        Assert.True(total > 4800, $"only {total} bytes for a whole sentence with voice {spec}");
    }

    /// <summary>
    /// The whole speech path for real: a scripted reply through the chat screen, the real Kokoro
    /// server and the real device (WinMM, or AudioQueue on a Mac). Passing means the turn waited for the audio to drain
    /// and nothing warned; whether it was audible is <c>--audio-check</c>'s question.
    /// </summary>
    [LiveSpeechFact]
    public async Task ChatScreen_SpeaksAReply_ThroughTheRealServerAndDevice()
    {
        string dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
        using var console = new TestConsole();
        console.Interactive();
        console.Profile.Width = 240;
        using var settings = new AppSettings(dir);
        settings.Update(d => { d.LlmUrl = "http://127.0.0.1:1234/v1"; d.TtsOutput = true; d.TtsSource = "http"; d.TtsHttpUrl = LiveTtsServer.BaseUrl!.AbsoluteUri; });

        var http = new StubHttpMessageHandler().Map("http://127.0.0.1:1234/v1/models", HttpStatusCode.OK, StubHttpMessageHandler.ModelsJson("llama"));
        var chat = new FakeChatClient().EnqueueText("Hello from Neon. ", "This is a live test.");
        using var session = new LlmSession(new LlmEndpointProbe(new HttpClient(http), TimeSpan.FromMilliseconds(500)), new ContextLengthProbe(new HttpClient(http), TimeSpan.FromMilliseconds(500)), (_, _) => chat);
        using var speech = new SpeechSession(request => new KokoroHttpSynthesizer(request.Url!), AudioSupport.DefaultPlayback, new ModelStore(Path.Combine(dir, "models"), new HttpClient(http)));
        // Voice input stays off (the default), so the session touches neither the microphone nor the models.
        using var voice = new VoiceSession(_ => new FakeAudioCapture(), _ => new FakeRecognizer(), (_, _) => new FakeVad(), new ModelStore(Path.Combine(dir, "models"), new HttpClient(http)), () => 0, (_, _) => new FakeWakeWordDetector());

        console.Input.PushText("hi");
        console.Input.PushKey(Keys.Enter);
        console.Input.PushText("/exit");
        console.Input.PushKey(Keys.Enter);

        var screen = new ChatScreen(console, settings, () => settings.Current, _ => null, session, speech, new KeySource(console.Input, TimeSpan.FromMilliseconds(1)), voice, _ => { }, _ => { }, TimeProvider.System);
        int code = await screen.RunAsync(CancellationToken.None);

        try
        {
            Assert.Equal(0, code);
            Assert.DoesNotContain("TTS: ", console.Output);   // ready at startup prints nothing under the banner
            Assert.Contains("● Hello from Neon. This is a live test.", console.Output);
            Assert.DoesNotContain("[Speech]", console.Output);
            Assert.DoesNotContain(ChatScreen.SpeechStoppedNotice, console.Output);
            Assert.True(speech.IsReady);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
