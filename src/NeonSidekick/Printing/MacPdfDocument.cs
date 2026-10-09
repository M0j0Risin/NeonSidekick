using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NeonSidekick.Images;
using NeonSidekick.Viewer;
using static NeonSidekick.Printing.MacPdfNative;

namespace NeonSidekick.Printing;

/// <summary>
/// A Mac's print surface and its PDF (2026-10-08, printing on a Mac): what GDI's device context is on Windows. It measures with
/// CoreText in the faces <see cref="MacPrintRules.FontName"/> picks, at the sizes <see cref="PrintLayout"/> sets, and draws
/// the same ops into a CoreGraphics PDF context in memory, one PDF page per sheet at the paper's own size: text by its
/// baseline (<c>CTLineDraw</c>, black from the context, CoreText's own font fallback for what Helvetica Neue and Menlo lack),
/// boxes filled black, pictures as CGImages over their BGRX bytes with high-quality interpolation. A landscape job is turned onto
/// the portrait paper here (<see cref="MacPrintRules.Turn"/>). The bytes go to CUPS as <c>application/pdf</c>, or to a file.
/// Measured and drawn by the same <c>CTFont</c>, so the layout's line breaks are what lands. Proven by the smoke's
/// <c>print:cups</c>, the Mac facts that read the PDF back, and by hand on paper; excluded from coverage.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed unsafe class MacPdfDocument : IPrintSurface
{
    private readonly Dictionary<PrintFont, nint> _attributes = new();
    private readonly List<nint> _fonts = [];

    public MacPdfDocument(MacPaper paper, bool landscape)
    {
        Paper = paper ?? throw new ArgumentNullException(nameof(paper));
        Landscape = landscape;
        Page = MacPrintRules.Metrics(paper, landscape);
    }

    public MacPaper Paper { get; }

    public bool Landscape { get; }

    public PageMetrics Page { get; }

    /// <summary>The attributes a font's text is made with: the CTFont, and the context's fill as its colour. Made once each.</summary>
    private nint Attributes(PrintFont font)
    {
        if (_attributes.TryGetValue(font, out nint attributes))
        {
            return attributes;
        }

        nint name = CFString(MacPrintRules.FontName(font));
        nint ctFont = CTFontCreateWithName(name, font.Size, null);
        ImageIONative.CFRelease(name);
        _fonts.Add(ctFont);
        nint* keys = stackalloc nint[2] { FontAttribute, ColorFromContextAttribute };
        nint* values = stackalloc nint[2] { ctFont, True };
        attributes = CFDictionaryCreate(0, keys, values, 2, KeyCallBacks, ValueCallBacks);
        _attributes[font] = attributes;
        return attributes;
    }

    /// <summary>A line of <paramref name="text"/> in <paramref name="font"/>, the caller releasing it.</summary>
    private nint Line(string text, PrintFont font)
    {
        nint chars = CFString(text);
        nint attributed = CFAttributedStringCreate(0, chars, Attributes(font));
        ImageIONative.CFRelease(chars);
        nint line = CTLineCreateWithAttributedString(attributed);
        ImageIONative.CFRelease(attributed);
        return line;
    }

    public double Width(string text, PrintFont font)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(font);
        if (text.Length == 0)
        {
            return 0;
        }

        nint line = Line(text, font);
        if (line == 0)
        {
            // No line: an average advance, never zero, so the layout still wraps (the Windows surface's fallback).
            return text.Length * font.Size * 0.55;
        }

        double width = CTLineGetTypographicBounds(line, null, null, null);
        ImageIONative.CFRelease(line);
        return width;
    }

    /// <summary>
    /// The PDF of <paramref name="pages"/>, <paramref name="copies"/> times over, collated (pages repeated, as Windows does). The
    /// token is looked at before every sheet: a cancel throws and nothing is kept.
    /// </summary>
    public byte[] Render(IReadOnlyList<PrintPage> pages, int copies, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pages);
        nint data = ImageIONative.CFDataCreateMutable(0, 0);
        nint consumer = CGDataConsumerCreateWithCFData(data);
        var media = new CGRect(0, 0, Paper.Width, Paper.Height);
        nint context = CGPDFContextCreate(consumer, &media, 0);
        CGDataConsumerRelease(consumer);
        if (context == 0)
        {
            ImageIONative.CFRelease(data);
            throw new InvalidOperationException("CoreGraphics made no PDF context");
        }

        try
        {
            var (translate, angle) = MacPrintRules.Turn(Paper, Landscape);
            for (int copy = 0; copy < Math.Max(1, copies); copy++)
            {
                foreach (var page in pages)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    CGContextBeginPage(context, &media);
                    CGContextSaveGState(context);
                    if (Landscape)
                    {
                        CGContextTranslateCTM(context, translate, 0);
                        CGContextRotateCTM(context, angle);
                    }

                    CGContextSetGrayFillColor(context, 0, 1);
                    foreach (var op in page.Ops)
                    {
                        Draw(context, op);
                    }

                    CGContextRestoreGState(context);
                    CGContextEndPage(context);
                }
            }

            CGPDFContextClose(context);
            CGContextRelease(context);
            context = 0;
            int length = (int)ImageIONative.CFDataGetLength(data);
            var bytes = new byte[length];
            Marshal.Copy((nint)ImageIONative.CFDataGetBytePtr(data), bytes, 0, length);
            return bytes;
        }
        finally
        {
            if (context != 0)
            {
                CGPDFContextClose(context);
                CGContextRelease(context);
            }

            ImageIONative.CFRelease(data);
        }
    }

    private void Draw(nint context, PrintOp op)
    {
        switch (op)
        {
            case PrintTextOp text when text.Text.Length > 0:
            {
                nint line = Line(text.Text, text.Font);
                if (line != 0)
                {
                    var (x, y) = MacPrintRules.Baseline(text.X, text.Y, Page);
                    CGContextSetTextPosition(context, x, y);
                    CTLineDraw(line, context);
                    ImageIONative.CFRelease(line);
                }

                break;
            }

            case PrintBoxOp box:
            {
                var (x, y, w, h) = MacPrintRules.Rect(box.X, box.Y, box.Width, box.Height, Page);
                CGContextFillRect(context, new CGRect(x, y, w, h));
                break;
            }

            case PrintImageOp image:
            {
                nint picture = Image(image.Bitmap);
                if (picture != 0)
                {
                    var (x, y, w, h) = MacPrintRules.Rect(image.X, image.Y, image.Width, image.Height, Page);
                    CGContextSetInterpolationQuality(context, InterpolationHigh);
                    CGContextDrawImage(context, new CGRect(x, y, w, h), picture);
                    ImageIONative.CGImageRelease(picture);
                }

                break;
            }
        }
    }

    /// <summary>A bitmap's BGRX bytes copied into a CGImage the caller releases (a copy: the PDF context may read it after the draw returns).</summary>
    private static nint Image(ViewerBitmap bitmap)
    {
        if (bitmap.Width <= 0 || bitmap.Height <= 0 || bitmap.Bgrx.Length < bitmap.Width * bitmap.Height * 4)
        {
            return 0;
        }

        nint data;
        fixed (byte* bytes = bitmap.Bgrx)
        {
            data = ImageIONative.CFDataCreate(0, bytes, bitmap.Width * bitmap.Height * 4);
        }

        nint provider = ImageIONative.CGDataProviderCreateWithCFData(data);
        ImageIONative.CFRelease(data);
        nint space = CGColorSpaceCreateDeviceRGB();
        nint image = ImageIONative.CGImageCreate((nuint)bitmap.Width, (nuint)bitmap.Height, 8, 32, (nuint)(bitmap.Width * 4), space, ImageIONative.Bgrx, provider, null, 1, 0);
        ImageIONative.CGColorSpaceRelease(space);
        ImageIONative.CGDataProviderRelease(provider);
        return image;
    }

    public void Dispose()
    {
        foreach (nint attributes in _attributes.Values)
        {
            ImageIONative.CFRelease(attributes);
        }

        foreach (nint font in _fonts)
        {
            ImageIONative.CFRelease(font);
        }

        _attributes.Clear();
        _fonts.Clear();
    }

    // ── reading PDFs back: a file's page count, and the smoke's and the tests' look at what was drawn ──

    /// <summary>The pages of the PDF at <paramref name="path"/>: 0 when it is not one CoreGraphics opens, or is locked by a password.</summary>
    public static int PageCount(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        nint provider = CGDataProviderCreateWithFilename(path);
        if (provider == 0)
        {
            return 0;
        }

        nint document = CGPDFDocumentCreateWithProvider(provider);
        ImageIONative.CGDataProviderRelease(provider);
        if (document == 0)
        {
            return 0;
        }

        try
        {
            return CGPDFDocumentIsUnlocked(document) == 0 ? 0 : (int)CGPDFDocumentGetNumberOfPages(document);
        }
        finally
        {
            CGPDFDocumentRelease(document);
        }
    }

    /// <summary>Each page's media box (width, height) of a PDF in memory: empty when it is not one.</summary>
    public static IReadOnlyList<(double Width, double Height)> MediaBoxes(byte[] pdf)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        nint data;
        fixed (byte* bytes = pdf)
        {
            data = ImageIONative.CFDataCreate(0, bytes, pdf.Length);
        }

        nint provider = ImageIONative.CGDataProviderCreateWithCFData(data);
        ImageIONative.CFRelease(data);
        nint document = CGPDFDocumentCreateWithProvider(provider);
        ImageIONative.CGDataProviderRelease(provider);
        if (document == 0)
        {
            return [];
        }

        try
        {
            var boxes = new List<(double, double)>();
            nuint count = CGPDFDocumentGetNumberOfPages(document);
            for (nuint i = 1; i <= count; i++)
            {
                var box = CGPDFPageGetBoxRect(CGPDFDocumentGetPage(document, i), MediaBox);
                boxes.Add((box.Width, box.Height));
            }

            return boxes;
        }
        finally
        {
            CGPDFDocumentRelease(document);
        }
    }

    /// <summary>The text PDFKit reads out of a PDF in memory, or null when it cannot: the proof that what was laid out was drawn.</summary>
    public static string? Text(byte[] pdf)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        if (!NativeLibrary.TryLoad("/System/Library/Frameworks/PDFKit.framework/PDFKit", out _))
        {
            return null;
        }

        nint pool = AppKitNative.objc_autoreleasePoolPush();
        try
        {
            nint data;
            fixed (byte* bytes = pdf)
            {
                data = AppKitNative.Send(AppKitNative.Class("NSData"), AppKitNative.Sel("dataWithBytes:length:"), (nint)bytes, pdf.Length);
            }

            nint document = AppKitNative.Send(AppKitNative.Send(AppKitNative.Class("PDFDocument"), AppKitNative.Sel("alloc")), AppKitNative.Sel("initWithData:"), data);
            if (document == 0)
            {
                return null;
            }

            string? text = AppKitNative.FromNSString(AppKitNative.Send(document, AppKitNative.Sel("string")));
            AppKitNative.SendVoid(document, AppKitNative.Sel("release"));
            return text;
        }
        finally
        {
            AppKitNative.objc_autoreleasePoolPop(pool);
        }
    }

    /// <summary>The PostScript name CoreText gives a font asked for by <paramref name="font"/>'s name: the tests' check that no fallback face stood in.</summary>
    public static string? ResolvedFontName(PrintFont font)
    {
        ArgumentNullException.ThrowIfNull(font);
        nint name = CFString(MacPrintRules.FontName(font));
        nint ctFont = CTFontCreateWithName(name, font.Size, null);
        ImageIONative.CFRelease(name);
        if (ctFont == 0)
        {
            return null;
        }

        nint resolved = CTFontCopyPostScriptName(ctFont);
        ImageIONative.CFRelease(ctFont);
        string? text = CFText(resolved);
        if (resolved != 0)
        {
            ImageIONative.CFRelease(resolved);
        }

        return text;
    }

    /// <summary>The Mac's region (<c>US</c>, <c>DE</c>…) from its current locale, for the fallback paper; null when it has none.</summary>
    public static string? Region()
    {
        nint locale = CFLocaleCopyCurrent();
        if (locale == 0)
        {
            return null;
        }

        try
        {
            return CFText(CFLocaleGetValue(locale, CountryKey));
        }
        finally
        {
            ImageIONative.CFRelease(locale);
        }
    }

    private static string? CFText(nint text)
    {
        if (text == 0)
        {
            return null;
        }

        byte* buffer = stackalloc byte[256];
        return ImageIONative.CFStringGetCString(text, buffer, 256, ImageIONative.Utf8) != 0 ? Marshal.PtrToStringUTF8((nint)buffer) : null;
    }
}
