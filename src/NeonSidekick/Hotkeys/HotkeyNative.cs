using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NeonSidekick.Hotkeys;

/// <summary>
/// The two user32 imports behind <see cref="WindowsHotkeyProbe"/> (2026-10-04, <c>/keycheck</c>). One of the Windows-only layers,
/// <c>Screen/ScreenNative</c>'s shape: source-generated <see cref="LibraryImportAttribute"/>, the window handle an
/// <see cref="IntPtr"/>, nothing marshalled but the bool the smoke's <c>keys:hotkey</c> proves on the published exe. user32 is a
/// system library: nothing joins <c>SmokeChecks.RequiredNativeLibraries</c>.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class HotkeyNative
{
    /// <summary>The key's auto-repeat is not reported: the probe never waits for a press, but a held key costs nothing this way.</summary>
    public const uint ModNoRepeat = 0x4000;

    /// <summary><c>ERROR_HOTKEY_ALREADY_REGISTERED</c>: another program (or this thread) holds the chord.</summary>
    public const int ErrorHotkeyAlreadyRegistered = 1409;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(IntPtr hWnd, int id);
}
