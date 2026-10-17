using System.Text.Json;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The sheet's tabs, ↑/↓ and the draft box in headless Edge (2026-10-16, the user's asks): ⇪ saves the draft and keeps it; a row's 🗑️ asks
/// twice, disarms after 3 s and on Esc; the 💾 tab marks a turned-off memory and says when Memory mode is off; the sheet reopens on its last tab
/// and bare <c>/prompts</c> on Prompts; the 🕘 tab lists newest first and clears on two taps; ↑/↓ walk the history (a wrapped draft's caret
/// first, the dropdown's keys its own, a sent line at once, the ✕'s text back); a one-line draft shows no scrollbar.
/// </summary>
public sealed partial class WebUiPageTests
{
    private const string Draft = "document.getElementById('draft')";

    private const string Rows = "[...document.querySelectorAll('#prompts-list .prompt-row')]";

    private static Task ClickTabAsync(HeadlessPage page, string tab) =>
        page.EvalAsync("document.querySelector('.sheet-tab[data-tab=\"" + tab + "\"]').click()");

    private static Task KeyAsync(HeadlessPage page, string key, string target = Draft) =>
        page.EvalAsync(target + ".dispatchEvent(new KeyboardEvent('keydown', { key: '" + key + "', bubbles: true, cancelable: true }))");

    private static Task SetDraftAsync(HeadlessPage page, string text, int? caret = null) =>
        page.EvalAsync("(() => { const d = " + Draft + "; d.focus(); d.value = " + JsonSerializer.Serialize(text) + "; d.dispatchEvent(new Event('input')); const c = "
            + (caret?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "d.value.length") + "; d.setSelectionRange(c, c); })()");

    // ── The tabs, ⇪ and 🗑️ ──

    [WindowsFact]
    public async Task SaveDraft_SavesThePrompt_AndKeepsTheDraft_AnEmptyDraftPostsNothing()
    {
        if (await PromptsPageAsync(prompts: ["one"]) is not { } page)
        {
            return;
        }

        await page.EvalAsync(Draft + ".value = 'two'");
        await OpenSheetAsync(page);
        await page.EvalAsync("document.getElementById('prompts-action').click()");
        await page.WaitAsync(Rows + ".length === 2", "the saved prompt listed");
        Assert.Equal("two", Assert.Single(_backend.PromptsSaved));
        Assert.Equal("two", await page.TextAsync(Draft + ".value"));   // kept

        await page.EvalAsync(Draft + ".value = '   '");
        await page.EvalAsync("document.getElementById('prompts-action').click()");
        await page.WaitAsync("document.getElementById('message').textContent === " + JsonSerializer.Serialize(WebUiText.DraftEmpty), "the empty draft said");
        Assert.Single(_backend.PromptsSaved);
    }

    [WindowsFact]
    public async Task ARowsTrash_AsksTwice_ThenDeletes_AndTheListIsReadAgain()
    {
        if (await PromptsPageAsync(prompts: ["one", "two"]) is not { } page)
        {
            return;
        }

        await OpenSheetAsync(page);
        await page.EvalAsync("document.querySelector('.row-delete').click()");
        Assert.True((await page.EvalAsync("document.querySelector('.row-delete').classList.contains('armed')")).GetBoolean());
        Assert.Empty(_backend.PromptsDeleted);

        await page.EvalAsync("document.querySelector('.row-delete').click()");
        await page.WaitAsync(Rows + ".length === 1 && " + Rows + "[0].textContent.includes('two')", "the first prompt gone");
        Assert.Equal(FakeWebUiBackend.PromptId(1), Assert.Single(_backend.PromptsDeleted));
    }

    [WindowsFact]
    public async Task AnArmedTrash_DisarmsAfterThreeSeconds_AndEscDisarmsBeforeItCloses()
    {
        if (await PromptsPageAsync(prompts: ["one"]) is not { } page)
        {
            return;
        }

        await OpenSheetAsync(page);
        const string Armed = "document.querySelector('.row-delete').classList.contains('armed')";
        await page.EvalAsync("document.querySelector('.row-delete').click()");
        await page.WaitAsync("!" + Armed, "disarmed after 3 s", seconds: 6);

        await page.EvalAsync("document.querySelector('.row-delete').click()");
        await KeyAsync(page, "Escape", "document");
        await page.WaitAsync("!" + Armed, "Esc disarms first");
        Assert.True((await page.EvalAsync(SheetShown)).GetBoolean());
        await KeyAsync(page, "Escape", "document");
        await page.WaitAsync(SheetHidden, "a second Esc closes the sheet");
        Assert.Empty(_backend.PromptsDeleted);
    }

    [WindowsFact]
    public async Task TheMemoryTab_MarksATurnedOffOne_SaysWhenTheModeIsOff_AndTheSheetReopensOnIt()
    {
        _backend.MemoryList.AddRange([new WebMemory("Likes tea.", false), new WebMemory("Uses Obsidian.", true)]);
        _backend.MemoryModeOff = true;
        _backend.Reply = new WebReply(WebOutcome.Ok, Open: WebUiText.OpenPrompts);
        if (await PromptsPageAsync(prompts: ["one"]) is not { } page)
        {
            return;
        }

        await OpenSheetAsync(page);
        await ClickTabAsync(page, "memory");
        await page.WaitAsync(Rows + ".length === 2 && document.querySelector('.sheet-tab[data-tab=\"memory\"]').getAttribute('aria-selected') === 'true'", "the memories");
        Assert.Contains("· " + WebUiText.MemoryOff, await page.TextAsync(Rows + "[1].textContent"), StringComparison.Ordinal);
        Assert.Equal(WebUiText.MemoryModeOff, await page.TextAsync("document.getElementById('prompts-note').textContent"));
        await page.EvalAsync("document.querySelectorAll('.row-delete')[1].click()");
        await page.EvalAsync("document.querySelectorAll('.row-delete')[1].click()");
        await page.WaitAsync(Rows + ".length === 1", "the turned-off memory deleted");
        Assert.Equal("Uses Obsidian.", Assert.Single(_backend.MemoriesDeleted));

        await page.EvalAsync("document.getElementById('prompts-close').click()");
        await page.WaitAsync(SheetHidden, "closed");
        await OpenSheetAsync(page);
        await page.WaitAsync("document.querySelector('.sheet-tab.on').dataset.tab === 'memory'", "reopened on the last tab");

        // Bare /prompts opens on Prompts, whatever tab was last.
        await page.EvalAsync("document.getElementById('prompts-close').click()");
        await page.WaitAsync(SheetHidden, "closed");
        await page.EvalAsync(Draft + ".value = '/prompts'");
        await page.EvalAsync("document.getElementById('send').click()");
        await page.WaitAsync(SheetShown + " && document.querySelector('.sheet-tab.on').dataset.tab === 'prompts'", "bare /prompts on its tab");
    }

    /// <summary>
    /// 📣 shows the glyph of the tab the sheet opens on, through a tap, ←/→, a close, a reload and bare <c>/prompts</c>; the tabs read Prompts,
    /// History, Memory, and → focuses the tab it selects (index.html's order and the script's agree) (2026-10-17, the user's asks).
    /// </summary>
    [WindowsFact]
    public async Task TheButton_ShowsTheCurrentTab_AndTheTabsReadPromptsHistoryMemory()
    {
        _backend.Reply = new WebReply(WebOutcome.Ok, Open: WebUiText.OpenPrompts);
        if (await PromptsPageAsync(prompts: ["one"]) is not { } page)
        {
            return;
        }

        static string Glyph(string label) => JsonSerializer.Serialize(label.Split(' ')[0]);
        const string Button = "document.getElementById('prompts-button').textContent";

        Assert.Equal("prompts,history,memory", await page.TextAsync("[...document.querySelectorAll('.sheet-tab')].map(t => t.dataset.tab).join()"));
        await page.WaitAsync(Button + " === " + Glyph(WebUiText.PromptsTab), "📣 at first");
        await OpenSheetAsync(page);

        await ClickTabAsync(page, "history");
        await page.WaitAsync(Button + " === " + Glyph(WebUiText.HistoryTab), "🕘 on the history tab");
        await KeyAsync(page, "ArrowRight", "document.querySelector('.sheet-tab[data-tab=\"history\"]')");
        await page.WaitAsync(Button + " === " + Glyph(WebUiText.MemoryTab) + " && document.activeElement.dataset.tab === 'memory'", "→ on to memory, focused");

        await page.EvalAsync("document.getElementById('prompts-close').click()");
        await page.WaitAsync(SheetHidden + " && " + Button + " === " + Glyph(WebUiText.MemoryTab), "💾 kept after a close");

        await page.EvalAsync("location.reload()");
        await page.WaitAsync("document.getElementById('prompts-button') !== null && history.state === null", "a reload's own entry");
        await page.WaitAsync(Button + " === " + Glyph(WebUiText.MemoryTab), "💾 kept after a reload");

        await page.EvalAsync(Draft + ".value = '/prompts'");
        await page.EvalAsync("document.getElementById('send').click()");
        await page.WaitAsync(SheetShown + " && document.querySelector('.sheet-tab.on').dataset.tab === 'prompts' && " + Button + " === " + Glyph(WebUiText.PromptsTab),
            "bare /prompts back on 📣");
    }

    [WindowsFact]
    public async Task TheHistoryTab_ListsNewestFirst_ARowFillsTheDraft_AndClearAllTakesTwoTaps()
    {
        _backend.HistoryLines.AddRange(["first", "second"]);
        if (await PromptsPageAsync() is not { } page)
        {
            return;
        }

        await OpenSheetAsync(page);
        await ClickTabAsync(page, "history");
        await page.WaitAsync(Rows + ".length === 2", "the history");
        Assert.Equal("second", await page.TextAsync(Rows + "[0].querySelector('.prompt-text').textContent"));

        await page.EvalAsync("document.getElementById('prompts-action').click()");
        Assert.True((await page.EvalAsync("document.getElementById('prompts-action').classList.contains('armed')")).GetBoolean());
        Assert.Equal(0, _backend.HistoryClears);
        await page.EvalAsync("document.getElementById('prompts-action').click()");
        await page.WaitAsync("document.querySelector('.prompts-none') && document.querySelector('.prompts-none').textContent === " + JsonSerializer.Serialize(WebUiText.HistoryNone), "the history cleared");
        Assert.Equal(1, _backend.HistoryClears);
    }

    [WindowsFact]
    public async Task AtThreeHundredTwentyPixels_TheSheetsBarFits()
    {
        if (await PromptsPageAsync(width: 320, prompts: ["one"]) is not { } page)
        {
            return;
        }

        await OpenSheetAsync(page);
        Assert.True((await page.EvalAsync("document.documentElement.scrollWidth <= innerWidth")).GetBoolean());
        Assert.True((await page.EvalAsync("document.getElementById('prompts-close').getBoundingClientRect().right <= innerWidth")).GetBoolean());
    }

    // ── ↑/↓ ──

    [WindowsFact]
    public async Task UpAndDown_WalkTheHistory_AndPastTheNewestBringTheDraftBack()
    {
        _backend.HistoryLines.AddRange(["first", "second"]);
        if (await PromptsPageAsync() is not { } page)
        {
            return;
        }

        await SetDraftAsync(page, "mine");
        await KeyAsync(page, "ArrowUp");
        await page.WaitAsync(Draft + ".value === 'second'", "the newest");
        await KeyAsync(page, "ArrowUp");
        await page.WaitAsync(Draft + ".value === 'first'", "the older");
        await KeyAsync(page, "ArrowUp");   // the oldest: stays
        await KeyAsync(page, "ArrowDown");
        await page.WaitAsync(Draft + ".value === 'second'", "forward again");
        await KeyAsync(page, "ArrowDown");
        await page.WaitAsync(Draft + ".value === 'mine'", "the draft in progress back");
        Assert.True((await page.EvalAsync(Draft + ".selectionStart === " + Draft + ".value.length")).GetBoolean());
    }

    [WindowsFact]
    public async Task OnAWrappedDraft_UpMovesTheCaretFirst_AndRecallsFromTheStart()
    {
        _backend.HistoryLines.Add("older");
        if (await PromptsPageAsync() is not { } page)
        {
            return;
        }

        await SetDraftAsync(page, "line one\nline two");   // the caret at the end, on the second row
        await KeyAsync(page, "ArrowUp");
        await Task.Delay(300);
        Assert.Equal("line one\nline two", await page.TextAsync(Draft + ".value"));

        await page.EvalAsync(Draft + ".setSelectionRange(0, 0)");
        await KeyAsync(page, "ArrowUp");
        await page.WaitAsync(Draft + ".value === 'older'", "recalled from the start");
    }

    [WindowsFact]
    public async Task WithTheDropdownOpen_UpIsTheLists()
    {
        _backend.HistoryLines.Add("older");
        _backend.Completion = new WebCompletion(0, 0, 3, [new WebCompletionItem("/tree", "a tree", "/tree "), new WebCompletionItem("/test", "a test", "/test ")], false);
        if (await PromptsPageAsync() is not { } page)
        {
            return;
        }

        await SetDraftAsync(page, "/tr");
        await page.WaitAsync("!document.getElementById('complete').classList.contains('hidden')", "the dropdown");
        await KeyAsync(page, "ArrowUp");
        await Task.Delay(300);
        Assert.Equal("/tr", await page.TextAsync(Draft + ".value"));
    }

    [WindowsFact]
    public async Task ALineSent_IsRecalledAtOnce_AndTheClearedTextComesBack()
    {
        if (await PromptsPageAsync() is not { } page)
        {
            return;
        }

        await SetDraftAsync(page, "hello there");
        await page.EvalAsync("document.getElementById('send').click()");
        await page.WaitAsync(Draft + ".value === ''", "sent");
        await KeyAsync(page, "ArrowUp");
        await page.WaitAsync(Draft + ".value === 'hello there'", "the sent line recalled");

        await SetDraftAsync(page, "half a thought");
        await page.EvalAsync("document.getElementById('clear-draft').click()");
        await page.WaitAsync(Draft + ".value === ''", "cleared");
        await KeyAsync(page, "ArrowUp");
        await page.WaitAsync(Draft + ".value === 'half a thought'", "the cleared text back, as Esc's at the PC");
        await KeyAsync(page, "ArrowUp");
        await page.WaitAsync(Draft + ".value === 'hello there'", "then the history");
    }

    // ── The draft box (the user's screenshot: a scrollbar under the ✕) ──

    [WindowsFact]
    public async Task AOneLineDraft_ShowsNoScrollbar_AndACappedOne_KeepsTheClearButtonOffItsScrollbar()
    {
        if (await PromptsPageAsync(width: 800) is not { } page)
        {
            return;
        }

        await SetDraftAsync(page, "one line");
        Assert.True((await page.EvalAsync(Draft + ".scrollHeight <= " + Draft + ".clientHeight")).GetBoolean());
        Assert.Equal("hidden", await page.TextAsync("getComputedStyle(" + Draft + ").overflowY"));

        await SetDraftAsync(page, string.Join('\n', Enumerable.Range(1, 80).Select(i => "line " + i)));
        Assert.Equal("auto", await page.TextAsync("getComputedStyle(" + Draft + ").overflowY"));
        Assert.True((await page.EvalAsync(
            "(() => { const d = " + Draft + ".getBoundingClientRect(), x = document.getElementById('clear-draft').getBoundingClientRect(); return x.right <= d.left + " + Draft + ".clientLeft + " + Draft + ".clientWidth + 0.5; })()")).GetBoolean());
    }
}
