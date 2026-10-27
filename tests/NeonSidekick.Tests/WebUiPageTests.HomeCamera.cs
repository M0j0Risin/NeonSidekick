using System.Text.Json;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// A camera's sheet on the Home tab (2026-10-27, the ring-integration plan's step 5), in headless Edge: its still fetched as it opens and on ↻
/// only (the sheet's own reads every second never ask), ↻ waiting while a still comes, Home Assistant's reason shown, the box dropped by a read
/// that is no longer a camera's and by a close, which ends the fetch under way.
/// </summary>
public sealed partial class WebUiPageTests
{
    private const string CameraPicture = "document.querySelector('#sheet .home-camera-picture')";
    private const string CameraRefresh = "document.querySelector('#sheet .home-camera-refresh')";

    private async Task<Fakes.HeadlessPage?> CameraSheetAsync(Home home)
    {
        home.WithCamera = true;
        _backend.Home = home;
        if (await SignedInAsync("/home") is not { } page)
        {
            return null;
        }

        await page.WaitAsync("document.querySelectorAll('.tile').length === 3", "the three tiles");
        await page.EvalAsync("document.querySelectorAll('.tile')[2].click()");
        await page.WaitAsync("document.getElementById('sheet-title').textContent.startsWith('Front Door Camera')", "the camera's sheet");
        return page;
    }

    [WindowsFact]
    public async Task Home_ACamerasSheet_ShowsItsStill_TheSheetsReadsNeverFetch_AndRefreshWaitsWhileOneComes()
    {
        var home = new Home();
        if (await CameraSheetAsync(home) is not { } page)
        {
            return;
        }

        await page.WaitAsync(CameraPicture + " && !" + CameraPicture + ".classList.contains('hidden') && " + CameraPicture + ".src.startsWith('data:image/jpeg') && "
            + CameraPicture + ".naturalWidth > 0", "the still drawn");
        Assert.Equal(1, Volatile.Read(ref home.CameraCalls));

        // The sheet reads again every second: the picture stays, and no read asks for another.
        int reads = Volatile.Read(ref home.SheetReads);
        await WaitForAsync(() => Volatile.Read(ref home.SheetReads) >= reads + 2);
        await page.WaitAsync(CameraPicture + ".src.startsWith('data:image/jpeg')", "the still kept across the sheet's reads");
        Assert.Equal(1, Volatile.Read(ref home.CameraCalls));
        Assert.Equal(WebUiText.HomeWords.CameraRefresh, await page.TextAsync(CameraRefresh + ".textContent"));

        home.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.EvalAsync(CameraRefresh + ".click()");
        await page.WaitAsync(CameraRefresh + ".disabled && " + CameraRefresh + ".textContent === " + JsonSerializer.Serialize(WebUiText.HomeWords.CameraFetching), "↻ waiting while the still comes");
        await WaitForAsync(() => Volatile.Read(ref home.CameraCalls) == 2);   // the button says so before the ask lands
        await page.EvalAsync(CameraRefresh + ".click()");

        home.Hold.SetResult();
        await page.WaitAsync("!" + CameraRefresh + ".disabled && " + CameraRefresh + ".textContent === " + JsonSerializer.Serialize(WebUiText.HomeWords.CameraRefresh), "↻ back");
        Assert.Equal(2, Volatile.Read(ref home.CameraCalls));
    }

    [WindowsFact]
    public async Task Home_ACamerasSheet_ShowsHomeAssistantsReason_AndARead_ThatIsNoLongerACamerasDropsTheBox_AsACloseDoes()
    {
        const string Reason = "Front Door Camera: the camera is off in Home Assistant (503); switch it on there first";
        var home = new Home { Stills = () => new WebCameraAnswer(null, new WebReply(WebOutcome.Refused, Reason)) };
        if (await CameraSheetAsync(home) is not { } page)
        {
            return;
        }

        await page.WaitAsync("document.querySelector('#sheet .home-camera-status') && document.querySelector('#sheet .home-camera-status').textContent === " + JsonSerializer.Serialize(Reason),
            "the reason under the picture");
        Assert.True((await page.EvalAsync("document.querySelector('#sheet .home-camera-status').classList.contains('failed')")).GetBoolean());
        Assert.True((await page.EvalAsync(CameraPicture + ".classList.contains('hidden')")).GetBoolean());

        home.CameraSheet = false;
        await page.WaitAsync("document.querySelector('#sheet .home-camera') === null", "the box gone with the camera");

        // Closed while a still comes: the box goes, and the late answer draws nothing.
        home.CameraSheet = true;
        home.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.WaitAsync(CameraRefresh + " && " + CameraRefresh + ".disabled", "a new box fetching");
        await page.EvalAsync("history.back()");
        await page.WaitAsync("document.getElementById('sheet-back').classList.contains('hidden') && document.querySelector('.home-camera') === null", "the sheet closed with its box");
        home.Hold.SetResult();
        Assert.Equal(0, (await page.EvalAsync("document.querySelectorAll('.home-camera').length")).GetInt32());
    }
}
