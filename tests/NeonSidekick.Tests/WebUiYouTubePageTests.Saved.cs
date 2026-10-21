using System.Text.Json;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Viewer;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The YouTube tab in headless Edge, later on 2026-10-16: the page saying whether it is in view (and again on a reconnect), <c>taken</c>
/// stopping the player, a held video surviving the page's first report, a fresh claim id per Play here tap; and the saved list under the
/// player (the filter, the moves, a two-tap remove, a row's play reaching the player, ☆ to ★).
/// </summary>
public sealed partial class WebUiYouTubePageTests
{
    private async Task<string> PageIdAsync(HeadlessPage page) => await page.TextAsync("sessionStorage.getItem('neon.youtube.page')");

    // ── In view ─────────────────────────────────────────────────────────────

    [WindowsFact]
    public async Task ThePage_SaysWhenItHides_AndSaysAgainOnAReconnectsOpen()
    {
        if (await SignedInAsync("/youtube") is not { } page)
        {
            return;
        }

        SpinWait.SpinUntil(() => Player.Owner is not null, TimeSpan.FromSeconds(10));
        string id = await PageIdAsync(page);
        Assert.True(Player.VisibleOf(id));

        await page.EvalAsync("Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => 'hidden' }); document.dispatchEvent(new Event('visibilitychange'))");
        SpinWait.SpinUntil(() => Player.VisibleOf(id) == false, TimeSpan.FromSeconds(10));
        Assert.False(Player.VisibleOf(id));

        Player.Seen(id, true, null);   // as a stale visible=1 on the browser's own reconnect would leave it
        await page.EvalAsync("source.onopen()");
        SpinWait.SpinUntil(() => Player.VisibleOf(id) == false, TimeSpan.FromSeconds(10));
        Assert.False(Player.VisibleOf(id));
    }

    [WindowsFact]
    public async Task Taken_StopsThePlayer()
    {
        if (await SignedInAsync("/youtube") is not { } page)
        {
            return;
        }

        SpinWait.SpinUntil(() => Player.Owner is not null, TimeSpan.FromSeconds(10));
        await page.EvalAsync("window.__msgs = []; (() => { const o = core.onMessage; core.onMessage = m => { window.__msgs.push(m); o(m); }; })()");
        using var other = Player.Attach(new string('z', 22), new string('9', 22), true, null, () => { });   // another page's Play here

        await page.WaitAsync("window.__msgs.some(m => m.cmd === 'stop')", "the stop");
        await page.WaitAsync("!document.getElementById('play-here').classList.contains('hidden')", "Play here again");
        Assert.DoesNotContain("pause", await page.TextAsync("JSON.stringify(window.__msgs.map(m => m.cmd))"), StringComparison.Ordinal);
    }

    [WindowsFact]
    public async Task AHeldVideo_PlaysOnThePageThatOpens_AndItsFirstReportNeverClosesIt()
    {
        Player.Play(new VideoRequest("aqz-KE-bpKQ"));   // no page attached: held
        Assert.NotNull(Player.Snapshot?.Held);
        if (await SignedInAsync("/youtube") is not { } page)
        {
            return;
        }

        // The page's first report (page, with its load) comes at its hello; offline, YouTube's script never loads, so its failure after 1.5 s
        // is the next report. A video the page report closed would refuse that one: the snapshot would stay null.
        SpinWait.SpinUntil(() => Player.Snapshot?.State == VideoState.Failed, TimeSpan.FromSeconds(15));
        Assert.Equal("aqz-KE-bpKQ", Player.Snapshot?.VideoId);
        Assert.Equal(VideoState.Failed, Player.Snapshot?.State);
        Assert.Equal(await PageIdAsync(page), Player.Owner);
    }

    [WindowsFact]
    public async Task PlayHere_SendsAFreshClaimIdEachTap()
    {
        string otherPage = new('z', 22);
        using var other = Player.Attach(otherPage, new string('9', 22), false, null, () => { });
        Player.Play(new VideoRequest("aqz-KE-bpKQ"));
        Player.Report(otherPage, "{\"ev\":\"state\",\"id\":\"aqz-KE-bpKQ\",\"state\":1,\"position\":3,\"duration\":635}", null);
        if (await SignedInAsync("/youtube") is not { } page)
        {
            return;
        }

        await page.WaitAsync("!document.getElementById('play-here').classList.contains('hidden')", "Play here");
        await page.EvalAsync("window.__urls = []; (() => { const E = window.EventSource; window.EventSource = function (u) { window.__urls.push(u); return new E(u); }; window.EventSource.CLOSED = 2; })()");
        await page.EvalAsync("document.getElementById('play-here').click()");
        await page.WaitAsync("document.getElementById('play-here').classList.contains('hidden')", "the first claim");

        using var back = Player.Attach(otherPage, new string('8', 22), true, null, () => { });   // the other page claims back (its first script's claim=1)
        await page.WaitAsync("!document.getElementById('play-here').classList.contains('hidden')", "Play here again");
        await page.EvalAsync("document.getElementById('play-here').click()");
        await page.WaitAsync("window.__urls.length === 2", "the second claim's stream");

        using var doc = JsonDocument.Parse(await page.TextAsync("JSON.stringify(window.__urls.map(u => new URL(u, location.href).searchParams.get('claim')))"));
        string[] claims = [.. doc.RootElement.EnumerateArray().Select(e => e.GetString() ?? "")];
        Assert.Equal(2, claims.Length);
        Assert.All(claims, c => Assert.True(WebVideoPlayer.IsId(c)));
        Assert.NotEqual(claims[0], claims[1]);
    }

    // ── The saved list ──────────────────────────────────────────────────────

    private void Saved(params WebSavedVideo[] videos)
    {
        lock (_backend.ListsGate)
        {
            _backend.SavedList.Clear();
            _backend.SavedList.AddRange(videos);
        }
    }

    private static readonly WebSavedVideo Bunny = FakeWebUiBackend.SavedVideo("aqz-KE-bpKQ", "Big Buck Bunny", "Blender");
    private static readonly WebSavedVideo Zoo = FakeWebUiBackend.SavedVideo("jNQXAC9IVRw", "Me at the zoo", "jawed");
    private static readonly WebSavedVideo Song = FakeWebUiBackend.SavedVideo("dQw4w9WgXcQ", "A song", "someone", watched: true);

    [WindowsFact]
    public async Task TheSavedList_DrawsUnderThePlayer_AndTheFilterHidesRowsAndTheMoves()
    {
        Saved(Bunny, Zoo, Song);
        if (await SignedInAsync("/youtube") is not { } page)
        {
            return;
        }

        await page.WaitAsync("document.querySelectorAll('.yt-row').length === 3", "the three rows");
        Assert.True((await page.EvalAsync("!!(document.querySelector('.yt-frame').compareDocumentPosition(document.getElementById('saved')) & Node.DOCUMENT_POSITION_FOLLOWING)")).GetBoolean());
        Assert.Equal("Me at the zoo", await page.TextAsync("document.querySelectorAll('.yt-row')[1].querySelector('.yt-title').textContent"));
        Assert.True((await page.EvalAsync("document.querySelectorAll('.yt-row')[2].classList.contains('yt-watched')")).GetBoolean());
        Assert.Equal("https://i.ytimg.com/vi/jNQXAC9IVRw/mqdefault.jpg", await page.TextAsync("document.querySelectorAll('.yt-row')[1].querySelector('.yt-thumb').getAttribute('src')"));
        Assert.Equal("", await page.TextAsync("document.querySelector('.yt-thumb').alt"));
        Assert.Equal(WebUiText.VideoWords.Saved!.Details, await page.TextAsync("document.querySelector('.yt-more').getAttribute('aria-label')"));

        // ⋯ opens a row's details and its four moves and remove, aria-expanded with it.
        await page.EvalAsync("document.querySelector('.yt-more').click()");
        Assert.Equal("true", await page.TextAsync("document.querySelector('.yt-more').getAttribute('aria-expanded')"));
        Assert.Equal(4, (await page.EvalAsync("document.querySelectorAll('.yt-details:not(.hidden) .yt-move').length")).GetInt32());

        // A filter: one row, no moves (the order is the whole list's).
        await page.EvalAsync("(() => { const f = document.getElementById('saved-filter'); f.value = 'zoo'; f.dispatchEvent(new Event('input')); })()");
        await page.WaitAsync("document.querySelectorAll('.yt-row').length === 1", "the filtered row");
        await page.EvalAsync("document.querySelector('.yt-more').click()");
        Assert.Equal(0, (await page.EvalAsync("document.querySelectorAll('.yt-move').length")).GetInt32());

        // By the channel and id too, and the no-match line.
        await page.EvalAsync("(() => { const f = document.getElementById('saved-filter'); f.value = 'nothing like it'; f.dispatchEvent(new Event('input')); })()");
        await page.WaitAsync("document.querySelectorAll('.yt-row').length === 0", "no rows");
        Assert.Equal(WebUiText.VideoWords.Saved!.NoMatch.Replace("{0}", "nothing like it", StringComparison.Ordinal), await page.TextAsync("document.getElementById('saved-note').textContent"));
    }

    [WindowsFact]
    public async Task ATwoTapRemove_PostsOnce_AndAMovePosts()
    {
        Saved(Bunny, Zoo);
        if (await SignedInAsync("/youtube") is not { } page)
        {
            return;
        }

        await page.WaitAsync("document.querySelectorAll('.yt-row').length === 2", "the rows");
        await page.EvalAsync("document.querySelectorAll('.yt-more')[1].click()");
        await page.EvalAsync("document.querySelectorAll('.yt-row')[1].querySelector('.yt-move').click()");   // ▲
        SpinWait.SpinUntil(() => _backend.SavedChanges.Contains("move jNQXAC9IVRw up"), TimeSpan.FromSeconds(10));
        Assert.Contains("move jNQXAC9IVRw up", _backend.SavedChanges);
        await page.WaitAsync("document.querySelector('.yt-row').dataset.id === 'jNQXAC9IVRw'", "the moved row first, its details kept open");
        Assert.False(await HiddenAsync(page, ".yt-row .yt-details"));

        await page.EvalAsync("document.querySelector('.yt-row .yt-delete').click()");
        Assert.True((await page.EvalAsync("document.querySelector('.yt-row .yt-delete').classList.contains('armed')")).GetBoolean());
        await Task.Delay(300);
        Assert.DoesNotContain(_backend.SavedChanges, c => c.StartsWith("remove", StringComparison.Ordinal));
        await page.EvalAsync("document.querySelector('.yt-row .yt-delete').click()");
        await page.WaitAsync("document.querySelectorAll('.yt-row').length === 1", "the row gone");
        Assert.Single(_backend.SavedChanges, c => c.StartsWith("remove", StringComparison.Ordinal));
    }

    [WindowsFact]
    public async Task ARowsTap_PostsAPlay_AndTheLoadThatFollowsReachesThePlayer_WithItsStart()
    {
        Saved(Bunny);
        if (await SignedInAsync("/youtube") is not { } page)
        {
            return;
        }

        await page.WaitAsync("document.querySelectorAll('.yt-row').length === 1", "the row");
        SpinWait.SpinUntil(() => Player.Owner is not null, TimeSpan.FromSeconds(10));
        await page.EvalAsync("window.__msgs = []; (() => { const o = core.onMessage; core.onMessage = m => { window.__msgs.push(m); o(m); }; })()");
        await page.EvalAsync("document.querySelector('.yt-play').click()");

        await page.WaitAsync("window.__msgs.some(m => m.cmd === 'load' && m.id === 'aqz-KE-bpKQ' && m.start === 57)", "the load with its start");
        Assert.Contains("play " + await PageIdAsync(page) + " aqz-KE-bpKQ", _backend.SavedChanges);
        Assert.Contains("0:57", await page.TextAsync("document.getElementById('message').textContent"), StringComparison.Ordinal);
    }

    [WindowsFact]
    public async Task ASavedPlaysMessage_FadesOnceItPlays_AndALaterMessageStays()
    {
        // 2026-10-20, the user's ask: "Resumed the saved video at …" goes shortly after the video starts. The player's state reports are
        // fed to the page's own send (offline, YouTube's script never loads); the wait is shortened.
        Saved(Bunny);
        if (await SignedInAsync("/youtube") is not { } page)
        {
            return;
        }

        await page.WaitAsync("document.querySelectorAll('.yt-row').length === 1", "the row");
        SpinWait.SpinUntil(() => Player.Owner is not null, TimeSpan.FromSeconds(10));
        await page.EvalAsync("playNoteMs = 200");
        await page.EvalAsync("document.querySelector('.yt-play').click()");
        await page.WaitAsync("document.getElementById('message').textContent.includes('0:57')", "the play's sentence");
        await page.EvalAsync("send({ ev: 'state', id: 'aqz-KE-bpKQ', state: 1, position: 57, duration: 635 })");
        await page.WaitAsync("document.getElementById('message').textContent === ''", "the sentence gone once it plays");

        // Paused, played again, and something else said before it starts: that stays.
        await page.EvalAsync("send({ ev: 'state', id: 'aqz-KE-bpKQ', state: 2, position: 60, duration: 635 })");
        await page.EvalAsync("document.querySelector('.yt-play').click()");
        await page.WaitAsync("document.getElementById('message').textContent.includes('0:57')", "the second play's sentence");
        await page.EvalAsync("Neon.say('something else')");
        await page.EvalAsync("send({ ev: 'state', id: 'aqz-KE-bpKQ', state: 1, position: 57, duration: 635 })");
        await Task.Delay(800);
        Assert.Equal("something else", await page.TextAsync("document.getElementById('message').textContent"));
    }

    [WindowsFact]
    public async Task TheCountLine_HidesWhileAVideoPlays_AndShowsWhenPausedOrClosed()
    {
        // 2026-10-20, the user's ask: "N saved videos; …" hidden while playing (its room kept), shown paused, stopped or with nothing loaded.
        Saved(Bunny, Zoo);
        if (await SignedInAsync("/youtube") is not { } page)
        {
            return;
        }

        const string Quiet = "document.getElementById('saved-caption').classList.contains('yt-caption-quiet')";
        await page.WaitAsync("document.querySelectorAll('.yt-row').length === 2", "the rows");
        Assert.NotEqual("", await page.TextAsync("document.getElementById('saved-caption').textContent"));
        Assert.False((await page.EvalAsync(Quiet)).GetBoolean());

        await page.EvalAsync("send({ ev: 'state', id: 'aqz-KE-bpKQ', state: 1, position: 3, duration: 635 })");
        await page.WaitAsync(Quiet, "hidden while it plays");
        Assert.Equal("hidden", await page.TextAsync("getComputedStyle(document.getElementById('saved-caption')).visibility"));
        await page.EvalAsync("send({ ev: 'state', id: 'aqz-KE-bpKQ', state: 3, position: 3, duration: 635 })");   // a buffer mid-play
        Assert.True((await page.EvalAsync(Quiet)).GetBoolean());

        await page.EvalAsync("send({ ev: 'state', id: 'aqz-KE-bpKQ', state: 2, position: 4, duration: 635 })");   // paused
        await page.WaitAsync("!" + Quiet, "shown while paused");

        await page.EvalAsync("send({ ev: 'state', id: 'aqz-KE-bpKQ', state: 1, position: 4, duration: 635 })");
        await page.WaitAsync(Quiet, "hidden again");
        await page.EvalAsync("source.dispatchEvent(new MessageEvent('close', { data: '{}' }))");   // the app closed the video
        await page.WaitAsync("!" + Quiet, "shown once closed");
    }

    [WindowsFact]
    public async Task TheSaveButton_TurnsFromStarToSaved()
    {
        _backend.SavedPlaying = new WebSavedPlaying("aqz-KE-bpKQ", "Big Buck Bunny", false);
        if (await SignedInAsync("/youtube") is not { } page)
        {
            return;
        }

        await page.WaitAsync("!document.getElementById('saved-save').classList.contains('hidden')", "the ☆");
        Assert.Equal(WebUiText.VideoWords.Saved!.SavePlaying, await page.TextAsync("document.getElementById('saved-save').textContent"));

        await page.EvalAsync("document.getElementById('saved-save').click()");
        await page.WaitAsync("document.getElementById('saved-save').textContent === " + JsonSerializer.Serialize(WebUiText.VideoWords.Saved!.Saved), "the ★");
        Assert.True((await page.EvalAsync("document.getElementById('saved-save').disabled")).GetBoolean());
        Assert.Contains("save", _backend.SavedChanges);
        await page.WaitAsync("document.querySelectorAll('.yt-row').length === 1", "the saved row");
    }
}
