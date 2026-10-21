namespace NeonSidekick.UI;

/// <summary>
/// An input source that owns the mouse (2026-10-06, the macOS build): what the screen needs from it beyond its events —
/// take the mouse or hand it back to the terminal, keep the wheel, and be told when the console's mode changed under it.
/// <see cref="WindowsConsoleInput"/> on Windows (console modes), the Unix reader elsewhere (the terminal's mouse-report
/// sequences). <c>SidekickApp</c> reads it off the injected input instead of naming the Windows class.
///
/// <para>The mouse's hover and the window's focus (2026-10-20, the welcome splash's robot) come through callbacks, never as
/// <see cref="InputEvent"/>s: every pane reader closes or resets on an event it doesn't know, and a hover is dozens a second.</para>
/// </summary>
public interface IMouseInput
{
    /// <summary>Takes the mouse (true) or hands it back to the terminal's own selection (false).</summary>
    void Capture(bool on);

    /// <summary>Keeps the wheel while true: a notch scrolls the screen instead of handing the mouse back.</summary>
    void HoldWheel(bool on);

    /// <summary>Called after the source changed the console's mode, so the screen can flush what it wrote meanwhile; null = nobody.</summary>
    Action? ModeChanged { get; set; }

    /// <summary>
    /// Asks for the mouse's moves with no button held (true) or stops asking (false): a terminal reports them only when asked
    /// (any-event tracking), and they come only while the mouse is taken.
    /// </summary>
    void Hover(bool on);

    /// <summary>The mouse moved to cell (x, y) — in a click's frame — with no button held, while hover is asked for; raised on the reader's thread, so it must not block. Null = nobody.</summary>
    Action<int, int>? PointerMoved { get; set; }

    /// <summary>The window gained (true) or lost focus, when the terminal says; raised on the reader's thread. Null = nobody.</summary>
    Action<bool>? FocusChanged { get; set; }
}
