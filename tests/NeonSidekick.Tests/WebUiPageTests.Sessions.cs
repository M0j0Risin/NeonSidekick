using System.Text.Json;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The 🗂️ sheet's Sessions tab in the browser (2026-11-16, plan lucid-roaming-lantern #40): the current row disabled; two taps resume another
/// (the first says so on the row's second line) and close the sheet; a refusal's reason in the note, the sheet open; with commands off no
/// row arms and the note says why; the empty and no-match words; the filter's ask sent once typing pauses, its hits drawn with their
/// matches marked; the rows dimmed while the PC is busy.
/// </summary>
public sealed partial class WebUiPageTests
{
    private const string SessionRows = "document.querySelectorAll('#manage-list .session-row')";

    private static WebSessionsView SessionsView(bool offered = true, string? note = null, params WebSessionRow[] rows) =>
        new("p", rows, [], offered, note);

    private static readonly WebSessionRow CurrentRow = new(1, "On screen now", "today 10:00", "2 turns", "gemma", true);

    private static readonly WebSessionRow OlderRow = new(2, "The trip plan", "Mon 09:00", "5 turns", "qwen", false);

    private async Task<HeadlessPage?> SessionsPageAsync(Func<string?, WebSessionsView?> sessions, WebState? state = null)
    {
        _backend.SessionsFor = sessions;
        _backend.Snap = new WebSnapshot([], [], state ?? State(), WebUiText.Words, null, null);
        if (await SignedInAsync("/") is not { } page)
        {
            return null;
        }

        await page.WaitAsync("window.NeonManage !== undefined", "manage.js");
        await page.EvalAsync("window.NeonManage.open('sessions')");
        await page.WaitAsync(ManageShown + " && document.querySelector('#manage-list').children.length > 0", "the sheet on its Sessions tab");
        return page;
    }

    [WindowsFact]
    public async Task TheCurrentRow_IsDisabled_AndTwoTapsOnAnother_ResumeIt_AndCloseTheSheet()
    {
        if (await SessionsPageAsync(_ => SessionsView(rows: [CurrentRow, OlderRow])) is not { } page)
        {
            return;
        }

        Assert.Equal("sessions", await page.TextAsync("document.activeElement.dataset.tab"));
        Assert.True((await page.EvalAsync(SessionRows + "[0].disabled")).GetBoolean());
        Assert.Contains(WebUiText.SessionsCurrent, await page.TextAsync(SessionRows + "[0].querySelector('.manage-meta').textContent"), StringComparison.Ordinal);

        await page.EvalAsync(SessionRows + "[1].click()");
        await page.WaitAsync(SessionRows + "[1].classList.contains('armed')", "armed");
        Assert.Equal(WebUiText.SessionsArmed, await page.TextAsync(SessionRows + "[1].querySelector('.manage-meta').textContent"));
        Assert.Equal("The trip plan", await page.TextAsync(SessionRows + "[1].querySelector('.manage-text').textContent"));   // its name kept
        Assert.Empty(_backend.SessionResumes);

        await page.EvalAsync(SessionRows + "[1].click()");
        await UntilAsync(() => _backend.SessionResumes.Contains(new WebSessionResume(2, "p")), "the resume");
        await page.WaitAsync(ManageHidden + " && history.state === null", "the sheet closed, its entry gone");
    }

    [WindowsFact]
    public async Task ARefusedResume_ShowsItsReason_AndTheSheetStays()
    {
        _backend.SessionReply = new WebReply(WebOutcome.Conflict, WebUiText.ProfileSwitched);
        if (await SessionsPageAsync(_ => SessionsView(rows: [OlderRow])) is not { } page)
        {
            return;
        }

        await page.EvalAsync(SessionRows + "[0].click(); " + SessionRows + "[0].click()");
        await page.WaitAsync("document.getElementById('manage-note').textContent === " + JsonSerializer.Serialize(WebUiText.ProfileSwitched), "the reason");
        Assert.True((await page.EvalAsync(ManageShown)).GetBoolean());
    }

    [WindowsFact]
    public async Task WithCommandsOff_NoRowArms_AndTheNoteSaysWhy()
    {
        if (await SessionsPageAsync(_ => SessionsView(false, WebUiText.CommandsOff, OlderRow)) is not { } page)
        {
            return;
        }

        await page.WaitAsync("document.getElementById('manage-note').textContent === " + JsonSerializer.Serialize(WebUiText.CommandsOff), "the note");
        Assert.Equal("true", await page.TextAsync(SessionRows + "[0].getAttribute('aria-disabled')"));
        await page.EvalAsync(SessionRows + "[0].click(); " + SessionRows + "[0].click()");
        Assert.False((await page.EvalAsync(SessionRows + "[0].classList.contains('armed')")).GetBoolean());
        Assert.Empty(_backend.SessionResumes);
    }

    [WindowsFact]
    public async Task NoSessions_SaysSo_AndAFilterThatFindsNothing_SaysThat()
    {
        if (await SessionsPageAsync(_ => SessionsView()) is not { } page)
        {
            return;
        }

        Assert.Equal(WebUiText.SessionsNone, await page.TextAsync("document.querySelector('#manage-list .manage-none').textContent"));
        await page.EvalAsync("(() => { const f = document.getElementById('manage-filter'); f.value = 'zebra'; f.dispatchEvent(new Event('input')); })()");
        await page.WaitAsync("document.querySelector('#manage-list .manage-none').textContent === " + JsonSerializer.Serialize(WebUiText.SessionsNoMatch), "no match");
    }

    [WindowsFact]
    public async Task TheFiltersAsk_GoesOnceTypingPauses_AndItsHitsAreDrawnMarked()
    {
        var hit = new WebSessionRow(3, "Something else", "Sun", "1 turn", "qwen", false, [new WebSnippetPart("File the "), new WebSnippetPart("tax", true), new WebSnippetPart(" forms")]);
        if (await SessionsPageAsync(q => q == "tax" ? new WebSessionsView("p", [], [hit], true) : SessionsView(rows: [OlderRow])) is not { } page)
        {
            return;
        }

        await page.EvalAsync("(() => { const f = document.getElementById('manage-filter'); for (const v of ['t', 'ta', 'tax']) { f.value = v; f.dispatchEvent(new Event('input')); } })()");
        Assert.True((await page.EvalAsync(SessionRows + "[0].classList.contains('hidden')")).GetBoolean());   // the instant filter: no title says it
        await page.WaitAsync("document.querySelector('#manage-list mark') !== null", "the hit");
        Assert.Equal("tax", await page.TextAsync("document.querySelector('#manage-list mark').textContent"));
        Assert.Equal("File the tax forms", await page.TextAsync("document.querySelector('#manage-list .manage-snippet').textContent"));
        Assert.Equal(WebUiText.SessionsFound, await page.TextAsync("document.querySelector('#manage-list .manage-head').textContent"));
        Assert.DoesNotContain("t", _backend.SessionQueries);
        Assert.DoesNotContain("ta", _backend.SessionQueries);
        Assert.Contains("tax", _backend.SessionQueries);
    }

    /// <summary>
    /// A long list's rows keep their height and the list scrolls (2026-11-20, the user's screenshot: with dozens of sessions each title
    /// showed a sliver of its glyphs, the flex column shrinking every row to its 44 px floor). Thirty rows on the phone's 800 px high page
    /// (40vh: 320 px), then thirty hits with their snippets: no title or snippet is cut top or bottom, and the list overflows instead.
    /// </summary>
    [WindowsFact]
    public async Task ALongList_KeepsEveryTitleWhole_AndScrolls()
    {
        WebSessionRow Row(int i, IReadOnlyList<WebSnippetPart>? snippet = null) =>
            new(100 + i, "Session number " + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + " about the trip", "Mon 09:00", "5 turns", "qwen", false, snippet);
        var rows = Enumerable.Range(0, 30).Select(i => Row(i)).ToArray();
        var hits = Enumerable.Range(0, 30).Select(i => Row(i, [new WebSnippetPart("File the "), new WebSnippetPart("tax", Match: true), new WebSnippetPart(" forms")])).ToArray();
        if (await SessionsPageAsync(q => q == "tax" ? new WebSessionsView("p", [], hits, true) : SessionsView(rows: rows)) is not { } page)
        {
            return;
        }

        const string Clipped = "[...document.querySelectorAll('#manage-list .session-row .manage-text, #manage-list .session-row .manage-snippet')].filter(t => t.scrollHeight > t.clientHeight + 1).length";
        const string Scrolls = "(() => { const l = document.getElementById('manage-list'); return l.scrollHeight > l.clientHeight + 1; })()";
        Assert.Equal(30, (await page.EvalAsync(SessionRows + ".length")).GetInt32());
        Assert.Equal(0, (await page.EvalAsync(Clipped)).GetInt32());
        Assert.True((await page.EvalAsync(Scrolls)).GetBoolean());

        await page.EvalAsync("(() => { const f = document.getElementById('manage-filter'); f.value = 'tax'; f.dispatchEvent(new Event('input')); })()");
        await page.WaitAsync("document.querySelectorAll('#manage-list .manage-snippet').length === 30", "the thirty hits");
        Assert.Equal(0, (await page.EvalAsync(Clipped)).GetInt32());
        Assert.True((await page.EvalAsync(Scrolls)).GetBoolean());
    }

    [WindowsFact]
    public async Task TheRows_AreDimmed_WhileThePcIsBusy()
    {
        if (await SessionsPageAsync(_ => SessionsView(rows: [OlderRow]), State(busy: true)) is not { } page)
        {
            return;
        }

        await page.WaitAsync("document.getElementById('manage-list').classList.contains('dim')", "dimmed");
        Assert.Equal(WebUiText.SessionsBusy, await page.TextAsync("document.getElementById('manage-note').textContent"));
        Publish("state", Json(State()));
        await page.WaitAsync("!document.getElementById('manage-list').classList.contains('dim')", "undimmed once idle");
    }
}
