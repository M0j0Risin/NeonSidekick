using System.Text.Json;
using NeonSidekick.App;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The Queue tab in the browser (2026-11-16, plan lucid-roaming-lantern #42; the 📣 sheet's first tab since 2026-11-21, the user's ask, the 🗂️
/// sheet's third until then): a fresh page's button reads ⏳ and opens on it, focus on its tab; the tabs read Queue, Prompts, History, Memory;
/// both sides drawn, a chord's line without ✕; ✕ arms on the first tap and posts on the second; Send now and Resume only when offered; no bar
/// action on it; a mode tap posts, its message kept in the note, and the re-read on the queue tag marks the new one; the ⏳ lines and bare
/// <c>/queue</c> open the tab, or switch to it; Back and Esc close it; an open still reading never shows once sent away; a 304 keeps an armed ✕.
/// </summary>
public sealed partial class WebUiPageTests
{
    private const string QueueRows = "document.querySelectorAll('#prompts-list .manage-row')";

    private const string QueueRemoves = "document.querySelectorAll('#prompts-list .manage-remove')";

    private const string OnQueue = "document.querySelector('#prompts .sheet-tab.on').dataset.tab === 'queue'";

    private static WebQueueView QueueView(bool sendOffered = false, bool held = false, string mode = "automatic") =>
        new([new WebQueuePcLine(0, "at the pc", 2)], [new WebQueueLine(4, "from the phone", false), new WebQueueLine(5, App.BotChat.ResumeLine, true)],
            mode, "empty", held, sendOffered, QueueMode.Names, QueueCancelMode.Names);

    private async Task<HeadlessPage?> QueuePageAsync(WebQueueView view, WebState? state = null, int width = 390)
    {
        _backend.QueueView = view;
        _backend.Snap = new WebSnapshot([], [], state ?? State(), WebUiText.Words, null, null);
        return await SignedInAsync("/", width);
    }

    // The sheet on its Queue tab, through the button a fresh page shows ⏳ on, its rows drawn.
    private static async Task OpenQueueAsync(HeadlessPage page)
    {
        await page.EvalAsync("document.getElementById('prompts-button').click()");
        await page.WaitAsync(SheetShown + " && " + OnQueue + " && " + QueueRows + ".length > 0", "the sheet on its Queue tab");
    }

    [WindowsFact]
    public async Task TheQueueTab_IsTheFirst_DrawsBothSides_AndItsXTakesTwoTaps()
    {
        if (await QueuePageAsync(QueueView()) is not { } page)
        {
            return;
        }

        Assert.Equal("queue,prompts,history,memory", await page.TextAsync("[...document.querySelectorAll('#prompts .sheet-tab')].map(t => t.dataset.tab).join()"));
        Assert.Equal("sessions,files", await page.TextAsync("[...document.querySelectorAll('#manage .manage-tab')].map(t => t.dataset.tab).join()"));
        await page.WaitAsync("document.getElementById('prompts-button').getAttribute('aria-label') === " + JsonSerializer.Serialize(WebUiText.PromptsButton), "the button's name");
        Assert.Equal(WebUiText.QueueTab.Split(' ')[0], await page.TextAsync("document.getElementById('prompts-button').textContent"));   // ⏳ on a fresh page
        Assert.Equal("polite", await page.TextAsync("document.getElementById('prompts-note').getAttribute('aria-live')"));   // its messages read out

        await OpenQueueAsync(page);
        Assert.Equal("queue", await page.TextAsync("document.activeElement.dataset.tab"));   // focus on the tab, never a ✕ an Enter would arm
        Assert.True((await page.EvalAsync("document.getElementById('prompts-action').classList.contains('hidden') && document.getElementById('prompts-filter').classList.contains('hidden')")).GetBoolean());
        Assert.Equal("at the pc|from the phone|" + App.BotChat.ResumeLine, await page.TextAsync("[...document.querySelectorAll('#prompts-list .manage-text')].map(t => t.textContent).join('|')"));
        Assert.Equal("🖼️ 2", await page.TextAsync("document.querySelector('#prompts-list .manage-pictures').textContent"));
        // Each row keeps its 44 px (2026-11-20, the list's rows no longer shrink: flex none).
        Assert.True((await page.EvalAsync("Math.min(...[..." + QueueRows + "].map(r => r.getBoundingClientRect().height))")).GetDouble() >= 44);
        Assert.Equal(2, (await page.EvalAsync(QueueRemoves + ".length")).GetInt32());   // none on the chord's line
        Assert.True((await page.EvalAsync("document.querySelector('#prompts-list .manage-row.chord') !== null")).GetBoolean());
        Assert.True((await page.EvalAsync("document.querySelector('.manage-send') === null && document.querySelector('.manage-resume') === null")).GetBoolean());

        await page.EvalAsync(QueueRemoves + "[1].click()");
        await page.WaitAsync(QueueRemoves + "[1].classList.contains('armed')", "armed");
        Assert.Empty(_backend.QueueActions);
        await page.EvalAsync(QueueRemoves + "[1].click()");
        await UntilAsync(() => _backend.QueueActions.Contains("remove key 4"), "the page line's remove");

        await page.EvalAsync(QueueRemoves + "[0].click(); " + QueueRemoves + "[0].click()");
        await UntilAsync(() => _backend.QueueActions.Contains("remove 0 at the pc"), "the PC line's remove");

        // On to Prompts: the bar's action back, the button its glyph.
        await ClickTabAsync(page, "prompts");
        await page.WaitAsync("!document.getElementById('prompts-action').classList.contains('hidden') && document.getElementById('prompts-button').textContent === "
            + JsonSerializer.Serialize(WebUiText.PromptsTab.Split(' ')[0]), "the action back on Prompts");
    }

    [WindowsFact]
    public async Task SendNowAndResume_ShowWhenOffered_AndAModeTap_KeepsItsMessage_IsMarkedAfterTheReRead()
    {
        if (await QueuePageAsync(QueueView(sendOffered: true, held: true)) is not { } page)
        {
            return;
        }

        await OpenQueueAsync(page);
        Assert.Equal(WebUiText.QueueSendNow, await page.TextAsync("document.querySelector('.manage-send').textContent"));
        await page.EvalAsync("document.querySelector('.manage-send').click()");
        await UntilAsync(() => _backend.QueueActions.Contains("send"), "Send now");
        await page.EvalAsync("document.querySelector('.manage-resume').click()");
        await UntilAsync(() => _backend.Resumes == 1, "Resume");

        const string Pressed = "document.querySelector('.manage-choices[data-field=\"mode\"] .manage-choice[aria-checked=\"true\"]').dataset.name";
        Assert.Equal("automatic", await page.TextAsync(Pressed));
        _backend.QueueReply = new WebReply(WebOutcome.Ok, "Queue mode: manual");
        await page.EvalAsync("document.querySelector('.manage-choices[data-field=\"mode\"] [data-name=\"manual\"]').click()");
        await UntilAsync(() => _backend.QueueActions.Contains("mode Manual"), "the mode's post");
        _backend.QueueView = QueueView(sendOffered: true, held: true, mode: "manual");
        Publish("state", Json(State() with { QueueTag = "moved" }));
        await page.WaitAsync(Pressed + " === 'manual'", "manual marked after the re-read");
        Assert.Equal("Queue mode: manual", await page.TextAsync("document.getElementById('prompts-note').textContent"));   // the redraws left it
    }

    [WindowsFact]
    public async Task TheWaitingLines_OpenTheQueueTab_ByTapAndByEnter_OrSwitchToIt_AndBackAndEscClose()
    {
        var state = State() with { Queued = ["from the phone"] };
        if (await QueuePageAsync(QueueView(), state) is not { } page)
        {
            return;
        }

        await page.WaitAsync("!document.getElementById('queued').classList.contains('hidden')", "the ⏳ line");
        await page.EvalAsync("document.getElementById('queued').click()");
        await page.WaitAsync(SheetShown + " && " + OnQueue + " && history.state && history.state.prompts", "the Queue tab by a tap");
        await page.EvalAsync("history.back()");
        await page.WaitAsync(SheetHidden + " && history.state === null", "Back: the page's own entry");

        await KeyAsync(page, "Enter", "document.getElementById('queued')");
        await page.WaitAsync(SheetShown + " && " + OnQueue, "the Queue tab by Enter");
        await KeyAsync(page, "Escape", "document.body");
        await page.WaitAsync(SheetHidden + " && history.state === null", "Esc closes it and its entry");

        // Open on another tab: a tap on the ⏳ lines switches to Queue, the entry kept.
        await OpenSheetAsync(page);
        await page.EvalAsync("document.getElementById('queued').click()");
        await page.WaitAsync(SheetShown + " && " + OnQueue + " && " + QueueRows + ".length > 0 && history.state.prompts === true", "switched to Queue");
    }

    [WindowsFact]
    public async Task BareQueue_OpensTheQueueTab()
    {
        _backend.Reply = new WebReply(WebOutcome.Ok, Open: WebUiText.OpenQueue);
        if (await QueuePageAsync(QueueView()) is not { } page)
        {
            return;
        }

        await page.EvalAsync("setSheetTab('memory')");   // whatever tab was last
        await page.EvalAsync("document.getElementById('draft').value = '/queue'");
        await page.EvalAsync("document.getElementById('send').click()");
        await page.WaitAsync(SheetShown + " && " + OnQueue + " && " + QueueRows + ".length > 0", "bare /queue on the Queue tab");
        Assert.True((await page.EvalAsync(ManageHidden)).GetBoolean());
    }

    /// <summary>
    /// An open still reading counts as open (the 🗂️ sheet's #132, the 📣 sheet's since 2026-11-21): sent away by Esc, a question or a tap on
    /// the transcript before its read answers, it never shows and leaves no history entry.
    /// </summary>
    [WindowsTheory]
    [InlineData("esc")]
    [InlineData("ask")]
    [InlineData("tap")]
    public async Task TheSheetSentAwayWhileItReads_NeverShows(string how)
    {
        if (await QueuePageAsync(QueueView()) is not { } page)
        {
            return;
        }

        await page.WaitAsync("typeof openPrompts === 'function'", "chat.js");
        await page.EvalAsync("(() => { const real = Neon.get; window.__release = null; Neon.get = (p, e) => new Promise(r => { window.__release = () => real(p, e).then(r); }); })()");
        await page.EvalAsync("setTimeout(() => document.getElementById('prompts-button').click(), 0)");   // its promise waits on the held read
        await page.WaitAsync("window.__release !== null && promptsOpenOrOpening() && " + SheetHidden, "open while it reads, not shown");
        switch (how)
        {
            case "esc":
                await KeyAsync(page, "Escape", "document.body");
                break;
            case "ask":
                PublishAsks(new WebAskView("a2", "yesno", "Allow?", null, null, null, [new WebChoice("y", "Yes"), new WebChoice("n", "No")], null, null, null));
                await page.WaitAsync("!promptsOpenOrOpening()", "the question called it off");
                break;
            default:
                await page.EvalAsync("document.querySelector('main').click()");
                break;
        }

        await page.EvalAsync("window.__release(); window.__released = true");
        await page.WaitAsync("window.__released === true && !promptsOpenOrOpening()", "called off");
        await Task.Delay(300);
        Assert.True((await page.EvalAsync(SheetHidden + " && (history.state === null || !history.state.prompts)")).GetBoolean(), "it never showed");
    }

    /// <summary>A re-read the PC answers with a 304 draws nothing (the review's #91): an armed ✕ stays armed through the feed's reset.</summary>
    [WindowsFact]
    public async Task AReReadAnsweredWith304_KeepsAnArmedX()
    {
        if (await QueuePageAsync(QueueView()) is not { } page)
        {
            return;
        }

        await OpenQueueAsync(page);
        await page.EvalAsync(QueueRemoves + "[0].click()");
        await page.WaitAsync(QueueRemoves + "[0].classList.contains('armed')", "armed");
        await page.EvalAsync("(() => { const real = Neon.get; window.__status = 0; Neon.get = (p, e) => real(p, e).then(r => { window.__status = r.status; return r; }); })()");
        await page.EvalAsync("reset()");
        await page.WaitAsync("window.__status === 304", "the 304");
        Assert.True((await page.EvalAsync(QueueRemoves + "[0].classList.contains('armed')")).GetBoolean(), "still armed");
    }

    private const string ModeGroup = "document.querySelector('#prompts-list .manage-choices[data-field=\"mode\"]')";

    private const string CancelGroup = "document.querySelector('#prompts-list .manage-choices[data-field=\"cancel\"]')";

    /// <summary>
    /// The two modes as radio groups (2026-11-21, the user's ask): named by their labels, each choice a radio with the checked one alone in the
    /// Tab order and one ◉ a group (hidden from a screen reader); a tap on the checked one posts nothing; → picks and posts the next, a second
    /// → while that post is out posts nothing, and after the redraw focus is on the new checked radio.
    /// </summary>
    [WindowsFact]
    public async Task TheModes_AreRadioGroups_AnArrowPicks_OnePostAtATime_FocusOnTheNewOne()
    {
        if (await QueuePageAsync(QueueView()) is not { } page)
        {
            return;
        }

        await OpenQueueAsync(page);
        foreach (var (group, label, names, checkedName) in new[] { (ModeGroup, WebUiText.QueueModeLabel, QueueMode.Names, "automatic"), (CancelGroup, WebUiText.QueueCancelLabel, QueueCancelMode.Names, "empty") })
        {
            Assert.Equal("radiogroup", await page.TextAsync(group + ".getAttribute('role')"));
            Assert.Equal(label, await page.TextAsync(group + ".getAttribute('aria-label')"));
            Assert.Equal(string.Join(',', names), await page.TextAsync("[..." + group + ".querySelectorAll('.manage-choice')].map(b => b.dataset.name).join()"));
            Assert.Equal(string.Join(',', names.Select(n => n == checkedName ? "radio true 0" : "radio false -1")),
                await page.TextAsync("[..." + group + ".querySelectorAll('.manage-choice')].map(b => b.getAttribute('role') + ' ' + b.getAttribute('aria-checked') + ' ' + b.tabIndex).join()"));
            Assert.Equal(1, (await page.EvalAsync("[..." + group + ".querySelectorAll('.manage-mark')].filter(m => m.textContent === '◉').length")).GetInt32());
            Assert.True((await page.EvalAsync("[..." + group + ".querySelectorAll('.manage-mark')].every(m => m.getAttribute('aria-hidden') === 'true')")).GetBoolean());
        }

        await page.EvalAsync(ModeGroup + ".querySelector('[data-name=\"automatic\"]').click()");
        await Task.Delay(300);
        Assert.DoesNotContain(_backend.QueueActions, a => a.StartsWith("mode", StringComparison.Ordinal));   // the checked one: nothing posted

        // The mode's post held until released.
        await page.EvalAsync("(() => { const real = window.fetch; window.__held = []; window.fetch = (u, o) => String(u).includes('/api/queue/mode') ? new Promise(r => window.__held.push(() => real(u, o).then(r))) : real(u, o); })()");
        await page.EvalAsync(ModeGroup + ".querySelector('[data-name=\"automatic\"]').focus()");
        await KeyAsync(page, "ArrowRight", "document.activeElement");
        await page.WaitAsync("window.__held.length === 1 && document.activeElement.dataset.name === 'manual'", "→ picks manual, its post out");
        await KeyAsync(page, "ArrowRight", "document.activeElement");
        await Task.Delay(300);
        Assert.Equal(1, (await page.EvalAsync("window.__held.length")).GetInt32());   // a second → posts nothing meanwhile
        _backend.QueueView = QueueView(mode: "manual");
        await page.EvalAsync("window.__held.forEach(f => f())");
        await UntilAsync(() => _backend.QueueActions.Contains("mode Manual"), "the mode's post");
        await page.WaitAsync("document.activeElement.dataset.name === 'manual' && document.activeElement.getAttribute('aria-checked') === 'true' && " + ModeGroup + ".contains(document.activeElement)",
            "focus on the new checked radio after the redraw");
        Assert.Single(_backend.QueueActions, a => a.StartsWith("mode", StringComparison.Ordinal));
    }

    /// <summary>
    /// The checked choice reads at a glance (2026-11-21): the accent ring is on it alone; and the two groups' buttons share columns, each 44 px
    /// tall, on a phone (the labels over their rows) and on a desktop, no row wider than the list.
    /// </summary>
    [WindowsTheory]
    [InlineData(390)]
    [InlineData(1024)]
    public async Task TheModes_MarkTheCheckedOne_AndLineUpInColumns(int width)
    {
        if (await QueuePageAsync(QueueView(), width: width) is not { } page)
        {
            return;
        }

        await OpenQueueAsync(page);
        const string Shadows = "[...document.querySelectorAll('#prompts-list .manage-choice')].map(b => getComputedStyle(b).boxShadow)";
        Assert.Equal(2, (await page.EvalAsync(Shadows + ".filter(s => s && s !== 'none').length")).GetInt32());   // one ring a group
        Assert.True((await page.EvalAsync("[...document.querySelectorAll('#prompts-list .manage-choice.on')].every(b => getComputedStyle(b).boxShadow !== 'none' && getComputedStyle(b).borderColor !== getComputedStyle(document.querySelector('#prompts-list .manage-choice:not(.on)')).borderColor)")).GetBoolean());

        string lines = await page.TextAsync("(() => { const a = [..." + ModeGroup + ".querySelectorAll('.manage-choice')].map(b => b.getBoundingClientRect()); const c = [..." + CancelGroup + ".querySelectorAll('.manage-choice')].map(b => b.getBoundingClientRect());"
            + " return a.map((r, i) => Math.abs(r.left - c[i].left) < 1 && Math.abs(r.width - c[i].width) < 1).join(); })()");
        Assert.Equal("true,true,true", lines);
        Assert.True((await page.EvalAsync("Math.min(...[...document.querySelectorAll('#prompts-list .manage-choice')].map(b => b.getBoundingClientRect().height))")).GetDouble() >= 44);
        Assert.True((await page.EvalAsync("(() => { const list = document.getElementById('prompts-list'); return list.scrollWidth <= list.clientWidth && document.documentElement.scrollWidth <= innerWidth; })()")).GetBoolean(), "no row wider than the list");
        if (width <= 420)
        {
            // The label on a row of its own, over its buttons.
            Assert.True((await page.EvalAsync("(() => { const l = " + ModeGroup + ".querySelector('.manage-choice-label').getBoundingClientRect(); const b = " + ModeGroup + ".querySelector('.manage-choice').getBoundingClientRect(); return l.bottom <= b.top + 1; })()")).GetBoolean());
        }
    }

    /// <summary>A failed read opens the tab all the same, saying why (the review's #111); the other tabs still read.</summary>
    [WindowsFact]
    public async Task AFailedRead_OpensTheTab_SayingWhy()
    {
        _backend.PromptList.Add("one");
        if (await QueuePageAsync(null!) is not { } page)
        {
            return;
        }

        await page.EvalAsync("document.getElementById('prompts-button').click()");
        await page.WaitAsync(SheetShown + " && " + OnQueue, "the sheet on Queue");
        Assert.Equal(WebUiText.ManageReadFailed, await page.TextAsync("document.getElementById('prompts-note').textContent"));
        await ClickTabAsync(page, "prompts");
        await page.WaitAsync(Rows + ".length === 1 && document.getElementById('prompts-note').textContent === ''", "Prompts read, the note cleared");
    }
}
