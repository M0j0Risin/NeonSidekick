using System.Globalization;
using System.Runtime.Versioning;
using NeonSidekick.Diagnostics;
using NeonSidekick.Viewer;
using static NeonSidekick.Screen.MacScreenNative;
using static NeonSidekick.Viewer.AppKitNative;

namespace NeonSidekick.Screen;

/// <summary>
/// The screen on a Mac (2026-10-07, Stage 2; <see cref="WindowsScreenSystem"/>'s twin). Chosen over the alternatives after probing
/// each on macOS 15.7.9: CoreGraphics' <c>CGDisplayCreateImage</c>/<c>CGWindowListCreateImage</c> still worked but are obsolete since
/// the macOS 15 SDK (Apple may remove them in any release), and <c>/usr/sbin/screencapture</c> would have been a new process-start site
/// and a PNG round trip through the disk; ScreenCaptureKit is the supported API, its completion blocks built by hand
/// (<see cref="MacScreenNative"/>). <b>Lists</b> come from CoreGraphics, synchronous and any thread: the displays by
/// <c>CGGetActiveDisplayList</c> with each one's mode for its pixels, the windows by <c>CGWindowListCopyWindowInfo</c> (on screen,
/// front to back), both turned into the portable shapes by <see cref="MacScreenRules"/> (one space, points times the largest scale).
/// <b>Pictures</b> come from ScreenCaptureKit's <c>SCScreenshotManager</c> (macOS 14): a display through
/// <c>captureImageInRect:</c> over its rectangle on macOS 15.2+ (the display filter gave an empty picture of an external 5K), else
/// <c>initWithDisplay:excludingWindows:</c> at its own pixels, the cursor left out; a window through
/// <c>initWithDesktopIndependentWindow:</c> at its <c>pointPixelScale</c>, its own content even when covered, without its shadow
/// (Windows' DWM frame); an area drawn from the displays it touches (<see cref="MacScreenRules.Plan"/>). Each picture is drawn into an
/// sRGB BGRX canvas, which converts the panel's colour profile (Display P3 on a Mac's own screens). <b>The permission</b> (Screen
/// Recording, held by the terminal app the app runs in): <see cref="Refusal"/> asks <c>CGPreflightScreenCaptureAccess</c> and, for a
/// capture asked for, <c>CGRequestScreenCaptureAccess</c> once per run (macOS shows its prompt only while the app has never been
/// answered); every list of windows and every picture is refused without it, since macOS would hand back blank titles and the
/// wallpaper. A window not on the screen (another Space, minimized) is refused before ScreenCaptureKit is asked: probed, its capture
/// never completed. Excluded from coverage with the native layer; the smoke's <c>screen:cg</c>/<c>screen:sck</c> prove it.
/// </summary>
[SupportedOSPlatform("macos14.0")]
public sealed class MacScreenSystem : IScreenSystem
{
    /// <summary>The largest side a capture may have, as on Windows (<c>WindowsScreenSystem.MaxSide</c>).</summary>
    public const int MaxSide = 16_384;

    /// <summary>How long one ScreenCaptureKit call may take: inside <see cref="ScreenCapture.Timeout"/>, so the wait gives up here first.</summary>
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(8);

    private int _asked;

    public IReadOnlyList<ScreenMonitor> Monitors()
    {
        var monitors = MacScreenRules.Monitors(Displays());
        return monitors.Count > 0 ? monitors : throw new ScreenException(ScreenText.MacNoDisplays);
    }

    public IReadOnlyList<ScreenWindow> Windows()
    {
        if (Refusal(ask: false) is { } refusal)
        {
            throw new ScreenException(refusal);
        }

        return MacScreenRules.Windows(Entries(OnScreenOnly | ExcludeDesktopElements, 0), MacScreenRules.Scale(Displays()));
    }

    public long? OwnWindow() =>
        MacScreenRules.Own(Entries(OnScreenOnly | ExcludeDesktopElements, 0), MacTerminal.Ancestors, MacTerminal.TermProgram, UI.ConsoleTitle.Last);

    public int? OwnMonitor()
    {
        var entries = Entries(OnScreenOnly | ExcludeDesktopElements, 0);
        if (MacScreenRules.Own(entries, MacTerminal.Ancestors, MacTerminal.TermProgram, UI.ConsoleTitle.Last) is not { } own
            || entries.FirstOrDefault(e => e.Id == own) is not { } window)
        {
            return null;
        }

        var displays = Displays();
        double scale = MacScreenRules.Scale(displays);
        return MacScreenRules.MonitorOf(MacScreenRules.ToSpace(window.X, window.Y, window.Width, window.Height, scale), MacScreenRules.Monitors(displays));
    }

    public string? Refusal(bool ask)
    {
        if (CGPreflightScreenCaptureAccess() != 0)
        {
            return null;
        }

        if (ask && Interlocked.Exchange(ref _asked, 1) == 0)
        {
            // macOS's own prompt, once per run: it shows only while this terminal has never been answered, and a grant counts
            // only after the terminal restarts, so the sentence below is the answer either way.
            _ = CGRequestScreenCaptureAccess();
            DiagnosticLog.Info(ScreenText.Category, "Asked macOS for the Screen Recording permission.");
        }

        return ScreenText.NoPermission(MacScreenRules.TerminalName(Entries(OnScreenOnly | ExcludeDesktopElements, 0), MacTerminal.Ancestors, MacTerminal.TermProgram));
    }

    public ScreenFrame CaptureArea(ScreenRect area)
    {
        Permitted();
        var displays = Displays();
        if (area.Width > MaxSide || area.Height > MaxSide || MacScreenRules.Plan(area, displays) is not { } canvas || canvas.Width > MaxSide || canvas.Height > MaxSide)
        {
            throw new ScreenException(ScreenText.Failed("the area is empty or too large"));
        }

        var images = new List<(nint Image, ScreenRect Into)>();
        try
        {
            using var content = ShareableContent();
            foreach (var placement in canvas.Placements)
            {
                var display = displays.First(d => d.Id == placement.Display);
                images.Add((CaptureDisplay(content.Handle, display), placement.Into));
            }

            return new ScreenFrame(canvas.Width, canvas.Height, Canvas(canvas.Width, canvas.Height, (context, height) =>
            {
                foreach (var (image, into) in images)
                {
                    CGContextDrawImage(context, new CGRect(into.Left, height - into.Bottom, into.Width, into.Height), image);
                }
            }));
        }
        finally
        {
            foreach (var (image, _) in images)
            {
                CGImageRelease(image);
            }
        }
    }

    public ScreenFrame CaptureWindow(long id)
    {
        Permitted();
        if (id <= 0 || id > uint.MaxValue)
        {
            throw new ScreenException(ScreenText.WindowGone);
        }

        // The window as it is now: gone, or not on the screen (ScreenCaptureKit never answers for one on another Space).
        var now = Entries(IncludingWindow, (uint)id).FirstOrDefault(e => e.Id == id) ?? throw new ScreenException(ScreenText.WindowGone);
        if (!now.OnScreen)
        {
            throw new ScreenException(ScreenText.WindowOffScreen);
        }

        using var content = ShareableContent();
        nint window = Find(Send(content.Handle, Sel("windows")), Sel("windowID"), (uint)id);
        if (window == 0)
        {
            throw new ScreenException(ScreenText.WindowGone);
        }

        nint pool = objc_autoreleasePoolPush();
        nint filter = 0, configuration = 0, image = 0;
        try
        {
            filter = Send(Send(Class("SCContentFilter"), Sel("alloc")), Sel("initWithDesktopIndependentWindow:"), window);
            CGRect rect = SendRect(filter, Sel("contentRect"));
            double scale = Math.Max(1, SendFloat(filter, Sel("pointPixelScale")));
            int width = (int)Math.Round(rect.Width * scale), height = (int)Math.Round(rect.Height * scale);
            if (width <= 0 || height <= 0 || width > MaxSide || height > MaxSide)
            {
                throw new ScreenException(ScreenText.WindowGone);
            }

            configuration = Configuration(width, height);
            image = Shoot(filter, configuration);
            int w = (int)CGImageGetWidth(image), h = (int)CGImageGetHeight(image);
            return new ScreenFrame(w, h, Canvas(w, h, (context, _) => CGContextDrawImage(context, new CGRect(0, 0, w, h), image)));
        }
        finally
        {
            if (image != 0)
            {
                CGImageRelease(image);
            }

            Release(configuration);
            Release(filter);
            objc_autoreleasePoolPop(pool);
        }
    }

    /// <summary>
    /// For the smoke's <c>screen:sck</c>: ScreenCaptureKit's displays and windows counted through a hand-built completion block —
    /// the block's round trip on the published exe, and no picture taken. Needs the permission (the caller checks).
    /// </summary>
    internal static (ulong Displays, ulong Windows) ShareableCounts()
    {
        using var content = ShareableContent();
        return (SendULong(Send(content.Handle, Sel("displays")), Sel("count")), SendULong(Send(content.Handle, Sel("windows")), Sel("count")));
    }

    private void Permitted()
    {
        if (Refusal(ask: true) is { } refusal)
        {
            throw new ScreenException(refusal);
        }
    }

    /// <summary>The active displays, the main one as CoreGraphics orders it, each with its mode's pixels.</summary>
    private static unsafe IReadOnlyList<MacDisplay> Displays()
    {
        const int Max = 32;
        uint* ids = stackalloc uint[Max];
        uint count = 0;
        if (CGGetActiveDisplayList(Max, ids, &count) != 0)
        {
            return [];
        }

        uint main = CGMainDisplayID();
        var displays = new List<MacDisplay>((int)count);
        for (int i = 0; i < count; i++)
        {
            CGRect bounds = CGDisplayBounds(ids[i]);
            nint mode = CGDisplayCopyDisplayMode(ids[i]);
            int pixelWidth = (int)Math.Round(bounds.Width), pixelHeight = (int)Math.Round(bounds.Height);
            if (mode != 0)
            {
                pixelWidth = (int)CGDisplayModeGetPixelWidth(mode);
                pixelHeight = (int)CGDisplayModeGetPixelHeight(mode);
                CGDisplayModeRelease(mode);
            }

            displays.Add(new MacDisplay(ids[i], bounds.X, bounds.Y, bounds.Width, bounds.Height, pixelWidth, pixelHeight, ids[i] == main));
        }

        return displays;
    }

    /// <summary>The window list's rows the rules read, front to back.</summary>
    private static List<MacWindowEntry> Entries(uint option, uint relativeTo)
    {
        nint list = CGWindowListCopyWindowInfo(option, relativeTo);
        if (list == 0)
        {
            return [];
        }

        try
        {
            nint count = CFArrayGetCount(list);
            var entries = new List<MacWindowEntry>((int)count);
            for (nint i = 0; i < count; i++)
            {
                nint row = CFArrayGetValueAtIndex(list, i);
                CGRect bounds = Rect(row, "kCGWindowBounds");
                entries.Add(new MacWindowEntry(
                    Long(row, "kCGWindowNumber"), (int)Long(row, "kCGWindowLayer"), Double(row, "kCGWindowAlpha", 1), Bool(row, "kCGWindowIsOnscreen"),
                    (int)Long(row, "kCGWindowSharingState"), Text(row, "kCGWindowName"), Text(row, "kCGWindowOwnerName"), (int)Long(row, "kCGWindowOwnerPID"),
                    bounds.X, bounds.Y, bounds.Width, bounds.Height));
            }

            return entries;
        }
        finally
        {
            CFRelease(list);
        }
    }

    /// <summary>ScreenCaptureKit's view of the shareable displays and windows, held for one capture.</summary>
    private static Owned ShareableContent()
    {
        nint manager = TryClass("SCShareableContent");
        if (manager == 0 || TryClass("SCScreenshotManager") == 0)
        {
            throw new ScreenException(ScreenText.UnsupportedMac);
        }

        var done = Await(block => SendVoid(manager, Sel("getShareableContentWithCompletionHandler:"), block), CallTimeout);
        return new Owned(Answered(done, "the shareable content"));
    }

    private static nint CaptureDisplay(nint content, MacDisplay display)
    {
        if (OperatingSystem.IsMacOSVersionAtLeast(15, 2))
        {
            // The display's rectangle in points (2026-10-07, found in the live check): on the user's 5K beside a MacBook's own
            // screen, the display filter came back empty (all zeros at full size, magenta at half, whatever the pixel format
            // or colour space), while the rectangle gave the screen at its 5120x2880 pixels; the MacBook's screen came out
            // either way. ScreenCaptureKit's rectangle capture is macOS 15.2's, so 14.0–15.1 keep the filter below.
            nint manager = TryClass("SCScreenshotManager");
            var rect = new CGRect(display.X, display.Y, display.Width, display.Height);
            return Answered(Await(block => SendVoidRectNint(manager, Sel("captureImageInRect:completionHandler:"), rect, block), CallTimeout), "the screenshot");
        }

        nint scDisplay = Find(Send(content, Sel("displays")), Sel("displayID"), display.Id);
        if (scDisplay == 0)
        {
            throw new ScreenException(ScreenText.Failed("display " + display.Id.ToString(CultureInfo.InvariantCulture) + " is not shareable"));
        }

        nint pool = objc_autoreleasePoolPush();
        nint filter = 0, configuration = 0;
        try
        {
            filter = Send(Send(Class("SCContentFilter"), Sel("alloc")), Sel("initWithDisplay:excludingWindows:"), scDisplay, Send(Class("NSArray"), Sel("array")));
            configuration = Configuration(display.PixelWidth, display.PixelHeight);
            return Shoot(filter, configuration);
        }
        finally
        {
            Release(configuration);
            Release(filter);
            objc_autoreleasePoolPop(pool);
        }
    }

    private static nint Configuration(int width, int height)
    {
        nint configuration = Send(Send(Class("SCStreamConfiguration"), Sel("alloc")), Sel("init"));
        SendVoidULong(configuration, Sel("setWidth:"), (ulong)width);
        SendVoidULong(configuration, Sel("setHeight:"), (ulong)height);
        SendVoidBool(configuration, Sel("setShowsCursor:"), 0);
        return configuration;
    }

    /// <summary>One screenshot of <paramref name="filter"/>: a CGImage the caller releases.</summary>
    private static nint Shoot(nint filter, nint configuration)
    {
        nint manager = TryClass("SCScreenshotManager");
        var done = Await(block => SendVoid(manager, Sel("captureImageWithFilter:configuration:completionHandler:"), filter, configuration, block), CallTimeout);
        return Answered(done, "the screenshot");
    }

    private static nint Answered(Completed done, string what)
    {
        if (done.TimedOut)
        {
            throw new ScreenException(ScreenText.TimedOut);
        }

        if (done.Result == 0)
        {
            // -3801 is SCStreamErrorUserDeclined: the permission went away while the app ran.
            throw new ScreenException(done.Code == -3801 ? ScreenText.NoPermission(null) : ScreenText.Failed(what + ": " + (done.Error ?? "no answer")));
        }

        return done.Result;
    }

    /// <summary>The element of an NSArray whose 32-bit id property is <paramref name="id"/>, or 0.</summary>
    private static nint Find(nint array, nint idSelector, uint id)
    {
        ulong count = array == 0 ? 0 : SendULong(array, Sel("count"));
        for (ulong i = 0; i < count; i++)
        {
            nint item = SendIndex(array, Sel("objectAtIndex:"), (nuint)i);
            if (SendUInt(item, idSelector) == id)
            {
                return item;
            }
        }

        return 0;
    }

    private static void Release(nint value)
    {
        if (value != 0)
        {
            SendVoid(value, Sel("release"));
        }
    }

    /// <summary>A retained Objective-C object, released when done.</summary>
    private readonly struct Owned(nint handle) : IDisposable
    {
        public nint Handle { get; } = handle;

        public void Dispose() => Release(Handle);
    }
}
