using System.Net;
using NeonSidekick.App;
using NeonSidekick.Audio;
using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;
using NeonSidekick.Speech;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>The speaker's lifetime in the session: it outlives its turn as the tail, and the session is the one that stops it.</summary>
public class SpeechSessionTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly StubHttpMessageHandler _http = new();
    private readonly FakeSynthesizer _synth = new();
    private readonly FakeAudioPlayback _playback = new();
    private readonly ModelStore _models;
    private readonly SpeechSession _speech;
    private readonly List<SynthesizerRequest> _requests = new();

    public SpeechSessionTests()
    {
        _models = new ModelStore(Path.Combine(_dir, "models"), new HttpClient(_http));
        _speech = new SpeechSession(request => { _requests.Add(request); return _synth; }, _ => _playback, _models);
    }

    public void Dispose()
    {
        _speech.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private async Task ConnectAsync()
    {
        await _speech.ConnectAsync(new AppSettingsData { TtsOutput = true, TtsSource = "http", TtsHttpUrl = "http://127.0.0.1:8880" }, null, CancellationToken.None);
        Assert.True(_speech.IsReady);
    }

    /// <summary>The request the session compares against, for a URL as the menu would hand it over.</summary>
    private SynthesizerRequest? Http(string url) => _speech.RequestFor(new AppSettingsData { TtsSource = "http", TtsHttpUrl = url });

    private SynthesizerRequest? InProcess() => _speech.RequestFor(new AppSettingsData { TtsSource = "in-process" });

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "condition not met in time");
    }

    [Fact]
    public async Task ConnectAsync_Cancelled_Rethrows_AndIsNotReadyWithTheReason()
    {
        // Ctrl+C under the connect's spinner (2026-09-17): the status line and the TTS tab name
        // the reason, never an empty detail. The in-process shape (a download or load throws).
        using var cts = new CancellationTokenSource();
        _synth.OnPrepare = ct => { cts.Cancel(); return Task.Delay(System.Threading.Timeout.Infinite, ct); };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _speech.ConnectAsync(new AppSettingsData { TtsOutput = true, TtsSource = "http", TtsHttpUrl = "http://127.0.0.1:8880" }, null, cts.Token));

        Assert.True(_speech.Enabled);
        Assert.False(_speech.Available);
        Assert.False(_speech.IsReady);
        Assert.Equal(SpeechSession.ConnectCancelledDetail, _speech.Detail);
        Assert.True(_speech.StatusIsWarning);
        Assert.Contains("(" + SpeechSession.ConnectCancelledDetail + ")", _speech.StatusLine());
    }

    [Fact]
    public async Task Playing_IsNull_BeforeATurn_AndAfterTheDeviceDrained()
    {
        await ConnectAsync();
        Assert.Null(_speech.Playing);

        var speaker = _speech.BeginTurn(CancellationToken.None);
        Assert.NotNull(speaker);
        Assert.Same(speaker, _speech.Playing);

        speaker.Feed("One. Two.");
        speaker.CompleteAdding();
        await speaker.Completion.WaitAsync(Timeout);

        Assert.Null(_speech.Playing);
        Assert.False(await _speech.StopAsync());   // nothing owed: false, and no device stop
        Assert.Equal(0, _playback.Stopped);
    }

    /// <summary><c>/botchat</c>'s overload (2026-09-24): the bot's own voice and speed through the session's synthesizer; a blank voice is the session's.</summary>
    [Fact]
    public async Task BeginTurn_WithAVoice_SpeaksInIt()
    {
        await ConnectAsync();

        var speaker = _speech.BeginTurn(CancellationToken.None, "bm_george", 1.5)!;
        speaker.Feed("One.");
        speaker.CompleteAdding();
        await speaker.Completion.WaitAsync(Timeout);
        var blank = _speech.BeginTurn(CancellationToken.None, " ", 1.0)!;
        blank.Feed("Two.");
        blank.CompleteAdding();
        await blank.Completion.WaitAsync(Timeout);

        Assert.Equal(("bm_george", 1.5), (_synth.Spoken[0].Voice, _synth.Spoken[0].Speed));
        Assert.Equal(_speech.VoiceSpec, _synth.Spoken[1].Voice);
    }

    [Fact]
    public async Task StopAsync_WithAudioOwed_SilencesTheDevice_AndReportsIt()
    {
        await ConnectAsync();
        _playback.HoldBytes = true;
        var speaker = _speech.BeginTurn(CancellationToken.None)!;
        speaker.Feed("One. Two.");
        speaker.CompleteAdding();
        await WaitUntilAsync(() => _playback.Writes.Count == 2);
        Assert.NotNull(_speech.Playing);

        bool stoppedEarly = await _speech.StopAsync();

        Assert.True(stoppedEarly);
        Assert.True(speaker.StoppedEarly);
        Assert.True(speaker.Completion.IsCompletedSuccessfully);
        Assert.True(_playback.Cleared >= 1);
        Assert.True(_playback.Stopped >= 1);
        Assert.Null(_speech.Playing);
        Assert.False(await _speech.StopAsync());   // idempotent, nothing to report the second time
    }

    [Fact]
    public async Task Stop_CancelsTheSpeakersToken_TheQueueSilencesTheDevice()
    {
        await ConnectAsync();
        _playback.HoldBytes = true;
        var speaker = _speech.BeginTurn(CancellationToken.None)!;
        speaker.Feed("One.");
        speaker.CompleteAdding();
        await WaitUntilAsync(() => _playback.Writes.Count == 1);

        _speech.Stop();

        await speaker.Completion.WaitAsync(Timeout);
        Assert.True(speaker.StoppedEarly);
        await WaitUntilAsync(() => _playback.Stopped >= 1);
    }

    [Fact]
    public async Task StopAsync_DisposesTheSpeakersProbe()
    {
        await ConnectAsync();
        var detector = new FakeWakeWordDetector();
        var probe = new EchoProbe(detector, "neon", PcmFormat.Kokoro);
        var speaker = _speech.BeginTurn(CancellationToken.None)!;
        speaker.Probe = probe;
        speaker.CompleteAdding();

        await _speech.StopAsync();

        // A disposed probe is complete: feeding it is a no-op and its task has ended.
        await probe.Completion.WaitAsync(Timeout);
    }

    /// <summary>The session's Warning lines while <paramref name="action"/> runs.</summary>
    private static async Task<List<string>> SpeechWarningsAsync(Func<Task> action)
    {
        var warnings = new List<string>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == "Speech" && e.Level == DiagnosticLevel.Warning) warnings.Add(e.Message); };
        DiagnosticLog.Emitted += capture;
        try
        {
            await action();
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        return warnings;
    }

    [Fact]
    public async Task BeginTurn_WhileTheLastSpeakerPlays_SilencesIt_AsASafetyNet()
    {
        await ConnectAsync();
        _playback.HoldBytes = true;
        var first = _speech.BeginTurn(CancellationToken.None)!;
        first.Feed("One.");
        first.CompleteAdding();
        await WaitUntilAsync(() => _playback.Writes.Count == 1);

        SpeechOutput? second = null;
        var warnings = await SpeechWarningsAsync(() => { second = _speech.BeginTurn(CancellationToken.None); return Task.CompletedTask; });

        Assert.NotNull(second);
        Assert.NotSame(first, second);
        Assert.Same(second, _speech.Playing);
        await first.Completion.WaitAsync(Timeout);
        Assert.True(first.StoppedEarly);
        Assert.Equal(new[] { SpeechSession.TailSilencedWarning }, warnings);
    }

    [Fact]
    public async Task BeginTurn_AfterTheLastSpeakerDrained_ForgetsIt_Quietly()
    {
        // The idle wake after a reply heard to the end: the drained speaker is housekeeping, not
        // a tail cut short — no warning, no device stop.
        await ConnectAsync();
        var first = _speech.BeginTurn(CancellationToken.None)!;
        first.Feed("One.");
        first.CompleteAdding();
        await first.Completion.WaitAsync(Timeout);
        Assert.Null(_speech.Playing);

        SpeechOutput? second = null;
        var warnings = await SpeechWarningsAsync(() => { second = _speech.BeginTurn(CancellationToken.None); return Task.CompletedTask; });

        Assert.NotNull(second);
        Assert.NotSame(first, second);
        Assert.Same(second, _speech.Playing);
        Assert.Empty(warnings);
        Assert.Equal(0, _playback.Stopped);
        Assert.False(first.StoppedEarly);
    }

    [Fact]
    public async Task TheAppToken_StopsTheSpeaker()
    {
        await ConnectAsync();
        _playback.HoldBytes = true;
        using var app = new CancellationTokenSource();
        var speaker = _speech.BeginTurn(app.Token)!;
        speaker.Feed("One.");
        speaker.CompleteAdding();
        await WaitUntilAsync(() => _playback.Writes.Count == 1);

        app.Cancel();

        await speaker.Completion.WaitAsync(Timeout);
        Assert.True(speaker.StoppedEarly);
    }

    [Fact]
    public async Task ConnectAsync_StopsATailFirst()
    {
        await ConnectAsync();
        _playback.HoldBytes = true;
        var speaker = _speech.BeginTurn(CancellationToken.None)!;
        speaker.Feed("One.");
        speaker.CompleteAdding();
        await WaitUntilAsync(() => _playback.Writes.Count == 1);

        await ConnectAsync();   // /tts, or a TTS field edited: the client is rebuilt

        Assert.True(speaker.Completion.IsCompletedSuccessfully);
        Assert.True(speaker.StoppedEarly);
        Assert.Null(_speech.Playing);
    }

    [Fact]
    public async Task Dispose_StopsTheSpeaker()
    {
        await ConnectAsync();
        _playback.HoldBytes = true;
        var speaker = _speech.BeginTurn(CancellationToken.None)!;
        speaker.Feed("One.");
        speaker.CompleteAdding();
        await WaitUntilAsync(() => _playback.Writes.Count == 1);

        _speech.Dispose();

        await speaker.Completion.WaitAsync(Timeout);
        Assert.True(speaker.StoppedEarly);
        Assert.True(_playback.Disposed);
    }

    // ── The voice picker's preview ──────────────────────────────────────────

    [Fact]
    public async Task BeginPreview_SpeaksTheGivenVoice_AtTheGivenSpeed_AndIsTheTail()
    {
        await _speech.ConnectAsync(new AppSettingsData { TtsOutput = true, TtsSource = "http", TtsHttpUrl = "http://127.0.0.1:8880", TtsVoice = "af_heart", TtsVoice2 = "af_sky", TtsSpeed = 1.3 }, null, CancellationToken.None);
        Assert.True(_speech.IsReady);

        var speaker = _speech.BeginPreview(Http("http://127.0.0.1:8880"), "bm_george", 0.8, CancellationToken.None);

        Assert.NotNull(speaker);
        Assert.Same(speaker, _speech.Playing);
        speaker.Feed(SettingsMenu.VoicePreviewText);
        speaker.CompleteAdding();
        await speaker.Completion.WaitAsync(Timeout);
        Assert.Equal(["bm_george"], _synth.Spoken.Select(s => s.Voice).Distinct());   // the phrase is two sentences, both in the given voice
        Assert.Equal([0.8], _synth.Spoken.Select(s => s.Speed).Distinct());
        Assert.Equal(SettingsMenu.VoicePreviewText, string.Join(" ", _synth.Spoken.Select(s => s.Text)));
        Assert.Null(_speech.Playing);

        // The turn still speaks the mix at the session's speed.
        var turn = _speech.BeginTurn(CancellationToken.None)!;
        turn.Feed("One.");
        turn.CompleteAdding();
        await turn.Completion.WaitAsync(Timeout);
        Assert.Equal(("af_heart(80)+af_sky(20)", 1.3), (_synth.Spoken[^1].Voice, _synth.Spoken[^1].Speed));   // the default mix is 80 since 2026-09-16
    }

    [Fact]
    public async Task BeginPreview_IsNull_WhenNotReady_AnotherSource_ABadUrl_OrNoVoice()
    {
        Assert.Null(_speech.BeginPreview(Http("http://127.0.0.1:8880"), "bm_george", 1.0, CancellationToken.None));   // never connected

        await ConnectAsync();
        Assert.Null(_speech.BeginPreview(Http("http://127.0.0.1:8881"), "bm_george", 1.0, CancellationToken.None));   // a TTS HTTP URL edited in the same session
        Assert.Null(Http("not a url"));
        Assert.Null(_speech.BeginPreview(null, "bm_george", 1.0, CancellationToken.None));
        Assert.Null(_speech.BeginPreview(InProcess(), "bm_george", 1.0, CancellationToken.None));   // a TTS source edited in the same session
        Assert.Null(_speech.BeginPreview(Http("http://127.0.0.1:8880"), " ", 1.0, CancellationToken.None));
        Assert.NotNull(_speech.BeginPreview(Http("http://127.0.0.1:8880/v1/"), "bm_george", 1.0, CancellationToken.None));   // the same server, spelled as the session normalises it
        Assert.Empty(_synth.Spoken);
    }

    // ── The in-process source ───────────────────────────────────────────────

    [Fact]
    public async Task Connect_InProcess_EnsuresTheModel_ThenPrepares_AndTheLineNamesTheEngine()
    {
        var body = FakeModelFiles.OnnxBytes(2048);
        _http.Map(ModelStore.KokoroModelUrl, (_, _) => Task.FromResult(StubHttpMessageHandler.Bytes(HttpStatusCode.OK, body, "application/octet-stream")));
        var phases = new List<string>();

        await _speech.ConnectAsync(new AppSettingsData { TtsOutput = true, TtsSource = "in-process", TtsVoice = "af_heart" }, phases.Add, CancellationToken.None);

        Assert.True(_speech.IsReady);
        Assert.Equal(TtsEngine.InProcess, _speech.Engine);
        Assert.Equal("in-process Kokoro", _speech.Source);
        Assert.Equal("🔊 TTS: in-process Kokoro voice=af_heart(80)+am_eric(20) speed=1.2", _speech.StatusLine());   // a fresh profile's blend (2026-09-16; speed 1.2 since 2026-09-18)
        var request = Assert.Single(_requests);
        Assert.Equal(SynthesizerRequest.InProcess(Path.Combine(_dir, "models", "kokoro.onnx")), request);
        Assert.True(File.Exists(request.ModelPath));
        Assert.Equal(1, _synth.PrepareCalls);
        Assert.Equal("downloading kokoro.onnx (2 KB)… 100%", phases[^2]);
        Assert.Equal("loading kokoro.onnx…", phases[^1]);

        // The picker: the connected synthesizer for the same request, a throwaway for the http one; neither prepares.
        Assert.NotNull(await _speech.ListVoicesAsync(InProcess(), CancellationToken.None));
        Assert.NotNull(await _speech.ListVoicesAsync(Http("http://127.0.0.1:8880"), CancellationToken.None));
        Assert.Null(await _speech.ListVoicesAsync(null, CancellationToken.None));
        Assert.Equal(2, _requests.Count);
        Assert.Equal(1, _synth.PrepareCalls);
        Assert.NotNull(_speech.BeginPreview(InProcess(), "bm_george", 1.0, CancellationToken.None));
        Assert.Null(_speech.BeginPreview(Http("http://127.0.0.1:8880"), "bm_george", 1.0, CancellationToken.None));

        // Present next time: no download, no phase.
        phases.Clear();
        await _speech.ConnectAsync(new AppSettingsData { TtsOutput = true, TtsSource = "in-process" }, phases.Add, CancellationToken.None);
        Assert.True(_speech.IsReady);
        Assert.Equal(["loading kokoro.onnx…"], phases);
        Assert.True(_synth.Disposed);   // the previous instance went with the reconnect
    }

    [Fact]
    public async Task Connect_InProcess_WhenTheDownloadFails_IsNotReady_AndNeverAsksTheFactory()
    {
        _http.Map(ModelStore.KokoroModelUrl, HttpStatusCode.NotFound, "gone", "text/plain");

        await _speech.ConnectAsync(new AppSettingsData { TtsOutput = true, TtsSource = "in-process" }, null, CancellationToken.None);

        Assert.False(_speech.IsReady);
        Assert.True(_speech.StatusIsWarning);
        Assert.Empty(_requests);
        Assert.StartsWith("🔊 TTS: in-process Kokoro is not ready (kokoro.onnx: ", _speech.StatusLine());
        Assert.EndsWith("); speech off until /tts", _speech.StatusLine());
        Assert.Equal("🔊 TTS: in-process Kokoro is not ready (the voices folder is missing beside the exe); speech off until /tts", SpeechSession.NotReadyLine(KokoroInProcessSynthesizer.MissingVoicesDetail));
    }

    [Fact]
    public async Task Connect_InProcess_WhenTheEngineRefuses_IsNotReady_WithItsDetail()
    {
        FakeModelFiles.WriteOnnx(_models.Kokoro().Path);
        _synth.Exists = false;
        _synth.ListDetail = "";

        await _speech.ConnectAsync(new AppSettingsData { TtsOutput = true, TtsSource = "in-process" }, null, CancellationToken.None);

        Assert.False(_speech.IsReady);
        Assert.Single(_requests);
        Assert.Equal(1, _synth.PrepareCalls);
        Assert.StartsWith("🔊 TTS: in-process Kokoro is not ready (", _speech.StatusLine());
    }

    [Fact]
    public async Task Connect_Http_IsUnchanged_AndAFlipToHttpIsANewRequest()
    {
        await ConnectAsync();
        Assert.Equal(TtsEngine.Http, _speech.Engine);
        Assert.Equal("http://127.0.0.1:8880/v1", _speech.Source);
        Assert.Equal("🔊 TTS: http://127.0.0.1:8880/v1 voice=af_heart(80)+am_eric(20) speed=1.2", _speech.StatusLine());
        Assert.Equal(SynthesizerRequest.Http(new Uri("http://127.0.0.1:8880/v1")), Assert.Single(_requests));
        Assert.Equal("http://127.0.0.1:8880/v1", Assert.Single(_requests).Source);

        await _speech.ConnectAsync(new AppSettingsData { TtsOutput = true, TtsSource = "http", TtsHttpUrl = "not a url" }, null, CancellationToken.None);
        Assert.False(_speech.IsReady);
        Assert.Equal("🔊 TTS: no server at not a url (not a valid URL); speech off until /tts", _speech.StatusLine());
        Assert.Single(_requests);

        await _speech.ConnectAsync(new AppSettingsData { TtsOutput = false, TtsSource = "in-process" }, null, CancellationToken.None);
        Assert.Equal(SpeechSession.OffLine, _speech.StatusLine());
        Assert.Single(_requests);   // off: no download, no factory
    }
    [Fact]
    public void SpeechLogLines_ArePinned()
    {
        Assert.Equal("TTS ready: in-process, voice af_heart + am_eric 80%, speed 1.2, 54 voices listed", SpeechSession.ReadyLogLine("in-process", "af_heart", "am_eric", 80, 1.2, 54));
        Assert.Equal("TTS ready: http://127.0.0.1:8880/v1/, voice af_heart, speed 1, 0 voices listed", SpeechSession.ReadyLogLine("http://127.0.0.1:8880/v1/", "af_heart", "", 50, 1.0, 0));
        Assert.Equal("Speaking: voice af_heart, speed 1.2", SpeechSession.SpeakingLogLine("af_heart", 1.2));
        Assert.Equal("Speech stopped: 48,000 of 96,000 bytes played", SpeechSession.StoppedLogLine(48000, 96000));
        Assert.Equal("Speech done: 96,000 bytes", SpeechSession.DoneLogLine(96000));
        Assert.Equal("Speech", SpeechSession.Category);
    }
}
