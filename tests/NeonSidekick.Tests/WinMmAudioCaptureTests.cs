using NeonSidekick.Audio;

namespace NeonSidekick.Tests;

/// <summary>
/// The device-bound facts run only where a microphone exists. The plain facts pin the lifecycle
/// contract every implementation of <see cref="IAudioCapture"/> must honour.
/// </summary>
public class WinMmAudioCaptureTests
{
    [Fact]
    public void Whisper_Is16kMono16()
    {
        var f = PcmFormat.Whisper;
        Assert.Equal(16000, f.SampleRate);
        Assert.Equal(2, f.BlockAlign);
        Assert.Equal(32000, f.BytesPerSecond);
        Assert.Equal(1600, f.BytesFor(50));
        Assert.Equal("16000 Hz, 16-bit, mono", f.ToString());
    }

    [Theory]
    [InlineData(0, 16, 1)]
    [InlineData(16000, 24, 1)]
    [InlineData(16000, 16, 0)]
    public void Ctor_RejectsUnsupportedFormats(int rate, int bits, int channels)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WinMmAudioCapture(new PcmFormat(rate, bits, channels)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WinMmAudioCapture(PcmFormat.Whisper, 0));
    }

    [Fact]
    public void Stop_WhenNotStarted_IsSafe_AndDisposeTwiceIsSafe()
    {
        var capture = new WinMmAudioCapture(PcmFormat.Whisper);
        capture.Stop();
        Assert.False(capture.IsCapturing);
        capture.Dispose();
        capture.Dispose();

        Assert.Throws<ObjectDisposedException>(capture.Start);
    }

    [WindowsFact]
    public void InputDeviceCount_IsNonNegative()
    {
        Assert.True(WinMmAudioCapture.InputDeviceCount() >= 0);
    }

    [AudioInputDeviceFact]
    public async Task Start_DeliversBuffersOfTheExpectedSize()
    {
        using var capture = new WinMmAudioCapture(PcmFormat.Whisper);
        var sizes = new List<int>();
        capture.DataAvailable += (_, count) => { lock (sizes) { sizes.Add(count); } };

        capture.Start();
        Assert.True(capture.IsCapturing);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            lock (sizes)
            {
                if (sizes.Count >= 5)
                {
                    break;
                }
            }

            await Task.Delay(20);
        }

        lock (sizes)
        {
            Assert.True(sizes.Count >= 5, $"only {sizes.Count} buffers in 3 s");
            Assert.All(sizes, s => Assert.Equal(1600, s));
        }
    }

    [AudioInputDeviceFact]
    public async Task Stop_EndsDelivery_AndStartWorksAgain()
    {
        using var capture = new WinMmAudioCapture(PcmFormat.Whisper);
        int delivered = 0;
        capture.DataAvailable += (_, _) => Interlocked.Increment(ref delivered);

        capture.Start();
        await Task.Delay(300);
        capture.Stop();
        capture.Stop();
        Assert.False(capture.IsCapturing);
        int afterStop = Volatile.Read(ref delivered);
        Assert.True(afterStop > 0);

        await Task.Delay(200);
        Assert.Equal(afterStop, Volatile.Read(ref delivered));   // nothing after Stop returned

        capture.Start();
        await Task.Delay(300);
        Assert.True(Volatile.Read(ref delivered) > afterStop);
        Assert.True(capture.IsCapturing);
    }

    [AudioInputDeviceFact]
    public void Dispose_StopsCapture()
    {
        var capture = new WinMmAudioCapture(PcmFormat.Whisper);
        capture.Start();
        capture.Dispose();
        Assert.False(capture.IsCapturing);
    }

    [AudioInputDeviceFact]
    public void ProbeDefaultDevice_OpensAndCloses()
    {
        Assert.Equal(WinMmNative.MmsyserrNoError, WinMmAudioCapture.ProbeDefaultDevice(PcmFormat.Whisper));
    }
}
