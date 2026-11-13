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

    /// <summary>The picture folders' words (2026-10-18) need the screen; the bare /splash is still the clear.</summary>
    [Fact]
    public async Task Headless_SplashWordsAndPasted_NeedTheScreen()
    {
        string output = await Headless("/splash view\n/splash purge\n/pasted thumbs\n/pasted\n/splash\n");

        Assert.Equal(2, output.Split("[error] " + NeonSidekick.UI.SplashCommand.NeedsScreen).Length - 1);
        Assert.Equal(2, output.Split("[error] " + NeonSidekick.Images.PastedCommand.NeedsScreen).Length - 1);
        Assert.Contains("(conversation cleared)", output);
    }

    /// <summary><c>/ha stills</c> (2026-10-27) needs the screen too: its purge deletes nothing here.</summary>
    [Fact]
    public async Task Headless_HaStills_NeedsTheScreen_AndDeletesNothing()
    {
        string still = Path.Combine(_settings.ProfileImagesDirectory, NeonSidekick.HomeAssistant.HaCamera.FolderName, "porch.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(still)!);
        File.WriteAllText(still, "stays");

        string output = await Headless("/ha stills purge\n/ha stills\n");

        Assert.Equal(2, output.Split("[error] " + NeonSidekick.HomeAssistant.HaText.StillsNeedsScreen).Length - 1);
        Assert.True(File.Exists(still));
    }

    /// <summary>
    /// <c>/ha camera</c> headless (2026-11-13, the user's pick: save it and print the path): by name, and by a chip's alias; a name nothing
    /// matches is the error, and nothing is fetched for it.
    /// </summary>
    [Fact]
    public async Task Headless_HaCamera_SavesTheStill_AndPrintsItsPath_ByNameOrAlias()
    {
        const string server = "http://ha.test:8123";
        _http.Map(server + "/api/states", System.Net.HttpStatusCode.OK, """
            [{"entity_id":"camera.porch","state":"idle","attributes":{"friendly_name":"Porch Camera"}},
             {"entity_id":"camera.yard","state":"idle","attributes":{"friendly_name":"Yard"}}]
            """);
        _http.Map(server + "/api/template", System.Net.HttpStatusCode.OK, "[]", "text/plain");
        _http.Map(server + "/api/camera_proxy/", (_, _) => Task.FromResult(StubHttpMessageHandler.Bytes(System.Net.HttpStatusCode.OK, StripFixtures.Jpeg(), "image/jpeg")));
        _haClient = (url, token) => new NeonSidekick.HomeAssistant.HaClient(url, token, new HttpClient(_http));
        _settings.Update(d =>
        {
            d.HomeAssistantUrl = server;
            d.HomeAssistantToken = "secret";
            d.HomeBarAliases = new Dictionary<string, string> { ["entity:camera.porch"] = "Door" };
        });

        string output = await Headless("/ha camera Porch Camera\n/ha camera Door\n/ha camera Shed\n");

        string folder = Path.Combine(_settings.ProfileImagesDirectory, NeonSidekick.HomeAssistant.HaCamera.FolderName);
        string[] saved = Directory.GetFiles(folder);
        Assert.Equal(2, saved.Length);   // the second, a second later or not, is a -2 copy: headless shares no fetch
        Assert.All(saved, f => Assert.StartsWith("porch-", Path.GetFileName(f), StringComparison.Ordinal));
        Assert.Equal(2, output.Split("Porch Camera: a still, saved as ").Length - 1);
        Assert.All(saved, f => Assert.Contains(Path.GetFileName(f), output, StringComparison.Ordinal));
        Assert.Contains("[error] Error: ", output);
        Assert.Contains("Shed", output);
        Assert.Equal(2, _http.Requests.Count(r => r.Uri.AbsolutePath.StartsWith("/api/camera_proxy/", StringComparison.Ordinal)));
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
