using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NeonSidekick.Diagnostics;
using static NeonSidekick.Viewer.AppKitNative;

namespace NeonSidekick.Viewer;

/// <summary>
/// The app's own Objective-C classes, made at runtime on a Mac (2026-10-07): <c>NeonSidekickWindow</c> (an NSWindow that can be
/// key without a title bar, for full screen, and whose <c>sendEvent:</c> hands every key to its window first),
/// <c>NeonSidekickView</c> (an NSView taking the keyboard, the mouse and the wheel), <c>NeonSidekickWindowDelegate</c> (the close,
/// a resize, the keyboard arriving, a screen of another scale) and <c>NeonSidekickAppDelegate</c> (AppKit's quit cancelled).
/// Each method is an <c>[UnmanagedCallersOnly]</c> function added with <c>class_addMethod</c>; the instances find their
/// <see cref="AppKitWindow"/> through <see cref="AppKitWindow.Of"/>. Every method catches all: nothing may unwind into AppKit.
/// Main thread only.
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe class AppKitClasses
{
    /// <summary>What the probe selector answers (<c>neonProbe</c>, the smoke's proof that a method of ours is called).</summary>
    public const long ProbeAnswer = 0x5EE;

    private static nint s_window;
    private static nint s_view;
    private static nint s_drawnView;
    private static nint s_menuTarget;
    private static nint s_windowDelegate;
    private static nint s_appDelegate;

    public static nint WindowClass => s_window != 0 ? s_window : (s_window = MakeWindowClass());

    public static nint ViewClass => s_view != 0 ? s_view : (s_view = MakeViewClass());

    /// <summary>
    /// The view that draws itself (<c>drawRect:</c> into <see cref="AppKitWindow.Draw"/>; the thumbnail browser's grid): a subclass of
    /// <see cref="ViewClass"/>, so a layer-backed view of the plain class (the picture viewer's) is never asked to draw.
    /// </summary>
    public static nint DrawnViewClass => s_drawnView != 0 ? s_drawnView : (s_drawnView = MakeDrawnViewClass());

    /// <summary>The target a picture menu's rows send <c>chosen:</c> to (<see cref="MacPictureMenu"/>); its class made once.</summary>
    public static nint MenuTargetClass => s_menuTarget != 0 ? s_menuTarget : (s_menuTarget = MakeMenuTargetClass());

    /// <summary>The tag of the row last chosen in a picture menu, or null; read and cleared by <see cref="MacPictureMenu"/>. Main thread.</summary>
    public static long? MenuChosen { get; set; }

    public static nint WindowDelegateClass => s_windowDelegate != 0 ? s_windowDelegate : (s_windowDelegate = MakeWindowDelegateClass());

    /// <summary>The app's delegate, a new instance (kept for the process by NSApp's caller).</summary>
    public static nint NewAppDelegate()
    {
        if (s_appDelegate == 0)
        {
            nint cls = objc_allocateClassPair(Class("NSObject"), "NeonSidekickAppDelegate", 0);
            class_addMethod(cls, Sel("applicationShouldTerminate:"), (nint)(delegate* unmanaged<nint, nint, nint, nuint>)&ShouldTerminate, "Q@:@");
            class_addMethod(cls, Sel("applicationSupportsSecureRestorableState:"), (nint)(delegate* unmanaged<nint, nint, nint, byte>)&Yes1, "B@:@");
            objc_registerClassPair(cls);
            s_appDelegate = cls;
        }

        return Send(Send(s_appDelegate, Sel("alloc")), Sel("init"));
    }

    private static nint MakeWindowClass()
    {
        nint cls = objc_allocateClassPair(Class("NSWindow"), "NeonSidekickWindow", 0);
        class_addMethod(cls, Sel("canBecomeKeyWindow"), (nint)(delegate* unmanaged<nint, nint, byte>)&Yes, "B@:");
        class_addMethod(cls, Sel("canBecomeMainWindow"), (nint)(delegate* unmanaged<nint, nint, byte>)&Yes, "B@:");
        class_addMethod(cls, Sel("sendEvent:"), (nint)(delegate* unmanaged<nint, nint, nint, void>)&SendEvent, "v@:@");
        class_addMethod(cls, Sel("neonProbe"), (nint)(delegate* unmanaged<nint, nint, long>)&Probe, "q@:");
        objc_registerClassPair(cls);
        return cls;
    }

    private static nint MakeViewClass()
    {
        nint cls = objc_allocateClassPair(Class("NSView"), "NeonSidekickView", 0);
        class_addMethod(cls, Sel("acceptsFirstResponder"), (nint)(delegate* unmanaged<nint, nint, byte>)&Yes, "B@:");
        class_addMethod(cls, Sel("acceptsFirstMouse:"), (nint)(delegate* unmanaged<nint, nint, nint, byte>)&Yes1, "B@:@");
        class_addMethod(cls, Sel("isFlipped"), (nint)(delegate* unmanaged<nint, nint, byte>)&Yes, "B@:");
        class_addMethod(cls, Sel("keyDown:"), (nint)(delegate* unmanaged<nint, nint, nint, void>)&Ignore, "v@:@");   // no beep for a key nobody takes
        AddMouse(cls, "mouseDown:", &MouseDown);
        AddMouse(cls, "mouseUp:", &MouseUp);
        AddMouse(cls, "mouseDragged:", &MouseDragged);
        AddMouse(cls, "mouseMoved:", &MouseMoved);
        AddMouse(cls, "mouseEntered:", &MouseEntered);
        AddMouse(cls, "mouseExited:", &MouseExited);
        AddMouse(cls, "rightMouseDown:", &RightMouseDown);
        AddMouse(cls, "scrollWheel:", &ScrollWheel);
        AddMouse(cls, "magnifyWithEvent:", &Magnify);
        objc_registerClassPair(cls);
        return cls;
    }

    private static nint MakeDrawnViewClass()
    {
        nint cls = objc_allocateClassPair(ViewClass, "NeonSidekickDrawnView", 0);
        class_addMethod(cls, Sel("drawRect:"), (nint)(delegate* unmanaged<nint, nint, CGRect, void>)&DrawRect, "v@:{CGRect={CGPoint=dd}{CGSize=dd}}");
        objc_registerClassPair(cls);
        return cls;
    }

    private static nint MakeMenuTargetClass()
    {
        nint cls = objc_allocateClassPair(Class("NSObject"), "NeonSidekickMenuTarget", 0);
        class_addMethod(cls, Sel("chosen:"), (nint)(delegate* unmanaged<nint, nint, nint, void>)&Chosen, "v@:@");
        objc_registerClassPair(cls);
        return cls;
    }

    // A picture menu's row: its tag kept for the menu's caller, which runs it once the menu is gone (Windows' posted message).
    [UnmanagedCallersOnly]
    private static void Chosen(nint self, nint selector, nint item)
    {
        try
        {
            MenuChosen = SendLong(item, Sel("tag"));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("Viewer", "A picture menu's row failed.", ex);
        }
    }

    [UnmanagedCallersOnly]
    private static void DrawRect(nint self, nint selector, CGRect dirty)
    {
        try
        {
            if (AppKitWindow.Of(self) is { } window)
            {
                nint context = Send(Send(Class("NSGraphicsContext"), Sel("currentContext")), Sel("CGContext"));
                if (context != 0)
                {
                    window.Draw(context, dirty);
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("Viewer", "A window failed to draw.", ex);
        }
    }

    private static void AddMouse(nint cls, string selector, delegate* unmanaged<nint, nint, nint, void> method) =>
        class_addMethod(cls, Sel(selector), (nint)method, "v@:@");

    private static nint MakeWindowDelegateClass()
    {
        nint cls = objc_allocateClassPair(Class("NSObject"), "NeonSidekickWindowDelegate", 0);
        class_addMethod(cls, Sel("windowWillClose:"), (nint)(delegate* unmanaged<nint, nint, nint, void>)&WillClose, "v@:@");
        class_addMethod(cls, Sel("windowDidResize:"), (nint)(delegate* unmanaged<nint, nint, nint, void>)&DidResize, "v@:@");
        class_addMethod(cls, Sel("windowDidBecomeKey:"), (nint)(delegate* unmanaged<nint, nint, nint, void>)&DidBecomeKey, "v@:@");
        class_addMethod(cls, Sel("windowDidChangeBackingProperties:"), (nint)(delegate* unmanaged<nint, nint, nint, void>)&DidChangeBacking, "v@:@");
        class_addMethod(cls, Sel("boundsChanged:"), (nint)(delegate* unmanaged<nint, nint, nint, void>)&BoundsChanged, "v@:@");   // a scroll view's clip view moved
        objc_registerClassPair(cls);
        return cls;
    }

    [UnmanagedCallersOnly]
    private static byte Yes(nint self, nint selector) => 1;

    [UnmanagedCallersOnly]
    private static byte Yes1(nint self, nint selector, nint argument) => 1;

    [UnmanagedCallersOnly]
    private static void Ignore(nint self, nint selector, nint argument)
    {
    }

    [UnmanagedCallersOnly]
    private static long Probe(nint self, nint selector) => ProbeAnswer;

    [UnmanagedCallersOnly]
    private static nuint ShouldTerminate(nint self, nint selector, nint sender)
    {
        try
        {
            AppKitHost.OnTerminateAsked();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("Viewer", "Asking the app to end failed.", ex);
        }

        return TerminateCancel;
    }

    // Every key goes to its window first (a chord AppKit would take for a menu's too); the rest to NSWindow's own.
    [UnmanagedCallersOnly]
    private static void SendEvent(nint self, nint selector, nint e)
    {
        try
        {
            if (SendULong(e, Sel("type")) == EventKeyDown && AppKitWindow.Of(self) is { } window && window.KeyDown(MacKeyEvent.From(e)))
            {
                return;
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("Viewer", "A window's key failed.", ex);
            return;
        }

        var super = new ObjCSuper { Receiver = self, SuperClass = Class("NSWindow") };
        SendSuperVoid(&super, selector, e);
    }

    private static void Mouse(nint view, nint e, Action<AppKitWindow, nint> handle)
    {
        try
        {
            if (AppKitWindow.Of(view) is { } window)
            {
                handle(window, e);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("Viewer", "A window's mouse event failed.", ex);
        }
    }

    [UnmanagedCallersOnly]
    private static void MouseDown(nint self, nint selector, nint e) => Mouse(self, e, static (w, ev) => w.MouseDown(ev));

    [UnmanagedCallersOnly]
    private static void MouseUp(nint self, nint selector, nint e) => Mouse(self, e, static (w, ev) => w.MouseUp(ev));

    [UnmanagedCallersOnly]
    private static void MouseDragged(nint self, nint selector, nint e) => Mouse(self, e, static (w, ev) => w.MouseDragged(ev));

    [UnmanagedCallersOnly]
    private static void MouseMoved(nint self, nint selector, nint e) => Mouse(self, e, static (w, ev) => w.MouseMoved(ev));

    [UnmanagedCallersOnly]
    private static void MouseEntered(nint self, nint selector, nint e) => Mouse(self, e, static (w, ev) => w.MouseEntered(ev));

    [UnmanagedCallersOnly]
    private static void MouseExited(nint self, nint selector, nint e) => Mouse(self, e, static (w, ev) => w.MouseExited(ev));

    [UnmanagedCallersOnly]
    private static void RightMouseDown(nint self, nint selector, nint e) => Mouse(self, e, static (w, ev) => w.RightMouseDown(ev));

    [UnmanagedCallersOnly]
    private static void Magnify(nint self, nint selector, nint e) => Mouse(self, e, static (w, ev) => w.Magnify(ev));

    [UnmanagedCallersOnly]
    private static void ScrollWheel(nint self, nint selector, nint e) => Mouse(self, e, static (w, ev) => w.ScrollWheel(ev));

    private static void Delegated(nint self, Action<AppKitWindow> handle)
    {
        try
        {
            if (AppKitWindow.Of(self) is { } window)
            {
                handle(window);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error("Viewer", "A window's delegate call failed.", ex);
        }
    }

    [UnmanagedCallersOnly]
    private static void WillClose(nint self, nint selector, nint notification) => Delegated(self, static w => w.WillClose());

    [UnmanagedCallersOnly]
    private static void DidResize(nint self, nint selector, nint notification) => Delegated(self, static w => w.Resized());

    [UnmanagedCallersOnly]
    private static void DidBecomeKey(nint self, nint selector, nint notification) => Delegated(self, static w => w.BecameKey());

    [UnmanagedCallersOnly]
    private static void BoundsChanged(nint self, nint selector, nint notification) => Delegated(self, static w => w.Scrolled());

    [UnmanagedCallersOnly]
    private static void DidChangeBacking(nint self, nint selector, nint notification) => Delegated(self, static w => w.ScaleChanged());
}

/// <summary>A key as AppKit gave it (2026-10-07): the hardware key code, the modifier flags, and whether it is a held key's repeat.</summary>
internal readonly record struct MacKeyEvent(ushort KeyCode, ulong Flags, bool Repeat)
{
    [SupportedOSPlatform("macos")]
    public static MacKeyEvent From(nint e) =>
        new((ushort)SendULong(e, Sel("keyCode")), SendULong(e, Sel("modifierFlags")), SendBool(e, Sel("isARepeat")) != 0);
}
