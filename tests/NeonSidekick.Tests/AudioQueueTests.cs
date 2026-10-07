using NeonSidekick.App;
using NeonSidekick.Audio;

namespace NeonSidekick.Tests;

/// <summary>
/// The macOS sound backend (2026-10-07): the pure decisions on every OS — the playback ledger, the silence watch, the
/// permission sentences — and the AudioQueue pair itself on a Mac with devices. The interface contract is
/// <c>WinMmAudioPlaybackTests</c>'/<c>WinMmAudioCaptureTests</c>' device facts, which run the platform's default backend.
/// </summary>
public class PlaybackLedgerTests
{
    [Fact]
    public void Bytes_MoveFromQueuedToInFlightToDone_NeverDippingToZeroOnTheWay()
    {
        var ledger = new PlaybackLedger(2);
        ledger.Queue(300);
        Assert.Equal(300, ledger.BufferedBytes);

        long first = ledger.Dispatch(ledger.FreeBuffer(), 100);
        Assert.Equal(300, ledger.BufferedBytes);
        Assert.Equal(100, ledger.InFlightBytes);

        long second = ledger.Dispatch(ledger.FreeBuffer(), 100);
        Assert.Equal(-1, ledger.FreeBuffer());

        Assert.True(ledger.Complete(first));
        Assert.Equal(200, ledger.BufferedBytes);
        Assert.False(ledger.Complete(first));   // a buffer handed back twice counts once

        long third = ledger.Dispatch(ledger.FreeBuffer(), 100);
        Assert.True(ledger.Complete(second));
        Assert.True(ledger.Complete(third));
        Assert.Equal(0, ledger.BufferedBytes);
    }

    [Fact]
    public void Clear_IsZeroAtOnce_AndVoidsEveryOutstandingTicket()
    {
        var ledger = new PlaybackLedger(4);
        ledger.Queue(1000);
        long stale = ledger.Dispatch(0, 200);

        ledger.Clear();
        Assert.Equal(0, ledger.BufferedBytes);
        Assert.Equal(0, ledger.FreeBuffer());

        // The same buffer reused after the clear: the flushed callback's late ticket must not free it or credit it.
        ledger.Queue(50);
        long fresh = ledger.Dispatch(0, 50);
        Assert.False(ledger.Complete(stale));
        Assert.Equal(50, ledger.BufferedBytes);
        Assert.True(ledger.Complete(fresh));
        Assert.Equal(0, ledger.BufferedBytes);
    }

    [Fact]
    public void Abandon_FreesTheBuffer_AndDropsItsBytes()
    {
        var ledger = new PlaybackLedger(1);
        ledger.Queue(10);
        long ticket = ledger.Dispatch(0, 10);
        ledger.Abandon(ticket);
        Assert.Equal(0, ledger.BufferedBytes);
        Assert.Equal(0, ledger.FreeBuffer());
    }

    [Fact]
    public void DropInFlight_KeepsTheQueuedBytes_AndVoidsTheBuffersTickets()
    {
        var ledger = new PlaybackLedger(2);
        ledger.Queue(500);
        long ticket = ledger.Dispatch(0, 100);
        ledger.DropInFlight();
        Assert.Equal(400, ledger.BufferedBytes);
        Assert.Equal(0, ledger.InFlightBytes);
        Assert.False(ledger.Complete(ticket));
        Assert.Equal(0, ledger.FreeBuffer());
    }

    [Fact]
    public void Guards_RejectNonsense()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlaybackLedger(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PlaybackLedger(0x10000));
        var ledger = new PlaybackLedger(1);
        ledger.Queue(0);
        ledger.Queue(-5);
        Assert.Equal(0, ledger.BufferedBytes);
        Assert.Throws<ArgumentOutOfRangeException>(() => ledger.Dispatch(0, 0));
        ledger.Dispatch(0, 1);
        Assert.Throws<InvalidOperationException>(() => ledger.Dispatch(0, 1));
        Assert.False(ledger.Complete(0xFFFF));   // an index past the buffers
    }

    [Fact]
    public async Task ConcurrentCompletes_AndClears_NeverGoNegative()
    {
        var ledger = new PlaybackLedger(4);
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        long minimum = 0;
        var tickets = new System.Collections.Concurrent.ConcurrentQueue<long>();

        var producer = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                ledger.Queue(10);
                int free = ledger.FreeBuffer();
                if (free >= 0)
                {
                    try
                    {
                        tickets.Enqueue(ledger.Dispatch(free, 10));
                    }
                    catch (InvalidOperationException)
                    {
                        // Taken between the look and the dispatch by nobody here, but harmless if it were.
                    }
                }
            }
        });
        var completer = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (tickets.TryDequeue(out long ticket))
                {
                    ledger.Complete(ticket);
                }

                minimum = Math.Min(minimum, ledger.BufferedBytes);
            }
        });
        var clearer = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                ledger.Clear();
                await Task.Delay(5);
            }
        });

        await Task.WhenAll(producer, completer, clearer);
        Assert.Equal(0, minimum);
    }
}

public class SilenceWatchTests
{
    private static readonly PcmFormat Format = PcmFormat.Whisper;

    [Fact]
    public void ASecondOfExactZeros_Trips_Once()
    {
        var watch = new SilenceWatch(Format);
        var buffer = new byte[Format.BytesFor(50)];
        int trips = 0;
        for (int i = 0; i < 40; i++)
        {
            trips += watch.Feed(buffer) ? 1 : 0;
            if (i == 18)
            {
                Assert.False(watch.Tripped);   // 950 ms
            }
        }

        Assert.Equal(1, trips);
        Assert.True(watch.Tripped);
    }

    [Fact]
    public void AQuietRoom_NeverTrips()
    {
        // A real microphone's noise floor: tiny values, now and then a zero, never a second of them.
        var watch = new SilenceWatch(Format);
        var random = new Random(7);
        var buffer = new byte[Format.BytesFor(50)];
        for (int i = 0; i < 200; i++)
        {
            for (int b = 0; b < buffer.Length; b += 2)
            {
                short sample = (short)random.Next(-3, 4);
                buffer[b] = (byte)(sample & 0xFF);
                buffer[b + 1] = (byte)((sample >> 8) & 0xFF);
            }

            Assert.False(watch.Feed(buffer));
        }
    }

    [Fact]
    public void ANonZeroByte_StartsTheCountOver_FromWhereItSits()
    {
        var watch = new SilenceWatch(Format, TimeSpan.FromMilliseconds(100));   // 3200 bytes
        var zeros = new byte[3000];
        Assert.False(watch.Feed(zeros));
        var tail = new byte[400];
        tail[100] = 1;   // 299 zero bytes after it
        Assert.False(watch.Feed(tail));
        Assert.False(watch.Feed(new byte[2900]));   // 3199
        Assert.True(watch.Feed(new byte[1]));
    }

    [Fact]
    public void HeardSound_AnyNonZeroByte_EvenAfterTripping()
    {
        var watch = new SilenceWatch(Format, TimeSpan.FromMilliseconds(50));
        Assert.True(watch.Feed(new byte[1600]));
        Assert.False(watch.HeardSound);
        var sound = new byte[10];
        sound[3] = 9;
        Assert.False(watch.Feed(sound));
        Assert.True(watch.HeardSound);
        watch.Reset();
        Assert.False(watch.HeardSound);
    }

    [Fact]
    public void Reset_WatchesAgain()
    {
        var watch = new SilenceWatch(Format, TimeSpan.FromMilliseconds(50));
        Assert.True(watch.Feed(new byte[1600]));
        Assert.False(watch.Feed(new byte[1600]));
        watch.Reset();
        Assert.False(watch.Tripped);
        Assert.True(watch.Feed(new byte[1600]));
    }
}

public class MicrophoneAccessTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData(MicrophoneAccess.NotDetermined, false)]
    [InlineData(MicrophoneAccess.Authorized, false)]
    [InlineData(MicrophoneAccess.Denied, true)]
    [InlineData(MicrophoneAccess.Restricted, true)]
    public void Refusal_OnlyForDeniedAndRestricted(long? status, bool refused)
    {
        Assert.Equal(refused, MicrophoneAccess.Refusal(status, "Terminal") is not null);
    }

    [Fact]
    public void SilenceVerdict_WaitsWhileTheQuestionIsOpen_AndOtherwiseSaysWhy()
    {
        Assert.Null(MicrophoneAccess.SilenceVerdict(MicrophoneAccess.NotDetermined, "Terminal"));
        Assert.Equal(MicrophoneText.Denied("iTerm2"), MicrophoneAccess.SilenceVerdict(MicrophoneAccess.Denied, "iTerm2"));
        Assert.Equal(MicrophoneText.Restricted, MicrophoneAccess.SilenceVerdict(MicrophoneAccess.Restricted, "Terminal"));
        Assert.Equal(MicrophoneText.Silent, MicrophoneAccess.SilenceVerdict(MicrophoneAccess.Authorized, "Terminal"));
        Assert.Equal(MicrophoneText.Silent, MicrophoneAccess.SilenceVerdict(null, "Terminal"));
    }

    [Fact]
    public void TheSentences_ArePinned()
    {
        Assert.Equal(
            "Microphone access is off for Terminal, so voice input hears nothing. Turn Terminal on in System Settings › Privacy & Security › Microphone, then try again.",
            MicrophoneText.Denied("Terminal"));
        Assert.Equal("no input device", MicrophoneText.NoInputDevice);
        Assert.StartsWith("The microphone sends only silence. On a MacBook with its lid closed", MicrophoneText.Silent, StringComparison.Ordinal);
        Assert.StartsWith("Microphone access is restricted on this Mac", MicrophoneText.Restricted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Apple_Terminal", "Terminal")]
    [InlineData("iTerm.app", "iTerm2")]
    [InlineData("vscode", "Visual Studio Code")]
    [InlineData("WezTerm", "WezTerm")]
    [InlineData("ghostty", "Ghostty")]
    [InlineData(null, "your terminal app")]
    [InlineData("tmux", "your terminal app")]
    public void TerminalName_FromTermProgram(string? termProgram, string expected)
    {
        Assert.Equal(expected, MicrophoneText.TerminalName(termProgram));
    }
}

public class AudioQueueTextTests
{
    [Fact]
    public void Failed_AddsTheFourCharacterCode_WhenThereIsOne()
    {
        Assert.Equal("AudioQueueStart failed with OSStatus 560227702 ('!dev').", AudioQueueText.Failed("AudioQueueStart", 0x21646576));
        Assert.Equal("AudioQueueNewOutput failed with OSStatus -66681.", AudioQueueText.Failed("AudioQueueNewOutput", -66681));
        Assert.Equal("", AudioQueueText.FourCharCode(0));
    }

    [Fact]
    public void Stalled_SaysHowLongAndHowMuch()
    {
        Assert.Equal("The output queue handed nothing back for 3.2 s; 4392 bytes in its buffers were dropped and it restarted.", AudioQueueText.Stalled(4392, TimeSpan.FromMilliseconds(3200)));
    }

    [Fact]
    public void OutputChanged_NamesBothDevices()
    {
        Assert.Equal("The default output changed (device 96 to 101); the queue reopened on it.", AudioQueueText.OutputChanged(96, 101));
    }
}

/// <summary>The AudioQueue pair itself, on a Mac (2026-10-07). The contract shared with WinMM is in the WinMM test classes.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class AudioQueueDeviceTests
{
    private static async Task WaitForDrainAsync(IAudioPlayback playback, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        while (playback.BufferedBytes > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
    }

    [MacFact]
    public void Playback_GuardsAndLifecycle_WithoutADevice()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioQueuePlayback(new PcmFormat(0, 16, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioQueuePlayback(PcmFormat.Kokoro, 0));
        var playback = new AudioQueuePlayback(PcmFormat.Kokoro);
        playback.Write(new byte[4800], 4800);
        Assert.Equal(0, playback.BufferedBytes);
        Assert.False(playback.IsPlaying);
        playback.Stop();
        playback.ClearBuffer();
        playback.Dispose();
        playback.Dispose();
        Assert.Throws<ObjectDisposedException>(playback.Start);
    }

    [MacFact]
    public void Capture_GuardsAndLifecycle_WithoutADevice()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioQueueCapture(new PcmFormat(16000, 24, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioQueueCapture(PcmFormat.Whisper, 0));
        var capture = new AudioQueueCapture(PcmFormat.Whisper);
        capture.Stop();
        Assert.False(capture.IsCapturing);
        capture.Dispose();
        capture.Dispose();
        Assert.Throws<ObjectDisposedException>(capture.Start);
    }

    [MacFact]
    public void Permission_ReadsAStatus()
    {
        Assert.InRange(AudioQueueNative.MicrophoneAuthorization() ?? -1, MicrophoneAccess.NotDetermined, MicrophoneAccess.Authorized);
    }

    /// <summary>
    /// The queue stopped after a second of nothing plays the next reply in real time (2026-10-07). Draining is not enough:
    /// a paused queue started again handed 0.8 s back in 32 ms, unplayed, and the user's timer alert was never heard.
    /// </summary>
    [AudioDeviceFact(MacOnly = true)]
    public async Task Playback_StoppedWhenIdle_PlaysTheNextReplyInRealTime()
    {
        using var playback = new AudioQueuePlayback(PcmFormat.Kokoro);
        var tone = AudioCheck.Tone(PcmFormat.Kokoro, 400, AudioCheck.ToneHz, AudioCheck.ToneAmplitude);

        playback.Start();
        var first = System.Diagnostics.Stopwatch.StartNew();
        playback.Write(tone, tone.Length);
        await WaitForDrainAsync(playback, TimeSpan.FromSeconds(3));
        Assert.Equal(0, playback.BufferedBytes);
        Assert.True(first.ElapsedMilliseconds >= 300, $"the first tone drained in {first.ElapsedMilliseconds} ms");

        await Task.Delay(AudioQueuePlayback.IdleStop + TimeSpan.FromMilliseconds(400));
        var second = System.Diagnostics.Stopwatch.StartNew();
        playback.Write(tone, tone.Length);
        Assert.True(playback.BufferedBytes > 0);
        await WaitForDrainAsync(playback, TimeSpan.FromSeconds(3));
        Assert.Equal(0, playback.BufferedBytes);
        Assert.True(second.ElapsedMilliseconds >= 300, $"the tone after the idle stop drained in {second.ElapsedMilliseconds} ms: thrown away, not played");
    }

    /// <summary>Many small writes in a row (a synthesizer's stream) all drain, split across the four buffers.</summary>
    [AudioDeviceFact(MacOnly = true)]
    public async Task Playback_ManySmallWrites_AllDrain()
    {
        using var playback = new AudioQueuePlayback(PcmFormat.Kokoro);
        var tone = AudioCheck.Tone(PcmFormat.Kokoro, 400, AudioCheck.ToneHz, AudioCheck.ToneAmplitude);
        playback.Start();
        for (int offset = 0; offset < tone.Length; offset += 1234)
        {
            int count = Math.Min(1234, tone.Length - offset);
            playback.Write(tone[offset..(offset + count)], count);
        }

        await WaitForDrainAsync(playback, TimeSpan.FromSeconds(3));
        Assert.Equal(0, playback.BufferedBytes);
    }

    /// <summary>A subscriber that stops the capture from inside the event (the pump's own thread) neither deadlocks nor sees another buffer.</summary>
    [AudioInputDeviceFact(MacOnly = true)]
    public async Task Capture_StopFromInsideTheEvent_EndsDelivery()
    {
        using var capture = new AudioQueueCapture(PcmFormat.Whisper);
        int delivered = 0;
        var stopped = new TaskCompletionSource();
        capture.DataAvailable += (_, _) =>
        {
            if (Interlocked.Increment(ref delivered) == 3)
            {
                capture.Stop();
                stopped.TrySetResult();
            }
        };

        capture.Start();
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(capture.IsCapturing);
        await Task.Delay(200);
        Assert.Equal(3, Volatile.Read(ref delivered));
    }
}
