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
        int code = await AudioCheck.RunAsync(_console, new WinMmAudioPlayback(PcmFormat.Kokoro));

        Assert.Equal(0, code);
        Assert.Contains(AudioCheck.DrainedLine, _console.Output);
    }

    /// <summary>The macOS build (2026-10-06): the default devices are WinMM's on Windows; elsewhere a refusal with the one sentence and no microphone.</summary>
    [Fact]
    public void AudioSupport_IsWinMm_OnWindows_AndARefusalElsewhere()
    {
        Assert.Equal(OperatingSystem.IsWindows(), AudioSupport.Available);
        if (!AudioSupport.Available)
        {
            var ex = Assert.Throws<PlatformNotSupportedException>(() => AudioSupport.DefaultPlayback(PcmFormat.Kokoro));
            Assert.Equal(AudioSupport.Unavailable, ex.Message);
            Assert.Throws<PlatformNotSupportedException>(() => AudioSupport.DefaultCapture(PcmFormat.Kokoro));
            Assert.Equal(0, AudioSupport.DefaultInputDeviceCount());
        }
    }
}
