using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.UI;
using PhotoSauce.MagicScaler;
using Spectre.Console;
using Spectre.Console.Testing;

namespace NeonSidekick.Tests;

public class ImageThumbnailTests : IDisposable
{
    private static readonly Color Pink = new(0xFF, 0x40, 0xC8);   // what SolidBmp paints

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));

    public ImageThumbnailTests() => Directory.CreateDirectory(_dir);

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

    private ImageAttachment Solid(int width, int height)
    {
        string path = Path.Combine(_dir, $"solid-{width}x{height}.bmp");
        File.WriteAllBytes(path, SmokeChecks.SolidBmp(width, height));
        var image = ImageFile.Load(path, out string? error);
        Assert.Null(error);
        return image!;
    }

    [Fact]
    public void Read_FitsTheColumns_KeepingTheAspectRatio()
    {
        var thumbnail = ImageThumbnail.Read(Solid(64, 32));

        Assert.NotNull(thumbnail);
        Assert.Equal(48, thumbnail.Width);
        Assert.Equal(24, thumbnail.Height);
        Assert.Equal(48 * 24, thumbnail.Pixels.Length);
        Assert.All(thumbnail.Pixels, p => Assert.Equal(Pink, p));
        Assert.Equal(Pink, thumbnail.At(47, 23));
    }

    /// <summary>A left/right two-tone picture keeps its halves apart: a stride or format slip would smear the columns.</summary>
    [Fact]
    public void Read_KeepsTheColumnsInPlace()
    {
        byte[] bmp = SmokeChecks.SolidBmp(96, 48);   // 24-bit, rows of 288 bytes
        for (int y = 0; y < 48; y++)
        {
            for (int x = 48; x < 96; x++)
            {
                int at = 54 + y * 288 + x * 3;
                bmp[at] = 0x20;        // blue
                bmp[at + 1] = 0xE0;    // green
                bmp[at + 2] = 0x10;    // red
            }
        }

        string path = Path.Combine(_dir, "halves.bmp");
        File.WriteAllBytes(path, bmp);
        var image = ImageFile.Load(path, out string? error);
        Assert.Null(error);

        var thumbnail = ImageThumbnail.Read(image!);

        Assert.NotNull(thumbnail);
        Assert.Equal(48, thumbnail.Width);
        Assert.Equal(24, thumbnail.Height);
        var green = new Color(0x10, 0xE0, 0x20);
        // Sampled away from the seam: the scaler's filter rings a shade either side of it.
        for (int y = 0; y < 24; y++)
        {
            Assert.Equal(Pink, thumbnail.At(2, y));
            Assert.Equal(Pink, thumbnail.At(12, y));
            Assert.Equal(green, thumbnail.At(36, y));
            Assert.Equal(green, thumbnail.At(45, y));
        }
    }

    [Fact]
    public void BytesPerPixel_KnowsThreeFormats()
    {
        Assert.Equal(3, ImageThumbnail.BytesPerPixel(PixelFormats.Bgr24bpp));
        Assert.Equal(4, ImageThumbnail.BytesPerPixel(PixelFormats.Bgra32bpp));
        Assert.Equal(1, ImageThumbnail.BytesPerPixel(PixelFormats.Grey8bpp));
        Assert.Equal(0, ImageThumbnail.BytesPerPixel(PixelFormats.Planar.Y8bpp));
    }

    [Fact]
    public void Decode_ReadsEachFormat_AndBlendsAlphaOverTheMatte()
    {
        var matte = new Color(0, 0, 0);

        Assert.Equal([new Color(0x10, 0x20, 0x30)], ImageThumbnail.Decode([0x30, 0x20, 0x10], 3, matte));
        Assert.Equal([new Color(0x80, 0x80, 0x80), new Color(0xFF, 0xFF, 0xFF)], ImageThumbnail.Decode([0x80, 0xFF], 1, matte));
        Assert.Equal(
            [new Color(0x10, 0x20, 0x30), matte, new Color(0x80, 0x00, 0x00)],
            ImageThumbnail.Decode([0x30, 0x20, 0x10, 0xFF, 0x30, 0x20, 0x10, 0x00, 0x00, 0x00, 0xFF, 0x80], 4, matte));
        Assert.Equal([new Color(0x80, 0x80, 0x80)], ImageThumbnail.Decode([0x00, 0x00, 0x00, 0x00], 4, new Color(0x80, 0x80, 0x80)));
    }

    [Fact]
    public void Read_ATallPicture_IsCappedByTheRows()
    {
        var thumbnail = ImageThumbnail.Read(Solid(32, 64));

        Assert.NotNull(thumbnail);
        Assert.Equal(12, thumbnail.Width);
        Assert.Equal(24, thumbnail.Height);
    }

    [Fact]
    public void Read_HonoursItsOwnSize()
    {
        var thumbnail = ImageThumbnail.Read(Solid(100, 100), columns: 8, maxRows: 40);

        Assert.NotNull(thumbnail);
        Assert.Equal(8, thumbnail.Width);
        Assert.Equal(8, thumbnail.Height);
    }

    [Fact]
    public void Read_BytesThatAreNoImage_IsNull()
    {
        var image = new ImageAttachment(Path.Combine(_dir, "broken.png"), [1, 2, 3, 4], ImageFile.Png, 1, 1);

        Assert.Null(ImageThumbnail.Read(image));
    }

    [Fact]
    public void ToCanvas_IsOneCellPerPixelAcross_TwoPixelRowsPerLine()
    {
        using var console = new TestConsole();
        console.Profile.Width = 240;
        var thumbnail = ImageThumbnail.Read(Solid(64, 32))!;

        console.Write(thumbnail.ToCanvas());

        Assert.Equal(12, console.Lines.Count);
        Assert.All(console.Lines, line => Assert.Equal(new string('▀', 48), line));
    }

    [Fact]
    public void At_OutsideTheGrid_Throws()
    {
        var thumbnail = new ImageThumbnail(2, 1, [Color.Red, Color.Blue]);

        Assert.Equal(Color.Blue, thumbnail.At(1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => thumbnail.At(2, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => thumbnail.At(0, 1));
    }
}
