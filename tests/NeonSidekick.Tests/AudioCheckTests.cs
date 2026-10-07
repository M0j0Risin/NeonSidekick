using NeonSidekick.App;
using NeonSidekick.Audio;
using NeonSidekick.Tests.Fakes;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

public class AudioCheckTests : IDisposable
{
    private readonly TestConsole _console = new();

    public AudioCheckTests() => _console.Profile.Width = 200;

    public void Dispose() => _console.Dispose();

    [Fact]
    public void Tone_Is500msOf24kMono16_AndStartsAtZero()
    {
        var pcm = AudioCheck.Tone(PcmFormat.Kokoro, AudioCheck.ToneMilliseconds, AudioCheck.ToneHz, AudioCheck.ToneAmplitude);

        Assert.Equal(24000, pcm.Length);              // 12000 frames × 2 bytes
        Assert.Equal(0, BitConverter.ToInt16(pcm, 0));

        // A quarter period of 440 Hz at 24 kHz is ~13.6 samples; sample 14 is near the peak and positive.
        short nearPeak = BitConverter.ToInt16(pcm, 14 * 2);
        Assert.InRange(nearPeak, 1400, 1500);

        // Little-endian: the low byte comes first.
        Assert.Equal((byte)(nearPeak & 0xFF), pcm[28]);
    }

    [Fact]
    public void Tone_Stereo_DuplicatesEachFrame()
    {
        var pcm = AudioCheck.Tone(new PcmFormat(8000, 16, 2), 10, 440, 1000);

        Assert.Equal(80 * 4, pcm.Length);
        Assert.Equal(BitConverter.ToInt16(pcm, 4), BitConverter.ToInt16(pcm, 6));
    }

    [Fact]
    public void Tone_Rejects8Bit()
    {
        Assert.Throws<ArgumentException>(() => AudioCheck.Tone(new PcmFormat(8000, 8, 1), 10, 440, 100));
    }

    [Fact]
    public async Task RunAsync_DrainsThroughTheSeam_ReturnsZero()
    {
        var playback = new FakeAudioPlayback();

        int code = await AudioCheck.RunAsync(_console, playback);

        Assert.Equal(0, code);
        Assert.Contains(AudioCheck.DrainedLine, _console.Output);
        Assert.Contains("queued 24000 bytes, 24000 drained, 0 left", _console.Output);
        Assert.Equal(1, playback.Started);
        Assert.Equal(24000, playback.WrittenBytes);
        Assert.True(playback.Disposed);
    }

    [Fact]
    public async Task RunAsync_BytesLeft_ReturnsOne_AndSaysNotAllPlayed()
    {
        var playback = new FakeAudioPlayback { HoldBytes = true };

        int code = await AudioCheck.RunAsync(_console, playback, drainBudget: TimeSpan.FromMilliseconds(120));

        Assert.Equal(1, code);
        Assert.Contains(AudioCheck.NotAllPlayedLine, _console.Output);
        Assert.Contains("0 drained, 24000 left", _console.Output);
        Assert.True(playback.Disposed);
    }

    [Fact]
    public async Task RunAsync_StartThrows_ReturnsOne()
    {
        var playback = new FakeAudioPlayback { ThrowOnStart = true };

        int code = await AudioCheck.RunAsync(_console, playback);

        Assert.Equal(1, code);
        Assert.Contains("Playback THREW InvalidOperationException", _console.Output);
        Assert.True(playback.Disposed);
    }

    [AudioDeviceFact]
    public async Task RunAsync_RealDevice_ReturnsZero()
    {
        int code = await AudioCheck.RunAsync(_console, AudioSupport.DefaultPlayback(PcmFormat.Kokoro));

        Assert.Equal(0, code);
        Assert.Contains(AudioCheck.DrainedLine, _console.Output);
    }

    /// <summary>
    /// The default devices (2026-10-06, the macOS build; 2026-10-07, sound on a Mac): WinMM's on Windows, AudioQueue's on
    /// macOS, and elsewhere a refusal with the one sentence and no microphone.
    /// </summary>
    [Fact]
    public void AudioSupport_IsWinMm_OnWindows_AudioQueue_OnMacOS_AndARefusalElsewhere()
    {
        Assert.Equal(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(), AudioSupport.Available);
        if (OperatingSystem.IsWindows())
        {
            Assert.IsType<WinMmAudioPlayback>(AudioSupport.DefaultPlayback(PcmFormat.Kokoro));
            Assert.IsType<WinMmAudioCapture>(AudioSupport.DefaultCapture(PcmFormat.Whisper));
            Assert.Equal("no wave-in device", AudioSupport.NoMicrophoneDetail);
            Assert.Null(AudioSupport.MicrophoneRefusal());
        }
        else if (OperatingSystem.IsMacOS())
        {
            using var playback = AudioSupport.DefaultPlayback(PcmFormat.Kokoro);
            using var capture = AudioSupport.DefaultCapture(PcmFormat.Whisper);
            Assert.IsType<AudioQueuePlayback>(playback);
            Assert.IsType<AudioQueueCapture>(capture);
            Assert.Equal(MicrophoneText.NoInputDevice, AudioSupport.NoMicrophoneDetail);
            Assert.InRange(AudioSupport.DefaultInputDeviceCount(), 0, 1);
            Assert.InRange(AudioSupport.DefaultOutputDeviceCount(), 0, 1);
        }
        else
        {
            var ex = Assert.Throws<PlatformNotSupportedException>(() => AudioSupport.DefaultPlayback(PcmFormat.Kokoro));
            Assert.Equal(AudioSupport.Unavailable, ex.Message);
            Assert.Throws<PlatformNotSupportedException>(() => AudioSupport.DefaultCapture(PcmFormat.Kokoro));
            Assert.Equal(0, AudioSupport.DefaultInputDeviceCount());
            Assert.Equal(AudioSupport.Unavailable, AudioSupport.NoMicrophoneDetail);
        }
    }

    [Fact]
    public void Unavailable_NamesBothBackends()
    {
        Assert.Equal("Audio needs Windows or macOS for now: this build has no sound output or microphone.", AudioSupport.Unavailable);
    }
}
