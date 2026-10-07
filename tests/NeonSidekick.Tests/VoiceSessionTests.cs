using NeonSidekick.Audio;
using System.Net;
using NeonSidekick.App;
using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;
using NeonSidekick.Speech;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

public class VoiceSessionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _models;
    private readonly StubHttpMessageHandler _http = new();
    private readonly FakeAudioCapture _capture = new();
    private readonly FakeRecognizer _recognizer = new();
    private readonly FakeVad _vad = new();
    private readonly FakeWakeWordDetector _wake = new();
    private readonly List<string> _phases = new();
    private int _microphones = 1;
    private int _deviceProbes;
    private readonly List<string> _recognizerPaths = new();
    private readonly List<string> _vadPaths = new();
    private readonly List<(string Directory, string Phrase)> _wakeBuilds = new();
    private readonly VoiceSession _voice;

    public VoiceSessionTests()
    {
        _models = Path.Combine(_dir, "models");
        _voice = new VoiceSession(
            _ => _capture,
            path => { _recognizerPaths.Add(path); return _recognizer; },
            (path, _) => { _vadPaths.Add(path); return _vad; },
            new ModelStore(_models, new HttpClient(_http)),
            () => { _deviceProbes++; return _microphones; },
            (directory, phrase) => { _wakeBuilds.Add((directory, phrase)); return _wake; });
    }

    public void Dispose()
    {
        _voice.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static AppSettingsData Enabled(string model = "ggml-base.en.bin", string key = "F4") =>
        new() { SttInput = true, SttWhisperModel = model, SttPushToTalkKey = key };

    private Task ConnectAsync(AppSettingsData effective) => _voice.ConnectAsync(effective, _phases.Add, CancellationToken.None);

    private void ServeBoth()
    {
        var body = FakeModelFiles.GgmlBytes(1000);
        _http.Map(ModelStore.WhisperRepository + "ggml-base.en.bin", (_, _) => Task.FromResult(StubHttpMessageHandler.Bytes(HttpStatusCode.OK, body, "application/octet-stream")));
        _http.Map(ModelStore.SileroUrl, (_, _) => Task.FromResult(StubHttpMessageHandler.Bytes(HttpStatusCode.OK, body, "application/octet-stream")));
    }

    [Fact]
    public async Task ConnectAsync_ADownloadCancelled_Rethrows_AndIsUnavailableWithTheReason_NeverOff()
    {
        // Ctrl+C under the connect's spinner (2026-09-17): the status is a warning naming the
        // reason, not "off" while the switch is on.
        using var cts = new CancellationTokenSource();
        var body = FakeModelFiles.GgmlBytes(1000);
        _http.Map(ModelStore.WhisperRepository + "ggml-base.en.bin", async (_, ct) =>
        {
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return StubHttpMessageHandler.Bytes(HttpStatusCode.OK, body, "application/octet-stream");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _voice.ConnectAsync(Enabled(), _phases.Add, cts.Token));

        Assert.True(_voice.Enabled);
        Assert.False(_voice.Available);
        Assert.True(_voice.StatusIsWarning);
        Assert.Equal(VoiceSession.UnavailableLine(VoiceSession.ConnectCancelledDetail), _voice.StatusLine());
    }

    // ── Status lines ────────────────────────────────────────────────────────

    [Fact]
    public void StatusLines_ArePinned()
    {
        // Every status line leads with its strip glyph (2026-09-18).
        Assert.Equal("🎤 STT: off (/stt to enable)", VoiceSession.OffLine);
        Assert.Equal("🎤 STT: F4 to talk", VoiceSession.ReadyLine("F4"));
        Assert.Equal("🎤 STT: no microphone (no wave-in device); voice off until /stt", VoiceSession.NoMicrophoneLine("no wave-in device"));
        Assert.Equal("🎤 STT: model ggml-base.en.bin missing (HTTP 404); voice off until /stt", VoiceSession.ModelMissingLine("ggml-base.en.bin", "HTTP 404"));
        Assert.Equal("🎤 STT: unavailable (boom); voice off until /stt", VoiceSession.UnavailableLine("boom"));
        Assert.Equal("downloading whisper ggml-base.en.bin (148 MB)…", VoiceSession.DownloadLabel("whisper ggml-base.en.bin", 147_964_211));
        Assert.Equal("downloading whisper ggml-base.en.bin (148 MB)… 42%", VoiceSession.DownloadLabel("whisper ggml-base.en.bin", 147_964_211, 42));
        Assert.Equal("loading silero vad…", VoiceSession.LoadingLabel("silero vad"));
        Assert.Equal("🎤 👂 STT: F4 to talk or say \"neon\"", VoiceSession.ReadyLine("F4", "neon"));
        Assert.Equal("🎤 👂 ✋ STT: F4 to talk or say \"neon\" (interrupt enabled)", VoiceSession.ReadyLine("F4", "neon", interrupt: true));
        Assert.Equal("🎤 STT: F4 to talk", VoiceSession.ReadyLine("F4", null, interrupt: true));   // no wake word: the interrupt switch alone says nothing
        Assert.Equal("✋ Interrupt: unavailable (HTTP 404); push-to-talk still works, /interrupt on retries", VoiceSession.InterruptUnavailableLine("HTTP 404"));
        Assert.Equal(TimeSpan.FromSeconds(4), VoiceSession.InterruptionOptions.NoSpeechTimeout);
        Assert.Equal(VoicePipelineOptions.Default.MaxUtterance, VoiceSession.InterruptionOptions.MaxUtterance);
        Assert.Equal("👂 Wake word: unavailable (HTTP 404); push-to-talk still works, /stt retries", VoiceSession.WakeUnavailableLine("HTTP 404"));
        Assert.Equal("downloading vosk model (41 MB)…", VoiceSession.DownloadLabel("vosk model", 40_960_000));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(null, true)]
    [InlineData("neon", false)]
    [InlineData("neon", true)]
    public void ReadyLine_LeadsWithTheStripsGlyphs(string? phrase, bool interrupt)
    {
        // The line and the hint row's strip follow one rule (2026-09-18): the microphone, the ear with a phrase, the hand with the interrupt (✋ since later that day; 🛑 before).
        string lead = ChatScreen.SpeechGlyphs(ttsOn: false, sttOn: true, wakeReady: phrase is not null, interruptReady: interrupt);

        Assert.StartsWith(lead + " STT: ", VoiceSession.ReadyLine("F4", phrase, interrupt));
    }

    // ── Wake word ───────────────────────────────────────────────────────────

    private static AppSettingsData WakeOn(string phrase = "neon") =>
        new() { SttInput = true, SttWake = true, SttWakePhrase = phrase };

    private void ServeVosk() =>
        _http.Map(ModelStore.VoskModelUrl, (_, _) => Task.FromResult(StubHttpMessageHandler.Bytes(HttpStatusCode.OK, FakeModelFiles.VoskZip(), "application/zip")));

    [Fact]
    public async Task WakeOff_TouchesNoVosk_AndTheReadyLineIsPlain()
    {
        FakeModelFiles.WriteBoth(_models);

        await ConnectAsync(Enabled());

        Assert.True(_voice.IsReady);
        Assert.False(_voice.WakeEnabled);
        Assert.False(_voice.WakeReady);
        Assert.False(_voice.WakeStatusIsWarning);
        Assert.Null(_voice.WakeStatusLine());
        Assert.Empty(_wakeBuilds);
        Assert.Empty(_http.Requests);
        Assert.Equal(VoiceSession.ReadyLine("F4"), _voice.StatusLine());
        Assert.Equal("hey neon", _voice.WakePhrase);   // the default phrase, kept for the line even when off
    }

    [Fact]
    public async Task WakeOn_DownloadsAndLoads_ThenTheReadyLineSaysThePhrase()
    {
        FakeModelFiles.WriteBoth(_models);
        ServeVosk();

        await ConnectAsync(WakeOn("  Hey   Neon "));

        Assert.True(_voice.IsReady);
        Assert.True(_voice.WakeReady, _voice.WakeDetail);
        Assert.Equal("hey neon", _voice.WakePhrase);
        Assert.Equal(Path.Combine(_models, ModelStore.VoskModelDirectoryName), _voice.WakeModelPath);
        Assert.Equal(new[] { (_voice.WakeModelPath, "hey neon") }, _wakeBuilds);
        Assert.True(_wake.Loaded);
        Assert.Single(_http.Requests);
        Assert.Equal(VoiceSession.ReadyLine("F4", "hey neon"), _voice.StatusLine());
        Assert.Null(_voice.WakeStatusLine());
        Assert.Contains(VoiceSession.UnpackingLabel, _phases);
        Assert.Contains(VoiceSession.LoadingLabel("vosk model"), _phases);
        Assert.Contains(_phases, p => p.StartsWith("downloading vosk model", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WakeOn_PresentModel_CostsNoHttp()
    {
        FakeModelFiles.WriteBoth(_models);
        FakeModelFiles.WriteVoskModelUnder(_models);

        await ConnectAsync(WakeOn());

        Assert.True(_voice.WakeReady, _voice.WakeDetail);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task WakeOn_VoskDownloadFails_VoiceStaysReady_WakeWarns()
    {
        FakeModelFiles.WriteBoth(_models);
        _http.Map(ModelStore.VoskModelUrl, HttpStatusCode.NotFound, "no");

        await ConnectAsync(WakeOn());

        Assert.True(_voice.IsReady);
        Assert.False(_voice.WakeReady);
        Assert.True(_voice.WakeStatusIsWarning);
        Assert.StartsWith("👂 Wake word: unavailable (HTTP 404", _voice.WakeStatusLine());
        Assert.Equal(VoiceSession.ReadyLine("F4"), _voice.StatusLine());
        Assert.Empty(_wakeBuilds);
        Assert.False(_voice.ArmWake(new CancellationTokenSource()));
    }

    [Fact]
    public async Task WakeOn_SavedVoskModel_StagesThatModel_UnderItsOwnName()
    {
        FakeModelFiles.WriteBoth(_models);
        const string name = "vosk-model-en-us-0.22-lgraph";
        _http.Map(ModelStore.VoskRepository + name + ".zip", (_, _) => Task.FromResult(StubHttpMessageHandler.Bytes(HttpStatusCode.OK, FakeModelFiles.VoskZip(name), "application/zip")));
        var settings = WakeOn();
        settings.SttVoskModel = " " + name.ToUpperInvariant() + " ";   // any case, trimmed

        await ConnectAsync(settings);

        Assert.True(_voice.WakeReady, _voice.WakeDetail);
        Assert.Equal(name.ToUpperInvariant(), _voice.WakeModel);
        Assert.Equal(Path.Combine(_models, name), _voice.WakeModelPath);
        Assert.Equal(new[] { (_voice.WakeModelPath, "neon") }, _wakeBuilds);
        Assert.Equal(ModelStore.VoskRepository + name + ".zip", Assert.Single(_http.Requests).Uri.AbsoluteUri);
        Assert.True(Directory.Exists(Path.Combine(_models, name)));
        Assert.False(Directory.Exists(Path.Combine(_models, ModelStore.VoskModelDirectoryName)));
    }

    [Fact]
    public async Task WakeOn_BlankVoskModel_IsTheDefault()
    {
        FakeModelFiles.WriteBoth(_models);
        ServeVosk();
        var settings = WakeOn();
        settings.SttVoskModel = "  ";

        await ConnectAsync(settings);

        Assert.True(_voice.WakeReady, _voice.WakeDetail);
        Assert.Equal(ModelStore.VoskModelDirectoryName, _voice.WakeModel);
        Assert.Equal(Path.Combine(_models, ModelStore.VoskModelDirectoryName), _voice.WakeModelPath);
    }

    [Fact]
    public async Task WakeOn_HandEditedVoskModel_WakeAndInterruptWarn_PushToTalkStays()
    {
        FakeModelFiles.WriteBoth(_models);
        ServeVosk();
        var settings = WakeOn();
        settings.SttInterrupt = true;
        settings.TtsOutput = true;
        settings.SttVoskModel = "vosk-model-en-us-0.22";   // a static-graph model, never offered

        await ConnectAsync(settings);

        Assert.True(_voice.IsReady);
        Assert.False(_voice.WakeReady);
        Assert.Equal("vosk-model-en-us-0.22", _voice.WakeModel);
        Assert.Equal(VoiceSession.WakeUnavailableLine(ModelStore.VoskModelError), _voice.WakeStatusLine());
        Assert.Equal(VoiceSession.InterruptUnavailableLine(ModelStore.VoskModelError), _voice.InterruptStatusLine());
        Assert.Empty(_wakeBuilds);
        Assert.Empty(_http.Requests);   // nothing is fetched for a name outside the list
        Assert.Equal(VoiceSession.ReadyLine("F4"), _voice.StatusLine());
    }

    [Fact]
    public async Task WakeOn_DetectorLoadFails_VoiceStaysReady_WakeWarns()
    {
        FakeModelFiles.WriteBoth(_models);
        FakeModelFiles.WriteVoskModelUnder(_models);
        _wake.LoadFails = true;

        await ConnectAsync(WakeOn());

        Assert.True(_voice.IsReady);
        Assert.Equal(VoiceSession.WakeUnavailableLine("model directory missing or incomplete: vosk-model-small-en-us-0.15"), _voice.WakeStatusLine());
    }

    [Fact]
    public async Task WakeOn_VoiceNotReady_WakeIsMoot()
    {
        _microphones = 0;
        ServeVosk();

        await ConnectAsync(WakeOn());

        Assert.False(_voice.IsReady);
        Assert.False(_voice.WakeReady);
        Assert.False(_voice.WakeStatusIsWarning);
        Assert.Null(_voice.WakeStatusLine());
        Assert.Empty(_http.Requests);   // no Vosk download when voice itself failed
    }

    [Fact]
    public async Task ArmWake_OpensTheMicrophone_AndAHitCancelsTheSource_OffTheCallingThread()
    {
        FakeModelFiles.WriteBoth(_models);
        FakeModelFiles.WriteVoskModelUnder(_models);
        await ConnectAsync(WakeOn());
        _wake.FinalAfterBuffers = 2;
        _wake.Text = "neon what time is it";
        using var wake = new CancellationTokenSource();
        bool cancelledInline = false;
        bool insideDeliver = false;
        wake.Token.Register(() => cancelledInline = Volatile.Read(ref insideDeliver));

        Assert.True(_voice.ArmWake(wake));
        Assert.Equal(1, _capture.Started);
        Assert.Equal(1, _wake.Resets);

        Volatile.Write(ref insideDeliver, true);
        _capture.Deliver(_capture.Silence(50), 1600);
        _capture.Deliver(_capture.Silence(50), 1600);
        Volatile.Write(ref insideDeliver, false);
        await Task.Run(() => wake.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)));

        Assert.True(wake.IsCancellationRequested);
        Assert.False(cancelledInline);
        var hit = _voice.DisarmWake();
        Assert.NotNull(hit);
        Assert.Equal("neon what time is it", hit.Text);
        Assert.True(hit.HasRequest);
        Assert.Equal(3200, hit.Seed.Length);
        Assert.Equal(1, _capture.Stopped);
        Assert.True(_voice.WakeReady);
        Assert.Equal(WakeDetectorMode.Utterance, _wake.LastMode);   // the idle line: finals, open vocabulary, timings
        Assert.Equal("neon", _wake.LastPhrase);
    }

    [Fact]
    public async Task ArmWake_KeywordMode_ReachesTheDetector_AndAPartialCancelsTheSource()
    {
        FakeModelFiles.WriteBoth(_models);
        FakeModelFiles.WriteVoskModelUnder(_models);
        await ConnectAsync(WakeOn());
        _wake.PartialAfterBuffers = 1;
        _wake.PartialText = "[unk] neon";
        using var wake = new CancellationTokenSource();

        Assert.True(_voice.ArmWake(wake, ignore: null, WakeDetectorMode.Keyword));
        Assert.Equal(WakeDetectorMode.Keyword, _wake.LastMode);
        Assert.Equal("neon", _wake.LastPhrase);

        // The partial on the first buffer, then the phrase persists through KeywordConfirm (400 ms = 8 more buffers).
        for (int i = 0; i < 9; i++)
        {
            _capture.Deliver(_capture.Silence(50), 1600);
        }

        await Task.Run(() => wake.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)));

        Assert.True(wake.IsCancellationRequested);
        var hit = _voice.DisarmWake();
        Assert.NotNull(hit);
        Assert.Equal("[unk] neon", hit.Text);
        Assert.Equal(9 * 1600, hit.Seed.Length);
    }

    [Fact]
    public async Task DisarmWake_WithoutAHit_StopsTheMicrophone_AndReturnsNull()
    {
        FakeModelFiles.WriteBoth(_models);
        FakeModelFiles.WriteVoskModelUnder(_models);
        await ConnectAsync(WakeOn());
        var wake = new CancellationTokenSource();

        Assert.True(_voice.ArmWake(wake));
        wake.Dispose();   // the input line returned for another reason and disposed it
        Assert.Null(_voice.DisarmWake());

        Assert.Equal(1, _capture.Stopped);
        Assert.Null(_voice.DisarmWake());   // idempotent
        Assert.Equal(1, _capture.Stopped);
    }

    [Fact]
    public async Task ArmWake_MicrophoneThrows_MarksWakeUnavailable_VoiceStaysReady()
    {
        FakeModelFiles.WriteBoth(_models);
        FakeModelFiles.WriteVoskModelUnder(_models);
        await ConnectAsync(WakeOn());
        _capture.ThrowOnStart = true;

        Assert.False(_voice.ArmWake(new CancellationTokenSource()));

        Assert.True(_voice.IsReady);
        Assert.False(_voice.WakeReady);
        Assert.Contains("MMSYSERR", _voice.WakeDetail);
        Assert.StartsWith("👂 Wake word: unavailable (microphone unavailable", _voice.WakeStatusLine());
    }

    [Fact]
    public async Task ArmWake_WhenNotReady_IsFalse_WithoutTouchingTheMicrophone()
    {
        FakeModelFiles.WriteBoth(_models);
        await ConnectAsync(Enabled());

        Assert.False(_voice.ArmWake(new CancellationTokenSource()));
        Assert.Equal(0, _capture.Started);
        Assert.Null(_voice.DisarmWake());
    }

    [Fact]
    public async Task ListenWithSeed_ReachesTheRecognizer_AheadOfTheLiveAudio()
    {
        FakeModelFiles.WriteBoth(_models);
        await ConnectAsync(Enabled());
        _vad.EndAfterBuffers = 1;
        _capture.OnStart = (c, _) => { c.Deliver(c.Silence(50), 1600); return Task.CompletedTask; };
        var seed = new byte[3200];
        Array.Fill(seed, (byte)7);

        var result = await _voice.ListenAsync(CancellationToken.None, null, CancellationToken.None, seed);

        Assert.True(result.Ok, result.Detail);
        Assert.Equal(4800, _recognizer.Received[0].Length);
        Assert.Equal(7, _recognizer.Received[0][0]);
        Assert.Equal(ListenEnd.EndOfSpeech, result.EndedBy);
    }

    // ── Interrupt ───────────────────────────────────────────────────────────

    private static AppSettingsData InterruptOn(bool wake = false) =>
        new() { SttInput = true, SttWake = wake, SttInterrupt = true, SttWakePhrase = "neon" };

    [Fact]
    public async Task InterruptOn_WakeOff_StagesVosk_AndTheReadyLineStaysPlain()
    {
        FakeModelFiles.WriteBoth(_models);
        ServeVosk();

        await ConnectAsync(InterruptOn());

        Assert.True(_voice.InterruptReady, _voice.InterruptDetail);
        Assert.False(_voice.WakeReady);          // the idle wake word stays off
        Assert.True(_voice.WakeAvailable);       // but the model is loaded
        Assert.Single(_http.Requests);
        Assert.Equal(new[] { (_voice.WakeModelPath, "neon") }, _wakeBuilds);
        Assert.Equal(VoiceSession.ReadyLine("F4"), _voice.StatusLine());   // the phrase is named only when the wake word listens
        Assert.Null(_voice.InterruptStatusLine());
        Assert.Null(_voice.WakeStatusLine());
    }

    [Fact]
    public async Task InterruptOn_WakeOn_TheReadyLineSaysBoth()
    {
        FakeModelFiles.WriteBoth(_models);
        FakeModelFiles.WriteVoskModelUnder(_models);

        await ConnectAsync(InterruptOn(wake: true));

        Assert.True(_voice.WakeReady && _voice.InterruptReady);
        Assert.Equal(VoiceSession.ReadyLine("F4", "neon", interrupt: true), _voice.StatusLine());
    }

    [Fact]
    public async Task InterruptOn_VoskFails_VoiceStaysReady_InterruptWarns()
    {
        FakeModelFiles.WriteBoth(_models);
        _http.Map(ModelStore.VoskModelUrl, HttpStatusCode.NotFound, "no");

        await ConnectAsync(InterruptOn());

        Assert.True(_voice.IsReady);
        Assert.False(_voice.InterruptReady);
        Assert.True(_voice.InterruptStatusIsWarning);
        Assert.StartsWith("✋ Interrupt: unavailable (HTTP 404", _voice.InterruptStatusLine());
        Assert.Null(_voice.WakeStatusLine());   // the wake word is off, so it has nothing to say
        Assert.Equal(VoiceSession.ReadyLine("F4"), _voice.StatusLine());
        Assert.False(_voice.ArmWake(new CancellationTokenSource()));
    }

    [Fact]
    public async Task MarkInterruptUnavailable_LeavesTheWakeWordAndPushToTalk()
    {
        FakeModelFiles.WriteBoth(_models);
        FakeModelFiles.WriteVoskModelUnder(_models);
        await ConnectAsync(InterruptOn(wake: true));

        _voice.MarkInterruptUnavailable("the microphone keeps hearing the assistant");

        Assert.False(_voice.InterruptReady);
        Assert.True(_voice.WakeReady);
        Assert.True(_voice.IsReady);
        Assert.Equal(VoiceSession.InterruptUnavailableLine("the microphone keeps hearing the assistant"), _voice.InterruptStatusLine());
        Assert.Equal(VoiceSession.ReadyLine("F4", "neon"), _voice.StatusLine());
        Assert.True(_voice.ArmWake(new CancellationTokenSource()));   // the idle wake word still arms
        Assert.Null(_voice.DisarmWake());

        await ConnectAsync(InterruptOn(wake: true));   // the next probe restores it
        Assert.True(_voice.InterruptReady);
    }

    [Fact]
    public async Task InterruptOff_WakeOff_StagesNoVosk()
    {
        FakeModelFiles.WriteBoth(_models);

        await ConnectAsync(Enabled());

        Assert.False(_voice.InterruptEnabled);
        Assert.False(_voice.InterruptReady);
        Assert.False(_voice.InterruptStatusIsWarning);
        Assert.Null(_voice.InterruptStatusLine());
        Assert.Empty(_wakeBuilds);
    }

    [Fact]
    public async Task ArmWake_WithAGuard_IgnoresWhatTheGuardRejects()
    {
        FakeModelFiles.WriteBoth(_models);
        FakeModelFiles.WriteVoskModelUnder(_models);
        await ConnectAsync(InterruptOn());
        _wake.FinalAfterBuffers = 1;
        _wake.Text = "i'm neon";
        _wake.LaterText = "neon";
        using var wake = new CancellationTokenSource();
        var seen = new List<string>();

        Assert.True(_voice.ArmWake(wake, u => { seen.Add(u.Text); return u.Text.Contains("i'm", StringComparison.Ordinal); }));
        _capture.Deliver(_capture.Silence(50), 1600);
        Assert.False(wake.IsCancellationRequested);
        _capture.Deliver(_capture.Silence(50), 1600);
        await Task.Run(() => wake.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)));

        Assert.True(wake.IsCancellationRequested);
        Assert.Equal(new[] { "i'm neon", "neon" }, seen);
        Assert.Equal("neon", _voice.DisarmWake()!.Text);
    }

    [Fact]
    public async Task ListenAsync_WithOptions_UsesThem_ForThatListenOnly()
    {
        FakeModelFiles.WriteBoth(_models);
        await ConnectAsync(Enabled());
        _vad.SpeakingFromBuffer = 0;   // nothing is ever speech: the no-speech timeout ends the listen
        _capture.OnStart = async (c, ct) => { while (!ct.IsCancellationRequested) { c.Deliver(c.Silence(50), 1600); await Task.Delay(45, CancellationToken.None); } };
        var quick = VoiceSession.InterruptionOptions with { NoSpeechTimeout = TimeSpan.FromMilliseconds(150) };

        var result = await _voice.ListenAsync(CancellationToken.None, null, CancellationToken.None, seed: null, options: quick);

        Assert.Equal(ListenEnd.NoSpeech, result.EndedBy);
        Assert.Equal(VoicePipelineOptions.Default, _voice.PipelineOptions);   // untouched
    }

    [Fact]
    public async Task Reconnect_DisposesTheDetector()
    {
        FakeModelFiles.WriteBoth(_models);
        FakeModelFiles.WriteVoskModelUnder(_models);
        await ConnectAsync(WakeOn());

        await ConnectAsync(Enabled());

        Assert.True(_wake.Disposed);
        Assert.False(_voice.WakeReady);
    }

    // ── The echo probe (a fork of the detector over the assistant's own audio) ──

    [Fact]
    public async Task Connect_ForksTheDetector_AndCreateEchoProbe_ResetsTheForkInKeywordMode()
    {
        FakeModelFiles.WriteBoth(_models);
        FakeModelFiles.WriteVoskModelUnder(_models);
        await ConnectAsync(InterruptOn());
        Assert.Equal(1, _wake.Forks);
        var fork = _wake.ForkResult!;

        using var probe = _voice.CreateEchoProbe(PcmFormat.Kokoro);

        Assert.NotNull(probe);
        Assert.Equal(WakeDetectorMode.Keyword, fork.LastMode);
        Assert.Equal("neon", fork.LastPhrase);
        Assert.Equal(0, _wake.Resets);   // the microphone's detector is untouched
        Assert.Equal(PcmFormat.Kokoro, probe!.Source);
    }

    [Fact]
    public async Task CreateEchoProbe_IsNull_WhenTheInterruptIsNotReady_OrTheForkFailed()
    {
        FakeModelFiles.WriteBoth(_models);
        FakeModelFiles.WriteVoskModelUnder(_models);
        await ConnectAsync(WakeOn());   // interrupt off
        Assert.Null(_voice.CreateEchoProbe(PcmFormat.Kokoro));

        _wake.ForkThrows = true;
        await ConnectAsync(InterruptOn());
        Assert.True(_voice.InterruptReady);   // the fork is optional: the text guard stands alone
        Assert.Null(_voice.CreateEchoProbe(PcmFormat.Kokoro));
    }

    [Fact]
    public async Task Reconnect_DisposesTheFork_BeforeTheDetector()
    {
        FakeModelFiles.WriteBoth(_models);
        FakeModelFiles.WriteVoskModelUnder(_models);
        await ConnectAsync(InterruptOn());
        var fork = _wake.ForkResult!;

        await ConnectAsync(Enabled());

        Assert.True(fork.Disposed);
        Assert.True(_wake.Disposed);
    }

    [Fact]
    public async Task Disabled_TouchesNothing()
    {
        await ConnectAsync(new AppSettingsData());

        Assert.False(_voice.Enabled);
        Assert.False(_voice.IsReady);
        Assert.False(_voice.StatusIsWarning);
        Assert.Equal(VoiceSession.OffLine, _voice.StatusLine());
        Assert.Equal(0, _deviceProbes);
        Assert.Empty(_http.Requests);
        Assert.False(Directory.Exists(_models));
        Assert.Empty(_phases);
    }

    [WindowsFact]
    public async Task NoMicrophone_IsAWarning_WithNoHttp()
    {
        _microphones = 0;

        await ConnectAsync(Enabled());

        Assert.True(_voice.StatusIsWarning);
        Assert.Equal(VoiceSession.NoMicrophoneLine("no wave-in device"), _voice.StatusLine());
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task Downloads_ThenLoads_ThenReady()
    {
        ServeBoth();

        await ConnectAsync(Enabled());

        Assert.True(_voice.IsReady, _voice.Detail);
        Assert.Equal(VoiceSession.ReadyLine("F4"), _voice.StatusLine());
        Assert.Equal(Path.Combine(_models, "ggml-base.en.bin"), _voice.ModelPath);
        Assert.True(File.Exists(Path.Combine(_models, "ggml-base.en.bin")));
        Assert.True(File.Exists(Path.Combine(_models, ModelStore.SileroFileName)));
        Assert.Equal(2, _http.Requests.Count);
        Assert.Equal(new[] { _voice.ModelPath }, _recognizerPaths);
        Assert.Equal(new[] { Path.Combine(_models, ModelStore.SileroFileName) }, _vadPaths);
        Assert.True(_recognizer.Loaded);
        Assert.True(_vad.Loaded);
        Assert.Contains(VoiceSession.DownloadLabel("whisper ggml-base.en.bin", 1004, 0), _phases);
        Assert.Contains(VoiceSession.DownloadLabel("whisper ggml-base.en.bin", 1004, 100), _phases);
        Assert.Contains(VoiceSession.DownloadLabel("silero vad", 1004, 100), _phases);
        Assert.Contains(VoiceSession.LoadingLabel("whisper ggml-base.en.bin"), _phases);
        Assert.Contains(VoiceSession.LoadingLabel("silero vad"), _phases);
        Assert.Equal(ConsoleKey.F4, _voice.PushToTalk);
    }

    [Fact]
    public async Task PresentFiles_CostNoHttp_AndReconnectReloads()
    {
        FakeModelFiles.WriteBoth(_models);

        await ConnectAsync(Enabled(key: "F8"));
        await ConnectAsync(Enabled(key: "F8"));

        Assert.True(_voice.IsReady);
        Assert.Empty(_http.Requests);
        Assert.Equal(2, _recognizerPaths.Count);
        Assert.True(_recognizer.Disposed);   // the first one was dropped on reconnect
        Assert.Equal(ConsoleKey.F8, _voice.PushToTalk);
        Assert.Equal("F8", _voice.PushToTalkName);
        Assert.Equal(VoiceSession.ReadyLine("F8"), _voice.StatusLine());
    }

    [Fact]
    public async Task DownloadFails_IsModelMissing()
    {
        _http.Map(ModelStore.WhisperRepository + "ggml-base.en.bin", HttpStatusCode.NotFound, "no");

        await ConnectAsync(Enabled());

        Assert.True(_voice.StatusIsWarning);
        Assert.StartsWith("🎤 STT: model ggml-base.en.bin missing (HTTP 404", _voice.StatusLine());
        Assert.Single(_http.Requests);   // silero is never attempted
        Assert.Empty(_recognizerPaths);
    }

    [Fact]
    public async Task SileroDownloadFails_IsModelMissing_NamingSilero()
    {
        FakeModelFiles.Write(Path.Combine(_models, "ggml-base.en.bin"));
        _http.Map(ModelStore.SileroUrl, HttpStatusCode.Forbidden, "no");

        await ConnectAsync(Enabled());

        Assert.StartsWith("🎤 STT: model silero missing (HTTP 403", _voice.StatusLine());
    }

    [Fact]
    public async Task RootedMissingPath_IsModelMissing_WithNoHttp()
    {
        string path = Path.Combine(_dir, "custom", "ggml-medium.bin");

        await ConnectAsync(Enabled(model: path));

        Assert.Equal(VoiceSession.ModelMissingLine(path, $"not found: {path}"), _voice.StatusLine());
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task BadModelName_IsModelMissing_WithTheMenuWording()
    {
        await ConnectAsync(Enabled(model: "huge.en"));

        Assert.Equal(VoiceSession.ModelMissingLine("huge.en", ModelStore.WhisperModelError), _voice.StatusLine());
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task RecognizerLoadFails_IsUnavailable()
    {
        FakeModelFiles.WriteBoth(_models);
        _recognizer.LoadFails = true;

        await ConnectAsync(Enabled());

        Assert.False(_voice.IsReady);
        Assert.StartsWith("🎤 STT: unavailable (model file not found", _voice.StatusLine());
        Assert.Empty(_vadPaths);
    }

    [Fact]
    public async Task VadLoadFails_IsUnavailable()
    {
        FakeModelFiles.WriteBoth(_models);
        _vad.LoadFails = true;

        await ConnectAsync(Enabled());

        Assert.Equal(VoiceSession.UnavailableLine("model file not found: ggml-silero-v6.2.0.bin"), _voice.StatusLine());
    }

    [Fact]
    public async Task FactoryThrows_IsUnavailable_NotACrash()
    {
        FakeModelFiles.WriteBoth(_models);
        var voice = new VoiceSession(_ => _capture, _ => throw new InvalidOperationException("no whisper"), (_, _) => _vad, new ModelStore(_models, new HttpClient(_http)), () => 1, (_, _) => _wake);

        await voice.ConnectAsync(Enabled(), null, CancellationToken.None);

        Assert.Equal(VoiceSession.UnavailableLine("InvalidOperationException: no whisper"), voice.StatusLine());
    }

    [Fact]
    public void ParsePushToTalk_BadName_FallsBackToF4_WithOneWarning()
    {
        var warnings = new List<string>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == "Voice" && e.Level == DiagnosticLevel.Warning) { warnings.Add(e.Message); } };
        DiagnosticLog.Emitted += capture;
        try
        {
            Assert.Equal(ConsoleKey.F8, VoiceSession.ParsePushToTalk("f8"));
            Assert.Equal(ConsoleKey.F4, VoiceSession.ParsePushToTalk("F9"));   // a chord since 2026-10-05
            Assert.Equal(ConsoleKey.F4, VoiceSession.ParsePushToTalk("nope"));
            Assert.Equal(ConsoleKey.F4, VoiceSession.ParsePushToTalk("A"));
            Assert.Equal(ConsoleKey.F4, VoiceSession.ParsePushToTalk("F12"));   // a key from before the fixed list
            Assert.Equal(ConsoleKey.F4, VoiceSession.ParsePushToTalk(null));
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        Assert.Equal(5, warnings.Count);
        Assert.Contains("using F4", warnings[0]);
    }

    // ── Listening ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Listen_NotReady_IsAFailedResult()
    {
        await ConnectAsync(new AppSettingsData());
        var result = await _voice.ListenAsync(CancellationToken.None, null, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal(0, _capture.Started);
    }

    [Fact]
    public async Task Listen_Transcribes_AndReusesThePipeline()
    {
        FakeModelFiles.WriteBoth(_models);
        await ConnectAsync(Enabled());
        _vad.EndAfterBuffers = 1;
        _capture.OnStart = (c, _) => { c.Deliver(c.Silence(50), 1600); return Task.CompletedTask; };

        var first = await _voice.ListenAsync(CancellationToken.None, null, CancellationToken.None);
        var second = await _voice.ListenAsync(CancellationToken.None, null, CancellationToken.None);

        Assert.Equal("hello", first.Text);
        Assert.Equal("hello", second.Text);
        Assert.True(_voice.IsReady);
        Assert.Equal(2, _capture.Started);
    }

    [Fact]
    public async Task Listen_Failure_MarksUnavailable()
    {
        FakeModelFiles.WriteBoth(_models);
        await ConnectAsync(Enabled());
        _voice.PipelineOptions = VoicePipelineOptions.Default with { DeliveryWatchdog = TimeSpan.FromMilliseconds(50) };

        var result = await _voice.ListenAsync(CancellationToken.None, null, CancellationToken.None);   // no buffers: watchdog

        Assert.False(result.Ok);
        Assert.False(_voice.IsReady);
        Assert.True(_voice.StatusIsWarning);
        Assert.Equal(VoiceSession.UnavailableLine(VoicePipeline.NoAudioDetail), _voice.StatusLine());

        await ConnectAsync(Enabled());   // the next probe restores it
        Assert.True(_voice.IsReady);
    }

    [Fact]
    public async Task Listen_CaptureFactoryThrows_MarksUnavailable()
    {
        FakeModelFiles.WriteBoth(_models);
        var voice = new VoiceSession(_ => throw new InvalidOperationException("no mic"), _ => _recognizer, (_, _) => _vad, new ModelStore(_models, new HttpClient(_http)), () => 1, (_, _) => _wake);
        await voice.ConnectAsync(Enabled(), null, CancellationToken.None);

        var result = await voice.ListenAsync(CancellationToken.None, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("no mic", result.Detail);
        Assert.False(voice.IsReady);
    }

    [Fact]
    public void Ctor_Guards()
    {
        var store = new ModelStore(_models, new HttpClient(_http));
        Func<string, string, IWakeWordDetector> wake = (_, _) => _wake;
        Assert.Throws<ArgumentNullException>(() => new VoiceSession(null!, _ => _recognizer, (_, _) => _vad, store, () => 1, wake));
        Assert.Throws<ArgumentNullException>(() => new VoiceSession(_ => _capture, null!, (_, _) => _vad, store, () => 1, wake));
        Assert.Throws<ArgumentNullException>(() => new VoiceSession(_ => _capture, _ => _recognizer, null!, store, () => 1, wake));
        Assert.Throws<ArgumentNullException>(() => new VoiceSession(_ => _capture, _ => _recognizer, (_, _) => _vad, null!, () => 1, wake));
        Assert.Throws<ArgumentNullException>(() => new VoiceSession(_ => _capture, _ => _recognizer, (_, _) => _vad, store, null!, wake));
        Assert.Throws<ArgumentNullException>(() => new VoiceSession(_ => _capture, _ => _recognizer, (_, _) => _vad, store, () => 1, null!));
    }
    [Fact]
    public void VoiceLogLines_ArePinned()
    {
        Assert.Equal("Wake listener armed (utterance: the idle wake word)", VoiceSession.ArmedLogLine(WakeDetectorMode.Utterance, null));
        Assert.Equal("Wake listener armed (keyword: the interrupt, confirm 200 ms)", VoiceSession.ArmedLogLine(WakeDetectorMode.Keyword, TimeSpan.FromMilliseconds(200)));
        Assert.Equal("Wake listener disarmed (hit)", VoiceSession.DisarmedLogLine(true));
        Assert.Equal("Wake listener disarmed (no hit)", VoiceSession.DisarmedLogLine(false));
        Assert.Equal("Voice", VoiceSession.Category);
    }
}
