using System.Buffers.Binary;
using System.Drawing;
using NeonSidekick.Images;
using NeonSidekick.Viewer;
using PhotoSauce.MagicScaler;

namespace NeonSidekick.Tests;

/// <summary>Pictures built by hand for the image tools' tests (2026-10-04): solid colours, a four-colour quadrant, noise, an animated GIF, a JPEG with EXIF.</summary>
internal static class ImageFixtures
{
    public static readonly Color Red = Color.FromArgb(255, 255, 0, 0);
    public static readonly Color Green = Color.FromArgb(255, 0, 255, 0);
    public static readonly Color Blue = Color.FromArgb(255, 0, 0, 255);
    public static readonly Color White = Color.FromArgb(255, 255, 255, 255);

    /// <summary>A 24-bit BMP drawn by <paramref name="pixel"/>(x, y), y from the top.</summary>
    public static byte[] Bmp(int width, int height, Func<int, int, Color> pixel)
    {
        int stride = (width * 3 + 3) / 4 * 4;
        var bytes = new byte[54 + stride * height];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2), bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), height);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(28), 24);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(34), stride * height);
        for (int y = 0; y < height; y++)
        {
            // BMP rows run bottom-up.
            int row = 54 + (height - 1 - y) * stride;
            for (int x = 0; x < width; x++)
            {
                var c = pixel(x, y);
                bytes[row + x * 3] = c.B;
                bytes[row + x * 3 + 1] = c.G;
                bytes[row + x * 3 + 2] = c.R;
            }
        }

        return bytes;
    }

    public static byte[] Solid(int width, int height, Color colour) => Bmp(width, height, (_, _) => colour);

    /// <summary>Top-left red, top-right green, bottom-left blue, bottom-right white.</summary>
    public static byte[] Quadrants(int width, int height) => Bmp(width, height, (x, y) =>
        y < height / 2 ? (x < width / 2 ? Red : Green) : (x < width / 2 ? Blue : White));

    /// <summary>Seeded colour noise: compresses badly, so sizes are big and predictable.</summary>
    public static byte[] Noise(int width, int height, int seed = 7)
    {
        var random = new Random(seed);
        return Bmp(width, height, (_, _) => Color.FromArgb(255, random.Next(256), random.Next(256), random.Next(256)));
    }

    /// <summary>A 1×1 GIF of two frames (red, then green), built by hand.</summary>
    public static byte[] AnimatedGif()
    {
        var bytes = new List<byte>();
        bytes.AddRange("GIF89a"u8.ToArray());
        bytes.AddRange([1, 0, 1, 0, 0x80, 0, 0]);               // 1×1, a global table of two colours
        bytes.AddRange([0xFF, 0, 0, 0, 0xFF, 0]);               // red, green
        bytes.AddRange([0x21, 0xFF, 0x0B, .. "NETSCAPE2.0"u8.ToArray(), 3, 1, 0, 0, 0]);
        foreach (byte index in new byte[] { 0, 1 })
        {
            bytes.AddRange([0x21, 0xF9, 4, 0, 10, 0, 0, 0]);    // 0.1 s a frame
            bytes.AddRange([0x2C, 0, 0, 0, 0, 1, 0, 1, 0, 0]);  // the frame: 1×1 at 0,0
            bytes.AddRange([2, 2, (byte)(index == 0 ? 0x44 : 0x4C), 1, 0]);   // LZW: clear, the index, end
        }

        bytes.Add(0x3B);
        return bytes.ToArray();
    }

    /// <summary>The copyright <see cref="ExifJpeg"/> carries.</summary>
    public const string Copyright = "NeonTest Copyright 2026";

    /// <summary>
    /// A small JPEG with an EXIF block spliced in after its start marker: IFD0 holds an artist, <see cref="Copyright"/> and a GPS
    /// pointer; the GPS IFD a version, a latitude reference and a latitude.
    /// </summary>
    public static byte[] ExifJpeg()
    {
        var settings = new ProcessImageSettings();
        settings.TrySetEncoderFormat("image/jpeg");
        using var output = new MemoryStream();
        MagicImageProcessor.ProcessImage(Quadrants(16, 16), output, settings);
        byte[] jpeg = output.ToArray();

        byte[] artist = "NeonTest Artist\0"u8.ToArray();
        byte[] copyright = System.Text.Encoding.ASCII.GetBytes(Copyright + "\0");
        var tiff = new List<byte>();
        void U16(int v) { tiff.Add((byte)v); tiff.Add((byte)(v >> 8)); }
        void U32(long v) { for (int i = 0; i < 4; i++) { tiff.Add((byte)(v >> (8 * i))); } }
        const int ifd0 = 8, ifd0Size = 2 + 3 * 12 + 4;
        int gpsIfd = ifd0 + ifd0Size, gpsSize = 2 + 3 * 12 + 4;
        int data = gpsIfd + gpsSize;
        int artistAt = data, copyrightAt = artistAt + artist.Length, latitudeAt = copyrightAt + copyright.Length;
        tiff.AddRange("II*\0"u8.ToArray());
        U32(ifd0);
        U16(3);
        U16(0x013B); U16(2); U32(artist.Length); U32(artistAt);
        U16(0x8298); U16(2); U32(copyright.Length); U32(copyrightAt);
        U16(0x8825); U16(4); U32(1); U32(gpsIfd);
        U32(0);
        U16(3);
        U16(0x0000); U16(1); U32(4); tiff.AddRange([2, 3, 0, 0]);
        U16(0x0001); U16(2); U32(2); tiff.AddRange([(byte)'N', 0, 0, 0]);
        U16(0x0002); U16(5); U32(3); U32(latitudeAt);
        U32(0);
        tiff.AddRange(artist);
        tiff.AddRange(copyright);
        foreach (var (n, d) in new[] { (47, 1), (36, 1), (1234, 100) })
        {
            U32(n);
            U32(d);
        }

        byte[] app1 = [.. "Exif\0\0"u8.ToArray(), .. tiff];
        int length = app1.Length + 2;
        return [0xFF, 0xD8, 0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. app1, .. jpeg.AsSpan(2).ToArray()];
    }

    /// <summary>Whether a picture's bytes carry <see cref="Copyright"/> anywhere (EXIF or XMP).</summary>
    public static bool HasCopyright(byte[] picture) => picture.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(Copyright)) >= 0;

    /// <summary>Whether a picture keeps a GPS position: an EXIF GPS pointer (tag 0x8825, either byte order) or XMP's GPSLatitude.</summary>
    public static bool HasGps(byte[] picture) =>
        picture.AsSpan().IndexOf("GPSLatitude"u8) >= 0 || picture.AsSpan().IndexOf((ReadOnlySpan<byte>)[0x25, 0x88, 0x04, 0x00]) >= 0 || picture.AsSpan().IndexOf((ReadOnlySpan<byte>)[0x88, 0x25, 0x00, 0x04]) >= 0;

    /// <summary>The colour at (x, y) of a picture's bytes, decoded.</summary>
    public static Color Pixel(byte[] picture, int x, int y)
    {
        var bitmap = ViewerImage.Decode(picture, "test") ?? throw new InvalidOperationException("not a picture");
        int at = (y * bitmap.Width + x) * 4;
        return Color.FromArgb(255, bitmap.Bgrx[at + 2], bitmap.Bgrx[at + 1], bitmap.Bgrx[at]);
    }

    public static (int Width, int Height, string? Mime) Size(byte[] picture)
    {
        var info = ImageFileInfo.Load(picture);
        return (info.Frames[0].Width, info.Frames[0].Height, info.MimeType);
    }

    public static bool Near(Color a, Color b, int tolerance = 12) =>
        Math.Abs(a.R - b.R) <= tolerance && Math.Abs(a.G - b.G) <= tolerance && Math.Abs(a.B - b.B) <= tolerance;
}

/// <summary><see cref="ImageEditor"/>: the geometry plan (pure), and every step through the real WIC codecs on hand-made pictures.</summary>
public sealed class ImageEditorTests
{
    private static (ImageEditResult Result, byte[] Bytes) Edit(byte[] source, ImageEditRequest request, ImageFormat? format = null)
    {
        var (result, error) = ImageEditor.Apply(source, request, format ?? ImageFormats.Png);
        Assert.True(result is not null, error);
        return (result!, result!.Bytes);
    }

    // ---- the plan ----

    [Theory]
    [InlineData(ImageFit.Contain, 50, 50, 50, 25)]
    [InlineData(ImageFit.Contain, 400, 400, 400, 200)]   // contain enlarges
    [InlineData(ImageFit.Shrink, 400, 400, 200, 100)]    // shrink never does
    [InlineData(ImageFit.Shrink, 50, 50, 50, 25)]
    [InlineData(ImageFit.Stretch, 50, 50, 50, 50)]
    [InlineData(ImageFit.Cover, 50, 50, 50, 50)]
    [InlineData(ImageFit.Pad, 50, 50, 50, 50)]
    public void Plan_BothSides_FollowTheFit(ImageFit fit, int width, int height, int finalWidth, int finalHeight)
    {
        var (geometry, error) = ImageEditor.Plan(200, 100, new ImageEditRequest { Width = width, Height = height, Fit = fit });

        Assert.Null(error);
        Assert.Equal((finalWidth, finalHeight), (geometry!.FinalWidth, geometry.FinalHeight));
    }

    [Fact]
    public void Plan_Cover_CutsAtTheAnchor_AndPad_FillsAtTheAnchor()
    {
        var center = ImageEditor.Plan(200, 100, new ImageEditRequest { Width = 50, Height = 50, Fit = ImageFit.Cover }).Geometry!;
        var left = ImageEditor.Plan(200, 100, new ImageEditRequest { Width = 50, Height = 50, Fit = ImageFit.Cover, Anchor = ImageAnchor.Left }).Geometry!;
        var right = ImageEditor.Plan(200, 100, new ImageEditRequest { Width = 50, Height = 50, Fit = ImageFit.Cover, Anchor = ImageAnchor.Right }).Geometry!;
        var padTop = ImageEditor.Plan(200, 100, new ImageEditRequest { Width = 50, Height = 50, Fit = ImageFit.Pad, Anchor = ImageAnchor.Top }).Geometry!;
        var padCenter = ImageEditor.Plan(200, 100, new ImageEditRequest { Width = 50, Height = 50, Fit = ImageFit.Pad }).Geometry!;

        Assert.Equal(new Rectangle(50, 0, 100, 100), center.Crop);
        Assert.Equal(new Rectangle(0, 0, 100, 100), left.Crop);
        Assert.Equal(new Rectangle(100, 0, 100, 100), right.Crop);
        Assert.Equal((50, 25, 0, 25), (padTop.ResizeWidth, padTop.ResizeHeight, padTop.FillTop, padTop.FillBottom));
        Assert.Equal((12, 13), (padCenter.FillTop, padCenter.FillBottom));
    }

    [Fact]
    public void Plan_OneSide_KeepsTheAspect_AndScaleMultiplies()
    {
        Assert.Equal((40, 20), Final(new ImageEditRequest { Width = 40 }));
        Assert.Equal((80, 40), Final(new ImageEditRequest { Height = 40 }));
        Assert.Equal((400, 200), Final(new ImageEditRequest { Scale = 2 }));
        Assert.Equal((100, 50), Final(new ImageEditRequest { Scale = 0.5 }));
        Assert.Equal((200, 100), Final(new ImageEditRequest { Width = 400, Fit = ImageFit.Shrink }));
        Assert.Equal((200, 100), Final(new ImageEditRequest()));

        static (int, int) Final(ImageEditRequest request)
        {
            var g = ImageEditor.Plan(200, 100, request).Geometry!;
            return (g.FinalWidth, g.FinalHeight);
        }
    }

    [Fact]
    public void Plan_AQuarterTurn_MeansTheFinalSides()
    {
        var g = ImageEditor.Plan(200, 100, new ImageEditRequest { Width = 50, Rotate = 90 }).Geometry!;

        // 50 wide after the turn: the source's height becomes 50, so it is scaled to 100×50 then turned to 50×100.
        Assert.Equal((100, 50), (g.ResizeWidth, g.ResizeHeight));
        Assert.Equal((50, 100), (g.FinalWidth, g.FinalHeight));
    }

    [Fact]
    public void Plan_Crop_InsideIsKept_OutsideIsRefused_AndPadAddsTwice()
    {
        var inside = ImageEditor.Plan(200, 100, new ImageEditRequest { Crop = new Rectangle(10, 10, 50, 40), Pad = 5 });
        var outside = ImageEditor.Plan(200, 100, new ImageEditRequest { Crop = new Rectangle(180, 0, 50, 40) });

        Assert.Equal(new Rectangle(10, 10, 50, 40), inside.Geometry!.Crop);
        Assert.Equal((60, 50), (inside.Geometry.FinalWidth, inside.Geometry.FinalHeight));
        Assert.Null(outside.Geometry);
        Assert.Equal("Error: the crop 180,0 50×40 is not inside the picture, which is 200×100", outside.Error);
    }

    [Fact]
    public void Plan_TooBig_IsRefusedBeforeAnyWork()
    {
        var (geometry, error) = ImageEditor.Plan(4000, 4000, new ImageEditRequest { Scale = 16 });

        Assert.Null(geometry);
        Assert.Equal("Error: the result would be 64000×64000; at most 32768 a side and 100 megapixels", error);
    }

    [Theory]
    [InlineData(ImageAnchor.Top, 0, ImageFlip.None, 0, -1)]
    [InlineData(ImageAnchor.Top, 90, ImageFlip.None, -1, 0)]       // the final top was the source's left
    [InlineData(ImageAnchor.Right, 90, ImageFlip.None, 0, -1)]     // the final right was the source's top
    [InlineData(ImageAnchor.Top, 180, ImageFlip.None, 0, 1)]
    [InlineData(ImageAnchor.Top, 270, ImageFlip.None, 1, 0)]
    [InlineData(ImageAnchor.Left, 0, ImageFlip.Horizontal, 1, 0)]
    [InlineData(ImageAnchor.TopLeft, 0, ImageFlip.Vertical, -1, 1)]
    public void SourceAnchor_UndoesTheFlipThenTheTurn(ImageAnchor anchor, int rotate, ImageFlip flip, int x, int y)
    {
        Assert.Equal((x, y), ImageEditor.SourceAnchor(anchor, rotate, flip));
    }

    // ---- the pixels ----

    [Fact]
    public void Apply_Resize_DownAndUp()
    {
        var (down, downBytes) = Edit(ImageFixtures.Quadrants(100, 50), new ImageEditRequest { Width = 40 });
        var (up, _) = Edit(ImageFixtures.Quadrants(100, 50), new ImageEditRequest { Scale = 2 });

        Assert.Equal((40, 20, 100, 50), (down.Width, down.Height, down.SourceWidth, down.SourceHeight));
        Assert.Equal((200, 100), (up.Width, up.Height));
        Assert.Equal((40, 20, "image/png"), ImageFixtures.Size(downBytes));
    }

    [Fact]
    public void Apply_Rotate90_TurnsClockwise()
    {
        var (result, bytes) = Edit(ImageFixtures.Quadrants(20, 10), new ImageEditRequest { Rotate = 90 });

        Assert.Equal((10, 20), (result.Width, result.Height));
        // Clockwise: the bottom-left (blue) comes to the top-left, the top-left (red) to the top-right.
        Assert.True(ImageFixtures.Near(ImageFixtures.Blue, ImageFixtures.Pixel(bytes, 1, 1)), ImageFixtures.Pixel(bytes, 1, 1).ToString());
        Assert.True(ImageFixtures.Near(ImageFixtures.Red, ImageFixtures.Pixel(bytes, 8, 1)), ImageFixtures.Pixel(bytes, 8, 1).ToString());
    }

    [Fact]
    public void Apply_Rotate270_AndFlips()
    {
        var (_, turned) = Edit(ImageFixtures.Quadrants(20, 10), new ImageEditRequest { Rotate = 270 });
        var (_, mirrored) = Edit(ImageFixtures.Quadrants(20, 10), new ImageEditRequest { Flip = ImageFlip.Horizontal });
        var (_, flipped) = Edit(ImageFixtures.Quadrants(20, 10), new ImageEditRequest { Flip = ImageFlip.Vertical });

        Assert.True(ImageFixtures.Near(ImageFixtures.Green, ImageFixtures.Pixel(turned, 1, 1)));       // anticlockwise: the top-right comes to the top-left
        Assert.True(ImageFixtures.Near(ImageFixtures.Green, ImageFixtures.Pixel(mirrored, 1, 1)));
        Assert.True(ImageFixtures.Near(ImageFixtures.Blue, ImageFixtures.Pixel(flipped, 1, 1)));
    }

    [Fact]
    public void Apply_Crop_TakesThatPart()
    {
        var (result, bytes) = Edit(ImageFixtures.Quadrants(20, 20), new ImageEditRequest { Crop = new Rectangle(10, 0, 10, 10) });

        Assert.Equal((10, 10), (result.Width, result.Height));
        Assert.True(ImageFixtures.Near(ImageFixtures.Green, ImageFixtures.Pixel(bytes, 5, 5)));
    }

    [Fact]
    public void Apply_Cover_KeepsTheAnchoredPart()
    {
        var (_, left) = Edit(ImageFixtures.Quadrants(40, 20), new ImageEditRequest { Width = 10, Height = 20, Fit = ImageFit.Cover, Anchor = ImageAnchor.Left });
        var (_, right) = Edit(ImageFixtures.Quadrants(40, 20), new ImageEditRequest { Width = 10, Height = 20, Fit = ImageFit.Cover, Anchor = ImageAnchor.Right });

        Assert.True(ImageFixtures.Near(ImageFixtures.Red, ImageFixtures.Pixel(left, 5, 2)));
        Assert.True(ImageFixtures.Near(ImageFixtures.Green, ImageFixtures.Pixel(right, 5, 2)));
    }

    [Fact]
    public void Apply_Filters_GreyAndNegative()
    {
        var (_, grey) = Edit(ImageFixtures.Solid(4, 4, ImageFixtures.Red), new ImageEditRequest { Filter = ImageFilter.Grey });
        var (_, negative) = Edit(ImageFixtures.Solid(4, 4, ImageFixtures.Red), new ImageEditRequest { Filter = ImageFilter.Negative });

        var g = ImageFixtures.Pixel(grey, 1, 1);
        Assert.True(g.R == g.G && g.G == g.B && g.R is > 40 and < 140, g.ToString());
        Assert.True(ImageFixtures.Near(Color.FromArgb(255, 0, 255, 255), ImageFixtures.Pixel(negative, 1, 1)), ImageFixtures.Pixel(negative, 1, 1).ToString());
    }

    [Fact]
    public void Apply_Brightness_Contrast_Saturation()
    {
        var grey = Color.FromArgb(255, 100, 100, 100);
        var (_, brighter) = Edit(ImageFixtures.Solid(4, 4, grey), new ImageEditRequest { Brightness = 50 });
        var (_, darker) = Edit(ImageFixtures.Solid(4, 4, grey), new ImageEditRequest { Brightness = -50 });
        var (_, greyed) = Edit(ImageFixtures.Solid(4, 4, ImageFixtures.Red), new ImageEditRequest { Saturation = -100 });

        Assert.True(ImageFixtures.Pixel(brighter, 1, 1).R > 130, ImageFixtures.Pixel(brighter, 1, 1).ToString());
        Assert.True(ImageFixtures.Pixel(darker, 1, 1).R < 70, ImageFixtures.Pixel(darker, 1, 1).ToString());
        var s = ImageFixtures.Pixel(greyed, 1, 1);
        Assert.True(Math.Abs(s.R - s.G) <= 2 && Math.Abs(s.G - s.B) <= 2, s.ToString());
    }

    [Fact]
    public void Apply_Hue120_TurnsRedTowardGreen()
    {
        var (_, bytes) = Edit(ImageFixtures.Solid(4, 4, ImageFixtures.Red), new ImageEditRequest { Hue = 120 });

        var c = ImageFixtures.Pixel(bytes, 1, 1);
        Assert.True(c.G > c.R + 40 && c.G > c.B + 40, c.ToString());
    }

    [Fact]
    public void Apply_Tint_PullsTowardTheColour_ByItsAmount()
    {
        var (_, none) = Edit(ImageFixtures.Solid(4, 4, ImageFixtures.White), new ImageEditRequest { Tint = ImageFixtures.Blue, TintAmount = 0, Pad = 1 });
        var (_, half) = Edit(ImageFixtures.Solid(4, 4, ImageFixtures.White), new ImageEditRequest { Tint = ImageFixtures.Blue, TintAmount = 50 });
        var (_, full) = Edit(ImageFixtures.Solid(4, 4, ImageFixtures.White), new ImageEditRequest { Tint = ImageFixtures.Blue, TintAmount = 100 });

        Assert.True(ImageFixtures.Near(ImageFixtures.White, ImageFixtures.Pixel(none, 2, 2)));
        var h = ImageFixtures.Pixel(half, 1, 1);
        Assert.True(h.B > 240 && h.R is > 60 and < 230, h.ToString());
        var f = ImageFixtures.Pixel(full, 1, 1);
        Assert.True(f.B > 240 && f.R < 20 && f.G < 20, f.ToString());
    }

    [Fact]
    public void Apply_Pad_AddsABorderInTheBackground()
    {
        var (result, bytes) = Edit(ImageFixtures.Solid(4, 4, ImageFixtures.Red), new ImageEditRequest { Pad = 3, Background = Color.FromArgb(255, 0, 0, 0) });

        Assert.Equal((10, 10), (result.Width, result.Height));
        Assert.True(ImageFixtures.Near(Color.FromArgb(255, 0, 0, 0), ImageFixtures.Pixel(bytes, 0, 0)));
        Assert.True(ImageFixtures.Near(ImageFixtures.Red, ImageFixtures.Pixel(bytes, 5, 5)));
    }

    [Fact]
    public void Apply_FitPad_FillsAroundThePicture()
    {
        var (result, bytes) = Edit(ImageFixtures.Solid(20, 10, ImageFixtures.Red), new ImageEditRequest { Width = 20, Height = 20, Fit = ImageFit.Pad, Background = ImageFixtures.Blue });

        Assert.Equal((20, 20), (result.Width, result.Height));
        Assert.True(ImageFixtures.Near(ImageFixtures.Blue, ImageFixtures.Pixel(bytes, 10, 1)));
        Assert.True(ImageFixtures.Near(ImageFixtures.Red, ImageFixtures.Pixel(bytes, 10, 10)));
    }

    [Fact]
    public void Apply_Jpeg_PutsTransparencyOnWhite()
    {
        var (_, transparent) = Edit(ImageFixtures.Solid(4, 4, ImageFixtures.Red), new ImageEditRequest { Pad = 4, Background = Color.FromArgb(0, 0, 0, 0) });
        var (_, jpeg) = Edit(transparent, new ImageEditRequest(), ImageFormats.Jpeg);

        Assert.True(ImageFileInfo.Load(transparent).Frames[0].HasAlpha);
        Assert.True(ImageFixtures.Near(ImageFixtures.White, ImageFixtures.Pixel(jpeg, 0, 0), 20), ImageFixtures.Pixel(jpeg, 0, 0).ToString());
    }

    [Fact]
    public void Apply_Blur_SoftensAnEdge()
    {
        var (_, sharp) = Edit(ImageFixtures.Quadrants(20, 20), new ImageEditRequest { Filter = ImageFilter.None, Pad = 0, Brightness = 0, Sharpen = false, Interpolation = "nearest", Width = 20 });
        var (_, blurred) = Edit(ImageFixtures.Quadrants(20, 20), new ImageEditRequest { Blur = 3 });

        Assert.True(ImageFixtures.Near(ImageFixtures.Red, ImageFixtures.Pixel(sharp, 9, 2), 4));
        var edge = ImageFixtures.Pixel(blurred, 9, 2);
        Assert.True(edge.G > 30 && edge.R < 230, edge.ToString());
    }

    [Fact]
    public void Apply_Sharpen_WithoutAResize_ChangesThePixels()
    {
        // Settled 2026-10-04: MagicScaler's unsharp mask runs on a same-size pass too, so sharpen needs no resize.
        var (_, sharpened) = Edit(ImageFixtures.Quadrants(32, 32), new ImageEditRequest { Sharpen = true });
        var (_, unsharpened) = Edit(ImageFixtures.Quadrants(32, 32), new ImageEditRequest { Sharpen = false });

        Assert.NotEqual(Convert.ToBase64String(unsharpened), Convert.ToBase64String(sharpened));
    }

    [Fact]
    public void Apply_Metadata_NoneDropsIt_BasicKeepsTheCopyright_AllKeepsThePlace()
    {
        var source = ImageFixtures.ExifJpeg();
        Assert.True(ImageFixtures.HasCopyright(source) && ImageFixtures.HasGps(source));

        var (_, none) = Edit(source, new ImageEditRequest { Metadata = ImageMetadataPolicy.None }, ImageFormats.Jpeg);
        var (_, basic) = Edit(source, new ImageEditRequest { Metadata = ImageMetadataPolicy.Basic }, ImageFormats.Jpeg);
        var (_, all) = Edit(source, new ImageEditRequest { Metadata = ImageMetadataPolicy.All }, ImageFormats.Jpeg);

        Assert.False(ImageFixtures.HasCopyright(none));
        Assert.False(ImageFixtures.HasGps(none));
        Assert.True(ImageFixtures.HasCopyright(basic));
        Assert.False(ImageFixtures.HasGps(basic));
        Assert.True(ImageFixtures.HasCopyright(all));
        Assert.True(ImageFixtures.HasGps(all));
    }

    // ---- the formats ----

    [Fact]
    public void Apply_EveryWritableFormat_RoundTrips_AndAMissingOne_SaysWhatCanBe()
    {
        var writable = ImageFormats.WritableFormats();
        Assert.Contains(ImageFormats.Png, writable);
        Assert.Contains(ImageFormats.Jpeg, writable);
        foreach (var format in writable)
        {
            var (result, bytes) = Edit(ImageFixtures.Quadrants(8, 8), new ImageEditRequest(), format);
            Assert.Equal(format.MimeType, ImageFixtures.Size(bytes).Mime);
            Assert.Equal((8, 8), (result.Width, result.Height));
        }

        foreach (var format in ImageFormats.All.Except(writable))
        {
            var (result, error) = ImageEditor.Apply(ImageFixtures.Quadrants(8, 8), new ImageEditRequest(), format);
            Assert.Null(result);
            Assert.Equal("Error: this Windows has no " + format.Label + " encoder; it can write " + string.Join(", ", writable.Select(f => f.Name)), error);
        }
    }

    [Fact]
    public void Apply_ChromaAndQuality_ChangeTheJpegsSize()
    {
        var source = ImageFixtures.Noise(64, 64);
        var (_, full) = Edit(source, new ImageEditRequest { Chroma = ImageChroma.Subsample444, Quality = 90 }, ImageFormats.Jpeg);
        var (_, sub) = Edit(source, new ImageEditRequest { Chroma = ImageChroma.Subsample420, Quality = 90 }, ImageFormats.Jpeg);
        var (low, lowBytes) = Edit(source, new ImageEditRequest { Quality = 20 }, ImageFormats.Jpeg);

        Assert.True(full.Length > sub.Length, $"{full.Length} vs {sub.Length}");
        Assert.True(lowBytes.Length < sub.Length);
        Assert.Equal(20, low.Quality);
    }

    [Fact]
    public void Apply_Colors_MakesAPalettePng_AndInterlace_AndDpi_AreWritten()
    {
        var source = ImageFixtures.Noise(32, 32);
        var (_, truecolour) = Edit(source, new ImageEditRequest());
        var (_, palette) = Edit(source, new ImageEditRequest { Colors = 16, Dither = DitherMode.None });
        var (_, interlaced) = Edit(source, new ImageEditRequest { Interlace = true, Dpi = 300 });

        Assert.Equal(3, palette[25]);                       // IHDR colour type 3: indexed
        Assert.True(palette.Length < truecolour.Length);
        Assert.Equal(1, interlaced[28]);                    // IHDR interlace method
        int phys = IndexOf(interlaced, "pHYs"u8);
        Assert.True(phys > 0);
        Assert.Equal(11811, BinaryPrimitives.ReadInt32BigEndian(interlaced.AsSpan(phys + 4)));   // 300 DPI in pixels a metre

        static int IndexOf(byte[] haystack, ReadOnlySpan<byte> needle) => haystack.AsSpan().IndexOf(needle);
    }

    [Theory]
    [InlineData("png", "chroma", "Error: chroma does not apply to PNG; it is for jpeg")]
    [InlineData("jpeg", "colors", "Error: colors does not apply to JPEG; it is for png or gif")]
    [InlineData("jpeg", "interlace", "Error: interlace does not apply to JPEG; it is for png")]
    [InlineData("png", "dither", "Error: dither does not apply to PNG; it is for gif, or png with colors")]
    [InlineData("png", "quality", "Error: quality does not apply to PNG; it is for jpeg, jxl or heif")]
    public void OptionMismatch_RefusesAnOptionTheFormatCannotTake(string formatName, string option, string expected)
    {
        var request = option switch
        {
            "chroma" => new ImageEditRequest { Chroma = ImageChroma.Subsample444 },
            "colors" => new ImageEditRequest { Colors = 16 },
            "interlace" => new ImageEditRequest { Interlace = true },
            "dither" => new ImageEditRequest { Dither = DitherMode.None },
            _ => new ImageEditRequest { Quality = 50 },
        };

        Assert.Equal(expected, ImageEditor.OptionMismatch(request, ImageFormats.ByName(formatName)!));
        Assert.Equal(expected, ImageEditor.Apply(ImageFixtures.Quadrants(4, 4), request, ImageFormats.ByName(formatName)!).Error);
    }

    // ---- max_kb ----

    [Fact]
    public void MaxKb_Jpeg_LowersTheQualityToFit()
    {
        var source = ImageFixtures.Noise(256, 256);
        var (full, _) = Edit(source, new ImageEditRequest { Quality = 90 }, ImageFormats.Jpeg);
        int target = (int)(full.Bytes.Length * 0.6 / 1024);

        var (result, bytes) = Edit(source, new ImageEditRequest { Quality = 90, MaxKb = target }, ImageFormats.Jpeg);

        Assert.True(bytes.Length <= target * 1024);
        Assert.True(result.Quality is > ImageEditor.MinFitQuality - 1 and < 90, result.Quality?.ToString());
        Assert.False(result.Shrunk);
        Assert.Equal((256, 256), (result.Width, result.Height));
        Assert.StartsWith("wrote a.jpg (JPEG, 256×256, ", ImageText.Written("a.jpg", result, target));
        Assert.EndsWith("; under " + target + " KB at quality " + result.Quality, ImageText.Written("a.jpg", result, target));
    }

    [Fact]
    public void MaxKb_ShrinksWhenTheQualityFloorIsNotEnough()
    {
        var source = ImageFixtures.Noise(256, 256);
        var (floor, _) = Edit(source, new ImageEditRequest { Quality = ImageEditor.MinFitQuality }, ImageFormats.Jpeg);
        int target = (int)(floor.Bytes.Length * 0.7 / 1024);

        var (result, bytes) = Edit(source, new ImageEditRequest { MaxKb = target }, ImageFormats.Jpeg);

        Assert.True(bytes.Length <= target * 1024);
        Assert.True(result.Shrunk);
        Assert.Equal(ImageEditor.MinFitQuality, result.Quality);
        Assert.True(result.Width < 256);
        Assert.EndsWith(", made smaller to fit", ImageText.Written("a.jpg", result, target));
    }

    [Fact]
    public void MaxKb_Png_OnlyShrinks_AndAnUnreachableCap_WritesNothing()
    {
        var source = ImageFixtures.Noise(128, 128);
        var (full, _) = Edit(source, new ImageEditRequest());
        int target = (int)(full.Bytes.Length * 0.7 / 1024);

        var (result, _) = Edit(source, new ImageEditRequest { MaxKb = target });
        var (none, error) = ImageEditor.Apply(source, new ImageEditRequest { MaxKb = 1 }, ImageFormats.Png);

        Assert.True(result.Shrunk);
        Assert.Null(result.Quality);
        Assert.True(result.Width < 128);
        Assert.Null(none);
        Assert.StartsWith("Error: could not get under 1 KB; the smallest tried was ", error);
        Assert.EndsWith(". Try colors (fewer colours) or a lossy format such as jpeg; nothing was written", error);
    }

    // ---- the rest ----

    [Fact]
    public void Apply_AnAnimatedGif_WritesTheFirstFrame_AndSaysSo()
    {
        var gif = ImageFixtures.AnimatedGif();
        Assert.Equal(2, ImageEditor.Info(gif)!.Frames);

        var (result, bytes) = Edit(gif, new ImageEditRequest { Scale = 4, Interpolation = "nearest" });

        Assert.Equal(2, result.Frames);
        Assert.True(ImageFixtures.Near(ImageFixtures.Red, ImageFixtures.Pixel(bytes, 1, 1)));
        Assert.EndsWith("; first of 2 frames", ImageText.Written("a.png", result));
    }

    [Fact]
    public void Apply_NotAPicture_AndInfo_OfOne()
    {
        Assert.Equal(ImageText.NotAnImage, ImageEditor.Apply("hello"u8.ToArray(), new ImageEditRequest(), ImageFormats.Png).Error);
        Assert.Null(ImageEditor.Info("hello"u8.ToArray()));

        var info = ImageEditor.Info(ImageFixtures.Quadrants(30, 20))!;
        Assert.Equal(("BMP", 30, 20, 1, false, Orientation.Normal), (info.Label, info.Width, info.Height, info.Frames, info.HasAlpha, info.Orientation));
        Assert.Equal("a.bmp: BMP, 30×20, " + NeonSidekick.Files.FileText.Size(info.Bytes), ImageText.Info("a.bmp", info));
    }

    [Fact]
    public void Written_NamesTheSourceSize_OnlyWhenItChanged()
    {
        var (same, _) = Edit(ImageFixtures.Quadrants(8, 8), new ImageEditRequest { Filter = ImageFilter.Grey });
        var (resized, _) = Edit(ImageFixtures.Quadrants(8, 8), new ImageEditRequest { Width = 4 });

        Assert.Matches(@"^wrote a\.png \(PNG, 8×8, \d+ B\)$", ImageText.Written("a.png", same));
        Assert.Matches(@"^wrote a\.png \(PNG, 4×4 from 8×8, \d+ B\)$", ImageText.Written("a.png", resized));
    }
}
