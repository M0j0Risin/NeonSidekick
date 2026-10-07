using NeonSidekick.App;
using NeonSidekick.Settings;
using NeonSidekick.Speech;
using NeonSidekick.Tests.Fakes;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

public class VoiceCheckTests : IDisposable
{
    private static readonly VoicePipelineOptions Quick = new(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(120));

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly TestConsole _console = new();
    private readonly StubHttpMessageHandler _http = new();
    private readonly FakeAudioCapture _capture = new();
    private readonly FakeRecognizer _recognizer = new();
    private readonly FakeVad _vad = new();
    private readonly FakeWakeWordDetector _wake = new();
    private readonly VoiceSession _voice;

    public VoiceCheckTests()
    {
        _console.Profile.Width = 200;
        string models = Path.Combine(_dir, "models");
        FakeModelFiles.WriteBoth(models);
        _voice = new VoiceSession(_ => _capture, _ => _recognizer, (_, _) => _vad, new ModelStore(models, new HttpClient(_http)), () => 1, (_, _) => _wake);
    }

    public void Dispose()
    {
        _voice.Dispose();
        _console.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private Task<int> RunAsync() => VoiceCheck.RunAsync(_console, _voice, new AppSettingsData(), Quick);   // the saved switch is off; the check forces it on

    [Fact]
    public async Task Heard_PrintsTheTranscript_AndExitsZero()
    {
        _vad.EndAfterBuffers = 2;
        _capture.OnStart = (c, _) => { c.Deliver(c.Silence(50), 1600); c.Deliver(c.Silence(50), 1600); return Task.CompletedTask; };

        int code = await RunAsync();

        Assert.Equal(0, code);
        Assert.Contains(VoiceCheck.IntroLine, _console.Output);
        Assert.Contains(VoiceCheck.ListeningLine, _console.Output);
        Assert.Contains(VoicePipeline.TranscribingLabel, _console.Output);
        Assert.Contains("RESULT: heard \"hello\" (0.1 s of audio, transcribed in 12 ms)", _console.Output);
        Assert.Equal(1, _capture.Stopped);
    }

    [Fact]
    public async Task HeardNothing_ExitsOne()
    {
        _vad.SpeakingFromBuffer = 0;
        _capture.OnStart = async (c, ct) => { while (!ct.IsCancellationRequested) { c.Deliver(c.Silence(50), 1600); await Task.Delay(45, CancellationToken.None); } };

        int code = await RunAsync();

        Assert.Equal(1, code);
        Assert.Contains(VoiceCheck.HeardNothingLine(ListenEnd.NoSpeech), _console.Output);
        Assert.Contains(VoiceCheck.NoTranscriptLine, _console.Output);
    }

    [Fact]
    public async Task CaptureThrows_ExitsOne_WithTheDetail()
    {
        _capture.ThrowOnStart = true;

        int code = await RunAsync();

        Assert.Equal(1, code);
        Assert.Contains("MMSYSERR", _console.Output);
        Assert.Contains(VoiceCheck.NoTranscriptLine, _console.Output);
    }

    [WindowsFact]
    public async Task NoMicrophone_ExitsOne_WithTheStatusLine()
    {
        var voice = new VoiceSession(_ => _capture, _ => _recognizer, (_, _) => _vad, new ModelStore(Path.Combine(_dir, "models"), new HttpClient(_http)), () => 0, (_, _) => _wake);

        int code = await VoiceCheck.RunAsync(_console, voice, new AppSettingsData(), Quick);

        Assert.Equal(1, code);
        Assert.Contains(VoiceSession.NoMicrophoneLine("no wave-in device"), _console.Output);
        Assert.Contains(VoiceCheck.NoTranscriptLine, _console.Output);
    }

    [Fact]
    public async Task DownloadProgress_IsPrintedOnce_WithoutPercentages()
    {
        var models = Path.Combine(_dir, "fresh");
        var body = FakeModelFiles.GgmlBytes(1000);
        _http.Map(ModelStore.WhisperRepository + "ggml-base.en.bin", (_, _) => Task.FromResult(StubHttpMessageHandler.Bytes(System.Net.HttpStatusCode.OK, body, "application/octet-stream")));
        _http.Map(ModelStore.SileroUrl, (_, _) => Task.FromResult(StubHttpMessageHandler.Bytes(System.Net.HttpStatusCode.OK, body, "application/octet-stream")));
        var voice = new VoiceSession(_ => _capture, _ => _recognizer, (_, _) => _vad, new ModelStore(models, new HttpClient(_http)), () => 1, (_, _) => _wake);
        _vad.EndAfterBuffers = 1;
        _capture.OnStart = (c, _) => { c.Deliver(c.Silence(50), 1600); return Task.CompletedTask; };

        int code = await VoiceCheck.RunAsync(_console, voice, new AppSettingsData(), Quick);

        Assert.Equal(0, code);
        Assert.Equal(1, _console.Output.Split(VoiceSession.DownloadLabel("whisper ggml-base.en.bin", 1004)).Length - 1);
        Assert.DoesNotContain("%", _console.Output);
    }

    [Fact]
    public async Task WakeWordSavedOn_IsIgnored_NoVoskDownload()
    {
        _vad.EndAfterBuffers = 1;
        _capture.OnStart = (c, _) => { c.Deliver(c.Silence(50), 1600); return Task.CompletedTask; };

        int code = await VoiceCheck.RunAsync(_console, _voice, new AppSettingsData { SttWake = true, SttInterrupt = true }, Quick);

        Assert.Equal(0, code);
        Assert.Empty(_http.Requests);           // the Vosk archive is never requested
        Assert.False(_voice.WakeEnabled);
        Assert.False(_wake.Loaded);
    }

    [Fact]
    public void Lines_ArePinned()
    {
        Assert.Equal("Voice input check: up to 5 s from the default microphone at 16000 Hz, 16-bit, mono.", VoiceCheck.IntroLine);
        Assert.Equal("RESULT: heard \"hi\" (1.5 s of audio, transcribed in 340 ms)", VoiceCheck.TranscriptLine("hi", TimeSpan.FromSeconds(1.5), TimeSpan.FromMilliseconds(340)));
        Assert.Equal(TimeSpan.FromSeconds(5), VoiceCheck.CheckOptions.MaxUtterance);
        Assert.Equal(TimeSpan.FromSeconds(5), VoiceCheck.CheckOptions.NoSpeechTimeout);
        Assert.Equal(VoicePipelineOptions.Default.DeliveryWatchdog, VoiceCheck.CheckOptions.DeliveryWatchdog);
    }
}
