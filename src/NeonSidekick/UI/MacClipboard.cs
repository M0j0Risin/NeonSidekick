using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NeonSidekick.Diagnostics;
using NeonSidekick.Images;
using static NeonSidekick.Viewer.AppKitNative;

namespace NeonSidekick.UI;

/// <summary>
/// The clipboard on macOS: the general pasteboard (<c>NSPasteboard</c>) through the app's Objective-C interop, with
/// <see cref="WindowsClipboard"/>'s three doors and their contract — nothing throws, a failure reads as null and writes as false.
/// Pictures since 2026-10-07 (the user's ask: a copied picture never reached the line on a Mac), and text the same day moved here
/// from <c>/usr/bin/pbpaste</c> and <c>pbcopy</c> (2026-10-06's process-start site, the user's call to retire it now that the
/// interop is in), so the clipboard starts no process.
///
/// <para>What is read (<see cref="PasteboardPick"/>): files Finder copied, as their paths (a drop's paste, so a picture among them is
/// attached by name, never Finder's icon of it); else <c>public.png</c> as it is (a screenshot, a browser); else <c>public.tiff</c>
/// (Preview, most AppKit apps), its largest frame made a PNG through ImageIO, as Windows makes a BMP of a bitmap; else the text.
/// Cmd+V is the terminal's own paste (text only; an image pastes nothing), so this is the line's paste: Ctrl+V, Option+V (with
/// Option as Meta) and the right click.</para>
///
/// <para>Each call runs on a pool thread inside its own autorelease pool, and is waited for at most <see cref="Timeout"/>, the
/// bound pbcopy and pbpaste had: a stalled pasteboard server costs the paste, not the input thread. AppKit.framework is loaded on
/// the first call (the pasteboard is AppKit's; no NSApp is made and nothing runs on the main thread — pbpaste is the same calls on
/// its own thread). Over SSH the pasteboard server of the user's login session answers, as it did pbpaste.</para>
/// </summary>
[SupportedOSPlatform("macos")]
public static unsafe partial class MacClipboard
{
    public const string Category = "Screen";

    /// <summary>How long a pasteboard call may take before the clipboard counts as busy.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    private static readonly Lazy<bool> s_loaded = new(() =>
    {
        NativeLibrary.Load(AppKitPath);
        return true;
    });

    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static partial byte SendBoolPair(nint receiver, nint selector, nint a, nint b);

    /// <summary>The clipboard's text, or null for none (empty, not text, or the pasteboard did not answer). Copied files read as their paths, one a line.</summary>
    public static string? TryReadText() => Bounded(() =>
    {
        nint pasteboard = General();
        if (PasteboardPick.FilePaths(FileUrls(pasteboard)) is { } paths)
        {
            return paths;
        }

        string? text = FromNSString(Send(pasteboard, Sel("stringForType:"), NSString(PasteboardPick.TextType)));
        return text is { Length: > 0 } ? text : null;
    }, null, "read the text");

    /// <summary>Puts <paramref name="text"/> on the clipboard; false when the pasteboard did not take it.</summary>
    public static bool TrySetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Bounded(() =>
        {
            nint pasteboard = General();
            SendLong(pasteboard, Sel("clearContents"));
            return SendBoolPair(pasteboard, Sel("setString:forType:"), NSString(text), NSString(PasteboardPick.TextType)) != 0;
        }, false, "set the text");
    }

    /// <summary>
    /// The picture on the clipboard as the bytes of an image file (a PNG), or null when there is none, when files were copied (their
    /// paths are the text), or when the pasteboard did not answer. No size cap here: <see cref="Files.ImageFile"/> judges the bytes.
    /// </summary>
    public static byte[]? TryReadImage() => Bounded(() =>
    {
        nint pasteboard = General();
        if (PasteboardPick.FilePaths(FileUrls(pasteboard)) is not null)
        {
            return null;
        }

        nint png = Send(pasteboard, Sel("dataForType:"), NSString(PasteboardPick.PngType));
        if (png != 0)
        {
            return Bytes(png);
        }

        nint tiff = Send(pasteboard, Sel("dataForType:"), NSString(PasteboardPick.TiffType));
        return tiff == 0 ? null : PngFromImageData(tiff);   // NSData is toll-free bridged to CFData
    }, null, "read a picture");

    /// <summary>
    /// The largest frame of an image file's bytes (a TIFF, as a rule) re-encoded as a PNG through ImageIO; null when ImageIO cannot read
    /// it. For the tests and the smoke, which make a TIFF of their own.
    /// </summary>
    internal static byte[]? PngFromImage(byte[] file)
    {
        ArgumentNullException.ThrowIfNull(file);
        fixed (byte* p = file)
        {
            nint data = ImageIONative.CFDataCreate(0, p, file.Length);
            try
            {
                return PngFromImageData(data);
            }
            finally
            {
                ImageIONative.CFRelease(data);
            }
        }
    }

    // The largest frame (PasteboardPick.Largest) of a CFData's image as a PNG's bytes; null when it is no image ImageIO reads.
    private static byte[]? PngFromImageData(nint data)
    {
        nint source = ImageIONative.CGImageSourceCreateWithData(data, 0);
        if (source == 0)
        {
            return null;
        }

        nint image = 0, output = 0, type = 0, destination = 0;
        try
        {
            var frames = new List<(int, int)>();
            nint widthKey = ImageIONative.ImageIOKey("kCGImagePropertyPixelWidth");
            nint heightKey = ImageIONative.ImageIOKey("kCGImagePropertyPixelHeight");
            for (nint i = 0, count = ImageIONative.CGImageSourceGetCount(source); i < count; i++)
            {
                nint properties = ImageIONative.CGImageSourceCopyPropertiesAtIndex(source, i, 0);
                frames.Add(properties == 0 ? (0, 0)
                    : (ImageIONative.Int(ImageIONative.CFDictionaryGetValue(properties, widthKey)) ?? 0, ImageIONative.Int(ImageIONative.CFDictionaryGetValue(properties, heightKey)) ?? 0));
                if (properties != 0)
                {
                    ImageIONative.CFRelease(properties);
                }
            }

            int best = PasteboardPick.Largest(frames);
            image = best < 0 ? 0 : ImageIONative.CGImageSourceCreateImageAtIndex(source, best, 0);
            if (image == 0)
            {
                return null;
            }

            output = ImageIONative.CFDataCreateMutable(0, 0);
            type = ImageIONative.CreateString(PasteboardPick.PngType);
            destination = ImageIONative.CGImageDestinationCreateWithData(output, type, 1, 0);
            if (destination == 0)
            {
                return null;
            }

            ImageIONative.CGImageDestinationAddImage(destination, image, 0);
            if (ImageIONative.CGImageDestinationFinalize(destination) == 0)
            {
                return null;
            }

            return new ReadOnlySpan<byte>(ImageIONative.CFDataGetBytePtr(output), (int)ImageIONative.CFDataGetLength(output)).ToArray();
        }
        finally
        {
            foreach (nint cf in (ReadOnlySpan<nint>)[destination, type, output])
            {
                if (cf != 0)
                {
                    ImageIONative.CFRelease(cf);
                }
            }

            if (image != 0)
            {
                ImageIONative.CGImageRelease(image);
            }

            ImageIONative.CFRelease(source);
        }
    }

    /// <summary>
    /// A TIFF of grey frames at <paramref name="sizes"/>, in that order, through ImageIO (the tests' and the smoke's stand-in for
    /// Preview's several-size copy); null when ImageIO would not write it.
    /// </summary>
    internal static byte[]? TiffOf(params (int Width, int Height)[] sizes)
    {
        ArgumentNullException.ThrowIfNull(sizes);
        nint output = ImageIONative.CFDataCreateMutable(0, 0);
        nint type = ImageIONative.CreateString("public.tiff");
        nint destination = ImageIONative.CGImageDestinationCreateWithData(output, type, sizes.Length, 0);
        nint space = ImageIONative.CGColorSpaceCreateWithName(ImageIONative.SrgbName);
        try
        {
            if (destination == 0)
            {
                return null;
            }

            foreach (var (width, height) in sizes)
            {
                byte[] pixels = new byte[width * height * 4];
                Array.Fill(pixels, (byte)0x80);
                fixed (byte* p = pixels)
                {
                    nint data = ImageIONative.CFDataCreate(0, p, pixels.Length);
                    nint provider = ImageIONative.CGDataProviderCreateWithCFData(data);
                    nint image = ImageIONative.CGImageCreate((nuint)width, (nuint)height, 8, 32, (nuint)(width * 4), space, BitmapBgrx, provider, null, 1, 0);
                    ImageIONative.CGImageDestinationAddImage(destination, image, 0);
                    ImageIONative.CGImageRelease(image);
                    ImageIONative.CGDataProviderRelease(provider);
                    ImageIONative.CFRelease(data);
                }
            }

            return ImageIONative.CGImageDestinationFinalize(destination) == 0 ? null
                : new ReadOnlySpan<byte>(ImageIONative.CFDataGetBytePtr(output), (int)ImageIONative.CFDataGetLength(output)).ToArray();
        }
        finally
        {
            ImageIONative.CGColorSpaceRelease(space);
            if (destination != 0)
            {
                ImageIONative.CFRelease(destination);
            }

            ImageIONative.CFRelease(type);
            ImageIONative.CFRelease(output);
        }
    }

    /// <summary>The pasteboard's change count (the smoke's proof it answered, reading nothing of the user's); null when it did not.</summary>
    internal static long? ChangeCount() => Bounded<long?>(() => SendLong(General(), Sel("changeCount")), null, "read the change count");

    private static nint General()
    {
        _ = s_loaded.Value;
        return Send(Class("NSPasteboard"), Sel("generalPasteboard"));
    }

    // Each item's file URL, as Finder writes one per copied file.
    private static IEnumerable<string?> FileUrls(nint pasteboard)
    {
        nint items = Send(pasteboard, Sel("pasteboardItems"));
        ulong count = items == 0 ? 0 : SendULong(items, Sel("count"));
        var urls = new List<string?>();
        for (ulong i = 0; i < count; i++)
        {
            nint item = SendIndex(items, Sel("objectAtIndex:"), (nuint)i);
            urls.Add(FromNSString(Send(item, Sel("stringForType:"), NSString(PasteboardPick.FileUrlType))));
        }

        return urls;
    }

    private static byte[] Bytes(nint data)
    {
        ulong length = SendULong(data, Sel("length"));
        nint bytes = Send(data, Sel("bytes"));
        return bytes == 0 || length == 0 ? [] : new ReadOnlySpan<byte>((void*)bytes, checked((int)length)).ToArray();
    }

    // The call on a pool thread in an autorelease pool, waited for at most Timeout; the fallback when it throws or takes longer.
    private static T Bounded<T>(Func<T> call, T fallback, string what)
    {
        var run = Task.Run(() =>
        {
            nint pool = objc_autoreleasePoolPush();
            try
            {
                return call();
            }
            finally
            {
                objc_autoreleasePoolPop(pool);
            }
        });

        try
        {
            if (run.Wait(Timeout))
            {
                return run.Result;
            }

            DiagnosticLog.Debug(Category, "The pasteboard did not answer within 2 s (" + what + ").");
            return fallback;
        }
        catch (AggregateException ex) when (ex.InnerException is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException or OverflowException)
        {
            DiagnosticLog.Debug(Category, $"The pasteboard could not {what}: {ex.InnerException.Message}");
            return fallback;
        }
    }
}
