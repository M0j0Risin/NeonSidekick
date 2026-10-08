using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NeonSidekick.Viewer;
using static NeonSidekick.Viewer.AppKitNative;

namespace NeonSidekick.Screen;

/// <summary>
/// The CoreGraphics, CoreFoundation and ScreenCaptureKit plumbing behind <see cref="MacScreenSystem"/> (2026-10-07, Stage 2: screen
/// capture on a Mac). The Objective-C side goes through <see cref="AppKitNative"/>'s typed <c>objc_msgSend</c> imports; this adds
/// the C calls, the window list's dictionary reads, and ScreenCaptureKit's completion blocks. <b>Blocks without the compiler:</b>
/// a block is a struct whose first word is its class; one marked <c>BLOCK_IS_GLOBAL</c> (isa <c>_NSConcreteGlobalBlock</c>) is never
/// copied or released by the runtime, so <c>Block_copy</c> hands back the same pointer and the struct can live in native memory with a
/// trailing field of our own (a <see cref="GCHandle"/> to the call's state). The invoke is an <c>[UnmanagedCallersOnly]</c> function.
/// Measured in a C probe built without block syntax on macOS 15.7.9 (the user's 5K M4), both <c>getShareableContentWithCompletionHandler:</c>
/// and <c>captureImageWithFilter:configuration:completionHandler:</c> called such a block back. Two lifetimes matter: the wait gives up
/// (the capture's 10 s cap) while ScreenCaptureKit may still call back later — found probing: a window on another Space never
/// completed — so a late call finds its state abandoned and drops the result; and the runtime may still read the block's flags
/// after the invoke returns (<c>Block_release</c>), so a spent block is not freed but reused by a later call once it has been idle
/// a minute. Excluded from coverage with the AppKit layer; the smoke's <c>screen:sck</c> proves the block on the published exe.
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe partial class MacScreenNative
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    public const string ScreenCaptureKitPath = "/System/Library/Frameworks/ScreenCaptureKit.framework/ScreenCaptureKit";

    public const uint OnScreenOnly = 1;              // kCGWindowListOptionOnScreenOnly
    public const uint IncludingWindow = 8;           // kCGWindowListOptionIncludingWindow
    public const uint ExcludeDesktopElements = 16;   // kCGWindowListExcludeDesktopElements

    /// <summary>BGRX in memory: 32-bit little-endian words with the alpha byte skipped first (<c>kCGBitmapByteOrder32Little | kCGImageAlphaNoneSkipFirst</c>).</summary>
    public const uint Bgrx = (2 << 12) | 6;

    private const int BlockIsGlobal = 1 << 28;
    private const int NumberSInt64 = 4;              // kCFNumberSInt64Type
    private const int NumberFloat64 = 6;             // kCFNumberFloat64Type
    private const int InterpolationHigh = 3;         // kCGInterpolationHigh
    private static readonly TimeSpan Reuse = TimeSpan.FromMinutes(1);

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockDescriptor
    {
        public nuint Reserved;
        public nuint Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockLiteral
    {
        public nint Isa;
        public int Flags;
        public int Reserved;
        public nint Invoke;
        public BlockDescriptor* Descriptor;
        public nint State;                           // ours: the GCHandle of the call's Completion
        public BlockDescriptor OwnDescriptor;        // ours: the descriptor kept beside the block
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CFRange
    {
        public nint Location;
        public nint Length;
    }

    /// <summary>What a completion handler handed back: an object retained for the caller (0 for none) and the error's words and code.</summary>
    public readonly record struct Completed(nint Result, string? Error, long Code, bool TimedOut);

    private sealed class Completion
    {
        public readonly ManualResetEventSlim Done = new();
        public readonly Lock Gate = new();
        public nint Result;
        public string? Error;
        public long Code;
        public bool Abandoned;
    }

    private static readonly Lock s_spentGate = new();
    private static readonly Queue<(nint Block, long At)> s_spent = new();
    private static nint s_globalBlockClass;
    private static nint s_srgb;
    private static readonly Dictionary<string, nint> s_keys = new(StringComparer.Ordinal);

    // ---- CoreGraphics ----

    [LibraryImport(CoreGraphics)]
    public static partial int CGGetActiveDisplayList(uint maxDisplays, uint* displays, uint* count);

    [LibraryImport(CoreGraphics)]
    public static partial uint CGMainDisplayID();

    [LibraryImport(CoreGraphics)]
    public static partial CGRect CGDisplayBounds(uint display);

    [LibraryImport(CoreGraphics)]
    public static partial nint CGDisplayCopyDisplayMode(uint display);

    [LibraryImport(CoreGraphics)]
    public static partial nuint CGDisplayModeGetPixelWidth(nint mode);

    [LibraryImport(CoreGraphics)]
    public static partial nuint CGDisplayModeGetPixelHeight(nint mode);

    [LibraryImport(CoreGraphics)]
    public static partial void CGDisplayModeRelease(nint mode);

    [LibraryImport(CoreGraphics)]
    public static partial nint CGWindowListCopyWindowInfo(uint option, uint relativeToWindow);

    [LibraryImport(CoreGraphics)]
    public static partial byte CGPreflightScreenCaptureAccess();

    [LibraryImport(CoreGraphics)]
    public static partial byte CGRequestScreenCaptureAccess();

    [LibraryImport(CoreGraphics)]
    public static partial nint CGBitmapContextCreate(void* data, nuint width, nuint height, nuint bitsPerComponent, nuint bytesPerRow, nint space, uint bitmapInfo);

    [LibraryImport(CoreGraphics)]
    public static partial void CGContextRelease(nint context);

    [LibraryImport(CoreGraphics)]
    public static partial void CGContextSetInterpolationQuality(nint context, int quality);

    [LibraryImport(CoreGraphics)]
    public static partial byte CGRectMakeWithDictionaryRepresentation(nint dictionary, CGRect* rect);

    // ---- CoreFoundation ----

    [LibraryImport(CoreFoundation)]
    public static partial nint CFArrayGetCount(nint array);

    [LibraryImport(CoreFoundation)]
    public static partial nint CFArrayGetValueAtIndex(nint array, nint index);

    [LibraryImport(CoreFoundation)]
    public static partial nint CFDictionaryGetValue(nint dictionary, nint key);

    [LibraryImport(CoreFoundation)]
    private static partial byte CFNumberGetValue(nint number, int type, void* value);

    [LibraryImport(CoreFoundation)]
    private static partial byte CFBooleanGetValue(nint boolean);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFStringGetLength(nint text);

    [LibraryImport(CoreFoundation)]
    private static partial void CFStringGetCharacters(nint text, CFRange range, char* buffer);

    [LibraryImport(CoreFoundation)]
    public static partial nint CFRetain(nint value);

    // ---- Objective-C shapes AppKitNative has no import for ----

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial float SendFloat(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    public static partial uint SendUInt(nint receiver, nint selector);

    [LibraryImport(ObjC)]
    public static partial nint class_getClassMethod(nint cls, nint selector);

    [LibraryImport(ObjC)]
    public static partial nint objc_lookUpClass(byte* name);

    /// <summary>A class by name, or 0 when it is not there (ScreenCaptureKit loaded first; macOS 13 has no <c>SCScreenshotManager</c>).</summary>
    public static nint TryClass(string name)
    {
        _ = NativeLibrary.TryLoad(ScreenCaptureKitPath, out _);
        int length = System.Text.Encoding.UTF8.GetByteCount(name);
        byte* bytes = stackalloc byte[length + 1];
        System.Text.Encoding.UTF8.GetBytes(name, new Span<byte>(bytes, length));
        bytes[length] = 0;
        return objc_lookUpClass(bytes);
    }

    // ---- The window list's dictionaries ----

    /// <summary>A CoreGraphics key (<c>kCGWindowNumber</c>…), read once from its exported constant.</summary>
    public static nint Key(string name)
    {
        lock (s_keys)
        {
            if (!s_keys.TryGetValue(name, out nint key))
            {
                key = Marshal.ReadIntPtr(NativeLibrary.GetExport(NativeLibrary.Load(CoreGraphics), name));
                s_keys[name] = key;
            }

            return key;
        }
    }

    public static long Long(nint dictionary, string key)
    {
        nint value = CFDictionaryGetValue(dictionary, Key(key));
        long result = 0;
        return value != 0 && CFNumberGetValue(value, NumberSInt64, &result) != 0 ? result : 0;
    }

    public static double Double(nint dictionary, string key, double fallback)
    {
        nint value = CFDictionaryGetValue(dictionary, Key(key));
        double result = 0;
        return value != 0 && CFNumberGetValue(value, NumberFloat64, &result) != 0 ? result : fallback;
    }

    public static bool Bool(nint dictionary, string key)
    {
        nint value = CFDictionaryGetValue(dictionary, Key(key));
        return value != 0 && CFBooleanGetValue(value) != 0;
    }

    /// <summary>A CFString value's characters; "" when the key is missing (a window's title without the permission).</summary>
    public static string Text(nint dictionary, string key)
    {
        nint value = CFDictionaryGetValue(dictionary, Key(key));
        if (value == 0)
        {
            return "";
        }

        int length = (int)CFStringGetLength(value);
        if (length <= 0)
        {
            return "";
        }

        var chars = new char[length];
        fixed (char* p = chars)
        {
            CFStringGetCharacters(value, new CFRange { Location = 0, Length = length }, p);
        }

        return new string(chars);
    }

    public static CGRect Rect(nint dictionary, string key)
    {
        nint value = CFDictionaryGetValue(dictionary, Key(key));
        CGRect rect = default;
        return value != 0 && CGRectMakeWithDictionaryRepresentation(value, &rect) != 0 ? rect : default;
    }

    // ---- Pixels ----

    /// <summary>
    /// A canvas of tightly packed BGRX rows in sRGB with <paramref name="draw"/>'s images drawn on it (CoreGraphics converts the
    /// display's colour profile and scales; the canvas is y-up, so <paramref name="draw"/> gets the canvas height to flip by).
    /// </summary>
    public static byte[] Canvas(int width, int height, Action<nint, int> draw)
    {
        ArgumentNullException.ThrowIfNull(draw);
        if (s_srgb == 0)
        {
            nint name = Marshal.ReadIntPtr(NativeLibrary.GetExport(NativeLibrary.Load(CoreGraphics), "kCGColorSpaceSRGB"));
            s_srgb = CGColorSpaceCreateWithName(name);   // kept for the process
        }

        var pixels = new byte[(long)width * height * 4];
        fixed (byte* p = pixels)
        {
            nint context = CGBitmapContextCreate(p, (nuint)width, (nuint)height, 8, (nuint)(width * 4), s_srgb, Bgrx);
            if (context == 0)
            {
                throw new ScreenException(ScreenText.Failed("CGBitmapContextCreate"));
            }

            try
            {
                CGContextSetInterpolationQuality(context, InterpolationHigh);
                draw(context, height);
            }
            finally
            {
                CGContextRelease(context);
            }
        }

        return pixels;
    }

    // ---- Completion blocks ----

    /// <summary>
    /// Runs <paramref name="start"/> with a fresh completion block for a <c>(id result, NSError *error)</c> handler and waits up to
    /// <paramref name="timeout"/> for it. The result comes back retained (the caller releases it); a call that times out is
    /// abandoned, and a late completion drops what it brings.
    /// </summary>
    public static Completed Await(Action<nint> start, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(start);
        var completion = new Completion();
        var state = GCHandle.Alloc(completion);
        nint block = NewBlock(GCHandle.ToIntPtr(state));
        try
        {
            start(block);
        }
        catch
        {
            // Never handed over: nothing will call it.
            state.Free();
            Spent(block);
            throw;
        }

        bool done = completion.Done.Wait(timeout);
        lock (completion.Gate)
        {
            if (!done && !completion.Done.IsSet)
            {
                completion.Abandoned = true;
                return new Completed(0, null, 0, TimedOut: true);
            }

            return new Completed(completion.Result, completion.Error, completion.Code, TimedOut: false);
        }
    }

    private static nint NewBlock(nint state)
    {
        BlockLiteral* block = null;
        lock (s_spentGate)
        {
            if (s_spent.TryPeek(out var spent) && Environment.TickCount64 - spent.At > (long)Reuse.TotalMilliseconds)
            {
                block = (BlockLiteral*)s_spent.Dequeue().Block;
            }
        }

        if (block is null)
        {
            block = (BlockLiteral*)NativeMemory.AllocZeroed((nuint)sizeof(BlockLiteral));
        }

        if (s_globalBlockClass == 0)
        {
            s_globalBlockClass = NativeLibrary.GetExport(NativeLibrary.Load(LibSystem), "_NSConcreteGlobalBlock");
        }

        block->Isa = s_globalBlockClass;
        block->Flags = BlockIsGlobal;
        block->Reserved = 0;
        block->Invoke = (nint)(delegate* unmanaged<BlockLiteral*, nint, nint, void>)&Invoked;
        block->OwnDescriptor = new BlockDescriptor { Reserved = 0, Size = (nuint)sizeof(BlockLiteral) };
        block->Descriptor = &block->OwnDescriptor;
        block->State = state;
        return (nint)block;
    }

    private static void Spent(nint block)
    {
        lock (s_spentGate)
        {
            s_spent.Enqueue((block, Environment.TickCount64));
        }
    }

    [UnmanagedCallersOnly]
    private static void Invoked(BlockLiteral* block, nint result, nint error)
    {
        try
        {
            var state = GCHandle.FromIntPtr(block->State);
            var completion = (Completion)state.Target!;
            state.Free();
            Spent((nint)block);
            lock (completion.Gate)
            {
                if (completion.Abandoned)
                {
                    return;
                }

                completion.Result = result != 0 ? CFRetain(result) : 0;
                if (error != 0)
                {
                    completion.Error = FromNSString(Send(error, Sel("localizedDescription")));
                    completion.Code = SendLong(error, Sel("code"));
                }

                completion.Done.Set();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Nothing may leave an unmanaged callback; the waiting call times out instead.
            Diagnostics.DiagnosticLog.Warn(ScreenText.Category, "A ScreenCaptureKit completion failed: " + ex.Message);
        }
    }
}
