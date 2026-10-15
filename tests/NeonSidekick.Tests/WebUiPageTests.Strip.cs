using System.Text.Json;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The PC's picture strip in the chat page's drawer (later on 2026-10-15): the 🎞️ button and its dot, the drawer newest first with the PC's pick
/// outlined, a tap opening the overlay and picking on the PC, Back and Esc peeling the topmost thing, one footer panel at a time, a transcript
/// picture tapped with the drawer open, the strip going away, and a tile whose thumbnail fails showing its original, then its name. The pictures'
/// addresses are the page's own files, so nothing needs decoding.
/// </summary>
public sealed partial class WebUiPageTests
{
    private static WebPicture StripPicture(int id, string thumb = "/favicon.svg", string full = "/favicon.svg") =>
        new(id, "pic-" + id + ".png", thumb, "/favicon.svg", full, 64, 64, false);

    private static WebStrip Strip(int? selected, params int[] ids) => new([.. ids.Select(id => StripPicture(id))], selected);

    private void StripIds(params int[] ids)
    {
        foreach (int id in ids)
        {
            _backend.StripIds[id] = true;
        }
    }

    private Task<bool> DrawerOpenAsync(HeadlessPage page) => HiddenAsync(page, "strip").ContinueWith(t => !t.Result, TaskScheduler.Default);

    [WindowsFact]
    public async Task TheStrip_OpensInADrawer_ATapPicksOnThePc_AndBackPeelsTheOverlayThenTheDrawer()
    {
        StripIds(3, 2, 1);
        _backend.Snap = new WebSnapshot([], [], State(), WebUiText.Words, null, null, Strip(2, 3, 2, 1));
        if (await SignedInAsync("/") is not { } page)
        {
            return;
        }

        await page.WaitAsync("!document.getElementById('strip-button').classList.contains('hidden')", "🎞️ with a strip");
        Assert.False(await HiddenAsync(page, "strip-new"));   // never opened: the dot
        Assert.Equal(0, (await page.EvalAsync("document.querySelectorAll('#strip-row img[src]').length")).GetInt32());   // nothing loads before it opens

        await page.EvalAsync("document.getElementById('strip-button').click()");
        Assert.True(await DrawerOpenAsync(page));
        Assert.True(await HiddenAsync(page, "strip-new"));
        Assert.True(await HiddenAsync(page, "strip-button"));   // the drawer has its ✕
        Assert.Equal("3,2,1", await page.TextAsync("[...document.querySelectorAll('.strip-tile')].map(t => t.dataset.id).join(',')"));
        Assert.Equal("2", await page.TextAsync("document.querySelector('.strip-tile.selected').dataset.id"));
        Assert.Equal("🎞️ " + WebUiText.StripMany.Replace("{0}", "3", StringComparison.Ordinal), await page.TextAsync("document.getElementById('strip-head').textContent"));
        Assert.Equal("3", await page.TextAsync("document.activeElement.dataset.id"));   // focus on the first tile

        await page.EvalAsync("document.querySelector('.strip-tile[data-id=\"1\"]').click()");
        await page.WaitAsync("!document.getElementById('overlay').classList.contains('hidden')", "the overlay");
        Assert.Equal("pic-1.png", await page.TextAsync("document.getElementById('overlay-name').textContent"));
        await WaitForAsync(() => _backend.StripPicks.Contains(1));

        await page.EvalAsync("history.back()");
        await page.WaitAsync("document.getElementById('overlay').classList.contains('hidden')", "Back closing the overlay");
        Assert.True(await DrawerOpenAsync(page));
        await page.EvalAsync("history.back()");
        await page.WaitAsync("document.getElementById('strip').classList.contains('hidden')", "Back closing the drawer");
        await page.WaitAsync("document.activeElement === document.getElementById('strip-button')", "focus back on 🎞️");

        // A new picture: the dot again.
        Publish("strip", JsonSerializer.Serialize(Strip(null, 4, 3, 2, 1), WebUiJsonContext.Default.WebStrip));
        await page.WaitAsync("!document.getElementById('strip-new').classList.contains('hidden')", "the dot for the new picture");
    }

    [WindowsFact]
    public async Task Esc_ClosesTheTopmostThing_AndAQuestionOrTheUsageCard_ClosesTheDrawer()
    {
        StripIds(1);
        _backend.Snap = new WebSnapshot([], [], State(), WebUiText.Words, null, null, Strip(null, 1));
        if (await SignedInAsync("/") is not { } page)
        {
            return;
        }

        const string Esc = "document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))";
        await page.WaitAsync("!document.getElementById('strip-button').classList.contains('hidden')", "🎞️");
        await page.EvalAsync("document.getElementById('strip-button').click()");
        await page.EvalAsync("document.querySelector('.strip-tile').click()");
        await page.WaitAsync("!document.getElementById('overlay').classList.contains('hidden')", "the overlay over the drawer");
        await page.EvalAsync(Esc);
        await page.WaitAsync("document.getElementById('overlay').classList.contains('hidden')", "Esc closing the overlay");
        Assert.True(await DrawerOpenAsync(page));   // the one Esc closed the overlay alone
        await page.EvalAsync(Esc);
        await page.WaitAsync("document.getElementById('strip').classList.contains('hidden')", "Esc closing the drawer");

        // The usage card and the drawer never show together.
        await page.EvalAsync("document.getElementById('strip-button').click()");
        await page.EvalAsync("document.getElementById('usage').classList.remove('hidden'); document.getElementById('usage').click()");
        await page.WaitAsync("!document.getElementById('usage-card').classList.contains('hidden') && document.getElementById('strip').classList.contains('hidden')", "the card replacing the drawer");
        await page.EvalAsync(Esc);
        await page.WaitAsync("document.getElementById('usage-card').classList.contains('hidden')", "Esc closing the card");

        // A question arriving closes the drawer.
        await page.EvalAsync("document.getElementById('strip-button').click()");
        Assert.True(await DrawerOpenAsync(page));
        var ask = new WebAskView("a1", "yesno", "Allow?", null, null, null, [new WebChoice("y", "Yes"), new WebChoice("n", "No")], null, null, null);
        Publish("asks", JsonSerializer.Serialize(new WebAsks([ask]), WebUiJsonContext.Default.WebAsks));
        await page.WaitAsync("document.getElementById('strip').classList.contains('hidden')", "the question closing the drawer");
    }

    [WindowsFact]
    public async Task ATranscriptPicture_TappedWithTheDrawerOpen_KeepsTheDrawer_PicksNothing_AndTheStripGoing_LeavesNeither()
    {
        StripIds(1);
        var picture = new WebPicture(9, "talk.png", "/favicon.svg", "/favicon.svg", "/favicon.svg", 64, 64, false);
        _backend.Snap = new WebSnapshot([new WebEntry(1, "picture", "talk.png", null, null, false, [picture])], [], State(), WebUiText.Words, null, null, Strip(null, 1));
        if (await SignedInAsync("/") is not { } page)
        {
            return;
        }

        await page.WaitAsync("document.querySelector('.entry.pictures .tile') !== null", "the transcript's picture");
        await page.EvalAsync("document.getElementById('strip-button').click()");
        await page.EvalAsync("document.querySelector('.entry.pictures .tile').click()");
        await page.WaitAsync("!document.getElementById('overlay').classList.contains('hidden')", "the overlay");
        await Task.Delay(300);
        Assert.True(await DrawerOpenAsync(page));
        Assert.Equal("talk.png", await page.TextAsync("document.getElementById('overlay-name').textContent"));
        await page.EvalAsync("document.getElementById('overlay-next').click()");   // the transcript's list: no step, no pick
        await Task.Delay(500);
        Assert.Empty(_backend.StripPicks);
        await page.EvalAsync("history.back()");
        await page.WaitAsync("document.getElementById('overlay').classList.contains('hidden')", "the overlay closed");

        // The strip going (a new chat, the setting off) with the drawer and the strip's overlay open: neither stays, and the history is clean.
        await page.EvalAsync("document.querySelector('.strip-tile').click()");
        await page.WaitAsync("!document.getElementById('overlay').classList.contains('hidden')", "the strip's overlay");
        Publish("strip", "null");
        await page.WaitAsync("document.getElementById('overlay').classList.contains('hidden') && document.getElementById('strip').classList.contains('hidden')", "both closed");
        await page.WaitAsync("history.state === null", "the history clean");
        await page.WaitAsync("document.getElementById('strip-button').classList.contains('hidden')", "no 🎞️ without a strip");
    }

    [WindowsFact]
    public async Task AStripTile_WhoseThumbnailFails_ShowsItsOriginal_ThenItsName()
    {
        _backend.Snap = new WebSnapshot([], [], State(), WebUiText.Words, null, null,
            new WebStrip([StripPicture(1, thumb: "/no-thumb.png"), StripPicture(2, thumb: "/no-thumb.png", full: "/no-full.png")], null));
        if (await SignedInAsync("/") is not { } page)
        {
            return;
        }

        await page.WaitAsync("!document.getElementById('strip-button').classList.contains('hidden')", "🎞️");
        await page.EvalAsync("document.getElementById('strip-button').click()");
        await page.WaitAsync("document.querySelector('.strip-tile[data-id=\"1\"] img.original') !== null && document.querySelector('.strip-tile[data-id=\"1\"] img').complete", "the first tile's original");
        Assert.EndsWith("/favicon.svg", await page.TextAsync("document.querySelector('.strip-tile[data-id=\"1\"] img').src"), StringComparison.Ordinal);
        await page.WaitAsync("document.querySelector('.strip-tile[data-id=\"2\"]').classList.contains('fallback')", "the second tile's name");
        Assert.Equal("pic-2.png", await page.TextAsync("document.querySelector('.strip-tile[data-id=\"2\"]').textContent"));
    }

    [WindowsFact]
    public async Task TheDrawer_AtTheBottom_KeepsTheLastLineInView_AndTheFilmButton_TakesTheBottomsPlace()
    {
        StripIds(1);
        _backend.Snap = new WebSnapshot(Notices(120), [], State(), WebUiText.Words, null, null, Strip(null, 1));
        if (await SignedInAsync("/") is not { } page)
        {
            return;
        }

        await page.WaitAsync("document.querySelectorAll('.entry.notice').length === 120 && !document.getElementById('strip-button').classList.contains('hidden')", "a long transcript and 🎞️");
        await page.WaitAsync("document.getElementById('to-bottom').classList.contains('hidden')", "at the bottom: no ⤓");
        // 🎞️ sits where ⤓ would: 8 px above the footer.
        double gap = (await page.EvalAsync("document.querySelector('footer.bottom').getBoundingClientRect().top - document.getElementById('strip-button').getBoundingClientRect().bottom")).GetDouble();
        Assert.InRange(gap, 6, 14);

        await page.EvalAsync("document.getElementById('strip-button').click()");
        await page.WaitAsync("window.innerHeight + window.scrollY >= document.body.scrollHeight - 80", "the last line still in view");
        double lastLine = (await page.EvalAsync("document.querySelector('.entry.notice:last-child').getBoundingClientRect().bottom - document.querySelector('footer.bottom').getBoundingClientRect().top")).GetDouble();
        Assert.True(lastLine <= 1, "the last line above the footer, not under it");
    }
}
