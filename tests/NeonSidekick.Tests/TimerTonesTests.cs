using NeonSidekick.Audio;
using NeonSidekick.Diagnostics;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Timers;

namespace NeonSidekick.Tests;

/// <summary>
/// The timer tone (2026-10-18, the user's ask: with TTS off a timer going off was silent): the tones' names, hints and rendering
/// (<see cref="TimerTones"/>), and the player (<see cref="TimerTonePlayer"/>) over a fake device and a manual clock.
/// </summary>
public class TimerTonesTests
{
    // ── The tones ───────────────────────────────────────────────────────────

    [Fact]
    public void Names_InMenuOrder_SilentTheDefault_EachWithAHint()
    {
        Assert.Equal(["silent", "chime", "bell", "beep", "alarm"], TimerTones.Names);
        Assert.Equal("silent", TimerTones.Default);
        Assert.All(TimerTones.Names, n => Assert.False(string.IsNullOrWhiteSpace(TimerTones.Describe(n))));
        Assert.Equal("no sound", TimerTones.Describe(TimerTones.Silent));
    }

    [Theory]
    [InlineData("chime", "chime")]
    [InlineData("  CHIME ", "chime")]
    [InlineData("Alarm", "alarm")]
    [InlineData("", "silent")]
    [InlineData(null, "silent")]
    [InlineData("klaxon", "silent")]
    public void Normalize_AnyCase_AndAnUnknownIsSilent(string? saved, string expected) => Assert.Equal(expected, TimerTones.Normalize(saved));

    [Theory]
    [InlineData("chime")]
    [InlineData("bell")]
    [InlineData("beep")]
    [InlineData("alarm")]
    public void Render_EachTone_WholeFrames_Short_Quiet_ALeadOfSilence_AndAQuietEnd(string name)
    {
        var format = PcmFormat.Kokoro;
        byte[] pcm = TimerTones.Render(name, format);

        Assert.NotEmpty(pcm);
        Assert.Equal(0, pcm.Length % format.BlockAlign);
        Assert.True(pcm.Length <= TimerTones.MaxSeconds * format.BytesPerSecond, name);
        var samples = Enumerable.Range(0, pcm.Length / 2).Select(i => (short)(pcm[2 * i] | (pcm[2 * i + 1] << 8))).ToArray();
        Assert.True(samples.Max(s => Math.Abs((int)s)) <= 0.26 * short.MaxValue, name);
        Assert.True(samples.Max(s => Math.Abs((int)s)) >= 0.24 * short.MaxValue, name);   // one fixed level: every tone near the same peak
        Assert.All(samples.Take(format.SampleRate / 20), s => Assert.Equal(0, s));       // the first 50 ms
        Assert.True(Math.Abs((int)samples[^1]) < 0.01 * short.MaxValue, name);
    }

    [Fact]
    public void Render_Silent_IsEmpty_AndAStereoFormatRepeatsEachSample()
    {
        Assert.Empty(TimerTones.Render(TimerTones.Silent, PcmFormat.Kokoro));
        Assert.Empty(TimerTones.Render("nonsense", PcmFormat.Kokoro));
        byte[] mono = TimerTones.Render(TimerTones.Beep, PcmFormat.Kokoro);
        byte[] stereo = TimerTones.Render(TimerTones.Beep, new PcmFormat(24000, 16, 2));
        Assert.Equal(mono.Length * 2, stereo.Length);
        Assert.Throws<ArgumentException>(() => TimerTones.Render(TimerTones.Beep, new PcmFormat(24000, 8, 1)));
    }

    // ── The player ──────────────────────────────────────────────────────────

    private readonly ManualTimeProvider _time = new();
    private readonly List<FakeAudioPlayback> _devices = [];

    private TimerTonePlayer Player(Action<FakeAudioPlayback>? setup = null) => new(format =>
    {
        var device = new FakeAudioPlayback { Format = format };
        setup?.Invoke(device);
        _devices.Add(device);
        return device;
    }, _time);

    [Fact]
    public async Task Play_StartsWritesTheClip_StopsAndDisposes()
    {
        using var player = Player();

        await player.PlayAsync(TimerTones.Chime, CancellationToken.None);

        var device = Assert.Single(_devices);
        Assert.Equal(1, device.Started);
        Assert.Equal(TimerTones.Render(TimerTones.Chime, TimerTonePlayer.Format), Assert.Single(device.Writes));
        Assert.Equal(1, device.Stopped);
        Assert.True(device.Disposed);
    }

    [Fact]
    public async Task Silent_AndNoFactory_NeverOpenADevice()
    {
        using var player = Player();
        using var none = new TimerTonePlayer(null, _time);

        await player.PlayAsync(TimerTones.Silent, CancellationToken.None);
        await player.PlayAsync("unknown", CancellationToken.None);
        await none.PlayAsync(TimerTones.Bell, CancellationToken.None);

        Assert.Empty(_devices);
    }

    [Fact]
    public async Task AStartThatThrows_NeverThrows_AndWarnsOnceAcrossTwoPlays()
    {
        var warnings = new List<string>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == TimerBoard.Category && e.Level == DiagnosticLevel.Warning) warnings.Add(e.Message); };
        DiagnosticLog.Emitted += capture;
        try
        {
            using var player = Player(d => d.ThrowOnStart = true);
            await player.PlayAsync(TimerTones.Beep, CancellationToken.None);
            await player.PlayAsync(TimerTones.Beep, CancellationToken.None);
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        Assert.Equal(TimerText.ToneUnavailableLogLine("waveOutOpen failed with MMSYSERR 6."), Assert.Single(warnings));
        Assert.All(_devices, d => Assert.True(d.Disposed));
    }

    [Fact]
    public async Task AFactoryThatThrows_NeverThrows()
    {
        using var player = new TimerTonePlayer(_ => throw new PlatformNotSupportedException(AudioSupport.Unavailable), _time);

        await player.PlayAsync(TimerTones.Alarm, CancellationToken.None);   // no throw
    }

    [Fact]
    public async Task ASecondPlay_WhileOneSounds_IsSkipped_AndGetsTheRunningTask()
    {
        using var player = Player(d => d.HoldBytes = true);

        var first = player.PlayAsync(TimerTones.Chime, CancellationToken.None);
        var second = player.PlayAsync(TimerTones.Bell, CancellationToken.None);

        Assert.Same(first, second);
        Assert.False(first.IsCompleted);
        var device = Assert.Single(_devices);
        device.Release();
        _time.Advance(TimerTonePlayer.Poll);   // the next check finds it drained
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, device.Started);
        Assert.Equal(1, device.Stopped);
        Assert.Equal(0, device.Cleared);
    }

    [Fact]
    public async Task Replace_WhileOneSounds_ClearsAndStopsIt_ThenStartsTheNewClip()
    {
        using var player = Player(d => d.HoldBytes = true);

        var first = player.PlayAsync(TimerTones.Chime, CancellationToken.None);
        var second = player.PlayAsync(TimerTones.Beep, CancellationToken.None, replace: true);

        await first.WaitAsync(TimeSpan.FromSeconds(10));
        // The replacing play opens its device on its own continuation after the first ends (2026-10-22: a CI-like run on two cores
        // checked the count before it had, the flake open since 2026-10-21): waited for, as the other steps are.
        Assert.True(SpinWait.SpinUntil(() => _devices.Count == 2 && _devices[1].Writes.Count == 1, TimeSpan.FromSeconds(10)));
        Assert.NotSame(first, second);
        Assert.Equal(2, _devices.Count);
        Assert.Equal(1, _devices[0].Cleared);
        Assert.Equal(1, _devices[0].Stopped);
        Assert.Equal(TimerTones.Render(TimerTones.Beep, TimerTonePlayer.Format), Assert.Single(_devices[1].Writes));
        _devices[1].Release();
        _time.Advance(TimerTonePlayer.Poll);
        await second.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, _devices[1].Cleared);
    }

    [Fact]
    public async Task Cancellation_ClearsAndStops_AndTheWaitIsCappedOnTheClock()
    {
        using var player = Player(d => d.HoldBytes = true);
        using var cts = new CancellationTokenSource();

        var cancelled = player.PlayAsync(TimerTones.Beep, cts.Token);
        await cts.CancelAsync();
        await cancelled.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, _devices[0].Cleared);
        Assert.Equal(1, _devices[0].Stopped);

        var capped = player.PlayAsync(TimerTones.Beep, CancellationToken.None);   // never drains: the cap ends it
        for (int i = 0; i < 2000 && !capped.IsCompleted; i++)
        {
            _time.Advance(TimerTonePlayer.Poll);
            await Task.Delay(1);   // the next wait registered before the clock moves again
        }

        await capped.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, _devices[1].Stopped);
    }

    [Fact]
    public async Task Dispose_StopsTheDeviceStillOpen_AndLaterPlaysDoNothing()
    {
        var player = Player(d => d.HoldBytes = true);
        var playing = player.PlayAsync(TimerTones.Bell, CancellationToken.None);

        player.Dispose();
        await playing.WaitAsync(TimeSpan.FromSeconds(10));
        await player.PlayAsync(TimerTones.Bell, CancellationToken.None);

        var device = Assert.Single(_devices);
        Assert.Equal(1, device.Stopped);
        Assert.True(device.Disposed);
    }
}
