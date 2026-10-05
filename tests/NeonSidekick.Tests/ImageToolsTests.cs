using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Images;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary><c>image_info</c> and <c>image_edit</c> (2026-10-04) over a real temp sandbox: the schemas, the names written, the refusals, the settings read at each call.</summary>
public sealed class ImageToolsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly WorkingDirectory _files;
    private readonly AppSettingsData _settings = new();
    private readonly IReadOnlyList<AIFunction> _tools;

    public ImageToolsTests()
    {
        _root = Path.Combine(_dir, "files");
        _files = new WorkingDirectory(() => _root, new ManualTimeProvider());
        _tools = ChatScreen.FileTools(_files, () => true, _ => { }, () => _settings);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // The sandbox names paths with the OS's separator, as every file tool's sentence does.
    private static string Rel(string path) => path.Replace('/', Path.DirectorySeparatorChar);

    private static void Starts(string expected, string actual) => Assert.True(actual.StartsWith(expected, StringComparison.Ordinal), actual);

    private static void Ends(string expected, string actual) => Assert.True(actual.EndsWith(expected, StringComparison.Ordinal), actual);

    private AIFunction Edit => _tools.Single(t => t.Name == ImageEditTool.ToolName);

    private AIFunction Info => _tools.Single(t => t.Name == ImageInfoTool.ToolName);

    private static AIFunctionArguments Args(params (string Name, object? Value)[] values) =>
        new(values.ToDictionary(v => v.Name, v => v.Value));

    private async Task<object?> Call(AIFunction tool, params (string Name, object? Value)[] values) =>
        await tool.InvokeAsync(Args(values), CancellationToken.None);

    private async Task<string> Text(AIFunction tool, params (string Name, object? Value)[] values) =>
        await Call(tool, values) switch
        {
            ToolImageResult image => image.Text,
            var other => ToolAnswers.Text(other),
        };

    private void Put(string relative, byte[] bytes)
    {
        string full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }

    private byte[] Get(string relative) => File.ReadAllBytes(Path.Combine(_root, relative));

    private bool Exists(string relative) => File.Exists(Path.Combine(_root, relative));

    [Fact]
    public void Schemas_ArePinned()
    {
        Assert.Equal(["path", "paths"], Info.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        Assert.Equal(
            ["path", "to", "overwrite", "format", "quality", "max_kb", "width", "height", "scale", "fit", "anchor", "interpolation",
             "crop_x", "crop_y", "crop_width", "crop_height", "rotate", "flip", "filter", "brightness", "contrast", "saturation", "hue",
             "tint", "tint_amount", "blur", "sharpen", "pad", "background", "metadata", "dpi", "chroma", "colors", "dither", "interlace", "view"],
            Edit.JsonSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        Assert.Equal(["path"], Edit.JsonSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.All(Edit.JsonSchema.GetProperty("properties").EnumerateObject(), p => Assert.False(string.IsNullOrWhiteSpace(p.Value.GetProperty("description").GetString())));
        Assert.Equal([0, 90, 180, 270], Edit.JsonSchema.GetProperty("properties").GetProperty("rotate").GetProperty("enum").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal(ImageFormats.All.Select(f => f.Name), Edit.JsonSchema.GetProperty("properties").GetProperty("format").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("never write a script", ImageEditTool.DescriptionText);
        Assert.Contains("in this order: crop, resize, rotate, flip, colour, blur, border", ImageEditTool.DescriptionText);
    }

    [Fact]
    public async Task Edit_WritesBesideTheSource_AsEdited_ThenNumbersAClash()
    {
        Put("photos/cat.bmp", ImageFixtures.Quadrants(40, 20));

        string first = await Text(Edit, ("path", "photos/cat.bmp"), ("width", 20));
        string second = await Text(Edit, ("path", "photos/cat.bmp"), ("width", 20));

        Assert.Matches(@"^wrote photos[/\\]cat-edited\.bmp \(BMP, 20×10 from 40×20, [\d.]+ [KB ]+\)$", first);
        Starts(Rel("wrote photos/cat-edited-2.bmp") + " (BMP, 20×10 from 40×20, ", second);
        Assert.Equal((20, 10, "image/bmp"), ImageFixtures.Size(Get("photos/cat-edited.bmp")));
    }

    [Fact]
    public async Task Edit_AFormatChangeAlone_KeepsTheStem()
    {
        Put("cat.bmp", ImageFixtures.Quadrants(8, 8));

        Starts("wrote cat.png (PNG, 8×8, ", await Text(Edit, ("path", "cat.bmp"), ("format", "png")));
        Starts("wrote cat.jpg (JPEG, 8×8, ", await Text(Edit, ("path", "cat.bmp"), ("to", "cat.jpg")));
        Assert.Equal("image/jpeg", ImageFixtures.Size(Get("cat.jpg")).Mime);
    }

    [Fact]
    public async Task Edit_AnExplicitTo_IsRefusedWhenTaken_UnlessOverwrite()
    {
        Put("cat.bmp", ImageFixtures.Quadrants(8, 8));
        Put("out/small.png", [1, 2, 3]);

        Assert.Equal(FileText.Exists(Rel("out/small.png")), await Text(Edit, ("path", "cat.bmp"), ("to", "out/small.png"), ("width", 4)));
        Starts(Rel("wrote out/small.png") + " (PNG, 4×4 from 8×8, ", await Text(Edit, ("path", "cat.bmp"), ("to", "out/small.png"), ("width", 4), ("overwrite", true)));
        Starts(Rel("wrote thumbs/cat-edited.png"), await Text(Edit, ("path", "cat.bmp"), ("to", "thumbs/"), ("format", "png"), ("width", 4)));
    }

    [Fact]
    public async Task Edit_TheSourceItself_NeedsOverwrite()
    {
        Put("cat.bmp", ImageFixtures.Quadrants(8, 8));

        Assert.Equal(ImageText.OverwriteSource, await Text(Edit, ("path", "cat.bmp"), ("to", "cat.bmp"), ("filter", "grey")));
        Starts("wrote cat.bmp (BMP, 8×8, ", await Text(Edit, ("path", "cat.bmp"), ("to", "cat.bmp"), ("filter", "grey"), ("overwrite", true)));
        var pixel = ImageFixtures.Pixel(Get("cat.bmp"), 1, 1);
        Assert.True(pixel.R == pixel.G && pixel.G == pixel.B);
    }

    [Fact]
    public async Task Edit_Refusals()
    {
        Put("cat.bmp", ImageFixtures.Quadrants(8, 8));
        Put("notes.png", "not a picture"u8.ToArray());

        Assert.Equal(FileText.OutsideRoot("../out.png"), await Text(Edit, ("path", "cat.bmp"), ("to", "../out.png"), ("width", 4)));
        Assert.Equal(FileText.NotAnImage("notes.png"), await Text(Edit, ("path", "notes.png"), ("width", 4)));
        Assert.Equal(FileText.Missing("gone.png"), await Text(Edit, ("path", "gone.png"), ("width", 4)));
        Assert.Equal(FileText.PathRequired("path"), await Text(Edit, ("width", 4)));
        Assert.Equal(ImageText.NothingToDo, await Text(Edit, ("path", "cat.bmp")));
        Assert.Equal(ImageText.Rotation("45"), await Text(Edit, ("path", "cat.bmp"), ("rotate", 45)));
        Assert.Equal(ImageText.CropIncomplete, await Text(Edit, ("path", "cat.bmp"), ("crop_x", 1), ("crop_y", 1)));
        Assert.Equal(ImageText.ScaleAndSize, await Text(Edit, ("path", "cat.bmp"), ("scale", 2), ("width", 4)));
        Assert.Equal(ImageText.BadColour("background", "mauvish"), await Text(Edit, ("path", "cat.bmp"), ("pad", 2), ("background", "mauvish")));
        Assert.Equal(FileText.BadChoice("format", "webp", ImageFormats.Choices), await Text(Edit, ("path", "cat.bmp"), ("format", "webp")));
        Assert.Equal(ImageText.OutOfRange("brightness", "150", -100, 100), await Text(Edit, ("path", "cat.bmp"), ("brightness", 150)));
        Assert.Equal(FileText.BadChoice("fit", "zoom", ImageWords.FitChoices), await Text(Edit, ("path", "cat.bmp"), ("width", 4), ("height", 4), ("fit", "zoom")));
        Assert.Equal("Error: chroma does not apply to BMP; it is for jpeg", await Text(Edit, ("path", "cat.bmp"), ("chroma", "444")));
        Assert.False(Exists("cat-edited.bmp"));
    }

    [Fact]
    public async Task Edit_RotateMinus90_IsAQuarterTurnAnticlockwise()
    {
        Put("cat.bmp", ImageFixtures.Quadrants(20, 10));

        Starts("wrote cat-edited.bmp (BMP, 10×20 from 20×10, ", await Text(Edit, ("path", "cat.bmp"), ("rotate", -90)));
        Assert.True(ImageFixtures.Near(ImageFixtures.Green, ImageFixtures.Pixel(Get("cat-edited.bmp"), 1, 1)));
    }

    [Fact]
    public async Task Edit_View_AttachesTheWrittenPicture()
    {
        Put("cat.bmp", ImageFixtures.Quadrants(8, 8));

        var answer = await Call(Edit, ("path", "cat.bmp"), ("format", "png"), ("width", 4), ("view", true));

        var image = Assert.IsType<ToolImageResult>(answer);
        Assert.Single(image.Images);
        Assert.Equal((4, 4), (image.Images[0].Width, image.Images[0].Height));
        Starts("wrote cat-edited.png (PNG, 4×4 from 8×8, ", image.Text);   // a resize too, so -edited
        Ends(FileText.ImageFollows, image.Text);
    }

    [Fact]
    public async Task Edit_TheSettings_AreReadAtEachCall_AndAnArgumentWins()
    {
        Put("cat.bmp", ImageFixtures.Noise(64, 64));
        Put("geo.jpg", ImageFixtures.ExifJpeg());

        _settings.ImageEditOutputFolder = "edited";
        Starts(Rel("wrote edited/cat-edited.bmp"), await Text(Edit, ("path", "cat.bmp"), ("width", 32)));
        Starts("wrote here.bmp", await Text(Edit, ("path", "cat.bmp"), ("width", 32), ("to", "here.bmp")));

        _settings.ImageEditQuality = 10;
        await Text(Edit, ("path", "cat.bmp"), ("to", "low.jpg"));
        await Text(Edit, ("path", "cat.bmp"), ("to", "high.jpg"), ("quality", 95));
        Assert.True(Get("low.jpg").Length < Get("high.jpg").Length);

        _settings.ImageEditMetadata = "all";
        await Text(Edit, ("path", "geo.jpg"), ("to", "kept.jpg"), ("filter", "grey"));
        await Text(Edit, ("path", "geo.jpg"), ("to", "dropped.jpg"), ("filter", "grey"), ("metadata", "none"));
        Assert.True(ImageFixtures.HasGps(Get("kept.jpg")));
        Assert.False(ImageFixtures.HasGps(Get("dropped.jpg")));
    }

    [Fact]
    public async Task Info_DescribesEachPicture_ThenTheFormatsAndDefaults()
    {
        Put("cat.bmp", ImageFixtures.Quadrants(30, 20));
        Put("anim.gif", ImageFixtures.AnimatedGif());
        Put("notes.png", "not a picture"u8.ToArray());
        _settings.ImageEditMetadata = "basic";

        string text = await Text(Info, ("path", "cat.bmp"), ("paths", new[] { "anim.gif", "notes.png", "gone.png" }));

        var lines = text.Split('\n');
        Assert.Equal(5, lines.Length);
        Assert.Matches(@"^cat\.bmp: BMP, 30×20, [\d.]+ [KB ]+$", lines[0]);
        Starts("anim.gif: GIF, 1×1, ", lines[1]);
        Assert.Contains(", 2 frames", lines[1]);
        Assert.Equal(FileText.NotAnImage("notes.png"), lines[2]);
        Assert.Equal(FileText.Missing("gone.png"), lines[3]);
        Assert.Equal(ImageText.Formats(ImageFormats.WritableFormats(), 90, "basic", ""), lines[4]);
        Ends("Defaults: quality 90, metadata basic, output beside the source", lines[4]);
        Assert.Equal(ImageText.NoPathError, await Text(Info));
    }

    [Fact]
    public void Read_AllTheArguments_IntoOneRequest()
    {
        var (request, reencodes, error) = ImageEditTool.Read(Args(
            ("width", 100), ("height", "50"), ("fit", "cover"), ("anchor", "top-left"), ("interpolation", "nearest"),
            ("crop_x", 1), ("crop_y", 2), ("crop_width", 30), ("crop_height", 40), ("rotate", 270), ("flip", "vertical"),
            ("filter", "sepia"), ("brightness", 10), ("contrast", -10), ("saturation", 20), ("hue", 45), ("tint", "#00ff00"), ("tint_amount", 30),
            ("blur", 1.5), ("sharpen", false), ("pad", 3), ("background", "black"), ("format", "jpg"), ("quality", 70), ("max_kb", 200),
            ("metadata", "basic"), ("dpi", 300), ("chroma", "444")));

        Assert.Null(error);
        Assert.True(reencodes);
        Assert.Equal((100, 50, ImageFit.Cover, ImageAnchor.TopLeft, "nearest"), (request!.Width, request.Height, request.Fit, request.Anchor, request.Interpolation));
        Assert.Equal(new System.Drawing.Rectangle(1, 2, 30, 40), request.Crop);
        Assert.Equal((270, ImageFlip.Vertical, ImageFilter.Sepia, 10, -10, 20, 45, 30), (request.Rotate, request.Flip, request.Filter, request.Brightness, request.Contrast, request.Saturation, request.Hue, request.TintAmount));
        Assert.Equal((1.5, (bool?)false, 3, ImageFormats.Jpeg, (int?)70, (int?)200), (request.Blur, request.Sharpen, request.Pad, request.Format, request.Quality, request.MaxKb));
        Assert.Equal((ImageMetadataPolicy.Basic, (int?)300, ImageChroma.Subsample444), (request.Metadata, request.Dpi, request.Chroma));

        var (plain, plainReencodes, _) = ImageEditTool.Read(Args(("width", 10)), ImageMetadataPolicy.All);
        Assert.False(plainReencodes);
        Assert.Equal(ImageMetadataPolicy.All, plain!.Metadata);   // the setting, when the call says nothing
        Assert.True(plain.ChangesPixels);
        Assert.False(new ImageEditRequest().ChangesPixels);
    }

    [Fact]
    public void QualityOf_ClampsAHandEditedValue()
    {
        Assert.Equal(90, ImageEditTool.QualityOf(new AppSettingsData()));
        Assert.Equal(1, ImageEditTool.QualityOf(new AppSettingsData { ImageEditQuality = -5 }));
        Assert.Equal(100, ImageEditTool.QualityOf(new AppSettingsData { ImageEditQuality = 500 }));
    }
}
