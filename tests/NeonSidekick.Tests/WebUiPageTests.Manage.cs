using System.Text.Json;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The 🗂️ sheet itself in the browser (2026-11-16, plan lucid-roaming-lantern; driven through its Sessions tab since 2026-11-21, when the Queue
/// tab that drove these moved to the 📣 sheet): 🗂️ named, its two tabs, ←/→ wrapping between them; the 📣 sheet's swap with it leaves no stale
/// entry, and a reload with one opens nothing; a sheet sent away while it reads never shows; the chat header at a phone's width keeps 🗂️ whole.
/// </summary>
public sealed partial class WebUiPageTests
{
    private const string ManageShown = "!document.getElementById('manage').classList.contains('hidden')";

    private const string ManageHidden = "document.getElementById('manage').classList.contains('hidden')";

    private static readonly WebSessionRow ManageRow = new(2, "The trip plan", "Mon 09:00", "5 turns", "qwen", false);

    private async Task<HeadlessPage?> ManagePageAsync()
    {
        _backend.SessionsFor = _ => new WebSessionsView("p", [ManageRow], [], true);
        _backend.Snap = new WebSnapshot([], [], State(), WebUiText.Words, null, null);
        return await SignedInAsync("/");
    }

    private static async Task OpenManageAsync(HeadlessPage page)
    {
        await page.WaitAsync("window.NeonManage !== undefined", "manage.js");
        await page.EvalAsync("window.NeonManage.open('sessions')");
        await page.WaitAsync(ManageShown + " && document.querySelectorAll('#manage-list .session-row').length > 0", "the sheet on its Sessions tab");
    }

    [WindowsFact]
    public async Task TheManageSheet_IsNamedForItsTwoTabs_AndTheArrowsWrapBetweenThem()
    {
        if (await ManagePageAsync() is not { } page)
        {
            return;
        }

        await page.WaitAsync("document.getElementById('manage-button').getAttribute('aria-label') === " + JsonSerializer.Serialize(WebUiText.ManageButton), "🗂️'s name");
        await OpenManageAsync(page);
        Assert.Equal("sessions,files", await page.TextAsync("[...document.querySelectorAll('#manage .manage-tab')].map(t => t.dataset.tab).join()"));
        Assert.Equal("sessions", await page.TextAsync("document.activeElement.dataset.tab"));
        await KeyAsync(page, "ArrowRight", "document.querySelector('.manage-tab[data-tab=\"sessions\"]')");
        await page.WaitAsync("document.activeElement.dataset.tab === 'files'", "→ on to Files");
        await KeyAsync(page, "ArrowRight", "document.querySelector('.manage-tab[data-tab=\"files\"]')");
        await page.WaitAsync("document.activeElement.dataset.tab === 'sessions'", "→ wraps back to Sessions");
    }

    [WindowsFact]
    public async Task ThePromptsSheetAndTheManageSheet_SwapPlaces_AndAReloadLeavesNoEntry()
    {
        if (await ManagePageAsync() is not { } page)
        {
            return;
        }

        await OpenManageAsync(page);
        await OpenSheetAsync(page);
        Assert.True((await page.EvalAsync(ManageHidden + " && history.state.prompts === true")).GetBoolean());
        await page.EvalAsync("document.getElementById('manage-button').click()");
        await page.WaitAsync(ManageShown + " && " + SheetHidden + " && history.state.manage === true", "the 🗂️ sheet in its place");
        await page.EvalAsync("history.back()");
        await page.WaitAsync(ManageHidden + " && history.state === null", "one Back: the page's own entry");

        await OpenManageAsync(page);
        await page.EvalAsync("location.reload()");
        await page.WaitAsync("document.getElementById('manage-button') !== null && history.state === null", "a reload's own entry");
        Assert.True((await page.EvalAsync(ManageHidden)).GetBoolean());
    }

    /// <summary>
    /// A sheet still reading counts as open (the review's #132): sent away before its read answers, it never shows and leaves no history entry.
    /// </summary>
    [WindowsFact]
    public async Task ASheetSentAwayWhileItReads_NeverShows()
    {
        if (await ManagePageAsync() is not { } page)
        {
            return;
        }

        await page.WaitAsync("window.NeonManage !== undefined", "manage.js");
        await page.EvalAsync("(() => { const real = Neon.get; window.__release = null; Neon.get = (p, e) => new Promise(r => { window.__release = () => real(p, e).then(r); }); })()");
        await page.EvalAsync("setTimeout(() => window.NeonManage.open('sessions'), 0)");   // its promise waits on the held read
        await page.WaitAsync("window.__release !== null && manageOpen() && " + ManageHidden, "open while it reads, not shown");
        await page.EvalAsync("window.NeonManage.close(false); window.__release(); window.__released = true");
        await page.WaitAsync("window.__released === true && !manageOpen()", "called off");
        await Task.Delay(300);
        Assert.True((await page.EvalAsync(ManageHidden + " && history.state === null")).GetBoolean(), "it never showed");
    }

    /// <summary>
    /// At a phone's width the chat page's header holds 🗂️ whole beside 🔈 and the nav, the profile's name keeping 40% of it (its ellipsis gives
    /// the rest), and nothing overflows.
    /// </summary>
    [WindowsFact]
    public async Task TheChatHeader_At360Pixels_Keeps_TheManageButtonWhole_AndTheProfileItsRoom()
    {
        _backend.Snap = new WebSnapshot([], [], State() with { VoiceSpeak = "" } with { Profile = "a rather long profile name for a phone" }, WebUiText.Words);
        if (await SignedInAsync("/", width: 360) is not { } page)
        {
            return;
        }

        await page.WaitAsync("document.querySelector('header.top nav').classList.contains('ready') && !document.getElementById('speak').classList.contains('hidden')", "the nav and 🔈");
        double share = (await page.EvalAsync("document.querySelector('header.top .who').getBoundingClientRect().width / document.querySelector('header.top').getBoundingClientRect().width")).GetDouble();
        Assert.True(share >= 0.4, $"the profile's name keeps {share:P0} of the header");
        Assert.True((await page.EvalAsync("(() => { const b = document.getElementById('manage-button'); return b.scrollWidth <= b.clientWidth && b.getBoundingClientRect().width >= 30; })()")).GetBoolean(), "🗂️ whole");
        Assert.True((await page.EvalAsync("document.querySelector('header.top').scrollWidth <= document.querySelector('header.top').clientWidth")).GetBoolean(), "no overflow");
    }
}
