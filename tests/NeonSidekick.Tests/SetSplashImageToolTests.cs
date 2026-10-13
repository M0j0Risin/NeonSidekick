using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Images;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>set_splash_image</c> (2026-09-24) over a real temp sandbox. One of the picture tools since 2026-10-12 (the user's call; the ComfyUI
/// group's until then, these tests <c>ComfyTests</c>' with it), its sentences <see cref="ImageText"/>'s.
/// </summary>
public sealed class SetSplashImageToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly WorkingDirectory _files;

    public SetSplashImageToolTests()
    {
        _root = Path.Combine(_dir, "files");
        Directory.CreateDirectory(_root);
        _files = new WorkingDirectory(() => _root, new ManualTimeProvider());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static byte[] Picture() => SmokeChecks.SolidBmp(8, 8);

    [Fact]
    public void SetSplashImage_CopiesIntoTheSplashFolder_WarnsWhenItIsTheOnlyOne_AndNeverOverwrites()
    {
        string splash = Path.Combine(_dir, "profile", "images", "splash_images");
        File.WriteAllBytes(Path.Combine(_root, "art.bmp"), Picture());
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "hi");
        var tool = new SetSplashImageTool(_files, () => splash);

        Assert.Equal(ImageText.SplashSet("art.bmp", Path.Combine(splash, "art.bmp"), onlyOne: true), tool.Set("art.bmp", null));
        Assert.Equal(ImageText.SplashSet("art.bmp", Path.Combine(splash, "art-2.bmp"), onlyOne: false), tool.Set("art.bmp", null));
        Assert.Equal(ImageText.SplashSet("art.bmp", Path.Combine(splash, "neon.bmp"), onlyOne: false), tool.Set("art.bmp", "neon"));
        Assert.Equal(ImageText.SplashNotAnImage("notes.txt"), tool.Set("notes.txt", null));
        Assert.Equal(SetSplashImageTool.Describe(), tool.Description);   // no images folder: the working directory alone
    }

    /// <summary>
    /// With the profile's images folder (2026-10-10, when the splash folder moved under it): a generated picture is taken by its full
    /// path and copied into splash_images; a picture already there is refused (the copy would land beside itself); a full path
    /// elsewhere outside the working directory is still refused; the description and the schema name the folder.
    /// </summary>
    [Fact]
    public void SetSplashImage_WithTheImagesFolder_TakesASavedPictureByItsFullPath_AndRefusesOneAlreadyInTheSplashFolder()
    {
        string imagesRoot = Path.Combine(_dir, "images");
        var images = new WorkingDirectory(() => imagesRoot, new ManualTimeProvider(), WorkingDirectoryOptions.Images);
        string splash = Path.Combine(imagesRoot, NeonSidekick.UI.SplashImages.ProfileFolderName);
        string made = Path.Combine(imagesRoot, "comfy_images", "pony-1.bmp");
        Directory.CreateDirectory(Path.GetDirectoryName(made)!);
        File.WriteAllBytes(made, Picture());
        string outside = Path.Combine(_dir, "elsewhere.bmp");
        File.WriteAllBytes(outside, Picture());
        var tool = new SetSplashImageTool(_files, () => splash, images);

        Assert.Equal(ImageText.SplashSet(made, Path.Combine(splash, "pony-1.bmp"), onlyOne: true), tool.Set(made, null));
        Assert.Equal(Picture(), File.ReadAllBytes(Path.Combine(splash, "pony-1.bmp")));
        string already = Path.Combine(splash, "pony-1.bmp");
        Assert.Equal(ImageText.SplashAlready(already), tool.Set(already, null));
        Assert.False(File.Exists(Path.Combine(splash, "pony-1-2.bmp")));
        Assert.StartsWith("Error:", tool.Set(outside, null));
        Assert.Single(Directory.EnumerateFiles(splash));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));   // the working directory untouched

        Assert.Equal(SetSplashImageTool.Describe(images.Root), tool.Description);
        Assert.Contains(images.Root, tool.Description, StringComparison.Ordinal);
        Assert.Contains("or a full path in the user's images folder", tool.JsonSchema.GetProperty("properties").GetProperty("path").GetProperty("description").GetString());
        Assert.DoesNotContain("images folder", new SetSplashImageTool(_files, () => splash).JsonSchema.GetProperty("properties").GetProperty("path").GetProperty("description").GetString());
    }

    [Fact]
    public void SplashAlready_IsPinned()
    {
        Assert.Equal(@"Error: 'C:\x\a.png' is already in the splash folder", ImageText.SplashAlready(@"C:\x\a.png"));
    }

    /// <summary>
    /// The group (2026-10-12, the user's call): <c>set_splash_image</c> is the picture tools' fourth when the host gives the splash folder,
    /// absent without one, and the ComfyUI group holds <c>generate_image</c> alone.
    /// </summary>
    [Fact]
    public void ThePictureTools_HoldIt_GivenTheSplashFolder_AndTheComfyUIGroupNoLonger()
    {
        if (!ImageCodecs.Available)
        {
            return;   // no picture tools at all without codecs
        }

        string splash = Path.Combine(_dir, "splash");
        var tools = ChatScreen.ImageTools(_files, () => new AppSettingsData(), splashDirectory: () => splash);
        Assert.Equal([ViewImageTool.ToolName, ImageInfoTool.ToolName, ImageEditTool.ToolName, SetSplashImageTool.ToolName], tools.Select(t => t.Name));
        Assert.DoesNotContain(ChatScreen.ImageTools(_files, () => new AppSettingsData()), t => t.Name == SetSplashImageTool.ToolName);
    }
}
