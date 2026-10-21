using NeonSidekick.Tests.Fakes;
using NeonSidekick.Viewer;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The YouTube tab's ⛶ Fill window (2026-10-20, the user's ask), in headless Edge and offline: the button only while this page owns a loaded
/// player, the video from the bar to the window's bottom with the header and the saved list gone, a message said while filled shown in the bar,
/// the player's node never moved or made anew, the label and the focus; and each way out (the button, Esc, Back, a closed video, a taken
/// player) putting the layout and the scroll offset back with our history entry gone, never piling up.
/// </summary>
public sealed partial class WebUiYouTubePageTests
{
    private const string Filled = "document.body.classList.contains('yt-windowed')";

    private const string Settled = "history.state === null && !backPending";

    /// <summary>The page made owner and given a load, with YouTube's script given a minute (offline it never loads, and a failure hides the button).</summary>
    private async Task<HeadlessPage?> LoadedPageAsync()
    {
        Player.Dispose();
        _backend.VideoPlayer = new WebVideoPlayer(apiWaitMs: 60_000);
        if (await SignedInAsync("/youtube") is not { } page)
        {
            return null;
        }

        await page.WaitAsync("document.getElementById('hint').textContent.length > 0", "the tab's words");
        Assert.True(SpinWait.SpinUntil(() => Player.Owner is not null, TimeSpan.FromSeconds(10)), "the page never became the owner");
        Assert.True(await HiddenAsync(page, "#fill"));

        Player.Play(new VideoRequest("aqz-KE-bpKQ"));
        await page.WaitAsync("!document.getElementById('fill').classList.contains('hidden')", "the fill button with a loaded player");
        Assert.Equal(WebUiText.VideoWords.Windowed, await page.TextAsync("document.getElementById('fill').textContent"));

        // Something to scroll, so the offset put back on leaving is seen.
        await page.EvalAsync("(() => { const s = document.getElementById('saved'); s.classList.remove('hidden'); s.style.minHeight = '3000px'; window.scrollTo(0, 400); })()");
        Assert.Equal(400, (await page.EvalAsync("Math.round(window.scrollY)")).GetInt32());
        return page;
    }

    private static async Task EnterAsync(HeadlessPage page)
    {
        await page.EvalAsync("document.getElementById('fill').click()");
        await page.WaitAsync(Filled + " && history.state && history.state.filled", "the window filled");
    }

    private static async Task AssertNormalAsync(HeadlessPage page, string way)
    {
        await page.WaitAsync("!" + Filled + " && " + Settled, "the normal layout again after " + way);
        Assert.Equal(400, (await page.EvalAsync("Math.round(window.scrollY)")).GetInt32());
        Assert.NotEqual("fixed", await page.TextAsync("getComputedStyle(document.querySelector('.yt-frame')).position"));
        Assert.NotEqual("none", await page.TextAsync("getComputedStyle(document.querySelector('header.top')).display"));
    }

    [WindowsFact]
    public async Task FillWindow_FillsTheWindowBelowTheBar_KeepsThePlayer_AndShowsAMessageInTheBar()
    {
        if (await LoadedPageAsync() is not { } page)
        {
            return;
        }

        await page.EvalAsync("window.__node = document.getElementById('player')");
        await EnterAsync(page);

        Assert.Equal(WebUiText.VideoWords.ExitWindowed, await page.TextAsync("document.getElementById('fill').textContent"));
        Assert.Equal("fill", await page.TextAsync("document.activeElement && document.activeElement.id"));
        Assert.Equal("none", await page.TextAsync("getComputedStyle(document.getElementById('saved')).display"));
        Assert.Equal("none", await page.TextAsync("getComputedStyle(document.querySelector('header.top')).display"));
        await page.WaitAsync("Math.abs(document.querySelector('.yt-frame').getBoundingClientRect().bottom - window.innerHeight) < 1"
            + " && Math.abs(document.querySelector('.yt-frame').getBoundingClientRect().top - document.querySelector('.yt-strip').getBoundingClientRect().bottom) < 1"
            + " && document.querySelector('.yt-strip').getBoundingClientRect().top === 0",
            "the video from the bar to the window's bottom");
        Assert.True((await page.EvalAsync("document.getElementById('player') === window.__node && document.querySelector('.yt-frame').contains(window.__node)")).GetBoolean());
        Assert.Equal(0, (await page.EvalAsync("Math.round(document.documentElement.scrollHeight - window.innerHeight)")).GetInt32());

        // A sentence said while filled: in the bar, under the strip, the video's top moved down to it.
        await page.EvalAsync("Neon.say('A sentence while filled')");
        await page.WaitAsync("(() => { const m = document.getElementById('message').getBoundingClientRect(); const s = document.querySelector('.yt-strip').getBoundingClientRect();"
            + " const f = document.querySelector('.yt-frame').getBoundingClientRect(); return m.height > 0 && Math.abs(m.top - s.bottom) < 1 && Math.abs(f.top - m.bottom) < 1; })()",
            "the message in the bar, the video under it");
        await page.EvalAsync("Neon.say('')");
        await page.WaitAsync("Math.abs(document.querySelector('.yt-frame').getBoundingClientRect().top - document.querySelector('.yt-strip').getBoundingClientRect().bottom) < 1",
            "the video back up under the strip");

        await page.EvalAsync("document.getElementById('fill').click()");
        await AssertNormalAsync(page, "the button");
        Assert.Equal(WebUiText.VideoWords.Windowed, await page.TextAsync("document.getElementById('fill').textContent"));
        Assert.True((await page.EvalAsync("document.getElementById('player') === window.__node")).GetBoolean());
    }

    [WindowsFact]
    public async Task FillWindow_EscAndBack_LeaveIt_AndTwoRoundsAddOneHistoryEntryAtMost()
    {
        if (await LoadedPageAsync() is not { } page)
        {
            return;
        }

        int before = (await page.EvalAsync("history.length")).GetInt32();

        await EnterAsync(page);
        await page.EvalAsync("document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))");
        await AssertNormalAsync(page, "Esc");

        await EnterAsync(page);
        await page.EvalAsync("history.back()");
        await AssertNormalAsync(page, "Back");

        await EnterAsync(page);
        await page.EvalAsync("document.getElementById('fill').click()");
        await AssertNormalAsync(page, "the button");

        Assert.InRange((await page.EvalAsync("history.length")).GetInt32(), before, before + 1);
    }

    [WindowsFact]
    public async Task FillWindow_EndsWhenTheVideoCloses()
    {
        if (await LoadedPageAsync() is not { } page)
        {
            return;
        }

        await EnterAsync(page);
        await page.EvalAsync("source.dispatchEvent(new MessageEvent('close', { data: '{}' }))");
        await AssertNormalAsync(page, "a closed video");
        Assert.True(await HiddenAsync(page, "#fill"));
        Assert.Equal(WebUiText.VideoWords.Closed, await page.TextAsync("document.getElementById('message').textContent"));
    }

    [WindowsFact]
    public async Task FillWindow_EndsWhenAnotherPageTakesThePlayer()
    {
        if (await LoadedPageAsync() is not { } page)
        {
            return;
        }

        await EnterAsync(page);
        using var other = Player.Attach(new string('z', 22), new string('9', 22), true, null, () => { });   // another page's Play here
        await AssertNormalAsync(page, "a taken player");
        Assert.True(await HiddenAsync(page, "#fill"));
        Assert.False(await HiddenAsync(page, "#play-here"));
        Assert.Equal(WebUiText.VideoWords.PlayHere, await page.TextAsync("document.getElementById('play-here').textContent"));
    }
}
