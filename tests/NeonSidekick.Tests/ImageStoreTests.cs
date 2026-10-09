using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>
/// The profile's images folder (2026-10-09, the user's ask: the camera's, the screen's, ComfyUI's and the pasted pictures kept out of the
/// working directory): a <see cref="WorkingDirectory"/> naming its paths in full, the picture tools reaching it by those paths and
/// nothing else outside the sandbox, and the saved paste's label.
/// </summary>
public sealed class ImageStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string _imagesRoot;
    private readonly WorkingDirectory _files;
    private readonly WorkingDirectory _images;
    private readonly AppSettingsData _settings = new();

    public ImageStoreTests()
    {
        _root = Path.Combine(_dir, "files");
        _imagesRoot = Path.Combine(_dir, "images");
        _files = new WorkingDirectory(() => _root, new ManualTimeProvider());
        _images = new WorkingDirectory(() => _imagesRoot, new ManualTimeProvider(), WorkingDirectoryOptions.Images);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static AIFunctionArguments Args(params (string Name, object? Value)[] values) => new(values.ToDictionary(v => v.Name, v => v.Value));

    private string Picture(string root, string relative)
    {
        string full = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, SmokeChecks.SolidBmp(8, 6));
        return full;
    }

    [Fact]
    public void TheImagesFolder_NamesItsPathsInFull_AndAFullPathInsideResolves()
    {
        var written = _images.WriteBytes(Path.Combine("camera_images", "a.jpg"), [1, 2, 3], overwrite: false);

        string full = Path.Combine(_imagesRoot, "camera_images", "a.jpg");
        Assert.Equal(FileOutcome.Ok, written.Outcome);
        Assert.Equal(full, written.Relative);   // what the model is given, and passes back
        Assert.Equal(FileOutcome.Ok, _images.Resolve(full, forWrite: false, out string resolved));
        Assert.Equal(full, resolved);
        Assert.Equal(Path.Combine(_imagesRoot, "camera_images") + Path.DirectorySeparatorChar, _images.Relative(Path.Combine(_imagesRoot, "camera_images"), isDirectory: true));
        Assert.Equal(_imagesRoot, _images.Relative(_imagesRoot));
        Assert.Equal(FileOutcome.OutsideRoot, _images.Resolve(Path.Combine(_imagesRoot, "..", "files", "x.png"), forWrite: false, out _));
        Assert.Equal("a.txt", _files.Relative(Path.Combine(_root, "a.txt")));   // the sandbox keeps its relative names
    }

    [Fact]
    public void Contains_IsAFullPathAtOrUnderTheRoot_BySpelling()
    {
        Assert.True(_images.Contains(Path.Combine(_imagesRoot, "comfy_images", "x.png")));
        Assert.True(_images.Contains(_imagesRoot));
        Assert.True(_images.Contains(" " + Path.Combine(_imagesRoot, "x.png") + " "));
        Assert.False(_images.Contains(Path.Combine("comfy_images", "x.png")));   // relative: never
        Assert.False(_images.Contains(_imagesRoot + "2"));                       // a sibling that starts the same
        Assert.False(_images.Contains(Path.Combine(_imagesRoot, "..", "files", "x.png")));
        Assert.False(_images.Contains(""));
    }

    [Fact]
    public void FileToolFor_PicksTheImagesFolder_OnlyForAFullPathInIt_TheWorkingDirectoryFirst()
    {
        Assert.Same(_images, FileTool.For(_files, _images, Path.Combine(_imagesRoot, "a.png")));
        Assert.Same(_files, FileTool.For(_files, _images, "a.png"));
        Assert.Same(_files, FileTool.For(_files, _images, Path.Combine(_dir, "elsewhere", "a.png")));   // refused by the sandbox, as before
        Assert.Same(_files, FileTool.For(_files, null, Path.Combine(_imagesRoot, "a.png")));

        // A working directory inside the images folder is asked first.
        var nested = new WorkingDirectory(() => Path.Combine(_imagesRoot, "work"), new ManualTimeProvider());
        Assert.Same(nested, FileTool.For(nested, _images, Path.Combine(_imagesRoot, "work", "a.png")));
        Assert.Same(_images, FileTool.For(nested, _images, Path.Combine(_imagesRoot, "a.png")));
    }

    [Fact]
    public async Task ViewImage_ReadsAFullPathInTheImagesFolder_ARelativeOneInTheSandbox_AndNothingElseOutside()
    {
        if (!ImageCodecs.Available)
        {
            return;
        }

        string saved = Picture(_imagesRoot, Path.Combine("camera_images", "shot.bmp"));
        Picture(_root, "local.bmp");
        string outside = Picture(Path.Combine(_dir, "elsewhere"), "x.bmp");
        var tools = ChatScreen.FileTools(_files, () => true, _ => { }, () => _settings, images: _images);
        var view = tools.Single(t => t.Name == ViewImageTool.ToolName);

        var shown = Assert.IsType<ToolImageResult>(await view.InvokeAsync(Args(("paths", new[] { saved, "local.bmp", outside }))));

        Assert.Equal(2, shown.Images.Count);
        Assert.Contains(saved, shown.Text, StringComparison.Ordinal);
        Assert.Contains(FileText.OutsideRoot(outside), shown.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImageEdit_OnASavedPicture_WritesBesideIt_OrIntoTheOutputFolder_InTheSandbox()
    {
        if (!ImageCodecs.Available)
        {
            return;
        }

        string saved = Picture(_imagesRoot, Path.Combine("comfy_images", "pony-5.bmp"));
        var tools = ChatScreen.FileTools(_files, () => true, _ => { }, () => _settings, images: _images);
        var edit = tools.Single(t => t.Name == ImageEditTool.ToolName);
        var info = tools.Single(t => t.Name == ImageInfoTool.ToolName);

        string described = ToolAnswers.Text(await info.InvokeAsync(Args(("path", saved))));
        Assert.StartsWith(saved, described, StringComparison.Ordinal);

        string beside = ToolAnswers.Text(await edit.InvokeAsync(Args(("path", saved), ("rotate", 90))));
        Assert.True(File.Exists(Path.Combine(_imagesRoot, "comfy_images", "pony-5-edited.bmp")), beside);
        Assert.Contains(Path.Combine(_imagesRoot, "comfy_images", "pony-5-edited.bmp"), beside, StringComparison.Ordinal);

        _settings.ImageEditOutputFolder = "edited";
        string into = ToolAnswers.Text(await edit.InvokeAsync(Args(("path", saved), ("rotate", 180))));
        Assert.True(File.Exists(Path.Combine(_root, "edited", "pony-5-edited.bmp")), into);   // the setting names a working-directory folder
    }

    [Fact]
    public void SavedLabel_AndBackAgain_ArePinned()
    {
        string path = Path.Combine(_imagesRoot, "pasted_images", "pasted-20261009-142233.png");
        Assert.Equal("[Image #2, saved as " + path + "]", PasteBlocks.SavedLabel(2, path));
        Assert.Equal("see [Image #2] and [Image #3]", PasteBlocks.WithoutSavedPaths("see " + PasteBlocks.SavedLabel(2, path) + " and " + PasteBlocks.SavedLabel(3, "x.png")));
        Assert.Equal("[Image #2] stays", PasteBlocks.WithoutSavedPaths("[Image #2] stays"));
    }

    [Fact]
    public void PasteBlocks_KnowAClipboardPicture_FromADroppedFile_AndRememberWhereItWasSaved()
    {
        var pastes = new PasteBlocks();
        var clip = new ImageAttachment("clipboard-1.png", [1], ImageFile.Png, 1, 1);
        var dropped = new ImageAttachment("photo.png", [2], ImageFile.Png, 1, 1);
        pastes.AddImage(clip, original: [9, 9]);
        pastes.AddImage(dropped, sourcePath: Path.Combine(_dir, "photo.png"));

        Assert.Equal(1, pastes.NumberOf(clip));
        Assert.Equal(2, pastes.NumberOf(dropped));
        Assert.Null(pastes.NumberOf(clip with { Path = "x" }));   // the very attachment, not an equal one
        Assert.True(pastes.IsClipboard(1));
        Assert.False(pastes.IsClipboard(2));
        Assert.Null(pastes.SavedPath(1));
        Assert.Null(pastes.Original(1)!.Path);

        pastes.MarkSaved(1, Path.Combine(_imagesRoot, "pasted_images", "p.png"));

        Assert.Equal(Path.Combine(_imagesRoot, "pasted_images", "p.png"), pastes.SavedPath(1));
        Assert.Equal(Path.Combine(_imagesRoot, "pasted_images", "p.png"), pastes.Original(1)!.Path);
        Assert.Equal([9, 9], pastes.Original(1)!.Bytes);
    }

    [Fact]
    public void TheImagesFolder_IsUnderTheProfile()
    {
        Assert.Equal("images", AppSettings.ImagesFolderName);
        Assert.Equal("pasted_images", AppSettingsData.DefaultPastedImagesFolder);
        Assert.True(WorkingDirectoryOptions.Images.FullPaths);
        Assert.False(WorkingDirectoryOptions.Sandbox.FullPaths);
    }
}
