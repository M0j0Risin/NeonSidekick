using System.Text.Json;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The PC's point and the page's 🗑️ on the session (later on 2026-10-15): a point is sent when its sequence rises, at most one per throttle with a
/// trailing send; the snapshot carries the last; the shared throttle alone; a picture's key names its file, never its path; a delete of a picture
/// the session doesn't hold is not found, a held one goes to the screen.
/// </summary>
public sealed partial class WebUiTests
{
    [Fact]
    public void ThePointsEvent_IsSentWhenItsSequenceRises_ThrottledWithATrailingSend()
    {
        using var subscription = _session.Events.Subscribe();
        WebPoint? point = new("aa", 1);
        _session.PointNow = () => point;

        _session.PointNudge();
        var first = JsonSerializer.Deserialize(Drain(subscription).Single(e => e.Name == "point").Json.Value, WebUiJsonContext.Default.WebPoint);
        Assert.Equal(new WebPoint("aa", 1), first);

        // The same sequence again: nothing (the screen nudges on every point, the page needs each once).
        _time.Advance(WebUiSession.StatusThrottle);
        _session.PointNudge();
        Assert.Empty(Drain(subscription));

        // A point past the window goes at once; the next inside it lands at the window's end, once.
        _time.Advance(WebUiSession.StatusThrottle);
        point = new WebPoint("bb", 2);
        _session.PointNudge();
        Assert.Single(Drain(subscription), e => e.Name == "point");
        point = new WebPoint("cc", 3);
        _session.PointNudge();
        Assert.Empty(Drain(subscription));
        _time.Advance(WebUiSession.StatusThrottle);
        var trailing = Drain(subscription).Single(e => e.Name == "point");
        Assert.Equal(new WebPoint("cc", 3), JsonSerializer.Deserialize(trailing.Json.Value, WebUiJsonContext.Default.WebPoint));

        Assert.Equal(new WebPoint("cc", 3), _session.Snapshot().Point);
    }

    [Fact]
    public void WebThrottle_SendsAtOnce_ThenOnceAtTheWindowsEnd_NeverTwice()
    {
        int sent = 0;
        using var throttle = new WebThrottle(_time, TimeSpan.FromMilliseconds(200), () => sent++);

        throttle.Nudge();
        Assert.Equal(1, sent);
        throttle.Nudge();
        throttle.Nudge();
        Assert.Equal(1, sent);
        _time.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal(2, sent);   // one trailing send for both
        _time.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal(2, sent);
        throttle.Nudge();
        Assert.Equal(3, sent);   // past the window: at once
    }

    [Fact]
    public void APicturesKey_NamesItsFile_NeverItsPath()
    {
        string path = Path.Combine(Path.GetTempPath(), "pics", "Pony-5.png");
        string key = WebPictureKey.Of(path);

        Assert.Matches("^[0-9a-f]{16}$", key);
        Assert.Equal(key, WebPictureKey.Of(Path.Combine(Path.GetTempPath(), "pics", ".", "Pony-5.png")));   // the full path, its dots resolved
        Assert.NotEqual(key, WebPictureKey.Of(Path.Combine(Path.GetTempPath(), "pics", "Pony-6.png")));
        Assert.DoesNotContain("pony", key, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(key, _session.Describe(1, "Pony-5.png", 1, 1, false, key, deletable: true).Key);
    }

    [WindowsFact]
    public void APicturesKey_IgnoresCase_OnWindows()
    {
        Assert.Equal(WebPictureKey.Of(@"C:\Pics\Pony-5.png"), WebPictureKey.Of(@"c:\pics\PONY-5.PNG"));
    }

    [Fact]
    public void ADelete_OfAPictureTheSessionDoesntHold_IsNotFound_AndAHeldOneGoesToTheScreen()
    {
        var asked = new List<int>();
        _session.StripHolds = id => id == 5;
        _session.PictureDelete = (id, _) =>
        {
            asked.Add(id);
            return new WebReply(WebOutcome.Ok, "Moved a.png to the Recycle bin.");
        };

        var gone = _session.DeletePicture(new WebPictureDelete { Id = 6 }, null);
        Assert.Equal((WebOutcome.NotFound, WebUiText.PictureNotHere), (gone.Outcome, gone.Message));
        Assert.Equal(WebOutcome.Ok, _session.DeletePicture(new WebPictureDelete { Id = 5 }, null).Outcome);
        Assert.Equal([5], asked);
    }
}
