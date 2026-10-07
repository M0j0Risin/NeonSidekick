using NeonSidekick.Audio;

namespace NeonSidekick.Tests;

/// <summary>Counts output devices once per assembly (WinMM on Windows, Core Audio's default on macOS); CI runners may have none.</summary>
internal static class AudioDevice
{
    public static readonly int OutputCount;
    public static readonly string Unavailable;

    static AudioDevice()
    {
        try
        {
            OutputCount = AudioSupport.DefaultOutputDeviceCount();
            Unavailable = OutputCount > 0 ? "" : "No audio output device on this machine.";

            // macOS (2026-10-07): a CI runner may name a default output that never plays (a null or virtual device), and
            // every device fact would then fail at its drain budget. 200 ms of silence must drain within 3 s (a cold device has once taken longer than 1.5 s).
            if (OutputCount > 0 && OperatingSystem.IsMacOS() && !Drains())
            {
                OutputCount = 0;
                Unavailable = "The default output device is present but does not play (200 ms did not drain in 3 s).";
            }
        }
        catch (Exception ex)
        {
            OutputCount = 0;
            Unavailable = "audio probe failed: " + ex.Message;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static bool Drains()
    {
        int bytes = PcmFormat.Kokoro.BytesFor(200);
        using var playback = new AudioQueuePlayback(PcmFormat.Kokoro);
        playback.Start();
        playback.Write(new byte[bytes], bytes);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (playback.BufferedBytes > 0 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
        }

        return playback.BufferedBytes == 0;
    }
}

/// <summary>Skips unless the machine has an audio output device. A local check, never a CI safety net.</summary>
public sealed class AudioDeviceFactAttribute : FactAttribute
{
    public AudioDeviceFactAttribute()
    {
        if (AudioDevice.OutputCount == 0)
        {
            Skip = AudioDevice.Unavailable;
        }
    }

    /// <summary>A fact about the AudioQueue backend itself (2026-10-07): skipped off macOS as well.</summary>
    public bool MacOnly
    {
        get => _macOnly;
        set
        {
            _macOnly = value;
            if (value && !OperatingSystem.IsMacOS())
            {
                Skip = MacFactAttribute.SkipReason;
            }
        }
    }

    private bool _macOnly;
}
