using NeonSidekick.Audio;
using NeonSidekick.Speech;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The pipeline over fakes. Buffers are delivered from <see cref="FakeAudioCapture.OnStart"/>, so
/// every scenario is scripted against the capture lifecycle rather than the clock; the timers
/// are short only where a test needs one to fire.
/// </summary>
public class VoicePipelineTests
{
    private static readonly VoicePipelineOptions Quick = new(
        NoSpeechTimeout: TimeSpan.FromMilliseconds(200),
        MaxUtterance: TimeSpan.FromMilliseconds(400),
        DeliveryWatchdog: TimeSpan.FromMilliseconds(120));

    private readonly List<string> _log = new();
    private readonly FakeAudioCapture _capture;
    private readonly FakeVad _vad = new();
    private readonly FakeRecognizer _recognizer;

    public VoicePipelineTests()
    {
        _capture = new FakeAudioCapture { Log = _log };
        _recognizer = new FakeRecognizer { Log = _log };
    }

    private VoicePipeline Pipeline(VoicePipelineOptions? options = null) => new(_capture, _vad, _recognizer, options ?? Quick);

    private static int Buffer50 => PcmFormat.Whisper.BytesFor(50);

    /// <summary>Delivers <paramref name="count"/> 50 ms buffers, then returns.</summary>
    private Func<FakeAudioCapture, CancellationToken, Task> Deliver(int count, Action? then = null) => (capture, _) =>
    {
        for (int i = 0; i < count; i++)
        {
            capture.Deliver(capture.Silence(50), Buffer50);
        }

        then?.Invoke();
        return Task.CompletedTask;
    };

    /// <summary>Delivers 50 ms buffers at roughly real time until capture stops, so the timers fire in their intended order.</summary>
    private static Func<FakeAudioCapture, CancellationToken, Task> DeliverForever() => async (capture, ct) =>
    {
        while (!ct.IsCancellationRequested)
        {
            capture.Deliver(capture.Silence(50), Buffer50);
            await Task.Delay(45, CancellationToken.None);
        }
    };

    [Fact]
    public async Task EndOfSpeech_StopsCaptureBeforeTranscribing()
    {
        _vad.EndAfterBuffers = 3;
        _capture.OnStart = Deliver(3);
        var phases = new List<string>();

        var result = await Pipeline().ListenAsync(CancellationToken.None, phases.Add, CancellationToken.None);

        Assert.True(result.Ok, result.Detail);
        Assert.Equal("hello", result.Text);
        Assert.Equal(ListenEnd.EndOfSpeech, result.EndedBy);
        Assert.Equal(new[] { "start", "stop", "transcribe" }, _log);
        Assert.Equal(3 * Buffer50, _recognizer.Received[0].Length);
        Assert.Equal(TimeSpan.FromMilliseconds(150), result.Audio);
        Assert.Equal(TimeSpan.FromMilliseconds(12), result.Elapsed);   // the recognizer's time, not the listen's
        Assert.Equal(new[] { VoicePipeline.TranscribingLabel }, phases);
        Assert.Equal(1, _vad.Resets);
        Assert.Equal(1, _capture.Stopped);
    }

    [Fact]
    public async Task FinishEarly_EndsByKey_AndTranscribesWhatThereIs()
    {
        using var finish = new CancellationTokenSource();
        _capture.OnStart = Deliver(2, finish.Cancel);

        var result = await Pipeline().ListenAsync(finish.Token, null, CancellationToken.None);

        Assert.True(result.Ok, result.Detail);
        Assert.Equal(ListenEnd.Key, result.EndedBy);
        Assert.Equal("hello", result.Text);
        Assert.Equal(2 * Buffer50, _recognizer.Received[0].Length);
    }

    [Fact]
    public async Task NoSpeech_IsHeardNothing_AndNeverTranscribes()
    {
        _vad.SpeakingFromBuffer = 0;
        _capture.OnStart = DeliverForever();

        var result = await Pipeline().ListenAsync(CancellationToken.None, null, CancellationToken.None);

        Assert.True(result.HeardNothing);
        Assert.Equal(ListenEnd.NoSpeech, result.EndedBy);
        Assert.Empty(_recognizer.Received);
        Assert.True(result.Audio > TimeSpan.Zero);
        Assert.Equal(1, _capture.Stopped);
    }

    [Fact]
    public async Task MaxDuration_CapsTheUtterance_AndTranscribes()
    {
        _capture.OnStart = DeliverForever();   // speaking from the first buffer, never ending

        var result = await Pipeline().ListenAsync(CancellationToken.None, null, CancellationToken.None);

        Assert.True(result.Ok, result.Detail);
        Assert.Equal(ListenEnd.MaxDuration, result.EndedBy);
        Assert.Equal("hello", result.Text);
        Assert.True(_recognizer.Received[0].Length <= PcmFormat.Whisper.BytesFor(400));
    }

    [Fact]
    public async Task BufferFull_EndsByMaxDuration_BeforeTheTimer()
    {
        var options = Quick with { MaxUtterance = TimeSpan.FromMilliseconds(100), NoSpeechTimeout = TimeSpan.FromSeconds(5) };
        _capture.OnStart = Deliver(5);   // 250 ms of audio into a 100 ms cap

        var result = await Pipeline(options).ListenAsync(CancellationToken.None, null, CancellationToken.None);

        Assert.Equal(ListenEnd.MaxDuration, result.EndedBy);
        Assert.Equal(PcmFormat.Whisper.BytesFor(100), _recognizer.Received[0].Length);
    }

    [Fact]
    public async Task NoBuffers_FailsWithTheWatchdogDetail()
    {
        var result = await Pipeline().ListenAsync(CancellationToken.None, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(ListenEnd.NoAudio, result.EndedBy);
        Assert.Equal(VoicePipeline.NoAudioDetail, result.Detail);
        Assert.Empty(_recognizer.Received);
        Assert.Equal(1, _capture.Stopped);
    }

    [Fact]
    public async Task Cancel_ThrowsOperationCanceled_AndStopsCapture()
    {
        using var cts = new CancellationTokenSource();
        _capture.OnStart = Deliver(1, cts.Cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Pipeline().ListenAsync(CancellationToken.None, null, cts.Token));

        Assert.Equal(1, _capture.Stopped);
        Assert.Empty(_recognizer.Received);
    }

    [Fact]
    public async Task CancelDuringTranscription_Propagates()
    {
        using var cts = new CancellationTokenSource();
        _vad.EndAfterBuffers = 1;
        _capture.OnStart = Deliver(1);
        _recognizer.OnTranscribe = (_, _) => { cts.Cancel(); return Task.CompletedTask; };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Pipeline().ListenAsync(CancellationToken.None, null, cts.Token));
        Assert.Equal(1, _capture.Stopped);
    }

    [Fact]
    public async Task AnnotationsOnly_IsHeardNothing_WithTheRawTextKept()
    {
        _vad.EndAfterBuffers = 1;
        _recognizer.Text = "[BLANK_AUDIO]";
        _capture.OnStart = Deliver(1);

        var result = await Pipeline().ListenAsync(CancellationToken.None, null, CancellationToken.None);

        Assert.True(result.HeardNothing);
        Assert.Equal("[BLANK_AUDIO]", result.RawText);
        Assert.Equal(ListenEnd.EndOfSpeech, result.EndedBy);
    }

    [Fact]
    public async Task Clean_IsApplied()
    {
        _vad.EndAfterBuffers = 1;
        _recognizer.Text = "[music] Hello   there. ";
        _capture.OnStart = Deliver(1);

        var result = await Pipeline().ListenAsync(CancellationToken.None, null, CancellationToken.None);

        Assert.Equal("Hello there.", result.Text);
    }

    [Fact]
    public async Task RecognizerFails_IsAFailedResult()
    {
        _vad.EndAfterBuffers = 1;
        _recognizer.Fail = true;
        _capture.OnStart = Deliver(1);

        var result = await Pipeline().ListenAsync(CancellationToken.None, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("scripted", result.Detail);
        Assert.Equal(ListenEnd.EndOfSpeech, result.EndedBy);
    }

    [Fact]
    public async Task StartThrows_IsAFailedResult()
    {
        _capture.ThrowOnStart = true;

        var result = await Pipeline().ListenAsync(CancellationToken.None, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(ListenEnd.NoAudio, result.EndedBy);
        Assert.Contains("MMSYSERR", result.Detail);
        Assert.Empty(_recognizer.Received);
    }

    [Fact]
    public async Task VadThrows_TimersStillEndTheUtterance()
    {
        _vad.ThrowOnFeed = true;
        _capture.OnStart = DeliverForever();

        var result = await Pipeline().ListenAsync(CancellationToken.None, null, CancellationToken.None);

        Assert.True(result.HeardNothing);
        Assert.Equal(ListenEnd.NoSpeech, result.EndedBy);
    }

    [Fact]
    public async Task Reentrant_Throws()
    {
        var pipeline = Pipeline(Quick with { DeliveryWatchdog = TimeSpan.FromSeconds(5), NoSpeechTimeout = TimeSpan.FromSeconds(5), MaxUtterance = TimeSpan.FromSeconds(5) });
        using var cts = new CancellationTokenSource();
        var first = pipeline.ListenAsync(CancellationToken.None, null, cts.Token);
        while (!pipeline.IsListening)
        {
            await Task.Delay(5);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.ListenAsync(CancellationToken.None, null, CancellationToken.None));

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(pipeline.IsListening);
    }

    [Fact]
    public async Task ListensAgain_AfterAnUtterance()
    {
        _vad.EndAfterBuffers = 1;
        _capture.OnStart = Deliver(1);
        var pipeline = Pipeline();

        var first = await pipeline.ListenAsync(CancellationToken.None, null, CancellationToken.None);
        var second = await pipeline.ListenAsync(CancellationToken.None, null, CancellationToken.None);

        Assert.True(first.Ok && second.Ok);
        Assert.Equal(2, _capture.Started);
        Assert.Equal(2, _vad.Resets);
        Assert.Equal(new[] { "start", "stop", "transcribe", "start", "stop", "transcribe" }, _log);
    }

    // ── Seeds (the wake word) ───────────────────────────────────────────────

    private static byte[] Seed(int bytes, byte value)
    {
        var seed = new byte[bytes];
        Array.Fill(seed, value);
        return seed;
    }

    [Fact]
    public async Task Seed_PrecedesTheLiveAudio_AndIsNotFedToTheVad()
    {
        _vad.EndAfterBuffers = 2;
        _capture.OnStart = Deliver(2);

        var result = await Pipeline().ListenAsync(CancellationToken.None, null, CancellationToken.None, Seed(3200, 9));

        Assert.True(result.Ok, result.Detail);
        Assert.Equal(ListenEnd.EndOfSpeech, result.EndedBy);
        var pcm = _recognizer.Received[0];
        Assert.Equal(3200 + 2 * Buffer50, pcm.Length);
        Assert.Equal(9, pcm[0]);
        Assert.Equal(9, pcm[3199]);
        Assert.Equal(0, pcm[3200]);
        Assert.Equal(2, _vad.Fed);                    // the seed never reached the VAD
        Assert.Equal(TimeSpan.FromMilliseconds(200), result.Audio);
        Assert.Equal(new[] { "start", "stop", "transcribe" }, _log);
    }

    [Fact]
    public async Task Seed_CountsTowardTheMaxUtterance()
    {
        // 400 ms cap = 12 800 bytes; a 12 000-byte seed leaves room for half a buffer.
        _capture.OnStart = Deliver(2);

        var result = await Pipeline().ListenAsync(CancellationToken.None, null, CancellationToken.None, Seed(12_000, 1));

        Assert.Equal(ListenEnd.MaxDuration, result.EndedBy);
        Assert.Equal(12_800, _recognizer.Received[0].Length);
    }

    [Fact]
    public async Task Seed_WithTheRequestAlreadySpoken_IsTranscribedWithoutTheMicrophone()
    {
        using var finished = new CancellationTokenSource();
        finished.Cancel();
        var phases = new List<string>();

        var result = await Pipeline().ListenAsync(finished.Token, phases.Add, CancellationToken.None, Seed(3200, 5));

        Assert.True(result.Ok, result.Detail);
        Assert.Equal(ListenEnd.Seeded, result.EndedBy);
        Assert.Equal("hello", result.Text);
        Assert.Equal(0, _capture.Started);
        Assert.Equal(0, _vad.Resets);
        Assert.Equal(new[] { "transcribe" }, _log);
        Assert.Equal(3200, _recognizer.Received[0].Length);
        Assert.Equal(TimeSpan.FromMilliseconds(100), result.Audio);
        Assert.Equal(new[] { VoicePipeline.TranscribingLabel }, phases);
    }

    [Fact]
    public async Task Seed_WithTheRequestAlreadySpoken_IsCappedAtTheMaxUtterance()
    {
        using var finished = new CancellationTokenSource();
        finished.Cancel();

        var result = await Pipeline().ListenAsync(finished.Token, null, CancellationToken.None, Seed(20_000, 5));

        Assert.Equal(ListenEnd.Seeded, result.EndedBy);
        Assert.Equal(12_800, _recognizer.Received[0].Length);
    }

    [Fact]
    public async Task PreFinished_WithoutASeed_StillOpensTheMicrophone_AndEndsByKey()
    {
        using var finished = new CancellationTokenSource();
        finished.Cancel();
        _capture.OnStart = Deliver(1);

        var result = await Pipeline().ListenAsync(finished.Token, null, CancellationToken.None, Array.Empty<byte>());

        Assert.Equal(ListenEnd.Key, result.EndedBy);
        Assert.Equal(1, _capture.Started);
    }

    [Fact]
    public async Task Seed_CancelledBeforeTranscription_Throws()
    {
        using var finished = new CancellationTokenSource();
        finished.Cancel();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Pipeline().ListenAsync(finished.Token, null, cts.Token, Seed(3200, 5)));

        Assert.Empty(_recognizer.Received);
    }

    [Fact]
    public void Defaults_ArePinned()
    {
        var d = VoicePipelineOptions.Default;
        Assert.Equal(TimeSpan.FromSeconds(10), d.NoSpeechTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), d.MaxUtterance);
        Assert.Equal(TimeSpan.FromSeconds(2), d.DeliveryWatchdog);
        Assert.Contains("Privacy & security > Microphone", VoicePipeline.NoAudioDetail);
        Assert.False(ListenResult.Failed(ListenEnd.NoAudio, TimeSpan.Zero, TimeSpan.Zero, "x").HeardNothing);
    }
}
