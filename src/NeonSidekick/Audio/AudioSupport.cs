namespace NeonSidekick.Audio;

/// <summary>
/// Whether this build can play or record sound (2026-10-06, the macOS build). The devices are WinMM's on Windows
/// (<see cref="WinMmAudioPlayback"/>, <see cref="WinMmAudioCapture"/>) and, since 2026-10-07, AudioQueue's on macOS
/// (<see cref="AudioQueuePlayback"/>, <see cref="AudioQueueCapture"/>); NAudio and OpenAL, which arrive through KokoroSharp,
/// never. Elsewhere (Linux) the default factories throw <see cref="PlatformNotSupportedException"/> with
/// <see cref="Unavailable"/>, the speech session marks audio unavailable with that sentence, the voice session reads no
/// microphone, and <c>--audio-check</c>/<c>--voice-check</c> say so and exit.
/// </summary>
public static class AudioSupport
{
    /// <summary>True where a backend exists: Windows (WinMM) and macOS (AudioQueue).</summary>
    public static bool Available => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    /// <summary>The sentence for every audio door where there is no backend. Pinned.</summary>
    public const string Unavailable = "Audio needs Windows or macOS for now: this build has no sound output or microphone.";

    /// <summary>The default playback: WinMM, AudioQueue, or the refusal.</summary>
    public static IAudioPlayback DefaultPlayback(PcmFormat format) =>
        OperatingSystem.IsWindows() ? new WinMmAudioPlayback(format)
        : OperatingSystem.IsMacOS() ? new AudioQueuePlayback(format)
        : throw new PlatformNotSupportedException(Unavailable);

    /// <summary>The default capture: WinMM, AudioQueue, or the refusal.</summary>
    public static IAudioCapture DefaultCapture(PcmFormat format) =>
        OperatingSystem.IsWindows() ? new WinMmAudioCapture(format)
        : OperatingSystem.IsMacOS() ? new AudioQueueCapture(format)
        : throw new PlatformNotSupportedException(Unavailable);

    /// <summary>The default microphone count: WinMM's on Windows, on macOS 1 when Core Audio names a default input, else none.</summary>
    public static int DefaultInputDeviceCount() =>
        OperatingSystem.IsWindows() ? WinMmAudioCapture.InputDeviceCount()
        : OperatingSystem.IsMacOS() ? AudioQueueCapture.InputDeviceCount()
        : 0;

    /// <summary>The default speaker count, the same way (the tests' <c>[AudioDeviceFact]</c>).</summary>
    public static int DefaultOutputDeviceCount() =>
        OperatingSystem.IsWindows() ? WinMmAudioPlayback.OutputDeviceCount()
        : OperatingSystem.IsMacOS() ? AudioQueuePlayback.OutputDeviceCount()
        : 0;

    /// <summary>
    /// Why the microphone may not be used, before anything records, or null: on macOS the terminal's permission denied or
    /// restricted (<see cref="MicrophoneAccess.Refusal"/>); never anything on Windows, whose WinMM has no such question.
    /// </summary>
    public static string? MicrophoneRefusal() => OperatingSystem.IsMacOS() ? AudioQueueCapture.Refusal() : null;

    /// <summary>The detail for "no microphone": Windows' pinned "no wave-in device", the Mac's "no input device", or <see cref="Unavailable"/>.</summary>
    public static string NoMicrophoneDetail =>
        OperatingSystem.IsWindows() ? "no wave-in device"
        : OperatingSystem.IsMacOS() ? MicrophoneText.NoInputDevice
        : Unavailable;
}
