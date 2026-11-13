using System.Text.Json;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// A botchat on the page in the browser (2026-11-12, plan what-would-it-take-tingly-ullman): a speaker line wears its bot's color class, and
/// any other value none; the status row's ⏭️ shows only while a botchat keeps the PC busy, posts the skip, says a refusal, fits a phone's
/// row, and a tap on it never arms the 🧠 shown in the same row.
/// </summary>
public sealed partial class WebUiPageTests
{
    private const string Skip = "document.getElementById('skip')";

    [WindowsFact]
    public async Task Chat_ASpeakerLine_WearsItsSlotsClass_AndABadSlotNone()
    {
        _backend.Snap = new WebSnapshot(
            [new WebEntry(1, "speaker", "🤖 neon", null, null, false) { Speaker = 0 }, new WebEntry(2, "speaker", "ada", null, null, false) { Speaker = 4 },
                new WebEntry(3, "speaker", "max", null, null, false)],
            [], State(), WebUiText.Words);
        if (await SignedInAsync("/") is not { } page)
        {
            return;
        }

        await page.WaitAsync("document.querySelectorAll('.entry.speaker').length === 3", "the three speaker lines");
        Assert.Equal("entry speaker c0|entry speaker c4|entry speaker",
            await page.TextAsync("[...document.querySelectorAll('.entry.speaker')].map(e => e.className).join('|')"));
        Assert.NotEqual(
            await page.TextAsync("getComputedStyle(document.querySelectorAll('.entry.speaker')[0]).color"),
            await page.TextAsync("getComputedStyle(document.querySelectorAll('.entry.speaker')[1]).color"));
        Assert.Equal("|||| c5|",
            await page.TextAsync("[7, -1, 'x', 2.5, 5, null].map(s => speakerClass({ kind: 'speaker', speaker: s })).join('|')"));
        Assert.Equal("", await page.TextAsync("speakerClass({ kind: 'notice', speaker: 1 })"));
    }

    [WindowsFact]
    public async Task Chat_TheSkip_ShowsOnlyInABusyBotChat_PostsTheSkip_AndSaysARefusal()
    {
        _backend.Snap = new WebSnapshot([], [], State(busy: true), WebUiText.Words);
        if (await SignedInAsync("/", width: 360) is not { } page)
        {
            return;
        }

        await page.WaitAsync("!document.getElementById('status').classList.contains('hidden')", "the busy row");
        Assert.True((await page.EvalAsync(Skip + ".classList.contains('hidden')")).GetBoolean());   // busy, but no botchat

        Publish("state", Json(State(busy: true) with { BotChat = true }));
        await page.WaitAsync("!" + Skip + ".classList.contains('hidden')", "the ⏭️ in a botchat");
        Assert.Equal(WebUiText.Skip, await page.TextAsync(Skip + ".getAttribute('aria-label')"));
        Assert.Equal("⏭️", await page.TextAsync(Skip + ".textContent"));
        await page.WaitAsync("document.documentElement.scrollWidth <= window.innerWidth", "no sideways scroll at 360 px");

        await page.EvalAsync(Skip + ".click()");
        await UntilAsync(() => _backend.Skips == 1, "the skip's post");

        _backend.SkipReply = new WebReply(WebOutcome.Conflict, WebUiText.NothingToSkip);
        await page.EvalAsync(Skip + ".click()");
        await page.WaitAsync("document.getElementById('message').textContent === " + JsonSerializer.Serialize(WebUiText.NothingToSkip), "the refusal said");

        Publish("state", Json(State(busy: false) with { BotChat = true }));
        await page.WaitAsync(Skip + ".classList.contains('hidden')", "hidden once the PC is idle");
    }

    [WindowsFact]
    public async Task Chat_ATapOnTheSkip_NeverArmsTheBrainBesideIt()
    {
        _backend.Snap = new WebSnapshot([], [], State(busy: true) with { BotChat = true }, WebUiText.Words, null,
            new WebStatus("🤖 neon is thinking", 1000, null, null, Learning: true));
        if (await SignedInAsync("/", width: 360) is not { } page)
        {
            return;
        }

        await page.WaitAsync("!" + Skip + ".classList.contains('hidden') && !document.getElementById('learn').classList.contains('hidden')", "both buttons");
        // A tap at the ⏭️'s middle lands on it, not on the 🧠's widened tap area (both reach 6 px past their sides).
        Assert.Equal("skip", await page.TextAsync(
            "(() => { const r = " + Skip + ".getBoundingClientRect(); const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2); hit.click(); return hit.id; })()"));
        await UntilAsync(() => _backend.Skips == 1, "the skip's post");
        Assert.False((await page.EvalAsync("document.getElementById('learn').classList.contains('armed')")).GetBoolean());
        Assert.Equal(0, _backend.LearnCancels);
    }
}
