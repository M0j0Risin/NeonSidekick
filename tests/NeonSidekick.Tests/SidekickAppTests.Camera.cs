using NeonSidekick.App;
using NeonSidekick.Camera;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>The camera outside the screen (2026-10-02): headless lists the cameras and nothing else; <c>--camera-check</c> over the fake.</summary>
public partial class SidekickAppTests
{
    [Fact]
    public async Task Headless_Camera_ListsTheCameras_AndEverythingElseNeedsTheScreen()
    {
        _camera = new FakeCameraSystem();

        string output = await Headless("/camera list\n/camera snap\n/camera\n");

        Assert.Contains("  1. Fake Cam  ← chosen" + Environment.NewLine, output);
        Assert.Equal(2, output.Split("[error] " + CameraText.NeedsScreen).Length - 1);
        Assert.Equal(0, _camera.Opens);
        Assert.DoesNotContain(SidekickApp.HeadlessNoAssistantReply, output);
    }

    [Fact]
    public async Task Headless_Camera_WithoutALayer_SaysSo()
    {
        string output = await Headless("/camera list\n");

        Assert.Contains("[error] " + CameraText.Failure(CameraFailure.Unsupported, null), output);
    }

    [Fact]
    public async Task CameraCheck_OverTheFake_PassesEveryLine()
    {
        _console.Profile.Width = 300;   // the check's lines unwrapped
        _camera = new FakeCameraSystem { Paint = (_, size) => FakeCameraSystem.Solid(size, 120, 120, 120) };

        int code = await App().RunAsync(SidekickOptions.None with { CameraCheck = true }, CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Contains(CameraCheck.IntroLine, _console.Output);
        Assert.Contains("camera:list", _console.Output);
        Assert.Contains("Fake Cam: 64x48 NV12 30 fps, frames 64x48 RGB32", _console.Output);
        Assert.Contains("camera:encode", _console.Output);
        Assert.Contains("0% of the picture changed", _console.Output);
        Assert.Contains("CAMERA CHECK PASS  5 checks", _console.Output);
    }

    [Fact]
    public async Task CameraCheck_ABlackPicture_IsSaid_AndNoCameraFails()
    {
        _console.Profile.Width = 300;   // the check's lines unwrapped
        _camera = new FakeCameraSystem { Paint = (_, size) => FakeCameraSystem.Solid(size, 0, 0, 0) };
        Assert.Equal(0, await App().RunAsync(SidekickOptions.None with { CameraCheck = true }, CancellationToken.None));
        Assert.Contains("the picture is black", _console.Output);

        _camera.Devices.Clear();
        Assert.Equal(1, await App().RunAsync(SidekickOptions.None with { CameraCheck = true }, CancellationToken.None));
        Assert.Contains("CAMERA CHECK FAIL", _console.Output);

        _camera.ListFailure = new CameraException(CameraFailure.NoMediaFoundation, null);
        Assert.Equal(1, await App().RunAsync(SidekickOptions.None with { CameraCheck = true }, CancellationToken.None));

        _camera.ListFailure = null;
        _camera.Devices.Add(new CameraDevice("Fake Cam", "x"));
        _camera.OpenFailure = new CameraException(CameraFailure.InUse, null);
        Assert.Equal(1, await App().RunAsync(SidekickOptions.None with { CameraCheck = true }, CancellationToken.None));
        Assert.Contains(CameraText.Failure(CameraFailure.InUse, null), _console.Output);
    }

    [Fact]
    public void TheCameraCheckFlag_IsACheckMode()
    {
        var options = SidekickOptions.Parse(["--camera-check"]);

        Assert.True(options.CameraCheck);
        Assert.True(options.IsCheck);
        Assert.Equal("camera-check", options.Mode);
        Assert.Contains("--camera-check", SidekickOptions.Usage);
    }
}
