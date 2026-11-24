using System.Text.Json;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// <c>ask_user</c>'s form in the browser (2026-11-24, the ask_user plan): a text question is its box alone, an option's description and the
/// recommended tag show (never checked), the answered count follows the form, Submit is never disabled and sends unanswered questions as
/// skips, a second tap clears a radio, Enter in a text box moves on instead of sending, Decline sends a note or none, and what was picked
/// and typed survives the rebuild another asks event makes.
/// </summary>
public sealed partial class WebUiPageTests
{
    private static WebAskView QuestionsAsk(string id = "q1") => new(id, "question", "Questions", null, null, null, [], null,
        [
            new WebQuestion("What should the file be called?", "Name", "text", []),
            new WebQuestion("Which color?", "Color", "single", [new("red", "Warm.", false), new("blue", null, true)]),
            new WebQuestion("Toppings?", null, "multi", [new("cheese", null, false), new("ham", null, false)]),
        ],
        "Submit");

    private const string First = "document.querySelector('#asks .ask')";

    [WindowsFact]
    public async Task Chat_AQuestionsAsk_TextBoxDescriptionsAndTag_EnterMovesOn_DraftsSurviveARebuild_ASecondTapSkips()
    {
        _backend.Snap = new WebSnapshot([], [QuestionsAsk()], State(), WebUiText.Words);
        if (await SignedInAsync("/") is not { } page)
        {
            return;
        }

        await page.WaitAsync(First + " !== null && " + First + ".querySelector('form') !== null", "the questions card");
        // A text question is its box alone; an option's description and the recommended tag, never checked; the count.
        Assert.Equal(0, (await page.EvalAsync(First + ".querySelectorAll('fieldset')[0].querySelectorAll('input[type=radio], input[type=checkbox]').length")).GetInt32());
        Assert.Equal(WebUiText.AskTyped, await page.TextAsync(First + ".querySelector('fieldset .q-text').placeholder"));
        Assert.Equal("Warm.", await page.TextAsync(First + ".querySelector('.q-desc').textContent"));
        Assert.Equal(WebUiText.AskRecommended, await page.TextAsync(First + ".querySelector('.q-rec').textContent"));
        Assert.False((await page.EvalAsync(First + ".querySelectorAll('fieldset')[1].querySelectorAll('input')[1].checked")).GetBoolean());
        Assert.Equal("0 of 3 answered", await page.TextAsync(First + ".querySelector('.q-count').textContent"));
        Assert.False((await page.EvalAsync(First + ".querySelector('button[type=submit]').disabled")).GetBoolean());

        // Typed, then Enter in the box: nothing sent, the focus on the next question's first input.
        await page.EvalAsync("(() => { const b = " + First + ".querySelector('.q-text'); b.value = 'notes'; b.dispatchEvent(new Event('input', { bubbles: true })); "
            + "b.focus(); b.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true })); })()");
        Assert.True((await page.EvalAsync("document.activeElement === " + First + ".querySelectorAll('fieldset')[1].querySelector('input')")).GetBoolean());
        Assert.Empty(_backend.Answers);

        // A second tap clears a radio; red picked, ham checked, a half-written note.
        await page.EvalAsync("(() => { const r = " + First + ".querySelectorAll('fieldset')[1].querySelectorAll('input'); r[1].click(); r[1].click(); r[0].click(); })()");
        Assert.False((await page.EvalAsync(First + ".querySelectorAll('fieldset')[1].querySelectorAll('input')[1].checked")).GetBoolean());
        await page.EvalAsync(First + ".querySelectorAll('fieldset')[2].querySelectorAll('input')[1].click()");
        await page.EvalAsync("(() => { const c = " + First + "; [...c.querySelectorAll('button')].find(b => b.textContent === '" + WebUiText.AskDecline + "').click(); "
            + "const n = c.querySelector('.q-note input'); n.value = 'half'; n.dispatchEvent(new Event('input', { bubbles: true })); })()");
        Assert.Equal("3 of 3 answered", await page.TextAsync(First + ".querySelector('.q-count').textContent"));

        // Another ask rebuilds the cards: the form is as it was left.
        var other = new WebAskView("a2", "yesno", "Allow?", null, null, null, [new WebChoice("y", "Yes"), new WebChoice("n", "No")], null, null, null);
        PublishWhenStreaming(new WebEvent("asks", () => JsonSerializer.Serialize(new WebAsks([QuestionsAsk(), other]), WebUiJsonContext.Default.WebAsks)));
        await page.WaitAsync("document.querySelectorAll('#asks .ask').length === 2", "two cards");
        Assert.Equal("notes", await page.TextAsync(First + ".querySelector('.q-text').value"));
        Assert.True((await page.EvalAsync(First + ".querySelectorAll('fieldset')[1].querySelectorAll('input')[0].checked")).GetBoolean());
        Assert.True((await page.EvalAsync(First + ".querySelectorAll('fieldset')[2].querySelectorAll('input')[1].checked")).GetBoolean());
        Assert.Equal("half", await page.TextAsync(First + ".querySelector('.q-note input').value"));

        // Red tapped again (a skip after the rebuild too), then Submit: the skip sent as nothing picked.
        await page.EvalAsync(First + ".querySelectorAll('fieldset')[1].querySelectorAll('input')[0].click()");
        await page.EvalAsync(First + ".querySelector('button[type=submit]').click()");
        await UntilAsync(() => _backend.Answers.Any(a => a.AskId == "q1"), "Submit's post");
        var sent = _backend.Answers.Single(a => a.AskId == "q1");
        Assert.Null(sent.Choice);
        Assert.Equal(3, sent.Questions!.Count);
        Assert.Equal("notes", sent.Questions[0].Other);
        Assert.Empty(sent.Questions[1].Picked!);
        Assert.Equal("", sent.Questions[1].Other);
        Assert.Equal([1], sent.Questions[2].Picked!);
    }

    [WindowsFact]
    public async Task Chat_AQuestionsAsk_SubmitWithNothingAnswered_SendsSkips_DeclineSendsANoteOrNone()
    {
        _backend.Snap = new WebSnapshot([], [QuestionsAsk()], State(), WebUiText.Words);
        if (await SignedInAsync("/") is not { } page)
        {
            return;
        }

        await page.WaitAsync(First + " !== null && " + First + ".querySelector('form') !== null", "the questions card");
        await page.EvalAsync(First + ".querySelector('button[type=submit]').click()");
        await UntilAsync(() => _backend.Answers.Any(a => a.AskId == "q1"), "Submit's post");
        var skipped = _backend.Answers.Single(a => a.AskId == "q1");
        Assert.All(skipped.Questions!, q => Assert.True(q.Picked!.Count == 0 && string.IsNullOrEmpty(q.Other)));

        // A plain decline: Decline, then Send with the note empty.
        PublishWhenStreaming(new WebEvent("asks", () => JsonSerializer.Serialize(new WebAsks([QuestionsAsk("q2")]), WebUiJsonContext.Default.WebAsks)));
        await page.WaitAsync(First + " !== null && " + First + ".querySelector('fieldset input[name^=\"q2-\"]') !== null", "the second card");
        await page.EvalAsync("(() => { const c = " + First + "; [...c.querySelectorAll('button')].find(b => b.textContent === '" + WebUiText.AskDecline + "').click(); "
            + "[...c.querySelectorAll('button')].find(b => b.textContent === '" + WebUiText.AskSend + "').click(); })()");
        await UntilAsync(() => _backend.Answers.Any(a => a.AskId == "q2"), "the decline's post");
        var plain = _backend.Answers.Single(a => a.AskId == "q2");
        Assert.Equal((WebAskBroker.DeclineChoice, ""), (plain.Choice, plain.Text));

        // With a note, sent by Enter in its box.
        PublishWhenStreaming(new WebEvent("asks", () => JsonSerializer.Serialize(new WebAsks([QuestionsAsk("q3")]), WebUiJsonContext.Default.WebAsks)));
        await page.WaitAsync(First + " !== null && " + First + ".querySelector('fieldset input[name^=\"q3-\"]') !== null", "the third card");
        await page.EvalAsync("(() => { const c = " + First + "; [...c.querySelectorAll('button')].find(b => b.textContent === '" + WebUiText.AskDecline + "').click(); "
            + "const n = c.querySelector('.q-note input'); n.value = ' later '; n.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true })); })()");
        await UntilAsync(() => _backend.Answers.Any(a => a.AskId == "q3"), "the noted decline's post");
        var noted = _backend.Answers.Single(a => a.AskId == "q3");
        Assert.Equal((WebAskBroker.DeclineChoice, "later"), (noted.Choice, noted.Text));
    }
}
