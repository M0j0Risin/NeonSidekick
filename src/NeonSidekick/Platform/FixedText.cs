namespace NeonSidekick.Platform;

/// <summary>
/// A fixed <c>WCHAR[n]</c> field as text (2026-11-14, the v0.5.7 review's #2): cut at its first NUL, since a driver may leave junk after
/// it (WinMM's MIDI caps, the CCD names). Shared by the Windows layers that read such fields; no P/Invoke of its own. Pure.
/// </summary>
internal static unsafe class FixedText
{
    /// <summary>The text of <paramref name="length"/> chars at <paramref name="buffer"/>, up to the first NUL (all of it with none).</summary>
    public static string Read(char* buffer, int length)
    {
        var span = new ReadOnlySpan<char>(buffer, length);
        int end = span.IndexOf('\0');
        return new string(end >= 0 ? span[..end] : span);
    }
}
