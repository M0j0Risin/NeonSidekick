namespace NeonSidekick.UI;

/// <summary>
/// An input source that owns the mouse (2026-10-06, the macOS build): what the screen needs from it beyond its events —
/// take the mouse or hand it back to the terminal, keep the wheel, and be told when the console's mode changed under it.
/// <see cref="WindowsConsoleInput"/> on Windows (console modes), the Unix reader elsewhere (the terminal's mouse-report
/// sequences). <c>SidekickApp</c> reads it off the injected input instead of naming the Windows class.
/// </summary>
public interface IMouseInput
{
    /// <summary>Takes the mouse (true) or hands it back to the terminal's own selection (false).</summary>
    void Capture(bool on);

    /// <summary>Keeps the wheel while true: a notch scrolls the screen instead of handing the mouse back.</summary>
    void HoldWheel(bool on);

    /// <summary>Called after the source changed the console's mode, so the screen can flush what it wrote meanwhile; null = nobody.</summary>
    Action? ModeChanged { get; set; }
}
