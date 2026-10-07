using System.Runtime.Versioning;
using NeonSidekick.Diagnostics;
using static NeonSidekick.Viewer.AppKitNative;

namespace NeonSidekick.Viewer;

/// <summary>
/// What every Mac window of the app's own does to its frame (2026-10-07, the windows over AppKit; <see cref="WindowChrome"/>'s
/// twin): an NSWindow of the app's class with a layer-backed view of its own, its delegate, the place it last closed restored and
/// kept (<see cref="MacPlacement"/>), full screen and back, the theme (the dark or light appearance by the theme's background, the
/// bar in its colour), and the two ways of coming forward. Full screen is the window made borderless over its screen with the
/// Dock and the menu bar hidden while the app is active — not a full-screen Space, so TAB to the terminal does not slide to
/// another desktop, as F11 on Windows stays on the same one. Subclasses take the keys, the mouse and the window's events. Main
/// thread only; excluded from coverage with the rest of the AppKit layer.
/// </summary>
[SupportedOSPlatform("macos")]
internal abstract class AppKitWindow
{
    private static readonly Dictionary<nint, AppKitWindow> s_objects = [];
    private static readonly HashSet<AppKitWindow> s_open = [];

    /// <summary>The app in front before one of ours took the keyboard (an NSRunningApplication, retained): the terminal, as a rule.</summary>
    private static nint s_before;

    private ulong _savedStyle;
    private CGRect _savedFrame;
    private ViewerStyle? _chrome;
    private bool _closed;

    /// <summary>The NSWindow, the content view and the delegate (each retained by this window until it is gone).</summary>
    protected nint Window { get; private set; }

    protected nint View { get; private set; }

    private nint _delegate;

    /// <summary>The view's own layer, the picture's and the overlays' parent.</summary>
    protected nint RootLayer { get; private set; }

    /// <summary>Whether the window is full screen.</summary>
    public bool FullScreen { get; private set; }

    /// <summary>Whether the window is open (made, not yet closed).</summary>
    public bool Alive => Window != 0 && !_closed;

    /// <summary>The window an Objective-C object of it belongs to (its window, view or delegate), or null.</summary>
    public static AppKitWindow? Of(nint instance) => s_objects.GetValueOrDefault(instance);

    /// <summary>
    /// The window made, not shown: <paramref name="width"/> × <paramref name="height"/> points where <paramref name="position"/>
    /// says it last closed (moved onto a screen), else centred on the first screen.
    /// </summary>
    protected void Create(double width, double height, Func<(int X, int Y)?>? position)
    {
        nint window = SendInitWindow(Send(AppKitClasses.WindowClass, Sel("alloc")), Sel("initWithContentRect:styleMask:backing:defer:"),
            new CGRect(0, 0, width, height), StyleStandard, BackingBuffered, 0);
        if (window == 0)
        {
            throw new InvalidOperationException("NSWindow could not be made");
        }

        Window = window;
        s_objects[window] = this;
        s_open.Add(this);
        SendVoidBool(window, Sel("setReleasedWhenClosed:"), 0);   // this owns it: released after its close, never by it
        SendVoidBool(window, Sel("setRestorable:"), 0);
        SendVoidULong(window, Sel("setCollectionBehavior:"), 1UL << 9);   // FullScreenNone: the green button zooms, no Space of its own

        nint view = SendInitRect(Send(AppKitClasses.ViewClass, Sel("alloc")), Sel("initWithFrame:"), new CGRect(0, 0, width, height));
        View = view;
        s_objects[view] = this;
        SendVoidBool(view, Sel("setWantsLayer:"), 1);
        SendVoid(window, Sel("setContentView:"), view);
        RootLayer = Send(view, Sel("layer"));

        nint tracking = SendInitTracking(Send(Class("NSTrackingArea"), Sel("alloc")), Sel("initWithRect:options:owner:userInfo:"), default, TrackingOptions, view, 0);
        SendVoid(view, Sel("addTrackingArea:"), tracking);
        SendVoid(tracking, Sel("release"));

        nint windowDelegate = Send(Send(AppKitClasses.WindowDelegateClass, Sel("alloc")), Sel("init"));
        _delegate = windowDelegate;
        s_objects[windowDelegate] = this;
        SendVoid(window, Sel("setDelegate:"), windowDelegate);
        SendVoid(window, Sel("makeFirstResponder:"), view);
        SendVoidBool(window, Sel("setTitlebarAppearsTransparent:"), 1);

        RestorePlace(width, height, position);
    }

    /// <summary>The title bar's text.</summary>
    protected void SetTitle(string title) => SendVoid(Window, Sel("setTitle:"), NSString(title));

    /// <summary>
    /// Shown: brought forward with the keyboard (<paramref name="activate"/>; the app activated over the terminal), or shown over the
    /// other windows without taking it (<paramref name="raise"/>), or, when neither, shown where it is in the order (a window already
    /// up left alone).
    /// </summary>
    protected void ShowWindow(bool activate, bool raise = true)
    {
        if (activate)
        {
            RememberFront();
            SendVoidBool(Send(Class("NSApplication"), Sel("sharedApplication")), Sel("activateIgnoringOtherApps:"), 1);
            SendVoid(Window, Sel("makeKeyAndOrderFront:"), 0);
            return;
        }

        if (raise || SendBool(Window, Sel("isVisible")) == 0)
        {
            SendVoid(Window, Sel("orderFrontRegardless"));
        }
    }

    /// <summary>The window closed (its <see cref="WillClose"/> runs: the place kept, the subclass told).</summary>
    public void Close()
    {
        if (Alive)
        {
            SendVoid(Window, Sel("close"));
        }
    }

    /// <summary>Told the corner the window will be restored at next (the app's hook), as it closes.</summary>
    protected abstract Action<int, int>? Placed { get; }

    /// <summary>The window's own end, after its place is kept: what the subclass lets go.</summary>
    protected abstract void OnClosed();

    /// <summary>The window's delegate: it is closing (by the red button, ⌘W, Esc, or the app). Once.</summary>
    internal void WillClose()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        s_open.Remove(this);
        if (FullScreen)
        {
            SetPresentation(false);
        }

        RememberPlace();
        if (s_open.Count == 0)
        {
            GiveBack();
        }

        try
        {
            OnClosed();
        }
        finally
        {
            // Let go after AppKit is done with the close in hand: the window, its view and its delegate, then forgotten.
            nint window = Window, view = View, windowDelegate = _delegate;
            AppKitHost.Post(() =>
            {
                SendVoid(window, Sel("setDelegate:"), 0);
                s_objects.Remove(window);
                s_objects.Remove(view);
                s_objects.Remove(windowDelegate);
                SendVoid(view, Sel("release"));
                SendVoid(windowDelegate, Sel("release"));
                SendVoid(window, Sel("release"));
            });
        }
    }

    // ---- what subclasses take ----

    /// <summary>A key down: true when the window took it (nothing else sees it).</summary>
    internal virtual bool KeyDown(MacKeyEvent key) => false;

    internal virtual void MouseDown(nint e)
    {
    }

    internal virtual void MouseUp(nint e)
    {
    }

    internal virtual void MouseDragged(nint e)
    {
    }

    internal virtual void MouseMoved(nint e)
    {
    }

    internal virtual void MouseEntered(nint e)
    {
    }

    internal virtual void MouseExited(nint e)
    {
    }

    internal virtual void RightMouseDown(nint e)
    {
    }

    internal virtual void ScrollWheel(nint e)
    {
    }

    internal virtual void Resized()
    {
    }

    internal virtual void BecameKey()
    {
    }

    internal virtual void ScaleChanged()
    {
    }

    // ---- helpers ----

    /// <summary>The view's size in points.</summary>
    protected (double Width, double Height) ClientSize
    {
        get
        {
            var bounds = SendRect(View, Sel("bounds"));
            return (bounds.Width, bounds.Height);
        }
    }

    /// <summary>The backing scale (2 on a Retina screen): pixels per point where the window is now.</summary>
    protected double Scale => Window == 0 ? 1 : Math.Max(1, SendDouble(Window, Sel("backingScaleFactor")));

    /// <summary>An event's point in the view, top-left origin (the view is flipped).</summary>
    protected (double X, double Y) PointOf(nint e) =>
        SendPointConvert(View, Sel("convertPoint:fromView:"), SendPoint(e, Sel("locationInWindow")), 0) is var p ? (p.X, p.Y) : default;

    /// <summary>Changes to layers without Core Animation's implicit quarter-second fades.</summary>
    protected static void WithoutAnimation(Action change)
    {
        nint transaction = Class("CATransaction");
        SendVoid(transaction, Sel("begin"));
        SendVoidBool(transaction, Sel("setDisableActions:"), 1);
        try
        {
            change();
        }
        finally
        {
            SendVoid(transaction, Sel("commit"));
        }
    }

    /// <summary>
    /// The theme on the frame: the bar and the window in <paramref name="style"/>'s background, with the dark appearance on a dark
    /// one (the bar's text and buttons light) and the light on a light one. Nothing when unchanged.
    /// </summary>
    protected void ApplyChrome(ViewerStyle style)
    {
        if (_chrome == style)
        {
            return;
        }

        _chrome = style;
        uint bg = style.Background;
        double luminance = (0.2126 * (bg & 0xFF)) + (0.7152 * ((bg >> 8) & 0xFF)) + (0.0722 * ((bg >> 16) & 0xFF));
        nint appearance = Send(Class("NSAppearance"), Sel("appearanceNamed:"), NSString(luminance < 128 ? "NSAppearanceNameDarkAqua" : "NSAppearanceNameAqua"));
        SendVoid(Window, Sel("setAppearance:"), appearance);
        SendVoid(Window, Sel("setBackgroundColor:"), NSColor(style.Caption));
    }

    /// <summary>Full screen and back: borderless over the window's screen, the Dock and menu bar hidden while the app is in front.</summary>
    protected void SetFullScreen(bool on)
    {
        if (on == FullScreen || !Alive)
        {
            return;
        }

        if (on)
        {
            nint screen = Send(Window, Sel("screen"));
            if (screen == 0)
            {
                return;
            }

            _savedStyle = SendULong(Window, Sel("styleMask"));
            _savedFrame = SendRect(Window, Sel("frame"));
            SendVoidULong(Window, Sel("setStyleMask:"), StyleBorderless);
            SendVoidRectBool(Window, Sel("setFrame:display:"), SendRect(screen, Sel("frame")), 1);
            SetPresentation(true);
            SendVoid(Window, Sel("makeFirstResponder:"), View);
        }
        else
        {
            SetPresentation(false);
            SendVoidULong(Window, Sel("setStyleMask:"), _savedStyle);
            SendVoidBool(Window, Sel("setTitlebarAppearsTransparent:"), 1);
            SendVoidRectBool(Window, Sel("setFrame:display:"), _savedFrame, 1);
            SendVoid(Window, Sel("makeFirstResponder:"), View);
        }

        FullScreen = on;
    }

    // The app in front kept as one of ours is activated (2026-10-07, found in the live check: after ⌘W the app stayed active with no
    // window, so the keys typed next went nowhere) — unless it is this app already, a window of ours raising another.
    private static void RememberFront()
    {
        nint front = Send(Send(Class("NSWorkspace"), Sel("sharedWorkspace")), Sel("frontmostApplication"));
        if (front == 0 || SendInt(front, Sel("processIdentifier")) == Environment.ProcessId)
        {
            return;
        }

        if (s_before != 0)
        {
            SendVoid(s_before, Sel("release"));
        }

        s_before = Send(front, Sel("retain"));
    }

    // The last window of ours closed while the app has the keyboard: it goes back to the app that had it (the terminal), or, when that
    // one is gone, to whichever macOS picks as this app hides.
    private static void GiveBack()
    {
        nint app = Send(Class("NSApplication"), Sel("sharedApplication"));
        if (SendBool(app, Sel("isActive")) == 0)
        {
            return;
        }

        if (s_before != 0 && SendBool(s_before, Sel("isTerminated")) == 0 && SendBoolULong(s_before, Sel("activateWithOptions:"), 0) != 0)
        {
            return;
        }

        SendVoid(app, Sel("hide:"), 0);
    }

    private static void SetPresentation(bool hidden) =>
        SendVoidULong(Send(Class("NSApplication"), Sel("sharedApplication")), Sel("setPresentationOptions:"), hidden ? PresentationHideDockAndMenuBar : PresentationDefault);

    // The screens' visible parts and the first screen's height (the flip's).
    private static (List<PlaceRect> Visible, double FirstHeight) Screens()
    {
        nint screens = Send(Class("NSScreen"), Sel("screens"));
        nuint count = (nuint)SendULong(screens, Sel("count"));
        var visible = new List<PlaceRect>();
        double first = 0;
        for (nuint i = 0; i < count; i++)
        {
            nint screen = SendIndex(screens, Sel("objectAtIndex:"), i);
            if (i == 0)
            {
                first = SendRect(screen, Sel("frame")).Height;
            }

            var v = SendRect(screen, Sel("visibleFrame"));
            visible.Add(new PlaceRect(v.X, v.Y, v.Width, v.Height));
        }

        return (visible.Select(r => MacPlacement.Flip(r, first)).ToList(), first);
    }

    private void RestorePlace(double width, double height, Func<(int X, int Y)?>? position)
    {
        var frame = SendRect(Window, Sel("frame"));   // the content's size plus the bar
        var (visible, first) = Screens();
        if (MacPlacement.Restore(position?.Invoke(), frame.Width, frame.Height, visible) is { } place)
        {
            var cocoa = MacPlacement.Flip(place, first);
            SendVoidRectBool(Window, Sel("setFrame:display:"), new CGRect(cocoa.X, cocoa.Y, cocoa.Width, cocoa.Height), 0);
            return;
        }

        SendVoid(Window, Sel("center"));
        DiagnosticLog.Debug("Viewer", $"A {width:0}x{height:0} window opens centred (no place saved, or none on a screen now).");
    }

    // The corner told to the app: the frame it had before full screen when it closes full screen.
    private void RememberPlace()
    {
        if (Placed is not { } placed)
        {
            return;
        }

        var frame = FullScreen ? _savedFrame : SendRect(Window, Sel("frame"));
        var (_, first) = Screens();
        var (x, y) = MacPlacement.Corner(MacPlacement.Flip(new PlaceRect(frame.X, frame.Y, frame.Width, frame.Height), first));
        try
        {
            placed(x, y);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("Viewer", "Could not keep the window's place: " + ex.Message);
        }
    }
}
