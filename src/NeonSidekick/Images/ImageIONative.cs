using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NeonSidekick.Images;

/// <summary>
/// Apple's ImageIO, CoreGraphics, CoreFoundation and vImage as the macOS picture backend calls them (2026-10-07, for
/// <see cref="ImageIOCodecs"/>). Every argument and result is a pointer, an integer or a double, which arm64 passes in
/// registers as a plain C call does; the two structs vImage takes go by pointer; nothing is marshalled by value (the NAudio
/// scar). CoreFoundation objects are <c>nint</c>s, released by whoever got them from a Create or Copy call. The property-key
/// constants are exported data, read with <see cref="NativeLibrary.GetExport"/> rather than spelt out, so a key is what the
/// system says it is. All four are system frameworks: nothing ships beside the exe, and the smoke's <c>image:imageio</c> runs
/// them on the published binary.
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe partial class ImageIONative
{
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string ImageIO = "/System/Library/Frameworks/ImageIO.framework/ImageIO";
    private const string Accelerate = "/System/Library/Frameworks/Accelerate.framework/Accelerate";

    public const uint Utf8 = 0x08000100;          // kCFStringEncodingUTF8
    public const nint NumberSInt32 = 3;           // kCFNumberSInt32Type
    public const nint NumberFloat64 = 6;          // kCFNumberFloat64Type

    /// <summary><c>kCGImageAlphaFirst | kCGBitmapByteOrder32Little</c>: straight-alpha BGRA in memory, MagicScaler's <c>Bgra32bpp</c>.</summary>
    public const uint BgraStraight = 4 | (2 << 12);

    /// <summary><c>kCGImageAlphaNoneSkipFirst | kCGBitmapByteOrder32Little</c>: BGRX in memory, the X ignored.</summary>
    public const uint Bgrx = 6 | (2 << 12);

    /// <summary><c>kvImageNoAllocate</c>: vImage writes into the buffer it is given.</summary>
    public const uint NoAllocate = 512;

    /// <summary>vImage's <c>vImage_Buffer</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct VImageBuffer
    {
        public void* Data;
        public nuint Height;
        public nuint Width;
        public nuint RowBytes;
    }

    /// <summary>vImage's <c>vImage_CGImageFormat</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct VImageFormat
    {
        public uint BitsPerComponent;
        public uint BitsPerPixel;
        public nint ColorSpace;
        public uint BitmapInfo;
        public uint Version;
        public double* Decode;
        public int RenderingIntent;
    }

    // ---- CoreFoundation ----

    [LibraryImport(CoreFoundation)]
    public static partial void CFRelease(nint cf);

    [LibraryImport(CoreFoundation)]
    public static partial nint CFDataCreate(nint allocator, byte* bytes, nint length);

    [LibraryImport(CoreFoundation)]
    public static partial nint CFDataCreateMutable(nint allocator, nint capacity);

    [LibraryImport(CoreFoundation)]
    public static partial nint CFDataGetLength(nint data);

    [LibraryImport(CoreFoundation)]
    public static partial byte* CFDataGetBytePtr(nint data);

    [LibraryImport(CoreFoundation)]
    public static partial nint CFDictionaryCreateMutable(nint allocator, nint capacity, void* keyCallBacks, void* valueCallBacks);

    [LibraryImport(CoreFoundation)]
    public static partial void CFDictionarySetValue(nint dictionary, nint key, nint value);

    [LibraryImport(CoreFoundation)]
    public static partial nint CFDictionaryGetValue(nint dictionary, nint key);

    [LibraryImport(CoreFoundation)]
    public static partial nint CFNumberCreate(nint allocator, nint type, void* value);

    [LibraryImport(CoreFoundation)]
    public static partial byte CFNumberGetValue(nint number, nint type, void* value);

    [LibraryImport(CoreFoundation)]
    public static partial nuint CFGetTypeID(nint cf);

    [LibraryImport(CoreFoundation)]
    public static partial nuint CFNumberGetTypeID();

    [LibraryImport(CoreFoundation)]
    public static partial nuint CFDictionaryGetTypeID();

    [LibraryImport(CoreFoundation)]
    public static partial nint CFStringCreateWithCString(nint allocator, byte* text, uint encoding);

    [LibraryImport(CoreFoundation)]
    public static partial byte CFStringGetCString(nint text, byte* buffer, nint size, uint encoding);

    [LibraryImport(CoreFoundation)]
    public static partial nint CFArrayGetCount(nint array);

    [LibraryImport(CoreFoundation)]
    public static partial nint CFArrayGetValueAtIndex(nint array, nint index);

    // ---- CoreGraphics ----

    [LibraryImport(CoreGraphics)]
    public static partial nint CGColorSpaceCreateWithName(nint name);

    [LibraryImport(CoreGraphics)]
    public static partial void CGColorSpaceRelease(nint space);

    [LibraryImport(CoreGraphics)]
    public static partial nuint CGImageGetWidth(nint image);

    [LibraryImport(CoreGraphics)]
    public static partial nuint CGImageGetHeight(nint image);

    [LibraryImport(CoreGraphics)]
    public static partial uint CGImageGetAlphaInfo(nint image);

    [LibraryImport(CoreGraphics)]
    public static partial nint CGImageCreate(
        nuint width, nuint height, nuint bitsPerComponent, nuint bitsPerPixel, nuint bytesPerRow, nint space, uint bitmapInfo,
        nint provider, double* decode, byte shouldInterpolate, int intent);

    [LibraryImport(CoreGraphics)]
    public static partial void CGImageRelease(nint image);

    [LibraryImport(CoreGraphics)]
    public static partial nint CGDataProviderCreateWithCFData(nint data);

    [LibraryImport(CoreGraphics)]
    public static partial void CGDataProviderRelease(nint provider);

    // ---- ImageIO ----

    [LibraryImport(ImageIO)]
    public static partial nint CGImageSourceCreateWithData(nint data, nint options);

    [LibraryImport(ImageIO)]
    public static partial nint CGImageSourceGetType(nint source);

    [LibraryImport(ImageIO)]
    public static partial nint CGImageSourceGetCount(nint source);

    [LibraryImport(ImageIO)]
    public static partial nint CGImageSourceCopyPropertiesAtIndex(nint source, nint index, nint options);

    [LibraryImport(ImageIO)]
    public static partial nint CGImageSourceCreateImageAtIndex(nint source, nint index, nint options);

    [LibraryImport(ImageIO)]
    public static partial nint CGImageDestinationCopyTypeIdentifiers();

    [LibraryImport(ImageIO)]
    public static partial nint CGImageDestinationCreateWithData(nint data, nint type, nint count, nint options);

    [LibraryImport(ImageIO)]
    public static partial void CGImageDestinationAddImage(nint destination, nint image, nint properties);

    [LibraryImport(ImageIO)]
    public static partial byte CGImageDestinationFinalize(nint destination);

    // ---- vImage ----

    [LibraryImport(Accelerate)]
    public static partial nint vImageBuffer_InitWithCGImage(VImageBuffer* buffer, VImageFormat* format, double* background, nint image, uint flags);

    // ---- exported constants ----

    private static readonly Lazy<nint> CoreFoundationHandle = new(() => NativeLibrary.Load(CoreFoundation));
    private static readonly Lazy<nint> CoreGraphicsHandle = new(() => NativeLibrary.Load(CoreGraphics));
    private static readonly Lazy<nint> ImageIOHandle = new(() => NativeLibrary.Load(ImageIO));

    /// <summary><c>kCFTypeDictionaryKeyCallBacks</c>: its address, which is what CFDictionaryCreateMutable takes.</summary>
    public static void* KeyCallBacks => (void*)NativeLibrary.GetExport(CoreFoundationHandle.Value, "kCFTypeDictionaryKeyCallBacks");

    /// <summary><c>kCFTypeDictionaryValueCallBacks</c>'s address.</summary>
    public static void* ValueCallBacks => (void*)NativeLibrary.GetExport(CoreFoundationHandle.Value, "kCFTypeDictionaryValueCallBacks");

    /// <summary>An ImageIO property key (a <c>CFStringRef</c> constant) by its exported name; 0 when this macOS has no such export.</summary>
    public static nint ImageIOKey(string name) =>
        NativeLibrary.TryGetExport(ImageIOHandle.Value, name, out nint address) ? *(nint*)address : 0;

    /// <summary><c>kCGColorSpaceSRGB</c>.</summary>
    public static nint SrgbName => *(nint*)NativeLibrary.GetExport(CoreGraphicsHandle.Value, "kCGColorSpaceSRGB");

    // ---- small helpers over the calls above ----

    /// <summary>A CFString's text; empty for none.</summary>
    public static string Text(nint text)
    {
        if (text == 0)
        {
            return "";
        }

        byte* buffer = stackalloc byte[512];
        return CFStringGetCString(text, buffer, 512, Utf8) != 0 ? Marshal.PtrToStringUTF8((nint)buffer) ?? "" : "";
    }

    /// <summary>A new CFString (released by the caller).</summary>
    public static nint CreateString(string text)
    {
        byte[] utf8 = [.. System.Text.Encoding.UTF8.GetBytes(text), 0];
        fixed (byte* p = utf8)
        {
            return CFStringCreateWithCString(0, p, Utf8);
        }
    }

    /// <summary>A new CFNumber holding a double (released by the caller).</summary>
    public static nint CreateNumber(double value) => CFNumberCreate(0, NumberFloat64, &value);

    /// <summary>A CFNumber's value as an int; null when <paramref name="number"/> is no number.</summary>
    public static int? Int(nint number)
    {
        if (number == 0 || CFGetTypeID(number) != CFNumberGetTypeID())
        {
            return null;
        }

        int value;
        return CFNumberGetValue(number, NumberSInt32, &value) != 0 ? value : null;
    }

    /// <summary>A new, empty mutable CFDictionary holding CF objects (released by the caller).</summary>
    public static nint CreateDictionary() => CFDictionaryCreateMutable(0, 0, KeyCallBacks, ValueCallBacks);

    /// <summary>Whether <paramref name="cf"/> is a CFDictionary.</summary>
    public static bool IsDictionary(nint cf) => cf != 0 && CFGetTypeID(cf) == CFDictionaryGetTypeID();
}
