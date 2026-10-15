using System.Text.Json;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The full-screen picture following new pictures while it is on the newest (later on 2026-10-15, the user's ask, as the PC's viewer): the strip's
/// view and the transcript's, both newest first with the live slot at the far left (the transcript's ran oldest first until the user's report), the live tag and its words, a held view keeping its picture, a step back
/// onto the newest following again, a live swap sending the PC no pick and dropping one still pending, the splash's remove-then-append, a reconnect.
/// </summary>
public sealed partial class WebUiPageTests
{
    private const string OverlayName = "document.getElementById('overlay-name').textContent";

    private const string LiveShown = "!document.getElementById('overlay-live').classList.contains('hidden')";

    private void PublishStrip(WebStrip strip) => Publish("strip", JsonSerializer.Serialize(strip, WebUiJsonContext.Default.WebStrip));

    private static WebEntry PictureEntry(long seq, int id) =>
        new(seq, "picture", "p" + id, null, null, false, [new WebPicture(id, "talk-" + id + ".png", "/favicon.svg", "/favicon.svg", "/favicon.svg", 64, 64, false)]);

    private async Task<HeadlessPage?> StripOverlayAsync(int tap, params int[] ids)
    {
        StripIds([.. ids, 90, 91]);
        _backend.Snap = new WebSnapshot([], [], State(), WebUiText.Words, null, null, Strip(null, ids));
        if (await SignedInAsync("/") is not { } page)
        {
            return null;
        }

        await page.WaitAsync("!document.getElementById('strip-button').classList.contains('hidden')", "🎞️");
        await page.EvalAsync("document.getElementById('strip-button').click()");
        await page.EvalAsync("document.querySelector('.strip-tile[data-id=\"" + tap + "\"]').click()");
        await page.WaitAsync("!document.getElementById('overlay').classList.contains('hidden')", "the overlay");
        return page;
    }

    [WindowsFact]
    public async Task TheStripsView_OnTheNewest_FollowsANewPicture_TaggedLive_AndSendsThePcNoPick()
    {
        if (await StripOverlayAsync(3, 3, 2, 1) is not { } page)
        {
            return;
        }

        await page.WaitAsync(LiveShown, "the live tag on the newest");
        Assert.Equal(WebUiText.Live, await page.TextAsync("document.getElementById('overlay-live').textContent"));
        Assert.Equal(WebUiText.LiveLabel, await page.TextAsync("document.getElementById('overlay-live').getAttribute('aria-label')"));
        Assert.Equal("polite", await page.TextAsync("document.getElementById('overlay-name').getAttribute('aria-live')"));

        PublishStrip(Strip(null, 90, 3, 2, 1));
        await page.WaitAsync(OverlayName + " === 'pic-90.png'", "the new picture shown");
        Assert.False(await HiddenAsync(page, "overlay-live"));
        await Task.Delay(500);
        Assert.Equal([3], _backend.StripPicks);   // the tap's alone
    }

    [WindowsFact]
    public async Task TheStripsView_Held_KeepsItsPicture_GainsItsNewerArrow_AndFollowsAgainBackOnTheNewest()
    {
        if (await StripOverlayAsync(3, 3, 2, 1) is not { } page)
        {
            return;
        }

        await page.EvalAsync("document.getElementById('overlay-next').click()");   // › older
        await page.WaitAsync(OverlayName + " === 'pic-2.png'", "the older picture");
        Assert.True(await HiddenAsync(page, "overlay-live"));

        PublishStrip(Strip(null, 90, 3, 2, 1));
        await page.WaitAsync("!document.getElementById('overlay-prev').disabled && document.querySelectorAll('.strip-tile').length === 4", "the strip grown");
        Assert.Equal("pic-2.png", await page.TextAsync(OverlayName));

        await page.EvalAsync("document.getElementById('overlay-prev').click()");
        await page.EvalAsync("document.getElementById('overlay-prev').click()");
        await page.WaitAsync(OverlayName + " === 'pic-90.png' && " + LiveShown, "back on the newest, live again");
    }

    [WindowsFact]
    public async Task ALiveSwap_DropsAPickStillPending()
    {
        if (await StripOverlayAsync(3, 3, 2, 1) is not { } page)
        {
            return;
        }

        // › then ‹: live again with a pick for 3 waiting its 300 ms; a newer picture lands inside them.
        await page.EvalAsync("document.getElementById('overlay-next').click(); document.getElementById('overlay-prev').click()");
        PublishStrip(Strip(null, 91, 3, 2, 1));
        await page.WaitAsync(OverlayName + " === 'pic-91.png'", "the new picture shown");
        await Task.Delay(600);
        Assert.Equal([3], _backend.StripPicks);   // the tap's; the pending pick for 3 dropped
    }

    [WindowsFact]
    public async Task TheTranscriptsView_RunsNewestFirst_LiveAtTheFarLeft_FollowsAnAppendedPicture_AndHeldStaysPut()
    {
        _backend.Snap = new WebSnapshot([PictureEntry(1, 11), PictureEntry(2, 12)], [], State(), WebUiText.Words);
        if (await SignedInAsync("/") is not { } page)
        {
            return;
        }

        await page.WaitAsync("document.querySelectorAll('.entry.pictures .tile').length === 2", "two pictures");
        await page.EvalAsync("document.querySelectorAll('.entry.pictures .tile')[1].click()");
        await page.WaitAsync(OverlayName + " === 'talk-12.png' && " + LiveShown, "the newest, live");
        // Newest first, as the strip and the PC's viewer: the newest is the first slot (no ‹), the older one is ›.
        Assert.True((await page.EvalAsync("document.getElementById('overlay-prev').disabled")).GetBoolean());
        Assert.False((await page.EvalAsync("document.getElementById('overlay-next').disabled")).GetBoolean());

        Publish("append", JsonSerializer.Serialize(PictureEntry(3, 13), WebUiJsonContext.Default.WebEntry));
        await page.WaitAsync(OverlayName + " === 'talk-13.png' && " + LiveShown, "the appended picture shown");

        // Held on an older one (›): an append leaves it where it is.
        await page.EvalAsync("document.getElementById('overlay-next').click()");
        await page.WaitAsync(OverlayName + " === 'talk-12.png'", "held on the one before");
        Assert.True(await HiddenAsync(page, "overlay-live"));
        Publish("append", JsonSerializer.Serialize(PictureEntry(4, 14), WebUiJsonContext.Default.WebEntry));
        await Task.Delay(300);
        Assert.Equal("talk-12.png", await page.TextAsync(OverlayName));

        // Its picture removed: the nearest stays shown.
        Publish("remove", JsonSerializer.Serialize(new WebRemoved(2), WebUiJsonContext.Default.WebRemoved));
        await page.WaitAsync(OverlayName + " !== 'talk-12.png'", "a neighbor shown");
        Assert.False(await HiddenAsync(page, "overlay"));
    }

    [WindowsFact]
    public async Task TheSplashsSwap_ARemoveThenAnAppend_GoesStraightToTheNewPicture()
    {
        _backend.Snap = new WebSnapshot([PictureEntry(1, 21), PictureEntry(2, 22)], [], State(), WebUiText.Words);
        if (await SignedInAsync("/") is not { } page)
        {
            return;
        }

        await page.WaitAsync("document.querySelectorAll('.entry.pictures .tile').length === 2", "two pictures");
        await page.EvalAsync("document.querySelectorAll('.entry.pictures .tile')[1].click()");
        await page.WaitAsync(OverlayName + " === 'talk-22.png'", "the last");
        // Every name the overlay shows, recorded, so a flash of the one before would show.
        await page.EvalAsync("window.__names = []; new MutationObserver(() => window.__names.push(document.getElementById('overlay-name').textContent)).observe(document.getElementById('overlay-name'), { childList: true, characterData: true, subtree: true })");

        Publish("remove", JsonSerializer.Serialize(new WebRemoved(2), WebUiJsonContext.Default.WebRemoved));
        Publish("append", JsonSerializer.Serialize(PictureEntry(3, 23), WebUiJsonContext.Default.WebEntry));
        await page.WaitAsync(OverlayName + " === 'talk-23.png'", "the new splash");
        Assert.DoesNotContain("talk-21.png", await page.TextAsync("window.__names.join(',')"), StringComparison.Ordinal);
    }

    [WindowsFact]
    public async Task ASinglePicture_IsLive_AndAReconnectWithANewerStrip_MovesIt()
    {
        if (await StripOverlayAsync(1, 1) is not { } page)
        {
            return;
        }

        await page.WaitAsync(LiveShown, "a lone picture, live");
        _backend.Snap = new WebSnapshot([], [], State(), WebUiText.Words, null, null, Strip(null, 90, 1));
        await page.EvalAsync("reconnect()");
        await page.WaitAsync(OverlayName + " === 'pic-90.png'", "the reconnect's newer picture");
    }
}
