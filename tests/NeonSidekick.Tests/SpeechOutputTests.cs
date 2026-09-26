using NeonSidekick.Audio;
using NeonSidekick.Diagnostics;
using NeonSidekick.Speech;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

public class SpeechOutputTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly FakeSynthesizer _synth = new();
    private readonly FakeAudioPlayback _playback = new();

    private SpeechOutput Output(CancellationToken token = default, Action<string>? onFailure = null) =>
        new(_synth, _playback, "af_heart", 1.1, token, onFailure);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "condition not met in time");
    }

    // ── The play head's sentence (the /speak position, 2026-09-17) ──────────

    [Fact]
    public async Task Speak_QueuesTheSentenceWhole_AndPlayingChunk_FollowsThePlayHead()
    {
        _playback.HoldBytes = true;   // nothing plays until released
        using var cts = new CancellationTokenSource();
        var output = Output(cts.Token);

        Assert.Equal(0, output.PlayingChunk);
        output.Speak("One. Still one.");   // a pre-split sentence is never re-chunked
        output.Speak("Two.");
        output.Speak("Three.");
        output.CompleteAdding();
        await WaitUntilAsync(() => output.ChunksStarted == 3 && _playback.Writes.Count == 3);

        Assert.Equal(new[] { "One. Still one.", "Two.", "Three." }, _synth.SpokenText);
        Assert.Equal(14_400, output.WrittenBytes);
        // Nothing heard yet: the first sentence counts from its first byte.
        Assert.Equal(1, output.PlayingChunk);
        _playback.Release(4800);   // the first sentence heard whole
        Assert.Equal(2, output.PlayingChunk);
        _playback.Release(100);    // into the second
        Assert.Equal(2, output.PlayingChunk);
        _playback.Release();       // everything heard: the last sentence
        Assert.Equal(3, output.PlayingChunk);
        Assert.Equal(0, output.StoppedAtChunk);

        cts.Cancel();
    }

    [Fact]
    public async Task StoppedAtChunk_IsTheSentenceSounding_WhenTheDeviceWasFirstSilenced()
    {
        _playback.HoldBytes = true;
        using var cts = new CancellationTokenSource();
        var output = Output(cts.Token);
        output.Speak("One.");
        output.Speak("Two.");
        output.Speak("Three.");
        output.CompleteAdding();
        await WaitUntilAsync(() => output.ChunksStarted == 3 && _playback.Writes.Count == 3);
        _playback.Release(4800 + 10);   // ten bytes into the second sentence

        cts.Cancel();                    // the queue's cancel path silences the device (StopAll)
        await output.Completion.WaitAsync(Timeout);

        // Taken before the buffer was cleared: after the clear the play head reads as the end.
        Assert.Equal(2, output.StoppedAtChunk);
        Assert.Equal(3, output.PlayingChunk);
        Assert.True(output.StoppedEarly);
        output.StopAll();   // a second stop keeps the first mark
        Assert.Equal(2, output.StoppedAtChunk);
    }

    [Fact]
    public async Task AnUnspeakableSentence_KeepsItsNumber_AndNeverCounts()
    {
        _playback.HoldBytes = true;
        using var cts = new CancellationTokenSource();
        var output = Output(cts.Token);
        output.Speak("One.");
        output.Speak("\U0001F44B");   // nothing speakable (an emoji alone): no audio, chunk 2 all the same
        output.Speak("Three.");
        output.CompleteAdding();
        await WaitUntilAsync(() => output.ChunksStarted == 3 && _playback.Writes.Count == 2);

        Assert.Equal(new[] { "One.", "Three." }, _synth.SpokenText);
        Assert.Equal(1, output.PlayingChunk);
        _playback.Release(4800);
        Assert.Equal(3, output.PlayingChunk);   // straight from 1 to 3
        Assert.Equal("One. Three.", output.SpokenNear(SpeechOutput.DefaultEchoLookBack));

        cts.Cancel();
    }

    // ── The echo probe (fed every byte written, at its stream position) ─────

    [Fact]
    public async Task Probe_IsFedEveryWrite_WithTheStreamPosition()
    {
        var detector = new FakeWakeWordDetector();
        using var probe = new EchoProbe(detector, "velora", PcmFormat.Kokoro);
        using var cts = new CancellationTokenSource();
        var output = Output(cts.Token);   // 4800 bytes per sentence
        output.Probe = probe;

        output.Feed("One. Two. Three.");
        output.CompleteAdding();
        await output.Completion.WaitAsync(Timeout);
        probe.Complete();
        await probe.Completion.WaitAsync(Timeout);

        Assert.Equal(14_400, output.WrittenBytes);
        Assert.Equal(PcmFormat.Kokoro, output.Format);
        // Three writes of 100 ms at 24 kHz, two 50 ms slices each at 16 kHz: the probe saw the whole stream.
        Assert.Equal(6, detector.Fed);
        Assert.Equal(6 * 1600, detector.BytesFed);
    }

    // ── SpokenNear (the interrupt's echo guard) ─────────────────────────────

    [Fact]
    public void SpokenNear_NothingFed_IsEmpty()
    {
        var output = Output();
        Assert.Equal("", output.SpokenNear(SpeechOutput.DefaultEchoLookBack));
        Assert.Equal(0, output.WrittenBytes);
        Assert.Equal(0, output.PlayedBytes);
    }

    [Fact]
    public async Task SpokenNear_FollowsThePlayHead_NotSynthesis()
    {
        _playback.HoldBytes = true;   // nothing plays until Release
        using var cts = new CancellationTokenSource();
        var output = Output(cts.Token);

        output.Feed("One. Two.");
        output.CompleteAdding();
        await WaitUntilAsync(() => output.ChunksStarted == 2 && _playback.Writes.Count == 2);

        // Both sentences are synthesised (2 × 4800 bytes) but the device has played nothing:
        // only the sentence at the play head counts, and it counts from its first byte.
        Assert.Equal(9600, output.WrittenBytes);
        Assert.Equal(0, output.PlayedBytes);
        Assert.Equal("One.", output.SpokenNear(SpeechOutput.DefaultEchoLookBack));

        _playback.Release();   // everything written has now been heard
        Assert.Equal(9600, output.PlayedBytes);
        Assert.Equal("One. Two.", output.SpokenNear(SpeechOutput.DefaultEchoLookBack));   // 3 s covers both
        Assert.Equal("Two.", output.SpokenNear(TimeSpan.Zero));                            // only the sentence ending at the play head
        Assert.Equal("One. Two.", output.SpokenNear(TimeSpan.FromMilliseconds(100)));      // 4800 bytes = 100 ms at 24 kHz: reaches One's end

        cts.Cancel();
    }

    [Fact]
    public async Task SpokenNear_AfterStopAll_CountsTheInFlightAudioAsHeard()
    {
        _playback.HoldBytes = true;
        using var cts = new CancellationTokenSource();
        var output = Output(cts.Token);
        output.Feed("One. Two.");
        output.CompleteAdding();
        await WaitUntilAsync(() => output.ChunksStarted == 2 && _playback.Writes.Count == 2);

        output.StopAll();

        Assert.Equal(9600, output.PlayedBytes);
        Assert.Equal("Two.", output.SpokenNear(TimeSpan.Zero));
        cts.Cancel();
    }

    [Fact]
    public async Task Feed_SentencesAreSynthesisedInOrder_AndWrittenToPlayback()
    {
        var output = Output();

        output.Feed("One. Two. Thr");
        output.Feed("ee.");
        output.CompleteAdding();
        await output.Completion.WaitAsync(Timeout);

        Assert.Equal(new[] { "One.", "Two.", "Three." }, _synth.SpokenText);
        Assert.All(_synth.Spoken, s => { Assert.Equal("af_heart", s.Voice); Assert.Equal(1.1, s.Speed); });
        Assert.Equal(3, _playback.Writes.Count);
        Assert.Equal(1, _playback.Started);
        Assert.Equal(0, _playback.Stopped);
        Assert.Equal(3, output.ChunksQueued);
        Assert.Equal(3, output.ChunksStarted);
        Assert.False(output.Failed);
    }

    [Fact]
    public async Task Feed_MarkdownAndEmoji_AreStrippedBeforeSynthesis()
    {
        var output = Output();

        output.Feed("**Hello** " + char.ConvertFromUtf32(0x1F44B) + " `world`. ");
        output.Feed("---\n");
        output.CompleteAdding();
        await output.Completion.WaitAsync(Timeout);

        Assert.Equal(new[] { "Hello world." }, _synth.SpokenText);
    }

    [Fact]
    public async Task Feed_AFencedCodeBlock_IsNeverSpoken_TheWordsAroundItAre()
    {
        var output = Output();

        output.Feed("Here is the code:\n``");
        output.Feed("`csharp\nConsole.WriteLine(\"Hi.\");\nvar x = 1;\n``");
        output.Feed("`\nIt prints a greeting.");
        output.CompleteAdding();
        await output.Completion.WaitAsync(Timeout);

        Assert.Equal(new[] { "Here is the code:", "It prints a greeting." }, _synth.SpokenText);
    }

    [Fact]
    public async Task Feed_ATable_IsNeverSpoken_TheWordsAroundItAre()
    {
        var output = Output();

        output.Feed("Two options:\n| Name | Speed |\n|--");
        output.Feed("-|---|\n| Fast. | Yes. |\n| Slow. |");
        output.Feed(" No. |\nPick the first.");
        output.CompleteAdding();
        await output.Completion.WaitAsync(Timeout);

        Assert.Equal(new[] { "Two options:", "Pick the first." }, _synth.SpokenText);
    }

    [Fact]
    public async Task Playback_IsStartedLazily_OnTheFirstChunk_AndNothingIsSpokenForNothing()
    {
        var output = Output();
        Assert.Equal(0, _playback.Started);

        output.CompleteAdding();
        await output.Completion.WaitAsync(Timeout);

        Assert.Equal(0, _playback.Started);
        Assert.Empty(_synth.Spoken);
    }

    [Fact]
    public async Task CompleteAdding_FlushesTheTail()
    {
        var output = Output();

        output.Feed("No terminator here");
        output.CompleteAdding();
        await output.Completion.WaitAsync(Timeout);

        Assert.Equal(new[] { "No terminator here" }, _synth.SpokenText);
    }

    [Fact]
    public async Task Completion_WaitsForTheDeviceToDrain_ThenCompletes()
    {
        _playback.HoldBytes = true;
        var output = Output();

        output.Feed("One. ");
        output.CompleteAdding();
        await WaitUntilAsync(() => _playback.Writes.Count == 1);
        await Task.Delay(100);
        Assert.False(output.Completion.IsCompleted, "must wait for the device, not just the queue");

        _playback.Release();
        await output.Completion.WaitAsync(Timeout);
        Assert.Equal(0, _playback.Stopped);
    }

    [Fact]
    public async Task Synthesis_RunsAheadOfPlayback()
    {
        _playback.HoldBytes = true;
        var output = Output();

        output.Feed("One. Two. ");
        output.CompleteAdding();

        // The second sentence is rendered while the first is still buffered in the device.
        await WaitUntilAsync(() => _synth.Spoken.Count == 2);
        Assert.True(_playback.BufferedBytes > 0);

        _playback.Release();
        await output.Completion.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Cancel_MidSynthesis_StopsPlayback_AndSkipsRemainingChunks()
    {
        using var cts = new CancellationTokenSource();
        var firstStarted = new TaskCompletionSource();
        _synth.OnSynthesize = async (text, ct) =>
        {
            if (text == "One.")
            {
                firstStarted.TrySetResult();
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
        };
        var output = Output(cts.Token);

        output.Feed("One. Two. Three. ");
        await firstStarted.Task.WaitAsync(Timeout);
        cts.Cancel();
        output.CompleteAdding();
        await output.Completion.WaitAsync(Timeout);

        Assert.True(output.Completion.IsCompletedSuccessfully);
        Assert.True(output.StoppedEarly, "Completion completes successfully on the cancellation path; StoppedEarly is how a caller tells");
        Assert.Equal(new[] { "One." }, _synth.SpokenText);
        Assert.True(_playback.Cleared >= 1, "the device must be flushed on cancellation");
        Assert.Equal(1, _playback.Stopped);
        Assert.False(output.Failed);
    }

    [Fact]
    public async Task Cancel_DuringTheSpokenTail_StopsPlayback()
    {
        using var cts = new CancellationTokenSource();
        _playback.HoldBytes = true;
        var output = Output(cts.Token);

        output.Feed("One. ");
        output.CompleteAdding();
        await WaitUntilAsync(() => _playback.Writes.Count == 1);
        await Task.Delay(50);
        Assert.False(output.Completion.IsCompleted);
        Assert.False(output.StoppedEarly);

        cts.Cancel();
        await output.Completion.WaitAsync(Timeout);

        Assert.True(output.StoppedEarly);
        Assert.True(_playback.Cleared >= 1);
        Assert.Equal(1, _playback.Stopped);
    }

    [Fact]
    public async Task Completion_WithoutCancellation_IsNotStoppedEarly()
    {
        var output = Output(CancellationToken.None);

        output.Feed("One. Two. ");
        output.CompleteAdding();
        await output.Completion.WaitAsync(Timeout);

        Assert.False(output.StoppedEarly);
        Assert.Equal(new[] { "One.", "Two." }, _synth.SpokenText);
    }

    [Fact]
    public async Task SynthesisFailure_WarnsOnce_InvokesOnFailure_AndSkipsTheRest()
    {
        _synth.FailFromCall = 1;
        var warnings = new List<DiagnosticEvent>();
        void Capture(DiagnosticEvent e)
        {
            if (e.Category == SpeechOutput.Category && e.Level == DiagnosticLevel.Warning)
            {
                warnings.Add(e);
            }
        }

        var failures = new List<string>();
        DiagnosticLog.Emitted += Capture;
        try
        {
            var output = Output(onFailure: failures.Add);

            output.Feed("One. Two. Three. ");
            output.CompleteAdding();
            await output.Completion.WaitAsync(Timeout);

            Assert.True(output.Failed);
            Assert.Equal(new[] { "One." }, _synth.SpokenText);   // the failed call; the rest never sent
            Assert.Single(warnings);
            Assert.Contains("HTTP 500", warnings[0].Message);
            Assert.Single(failures);
            Assert.Contains("HTTP 500", failures[0]);
            Assert.Empty(_playback.Writes);
        }
        finally
        {
            DiagnosticLog.Emitted -= Capture;
        }
    }

    [Fact]
    public async Task StartThrows_IsReportedAsFailure_NotAnException()
    {
        _playback.ThrowOnStart = true;
        string? failure = null;
        var output = Output(onFailure: d => failure = d);

        output.Feed("One. ");
        output.CompleteAdding();
        await output.Completion.WaitAsync(Timeout);

        Assert.True(output.Completion.IsCompletedSuccessfully);
        Assert.True(output.Failed);
        Assert.Contains("failed to start", failure);
        Assert.Empty(_synth.Spoken);
    }

    [Fact]
    public async Task OnFailureThrowing_DoesNotFaultTheTurn()
    {
        _synth.FailFromCall = 1;
        var output = Output(onFailure: _ => throw new InvalidOperationException("handler bug"));

        output.Feed("One. ");
        output.CompleteAdding();
        await output.Completion.WaitAsync(Timeout);

        Assert.True(output.Completion.IsCompletedSuccessfully);
        Assert.True(output.Failed);
    }

    [Fact]
    public void Ctor_RejectsABlankVoice()
    {
        Assert.Throws<ArgumentException>(() => new SpeechOutput(_synth, _playback, " ", 1.0, CancellationToken.None));
    }
}
