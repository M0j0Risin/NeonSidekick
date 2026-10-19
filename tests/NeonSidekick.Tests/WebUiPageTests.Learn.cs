using System.Text.Json;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The status row's 🧠 in the browser (2026-10-17, the user's ask): shown while the status says a reflection runs — the row showing for it
/// alone — and hidden after; one tap arms it, the second posts the cancel; armed and then hidden by the reflection's end, the next 🧠's first
/// tap only arms.
/// </summary>
public sealed partial class WebUiPageTests
{
    [WindowsFact]
    public async Task Chat_TheBrain_ShowsWhileAReflectionRuns_AndTwoTapsCancelIt()
    {
        _backend.Snap = new WebSnapshot([], [], State(), WebUiText.Words, null, new WebStatus(null, null, null, null, Learning: true));
        if (await SignedInAsync("/") is not { } page)
        {
            return;
        }

        await page.WaitAsync("!document.getElementById('learn').classList.contains('hidden') && !document.getElementById('status').classList.contains('hidden')", "the 🧠 on a row with nothing else");
        Assert.Equal(WebUiText.LearnCancel, await page.TextAsync("document.getElementById('learn').getAttribute('aria-label')"));

        await page.EvalAsync("document.getElementById('learn').click()");
        await page.WaitAsync("document.getElementById('learn').classList.contains('armed')", "armed by the first tap");
        Assert.Equal(WebUiText.LearnCancelArmed, await page.TextAsync("document.getElementById('learn').getAttribute('aria-label')"));
        Assert.Equal(0, _backend.LearnCancels);
        await page.EvalAsync("document.getElementById('learn').click()");
        await UntilAsync(() => _backend.LearnCancels == 1, "the second tap's post");

        // Armed, then the reflection ends: the 🧠 goes, disarmed; the next one's first tap only arms.
        await page.EvalAsync("document.getElementById('learn').click()");
        await page.WaitAsync("document.getElementById('learn').classList.contains('armed')", "armed again");
        Publish(new WebStatus(null, null, null, null, Learning: false));
        await page.WaitAsync("document.getElementById('learn').classList.contains('hidden') && !document.getElementById('learn').classList.contains('armed')", "hidden and disarmed");
        await page.WaitAsync("document.getElementById('status').classList.contains('hidden')", "the row gone with it");
        Publish(new WebStatus(null, null, null, null, Learning: true));
        await page.WaitAsync("!document.getElementById('learn').classList.contains('hidden')", "the next reflection's 🧠");
        await page.EvalAsync("document.getElementById('learn').click()");
        await page.WaitAsync("document.getElementById('learn').classList.contains('armed')", "only armed");
        await Task.Delay(300);
        Assert.Equal(1, _backend.LearnCancels);
    }

    private void Publish(WebStatus status) =>
        PublishWhenStreaming(new WebEvent("status", () => JsonSerializer.Serialize(status, WebUiJsonContext.Default.WebStatus)));

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "timed out waiting for " + what);
            await Task.Delay(25);
        }
    }
}
