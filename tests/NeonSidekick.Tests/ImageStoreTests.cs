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
        var tools = (IReadOnlyList<AIFunction>)[.. ChatScreen.FileTools(_files, () => true, _ => { }, () => _settings, images: _images), .. ChatScreen.ImageTools(_files, () => _settings, _images)];
        var view = tools.Single(t => t.Name == ViewImageTool.ToolName);

        var shown = Assert.IsType<ToolImageResult>(await view.InvokeAsync(Args(("paths", new[] { saved, "local.bmp", outside }))));

        Assert.Equal(2, shown.Images.Count);
        Assert.Contains(saved, shown.Text, StringComparison.Ordinal);
        Assert.Contains(FileText.OutsideRoot(outside), shown.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImageEdit_OnASavedPicture_WritesBesideIt_OrIntoTheOutputFolder_UnderTheImagesFolder()
    {
        if (!ImageCodecs.Available)
        {
            return;
        }

        string saved = Picture(_imagesRoot, Path.Combine("comfy_images", "pony-5.bmp"));
        var tools = (IReadOnlyList<AIFunction>)[.. ChatScreen.FileTools(_files, () => true, _ => { }, () => _settings, images: _images), .. ChatScreen.ImageTools(_files, () => _settings, _images)];
        var edit = tools.Single(t => t.Name == ImageEditTool.ToolName);
        var info = tools.Single(t => t.Name == ImageInfoTool.ToolName);

        string described = ToolAnswers.Text(await info.InvokeAsync(Args(("path", saved))));
        Assert.StartsWith(saved, described, StringComparison.Ordinal);

        string beside = ToolAnswers.Text(await edit.InvokeAsync(Args(("path", saved), ("rotate", 90))));
        Assert.True(File.Exists(Path.Combine(_imagesRoot, "comfy_images", "pony-5-edited.bmp")), beside);
        Assert.Contains(Path.Combine(_imagesRoot, "comfy_images", "pony-5-edited.bmp"), beside, StringComparison.Ordinal);

        // The setting names a folder under the images folder since 2026-10-10 (the user's ask; a working-directory one until then),
        // the answer its full path; a picture in the working directory goes there too.
        _settings.ImageEditOutputFolder = "edited";
        string into = ToolAnswers.Text(await edit.InvokeAsync(Args(("path", saved), ("rotate", 180))));
        Assert.True(File.Exists(Path.Combine(_imagesRoot, "edited", "pony-5-edited.bmp")), into);
        Assert.Contains(Path.Combine(_imagesRoot, "edited", "pony-5-edited.bmp"), into, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_root, "edited")), into);
        Picture(_root, "cat.bmp");
        string fromSandbox = ToolAnswers.Text(await edit.InvokeAsync(Args(("path", "cat.bmp"), ("rotate", 90))));
        Assert.True(File.Exists(Path.Combine(_imagesRoot, "edited", "cat-edited.bmp")), fromSandbox);
        Assert.Contains("output into " + Path.Combine(_imagesRoot, "edited"), ToolAnswers.Text(await info.InvokeAsync(Args(("path", saved)))), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchFiles_ListsTheImagesFolder_ByItsFullPath_NewestFirst_InFullPaths()
    {
        // The model's own call (2026-10-09): the newest pictures by order modified, the folder named in full.
        var time = new ManualTimeProvider();
        string older = Picture(_imagesRoot, Path.Combine("comfy_images", "old.bmp"));
        string newer = Picture(_imagesRoot, Path.Combine("camera_images", "new.bmp"));
        File.SetLastWriteTimeUtc(older, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newer, new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc));
        Picture(_root, "local.bmp");
        var tools = (IReadOnlyList<AIFunction>)[.. ChatScreen.FileTools(_files, () => true, _ => { }, () => _settings, images: _images), .. ChatScreen.ImageTools(_files, () => _settings, _images)];
        var search = tools.Single(t => t.Name == SearchFilesTool.ToolName);

        string recent = ToolAnswers.Text(await search.InvokeAsync(Args(("path", _imagesRoot), ("order", "modified"), ("limit", 5))));
        int at = recent.IndexOf(newer, StringComparison.Ordinal);
        Assert.True(at >= 0, recent);
        Assert.True(at < recent.IndexOf(older, StringComparison.Ordinal), recent);
        Assert.DoesNotContain("local.bmp", recent, StringComparison.Ordinal);

        string found = ToolAnswers.Text(await search.InvokeAsync(Args(("path", Path.Combine(_imagesRoot, "camera_images")), ("files", "*.bmp"))));
        Assert.Contains(newer, found, StringComparison.Ordinal);
        Assert.DoesNotContain(older, found, StringComparison.Ordinal);

        // A relative path is still the working directory's, and a full path elsewhere is still refused.
        Assert.Contains("local.bmp", ToolAnswers.Text(await search.InvokeAsync(Args(("files", "*.bmp")))), StringComparison.Ordinal);
        Assert.Equal(FileText.Missing("images\\"), ToolAnswers.Text(await search.InvokeAsync(Args(("path", "images")))).Replace('/', '\\'));
        string elsewhere = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        Assert.StartsWith("Error: ", ToolAnswers.Text(await search.InvokeAsync(Args(("path", elsewhere)))), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FileInfo_ReadsAFullPathInTheImagesFolder()
    {
        string saved = Picture(_imagesRoot, Path.Combine("pasted_images", "p.bmp"));
        var info = new FileInfoTool(_files, _images);

        string answer = ToolAnswers.Text(await info.InvokeAsync(Args(("path", saved))));

        Assert.DoesNotContain("Error", answer, StringComparison.Ordinal);
        Assert.Contains(saved, answer, StringComparison.Ordinal);
        Assert.StartsWith("Error: ", new FileInfoTool(_files).Describe(saved), StringComparison.Ordinal);   // without the folder, as before
    }

    /// <summary>
    /// <c>open</c> on a full path in the images folder (2026-10-11, code review: its description sends the model there to show a
    /// picture, and the app's own pictures live in that folder): the picture opener gets it, and the description names the folder.
    /// </summary>
    [Fact]
    public async Task Open_OpensAFullPathInTheImagesFolder()
    {
        string saved = Picture(_imagesRoot, Path.Combine("comfy_images", "c.bmp"));
        _files.ImagesFolder = () => _imagesRoot;
        var pictures = new List<string>();
        var opened = new List<string>();
        var tools = ChatScreen.FileTools(_files, () => true, opened.Add, () => _settings, images: _images, openPicture: path => { pictures.Add(path); return FileText.InPictureViewer; });
        var open = tools.Single(t => t.Name == OpenTool.ToolName);

        string answer = ToolAnswers.Text(await open.InvokeAsync(Args(("path", saved))));

        Assert.DoesNotContain("Error", answer, StringComparison.Ordinal);
        Assert.Equal(new[] { saved }, pictures);
        Assert.Empty(opened);
        Assert.Contains(FileText.ImagesFolderInfoSentence(_images.Root), open.Description, StringComparison.Ordinal);
        Assert.Equal(FileText.InImagesFolder(saved), new OpenTool(_files, opened.Add).Describe(saved));   // without the folder, refused
    }

    [Fact]
    public void TheDescriptions_NameTheImagesFolder_OnlyWhileItIsOffered()
    {
        var tools = (IReadOnlyList<AIFunction>)[.. ChatScreen.FileTools(_files, () => true, _ => { }, () => _settings, images: _images), .. ChatScreen.ImageTools(_files, () => _settings, _images)];
        string root = _images.Root;

        Assert.Equal(SearchFilesTool.DescriptionText + " " + FileText.ImagesFolderSearchSentence(root), tools.Single(t => t.Name == SearchFilesTool.ToolName).Description);
        Assert.Equal(FileInfoTool.DescriptionText + " " + FileText.ImagesFolderInfoSentence(root), tools.Single(t => t.Name == FileInfoTool.ToolName).Description);
        Assert.Contains("full path in the user's images folder", tools.Single(t => t.Name == SearchFilesTool.ToolName).JsonSchema.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("full path in the user's images folder", tools.Single(t => t.Name == FileInfoTool.ToolName).JsonSchema.GetRawText(), StringComparison.Ordinal);
        if (ImageCodecs.Available)
        {
            var view = tools.Single(t => t.Name == ViewImageTool.ToolName);
            Assert.Equal(ViewImageTool.Describe(_settings.FileViewImageMaxPerCall, root), view.Description);
            Assert.Contains("(in the user's images folder, " + root + "; search_files lists it by that full path)", view.Description, StringComparison.Ordinal);
        }

        // Without the folder: the old words, no path in them.
        Assert.Equal(SearchFilesTool.DescriptionText, new SearchFilesTool(_files, () => _settings).Description);
        Assert.Equal(FileInfoTool.DescriptionText, new FileInfoTool(_files).Description);
        Assert.DoesNotContain("images folder", new FileInfoTool(_files).JsonSchema.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("images folder", ViewImageTool.Describe(4), StringComparison.Ordinal);
        Assert.Equal(
            "The user's images folder, C:\\p\\images, where camera, screen, generate_image and pasted pictures are saved, is outside the working directory: " +
            "give its full path (or a subfolder's) as path to list or search it, with order modified for the newest; its results are full paths, which view_image, image_info and image_edit take.",
            FileText.ImagesFolderSearchSentence("C:\\p\\images"));
        Assert.Equal("A full path in the user's images folder (C:\\p\\images) works too.", FileText.ImagesFolderInfoSentence("C:\\p\\images"));
    }

    [Fact]
    public async Task TheOtherFileTools_RefuseTheImagesFolder_NamingTheToolsThatReachIt()
    {
        string saved = Picture(_imagesRoot, Path.Combine("comfy_images", "a.bmp"));
        string elsewhere = Path.Combine(_dir, "elsewhere", "x.txt");
        _files.ImagesFolder = () => _imagesRoot;
        var tools = (IReadOnlyList<AIFunction>)[.. ChatScreen.FileTools(_files, () => true, _ => { }, () => _settings, images: _images), .. ChatScreen.ImageTools(_files, () => _settings, _images)];

        Assert.Equal(FileOutcome.InImagesFolder, _files.Resolve(saved, forWrite: false, out _));
        Assert.Equal(FileOutcome.InImagesFolder, _files.Resolve(_imagesRoot, forWrite: false, out _));
        Assert.Equal(FileOutcome.OutsideRoot, _files.Resolve(elsewhere, forWrite: false, out _));
        Assert.Equal(FileOutcome.OutsideRoot, _files.Resolve(_imagesRoot + "2", forWrite: false, out _));   // a sibling that starts the same

        Assert.Equal(FileText.InImagesFolder(saved), ToolAnswers.Text(await tools.Single(t => t.Name == ReadFileTool.ToolName).InvokeAsync(Args(("path", saved)))));
        Assert.Equal(FileText.InImagesFolder(saved), ToolAnswers.Text(await tools.Single(t => t.Name == DeleteTool.ToolName).InvokeAsync(Args(("path", saved)))));
        Assert.True(File.Exists(saved));   // the listing is read-only: nothing else reaches the folder
        Assert.Equal(FileText.OutsideRoot(elsewhere), ToolAnswers.Text(await tools.Single(t => t.Name == ReadFileTool.ToolName).InvokeAsync(Args(("path", elsewhere)))));

        // Without the hook, every refusal is the old one.
        _files.ImagesFolder = null;
        Assert.Equal(FileOutcome.OutsideRoot, _files.Resolve(saved, forWrite: false, out _));
        Assert.Equal(
            "Error: 'C:\\p\\images\\a.png' is in the user's images folder, outside the working directory: only search_files, file_info, open, view_image, image_info and image_edit reach it, by full path",
            FileText.InImagesFolder("C:\\p\\images\\a.png"));
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
