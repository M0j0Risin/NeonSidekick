using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NeonSidekick.Viewer;

namespace NeonSidekick.Printing;

/// <summary>
/// CoreGraphics' PDF context, CoreText and the few CoreFoundation calls <see cref="MacPdfDocument"/> draws a print job with
/// (2026-10-08, printing on a Mac). The CF basics already bound for pictures (<c>Images.ImageIONative</c>) are used from there;
/// these are the rest. A <see cref="CGRect"/> (four doubles) goes by value, as AppKit's calls already pass it; the exported
/// constants (<c>kCTFontAttributeName</c> and the rest) are read through <see cref="NativeLibrary.GetExport"/>, so a key is what
/// the system says it is. System frameworks only: nothing ships beside the exe.
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe partial class MacPdfNative
{
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreText = "/System/Library/Frameworks/CoreText.framework/CoreText";

    /// <summary><c>kCGInterpolationHigh</c>.</summary>
    public const int InterpolationHigh = 3;

    /// <summary><c>kCGPDFMediaBox</c>.</summary>
    public const int MediaBox = 0;

    // ---- CoreFoundation ----

    [LibraryImport(CoreFoundation)]
    public static partial nint CFStringCreateWithCharacters(nint allocator, char* characters, nint length);

    [LibraryImport(CoreFoundation)]
    public static partial nint CFAttributedStringCreate(nint allocator, nint text, nint attributes);

    [LibraryImport(CoreFoundation)]
    public static partial nint CFDictionaryCreate(nint allocator, nint* keys, nint* values, nint count, nint keyCallBacks, nint valueCallBacks);

    [LibraryImport(CoreFoundation)]
    public static partial nint CFLocaleCopyCurrent();

    [LibraryImport(CoreFoundation)]
    public static partial nint CFLocaleGetValue(nint locale, nint key);

    // ---- CoreGraphics ----

    [LibraryImport(CoreGraphics)]
    public static partial nint CGDataConsumerCreateWithCFData(nint data);

    [LibraryImport(CoreGraphics)]
    public static partial void CGDataConsumerRelease(nint consumer);

    [LibraryImport(CoreGraphics)]
    public static partial nint CGPDFContextCreate(nint consumer, CGRect* mediaBox, nint auxiliaryInfo);

    [LibraryImport(CoreGraphics)]
    public static partial void CGPDFContextClose(nint context);

    [LibraryImport(CoreGraphics)]
    public static partial void CGContextBeginPage(nint context, CGRect* mediaBox);

    [LibraryImport(CoreGraphics)]
    public static partial void CGContextEndPage(nint context);

    [LibraryImport(CoreGraphics)]
    public static partial void CGContextRelease(nint context);

    [LibraryImport(CoreGraphics)]
    public static partial void CGContextSaveGState(nint context);

    [LibraryImport(CoreGraphics)]
    public static partial void CGContextRestoreGState(nint context);

    [LibraryImport(CoreGraphics)]
    public static partial void CGContextTranslateCTM(nint context, double tx, double ty);

    [LibraryImport(CoreGraphics)]
    public static partial void CGContextRotateCTM(nint context, double angle);

    [LibraryImport(CoreGraphics)]
    public static partial void CGContextSetGrayFillColor(nint context, double gray, double alpha);

    [LibraryImport(CoreGraphics)]
    public static partial void CGContextFillRect(nint context, CGRect rect);

    [LibraryImport(CoreGraphics)]
    public static partial void CGContextSetTextPosition(nint context, double x, double y);

    [LibraryImport(CoreGraphics)]
    public static partial void CGContextSetInterpolationQuality(nint context, int quality);

    [LibraryImport(CoreGraphics)]
    public static partial void CGContextDrawImage(nint context, CGRect rect, nint image);

    [LibraryImport(CoreGraphics)]
    public static partial nint CGColorSpaceCreateDeviceRGB();

    [LibraryImport(CoreGraphics, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint CGDataProviderCreateWithFilename(string path);

    [LibraryImport(CoreGraphics)]
    public static partial nint CGPDFDocumentCreateWithProvider(nint provider);

    [LibraryImport(CoreGraphics)]
    public static partial nuint CGPDFDocumentGetNumberOfPages(nint document);

    [LibraryImport(CoreGraphics)]
    public static partial byte CGPDFDocumentIsUnlocked(nint document);

    [LibraryImport(CoreGraphics)]
    public static partial nint CGPDFDocumentGetPage(nint document, nuint pageNumber);

    [LibraryImport(CoreGraphics)]
    public static partial CGRect CGPDFPageGetBoxRect(nint page, int box);

    [LibraryImport(CoreGraphics)]
    public static partial void CGPDFDocumentRelease(nint document);

    // ---- CoreText ----

    [LibraryImport(CoreText)]
    public static partial nint CTFontCreateWithName(nint name, double size, void* matrix);

    [LibraryImport(CoreText)]
    public static partial nint CTFontCopyPostScriptName(nint font);

    [LibraryImport(CoreText)]
    public static partial nint CTLineCreateWithAttributedString(nint text);

    [LibraryImport(CoreText)]
    public static partial double CTLineGetTypographicBounds(nint line, double* ascent, double* descent, double* leading);

    [LibraryImport(CoreText)]
    public static partial void CTLineDraw(nint line, nint context);

    // ---- exported constants ----

    private static nint s_fontKey;
    private static nint s_fromContextKey;
    private static nint s_true;
    private static nint s_keyCallBacks;
    private static nint s_valueCallBacks;
    private static nint s_countryKey;

    /// <summary><c>kCTFontAttributeName</c>.</summary>
    public static nint FontAttribute => Constant(ref s_fontKey, CoreText, "kCTFontAttributeName");

    /// <summary><c>kCTForegroundColorFromContextAttributeName</c>: the text takes the context's fill, black.</summary>
    public static nint ColorFromContextAttribute => Constant(ref s_fromContextKey, CoreText, "kCTForegroundColorFromContextAttributeName");

    /// <summary><c>kCFBooleanTrue</c>.</summary>
    public static nint True => Constant(ref s_true, CoreFoundation, "kCFBooleanTrue");

    /// <summary><c>kCFLocaleCountryCode</c>.</summary>
    public static nint CountryKey => Constant(ref s_countryKey, CoreFoundation, "kCFLocaleCountryCode");

    /// <summary>The address of <c>kCFTypeDictionaryKeyCallBacks</c> (a struct, not a pointer to one).</summary>
    public static nint KeyCallBacks => s_keyCallBacks != 0 ? s_keyCallBacks : s_keyCallBacks = NativeLibrary.GetExport(NativeLibrary.Load(CoreFoundation), "kCFTypeDictionaryKeyCallBacks");

    /// <summary>The address of <c>kCFTypeDictionaryValueCallBacks</c>.</summary>
    public static nint ValueCallBacks => s_valueCallBacks != 0 ? s_valueCallBacks : s_valueCallBacks = NativeLibrary.GetExport(NativeLibrary.Load(CoreFoundation), "kCFTypeDictionaryValueCallBacks");

    private static nint Constant(ref nint cache, string library, string name)
    {
        if (cache == 0)
        {
            cache = *(nint*)NativeLibrary.GetExport(NativeLibrary.Load(library), name);
        }

        return cache;
    }

    /// <summary>A CFString the caller releases, with <paramref name="text"/>'s UTF-16 as it is.</summary>
    public static nint CFString(string text)
    {
        fixed (char* chars = text)
        {
            return CFStringCreateWithCharacters(0, chars, text.Length);
        }
    }
}
