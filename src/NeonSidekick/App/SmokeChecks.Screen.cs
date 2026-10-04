using NeonSidekick.Screen;

namespace NeonSidekick.App;

public static partial class SmokeChecks
{
    /// <summary>
    /// <c>screen:gdi</c> (2026-10-04): the screen capture's GDI path in the published binary — the monitors enumerated through the
    /// <c>[UnmanagedCallersOnly]</c> callback (one at least), a 64×64 corner of the first one blitted into a top-down DIB section,
    /// encoded through <c>CameraJpeg</c>'s raw-rows overload and decoded back by the viewer at the same size. Skipped off Windows
    /// and in a session with no desktop to capture (a service, a locked CI runner), where Windows lists no monitor.
    /// </summary>
    public static SmokeCheck ProbeScreenGdi()
    {
        const string name = "screen:gdi";
        if (!OperatingSystem.IsWindows())
        {
            return new SmokeCheck(name, true, "skipped: not Windows");
        }

        try
        {
            var screen = new WindowsScreenSystem();
            IReadOnlyList<ScreenMonitor> monitors;
            try
            {
                monitors = screen.Monitors();
            }
            catch (ScreenException)
            {
                return new SmokeCheck(name, true, "skipped: no monitor in this session");
            }

            var first = monitors[0].Bounds;
            var frame = screen.CaptureArea(new ScreenRect(first.Left, first.Top, Math.Min(64, first.Width), Math.Min(64, first.Height)));
            var image = ScreenCapture.Attachment(frame);
            var decoded = Viewer.ViewerImage.Decode(image.Bytes, "probe.jpg") ?? throw new InvalidOperationException("the JPEG did not decode");
            bool ok = decoded.Width == frame.Width && decoded.Height == frame.Height && image.Screen;
            return new SmokeCheck(name, ok, $"{monitors.Count} monitor(s); a {frame.Width}x{frame.Height} corner blitted, {image.Bytes.Length} bytes JPEG, decoded {decoded.Width}x{decoded.Height}");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new SmokeCheck(name, false, $"{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// <c>screen:windows</c> (2026-10-04): the window side — <c>EnumWindows</c> through its callback, the titles, DWM's frame bounds
    /// and cloaking; then the frontmost window drawn by <c>PrintWindow(PW_RENDERFULLCONTENT)</c>. A window that refuses (or one
    /// gone between the two calls) is a clean <see cref="ScreenException"/>, which passes: the point is that the calls marshal.
    /// Skipped off Windows and with no window listed.
    /// </summary>
    public static SmokeCheck ProbeScreenWindows()
    {
        const string name = "screen:windows";
        if (!OperatingSystem.IsWindows())
        {
            return new SmokeCheck(name, true, "skipped: not Windows");
        }

        try
        {
            var screen = new WindowsScreenSystem();
            var windows = screen.Windows();
            if (windows.Count == 0)
            {
                return new SmokeCheck(name, true, "skipped: no window listed in this session");
            }

            string drawn;
            try
            {
                var frame = screen.CaptureWindow(windows[0].Id);
                drawn = $"the front one drawn at {frame.Width}x{frame.Height}";
            }
            catch (ScreenException e)
            {
                drawn = "the front one refused cleanly (" + e.Message + ")";
            }

            return new SmokeCheck(name, true, $"{windows.Count} window(s) listed; {drawn}");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new SmokeCheck(name, false, $"{e.GetType().Name}: {e.Message}");
        }
    }
}
