using NeonSidekick.Audio;

namespace NeonSidekick.Tests;

/// <summary>Counts microphones once per assembly (WinMM on Windows, Core Audio's default on macOS); CI runners may have none.</summary>
internal static class AudioInputDevice
{
    public static readonly int InputCount;
    public static readonly string Unavailable;

    static AudioInputDevice()
    {
        try
        {
            InputCount = AudioSupport.DefaultInputDeviceCount();
            Unavailable = InputCount > 0 ? "" : "No microphone on this machine.";

            // macOS (2026-10-07): recording from a terminal that was never asked would bring up the permission question,
            // and AudioQueueStart waits for the answer; a CI runner has nobody to give it. Only an allowed terminal records.
            if (InputCount > 0 && OperatingSystem.IsMacOS() && AudioQueueNative.MicrophoneAuthorization() != MicrophoneAccess.Authorized)
            {
                InputCount = 0;
                Unavailable = "The terminal running the tests has no microphone permission (System Settings › Privacy & Security › Microphone).";
            }
        }
        catch (Exception ex)
        {
            InputCount = 0;
            Unavailable = "audio probe failed: " + ex.Message;
        }
    }
}

/// <summary>Skips unless the machine has a microphone. A local check, never a CI safety net.</summary>
public sealed class AudioInputDeviceFactAttribute : FactAttribute
{
    public AudioInputDeviceFactAttribute()
    {
        if (AudioInputDevice.InputCount == 0)
        {
            Skip = AudioInputDevice.Unavailable;
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
