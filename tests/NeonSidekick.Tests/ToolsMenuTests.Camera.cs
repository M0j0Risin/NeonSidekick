using NeonSidekick.App;
using NeonSidekick.Camera;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.Tests;

/// <summary>The Camera tab of <c>/tools</c> (2026-10-02): after Ask, its rows, the pickers and the typed watch values.</summary>
public partial class ToolsMenuTests
{
    /// <summary>Offered → Web → Files → Shell → Ask → Camera, then the row and Enter.</summary>
    private void OpenCameraRow(int row) => Push([Keys.Right, Keys.Right, Keys.Right, Keys.Right, Keys.Right, .. Enumerable.Repeat(Keys.Down, row), Keys.Enter]);

    [Fact]
    public void TheCameraTab_SitsAfterAsk_WithItsElevenRows()
    {
        int tab = ToolsText.TabTitles.ToList().IndexOf(ToolsText.CameraTabTitle);

        Assert.Equal(ToolsText.TabTitles.ToList().IndexOf(ToolsText.AskTabTitle) + 1, tab);
        Assert.Equal(
            ["Camera tool", "Camera shutter", "Camera preview", "Camera device", "Camera resolution", "Camera output folder", "Camera keep in sessions", "Camera watch interval (s)", "Camera watch change (%)", "Camera watch speaks up", "Camera watch min gap (s)"],
            SettingsMenu.ToolsTabFields[tab - 1].Select(SettingsMenu.FieldName));
        Assert.Equal([SettingsField.CameraTools, SettingsField.CameraKeepInSessions, SettingsField.CameraWatchUnprompted], SettingsMenu.ToolsTabFields[tab - 1].Where(SettingsMenu.IsToggle));
        Assert.DoesNotContain(SettingsMenu.ToolsTabFields[tab - 1], SettingsMenu.RefusedMidTurn);
        var data = new AppSettingsData();
        Assert.Equal(SettingsMenu.FirstCameraLabel, SettingsMenu.FieldValue(SettingsField.CameraDevice, data, _settings.ProfileDirectory));
        Assert.Equal("8%", SettingsMenu.FieldValue(SettingsField.CameraWatchThreshold, data, _settings.ProfileDirectory));
        Assert.Equal("camera_images", SettingsMenu.FieldValue(SettingsField.CameraOutputFolder, data, _settings.ProfileDirectory));
        Assert.Equal(SettingsMenu.CameraOutputHereLabel, SettingsMenu.FieldValue(SettingsField.CameraOutputFolder, new AppSettingsData { CameraOutputFolder = "" }, _settings.ProfileDirectory));
        Assert.Equal("off", SettingsMenu.FieldValue(SettingsField.BotChatCamera, data, _settings.ProfileDirectory));
        Assert.Equal("Botchat camera", SettingsMenu.FieldName(SettingsField.BotChatCamera));
    }

    [Fact]
    public async Task OnThePane_TheCameraSwitch_Saves_AndTheShutterAndPreviewArePickers()
    {
        var (menu, pane, _) = PaneMenu();
        OpenCameraRow(0);                          // Camera tool: the on/off page, off under the cursor
        Push(Keys.Up, Keys.Enter);                 // on
        Push(Keys.Down, Keys.Enter, Keys.Down, Keys.Enter);   // Camera shutter: user → model
        Push(Keys.Down, Keys.Enter, Keys.Down, Keys.Enter);   // Camera preview: live → post
        Push(Keys.Down, Keys.Down, Keys.Enter, Keys.Up, Keys.Enter);   // Camera resolution: 1280x720 → 640x480
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.True(_settings.Current.CameraTools);
        Assert.Equal("model", _settings.Current.CameraShutter);
        Assert.Equal("post", _settings.Current.CameraPreview);
        Assert.Equal("640x480", _settings.Current.CameraResolution);
        Assert.Contains("model    " + CameraShutterMode.Describe("model"), _console.Output);
        Assert.Contains("disabled " + CameraPreviewMode.Describe("disabled"), _console.Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheDevicePicker_ListsTheCameras_UnderTheFirstCameraRow()
    {
        var (menu, pane, settings) = PaneMenu();
        settings.Cameras = () => [new CameraDevice("MX Brio", "a"), new CameraDevice("Laptop Camera", "b")];
        OpenCameraRow(3);
        Push(Keys.Down, Keys.Down, Keys.Enter);    // (first camera) → MX Brio → Laptop Camera
        OpenAgain();
        Push(Keys.Up, Keys.Up, Keys.Enter);        // back to (first camera)
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal("", _settings.Current.CameraDevice);
        Assert.Contains("  MX Brio", _console.Output);
        Assert.Contains("▸ Laptop Camera", _console.Output);   // the second visit opens on the saved one
        pane.Dispose();

        void OpenAgain() => Push(Keys.Enter);
    }

    [Fact]
    public async Task OnThePane_TheDevicePicker_SaysWhyTheCamerasCouldNotBeListed()
    {
        var (menu, pane, settings) = PaneMenu();
        settings.Cameras = () => throw new CameraException(CameraFailure.NoMediaFoundation, null);
        OpenCameraRow(3);
        Push(Keys.Escape, Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Contains("Media Foundation", _console.Output);
        Assert.Contains(SettingsMenu.FirstCameraLabel, _console.Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheOutputFolder_IsTyped_AFolderOutsideRefused_EmptyAllowed()
    {
        var (menu, pane, _) = PaneMenu();
        OpenCameraRow(5);
        Push([.. Enumerable.Repeat(Keys.Backspace, 20)]);
        Type("../elsewhere");                      // refused: it climbs out
        Push([Keys.Enter, .. Enumerable.Repeat(Keys.Backspace, 20)]);
        Type("comfy_images");                      // the ComfyUI folder is fine
        Push([Keys.Enter, .. Enumerable.Repeat(Keys.Backspace, 20)]);
        Push(Keys.Enter);                          // empty: the working directory
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal("", _settings.Current.CameraOutputFolder);
        Assert.Contains("Camera output folder " + SettingsMenu.CameraOutputFolderError + "; keeping camera_images.", _console.Output);
        Assert.Contains("  · Camera output folder: comfy_images", _console.Output);
        pane.Dispose();
    }

    [Fact]
    public async Task OnThePane_TheWatchValues_AreTyped_OutOfRangeRefused()
    {
        var (menu, pane, _) = PaneMenu();
        OpenCameraRow(7);
        Push([.. Enumerable.Repeat(Keys.Backspace, 3)]);
        Type("1");                                 // refused: under 2
        Push([Keys.Enter, .. Enumerable.Repeat(Keys.Backspace, 3)]);
        Type("30");
        Push([Keys.Down, Keys.Enter, .. Enumerable.Repeat(Keys.Backspace, 3)]);
        Type("25%");
        Push([Keys.Down, Keys.Down, Keys.Enter, .. Enumerable.Repeat(Keys.Backspace, 4)]);
        Type("10");                                // refused: under 30
        Push([Keys.Enter, .. Enumerable.Repeat(Keys.Backspace, 4)]);
        Type("600");
        Push(Keys.Escape);

        await menu.ShowAsync(CancellationToken.None);

        Assert.Equal(30, _settings.Current.CameraWatchSeconds);
        Assert.Equal(25, _settings.Current.CameraWatchThreshold);
        Assert.Equal(600, _settings.Current.CameraWatchMinGapSeconds);
        Assert.Contains("Camera watch interval (s) " + SettingsMenu.CameraWatchSecondsRangeError + "; keeping 10.", _console.Output);
        Assert.Contains("Camera watch min gap (s) " + SettingsMenu.CameraWatchMinGapRangeError + "; keeping 120.", _console.Output);
        pane.Dispose();
    }
}
