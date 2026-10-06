namespace NeonSidekick.Audio;

/// <summary>
/// Whether this build can play or record sound (2026-10-06, the macOS build). The only devices are WinMM's
/// (<see cref="WinMmAudioPlayback"/>, <see cref="WinMmAudioCapture"/>); elsewhere the default factories throw
/// <see cref="PlatformNotSupportedException"/> with <see cref="Unavailable"/> instead of a <c>DllNotFoundException</c> for
/// winmm, the speech session marks audio unavailable with that sentence, the voice session reads no microphone, and
/// <c>--audio-check</c>/<c>--voice-check</c> say so and exit. An AudioQueue pair behind the same interfaces brings it back.
/// </summary>
public static class AudioSupport
{
    /// <summary>True where WinMM exists: Windows.</summary>
    public static bool Available => OperatingSystem.IsWindows();

    /// <summary>The sentence for every audio door off Windows. Pinned.</summary>
    public const string Unavailable = "Audio needs Windows for now: this build has no sound output or microphone yet.";

    /// <summary>The default playback: WinMM, or the refusal.</summary>
    public static IAudioPlayback DefaultPlayback(PcmFormat format) =>
        Available ? new WinMmAudioPlayback(format) : throw new PlatformNotSupportedException(Unavailable);

    /// <summary>The default capture: WinMM, or the refusal.</summary>
    public static IAudioCapture DefaultCapture(PcmFormat format) =>
        Available ? new WinMmAudioCapture(format) : throw new PlatformNotSupportedException(Unavailable);

    /// <summary>The default microphone count: WinMM's, or none.</summary>
    public static int DefaultInputDeviceCount() => Available ? WinMmAudioCapture.InputDeviceCount() : 0;
}
