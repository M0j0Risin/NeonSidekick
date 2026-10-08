using NeonSidekick.App;
using NeonSidekick.Audio;

namespace NeonSidekick.Tests;

public class PcmFormatTests
{
    [Fact]
    public void Kokoro_Is24kMono16()
    {
        var f = PcmFormat.Kokoro;
        Assert.Equal(24000, f.SampleRate);
        Assert.Equal(2, f.BlockAlign);
        Assert.Equal(48000, f.BytesPerSecond);
        Assert.Equal(4800, f.BytesFor(100));
        Assert.Equal("24000 Hz, 16-bit, mono", f.ToString());
    }
}

/// <summary>
/// The device-bound facts run only where an output device exists, on the platform's own backend
/// (<see cref="AudioSupport.DefaultPlayback"/>: WinMM on Windows, AudioQueue on macOS since 2026-10-07). They pin the
/// lifecycle contract every implementation of <see cref="IAudioPlayback"/> must honour.
/// </summary>
public class WinMmAudioPlaybackTests
{
    private static async Task WaitForDrainAsync(IAudioPlayback playback, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        while (playback.BufferedBytes > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
    }

    [Fact]
    public void Write_BeforeStart_IsIgnored()
    {
        using var playback = new WinMmAudioPlayback(PcmFormat.Kokoro);
        playback.Write(new byte[4800], 4800);

        Assert.Equal(0, playback.BufferedBytes);
        Assert.False(playback.IsPlaying);
    }

    [Fact]
    public void Stop_WhenNotStarted_IsSafe_AndDisposeTwiceIsSafe()
    {
        var playback = new WinMmAudioPlayback(PcmFormat.Kokoro);
        playback.Stop();
        playback.ClearBuffer();
        playback.Dispose();
        playback.Dispose();

        Assert.Throws<ObjectDisposedException>(playback.Start);
    }

    [Theory]
    [InlineData(0, 16, 1)]
    [InlineData(24000, 24, 1)]
    [InlineData(24000, 16, 0)]
    public void Ctor_RejectsUnsupportedFormats(int rate, int bits, int channels)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WinMmAudioPlayback(new PcmFormat(rate, bits, channels)));
    }

    [AudioDeviceFact]
    public async Task Tone_DrainsToZero_OnTheDefaultDevice()
    {
        using var playback = AudioSupport.DefaultPlayback(PcmFormat.Kokoro);
        var tone = AudioCheck.Tone(PcmFormat.Kokoro, 300, AudioCheck.ToneHz, AudioCheck.ToneAmplitude);

        playback.Start();
        playback.Write(tone, tone.Length);
        Assert.True(playback.IsPlaying);

        await WaitForDrainAsync(playback, TimeSpan.FromSeconds(3));

        Assert.Equal(0, playback.BufferedBytes);
    }

    [AudioDeviceFact]
    public void ClearBuffer_DropsQueuedBytesImmediately()
    {
        using var playback = AudioSupport.DefaultPlayback(PcmFormat.Kokoro);
        var tone = AudioCheck.Tone(PcmFormat.Kokoro, 2000, AudioCheck.ToneHz, AudioCheck.ToneAmplitude);

        playback.Start();
        playback.Write(tone, tone.Length);
        Assert.True(playback.BufferedBytes > 0);

        playback.ClearBuffer();

        Assert.Equal(0, playback.BufferedBytes);
        Assert.True(playback.IsPlaying, "ClearBuffer silences; it does not close the device");
    }

    [AudioDeviceFact]
    public async Task StopThenStart_PlaysAgain()
    {
        using var playback = AudioSupport.DefaultPlayback(PcmFormat.Kokoro);
        var tone = AudioCheck.Tone(PcmFormat.Kokoro, 200, AudioCheck.ToneHz, AudioCheck.ToneAmplitude);

        playback.Start();
        playback.Write(tone, tone.Length);
        playback.Stop();
        Assert.False(playback.IsPlaying);
        Assert.Equal(0, playback.BufferedBytes);

        playback.Start();
        playback.Write(tone, tone.Length);
        await WaitForDrainAsync(playback, TimeSpan.FromSeconds(3));

        Assert.Equal(0, playback.BufferedBytes);
        Assert.True(playback.IsPlaying);
    }

    [AudioDeviceFact]
    public void ProbeDefaultDevice_OpensAndCloses()
    {
        if (OperatingSystem.IsMacOS())
        {
            Assert.Equal(0, AudioQueuePlayback.ProbeDefaultDevice(PcmFormat.Kokoro));
            return;
        }

        Assert.Equal(WinMmNative.MmsyserrNoError, WinMmAudioPlayback.ProbeDefaultDevice(PcmFormat.Kokoro));
    }
}
