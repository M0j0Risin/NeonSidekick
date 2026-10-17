using System.Text.Json;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// A bot check's card in the page (2026-10-17): the live ask draws the Browser window's picture, a tap posts its place as fractions of the
/// picture, a held press posts its down and then its up, the card's rebuild keeps the same picture, and the card and its reads go when the
/// ask closes.
/// </summary>
public sealed partial class WebUiPageTests
{
    private static WebAskView BotCheckAsk(string id = "a7") =>
        new(id, "botcheck", "Browser window", "Solve the check for example.com in the Browser window.", null, null, [new WebChoice("giveup", "Give up")], null, null, null, Live: true);

    private void PublishAsks(params WebAskView[] asks) =>
        _backend.Events.Publish(new WebEvent("asks", () => JsonSerializer.Serialize(new WebAsks([.. asks]), WebUiJsonContext.Default.WebAsks)));

    private const string Picture = "document.querySelector('#asks .botcheck-picture')";

    // A pointer's down and up at a fraction of the picture, as a finger's.
    private static string Pointer(string type, double x, double y) =>
        "(() => { const i = " + Picture + "; const r = i.getBoundingClientRect(); i.dispatchEvent(new PointerEvent('" + type + "', { pointerId: 1, button: 0, bubbles: true, "
        + "clientX: r.left + r.width * " + x.ToString(System.Globalization.CultureInfo.InvariantCulture) + ", clientY: r.top + r.height * " + y.ToString(System.Globalization.CultureInfo.InvariantCulture) + " })); return true; })()";

    [WindowsFact]
    public async Task Chat_ABotCheckCard_ShowsTheWindow_AndATapAndAHoldReachIt()
    {
        _backend.BotCheckLiveId = "a7";
        _backend.Snap = new WebSnapshot([], [BotCheckAsk()], State(), WebUiText.Words);
        if (await SignedInAsync("/") is not { } page)
        {
            return;
        }

        await page.WaitAsync(Picture + " && " + Picture + ".src.startsWith('data:image/jpeg') && " + Picture + ".naturalWidth === 1", "the window's picture");
        Assert.Equal(WebUiText.BotCheckPicture, await page.TextAsync(Picture + ".alt"));
        Assert.Equal(WebUiText.BotCheckHint, await page.TextAsync("document.querySelector('#asks .botcheck-hint').textContent"));
        Assert.Equal(WebUiText.BotCheckTypeHere, await page.TextAsync("document.querySelector('#asks .botcheck-type input').placeholder"));
        Assert.Contains(_backend.BotCheckWidths, w => w is >= WebUiHost.BotCheckMinWidth and <= WebUiHost.BotCheckMaxWidth);

        // A quick tap: one post, its place a fraction of the picture.
        await page.EvalAsync(Pointer("pointerdown", 0.25, 0.5));
        await page.EvalAsync(Pointer("pointerup", 0.25, 0.5));
        SpinWait.SpinUntil(() => _backend.BotCheckInputs.Count == 1, TimeSpan.FromSeconds(10));
        var tap = Assert.Single(_backend.BotCheckInputs);
        Assert.Equal(("a7", "tap"), (tap.AskId, tap.Kind));
        Assert.Equal(0.25, tap.X, 2);
        Assert.Equal(0.5, tap.Y, 2);

        // A press held past 150 ms: its down, then its up where it pressed.
        await page.EvalAsync(Pointer("pointerdown", 0.75, 0.2));
        await Task.Delay(300);
        await page.EvalAsync(Pointer("pointerup", 0.9, 0.9));
        SpinWait.SpinUntil(() => _backend.BotCheckInputs.Count == 3, TimeSpan.FromSeconds(10));
        var held = _backend.BotCheckInputs.Skip(1).ToList();
        Assert.Equal(["down", "up"], held.Select(i => i.Kind));
        Assert.All(held, i => Assert.Equal(0.75, i.X, 2));
        Assert.All(held, i => Assert.Equal(0.2, i.Y, 2));

        // The buttons and the box: Enter alone, then a text with no Enter after it.
        await page.EvalAsync("document.querySelector('#asks .botcheck-bar button[aria-label=\"" + WebUiText.BotCheckEnter + "\"]').click()");
        await page.EvalAsync("(() => { const f = document.querySelector('#asks .botcheck-type input'); f.value = 'Main St'; f.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })); return true; })()");
        SpinWait.SpinUntil(() => _backend.BotCheckInputs.Count == 5, TimeSpan.FromSeconds(10));
        var typed = _backend.BotCheckInputs.Skip(3).ToList();
        Assert.Equal([("key", "Enter", (string?)null), ("text", null, "Main St")], typed.Select(i => (i.Kind!, i.Key, i.Text)));

        // The cards rebuilt on another asks event keep the same picture element.
        await page.EvalAsync(Picture + ".dataset.kept = '1'");
        PublishAsks(BotCheckAsk());
        await page.WaitAsync("document.querySelectorAll('#asks section.ask').length === 1 && " + Picture + ".dataset.kept === '1'", "the same picture after the rebuild");

        // The ask closed: the card goes, and its reads stop.
        PublishAsks();
        await page.WaitAsync("document.querySelectorAll('#asks .botcheck-view').length === 0", "the card gone");
        int reads = _backend.BotCheckWidths.Count;
        await Task.Delay(1200);
        Assert.Equal(reads, _backend.BotCheckWidths.Count);
    }

    [WindowsFact]
    public async Task Chat_ABotCheckCard_WhoseCheckIsOver_DropsItsView_AndKeepsGiveUp()
    {
        _backend.BotCheckLiveId = "another";
        _backend.Snap = new WebSnapshot([], [BotCheckAsk()], State(), WebUiText.Words);
        if (await SignedInAsync("/") is not { } page)
        {
            return;
        }

        await page.WaitAsync("document.querySelector('#asks .botcheck-box') && document.querySelector('#asks .botcheck-box').classList.contains('hidden')", "the view hidden on a 404");
        Assert.Equal("Give up", await page.TextAsync("document.querySelector('#asks .choices button').textContent"));
        int reads = _backend.BotCheckWidths.Count;
        await Task.Delay(1200);
        Assert.Equal(reads, _backend.BotCheckWidths.Count);
    }
}
