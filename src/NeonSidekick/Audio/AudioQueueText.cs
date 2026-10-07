using System.Globalization;

namespace NeonSidekick.Audio;

/// <summary>The AudioQueue pair's log and error wording (2026-10-07, sound on a Mac). Pure, so it is covered on every OS.</summary>
public static class AudioQueueText
{
    /// <summary><c>AudioQueueNewOutput failed with OSStatus -66681</c>, with the four-character code when the status is one (<c>'!dev'</c>).</summary>
    public static string Failed(string function, int status) =>
        string.Create(CultureInfo.InvariantCulture, $"{function} failed with OSStatus {status}{FourCharCode(status)}.");

    /// <summary>The playback's note when it reopens on a new default output.</summary>
    public static string OutputChanged(uint from, uint to) =>
        string.Create(CultureInfo.InvariantCulture, $"The default output changed (device {from} to {to}); the queue reopened on it.");

    /// <summary>The playback's warning when the queue stops handing buffers back (<c>AudioQueuePlayback.StallLimit</c>).</summary>
    public static string Stalled(long droppedBytes, TimeSpan quiet) =>
        string.Create(CultureInfo.InvariantCulture, $"The output queue handed nothing back for {quiet.TotalSeconds:F1} s; {droppedBytes} bytes in its buffers were dropped and it restarted.");

    /// <summary>The capture's note when its ring of copied buffers overflows (the pump fell behind by more than a second).</summary>
    public const string CaptureOverflow = "The capture pump fell behind; a microphone buffer was dropped.";

    /// <summary><c> ('!dev')</c> when all four bytes of the status are printable ASCII, else empty.</summary>
    public static string FourCharCode(int status)
    {
        Span<char> code = stackalloc char[4];
        for (int i = 0; i < 4; i++)
        {
            int b = (status >> (24 - (8 * i))) & 0xFF;
            if (b is < 0x20 or > 0x7E)
            {
                return "";
            }

            code[i] = (char)b;
        }

        return " ('" + new string(code) + "')";
    }
}
