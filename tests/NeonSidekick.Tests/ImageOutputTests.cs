using NeonSidekick.Images;

namespace NeonSidekick.Tests;

/// <summary><see cref="ImageOutput"/> and <see cref="ImageFormats"/>' pure side: the format chosen, the name made, the clash numbering (2026-10-04).</summary>
public sealed class ImageOutputTests
{
    private static bool All(ImageFormat _) => true;

    private static bool NoJxl(ImageFormat format) => format != ImageFormats.Jxl;

    [Fact]
    public void FormatFor_TheArgument_ThenTo_ThenTheSource_ThenPng()
    {
        Assert.Equal(ImageFormats.Gif, ImageOutput.FormatFor(ImageFormats.Gif, "out.png", false, "image/jpeg", All));
        Assert.Equal(ImageFormats.Png, ImageOutput.FormatFor(null, "out.png", false, "image/jpeg", All));
        Assert.Equal(ImageFormats.Jpeg, ImageOutput.FormatFor(null, "thumbs", true, "image/jpeg", All));      // a folder names no format
        Assert.Equal(ImageFormats.Jpeg, ImageOutput.FormatFor(null, null, false, "image/jpeg", All));
        Assert.Equal(ImageFormats.Png, ImageOutput.FormatFor(null, null, false, "image/webp", All));          // WebP never writes: PNG, lossless
        Assert.Equal(ImageFormats.Png, ImageOutput.FormatFor(null, null, false, "image/jxl", NoJxl));         // a source this Windows cannot write
    }

    [Fact]
    public void OutputFor_NoTo_BesideTheSource_EditedOrConverted()
    {
        Assert.Equal(("photos/cat-edited.png", (string?)null), ImageOutput.OutputFor(null, false, "photos/cat.png", ImageFormats.Png, onlyFormatChanged: false, null));
        Assert.Equal(("photos/cat.png", (string?)null), ImageOutput.OutputFor(null, false, "photos/cat.webp", ImageFormats.Png, onlyFormatChanged: true, null));
        Assert.Equal(("cat-edited.jpg", (string?)null), ImageOutput.OutputFor("", false, "cat.jpeg", ImageFormats.Jpeg, onlyFormatChanged: false, ""));
    }

    [Fact]
    public void OutputFor_TheDefaultFolder_TakesTheNameWhenThereIsNoTo()
    {
        Assert.Equal(("edited/cat-edited.png", (string?)null), ImageOutput.OutputFor(null, false, "photos/cat.png", ImageFormats.Png, false, "edited"));
        Assert.Equal(("edited/sub/cat.png", (string?)null), ImageOutput.OutputFor(null, false, "cat.bmp", ImageFormats.Png, true, @"edited\sub\"));
        Assert.Equal(("cat-edited.png", (string?)null), ImageOutput.OutputFor(null, false, "photos/cat.png", ImageFormats.Png, false, "."));
        // An explicit to wins over the setting, file or folder.
        Assert.Equal(("out/cat-edited.png", (string?)null), ImageOutput.OutputFor("out", true, "photos/cat.png", ImageFormats.Png, false, "edited"));
        Assert.Equal(("final.png", (string?)null), ImageOutput.OutputFor("final.png", false, "photos/cat.png", ImageFormats.Png, false, "edited"));
    }

    [Fact]
    public void OutputFor_AFileTo_IsKept_TheExtensionAdded_AClashRefused()
    {
        Assert.Equal(("out/small.png", (string?)null), ImageOutput.OutputFor("out/small.png", false, "cat.jpg", ImageFormats.Png, false, null));
        Assert.Equal(("out/small.jpg", (string?)null), ImageOutput.OutputFor("out/small", false, "cat.jpg", ImageFormats.Jpeg, false, null));
        Assert.Equal(("small.JPEG", (string?)null), ImageOutput.OutputFor("small.JPEG", false, "cat.png", ImageFormats.Jpeg, false, null));
        Assert.Equal((null, "Error: \"to\" 'small.png' has another extension than JPEG (.jpg); give a matching name or leave \"format\" out"),
            ImageOutput.OutputFor("small.png", false, "cat.png", ImageFormats.Jpeg, false, null));
        Assert.Equal((null, "Error: \"to\" 'small.webp' has an extension no picture format here writes; give \"format\" or use one of .png, .jpg, .gif, .bmp, .tif, .jxl, .heic"),
            ImageOutput.OutputFor("small.webp", false, "cat.png", ImageFormats.Png, false, null));
        // A trailing slash is a folder even before it exists.
        Assert.Equal(("new/cat-edited.png", (string?)null), ImageOutput.OutputFor("new/", false, "cat.png", ImageFormats.Png, false, null));
    }

    [Fact]
    public void Numbered_AndSamePath()
    {
        Assert.Equal("a/cat-edited.png", ImageOutput.Numbered("a/cat-edited.png", 1));
        Assert.Equal("a/cat-edited-2.png", ImageOutput.Numbered("a/cat-edited.png", 2));
        Assert.Equal("a/cat-edited-13.png", ImageOutput.Numbered("a/cat-edited.png", 13));
        Assert.True(ImageOutput.SamePath(@"Photos\Cat.PNG", "photos/cat.png"));
        Assert.False(ImageOutput.SamePath("photos/cat.png", "photos/cat-edited.png"));
    }

    [Fact]
    public void Formats_ByName_ByExtension_ByMimeType()
    {
        Assert.Equal(ImageFormats.Jpeg, ImageFormats.ByName("JPG"));
        Assert.Equal(ImageFormats.Tiff, ImageFormats.ByName(".tif"));
        Assert.Equal(ImageFormats.Heif, ImageFormats.ByName("heic"));
        Assert.Null(ImageFormats.ByName("webp"));
        Assert.Null(ImageFormats.ByName(""));
        Assert.Equal(ImageFormats.Jpeg, ImageFormats.ByExtension("a/b.JPEG"));
        Assert.Null(ImageFormats.ByExtension("a/b"));
        Assert.Equal(ImageFormats.Png, ImageFormats.ByMimeType("IMAGE/PNG"));
        Assert.Null(ImageFormats.ByMimeType("image/webp"));
        Assert.Equal("png, jpeg, gif, bmp, tiff, jxl, heif", ImageFormats.Choices);
        Assert.Equal(".jpg", ImageFormats.Jpeg.Extension);
        Assert.Equal("JPEG", ImageFormats.Jpeg.Label);
    }

    [Fact]
    public void Words_ParseTheirChoices_AndTheMetadataSetting()
    {
        Assert.True(ImageWords.TryParseFit(" Cover ", out var fit) && fit == ImageFit.Cover);
        Assert.True(ImageWords.TryParseFit("max", out fit) && fit == ImageFit.Shrink);
        Assert.False(ImageWords.TryParseFit("zoom", out _));
        Assert.True(ImageWords.TryParseAnchor("top_left", out var anchor) && anchor == ImageAnchor.TopLeft);
        Assert.True(ImageWords.TryParseAnchor("bottom right", out anchor) && anchor == ImageAnchor.BottomRight);
        Assert.True(ImageWords.TryParseChroma("4:4:4", out var chroma) && chroma == ImageChroma.Subsample444);
        Assert.True(ImageWords.TryParseInterpolation("bicubic", out string name) && name == "cubic");
        Assert.True(ImageWords.TryParseDither("diffuse", out var dither) && dither == PhotoSauce.MagicScaler.DitherMode.ErrorDiffusion);
        Assert.True(ImageColor.TryParseFilter("grayscale", out var filter) && filter == ImageFilter.Grey);
        Assert.Equal(ImageMetadataPolicy.Basic, ImageWords.MetadataOf("Basic"));
        Assert.Equal(ImageMetadataPolicy.None, ImageWords.MetadataOf("everything"));   // unknown is the safe reading
        Assert.Equal("all", ImageWords.MetadataName("ALL"));
        Assert.Equal("none", ImageWords.MetadataName(null));
    }
}
