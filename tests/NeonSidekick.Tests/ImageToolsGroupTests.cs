using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Memory;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Timers;

namespace NeonSidekick.Tests;

/// <summary>
/// The picture tools as a group of their own (2026-10-10, the user's ask): <c>view_image</c>, <c>image_info</c> and <c>image_edit</c> off the
/// file tools, under the <c>Image tools</c> switch on <c>/tools</c> › Files › Image, the word <c>/tools image</c> and the toolbar's 🖼️ (🌄 until 2026-10-27); and
/// <c>Image edit output folder</c> naming a folder under the profile's images folder (later that day, the user's ask).
/// </summary>
public sealed class ImageToolsGroupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ns-imagegroup-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void TheSwitch_IsOffByDefault_ARowOfItsOwn_FirstOnTheImageMenu()
    {
        Assert.False(new AppSettingsData().ImageTools);
        Assert.Equal("Image tools", SettingsMenu.FieldName(SettingsField.ImageTools));
        Assert.True(SettingsMenu.IsToggle(SettingsField.ImageTools));
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.ImageTools, new AppSettingsData(), ""));
        Assert.Equal("on", SettingsMenu.FieldValue(SettingsField.ImageTools, new AppSettingsData { ImageTools = true }, ""));
        Assert.Equal(SettingsField.ImageTools, SettingsMenu.ToolsTabFields[ToolsText.MenuIndex(ToolsText.ImageMenuTitle)][0]);
        Assert.DoesNotContain(SettingsField.ImageTools, SettingsMenu.ToolsTabFields[ToolsText.MenuIndex(ToolsText.FileMenuTitle)]);
    }

    [Fact]
    public void TheWord_AndTheToolbarItem_OpenTheSwitch()
    {
        Assert.Equal(SettingsField.ImageTools, ToolsText.SwitchField(" IMAGE "));
        Assert.Equal(ToolsText.SwitchWords.ToList().IndexOf(ToolbarItems.Files) + 1, ToolsText.SwitchWords.ToList().IndexOf(ToolbarItems.Image));
        Assert.Equal("🖼️", ToolbarItems.Glyph(ToolbarItems.Image));   // 🌄 until 2026-10-27 (the user's ask)
        Assert.Equal("Image", ToolbarItems.Title(ToolbarItems.Image));
        Assert.Equal(ToolbarItems.Image, ChatScreen.ToolbarItemOf(ChatScreen.ImageToolGlyph));
        Assert.Equal(ToolsText.SwitchLine(ToolbarItems.Image), ChatScreen.ToolbarWord(ChatScreen.ImageToolGlyph));
        Assert.Equal(ToolbarItems.Defaults.ToList().IndexOf(ToolbarItems.Files) + 1, ToolbarItems.Defaults.ToList().IndexOf(ToolbarItems.Image));   // a default since later on 2026-10-10 (the user's ask), after the folder
        Assert.True(ChatScreen.ToolbarItemOff(ToolbarItems.Image, new AppSettingsData()));
        Assert.False(ChatScreen.ToolbarItemOff(ToolbarItems.Image, new AppSettingsData { ImageTools = true }));
        // The frame is also the status bar's ComfyUI generation (2026-10-27): only the toolbar's routes read it as the Image switch,
        // and no status-bar handler claims it as one of theirs (the cancel answers the strip's zone alone).
        Assert.Equal(NeonSidekick.Comfy.ComfyText.TextToImageLabel, ChatScreen.ImageToolGlyph);
        Assert.Null(ChatScreen.SwitchForGlyph(ChatScreen.ImageToolGlyph));
        Assert.Null(BackgroundJobs.KindOfGlyph(ChatScreen.ImageToolGlyph));
    }

    [Fact]
    public void TheGroup_IsImage_SwitchedByImageTools_ItsOwnOffNote_ApartFromFiles()
    {
        Directory.CreateDirectory(_dir);
        var files = new WorkingDirectory(() => _dir, _time);
        var images = ChatScreen.ImageTools(files, () => new AppSettingsData());
        IReadOnlyList<ToolGroup> Groups(bool filesEnabled, bool imagesEnabled) => SystemPromptSummary.ToolGroups(
            ChatScreen.ClockTools(_time), ChatScreen.TimerTools(new TimerBoard(_time, () => { })), ChatScreen.FileTools(files, () => true, _ => { }, () => new AppSettingsData()),
            ChatScreen.MemoryTools(new MemoryStore(_dir, _time)), memoryEnabled: true, filesEnabled: filesEnabled, images: images, imagesEnabled: imagesEnabled);

        Assert.Equal(ImageToolNames.All, images.Select(t => t.Name));
        if (!ImageCodecs.Available)
        {
            Assert.DoesNotContain(Groups(true, true), g => g.Switch == SettingsField.ImageTools);   // no codecs, no group
            return;
        }

        var group = Groups(filesEnabled: false, imagesEnabled: true).Single(g => g.Switch == SettingsField.ImageTools);
        Assert.Equal("Image (3)", group.Title);
        Assert.Equal(ToolsText.ImageMenuTitle, group.Label);
        Assert.True(group.Offered);   // File tools off does not hold it back
        var off = Groups(filesEnabled: true, imagesEnabled: false).Single(g => g.Switch == SettingsField.ImageTools);
        Assert.False(off.Offered);
        Assert.Equal("Image (3) — not offered: Image tools is off", off.Title);
        Assert.Equal("Image tools is off", SystemPromptSummary.ImagesOffSuffix);
        Assert.DoesNotContain(Groups(true, true).Single(g => g.Switch == SettingsField.FileTools).Tools, t => t is ViewImageTool or ImageInfoTool or ImageEditTool);
    }

    [Fact]
    public void TheOutputFolder_IsUnderTheImagesFolder_WhenThereIsOne_ElseTheWorkingDirectorysAsBefore()
    {
        string images = Path.Combine(_dir, "images");
        Assert.Null(ImageEditTool.OutputFolderFor("", images));
        Assert.Null(ImageEditTool.OutputFolderFor("  ", null));
        Assert.Equal(Path.Combine(images, "edited"), ImageEditTool.OutputFolderFor(" edited ", images));
        Assert.Equal(Path.Combine(images, "a", "b"), ImageEditTool.OutputFolderFor("a/b", images));
        Assert.Equal(Path.Combine(images, "a", "b"), ImageEditTool.OutputFolderFor("a\\b", images));
        Assert.Equal("edited", ImageEditTool.OutputFolderFor("edited", null));   // a tool without the images folder
        Assert.Equal("must be a folder under the profile's images folder (a relative path), or empty", SettingsMenu.ImageEditOutputFolderError);
    }
}
