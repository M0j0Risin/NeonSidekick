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

    /// <summary>
    /// <c>keys:carbon</c> (2026-10-11, a Mac's <c>/keycheck</c>): HIToolbox bound by its install name in the published binary,
    /// <c>CopySymbolicHotKeys</c> answering 0 with entries, and the exclusive register and release through
    /// <see cref="MacHotkeyProbe.RegisterOnMain"/> (on the main thread, as <c>/keycheck</c>'s; since the code review of 2026-10-11) on
    /// Ctrl+Option+Shift+F19 (a chord no shortcut uses) answering free or held. Any other
    /// answer fails. Skipped off a Mac, and without a window server (over SSH), where HIToolbox has no session to ask.
    /// </summary>
    public static SmokeCheck ProbeCarbonHotkeys()
    {
        const string name = "keys:carbon";
        if (!OperatingSystem.IsMacOS())
        {
            return new SmokeCheck(name, true, "skipped: not macOS");
        }

        if (!Viewer.AppKitNative.HasWindowServer())
        {
            return new SmokeCheck(name, true, "skipped: no window server");
        }

        try
        {
            var shortcuts = MacHotkeyNative.SymbolicHotKeys(out int listed);
            // The path /keycheck takes: on the main thread while the AppKit host runs (it does under --smoke).
            int status = MacHotkeyProbe.RegisterOnMain(80, MacHotkeyRules.ControlKey | MacHotkeyRules.OptionKeyBit | MacHotkeyRules.ShiftKey, 0xF19);
            bool passed = listed == MacHotkeyRules.NoErr && shortcuts.Count > 0 && status is MacHotkeyRules.NoErr or MacHotkeyRules.HotKeyExists;
            string answer = status switch
            {
                MacHotkeyRules.NoErr => KeyCheckText.FreeWord,
                MacHotkeyRules.HotKeyExists => KeyCheckText.HeldWord,
                _ => KeyCheckText.UnknownWord(status),
            };
            return new SmokeCheck(name, passed, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{shortcuts.Count} macOS shortcuts (status {listed}); Ctrl+Option+Shift+F19 is {answer}"));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new SmokeCheck(name, false, $"{e.GetType().Name}: {e.Message}");
        }
    }
}
