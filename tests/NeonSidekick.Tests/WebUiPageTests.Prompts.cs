using System.Text.Json;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The page's saved prompts (later on 2026-10-15, the user's ask) in headless Edge: 📣 opens the sheet numbered as at the PC, a tap fills the
/// draft with Undo, ➤ sends the prompt and leaves the draft, an empty list says so, the filter past eight, Esc and Back close it, bare
/// <c>/prompts</c> sent opens it, the drawer and the sheet swap without leaving a stale entry, a picture tapped under it, a reload, focus, a 320 px
/// row with ■, the floating buttons under it, and a refusal's Send as a message sending the prompt.
/// </summary>
public sealed partial class WebUiPageTests
{
    private const string SheetShown = "!document.getElementById('prompts').classList.contains('hidden')";

    private const string SheetHidden = "document.getElementById('prompts').classList.contains('hidden')";

    private async Task<HeadlessPage?> PromptsPageAsync(int width = 390, IReadOnlyList<WebEntry>? entries = null, WebState? state = null, WebStrip? strip = null, params string[] prompts)
    {
        _backend.PromptList.AddRange(prompts);
        _backend.Snap = new WebSnapshot(entries ?? [], [], state ?? State(), WebUiText.Words, null, null, strip);
        return await SignedInAsync("/", width);
    }

    private static async Task OpenSheetAsync(HeadlessPage page)
    {
        await page.EvalAsync("document.getElementById('prompts-button').click()");
        await page.WaitAsync(SheetShown, "the sheet");
    }

    [WindowsFact]
    public async Task ThePromptsButton_OpensTheSheet_NumberedAsAtThePc_ATapFillsTheDraft_WithUndo()
    {
        if (await PromptsPageAsync(prompts: ["Summarize this.", "Review the diff"]) is not { } page)
        {
            return;
        }

        await page.EvalAsync("document.getElementById('draft').value = 'mine'");
        await OpenSheetAsync(page);
        Assert.Equal("1Summarize this.", await page.TextAsync("document.querySelector('.prompt-text').textContent"));
        Assert.Equal("prompt-text", await page.TextAsync("document.activeElement.className"));   // focus on the first row

        await page.EvalAsync("document.querySelectorAll('.prompt-text')[1].click()");
        await page.WaitAsync(SheetHidden + " && document.getElementById('draft').value === 'Review the diff\\nmine'", "the prompt on the draft");
        Assert.Equal("draft", await page.TextAsync("document.activeElement.id"));
        await page.EvalAsync("[...document.querySelectorAll('#message button')].find(b => b.textContent === '" + WebUiText.Undo + "').click()");
        await page.WaitAsync("document.getElementById('draft').value === 'mine'", "undone");
        Assert.Empty(_backend.Sent);
    }

    [WindowsFact]
    public async Task ARowsSend_PostsThePrompt_AndLeavesTheDraft_AndEscClosesTheSheet_FocusBackOnTheButton()
    {
        if (await PromptsPageAsync(prompts: ["Summarize this."]) is not { } page)
        {
            return;
        }

        await page.EvalAsync("document.getElementById('draft').value = 'mine'");
        await OpenSheetAsync(page);
        await page.EvalAsync("document.querySelector('.prompt-send').click()");
        await page.WaitAsync(SheetHidden, "closed by the send");
        Assert.True(SpinWait.SpinUntil(() => !_backend.Sent.IsEmpty, TimeSpan.FromSeconds(5)));
        Assert.Equal("Summarize this.", Assert.Single(_backend.Sent).Text);
        Assert.Equal("mine", await page.TextAsync("document.getElementById('draft').value"));

        await OpenSheetAsync(page);
        await page.EvalAsync("document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))");
        await page.WaitAsync(SheetHidden, "Esc closes it");
        Assert.Equal("prompts-button", await page.TextAsync("document.activeElement.id"));
    }

    [WindowsFact]
    public async Task AnEmptyList_SaysSo_AndPastEight_TheFilterShows_AndBackCloses()
    {
        if (await PromptsPageAsync() is not { } page)
        {
            return;
        }

        await OpenSheetAsync(page);
        Assert.Equal(WebUiText.PromptsNone, await page.TextAsync("document.querySelector('.prompts-none').textContent"));
        await page.EvalAsync("history.back()");
        await page.WaitAsync(SheetHidden, "Back closes it");

        _backend.PromptList.AddRange(Enumerable.Range(1, 9).Select(i => "prompt " + i));
        await OpenSheetAsync(page);
        Assert.False(await HiddenAsync(page, "prompts-filter"));
        Assert.Equal("prompts-filter", await page.TextAsync("document.activeElement.id"));
        await page.EvalAsync("const f = document.getElementById('prompts-filter'); f.value = 'prompt 9'; f.dispatchEvent(new Event('input'))");
        Assert.Equal(1, (await page.EvalAsync("[...document.querySelectorAll('.prompt-row')].filter(r => !r.classList.contains('hidden')).length")).GetInt32());
    }

    [WindowsFact]
    public async Task BarePromptsSent_OpensTheSheet_AndARefusalsSendAsAMessage_SendsThePrompt()
    {
        if (await PromptsPageAsync(prompts: ["/nope"]) is not { } page)
        {
            return;
        }

        _backend.Reply = new WebReply(WebOutcome.Ok, Open: WebUiText.OpenPrompts);
        await page.EvalAsync("document.getElementById('draft').value = '/prompts'; document.getElementById('send').click()");
        await page.WaitAsync(SheetShown + " && document.getElementById('draft').value === ''", "the sheet, the line gone from the box");

        _backend.Reply = new WebReply(WebOutcome.Refused, "Unknown command: /nope", AsText: true);
        await page.EvalAsync("document.getElementById('draft').value = 'typed since'");
        await page.EvalAsync("document.querySelector('.prompt-send').click()");
        await page.WaitAsync("[...document.querySelectorAll('#message button')].some(b => b.textContent === '" + WebUiText.SendAsMessage + "')", "the refusal");
        _backend.Reply = WebReply.Ok;
        await page.EvalAsync("[...document.querySelectorAll('#message button')].find(b => b.textContent === '" + WebUiText.SendAsMessage + "').click()");
        Assert.True(SpinWait.SpinUntil(() => _backend.Sent.Count(s => s.AsText) == 1, TimeSpan.FromSeconds(5)));
        Assert.Equal("/nope", _backend.Sent.Single(s => s.AsText).Text);   // the prompt, not the box's text
    }

    [WindowsFact]
    public async Task TheDrawerAndTheSheet_SwapPlaces_LeavingNoStaleEntry_EitherWay()
    {
        StripIds(1);
        if (await PromptsPageAsync(strip: Strip(null, 1), prompts: ["one"]) is not { } page)
        {
            return;
        }

        await page.WaitAsync("!document.getElementById('strip-button').classList.contains('hidden')", "🎞️");
        await page.EvalAsync("document.getElementById('strip-button').click()");
        await page.WaitAsync("!document.getElementById('strip').classList.contains('hidden')", "the drawer");
        await OpenSheetAsync(page);
        Assert.True(await HiddenAsync(page, "strip"));
        await page.EvalAsync("history.back()");
        await page.WaitAsync(SheetHidden + " && history.state === null", "Back: the page's own entry");

        await OpenSheetAsync(page);
        await page.EvalAsync("document.getElementById('strip-button').click()");
        await page.WaitAsync("!document.getElementById('strip').classList.contains('hidden') && " + SheetHidden, "the drawer in its place");
        await page.EvalAsync("history.back()");
        await page.WaitAsync("document.getElementById('strip').classList.contains('hidden') && history.state === null", "Back: the page's own entry");
    }

    [WindowsFact]
    public async Task APictureUnderTheSheet_OpensOverIt_AndBackReturnsToIt_AndAReloadLeavesNoEntry()
    {
        var entry = new WebEntry(1, "picture", "p", null, null, false, [new WebPicture(5, "a.png", "/favicon.svg", "/favicon.svg", "/favicon.svg", 64, 64, false)]);
        if (await PromptsPageAsync(entries: [entry], prompts: ["one"]) is not { } page)
        {
            return;
        }

        await page.WaitAsync("document.querySelectorAll('.entry.pictures .tile').length === 1", "the picture");
        await OpenSheetAsync(page);
        await page.EvalAsync("document.querySelector('.entry.pictures .tile').click()");
        await page.WaitAsync("!document.getElementById('overlay').classList.contains('hidden')", "the overlay");
        await page.EvalAsync("history.back()");
        await page.WaitAsync("document.getElementById('overlay').classList.contains('hidden') && " + SheetShown, "back on the sheet");
        Assert.True(await HiddenAsync(page, "to-bottom"));   // the floating buttons hide under it

        await page.EvalAsync("location.reload()");
        await page.WaitAsync("document.getElementById('prompts-button') !== null && history.state === null", "a reload's own entry");
    }

    [WindowsFact]
    public async Task At320Pixels_TheRowFits_WithTheStopButton()
    {
        if (await PromptsPageAsync(width: 320, state: State(busy: true)) is not { } page)
        {
            return;
        }

        await page.WaitAsync("!document.getElementById('stop').classList.contains('hidden')", "■");
        Assert.True((await page.EvalAsync("document.documentElement.scrollWidth <= innerWidth")).GetBoolean());
        Assert.True((await page.EvalAsync("document.getElementById('draft').getBoundingClientRect().width >= 120")).GetBoolean());
        Assert.True((await page.EvalAsync("document.getElementById('stop').getBoundingClientRect().right <= innerWidth")).GetBoolean());
    }
}
