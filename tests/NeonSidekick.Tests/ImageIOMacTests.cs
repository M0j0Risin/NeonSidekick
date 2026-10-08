using NeonSidekick.Files;
using NeonSidekick.Images;
using NeonSidekick.Viewer;
using PhotoSauce.MagicScaler;

namespace NeonSidekick.Tests;

/// <summary>
/// The ImageIO backend on a Mac (2026-10-07): registered behind MagicScaler by <c>ModuleInit</c> as <c>Program.cs</c> does, and the
/// things only a Mac does — HEIC in and out, an iPhone-style sideways HEIC turned upright once (its <c>irot</c> box and its EXIF
/// orientation both say 6), the extra extensions on the line, and "metadata none" meaning none though ImageIO adds its own.
/// Skipped elsewhere; the shared picture tests run here too.
/// </summary>
public sealed class ImageIOMacTests
{
    /// <summary>
    /// 64×32 stored, left half red and right half blue, written by ImageIO itself as HEIC with orientation 6 (2026-10-07): it
    /// displays 32×64, red on top. Made with <c>CGImageDestination</c> and <c>kCGImagePropertyOrientation</c>; 630 bytes.
    /// </summary>
    private static readonly byte[] SidewaysHeic = Convert.FromBase64String(
        "AAAAJGZ0eXBoZWljAAAAAG1pZjFNaVBybWlhZk1pSEJoZWljAAABw21ldGEAAAAAAAAAIWhkbHIAAAAAAAAAAHBpY3QAAAAAAAAAAAAAAAAAAA"
        + "AAJGRpbmYAAAAcZHJlZgAAAAAAAAABAAAADHVybCAAAAABAAAADnBpdG0AAAAAAAEAAAA4aWluZgAAAAAAAgAAABVpbmZlAgAAAAABAABodmMx"
        + "AAAAABVpbmZlAgAAAQACAABFeGlmAAAAABppcmVmAAAAAAAAAA5jZHNjAAIAAQABAAAA5mlwcnAAAADFaXBjbwAAABNjb2xybmNseAACAAIABo"
        + "AAAAAMY2xsaQDLAEAAAAAUaXNwZQAAAAAAAABAAAAAIAAAAAlpcm90AwAAABBwaXhpAAAAAAMICAgAAABxaHZjQwEDcAAAALAAAAAAAB7wAPz9"
        + "+PgAAAsDoAABABdAAQwB//8DcAAAAwCwAAADAAADAB5wJKEAAQAjQgEBA3AAAAMAsAAAAwAAAwAeoBQgQcGMI4h7kWVTcCAgYAiiAAEACUQBwG"
        + "FyyEBTJAAAABlpcG1hAAAAAAAAAAEAAQaBAgOEBYYAAAAsaWxvYwAAAABEAAACAAEAAAABAAACGwAAAFsAAgAAAAEAAAH3AAAAJAAAAAFtZGF0"
        + "AAAAAAAAAI8AAAAGRXhpZgAATU0AKgAAAAgAAQESAAMAAAABAAYAAAAAAAAAAABXKAGvo1NGgXz//drP//bl+y/QCj1f/6jnyP5uvC/7jRMhWP"
        + "ywy3/q+2h3sDBQL/Q+g2BY+dKRxWrxAwSUOI/N2/5PyACUA9Lr7r/Kve23gCg9qanFO6//");

    [MacFact]
    public void ImageIO_IsRegistered_AndWritesTheSixFormats()
    {
        Assert.True(ImageIOCodecs.Registered);
        Assert.True(ImageCodecs.Available);
        Assert.Equal(["public.png", "public.jpeg", "com.compuserve.gif", "com.microsoft.bmp", "public.tiff", "public.heic"], ImageIOCodecs.Writers.Select(w => w.Uti));
        Assert.Equal([ImageFormats.Png, ImageFormats.Jpeg, ImageFormats.Gif, ImageFormats.Bmp, ImageFormats.Tiff, ImageFormats.Heif], ImageFormats.WritableFormats());
        Assert.True(OperatingSystem.IsMacOS() && ImageIOCodecs.Register());   // once only: a second call is a no-op
    }

    [MacFact]
    public void ASidewaysHeic_ReadsUpright_TurnedOnce_AndGoesToTheModelAsJpeg()
    {
        var info = ImageEditor.Info(SidewaysHeic)!;
        Assert.Equal(("image/heic", "HEIF", 32, 64, Orientation.Rotate90), (info.MimeType, info.Label, info.Width, info.Height, info.Orientation));

        Assert.True(ImageFile.TryLoad(SidewaysHeic, "IMG_0001.HEIC", out var sent, out _));
        Assert.Equal((ImageFile.Jpeg, 32, 64), (sent!.MediaType, sent.Width, sent.Height));
        Assert.True(ImageFixtures.Near(ImageFixtures.Red, ImageFixtures.Pixel(sent.Bytes, 16, 4), 40));    // red on top: turned once, not twice
        Assert.True(ImageFixtures.Near(ImageFixtures.Blue, ImageFixtures.Pixel(sent.Bytes, 16, 59), 40));

        var thumbnail = UI.ImageThumbnail.Read(sent)!;
        Assert.True(thumbnail.Height > thumbnail.Width);
    }

    [MacFact]
    public void Heif_AndTiff_RoundTrip_ThroughImageEdit()
    {
        var source = ImageFixtures.Quadrants(16, 8);
        var (heif, heifError) = ImageEditor.Apply(source, new ImageEditRequest { Rotate = 90 }, ImageFormats.Heif);
        var (tiff, tiffError) = ImageEditor.Apply(source, new ImageEditRequest(), ImageFormats.Tiff);

        Assert.True(heif is not null, heifError);
        Assert.True(tiff is not null, tiffError);
        Assert.Equal((8, 16, "image/heic"), ImageFixtures.Size(heif!.Bytes));
        Assert.Equal((16, 8, "image/tiff"), ImageFixtures.Size(tiff!.Bytes));
        Assert.True(ImageFixtures.Near(ImageFixtures.Blue, ImageFixtures.Pixel(heif.Bytes, 1, 2), 60));   // a clockwise turn: the bottom-left (blue) goes top-left
        Assert.True(ImageFixtures.Near(ImageFixtures.Red, ImageFixtures.Pixel(heif.Bytes, 6, 2), 60));    // and the top-left (red) top-right
        Assert.True(ImageFile.TryLoad(tiff.Bytes, "scan.tif", out var sent, out _));
        Assert.Equal(ImageFile.Png, sent!.MediaType);   // a TIFF goes as PNG, as a BMP does
    }

    [MacFact]
    public void MetadataNone_LeavesNothing_ThoughImageIOAddsItsOwn()
    {
        // ImageIO writes an EXIF block (size, colour space) and an empty Photoshop/IPTC block into every JPEG, eXIf into a PNG;
        // the encoder strips them when no metadata is asked for, as WIC writes none.
        var source = ImageFixtures.Quadrants(16, 16);
        foreach (var format in new[] { ImageFormats.Jpeg, ImageFormats.Png })
        {
            var (result, error) = ImageEditor.Apply(source, new ImageEditRequest { Width = 8 }, format);
            Assert.True(result is not null, error);
            Assert.Equal(MetadataKinds.None, MetadataStripper.Survey(result!.Bytes)!.Found);
        }

        Assert.True(ImageFile.TryLoad(ImageFixtures.Quadrants(16, 16), "a.bmp", out var sent, out _));
        Assert.Equal(MetadataKinds.None, MetadataStripper.Survey(sent!.Bytes)!.Found);
    }

    [MacFact]
    public void TheLine_TakesHeicTiffAndAvif_OnAMac_AndSendsThePhotosAsJpeg()
    {
        Assert.Equal([".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".heic", ".heif", ".tif", ".tiff", ".avif"], ImageFile.Extensions);
        Assert.True(ImageFile.IsImagePath("/Users/me/IMG_0001.HEIC"));
        Assert.True(ImageFile.IsImagePath("/Users/me/scan.tiff"));
        Assert.Equal(ImageFile.Jpeg, ImageFile.SentAs("image/heic"));
        Assert.Equal(ImageFile.Jpeg, ImageFile.SentAs("image/heif"));
        Assert.Equal(ImageFile.Jpeg, ImageFile.SentAs("image/avif"));
        Assert.Equal(ImageFile.Png, ImageFile.SentAs("image/tiff"));
    }

    [MacFact]
    public void TheNoEncoderSentence_NamesTheMac()
    {
        var (result, error) = ImageEditor.Apply(ImageFixtures.Quadrants(4, 4), new ImageEditRequest(), ImageFormats.Jxl);
        Assert.Null(result);
        Assert.Equal("Error: this Mac has no JXL encoder; it can write png, jpeg, gif, bmp, tiff, heif", error);
    }
}
