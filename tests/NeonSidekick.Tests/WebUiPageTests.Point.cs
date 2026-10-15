using System.Text.Json;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// Later on 2026-10-15 in headless Edge: the page follows the PC's point (the user's ask) — the strip's view and the transcript's, live dropped and
/// back, a pending swipe's pick dropped, an unknown file or an old sequence moving nothing, a reconnect's newer point, the drawer scrolled to the
/// PC's pick — and the overlay's 🗑️: shown only for a picture with a file, two taps, disarmed by a step, the clock and a point, the next picture
/// after a delete, the last one closing, the desktop's Delete and Esc, and a deleted picture never brought back.
/// </summary>
public sealed partial class WebUiPageTests
{
    private const string DeleteHidden = "document.getElementById('overlay-delete').classList.contains('hidden')";

    private const string DeleteArmed = "document.getElementById('overlay-delete').classList.contains('armed')";

    private static WebPicture KeyedPicture(int id, string prefix = "pic-", bool deletable = true) =>
        new(id, prefix + id + ".png", "/favicon.svg", "/favicon.svg", "/favicon.svg", 64, 64, false, "k" + id, deletable);

    private static WebStrip KeyedStrip(int? selected, params int[] ids) => new([.. ids.Select(id => KeyedPicture(id))], selected);

    private static WebEntry KeyedEntry(long seq, int id, bool deletable = true) =>
        new(seq, "picture", "p" + id, null, null, false, [KeyedPicture(id, "talk-", deletable)]);

    private void PublishPoint(string key, long seq) => Publish("point", JsonSerializer.Serialize(new WebPoint(key, seq), WebUiJsonContext.Default.WebPoint));

    private async Task<HeadlessPage?> KeyedStripOverlayAsync(int tap, WebPoint? point = null, params int[] ids)
    {
        StripIds(ids);
        _backend.Snap = new WebSnapshot([], [], State(), WebUiText.Words, null, null, KeyedStrip(null, ids), point);
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

    private async Task<HeadlessPage?> KeyedTranscriptOverlayAsync(int tapIndex, params WebEntry[] entries)
    {
        _backend.Snap = new WebSnapshot(entries, [], State(), WebUiText.Words);
        if (await SignedInAsync("/") is not { } page)
        {
            return null;
        }

        await page.WaitAsync("document.querySelectorAll('.entry.pictures .tile').length === " + entries.Length, "the pictures");
        await page.EvalAsync("document.querySelectorAll('.entry.pictures .tile')[" + tapIndex + "].click()");
        await page.WaitAsync("!document.getElementById('overlay').classList.contains('hidden')", "the overlay");
        return page;
    }

    // ── The PC's point ──

    [WindowsFact]
    public async Task TheStripsView_FollowsThePcsPoint_HoldingOnAnOlderPicture_AndLiveAgainOnTheNewest()
    {
        if (await KeyedStripOverlayAsync(3, null, 3, 2, 1) is not { } page)
        {
            return;
        }

        await page.WaitAsync(LiveShown, "live on the newest");
        PublishPoint("k1", 1);
        await page.WaitAsync(OverlayName + " === 'pic-1.png'", "the PC's picture");
        Assert.True(await HiddenAsync(page, "overlay-live"));

        PublishPoint("k3", 2);
        await page.WaitAsync(OverlayName + " === 'pic-3.png' && " + LiveShown, "the newest, live again");
    }

    [WindowsFact]
    public async Task TheTranscriptsView_FollowsAPoint_ByTheFilesKey()
    {
        if (await KeyedTranscriptOverlayAsync(1, KeyedEntry(1, 11), KeyedEntry(2, 12)) is not { } page)
        {
            return;
        }

        await page.WaitAsync(OverlayName + " === 'talk-12.png'", "the newest");
        PublishPoint("k11", 1);
        await page.WaitAsync(OverlayName + " === 'talk-11.png'", "the PC's picture");
    }

    [WindowsFact]
    public async Task APoint_DropsASwipesPendingPick_AndAnUnknownFileOrAnOldSequenceMovesNothing()
    {
        if (await KeyedStripOverlayAsync(3, null, 3, 2, 1) is not { } page)
        {
            return;
        }

        // › then a point inside the pick's 300 ms: the pick for 2 is never posted.
        await page.EvalAsync("document.getElementById('overlay-next').click()");
        PublishPoint("k1", 1);
        await page.WaitAsync(OverlayName + " === 'pic-1.png'", "the PC's picture");
        await Task.Delay(600);
        Assert.Equal([3], _backend.StripPicks);   // the tap's alone

        PublishPoint("nope", 2);
        PublishPoint("k3", 2);   // not newer than the unknown one's sequence
        await Task.Delay(400);
        Assert.Equal("pic-1.png", await page.TextAsync(OverlayName));
    }

    [WindowsFact]
    public async Task AFreshPage_TakesTheSnapshotsPoint_WithoutMoving_AndAReconnect_FollowsANewerOne()
    {
        if (await KeyedStripOverlayAsync(3, new WebPoint("k2", 5), 3, 2, 1) is not { } page)
        {
            return;
        }

        PublishPoint("k1", 5);   // the snapshot's sequence: already seen
        await Task.Delay(400);
        Assert.Equal("pic-3.png", await page.TextAsync(OverlayName));

        _backend.Snap = new WebSnapshot([], [], State(), WebUiText.Words, null, null, KeyedStrip(null, 3, 2, 1), new WebPoint("k1", 7));
        await page.EvalAsync("reconnect()");
        await page.WaitAsync(OverlayName + " === 'pic-1.png'", "the reconnect's newer point");
    }

    [WindowsFact]
    public async Task TheDrawer_OpensOnThePcsPick_AndScrollsToItWhenItMoves()
    {
        int[] ids = [.. Enumerable.Range(1, 30).Reverse()];
        StripIds(ids);
        _backend.Snap = new WebSnapshot([], [], State(), WebUiText.Words, null, null, KeyedStrip(1, ids));
        if (await SignedInAsync("/") is not { } page)
        {
            return;
        }

        const string InView = "(() => { const t = document.querySelector('.strip-tile.selected'), r = document.getElementById('strip-row'); if (!t) return false; const a = t.getBoundingClientRect(), b = r.getBoundingClientRect(); return a.left >= b.left - 1 && a.right <= b.right + 1; })()";
        await page.WaitAsync("!document.getElementById('strip-button').classList.contains('hidden')", "🎞️");
        await page.EvalAsync("document.getElementById('strip-button').click()");
        await page.WaitAsync(InView, "the oldest, outlined, in view as the drawer opens");

        Publish("strip", JsonSerializer.Serialize(KeyedStrip(30, ids), WebUiJsonContext.Default.WebStrip));
        await page.WaitAsync(InView + " && document.querySelector('.strip-tile.selected').dataset.id === '30'", "the newest, scrolled to");
    }

    // ── The 🗑️ ──

    [WindowsFact]
    public async Task ThePicturesWithoutAFile_ShowNoDelete()
    {
        if (await KeyedTranscriptOverlayAsync(0, KeyedEntry(1, 11, deletable: false)) is not { } page)
        {
            return;
        }

        Assert.True((await page.EvalAsync(DeleteHidden)).GetBoolean());
    }

    [WindowsFact]
    public async Task TwoTaps_DeleteThePicture_TheViewMovesOn_AndItsTilesShowTheirFallback()
    {
        if (await KeyedTranscriptOverlayAsync(1, KeyedEntry(1, 11), KeyedEntry(2, 12)) is not { } page)
        {
            return;
        }

        await page.WaitAsync(OverlayName + " === 'talk-12.png' && !" + DeleteHidden, "the 🗑️");
        await page.EvalAsync("document.getElementById('overlay-delete').click()");
        await page.WaitAsync(DeleteArmed, "armed");
        Assert.Equal(WebUiText.DeleteArmed, await page.TextAsync("document.getElementById('overlay-delete').getAttribute('aria-label')"));
        Assert.Empty(_backend.Deleted);

        await page.EvalAsync("document.getElementById('overlay-delete').click()");
        await page.WaitAsync(OverlayName + " === 'talk-11.png'", "the next older picture");
        Assert.Equal([12], _backend.Deleted);
        Assert.Equal(_backend.DeleteMessage, await page.TextAsync("document.getElementById('overlay-note').textContent"));
        await page.WaitAsync("document.querySelectorAll('.entry.pictures .tile.fallback').length === 1", "the deleted file's tile");

        // A new picture to a live view: the list has it and not the deleted one.
        Publish("append", JsonSerializer.Serialize(KeyedEntry(3, 13), WebUiJsonContext.Default.WebEntry));
        await page.WaitAsync(OverlayName + " === 'talk-13.png'", "the new picture");
        await page.EvalAsync("document.getElementById('overlay-next').click()");
        await page.WaitAsync(OverlayName + " === 'talk-11.png'", "past the deleted one");
        Assert.True((await page.EvalAsync("document.getElementById('overlay-next').disabled")).GetBoolean());
    }

    [WindowsFact]
    public async Task AStep_AClockOrAPoint_DisarmsTheDelete()
    {
        if (await KeyedStripOverlayAsync(3, null, 3, 2, 1) is not { } page)
        {
            return;
        }

        await page.EvalAsync("document.getElementById('overlay-delete').click()");
        await page.WaitAsync(DeleteArmed, "armed");
        await page.EvalAsync("document.getElementById('overlay-next').click()");
        await page.WaitAsync("!" + DeleteArmed, "a step disarms it");
        await page.EvalAsync("document.getElementById('overlay-delete').click()");   // arms the new picture, deletes nothing
        await page.WaitAsync(DeleteArmed, "armed on the new picture");
        Assert.Empty(_backend.Deleted);

        PublishPoint("k1", 1);
        await page.WaitAsync(OverlayName + " === 'pic-1.png' && !" + DeleteArmed, "the PC's point disarms it");
        await page.EvalAsync("document.getElementById('overlay-delete').click()");
        await page.WaitAsync(DeleteArmed, "armed again");
        await page.WaitAsync("!" + DeleteArmed, "the clock disarms it", 5);
        Assert.Empty(_backend.Deleted);
    }

    [WindowsFact]
    public async Task DeletingTheOnlyPicture_ClosesTheView_AndOnADesktop_DeleteTwiceDoesIt_EscDisarmsFirst()
    {
        if (await KeyedTranscriptOverlayAsync(0, KeyedEntry(1, 11)) is not { } page)
        {
            return;
        }

        const string Press = "document.dispatchEvent(new KeyboardEvent('keydown', { key: '{0}', bubbles: true }))";
        await page.EvalAsync(Press.Replace("{0}", "Delete", StringComparison.Ordinal));
        await page.WaitAsync(DeleteArmed, "armed by Delete");
        await page.EvalAsync(Press.Replace("{0}", "Escape", StringComparison.Ordinal));
        await page.WaitAsync("!" + DeleteArmed, "Esc disarms first");
        Assert.False(await HiddenAsync(page, "overlay"));

        await page.EvalAsync(Press.Replace("{0}", "Delete", StringComparison.Ordinal));
        await page.EvalAsync(Press.Replace("{0}", "Delete", StringComparison.Ordinal));
        await page.WaitAsync("document.getElementById('overlay').classList.contains('hidden')", "the view closed");
        Assert.Equal([11], _backend.Deleted);
    }
}
