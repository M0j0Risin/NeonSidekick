using System.Text.Json;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The session's side of the tabs and the picture strip (later on 2026-10-15): the pulse carries the state's tabs; a strip picture loads while the
/// strip holds it even when the transcript doesn't; the strip's event is sent when its key changes, at most one per throttle with a trailing send,
/// and a pick the screen refuses is not found.
/// </summary>
public sealed partial class WebUiTests
{
    [Fact]
    public void ThePulse_CarriesTheTabsTheStateSays()
    {
        using var session = new WebUiSession(
            () => new WebState("p", "", "", "m", false, false, false, "", [], null, "1", "tag", "", ChatTab: false, HomeTab: true, ShoppingTab: false),
            _ => WebReply.Ok, () => null, () => { }, _time);

        var pulse = session.Pulse();

        Assert.Equal((false, true, false), (pulse.ChatTab, pulse.HomeTab, pulse.ShoppingTab));
        Assert.Equal(WebTabs.All, session.Tabs());   // no TabsNow: every tab
        session.TabsNow = () => new WebTabs(true, false, false);
        Assert.Equal(new WebTabs(true, false, false), session.Tabs());
    }

    [Fact]
    public void AStripPicture_Loads_WhileTheStripHoldsIt_ThoughTheTranscriptDoesnt()
    {
        var source = new WebPictureSource("a.png", null, [1, 2, 3], "image/png");
        _session.PictureSource = _ => source;
        _session.StripHolds = id => id == 7;

        Assert.Same(source, _session.Picture(7));
        Assert.Null(_session.Picture(8));
    }

    [Fact]
    public void TheStripsEvent_IsSentWhenItsKeyChanges_ThrottledWithATrailingSend()
    {
        using var subscription = _session.Events.Subscribe();
        string key = "on|1|some";
        int built = 0;
        _session.StripKey = () => key;
        _session.StripView = () =>
        {
            built++;
            return new WebStrip([_session.Describe(7, "a.png", 8, 8, false)], 7);
        };

        _session.StripNudge();
        var first = Drain(subscription).Single(e => e.Name == "strip");
        var strip = JsonSerializer.Deserialize(first.Json.Value, WebUiJsonContext.Default.WebStrip)!;
        Assert.Equal((7, (int?)7), (strip.Pictures.Single().Id, strip.Selected));

        // The same key: nothing built, nothing sent (the tick reads it every second).
        _session.PublishStrip();
        Assert.Empty(Drain(subscription));
        Assert.Equal(1, built);

        // A change inside the throttle waits for the window's end, then lands.
        key = "on|2|some";
        _session.StripNudge();
        Assert.Empty(Drain(subscription));
        _time.Advance(WebUiSession.StatusThrottle);
        Assert.Single(Drain(subscription), e => e.Name == "strip");

        // The strip gone (the setting off, a new chat): a null strip, so the drawer closes.
        key = "off|2|some";
        _session.StripView = () => null;
        _time.Advance(WebUiSession.StatusThrottle);
        _session.StripNudge();
        Assert.Equal("null", Drain(subscription).Single(e => e.Name == "strip").Json.Value);
    }

    [Fact]
    public void AStripPick_TheScreenRefuses_IsNotFound()
    {
        _session.StripSelect = id => id == 3;

        Assert.Equal(WebOutcome.Ok, _session.SelectStripPicture(new WebStripSelect { Id = 3 }, null).Outcome);
        var gone = _session.SelectStripPicture(new WebStripSelect { Id = 4 }, null);
        Assert.Equal((WebOutcome.NotFound, WebUiText.NoSuchPicture), (gone.Outcome, gone.Message));
    }

    [Fact]
    public void TheSnapshot_CarriesTheStrip()
    {
        _session.StripView = () => new WebStrip([_session.Describe(2, "b.png", 4, 4, false)], null);

        Assert.Equal(2, _session.Snapshot().Strip!.Pictures.Single().Id);
    }
}
