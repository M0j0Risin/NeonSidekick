using NeonSidekick.App;
using NeonSidekick.Images;
using NeonSidekick.Settings;

namespace NeonSidekick.Tests;

/// <summary>The picture menu's edits (<see cref="PictureActions"/>, 2026-10-04) on real temp files: the requests, where each mode writes, what is refused.</summary>
public sealed class PictureActionsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", "picture-actions-" + Guid.NewGuid().ToString("N"));

    public PictureActionsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static readonly PictureEditSettings Beside = PictureEditSettings.Default;
    private static readonly PictureEditSettings Overwrite = PictureEditSettings.Default with { Mode = ImageEditMode.OverwriteOriginal };

    private string Put(string name, int width = 40, int height = 20)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, SmokeChecks.SolidBmp(width, height));
        return path;
    }

    private static (int Width, int Height) SizeOf(string path) => ImageEditor.Info(File.ReadAllBytes(path)) is { } info ? (info.Width, info.Height) : (0, 0);

    [Fact]
    public void RequestFor_EachCommandIsOneFixedRequest()
    {
        Assert.Equal(90, PictureActions.RequestFor(PictureCommand.RotateRight)!.Rotate);
        Assert.Equal(270, PictureActions.RequestFor(PictureCommand.RotateLeft)!.Rotate);
        Assert.Equal(180, PictureActions.RequestFor(PictureCommand.Rotate180)!.Rotate);
        Assert.Equal(ImageFlip.Vertical, PictureActions.RequestFor(PictureCommand.FlipVertical)!.Flip);
        Assert.Equal(ImageFilter.Sepia, PictureActions.RequestFor(PictureCommand.Sepia)!.Filter);
        Assert.Equal(0.25, PictureActions.RequestFor(PictureCommand.Quarter)!.Scale);
        var fit = PictureActions.RequestFor(PictureCommand.Fit1024)!;
        Assert.Equal((1024, 1024, ImageFit.Shrink), (fit.Width, fit.Height, fit.Fit));
        Assert.Equal(ImageFormats.Jpeg, PictureActions.RequestFor(PictureCommand.ToJpeg)!.Format);
        Assert.Equal(500, PictureActions.RequestFor(PictureCommand.Under500Kb)!.MaxKb);
        Assert.Equal(ImageMetadataPolicy.Basic, PictureActions.RequestFor(PictureCommand.Grey, ImageMetadataPolicy.Basic)!.Metadata);
        Assert.Null(PictureActions.RequestFor(PictureCommand.Delete));
        Assert.True(PictureActions.IsEdit(PictureCommand.Under200Kb));
        Assert.False(PictureActions.IsEdit(PictureCommand.OpenInViewer));
    }

    [Fact]
    public void Beside_WritesANewFile_EditedThenNumbered_TheSourceUntouched()
    {
        string source = Put("cat.bmp");
        byte[] before = File.ReadAllBytes(source);

        var first = PictureActions.Edit(source, PictureCommand.RotateRight, Beside);
        var second = PictureActions.Edit(source, PictureCommand.RotateRight, Beside);

        Assert.False(first.Failed, first.Line);
        Assert.Equal(Path.Combine(_dir, "cat-edited.bmp"), first.Written);
        Assert.Equal(Path.Combine(_dir, "cat-edited-2.bmp"), second.Written);
        Assert.Equal((20, 40), SizeOf(first.Written!));
        Assert.Equal(before, File.ReadAllBytes(source));
        Assert.False(first.Replaced);
        Assert.Null(first.Removed);
        Assert.StartsWith("wrote cat-edited.bmp (BMP, 20×40 from 40×20", first.Line, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Beside_AConversion_IsTheStemInTheNewFormat_TheSourceKept()
    {
        string source = Put("dog.bmp");

        var outcome = PictureActions.Edit(source, PictureCommand.ToPng, Beside);

        Assert.Equal(Path.Combine(_dir, "dog.png"), outcome.Written);
        Assert.True(File.Exists(source));
        Assert.Equal("image/png", ImageEditor.Info(File.ReadAllBytes(outcome.Written!))!.MimeType);
    }

    [Fact]
    public void Overwrite_ReplacesTheSourceInPlace()
    {
        string source = Put("cat.bmp");

        var outcome = PictureActions.Edit(source, PictureCommand.Rotate180, Overwrite);
        var turned = PictureActions.Edit(source, PictureCommand.RotateLeft, Overwrite);

        Assert.True(outcome.Replaced);
        Assert.Equal(source, outcome.Written);
        Assert.StartsWith("replaced cat.bmp (BMP", outcome.Line, StringComparison.Ordinal);
        Assert.Equal((20, 40), SizeOf(source));
        Assert.True(turned.Replaced);
        Assert.Equal(["cat.bmp"], Directory.GetFiles(_dir).Select(Path.GetFileName));   // no temp file, no copy
    }

    [Fact]
    public void Overwrite_AConversion_WritesTheNewName_AndDeletesTheSource()
    {
        string source = Put("cat.bmp");
        File.WriteAllBytes(Path.Combine(_dir, "cat.png"), [1, 2, 3]);   // another file already has the name: never written over

        var outcome = PictureActions.Edit(source, PictureCommand.ToPng, Overwrite);

        Assert.False(outcome.Failed, outcome.Line);
        Assert.Equal(Path.Combine(_dir, "cat-2.png"), outcome.Written);
        Assert.Equal(source, outcome.Removed);
        Assert.False(File.Exists(source));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(_dir, "cat.png")));
        Assert.EndsWith("; deleted cat.bmp (Image edit mode: overwrite-original)", outcome.Line, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingWouldChange_IsRefused_AndNothingWritten()
    {
        string source = Put("small.bmp", 40, 20);

        var fits = PictureActions.Edit(source, PictureCommand.Fit512, Beside);
        var format = PictureActions.Edit(source, PictureCommand.ToBmp, Beside);
        var under = PictureActions.Edit(source, PictureCommand.Under200Kb, Beside);
        var gone = PictureActions.Edit(Path.Combine(_dir, "gone.bmp"), PictureCommand.Grey, Beside);

        Assert.True(fits.Failed);
        Assert.Equal("Error: the picture is 40×20, already within 512 px; nothing was written", fits.Line);
        Assert.Equal("Error: the picture is already BMP; nothing was written", format.Line);
        Assert.StartsWith("Error: the file is ", under.Line, StringComparison.Ordinal);
        Assert.EndsWith("already under 200 KB; nothing was written", under.Line, StringComparison.Ordinal);
        Assert.Equal("Error: gone.bmp is no longer there", gone.Line);
        Assert.Equal(["small.bmp"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void ABigPicture_FitsTheBox_AndANotPicture_IsRefused()
    {
        string source = Put("wide.bmp", 1200, 300);
        File.WriteAllText(Path.Combine(_dir, "notes.png"), "not a picture");

        var fit = PictureActions.Edit(source, PictureCommand.Fit1024, Beside);
        var bad = PictureActions.Edit(Path.Combine(_dir, "notes.png"), PictureCommand.Grey, Beside);

        Assert.Equal((1024, 256), SizeOf(fit.Written!));
        Assert.Equal(ImageText.NotAnImage, bad.Line);
    }

    [Fact]
    public void StripMetadata_Beside_WritesALosslessCopy_TheSourceUntouched()
    {
        string source = Path.Combine(_dir, "geo.jpeg");
        byte[] before = ImageFixtures.ExifJpeg();
        File.WriteAllBytes(source, before);

        var outcome = PictureActions.Edit(source, PictureCommand.StripMetadata, Beside with { Metadata = ImageMetadataPolicy.All });

        Assert.False(outcome.Failed, outcome.Line);
        Assert.Equal(Path.Combine(_dir, "geo-edited.jpeg"), outcome.Written);
        Assert.StartsWith("wrote geo-edited.jpeg (JPEG, lossless: removed EXIF with GPS; ", outcome.Line, StringComparison.Ordinal);
        byte[] after = File.ReadAllBytes(outcome.Written!);
        Assert.False(ImageFixtures.HasGps(after));   // the metadata setting does not apply: the row takes it all
        Assert.Equal(StripFixtures.Scan(before), StripFixtures.Scan(after));
        Assert.Equal(before, File.ReadAllBytes(source));
        Assert.True(PictureActions.IsEdit(PictureCommand.StripMetadata));
        Assert.Null(PictureActions.RequestFor(PictureCommand.StripMetadata));
    }

    [Fact]
    public void StripMetadata_Overwrite_ReplacesTheSource_AndTheRefusals()
    {
        string source = Path.Combine(_dir, "geo.jpg");
        File.WriteAllBytes(source, ImageFixtures.ExifJpeg());

        var outcome = PictureActions.Edit(source, PictureCommand.StripMetadata, Overwrite);
        var again = PictureActions.Edit(source, PictureCommand.StripMetadata, Overwrite);
        var bmp = PictureActions.Edit(Put("cat.bmp"), PictureCommand.StripMetadata, Beside);

        Assert.True(outcome.Replaced);
        Assert.StartsWith("replaced geo.jpg (JPEG, lossless", outcome.Line, StringComparison.Ordinal);
        Assert.False(ImageFixtures.HasGps(File.ReadAllBytes(source)));
        Assert.Equal(ImageText.NoMetadata("geo.jpg"), again.Line);
        Assert.Equal(ImageText.CannotStripLosslessly("BMP"), bmp.Line);
        Assert.Equal(["cat.bmp", "geo.jpg"], Directory.GetFiles(_dir).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void Settings_FromTheEffectiveSettings()
    {
        var settings = PictureEditSettings.From(new AppSettingsData { ImageEditQuality = 500, ImageEditMetadata = "all", ImageEditMode = "overwrite-original" });

        Assert.Equal(new PictureEditSettings(100, ImageMetadataPolicy.All, ImageEditMode.OverwriteOriginal), settings);
        Assert.Equal(ImageEditMode.BesideOriginal, PictureEditSettings.From(new AppSettingsData()).Mode);
        Assert.Equal(["beside-original", "overwrite-original"], ImageWords.EditModeNames);
        Assert.Equal(ImageEditMode.OverwriteOriginal, ImageWords.EditModeOf(" OVERWRITE_original "));
        Assert.Equal(ImageEditMode.BesideOriginal, ImageWords.EditModeOf(null));
    }
}
