using System.Net;
using NeonSidekick.Diagnostics;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.UI;
using NeonSidekick.Viewer;
using NeonSidekick.WebUi;
using NeonSidekick.YouTube;

namespace NeonSidekick.Tests;

/// <summary>
/// The YouTube tab's saved list on the screen (later on 2026-10-16, the user's ask): the hooks over a real <c>youtube.json</c> — a play claims
/// the tapping page and resumes 3 s back on the tab whatever <i>YouTube open links in</i> says, remove, clear, move and save change the file and
/// flash the PC with an audit line, a missing title starts a lookup — and the PC's open <c>/youtube list</c> pane drawn again after a removal
/// made from the page.
/// </summary>
public partial class ChatScreenTests
{
    private const string ZooId = "jNQXAC9IVRw";

    [Fact]
    public async Task TheSavedListsHooks_PlayOnTheTab_UnderYouTubeWindowToo_AndChangeTheFile_FlashingThePc()
    {
        YouTubeRouteFixture(YouTubeOpenLinksIn.YouTubeWindow, webUi: true, youTubeTab: true);
        var library = new YouTubeLibrary(_settings.ProfileDirectory);
        library.Add(BunnyId, "Big Buck Bunny", "Blender", duration: 635, position: 30);
        library.Add(ZooId, "Me at the zoo", "jawed");
        var flashed = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var lines = new List<string>();
        void Record(DiagnosticEvent e) { lock (lines) { lines.Add(e.Message); } }
        WebReply play = default, notConnected = default, gone = default, moved = default, removed = default, savedNow = default, cleared = default;
        WebSavedVideos? before = null;
        VideoSink? active = null;
        List<string> events = [];
        DiagnosticLog.Emitted += Record;
        try
        {
            // Each change's flash is waited for before the next change (2026-10-19, the first CI-like run): FlashPc hands the PC only
            // the newest of a burst, so five changes in a row on a slow machine flashed once ("cleared") and the others never came.
            void Flashed(string text) => SpinWait.SpinUntil(() => flashed.Contains(text), TimeSpan.FromSeconds(10));
            var input = WhenIdle(
                i =>
                {
                    UntilServing();
                    Web.FlashOnPc = flashed.Enqueue;
                    var web = _running!.WebVideoForTests!;
                    before = Web.SavedVideos();
                    notConnected = Web.PlaySaved(new WebSavedPlay { Page = PageOf('a'), Id = BunnyId }, IPAddress.Loopback);
                    using var page = web.Attach(PageOf('a'), PageOf('1'), false, null, () => { });
                    play = Web.PlaySaved(new WebSavedPlay { Page = PageOf('a'), Id = BunnyId }, IPAddress.Loopback);
                    Flashed(WebUiText.SavedPlayFlash);
                    gone = Web.PlaySaved(new WebSavedPlay { Page = PageOf('a'), Id = "xxxxxxxxxxx" }, IPAddress.Loopback);
                    active = _running.RoutedVideoForTests!.Active;
                    while (page.Events.TryRead(out var e))
                    {
                        events.Add(e.Name + " " + e.Json.Value);
                    }

                    moved = Web.MoveSaved(new WebSavedMove { Id = ZooId, To = "top" }, IPAddress.Loopback);
                    Flashed(WebUiText.SavedMoveFlash);
                    removed = Web.RemoveSaved(new WebSavedId { Id = BunnyId }, IPAddress.Loopback);
                    Flashed(WebUiText.SavedRemoveFlash);
                    savedNow = Web.SaveCurrent(IPAddress.Loopback);   // the tab's video, Bunny, saved again
                    Flashed(WebUiText.SavedSaveFlash);
                    cleared = Web.ClearSaved(IPAddress.Loopback);
                    Flashed(WebUiText.SavedClearFlash);
                    PushLine(i, "/exit");
                });

            await RunAsync(input);
        }
        finally
        {
            DiagnosticLog.Emitted -= Record;
        }

        Assert.Equal([BunnyId, ZooId], before!.Items.Select(v => v.Id));
        Assert.Equal(YouTubeText.SavedCaption(2), before.Caption);
        Assert.False(string.IsNullOrEmpty(before.Items[0].Details));

        Assert.Equal(WebOutcome.Conflict, notConnected.Outcome);
        Assert.Equal(WebUiText.YouTubePageNotConnected, notConnected.Message);
        Assert.Equal(WebOutcome.Ok, play.Outcome);
        Assert.Equal(YouTubeText.Resumed(27).Trim(), play.Message);   // 3 s back from where it was left
        Assert.Equal(VideoSink.Web, active);   // on the tab, though YouTube open links in says the video window
        Assert.Contains(events, e => e.StartsWith("load ", StringComparison.Ordinal) && e.Contains("\"start\":27", StringComparison.Ordinal));
        Assert.Empty(_videoPlayer!.Plays);
        Assert.Equal(WebOutcome.NotFound, gone.Outcome);

        Assert.Equal((WebOutcome.Ok, WebOutcome.Ok, WebOutcome.Ok, WebOutcome.Ok), (moved.Outcome, removed.Outcome, savedNow.Outcome, cleared.Outcome));
        Assert.Empty(new YouTubeLibrary(_settings.ProfileDirectory).List());
        Assert.Contains(WebUiText.SavedPlayFlash, flashed);
        Assert.Contains(WebUiText.SavedMoveFlash, flashed);
        Assert.Contains(WebUiText.SavedRemoveFlash, flashed);
        Assert.Contains(WebUiText.SavedSaveFlash, flashed);
        Assert.Contains(WebUiText.SavedClearFlash, flashed);
        lock (lines)
        {
            Assert.Contains(WebUiText.SavedLogLine("played", IPAddress.Loopback), lines);
            Assert.Contains(WebUiText.SavedLogLine("cleared", IPAddress.Loopback), lines);
            Assert.DoesNotContain(lines, l => l.StartsWith("Web UI: the YouTube tab", StringComparison.Ordinal) && l.Contains("Bunny", StringComparison.Ordinal));   // no titles
        }
    }

    [Fact]
    public async Task TheSavedListsMove_ChangesTheOrder_AnUnknownMoveIsRefused_AndASaveWithNothingOpenToo()
    {
        YouTubeRouteFixture(YouTubeOpenLinksIn.WebUiWindow, webUi: true);
        var library = new YouTubeLibrary(_settings.ProfileDirectory);
        library.Add(BunnyId, "Big Buck Bunny");
        library.Add(ZooId, "Me at the zoo");
        WebReply sideways = default, nothingOpen = default, removeGone = default;
        var input = WhenIdle(
            i =>
            {
                UntilServing();
                Web.MoveSaved(new WebSavedMove { Id = BunnyId, To = "down" }, null);
                sideways = Web.MoveSaved(new WebSavedMove { Id = BunnyId, To = "sideways" }, null);
                nothingOpen = Web.SaveCurrent(null);
                removeGone = Web.RemoveSaved(new WebSavedId { Id = "xxxxxxxxxxx" }, null);
                PushLine(i, "/exit");
            });

        await RunAsync(input);

        Assert.Equal([ZooId, BunnyId], new YouTubeLibrary(_settings.ProfileDirectory).List().Select(v => v.Id));
        Assert.Equal((WebOutcome.Refused, WebUiText.SavedMoveUnknown), (sideways.Outcome, sideways.Message));
        Assert.Equal((WebOutcome.Refused, YouTubeText.NothingToSave), (nothingOpen.Outcome, nothingOpen.Message));   // "Error: " taken off
        Assert.Equal(WebOutcome.NotFound, removeGone.Outcome);
    }

    [Fact]
    public async Task AMissingTitle_StartsALookup_AndTheListSaysItLooks()
    {
        YouTubeRouteFixture(YouTubeOpenLinksIn.WebUiWindow, webUi: true);
        new YouTubeLibrary(_settings.ProfileDirectory).Add(ZooId);   // no title yet
        WebSavedVideos? first = null;
        WebSavedVideos? after = null;
        var input = WhenIdle(
            i =>
            {
                UntilServing();
                first = Web.SavedVideos();
                SpinWait.SpinUntil(() => new YouTubeLibrary(_settings.ProfileDirectory).Find(ZooId)?.Title is not null, TimeSpan.FromSeconds(10));
                SpinWait.SpinUntil(() => Web.SavedVideos() is { Looking: false }, TimeSpan.FromSeconds(10));
                after = Web.SavedVideos();
                PushLine(i, "/exit");
            });

        await RunAsync(input);

        Assert.True(first!.Looking);
        Assert.Null(first.Items[0].Title);
        Assert.NotNull(after!.Items[0].Title);
        Assert.False(after.Looking);
    }

    [Fact]
    public async Task TheSavedList_IsNoneWithoutAWebPlayer()
    {
        YouTubeRouteFixture(YouTubeOpenLinksIn.WebUiWindow, webUi: true);
        _videoPlayer = null;   // no video window: no router, no web player
        WebSavedVideos? saved = new([], "", "", false, null);
        var input = WhenIdle(
            i =>
            {
                saved = Web.SavedVideos();
                PushLine(i, "/exit");
            });

        await RunAsync(input);

        Assert.Null(saved);
    }

    [Fact]
    public async Task TheOpenYouTubeListPane_IsDrawnAgain_AfterARemovalFromThePage()
    {
        YouTubeRouteFixture(YouTubeOpenLinksIn.WebUiWindow, webUi: true);
        _console.Profile.Height = 40;
        _geometry = new ScreenGeometry(() => null);
        var library = new YouTubeLibrary(_settings.ProfileDirectory);
        library.Add(BunnyId, "Big Buck Bunny");
        library.Add(ZooId, "Me at the zoo");
        var input = Scripted();
        int waits = 0;
        bool removed = false;
        bool redrawn = false;
        input.OnWait = () =>
        {
            waits++;
            if (waits == 1)
            {
                UntilServing();
                PushLine(input, "/youtube list");
            }
            else if (!removed && Output.Contains(YouTubeText.SavedCaption(2), StringComparison.Ordinal))   // the locked copy: the pane's watch redraws meanwhile
            {
                removed = true;
                Web.RemoveSaved(new WebSavedId { Id = ZooId }, null);   // the page's 🗑️, while the PC's pane is open
                _time.Advance(App.YouTubeSavedMenu.WatchEvery);
            }
            else if (removed && !redrawn && Output.Contains(YouTubeText.SavedCaption(1), StringComparison.Ordinal))
            {
                redrawn = true;
                input.Push(Keys.Escape);
                PushLine(input, "/exit");
            }
            else if (waits > 400)
            {
                input.OnWait = null;
                input.Push(Keys.Escape);
                PushLine(input, "/exit");
            }
        };

        await RunAsync(input);

        Assert.True(redrawn);
    }
}
