using NeonSidekick.App;
using NeonSidekick.Files;
using PhotoSauce.MagicScaler;

namespace NeonSidekick.Tests;

public class ImageFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));

    public ImageFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Bmp(string name, int width, int height)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, SmokeChecks.SolidBmp(width, height));
        return path;
    }

    private static bool IsPng(byte[] bytes) => bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G';

    private static bool IsJpeg(byte[] bytes) => bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;

    /// <summary>A black 8-bit RGB PNG built by hand: one IDAT of deflated zeros, so a huge picture is a small file.</summary>
    internal static byte[] BlackPng(int width, int height)
    {
        using var stream = new MemoryStream();
        stream.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        byte[] ihdr = new byte[17];
        "IHDR"u8.CopyTo(ihdr);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(8), height);
        ihdr[12] = 8;   // bit depth
        ihdr[13] = 2;   // colour type: RGB
        Chunk(stream, ihdr);
        using (var idat = new MemoryStream())
        {
            idat.Write("IDAT"u8);
            using (var deflate = new System.IO.Compression.ZLibStream(idat, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            {
                byte[] row = new byte[1 + width * 3];   // filter byte 0, then black pixels
                for (int y = 0; y < height; y++)
                {
                    deflate.Write(row);
                }
            }

            Chunk(stream, idat.ToArray());
        }

        Chunk(stream, "IEND"u8.ToArray());
        return stream.ToArray();

        static void Chunk(Stream stream, byte[] typeAndData)
        {
            Span<byte> length = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, typeAndData.Length - 4);
            stream.Write(length);
            stream.Write(typeAndData);
            Span<byte> crc = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typeAndData));
            stream.Write(crc);
        }

        static uint Crc32(byte[] bytes)
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte b in bytes)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++)
                {
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
                }
            }

            return ~crc;
        }
    }

    [Theory]
    [InlineData(@"C:\pics\shot.png", true)]
    [InlineData(@"C:\pics\SHOT.PNG", true)]
    [InlineData(@"C:\pics\a.jpg", true)]
    [InlineData(@"C:\pics\a.jpeg", true)]
    [InlineData(@"C:\pics\a.gif", true)]
    [InlineData(@"C:\pics\a.webp", true)]
    [InlineData(@"C:\pics\a.bmp", true)]
    [InlineData(@"C:\pics\a.txt", false)]
    [InlineData(@"C:\pics\png", false)]
    [InlineData("", false)]
    public void IsImagePath_IsByExtensionOnly(string path, bool expected)
    {
        Assert.Equal(expected, ImageFile.IsImagePath(path));
    }

    [Fact]
    public void TryPastedPath_TakesOneExistingFullyQualifiedImagePath_QuotedOrNot()
    {
        string path = Bmp("a picture.bmp", 2, 2);

        Assert.True(ImageFile.TryPastedPath(path, out string found));
        Assert.Equal(path, found);
        Assert.True(ImageFile.TryPastedPath("  \"" + path + "\"  ", out found));
        Assert.Equal(path, found);
        Assert.True(ImageFile.TryPastedPath("\"" + path + "\"\n", out _));   // a trailing line break is trimmed like any other
    }

    [Fact]
    public void TryPastedPaths_TakesOnePerLine_OrARunOnALine_QuotedOrNot()
    {
        string a = Bmp("a shot.bmp", 4, 4);   // a space: the terminal quotes it
        string b = Bmp("b.bmp", 4, 4);

        Assert.True(ImageFile.TryPastedPaths("\"" + a + "\"\r\n" + b + "\r\n", out var paths));
        Assert.Equal([a, b], paths);
        Assert.True(ImageFile.TryPastedPaths("\"" + a + "\" " + b, out paths));          // one line, a run
        Assert.Equal([a, b], paths);
        Assert.True(ImageFile.TryPastedPaths(b + "  " + b + "\n\n" + b + "\n", out paths)); // blank lines skipped
        Assert.Equal(3, paths.Count);
        Assert.True(ImageFile.TryPastedPaths(a, out paths));                              // an unquoted path with a space is still one
        Assert.Equal([a], paths);
    }

    [Fact]
    public void TryPastedPaths_IsAllOrNothing()
    {
        string a = Bmp("a.bmp", 4, 4);
        string missing = Path.Combine(_dir, "gone.bmp");

        Assert.False(ImageFile.TryPastedPaths(a + "\n" + missing, out var paths));
        Assert.Empty(paths);
        Assert.False(ImageFile.TryPastedPaths(a + "\nand this one?", out _));
        Assert.False(ImageFile.TryPastedPaths("look at " + a, out _));
        Assert.False(ImageFile.TryPastedPaths("", out _));
        Assert.False(ImageFile.TryPastedPaths("\n \n", out _));
        Assert.Throws<ArgumentNullException>(() => ImageFile.TryPastedPaths(null!, out _));
    }

    [Fact]
    public void Tokens_SplitOnWhitespace_KeepingAQuotedStretchWhole()
    {
        Assert.Equal(["\"C:\\a b.png\"", "C:\\c.png"], ImageFile.Tokens("\"C:\\a b.png\"  C:\\c.png"));
        Assert.Equal(["\"open end"], ImageFile.Tokens("\"open end"));   // an unclosed quote runs to the end
        Assert.Empty(ImageFile.Tokens("   "));
    }

    [Fact]
    public void TryPastedPath_LeavesEverythingElseAsText()
    {
        string path = Bmp("a.bmp", 2, 2);
        string missing = Path.Combine(_dir, "nope.png");

        Assert.False(ImageFile.TryPastedPath("", out _));
        Assert.False(ImageFile.TryPastedPath(missing, out _));                         // must exist
        Assert.False(ImageFile.TryPastedPath("look at " + path, out _));               // a path among words
        Assert.False(ImageFile.TryPastedPath(path + "\n" + path, out _));              // two lines
        Assert.False(ImageFile.TryPastedPath("a.bmp", out _));                         // never resolved against the process directory
        Assert.False(ImageFile.TryPastedPath(_dir, out _));                            // a folder
        Assert.False(ImageFile.TryPastedPath("\"" + path, out _));                     // a lone quote
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "x");
        Assert.False(ImageFile.TryPastedPath(Path.Combine(_dir, "notes.txt"), out _)); // not an image extension
        Assert.Throws<ArgumentNullException>(() => ImageFile.TryPastedPath(null!, out _));
    }

    [Fact]
    public void Load_ReencodesABitmapAsPng_AtItsOwnSize()
    {
        string path = Bmp("small.bmp", 6, 4);

        var image = ImageFile.Load(path, out string? error);

        Assert.Null(error);
        Assert.NotNull(image);
        Assert.Equal(path, image.Path);
        Assert.Equal(ImageFile.Png, image.MediaType);
        Assert.True(IsPng(image.Bytes));
        Assert.Equal((6, 4), (image.Width, image.Height));
    }

    [Fact]
    public void Load_DownscalesToTheLongSide_KeepingTheRatio()
    {
        string path = Bmp("wide.bmp", ImageFile.MaxSide * 2, 8);

        var image = ImageFile.Load(path, out string? error);

        Assert.Null(error);
        Assert.NotNull(image);
        Assert.Equal((ImageFile.MaxSide, 4), (image.Width, image.Height));
        Assert.True(IsPng(image.Bytes));
    }

    [Fact]
    public void Load_SendsAFittingPng_ByteForByte()
    {
        var first = ImageFile.Load(Bmp("seed.bmp", 5, 5), out _);
        Assert.NotNull(first);
        string path = Path.Combine(_dir, "seed.png");
        File.WriteAllBytes(path, first.Bytes);

        var image = ImageFile.Load(path, out string? error);

        Assert.Null(error);
        Assert.NotNull(image);
        Assert.Equal(ImageFile.Png, image.MediaType);
        Assert.Equal(first.Bytes, image.Bytes);
        Assert.Equal((5, 5), (image.Width, image.Height));
    }

    private string Jpeg(string name, int width, int height)
    {
        // A real JPEG from the codec (the fixture only; the app never encodes one from a BMP).
        string path = Path.Combine(_dir, name);
        var settings = new ProcessImageSettings();
        Assert.True(settings.TrySetEncoderFormat(ImageFile.Jpeg));
        MagicImageProcessor.ProcessImage(SmokeChecks.SolidBmp(width, height), path, settings);
        return path;
    }

    [Fact]
    public void Load_SendsAFittingJpeg_ByteForByte_AndAWideOne_Downscaled()
    {
        string small = Jpeg("small.jpg", 8, 6);
        string wide = Jpeg("wide.jpg", ImageFile.MaxSide * 2, 8);

        var fitting = ImageFile.Load(small, out string? error);
        Assert.Null(error);
        Assert.NotNull(fitting);
        Assert.Equal(ImageFile.Jpeg, fitting.MediaType);
        Assert.Equal(File.ReadAllBytes(small), fitting.Bytes);
        Assert.Equal((8, 6), (fitting.Width, fitting.Height));

        var scaled = ImageFile.Load(wide, out error);
        Assert.Null(error);
        Assert.NotNull(scaled);
        Assert.Equal(ImageFile.Jpeg, scaled.MediaType);
        Assert.True(IsJpeg(scaled.Bytes));
        Assert.Equal((ImageFile.MaxSide, 4), (scaled.Width, scaled.Height));
    }

    [Fact]
    public void TheTypeComesFromTheBytes_NotTheName()
    {
        string png = Path.Combine(_dir, "really-a-png.jpg");
        File.WriteAllBytes(png, ImageFile.Load(Bmp("seed2.bmp", 3, 3), out _)!.Bytes);

        var image = ImageFile.Load(png, out string? error);

        Assert.Null(error);
        Assert.NotNull(image);
        Assert.Equal(ImageFile.Png, image.MediaType);
    }

    [Fact]
    public void SentAs_IsPinned()
    {
        Assert.Equal(ImageFile.Jpeg, ImageFile.SentAs("image/jpeg"));
        Assert.Equal(ImageFile.Jpeg, ImageFile.SentAs("image/webp"));
        Assert.Equal(ImageFile.Png, ImageFile.SentAs("image/png"));
        Assert.Equal(ImageFile.Png, ImageFile.SentAs("image/gif"));
        Assert.Equal(ImageFile.Png, ImageFile.SentAs("image/bmp"));
        Assert.Equal(ImageFile.Png, ImageFile.SentAs(""));
        Assert.Throws<ArgumentNullException>(() => ImageFile.SentAs(null!));
    }

    [Fact]
    public void TryLoad_ReportsTheFailure_AndLoadStillGivesTheSentence()
    {
        string missing = Path.Combine(_dir, "nope.png");
        string text = Path.Combine(_dir, "words.png");
        File.WriteAllText(text, "not a picture");
        string huge = Path.Combine(_dir, "huge.png");
        using (var stream = new FileStream(huge, FileMode.CreateNew))
        {
            stream.SetLength(ImageFile.MaxFileBytes + 1);
        }

        Assert.False(ImageFile.TryLoad(missing, out var none, out var failure));
        Assert.Null(none);
        Assert.Equal(ImageLoadFailure.NotFound, failure);
        Assert.False(ImageFile.TryLoad(text, out _, out failure));
        Assert.Equal(ImageLoadFailure.CouldNotRead, failure);
        Assert.False(ImageFile.TryLoad(huge, out _, out failure));
        Assert.Equal(ImageLoadFailure.TooLarge, failure);

        Assert.True(ImageFile.TryLoad(Bmp("ok.bmp", 3, 2), out var image, out failure));
        Assert.Equal(ImageLoadFailure.None, failure);
        Assert.Equal((3, 2, ImageFile.Png), (image!.Width, image.Height, image.MediaType));

        Assert.Equal(ImageFile.NotFound(missing), ImageFile.Notice(ImageLoadFailure.NotFound, missing));
        Assert.Equal(ImageFile.TooLarge(huge), ImageFile.Notice(ImageLoadFailure.TooLarge, huge));
        Assert.Equal(ImageFile.CouldNotRead(text), ImageFile.Notice(ImageLoadFailure.CouldNotRead, text));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageFile.Notice(ImageLoadFailure.None, text));
        Assert.Throws<ArgumentNullException>(() => ImageFile.TryLoad(null!, out _, out _));
    }

    [Fact]
    public void Load_RefusesAFileOverTheCap_Unread()
    {
        string path = Path.Combine(_dir, "huge.png");
        using (var stream = new FileStream(path, FileMode.CreateNew))
        {
            stream.SetLength(ImageFile.MaxFileBytes + 1);
        }

        var image = ImageFile.Load(path, out string? error);

        Assert.Null(image);
        Assert.Equal($"(image not attached: {path} is over 20 MB or 40 megapixels)", error);
    }

    [Fact]
    public void Load_RefusesTooManyPixels_Undecoded()
    {
        // A real 49-megapixel PNG that deflates to a few hundred KB: under the byte cap, over the pixel one.
        string path = Path.Combine(_dir, "vast.png");
        File.WriteAllBytes(path, BlackPng(7000, 7000));
        Assert.True(new FileInfo(path).Length < ImageFile.MaxFileBytes);

        var image = ImageFile.Load(path, out string? error);

        Assert.Null(image);
        Assert.Equal(ImageFile.TooLarge(path), error);
    }

    [Fact]
    public void Load_RefusesWhatTheCodecsCannotRead_AndAMissingFile()
    {
        string corrupt = Path.Combine(_dir, "corrupt.png");
        File.WriteAllText(corrupt, "not a picture at all");
        string missing = Path.Combine(_dir, "gone.png");

        Assert.Null(ImageFile.Load(corrupt, out string? error));
        Assert.Equal($"(image not attached: {corrupt} could not be read as an image)", error);
        Assert.Null(ImageFile.Load(missing, out error));
        Assert.Equal($"(image not attached: {missing} was not found)", error);
        Assert.Throws<ArgumentNullException>(() => ImageFile.Load(null!, out _));
    }

    [Fact]
    public void Constants_ArePinned()
    {
        Assert.Equal(2048, ImageFile.MaxSide);
        Assert.Equal(20_000_000, ImageFile.MaxFileBytes);
        Assert.Equal(40_000_000, ImageFile.MaxPixels);
        Assert.Equal(85, ImageFile.JpegQuality);
        Assert.Equal(new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" }, ImageFile.Extensions);
    }
    // ── Bytes that never were a file (a picture off the clipboard) ─────────

    [Fact]
    public void Load_FromBytes_DecodesLikeAFile_UnderTheNameGiven()
    {
        byte[] png = BlackPng(5, 3);
        string path = Path.Combine(_dir, "black.png");
        File.WriteAllBytes(path, png);

        var fromBytes = ImageFile.Load(png, "clipboard-1.png", out string? error);
        var fromFile = ImageFile.Load(path, out _);
        var bmp = ImageFile.Load(SmokeChecks.SolidBmp(6, 4), "clipboard-2.png", out string? bmpError);

        Assert.Null(error);
        Assert.NotNull(fromBytes);
        Assert.NotNull(fromFile);
        Assert.Equal("clipboard-1.png", fromBytes.Path);
        Assert.Same(png, fromBytes.Bytes);                    // a fitting PNG goes byte for byte
        Assert.Equal(fromFile.Bytes, fromBytes.Bytes);
        Assert.Equal((5, 3, ImageFile.Png), (fromBytes.Width, fromBytes.Height, fromBytes.MediaType));
        Assert.Null(bmpError);
        Assert.NotNull(bmp);
        Assert.Equal("clipboard-2.png", bmp.Path);
        Assert.True(IsPng(bmp.Bytes));                        // a DIB re-encodes as the name says
        Assert.Equal((6, 4), (bmp.Width, bmp.Height));
    }

    [Fact]
    public void Load_FromBytes_RefusesTheCap_AndGarbage()
    {
        Assert.Null(ImageFile.Load(new byte[ImageFile.MaxFileBytes + 1], "clipboard-1.png", out string? error));
        Assert.Equal("(image not attached: clipboard-1.png is over 20 MB or 40 megapixels)", error);
        Assert.Null(ImageFile.Load("not a picture"u8.ToArray(), "clipboard-1.png", out error));
        Assert.Equal("(image not attached: clipboard-1.png could not be read as an image)", error);
        Assert.Null(ImageFile.Load([], "clipboard-1.png", out error));
        Assert.Equal("(image not attached: clipboard-1.png could not be read as an image)", error);
        Assert.False(ImageFile.TryLoad(BlackPng(7000, 7000), "big.png", out _, out var failure));
        Assert.Equal(ImageLoadFailure.TooLarge, failure);
        Assert.Throws<ArgumentNullException>(() => ImageFile.Load((byte[])null!, "x", out _));
        Assert.Throws<ArgumentNullException>(() => ImageFile.Load([], null!, out _));
    }

    [Fact]
    public void ClipboardName_IsPinned()
    {
        Assert.Equal("clipboard-1.png", ImageFile.ClipboardName(1));
        Assert.Equal("clipboard-12.png", ImageFile.ClipboardName(12));
    }
}
