namespace NeonSidekick.Screen;

/// <summary>A rectangle in physical screen pixels (the virtual desktop's coordinates: a monitor left of the primary is negative).</summary>
public readonly record struct ScreenRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;

    public int Bottom => Top + Height;

    /// <summary>The smallest rectangle holding every one of <paramref name="rects"/> (the virtual desktop for every monitor); empty for none.</summary>
    public static ScreenRect Union(IEnumerable<ScreenRect> rects)
    {
        ArgumentNullException.ThrowIfNull(rects);
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        foreach (var r in rects)
        {
            left = Math.Min(left, r.Left);
            top = Math.Min(top, r.Top);
            right = Math.Max(right, r.Right);
            bottom = Math.Max(bottom, r.Bottom);
        }

        return left == int.MaxValue ? default : new ScreenRect(left, top, right - left, bottom - top);
    }
}

/// <summary>A monitor: its number (1-based, Windows' enumeration order), its device name (<c>\\.\DISPLAY1</c>), where it sits, whether it is the primary.</summary>
public sealed record ScreenMonitor(int Number, string Name, ScreenRect Bounds, bool Primary);

/// <summary>A window that can be captured: its id (the handle as a number), title, process name and frame on the screen.</summary>
public sealed record ScreenWindow(long Id, string Title, string Process, ScreenRect Bounds);

/// <summary>A captured picture: top-down rows of BGRX pixels, tightly packed (<c>CameraJpeg</c>'s layout).</summary>
public sealed record ScreenFrame(int Width, int Height, byte[] Bgrx);

/// <summary>
/// The screen as <c>screen_capture</c> and <c>/screen</c> see it (2026-10-04): the monitors, the windows that can be captured, the
/// app's own window, and the pictures. <see cref="WindowsScreenSystem"/> on Windows (GDI); <c>FakeScreenSystem</c> in the tests;
/// none elsewhere (<c>Program</c> passes null, and the tool is not offered). Every call may throw <see cref="ScreenException"/>.
/// </summary>
public interface IScreenSystem
{
    /// <summary>The monitors, numbered from 1.</summary>
    IReadOnlyList<ScreenMonitor> Monitors();

    /// <summary>Visible, titled, uncloaked top-level windows, front to back.</summary>
    IReadOnlyList<ScreenWindow> Windows();

    /// <summary>The app's own window (the terminal it runs in): its id, or null when it cannot be told.</summary>
    long? OwnWindow();

    /// <summary>The number of the monitor the app's own window is on; null when it cannot be told (the primary is used then).</summary>
    int? OwnMonitor();

    /// <summary>The pixels of <paramref name="area"/> as the screen shows them.</summary>
    ScreenFrame CaptureArea(ScreenRect area);

    /// <summary>The window's own pixels, drawn even when another window covers it.</summary>
    ScreenFrame CaptureWindow(long id);
}

/// <summary>A screen failure whose message is the sentence the user and the model read.</summary>
public sealed class ScreenException : Exception
{
    public ScreenException()
    {
    }

    public ScreenException(string message)
        : base(message)
    {
    }

    public ScreenException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
