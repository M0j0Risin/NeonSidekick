using System.Text.Json;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// A reflection's ask in the browser (2026-10-17, the user's question): the summary as the card's body, Keep and Discard right under it, and
/// the whole proposal folded behind View below the buttons; an opened fold stays open when another ask redraws the cards; Keep posts keep.
/// </summary>
public sealed partial class WebUiPageTests
{
    private static WebAskView LearnAsk() => new("l1", "learn", "🧠 A reflection wants to create skill “greeting”", "Greets the user by name.", null, null,
        [new WebChoice("keep", "Keep"), new WebChoice("discard", "Discard")], null, null, null,
        Detail: "Description: Greets the user by name.\n\nInstructions:\n" + string.Join("\n", Enumerable.Range(1, 80).Select(i => "Step " + i + ".")), DetailLabel: "View");

    [WindowsFact]
    public async Task Chat_AReflectionsAsk_ShowsTheSummary_TheButtons_ThenTheProposalFolded()
    {
        _backend.Snap = new WebSnapshot([], [LearnAsk()], State(), WebUiText.Words);
        if (await SignedInAsync("/") is not { } page)
        {
            return;
        }

        // The cards under #asks only (2026-10-19, the first CI-like run): the usage card is a section.ask too, always in the page, so a bare
        // '.ask' counted one card more than there were. "Two cards" passed while the second ask was still on its way, and failed once it
        // had drawn first, which is what one core always gave.
        const string Card = "document.querySelector('#asks .ask')";
        await page.WaitAsync(Card + " !== null && " + Card + ".querySelector('details.detail') !== null", "the learn card with its fold");
        Assert.Equal("Greets the user by name.", await page.TextAsync(Card + ".querySelector('p.text').textContent"));
        Assert.Equal("View", await page.TextAsync(Card + ".querySelector('details.detail summary').textContent"));
        Assert.False((await page.EvalAsync(Card + ".querySelector('details.detail').open")).GetBoolean());
        Assert.True((await page.EvalAsync(   // the buttons before the fold
            "(() => { const c = " + Card + "; return [...c.children].indexOf(c.querySelector('.choices')) < [...c.children].indexOf(c.querySelector('details.detail')); })()")).GetBoolean());

        await page.EvalAsync(Card + ".querySelector('details.detail summary').click()");
        await page.WaitAsync(Card + ".querySelector('details.detail').open", "the fold opened");
        Assert.Contains("Step 80.", await page.TextAsync(Card + ".querySelector('details.detail pre').textContent"), StringComparison.Ordinal);

        // Another ask arriving rebuilds the cards: the fold stays open.
        var other = new WebAskView("a2", "yesno", "Allow?", null, null, null, [new WebChoice("y", "Yes"), new WebChoice("n", "No")], null, null, null);
        PublishWhenStreaming(new WebEvent("asks", () => JsonSerializer.Serialize(new WebAsks([LearnAsk(), other]), WebUiJsonContext.Default.WebAsks)));
        await page.WaitAsync("document.querySelectorAll('#asks .ask').length === 2", "two cards");
        Assert.True((await page.EvalAsync(Card + ".querySelector('details.detail').open")).GetBoolean());

        await page.EvalAsync("[..." + Card + ".querySelectorAll('.choices button')].find(b => b.textContent === 'Keep').click()");
        await UntilAsync(() => _backend.Answers.Any(a => a.AskId == "l1" && a.Choice == "keep"), "Keep's post");
    }
}
