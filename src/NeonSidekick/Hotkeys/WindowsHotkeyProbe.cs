using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NeonSidekick.Hotkeys;

/// <summary>
/// The probe the user ran by hand on 2026-10-04 to find the NVIDIA overlay's Ctrl+Alt+M and Ctrl+Alt+R, as <c>/keycheck</c>'s:
/// <c>RegisterHotKey</c> on the calling thread (no window) and, when it took, <c>UnregisterHotKey</c> at once on the same thread,
/// so the app holds each chord for microseconds. Taken is <see cref="HotkeyStatus.Free"/>; error 1409 is
/// <see cref="HotkeyStatus.Held"/>; any other error <see cref="HotkeyStatus.Unknown"/>. It sees only hotkeys registered this way:
/// a keyboard hook (AutoHotkey, PowerToys Keyboard Manager) and Windows Terminal's own bindings never show.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsHotkeyProbe : IHotkeyProbe
{
    /// <summary>The ids the probe registers under: a range of its own, so a probe never meets another hotkey of this thread's.</summary>
    private const int FirstId = 0xB000;

    private int _next;

    public HotkeyProbeResult Probe(KeyChord chord)
    {
        int id = FirstId + (Interlocked.Increment(ref _next) & 0x0FFF);
        if (HotkeyNative.RegisterHotKey(IntPtr.Zero, id, chord.Modifiers | HotkeyNative.ModNoRepeat, chord.VirtualKey))
        {
            _ = HotkeyNative.UnregisterHotKey(IntPtr.Zero, id);
            return new HotkeyProbeResult(HotkeyStatus.Free);
        }

        int error = Marshal.GetLastPInvokeError();
        return error == HotkeyNative.ErrorHotkeyAlreadyRegistered
            ? new HotkeyProbeResult(HotkeyStatus.Held)
            : new HotkeyProbeResult(HotkeyStatus.Unknown, error);
    }
}
