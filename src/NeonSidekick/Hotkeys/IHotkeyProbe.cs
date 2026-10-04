namespace NeonSidekick.Hotkeys;

/// <summary>What Windows said about a chord: no program holds it, another does, or an error that says neither.</summary>
public enum HotkeyStatus
{
    Free,
    Held,
    Unknown,
}

/// <summary>A probe's answer: the status and, for <see cref="HotkeyStatus.Unknown"/>, the Win32 error.</summary>
public readonly record struct HotkeyProbeResult(HotkeyStatus Status, int Error = 0);

/// <summary>
/// Whether another program holds a chord as a global hotkey (2026-10-04, <c>/keycheck</c>): <see cref="WindowsHotkeyProbe"/> on
/// Windows, a fake in the tests, none elsewhere (Program passes null and <c>/keycheck</c> says it needs Windows).
/// </summary>
public interface IHotkeyProbe
{
    HotkeyProbeResult Probe(KeyChord chord);
}
