using System.Buffers.Binary;
using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The pure part of the clipboard: a <c>CF_DIB</c> block made a BMP. The Win32 reads and writes
/// touch the real clipboard and are checked in the field, never here.
/// </summary>
public class WindowsClipboardTests
{
    /// <summary>A CF_DIB block: the BMP fixture without its 14-byte file header.</summary>
    private static byte[] Dib(int width, int height) => SmokeChecks.SolidBmp(width, height)[WindowsClipboard.BmpFileHeaderLength..];

    private static byte[] Header(int length, ushort bitCount, int compression, int coloursUsed, int extra = 0)
    {
        var dib = new byte[length + extra];
        BinaryPrimitives.WriteInt32LittleEndian(dib, length);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), 1);
        BinaryPrimitives.WriteInt16LittleEndian(dib.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), bitCount);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(16), compression);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(32), coloursUsed);
        return dib;
    }

    private static int PixelOffset(byte[] bmp) => BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(10));

    [Fact]
    public void BmpFromDib_PutsTheFileHeaderInFront_AndTheCodecsReadIt()
    {
        byte[] original = SmokeChecks.SolidBmp(4, 3);

        byte[]? bmp = WindowsClipboard.BmpFromDib(Dib(4, 3));

        Assert.NotNull(bmp);
        Assert.Equal(original, bmp);
        Assert.Equal(54, PixelOffset(bmp));
        var image = ImageFile.Load(bmp, "clipboard-1.png", out string? error);
        Assert.Null(error);
        Assert.NotNull(image);
        Assert.Equal((4, 3), (image.Width, image.Height));
    }

    [Fact]
    public void BmpFromDib_CountsThePaletteAndTheMasks()
    {
        // 8 bpp with biClrUsed 0: every colour of the depth is in the table.
        byte[]? palette = WindowsClipboard.BmpFromDib(Header(40, 8, 0, 0, extra: 256 * 4 + 4));
        // 4 bpp naming 3 colours: only those.
        byte[]? named = WindowsClipboard.BmpFromDib(Header(40, 4, 0, 3, extra: 3 * 4 + 4));
        // 32 bpp BI_BITFIELDS on the 40-byte header: the three masks follow it.
        byte[]? masks = WindowsClipboard.BmpFromDib(Header(40, 32, 3, 0, extra: 12 + 4));
        // The same on a V5 header (124 bytes): the masks are inside it.
        byte[]? v5 = WindowsClipboard.BmpFromDib(Header(124, 32, 3, 0, extra: 4));

        Assert.Equal(14 + 40 + 256 * 4, PixelOffset(palette!));
        Assert.Equal(14 + 40 + 3 * 4, PixelOffset(named!));
        Assert.Equal(14 + 40 + 12, PixelOffset(masks!));
        Assert.Equal(14 + 124, PixelOffset(v5!));
        Assert.Equal((byte)'B', v5![0]);
        Assert.Equal((byte)'M', v5[1]);
        Assert.Equal(v5.Length, BinaryPrimitives.ReadInt32LittleEndian(v5.AsSpan(2)));
    }

    [Fact]
    public void BmpFromDib_RefusesWhatIsNotADib()
    {
        Assert.Null(WindowsClipboard.BmpFromDib(new byte[39]));
        Assert.Null(WindowsClipboard.BmpFromDib(Header(124, 32, 0, 0)[..40]));       // a header longer than the block
        Assert.Null(WindowsClipboard.BmpFromDib(Header(40, 8, 0, 0)));                 // a palette past the end
        Assert.Null(WindowsClipboard.BmpFromDib(Header(40, 24, 0, -1, extra: 4)));     // an impossible colour count
        Assert.Null(WindowsClipboard.BmpFromDib(Header(12, 24, 0, 0, extra: 40)));     // a core header is not a DIB here
    }

    [Fact]
    public void Constants_ArePinned()
    {
        Assert.Equal(14, WindowsClipboard.BmpFileHeaderLength);
        Assert.Equal(40, WindowsClipboard.DibInfoHeaderLength);
        Assert.Equal(8u, ConsoleInputNative.ClipboardDib);
        Assert.Equal("PNG", ConsoleInputNative.ClipboardPngFormatName);
    }
}
