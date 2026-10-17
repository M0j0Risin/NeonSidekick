using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The held queue's banner in the browser (2026-10-17, the user's question): shown over the waiting lines while the state says they are held
/// after a cancel, its Resume posting and hiding it; gone when the state no longer says so.
/// </summary>
public sealed partial class WebUiPageTests
{
    [WindowsFact]
    public async Task Chat_TheHeldQueue_ShowsItsBanner_AndResumePostsAndHidesIt()
    {
        var held = new WebState("p", "", "", "m", false, false, false, "", ["what is this?"], null, "1", "tag", QueueHeld: WebUiText.QueueHeld);
        _backend.Snap = new WebSnapshot([], [], held, WebUiText.Words);
        if (await SignedInAsync("/") is not { } page)
        {
            return;
        }

        await page.WaitAsync("!document.getElementById('queue-held').classList.contains('hidden')", "the held banner");
        Assert.Equal(WebUiText.QueueHeld, await page.TextAsync("document.querySelector('#queue-held .queue-held-text').textContent"));
        Assert.Equal(WebUiText.Resume, await page.TextAsync("document.getElementById('queue-resume').textContent"));
        Assert.False((await page.EvalAsync("document.getElementById('queued').classList.contains('hidden')")).GetBoolean());

        await page.EvalAsync("document.getElementById('queue-resume').click()");
        await UntilAsync(() => _backend.Resumes == 1, "Resume's post");
        await page.WaitAsync("document.getElementById('queue-held').classList.contains('hidden')", "the banner gone on an ok reply");
    }
}
