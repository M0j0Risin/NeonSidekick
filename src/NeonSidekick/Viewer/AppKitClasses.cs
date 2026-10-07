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
    private static nint s_windowDelegate;
    private static nint s_appDelegate;

    public static nint WindowClass => s_window != 0 ? s_window : (s_window = MakeWindowClass());

    public static nint ViewClass => s_view != 0 ? s_view : (s_view = MakeViewClass());

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
        objc_registerClassPair(cls);
        return cls;
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
    private static void DidChangeBacking(nint self, nint selector, nint notification) => Delegated(self, static w => w.ScaleChanged());
}

/// <summary>A key as AppKit gave it (2026-10-07): the hardware key code, the modifier flags, and whether it is a held key's repeat.</summary>
internal readonly record struct MacKeyEvent(ushort KeyCode, ulong Flags, bool Repeat)
{
    [SupportedOSPlatform("macos")]
    public static MacKeyEvent From(nint e) =>
        new((ushort)SendULong(e, Sel("keyCode")), SendULong(e, Sel("modifierFlags")), SendBool(e, Sel("isARepeat")) != 0);
}
