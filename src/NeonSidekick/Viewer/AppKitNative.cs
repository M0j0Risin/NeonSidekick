using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NeonSidekick.Viewer;

/// <summary>A point in AppKit's coordinates (<c>CGPoint</c>: two doubles on arm64, passed in floating registers).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CGPoint
{
    public double X;
    public double Y;

    public CGPoint(double x, double y)
    {
        X = x;
        Y = y;
    }
}

/// <summary>A rectangle in AppKit's coordinates (<c>CGRect</c>: origin and size, four doubles, an HFA arm64 returns in d0–d3).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CGRect
{
    public double X;
    public double Y;
    public double Width;
    public double Height;

    public CGRect(double x, double y, double width, double height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }
}

/// <summary>
/// The Objective-C runtime, AppKit, Core Animation, CoreGraphics and libdispatch for the app's own windows on a Mac (2026-10-07,
/// Stage 2: the windows over AppKit). No Xamarin, no <c>net10.0-macos</c>: libobjc's <c>objc_msgSend</c> imported once per
/// argument shape (a typed signature each, never a variadic call — arm64 passes a variadic's arguments on the stack, a typed
/// one in registers as the method expects), the classes looked up by name, and the app's own NSWindow, NSView and delegate
/// subclasses made at runtime (<see cref="AppKitClasses"/>) with <c>[UnmanagedCallersOnly]</c> methods — the shape
/// <c>WebViewHandler</c> uses for COM. arm64 only (the Mac build's one runtime): no <c>objc_msgSend_stret</c>, a CGRect comes
/// back in registers. Every call is on the main thread (<see cref="AppKitHost"/>) but CoreGraphics', which is thread-safe.
/// Excluded from coverage with the rest of the AppKit layer; the smoke's <c>viewer:window</c> proves it on the published exe.
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe partial class AppKitNative
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    public const string AppKitPath = "/System/Library/Frameworks/AppKit.framework/AppKit";
    public const string QuartzCorePath = "/System/Library/Frameworks/QuartzCore.framework/QuartzCore";

    // NSWindowStyleMask
    public const ulong StyleBorderless = 0;
    public const ulong StyleTitled = 1;
    public const ulong StyleClosable = 2;
    public const ulong StyleMiniaturizable = 4;
    public const ulong StyleResizable = 8;
    public const ulong StyleStandard = StyleTitled | StyleClosable | StyleMiniaturizable | StyleResizable;

    public const ulong BackingBuffered = 2;

    // NSApplicationActivationPolicy
    public const long ActivationAccessory = 1;

    // NSApplicationPresentationOptions: the Dock and the menu bar hidden while the app is active (full screen).
    public const ulong PresentationDefault = 0;
    public const ulong PresentationHideDockAndMenuBar = 2 | 8;

    // NSEventType
    public const ulong EventKeyDown = 10;
    public const ulong EventApplicationDefined = 15;

    // NSTrackingAreaOptions: entered/exited and moved, whatever the window's state, the rect the view's visible one.
    public const ulong TrackingOptions = 0x01 | 0x02 | 0x80 | 0x200;

    // CAAutoresizingMask: kCALayerWidthSizable | kCALayerHeightSizable.
    public const uint LayerSizable = 2 | 16;

    // CGBitmapInfo: BGRX (the decode's order) and premultiplied BGRA (the arrows'), both 32-bit little-endian.
    public const uint BitmapBgrx = 6 | (2 << 12);
    public const uint BitmapPremultipliedBgra = 2 | (2 << 12);

    // NSApplicationTerminateReply
    public const nuint TerminateCancel = 0;

    // ---- libobjc ----

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint objc_getClass(string name);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint sel_registerName(string name);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint objc_allocateClassPair(nint superclass, string name, nuint extraBytes);

    [LibraryImport(ObjC)]
    public static partial void objc_registerClassPair(nint cls);

    [LibraryImport(ObjC, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool class_addMethod(nint cls, nint selector, nint implementation, string types);

    [LibraryImport(ObjC)]
    public static partial nint objc_autoreleasePoolPush();

    [LibraryImport(ObjC)]
    public static partial void objc_autoreleasePoolPop(nint pool);

    // objc_msgSend, one import per shape: named by what they take and give back.

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial nint Send(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial nint Send(nint receiver, nint selector, nint a);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial nint Send(nint receiver, nint selector, nint a, nint b);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial void SendVoid(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial void SendVoid(nint receiver, nint selector, nint a);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial void SendVoid(nint receiver, nint selector, nint a, nint b);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial void SendVoidBool(nint receiver, nint selector, byte value);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial byte SendBool(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial byte SendBoolLong(nint receiver, nint selector, long value);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial int SendInt(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial byte SendBoolULong(nint receiver, nint selector, ulong value);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial void SendVoidULong(nint receiver, nint selector, ulong value);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial void SendVoidUInt(nint receiver, nint selector, uint value);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial ulong SendULong(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial long SendLong(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial nint SendIndex(nint receiver, nint selector, nuint index);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial double SendDouble(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial void SendVoidDouble(nint receiver, nint selector, double value);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial void SendVoidFloat(nint receiver, nint selector, float value);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial CGRect SendRect(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial void SendVoidRect(nint receiver, nint selector, CGRect rect);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial void SendVoidRectBool(nint receiver, nint selector, CGRect rect, byte flag);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial CGPoint SendPoint(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial CGPoint SendPointConvert(nint receiver, nint selector, CGPoint point, nint view);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial nint SendInitWindow(nint receiver, nint selector, CGRect contentRect, ulong style, ulong backing, byte defer);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial nint SendInitRect(nint receiver, nint selector, CGRect rect);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial nint SendInitTracking(nint receiver, nint selector, CGRect rect, ulong options, nint owner, nint userInfo);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial nint SendColor(nint receiver, nint selector, double red, double green, double blue, double alpha);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial nint SendFont(nint receiver, nint selector, double size);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial void SendVoidNintBool(nint receiver, nint selector, nint a, byte flag);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial nint SendOtherEvent(nint receiver, nint selector, ulong type, CGPoint location, ulong flags, double timestamp, long windowNumber, nint context, short subtype, long data1, long data2);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial nint SendUtf8(nint receiver, nint selector, byte* text);

    /// <summary><c>struct objc_super</c>: the receiver and the class whose superclass's method is wanted (<c>objc_msgSendSuper</c>'s first argument).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ObjCSuper
    {
        public nint Receiver;
        public nint SuperClass;
    }

    [LibraryImport(ObjC, EntryPoint = "objc_msgSendSuper")]
    public static partial void SendSuperVoid(ObjCSuper* super, nint selector, nint a);

    // ---- CoreFoundation, CoreGraphics ----

    [LibraryImport(CoreFoundation)]
    public static partial void CFRelease(nint value);

    [LibraryImport(CoreGraphics)]
    public static partial nint CGSessionCopyCurrentDictionary();

    [LibraryImport(CoreGraphics)]
    public static partial nint CGColorSpaceCreateWithName(nint name);

    [LibraryImport(CoreGraphics)]
    public static partial void CGColorSpaceRelease(nint space);

    [LibraryImport(CoreGraphics)]
    public static partial nint CGDataProviderCreateWithData(nint info, void* data, nuint size, delegate* unmanaged<nint, void*, nuint, void> release);

    [LibraryImport(CoreGraphics)]
    public static partial void CGDataProviderRelease(nint provider);

    [LibraryImport(CoreGraphics)]
    public static partial nint CGImageCreate(nuint width, nuint height, nuint bitsPerComponent, nuint bitsPerPixel, nuint bytesPerRow, nint space, uint bitmapInfo, nint provider, double* decode, byte shouldInterpolate, int intent);

    [LibraryImport(CoreGraphics)]
    public static partial void CGImageRelease(nint image);

    [LibraryImport(CoreGraphics)]
    public static partial nint CGColorCreateSRGB(double red, double green, double blue, double alpha);

    [LibraryImport(CoreGraphics)]
    public static partial void CGColorRelease(nint color);

    // ---- libdispatch, pthread ----

    [LibraryImport(LibSystem)]
    public static partial void dispatch_async_f(nint queue, nint context, delegate* unmanaged<nint, void> work);

    [LibraryImport(LibSystem)]
    public static partial void dispatch_after_f(ulong when, nint queue, nint context, delegate* unmanaged<nint, void> work);

    [LibraryImport(LibSystem)]
    public static partial ulong dispatch_time(ulong when, long deltaNanoseconds);

    [LibraryImport(LibSystem)]
    public static partial int pthread_main_np();

    private static nint s_mainQueue;
    private static nint s_srgb;
    private static readonly Dictionary<string, nint> s_selectors = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, nint> s_classes = new(StringComparer.Ordinal);
    private static readonly Lock s_gate = new();

    /// <summary>The main queue (<c>dispatch_get_main_queue()</c> is a macro over this exported object's address).</summary>
    public static nint MainQueue
    {
        get
        {
            if (s_mainQueue == 0)
            {
                s_mainQueue = NativeLibrary.GetExport(NativeLibrary.Load(LibSystem), "_dispatch_main_q");
            }

            return s_mainQueue;
        }
    }

    /// <summary>A selector, registered once and kept.</summary>
    public static nint Sel(string name)
    {
        lock (s_gate)
        {
            if (!s_selectors.TryGetValue(name, out nint selector))
            {
                selector = sel_registerName(name);
                s_selectors[name] = selector;
            }

            return selector;
        }
    }

    /// <summary>A class by name (AppKit and QuartzCore loaded first), kept; throws when there is none.</summary>
    public static nint Class(string name)
    {
        lock (s_gate)
        {
            if (!s_classes.TryGetValue(name, out nint cls))
            {
                cls = objc_getClass(name);
                if (cls == 0)
                {
                    throw new InvalidOperationException($"no Objective-C class {name}");
                }

                s_classes[name] = cls;
            }

            return cls;
        }
    }

    /// <summary>A new autoreleased NSString with <paramref name="text"/>'s characters. Inside an autorelease pool only.</summary>
    public static nint NSString(string text)
    {
        int length = System.Text.Encoding.UTF8.GetByteCount(text);
        Span<byte> bytes = length < 1024 ? stackalloc byte[length + 1] : new byte[length + 1];
        System.Text.Encoding.UTF8.GetBytes(text, bytes);
        bytes[length] = 0;
        fixed (byte* p = bytes)
        {
            return SendUtf8(Class("NSString"), Sel("stringWithUTF8String:"), p);
        }
    }

    /// <summary>An NSString's characters, or null for nil.</summary>
    public static string? FromNSString(nint value)
    {
        if (value == 0)
        {
            return null;
        }

        nint utf8 = Send(value, Sel("UTF8String"));
        return utf8 == 0 ? null : Marshal.PtrToStringUTF8(utf8);
    }

    /// <summary>A COLORREF (<c>0x00BBGGRR</c>, the viewer's style) as an autoreleased sRGB NSColor.</summary>
    public static nint NSColor(uint colorRef) =>
        SendColor(Class("NSColor"), Sel("colorWithSRGBRed:green:blue:alpha:"), (colorRef & 0xFF) / 255.0, ((colorRef >> 8) & 0xFF) / 255.0, ((colorRef >> 16) & 0xFF) / 255.0, 1.0);

    /// <summary>A COLORREF as a CGColor the caller releases (<see cref="CGColorRelease"/>).</summary>
    public static nint CGColor(uint colorRef) =>
        CGColorCreateSRGB((colorRef & 0xFF) / 255.0, ((colorRef >> 8) & 0xFF) / 255.0, ((colorRef >> 16) & 0xFF) / 255.0, 1.0);

    /// <summary>
    /// 32-bit pixels as a CGImage the caller releases, with no copy: the array is pinned and handed to CoreGraphics, and let go by
    /// the data provider's release callback when the last image over it is gone (on whichever thread drops it). Any thread.
    /// </summary>
    public static nint CGImage(byte[] pixels, int width, int height, uint bitmapInfo)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (s_srgb == 0)
        {
            nint name = Marshal.ReadIntPtr(NativeLibrary.GetExport(NativeLibrary.Load(CoreGraphics), "kCGColorSpaceSRGB"));
            s_srgb = CGColorSpaceCreateWithName(name);   // kept for the process
        }

        var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        nint provider = CGDataProviderCreateWithData(GCHandle.ToIntPtr(pin), (void*)pin.AddrOfPinnedObject(), (nuint)pixels.Length, &ReleasePixels);
        if (provider == 0)
        {
            pin.Free();
            return 0;
        }

        nint image = CGImageCreate((nuint)width, (nuint)height, 8, 32, (nuint)(width * 4), s_srgb, bitmapInfo, provider, null, 1, 0);
        CGDataProviderRelease(provider);   // the image keeps it; the pin goes with the last of them
        return image;
    }

    [UnmanagedCallersOnly]
    private static void ReleasePixels(nint info, void* data, nuint size) => GCHandle.FromIntPtr(info).Free();

    /// <summary>Whether a window server is there to draw on (a desktop session; none over SSH or on a headless box).</summary>
    public static bool HasWindowServer()
    {
        nint session = CGSessionCopyCurrentDictionary();
        if (session == 0)
        {
            return false;
        }

        CFRelease(session);
        return true;
    }
}
