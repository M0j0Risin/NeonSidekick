using NeonSidekick.Viewer;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// A video waiting for the YouTube tab (2026-10-16): the state and the pulse carry the banner's words only while one is held, null once a page
/// attaches; <see cref="WebUiSession.WatchNudge"/> says a page will see it when a Chat stream is open or a pulse was read in the last 5 seconds.
/// </summary>
public sealed partial class WebUiTests
{
    [Fact]
    public void TheWatchWords_RideTheStateAndThePulse_OnlyWhileAVideoIsHeld()
    {
        WebVideoPlayer? web = null;
        WebState Now() => new("p", "", "", "m", false, false, false, "", [], null, "1", YouTubeTab: true, Watch: web?.Waiting == true ? WebUiText.WatchBanner : null);
        using var session = new WebUiSession(Now, _ => WebReply.Ok, () => null, () => { }, _time);
        web = new WebVideoPlayer(_time, session.WatchNudge);

        Assert.Null(session.Pulse().Watch);
        web.Play(new VideoRequest("aqz-KE-bpKQ"));
        Assert.Equal(WebUiText.WatchBanner, session.Pulse().Watch);
        Assert.True(session.Pulse().YouTubeTab);

        using var attachment = web.Attach(new string('a', 22), new string('1', 22), false, null, () => { });
        Assert.Null(session.Pulse().Watch);
        Assert.Null(Now().Watch);
        web.Dispose();
    }

    [Fact]
    public void WatchNudge_IsTrue_WithAChatStream_OrAFreshPulse_AndFalseOtherwise()
    {
        Assert.False(_session.WatchNudge());   // nobody: no stream, no pulse read

        _session.Pulse();
        Assert.True(_session.WatchNudge());    // a pulse read just now
        _time.Advance(WebUiSession.PulseFresh + TimeSpan.FromSeconds(1));
        Assert.False(_session.WatchNudge());

        using var stream = _session.Events.Subscribe();
        Assert.True(_session.WatchNudge());
        Assert.Contains(Drain(stream), e => e.Name == "watch" && System.Text.Json.JsonSerializer.Deserialize(e.Json.Value, WebUiJsonContext.Default.WebTextBack)!.Text == WebUiText.WatchBanner);
    }

}
