using System.Drawing;
using System.Text;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Images;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// Pictures after the 2026-10-22 fixes (the fix plan's Phase 9): a PNG's and WebP's own EXIF orientation read, written back by a strip and
/// applied by an edit; every picture sent to a model without its metadata; JFIF's thumbnail, ImageMagick's raw profiles, XMP's location, a
/// GIF's unknown extensions and stray bytes in a JPEG all handled; a WebP's odd chunk padded; the colours CSS writes; the pads and the
/// matte one colour; the tools off the turn's thread, each picture once, the "more" line capped, a locked file not called broken.
/// </summary>
public sealed class PictureFixesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", "pictures-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly WorkingDirectory _files;

    public PictureFixesTests()
    {
        _root = Path.Combine(_dir, "files");
        Directory.CreateDirectory(_root);
        _files = new WorkingDirectory(() => _root, new ManualTimeProvider());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static byte[] PngWithOrientation(int orientation) =>
        StripFixtures.BeforeIend(StripFixtures.Png(), StripFixtures.Chunk("eXIf", StripFixtures.Tiff((0x0112, (ushort)orientation, null))));

    [WindowsFact]
    public void APngsOwnOrientation_IsRead_KeptByAStrip_AndAppliedByAnEdit()
    {
        byte[] png = PngWithOrientation(6);
        Assert.Equal(6, MetadataStripper.ExifOrientation(png));
        Assert.Null(MetadataStripper.ExifOrientation(StripFixtures.Png()));

        var info = ImageEditor.Info(png)!;
        Assert.Equal(6, (int)info.Orientation);

        byte[] upright = ImageEditor.Upright(PngWithOrientation(6));
        Assert.Null(MetadataStripper.ExifOrientation(upright));
        Assert.Equal(1, (int)ImageEditor.Info(upright)!.Orientation);
    }

    [WindowsFact]
    public void APictureSentToAModel_HasNoMetadata()
    {
        byte[] png = StripFixtures.BeforeIend(StripFixtures.Png(), StripFixtures.Chunk("tEXt", "Comment\0secret place"u8));
        Assert.True(ImageFile.TryLoad(png, "a.png", out var image, out _));
        Assert.Equal(MetadataKinds.None, MetadataStripper.Survey(image!.Bytes)!.Found);
        Assert.DoesNotContain("secret", Encoding.ASCII.GetString(image.Bytes), StringComparison.Ordinal);

        Assert.True(ImageFile.TryLoad(PngWithOrientation(6), "b.png", out var turned, out _));
        Assert.Equal(6, MetadataStripper.ExifOrientation(turned!.Bytes));   // its orientation kept for a viewer that reads it
    }

    [Fact]
    public void APhotoFormat_GoesAsAJpeg_OnEverySystem()
    {
        Assert.Equal(ImageFile.Jpeg, ImageFile.SentAs("image/heic"));
        Assert.Equal(ImageFile.Jpeg, ImageFile.SentAs("image/jxl"));
        Assert.Equal(ImageFile.Png, ImageFile.SentAs("image/tiff"));
    }

    [Fact]
    public void JfifsOwnThumbnail_IsTakenOut()
    {
        byte[] jfif = [.. "JFIF\0"u8.ToArray(), 1, 2, 0, 0, 1, 0, 1, 1, 1, 0xAA, 0xBB, 0xCC];
        byte[] jpeg = [0xFF, 0xD8, .. StripFixtures.Segment(0xE0, jfif), .. StripFixtures.Segment(0xDA, [1, 1, 0, 0, 63, 0]), 0x12, 0xFF, 0xD9];
        Assert.True(MetadataStripper.Survey(jpeg)!.Found.HasFlag(MetadataKinds.Thumbnail));
        var (result, _) = MetadataStripper.Strip(jpeg);
        Assert.Equal([0xFF, 0xE0, 0x00, 0x10], result!.Bytes.AsSpan(2, 4).ToArray());
        Assert.DoesNotContain((byte)0xAA, result.Bytes);
    }

    [Fact]
    public void StrayBytesBetweenJpegSegments_AreSkipped()
    {
        byte[] jpeg = [0xFF, 0xD8, .. StripFixtures.Segment(0xFE, "note"u8), 0x00, 0x00, .. StripFixtures.Segment(0xDA, [1, 1, 0, 0, 63, 0]), 0x12, 0xFF, 0xD9];
        var (result, error) = MetadataStripper.Strip(jpeg);
        Assert.Null(error);
        Assert.Equal(MetadataKinds.Comment, result!.Removed);
    }

    [Fact]
    public void RawProfiles_AndXmpsLocation_AreReported()
    {
        byte[] png = StripFixtures.BeforeIend(StripFixtures.Png(), StripFixtures.Chunk("tEXt", "Raw profile type exif\0\nexif\n  8\n45786966"u8));
        Assert.True(MetadataStripper.Survey(png)!.Found.HasFlag(MetadataKinds.Exif));
        byte[] xmp = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0<x:xmpmeta><rdf:Description exif:GPSLatitude=\"30,1N\"/></x:xmpmeta>");
        byte[] jpeg = StripFixtures.AfterSoi(StripFixtures.Jpeg(), StripFixtures.Segment(0xE1, xmp));
        Assert.True(MetadataStripper.Survey(jpeg)!.Found.HasFlag(MetadataKinds.Gps));
    }

    [Fact]
    public void AGifsUnknownExtension_IsTakenOut()
    {
        byte[] gif = [.. "GIF89a"u8.ToArray(), 1, 0, 1, 0, 0, 0, 0, 0x21, 0x2A, 3, (byte)'a', (byte)'b', (byte)'c', 0, 0x2C, 0, 0, 0, 0, 1, 0, 1, 0, 0, 2, 1, 0, 0, 0x3B];
        var (result, _) = MetadataStripper.Strip(gif);
        Assert.Equal(MetadataKinds.Vendor, result!.Removed);
        Assert.Equal(-1, result.Bytes.AsSpan().IndexOf((ReadOnlySpan<byte>)[0x21, 0x2A]));
    }

    [Fact]
    public void AWebPsUnpaddedOddChunk_IsPadded_BeforeTheOrientationGoesIn()
    {
        byte[] vp8l = [.. "VP8L"u8.ToArray(), 13, 0, 0, 0, .. StripFixtures.Vp8l];   // odd, no pad: the file's last chunk
        byte[] webp = StripFixtures.WebP(StripFixtures.Vp8x(0x08), StripFixtures.RiffChunk("EXIF", StripFixtures.Tiff((0x0112, 6, null))), vp8l);
        var (result, _) = MetadataStripper.Strip(webp, 6);
        Assert.Equal(["VP8X", "VP8L", "EXIF"], StripFixtures.WebPChunks(result!.Bytes));
        Assert.Equal(6, MetadataStripper.ExifOrientation(result.Bytes));
    }

    [Fact]
    public void AnEightDigitHexColor_IsRrggbbaa()
    {
        Assert.True(ImageColor.TryParseColor("#11223344", out var colour));
        Assert.Equal((0x11, 0x22, 0x33, 0x44), (colour.R, colour.G, colour.B, colour.A));
    }

    [Fact]
    public void CannotFit_OffersColors_OnlyWhereTheyAreTaken()
    {
        Assert.DoesNotContain("colors", ImageText.CannotFit(100, 150_000, ImageFormats.Bmp), StringComparison.Ordinal);
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Contains("colors", ImageText.CannotFit(100, 150_000, ImageFormats.Png), StringComparison.Ordinal);
        }
    }

    [WindowsFact]
    public void AJpegsPads_AreTheMatte_NeverBlack()
    {
        byte[] source = ImageFixtures.Solid(8, 4, ImageFixtures.Red);
        var request = new ImageEditRequest { Width = 8, Height = 8, Fit = ImageFit.Pad, Background = Color.FromArgb(0, 0, 0, 0), Pad = 2 };
        var (result, error) = ImageEditor.Apply(source, request, ImageFormats.Jpeg);
        Assert.Null(error);
        var corner = ImageFixtures.Pixel(result!.Bytes, 0, 0);
        Assert.True(corner.R > 200 && corner.G > 200 && corner.B > 200, corner.ToString());
    }

    [WindowsFact]
    public async Task ViewImage_TakesEachPictureOnce_AndLeavesBlanksOut()
    {
        File.WriteAllBytes(Path.Combine(_root, "a.png"), StripFixtures.Png());
        var tool = new ViewImageTool(_files, () => new AppSettingsData());
        var (text, images) = tool.Load(["a.png", " ", "a.png", "A.PNG"], CancellationToken.None);
        Assert.Single(images);
        Assert.Single(text.Split('\n'));
        var arguments = new AIFunctionArguments { ["paths"] = new[] { "a.png" } };
        Assert.IsType<ToolImageResult>(await tool.InvokeAsync(arguments, CancellationToken.None));
    }

    [Fact]
    public void TheMoreLine_NamesTwentyAtMost()
    {
        string line = FileText.MorePictures(Enumerable.Range(0, 30).Select(i => i + ".png").ToList());
        Assert.Contains("19.png, and 10 more", line, StringComparison.Ordinal);
        Assert.DoesNotContain("20.png", line, StringComparison.Ordinal);
    }

    [WindowsFact]
    public void ALockedPicture_IsSaidToBeUnreadable_NotBroken()
    {
        string full = Path.Combine(_root, "busy.png");
        File.WriteAllBytes(full, StripFixtures.Png());
        using (new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = _files.ReadImage("busy.png");
            Assert.Equal(FileOutcome.Failed, result.Outcome);
        }

        File.WriteAllBytes(Path.Combine(_root, "junk.png"), "not a picture"u8.ToArray());
        Assert.Equal(FileOutcome.NotAnImage, _files.ReadImage("junk.png").Outcome);
    }

    [Fact]
    public void ImageInfo_DescribesTwentyAtMost_AndHonorsEsc()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ImageInfoTool.Describe(_files, ["a.png"], new AppSettingsData(), null, cancelled.Token));
        string many = ImageInfoTool.Describe(_files, [.. Enumerable.Range(0, 25).Select(i => i + ".png")], new AppSettingsData());
        Assert.Contains("5 more not shown", many, StringComparison.Ordinal);
    }
}
