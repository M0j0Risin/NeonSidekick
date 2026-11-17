using System.Text.Json;
using NeonSidekick.App;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The 🗂️ sheet and its Queue tab in the browser (2026-11-16, plan lucid-roaming-lantern #42): 🗂️ named and opening the sheet with focus on its
/// tab; both sides drawn, a chord's line without ✕; ✕ arms on the first tap and posts on the second; Send now and Resume only when offered; a
/// mode tap posts and the re-read on the queue tag marks the new one; the ⏳ lines open the tab by tap and Enter; Back, Esc and the 📣 sheet's
/// swap with it leave no stale entry, and a reload with one opens nothing.
/// </summary>
public sealed partial class WebUiPageTests
{
    private const string ManageShown = "!document.getElementById('manage').classList.contains('hidden')";

    private const string ManageHidden = "document.getElementById('manage').classList.contains('hidden')";

    private static WebQueueView QueueView(bool sendOffered = false, bool held = false, string mode = "automatic") =>
        new([new WebQueuePcLine(0, "at the pc", 2)], [new WebQueueLine(4, "from the phone", false), new WebQueueLine(5, App.BotChat.ResumeLine, true)],
            mode, "empty", held, sendOffered, QueueMode.Names, QueueCancelMode.Names);

    private async Task<HeadlessPage?> QueuePageAsync(WebQueueView view, WebState? state = null)
    {
        _backend.QueueView = view;
        _backend.Snap = new WebSnapshot([], [], state ?? State(), WebUiText.Words, null, null);
        return await SignedInAsync("/");
    }

    private static async Task OpenManageAsync(HeadlessPage page)
    {
        await page.WaitAsync("window.NeonManage !== undefined", "manage.js");
        await page.EvalAsync("window.NeonManage.open('queue')");
        await page.WaitAsync(ManageShown + " && document.querySelectorAll('#manage-list .manage-row').length > 0", "the sheet on its Queue tab");
    }

    [WindowsFact]
    public async Task TheQueueTab_DrawsBothSides_AndItsXTakesTwoTaps()
    {
        if (await QueuePageAsync(QueueView()) is not { } page)
        {
            return;
        }

        await page.WaitAsync("document.getElementById('manage-button').getAttribute('aria-label') === " + JsonSerializer.Serialize(WebUiText.ManageButton), "🗂️'s name");
        await OpenManageAsync(page);
        Assert.Equal("queue", await page.TextAsync("document.activeElement.dataset.tab"));   // focus on the tab, the sheet at the bottom
        Assert.Equal("at the pc|from the phone|" + App.BotChat.ResumeLine, await page.TextAsync("[...document.querySelectorAll('#manage-list .manage-text')].map(t => t.textContent).join('|')"));
        Assert.Equal("🖼️ 2", await page.TextAsync("document.querySelector('#manage-list .manage-pictures').textContent"));
        Assert.Equal(2, (await page.EvalAsync("document.querySelectorAll('#manage-list .manage-remove').length")).GetInt32());   // none on the chord's line
        Assert.True((await page.EvalAsync("document.querySelector('#manage-list .manage-row.chord') !== null")).GetBoolean());
        Assert.True((await page.EvalAsync("document.querySelector('.manage-send') === null && document.querySelector('.manage-resume') === null")).GetBoolean());

        await page.EvalAsync("document.querySelectorAll('#manage-list .manage-remove')[1].click()");
        await page.WaitAsync("document.querySelectorAll('#manage-list .manage-remove')[1].classList.contains('armed')", "armed");
        Assert.Empty(_backend.QueueActions);
        await page.EvalAsync("document.querySelectorAll('#manage-list .manage-remove')[1].click()");
        await UntilAsync(() => _backend.QueueActions.Contains("remove key 4"), "the page line's remove");

        await page.EvalAsync("document.querySelectorAll('#manage-list .manage-remove')[0].click(); document.querySelectorAll('#manage-list .manage-remove')[0].click()");
        await UntilAsync(() => _backend.QueueActions.Contains("remove 0 at the pc"), "the PC line's remove");
    }

    [WindowsFact]
    public async Task SendNowAndResume_ShowWhenOffered_AndAModeTap_IsMarkedAfterTheReRead()
    {
        if (await QueuePageAsync(QueueView(sendOffered: true, held: true)) is not { } page)
        {
            return;
        }

        await OpenManageAsync(page);
        Assert.Equal(WebUiText.QueueSendNow, await page.TextAsync("document.querySelector('.manage-send').textContent"));
        await page.EvalAsync("document.querySelector('.manage-send').click()");
        await UntilAsync(() => _backend.QueueActions.Contains("send"), "Send now");
        await page.EvalAsync("document.querySelector('.manage-resume').click()");
        await UntilAsync(() => _backend.Resumes == 1, "Resume");

        const string Pressed = "document.querySelector('.manage-choice[aria-pressed=\"true\"]').textContent";
        Assert.Equal("automatic", await page.TextAsync(Pressed));
        await page.EvalAsync("[...document.querySelectorAll('.manage-choice')].find(b => b.textContent === 'manual').click()");
        await UntilAsync(() => _backend.QueueActions.Contains("mode Manual"), "the mode's post");
        _backend.QueueView = QueueView(sendOffered: true, held: true, mode: "manual");
        Publish("state", Json(State() with { QueueTag = "moved" }));
        await page.WaitAsync(Pressed + " === 'manual'", "manual marked after the re-read");
    }

    [WindowsFact]
    public async Task TheWaitingLines_OpenTheQueueTab_ByTapAndByEnter_AndBackAndEscClose()
    {
        var state = State() with { Queued = ["from the phone"] };
        if (await QueuePageAsync(QueueView(), state) is not { } page)
        {
            return;
        }

        await page.WaitAsync("window.NeonManage !== undefined && !document.getElementById('queued').classList.contains('hidden')", "the ⏳ line");
        await page.EvalAsync("document.getElementById('queued').click()");
        await page.WaitAsync(ManageShown + " && history.state && history.state.manage", "the Queue tab by a tap");
        await page.EvalAsync("history.back()");
        await page.WaitAsync(ManageHidden + " && history.state === null", "Back: the page's own entry");

        await KeyAsync(page, "Enter", "document.getElementById('queued')");
        await page.WaitAsync(ManageShown, "the Queue tab by Enter");
        await KeyAsync(page, "Escape", "document.body");
        await page.WaitAsync(ManageHidden + " && history.state === null", "Esc closes it and its entry");
    }

    [WindowsFact]
    public async Task ThePromptsSheetAndTheManageSheet_SwapPlaces_AndAReloadLeavesNoEntry()
    {
        if (await QueuePageAsync(QueueView()) is not { } page)
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
        if (await QueuePageAsync(QueueView()) is not { } page)
        {
            return;
        }

        await page.WaitAsync("window.NeonManage !== undefined", "manage.js");
        await page.EvalAsync("(() => { const real = Neon.get; window.__release = null; Neon.get = (p, e) => new Promise(r => { window.__release = () => real(p, e).then(r); }); })()");
        await page.EvalAsync("setTimeout(() => window.NeonManage.open('queue'), 0)");   // its promise waits on the held read
        await page.WaitAsync("window.__release !== null && manageOpen() && " + ManageHidden, "open while it reads, not shown");
        await page.EvalAsync("window.NeonManage.close(false); window.__release(); window.__released = true");
        await page.WaitAsync("window.__released === true && !manageOpen()", "called off");
        await Task.Delay(300);
        Assert.True((await page.EvalAsync(ManageHidden + " && history.state === null")).GetBoolean(), "it never showed");
    }

    /// <summary>A re-read the PC answers with a 304 draws nothing (the review's #91): an armed ✕ stays armed.</summary>
    [WindowsFact]
    public async Task AReReadAnsweredWith304_KeepsAnArmedX()
    {
        if (await QueuePageAsync(QueueView()) is not { } page)
        {
            return;
        }

        await OpenManageAsync(page);
        await page.EvalAsync("document.querySelectorAll('#manage-list .manage-remove')[0].click()");
        await page.WaitAsync("document.querySelectorAll('#manage-list .manage-remove')[0].classList.contains('armed')", "armed");
        await page.EvalAsync("(() => { const real = Neon.get; window.__status = 0; Neon.get = (p, e) => real(p, e).then(r => { window.__status = r.status; return r; }); })()");
        await page.EvalAsync("window.NeonManage.reset()");
        await page.WaitAsync("window.__status === 304", "the 304");
        Assert.True((await page.EvalAsync("document.querySelectorAll('#manage-list .manage-remove')[0].classList.contains('armed')")).GetBoolean(), "still armed");
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
