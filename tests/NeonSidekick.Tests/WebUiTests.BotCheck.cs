using System.Text.Json;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>A bot check's card in the session (2026-10-17): the ask's live flag reaches the page's view, its words go with the snapshot, and a session with no screen's hooks answers 404.</summary>
public sealed partial class WebUiTests
{
    [Fact]
    public void ABotCheckAsk_CarriesItsLiveFlag_ToThePagesView()
    {
        using var subscription = _session.Events.Subscribe();
        var live = _session.Asks.Open(new WebAskSpec("botcheck", "Browser window", [new WebChoice("giveup", "Give up")], Body: "Solve it.", Live: true));
        var plain = _session.Asks.Open(new WebAskSpec("allow", "Bot check", [new WebChoice("once", "Allow once")]));

        var views = _session.Snapshot().Asks;
        Assert.Equal([true, false], views.Select(v => v.Live));
        var sent = JsonSerializer.Deserialize(Drain(subscription).Last(e => e.Name == "asks").Json.Value, WebUiJsonContext.Default.WebAsks)!;
        Assert.Equal([true, false], sent.Asks.Select(v => v.Live));
        _session.Asks.Close(live);
        _session.Asks.Close(plain);
    }

    [Fact]
    public async Task ABotCheckCard_WithNoScreensHooks_Is404()
    {
        Assert.Equal((WebOutcome.NotFound, null), await _session.BotCheckFrameAsync("a1", 640, CancellationToken.None));
        Assert.Equal(WebOutcome.NotFound, (await _session.BotCheckInputAsync(new WebBotCheckInput { AskId = "a1", Kind = "tap" }, null, CancellationToken.None)).Outcome);
    }

    [Fact]
    public void TheBotCheckCardsWords_AreSentWithTheSnapshot()
    {
        var w = WebUiText.Words;
        Assert.Equal(WebUiText.BotCheckHint, w.BotCheckHint);
        Assert.Equal(WebUiText.BotCheckStalled, w.BotCheckStalled);
        Assert.All(
            new[] { w.BotCheckPicture, w.BotCheckZoom, w.BotCheckScrollUp, w.BotCheckScrollDown, w.BotCheckTypeHere, w.BotCheckSend, w.BotCheckEnter, w.BotCheckBackspace, w.BotCheckTab },
            word => Assert.False(string.IsNullOrEmpty(word)));
        Assert.Equal(["Enter", "Backspace", "Tab"], new[] { w.BotCheckEnter, w.BotCheckBackspace, w.BotCheckTab });   // the keys BrowserRemote takes, as named there
    }
}
