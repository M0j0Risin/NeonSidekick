namespace NeonSidekick.Hotkeys;

/// <summary>What Windows said about a chord: no program holds it, another does, or an error that says neither.</summary>
public enum HotkeyStatus
{
    Free,
    Held,
    Unknown,
}

/// <summary>
/// Who holds a held chord, or why one went unasked (2026-10-11, the Mac's <c>/keycheck</c>): Windows only ever says another program,
/// so its results keep <see cref="None"/>; a Mac's probe tells macOS's own shortcuts, another app's exclusive hotkey and the terminal's
/// Option key apart, and marks a key a Mac keyboard has no code for.
/// </summary>
public enum HotkeyCause
{
    None,
    System,
    OtherApp,
    Terminal,
    NoKeyCode,
}

/// <summary>A probe's answer: the status, for <see cref="HotkeyStatus.Unknown"/> the error, and on a Mac who holds it.</summary>
public readonly record struct HotkeyProbeResult(HotkeyStatus Status, int Error = 0, HotkeyCause Cause = HotkeyCause.None);

/// <summary>
/// Whether another program holds a chord as a global hotkey (2026-10-04, <c>/keycheck</c>): <see cref="WindowsHotkeyProbe"/> on
/// Windows, <see cref="MacHotkeyProbe"/> on a Mac (2026-10-11), a fake in the tests, none elsewhere (Program passes null and
/// <c>/keycheck</c> says it needs Windows or macOS).
/// </summary>
public interface IHotkeyProbe
{
    HotkeyProbeResult Probe(KeyChord chord);

    /// <summary>The probe speaks a Mac's words (its header, caveat and causes); Windows' by default. Decided by the probe, not the machine, so the tests pin both.</summary>
    bool Mac => false;

    /// <summary>
    /// A run's start, before any <see cref="Probe"/>: a Mac's probe takes its snapshot of macOS's shortcuts and reads the terminal here,
    /// and answers the lines shown under the caveat (what it found about the terminal's Option key). None by default.
    /// </summary>
    IReadOnlyList<string> Begin() => [];
}
