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

    /// <summary>
    /// <c>screen:cg</c> (2026-10-07, a Mac's screen capture): the displays through <c>CGGetActiveDisplayList</c> and their modes, the
    /// window list through <c>CGWindowListCopyWindowInfo</c> and its dictionaries, the permission through
    /// <c>CGPreflightScreenCaptureAccess</c> (never the request: no prompt in a smoke). Nothing is captured. Without the permission
    /// (GitHub's macOS runner) the windows are counted as rows, since their titles are hidden; skipped off macOS 14+ and with no
    /// window server.
    /// </summary>
    public static SmokeCheck ProbeScreenCg()
    {
        const string name = "screen:cg";
        if (!OperatingSystem.IsMacOSVersionAtLeast(14))
        {
            return new SmokeCheck(name, true, "skipped: not macOS 14+");
        }

        try
        {
            if (!Viewer.AppKitNative.HasWindowServer())
            {
                return new SmokeCheck(name, true, "skipped: no window server");
            }

            var screen = new MacScreenSystem();
            var monitors = screen.Monitors();
            var first = monitors[0].Bounds;
            string windows = screen.Refusal(ask: false) is null
                ? $"{screen.Windows().Count} window(s) listed"
                : "no Screen Recording permission, so no windows listed (the refusal names the terminal)";
            return new SmokeCheck(name, true, $"{monitors.Count} display(s), the first {first.Width}x{first.Height}; {windows}");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new SmokeCheck(name, false, $"{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// <c>screen:sck</c> (2026-10-07): ScreenCaptureKit bound — its classes and the two class methods the capture calls answer —
    /// and, with the permission, <c>getShareableContentWithCompletionHandler:</c> run through the hand-built completion block (the
    /// one piece no unit test can reach), counting the displays and windows. Never a screenshot. Skipped off macOS 14+, with no
    /// window server, and (the call, not the binding) without the permission.
    /// </summary>
    public static SmokeCheck ProbeScreenSck()
    {
        const string name = "screen:sck";
        if (!OperatingSystem.IsMacOSVersionAtLeast(14))
        {
            return new SmokeCheck(name, true, "skipped: not macOS 14+");
        }

        try
        {
            nint content = MacScreenNative.TryClass("SCShareableContent"), manager = MacScreenNative.TryClass("SCScreenshotManager");
            bool bound = content != 0 && manager != 0 && MacScreenNative.TryClass("SCContentFilter") != 0 && MacScreenNative.TryClass("SCStreamConfiguration") != 0
                && MacScreenNative.class_getClassMethod(content, Viewer.AppKitNative.Sel("getShareableContentWithCompletionHandler:")) != 0
                && MacScreenNative.class_getClassMethod(manager, Viewer.AppKitNative.Sel("captureImageWithFilter:configuration:completionHandler:")) != 0;
            if (!bound)
            {
                return new SmokeCheck(name, false, "ScreenCaptureKit's classes or methods are missing");
            }

            if (!Viewer.AppKitNative.HasWindowServer())
            {
                return new SmokeCheck(name, true, "bound; the call skipped: no window server");
            }

            if (new MacScreenSystem().Refusal(ask: false) is not null)
            {
                return new SmokeCheck(name, true, "bound; the call skipped: no Screen Recording permission");
            }

            var (displays, windows) = MacScreenSystem.ShareableCounts();
            return new SmokeCheck(name, displays > 0, $"bound; the completion block answered: {displays} display(s), {windows} window(s)");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new SmokeCheck(name, false, $"{e.GetType().Name}: {e.Message}");
        }
    }
}
