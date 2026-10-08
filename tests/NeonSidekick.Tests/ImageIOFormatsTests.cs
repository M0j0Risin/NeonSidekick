using NeonSidekick.Files;
using NeonSidekick.Images;
using PhotoSauce.MagicScaler;

namespace NeonSidekick.Tests;

/// <summary>
/// <see cref="ImageIOFormats"/> (2026-10-07, pictures on a Mac): the pure half of the ImageIO backend — the format table, the
/// magic bytes MagicScaler routes by, the option and metadata mapping, the pixel shuffles. Runs on every OS; the native half
/// is <see cref="ImageIOMacTests"/>'s.
/// </summary>
public sealed class ImageIOFormatsTests
{
    [Fact]
    public void TheTable_NamesEveryFormat_AndOnlyTheWritersTakePixels()
    {
        Assert.Equal(["public.png", "public.jpeg", "com.compuserve.gif", "com.microsoft.bmp", "public.tiff", "public.heic", "public.heif", "org.webmproject.webp", "public.avif", "public.jpeg-xl"], ImageIOFormats.All.Select(f => f.Uti));
        Assert.Equal(["public.png", "public.jpeg", "com.compuserve.gif", "com.microsoft.bmp", "public.tiff", "public.heic"], ImageIOFormats.All.Where(f => f.Writes).Select(f => f.Uti));
        Assert.Equal("image/heic", ImageIOFormats.MimeTypeOf("public.heic"));   // ImageFormats.Heif's, so image_edit's writable probe reads it back
        Assert.Equal(ImageFormats.Heif.MimeType, ImageIOFormats.Heic.MimeType);
        Assert.Equal("image/webp", ImageIOFormats.MimeTypeOf("org.webmproject.webp"));
        Assert.Null(ImageIOFormats.MimeTypeOf("com.adobe.photoshop-image"));
        Assert.Null(ImageIOFormats.ByUti(null));
    }

    [Fact]
    public void JpegAndBmp_TakeNoAlpha_SoMagicScalerMattesFirst_AsForWic()
    {
        Assert.DoesNotContain(PixelFormats.Bgra32bpp, ImageIOFormats.Jpeg.EncoderPixelFormats);
        Assert.DoesNotContain(PixelFormats.Bgra32bpp, ImageIOFormats.Bmp.EncoderPixelFormats);
        Assert.Contains(PixelFormats.Bgra32bpp, ImageIOFormats.Png.EncoderPixelFormats);
        Assert.Contains(PixelFormats.Bgra32bpp, ImageIOFormats.Heic.EncoderPixelFormats);
    }

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { (byte)'B', (byte)'M', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { (byte)'I', (byte)'I', 0x2A, 0, 8, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { (byte)'M', (byte)'M', 0, 0x2A, 0, 0, 0, 8, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 9, 9, 9, 9, (byte)'W', (byte)'E', (byte)'B', (byte)'P' }, true)]
    [InlineData(new byte[] { 0, 0, 0, 0x24, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'h', (byte)'e', (byte)'i', (byte)'c' }, true)]
    [InlineData(new byte[] { 0, 0, 0, 0x1C, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'a', (byte)'v', (byte)'i', (byte)'f' }, true)]
    [InlineData(new byte[] { 0, 0, 0, 0x1C, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m' }, false)]   // an MP4, not a picture
    [InlineData(new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 9, 9, 9, 9, (byte)'W', (byte)'A', (byte)'V', (byte)'E' }, false)]
    [InlineData(new byte[] { (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o', 0, 0, 0, 0, 0, 0, 0 }, false)]
    public void Patterns_RouteThePictureFormats_AndNothingElse(byte[] head, bool matches)
    {
        bool any = ImageIOFormats.Patterns.Any(p => head.Length >= p.Offset + p.Signature.Length
            && Enumerable.Range(0, p.Signature.Length).All(i => (head[p.Offset + i] & p.Mask[i]) == (p.Signature[i] & p.Mask[i])));
        Assert.Equal(matches, any);
        Assert.All(ImageIOFormats.Patterns, p => Assert.Equal(p.Signature.Length, p.Mask.Length));
    }

    [Fact]
    public void Compression_IsTheQualityOverAHundred_AndZeroLeavesImageIOsDefault()
    {
        Assert.Null(ImageIOFormats.Compression(0));
        Assert.Equal(0.85, ImageIOFormats.Compression(ImageFile.JpegQuality));
        Assert.Equal(1.0, ImageIOFormats.Compression(250));
        Assert.Equal(0.01, ImageIOFormats.Compression(1));
    }

    [Fact]
    public void Read_TakesTheQuality_TheInterlace_OrTheMacOptionsWhole()
    {
        Assert.Equal(new ImageIOEncoderOptions(85), ImageIOFormats.Read(new JpegEncoderOptions(85, ChromaSubsampleMode.Default, false)));
        Assert.Equal(new ImageIOEncoderOptions(Interlace: true), ImageIOFormats.Read(new PngEncoderOptions(PngFilter.Unspecified, true)));
        Assert.Equal(new ImageIOEncoderOptions(), ImageIOFormats.Read(null));
        var mine = new ImageIOEncoderOptions(70, true, 300, ["System.Copyright"]);
        Assert.Same(mine, ImageIOFormats.Read(mine));
    }

    [Fact]
    public void KeysFor_MapsWicsPolicyNames_ToImageIOsKeys_SubjectHasNone()
    {
        Assert.Empty(ImageIOFormats.KeysFor(ImageEditor.MetadataNamesOf(ImageMetadataPolicy.None)));
        var basic = ImageIOFormats.KeysFor(ImageEditor.MetadataNamesOf(ImageMetadataPolicy.Basic));
        var all = ImageIOFormats.KeysFor(ImageEditor.MetadataNamesOf(ImageMetadataPolicy.All));

        Assert.Equal(ImageEditor.BasicMetadata.Count - 1, basic.Count);   // System.Subject is Windows' XPSubject: no ImageIO property
        Assert.Contains(("kCGImagePropertyTIFFDictionary", "kCGImagePropertyTIFFCopyright"), basic);
        Assert.DoesNotContain(basic, k => k.Dictionary == "kCGImagePropertyGPSDictionary");
        Assert.Equal(basic.Count + ImageEditor.GpsMetadata.Count, all.Count);
        Assert.Contains(("kCGImagePropertyGPSDictionary", "kCGImagePropertyGPSLatitude"), all);
        Assert.All(ImageEditor.BasicMetadata.Concat(ImageEditor.GpsMetadata).Where(n => n != "System.Subject"), n => Assert.True(ImageIOFormats.MetadataKeys.ContainsKey(n), n));
    }

    [Fact]
    public void OrientationExif_IsTheBareTiff_OnlyForATurn()
    {
        Assert.Null(ImageIOFormats.OrientationExif(1));
        Assert.Null(ImageIOFormats.OrientationExif(0));
        Assert.Null(ImageIOFormats.OrientationExif(9));
        Assert.Equal(MetadataStripper.OrientationTiff(6), ImageIOFormats.OrientationExif(6));
    }

    [Theory]
    [InlineData(0u, false)]   // none
    [InlineData(1u, true)]    // premultiplied last
    [InlineData(2u, true)]    // premultiplied first
    [InlineData(3u, true)]    // last
    [InlineData(4u, true)]    // first
    [InlineData(5u, false)]   // none, skip last
    [InlineData(6u, false)]   // none, skip first
    [InlineData(7u, true)]    // alpha only
    [InlineData(6u | (2u << 12), false)]   // the byte-order bits do not count
    public void HasAlpha_IsAnAlphaChannel_NotASkippedByte(uint alphaInfo, bool expected) => Assert.Equal(expected, ImageIOFormats.HasAlpha(alphaInfo));

    [Fact]
    public void TheShuffles_DropAndRestoreTheFourthByte()
    {
        byte[] bgrx = [1, 2, 3, 99, 4, 5, 6, 99];
        var bgr = new byte[6];
        ImageIOFormats.BgrxToBgr(bgrx, bgr, 2);
        Assert.Equal([1, 2, 3, 4, 5, 6], bgr);

        var back = new byte[8];
        ImageIOFormats.BgrToBgrx(bgr, back, 2);
        Assert.Equal([1, 2, 3, 255, 4, 5, 6, 255], back);
    }
}
