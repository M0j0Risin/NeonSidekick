using NeonSidekick.Hotkeys;

namespace NeonSidekick.App;

public static partial class SmokeChecks
{
    /// <summary>
    /// <c>keys:hotkey</c> (2026-10-04, <c>/keycheck</c>): <c>RegisterHotKey</c> and <c>UnregisterHotKey</c> through
    /// <see cref="WindowsHotkeyProbe"/> in the published binary, on Ctrl+Alt+Shift+F24 — a chord no keyboard has, so nothing is
    /// taken from anyone. Free or held both pass (either way Windows answered); an error that is neither fails. Skipped off Windows.
    /// </summary>
    public static SmokeCheck ProbeHotkey()
    {
        const string name = "keys:hotkey";
        if (!OperatingSystem.IsWindows())
        {
            return new SmokeCheck(name, true, "skipped: not Windows");
        }

        try
        {
            var result = new WindowsHotkeyProbe().Probe(new KeyChord(true, true, true, ConsoleKey.F24));
            return new SmokeCheck(name, result.Status != HotkeyStatus.Unknown, "Ctrl+Alt+Shift+F24 is " + KeyCheckText.Status(result));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new SmokeCheck(name, false, $"{e.GetType().Name}: {e.Message}");
        }
    }
}
