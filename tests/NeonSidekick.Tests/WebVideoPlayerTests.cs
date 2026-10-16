using System.Text.Json;
using System.Threading.Channels;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Viewer;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// The Web UI's YouTube tab as a player (2026-10-16): who plays (the first page; Play here; a reconnect, a reload and a duplicate told apart;
/// the owner leaving after the grace and the longest-waiting page promoted), a held play going out as a page attaches, the Resume offer by the
/// saved videos' rules, the owner's reports alone, the load's settle, the cap of streams, a full queue, and the forgets. On the manual clock.
/// </summary>
public class WebVideoPlayerTests
{
    private const string Bunny = "aqz-KE-bpKQ";
    private const string Other = "jNQXAC9IVRw";
    private readonly ManualTimeProvider _time = new();
    private readonly List<string> _flashes = [];
    private readonly List<VideoSnapshot?> _seen = [];
    private bool _nudged;
    private int _nudges;

    private WebVideoPlayer Player()
    {
        var player = new WebVideoPlayer(_time, () => { _nudges++; return _nudged; }, _flashes.Add);
        player.Changed += s => _seen.Add(s);
        return player;
    }

    private static string Id(char c) => new(c, 22);

    private sealed record Attached(WebVideoAttachment Attachment, Func<int> Ends)
    {
        public List<(string Name, string Json)> Drain()
        {
            var events = new List<(string, string)>();
            while (Attachment.Events.TryRead(out var e))
            {
                events.Add((e.Name, e.Json.Value));
            }

            return events;
        }
    }

    private static Attached Attach(WebVideoPlayer player, string page, string load, bool claim = false)
    {
        int ends = 0;
        var attachment = player.Attach(page, load, claim, null, () => ends++);
        return new Attached(attachment, () => ends);
    }

    private static string State(int state, string id = Bunny, double position = 0, double duration = 635) =>
        "{\"ev\":\"state\",\"id\":\"" + id + "\",\"title\":\"Big Buck Bunny\",\"state\":" + state + ",\"position\":" + position.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + ",\"duration\":" + duration.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"volume\":100,\"muted\":false}";

    [Fact]
    public void TheFirstPageOwns_ASecondWaits_ItsClaimTakesOver_TheOldStreamLeftOpen()
    {
        var player = Player();
        var a = Attach(player, Id('a'), Id('1'));
        var b = Attach(player, Id('b'), Id('2'));

        Assert.True(a.Attachment.Hello.Owner);
        Assert.False(b.Attachment.Hello.Owner);
        Assert.Equal(Id('a'), player.Owner);

        var c = Attach(player, Id('b'), Id('2'), claim: true);   // Play here: the page's own stream again, with claim
        Assert.Equal(1, b.Ends());   // its old stream replaced
        Assert.Equal(Id('b'), player.Owner);
        Assert.Contains(a.Drain(), e => e.Name == "taken");
        Assert.Equal(0, a.Ends());   // taken never ends a stream: a waits, Play here and all
        Assert.Equal([WebUiText.YouTubeClaimFlash], _flashes);
        Assert.True(c.Attachment.Hello.Owner);

        var again = Attach(player, Id('b'), Id('2'));   // the same page reconnecting: quietly the owner still
        Assert.True(again.Attachment.Hello.Owner);
        Assert.Equal(1, c.Ends());
        Assert.Single(_flashes);
    }

    [Fact]
    public void AHeldPlay_GoesOutAsALoad_WhenAPageAttaches()
    {
        _nudged = true;
        var player = Player();

        player.Play(new VideoRequest(Bunny, 30));
        Assert.Equal(1, _nudges);
        Assert.NotNull(player.Snapshot?.Held);
        Assert.True(player.Snapshot!.Held!.Nudged);
        Assert.Equal(VideoPlace.WebTab, player.Snapshot.Place);
        Assert.True(player.Waiting);

        var a = Attach(player, Id('a'), Id('1'));
        Assert.False(player.Waiting);
        Assert.Null(player.Snapshot!.Held);
        var load = Assert.Single(a.Drain(), e => e.Name == "load");
        Assert.Contains("\"id\":\"" + Bunny + "\"", load.Json);
        Assert.Contains("\"start\":30", load.Json);
    }

    [Fact]
    public void TheOwnerLeaving_CountsOnlyAfterTheGrace_ThenOffersResume_AndPromotesTheLongestWaiting()
    {
        var player = Player();
        var a = Attach(player, Id('a'), Id('1'));
        var b = Attach(player, Id('b'), Id('2'));
        var c = Attach(player, Id('c'), Id('3'));
        player.Play(new VideoRequest(Bunny));
        player.Report(Id('a'), State(1, position: 192), null);

        a.Attachment.Dispose();
        _time.Advance(WebVideoPlayer.DetachGrace - TimeSpan.FromSeconds(1));
        Assert.NotNull(player.Snapshot);   // a reconnect could still come

        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Null(player.Snapshot);
        Assert.Null(_seen[^1]);
        Assert.Equal(Id('b'), player.Owner);
        var owner = Assert.Single(b.Drain(), e => e.Name == "owner");
        using var doc = JsonDocument.Parse(owner.Json);
        Assert.True(doc.RootElement.GetProperty("owner").GetBoolean());
        Assert.Equal(189, doc.RootElement.GetProperty("resume").GetProperty("start").GetDouble());
        Assert.DoesNotContain(c.Drain(), e => e.Name == "owner");

        player.Play(new VideoRequest(Other));
        Assert.Contains(b.Drain(), e => e.Name == "load");   // the next play goes to the promoted page, never held
        Assert.Null(player.Snapshot?.Held);
    }

    [Fact]
    public void AReconnectWithinTheGrace_KeepsTheVideo()
    {
        var player = Player();
        var a = Attach(player, Id('a'), Id('1'));
        player.Play(new VideoRequest(Bunny));
        a.Attachment.Dispose();
        _time.Advance(TimeSpan.FromSeconds(2));

        var back = Attach(player, Id('a'), Id('1'));
        _time.Advance(WebVideoPlayer.DetachGrace * 2);

        Assert.True(back.Attachment.Hello.Owner);
        Assert.NotNull(player.Snapshot);
    }

    [Fact]
    public void ADetach_GivesNullAfterTheGrace_TheOfferInTheNextHello_AndCloseClearsIt()
    {
        var player = Player();
        var a = Attach(player, Id('a'), Id('1'));
        player.Play(new VideoRequest(Bunny));
        player.Report(Id('a'), State(1, position: 60), null);
        a.Attachment.Dispose();
        _time.Advance(WebVideoPlayer.DetachGrace + TimeSpan.FromSeconds(1));
        Assert.Null(player.Snapshot);

        var b = Attach(player, Id('b'), Id('2'));
        Assert.NotNull(b.Attachment.Hello.Resume);
        Assert.Equal("Resume \"Big Buck Bunny\" at 0:57", b.Attachment.Hello.Resume!.Label);

        player.Close();
        Assert.Null(player.Offer);
        Assert.Null(Attach(player, Id('c'), Id('3')).Attachment.Hello.Resume);
    }

    [Fact]
    public void TheOffer_RewindsThreeSeconds_StartsOverUnderFive_AndIsNeverMadeForAFinishedFailedRefusedOrHeldVideo()
    {
        var playing = new VideoSnapshot(VideoState.Paused, Bunny, "Big Buck Bunny", Position: 120, Duration: 635);
        Assert.Equal(117, WebVideoPlayer.OfferFrom(playing)!.Start);
        Assert.Equal(0, WebVideoPlayer.OfferFrom(playing with { Position = 4 })!.Start);
        Assert.Contains("from the start", WebVideoPlayer.OfferFrom(playing with { Position = 4 })!.Label);
        Assert.Null(WebVideoPlayer.OfferFrom(playing with { Position = 630 }));
        Assert.Null(WebVideoPlayer.OfferFrom(playing with { State = VideoState.Ended }));
        Assert.Null(WebVideoPlayer.OfferFrom(playing with { State = VideoState.Failed }));
        Assert.Null(WebVideoPlayer.OfferFrom(playing with { Error = 150 }));
        Assert.Null(WebVideoPlayer.OfferFrom(playing with { Held = new VideoHold(false, DateTimeOffset.UnixEpoch) }));
    }

    [Fact]
    public void TheOffersLabel_HoldsTheTitle_OrTheId_AndAPlaylistsEntry()
    {
        Assert.Equal("Resume \"Big Buck Bunny\" at 3:12", WebVideoPlayer.OfferFrom(new VideoSnapshot(VideoState.Paused, Bunny, "Big Buck Bunny", Position: 195, Duration: 635))!.Label);
        Assert.Equal("Resume video " + Bunny + " at 3:12", WebVideoPlayer.OfferFrom(new VideoSnapshot(VideoState.Paused, Bunny, Position: 195, Duration: 635))!.Label);
        var list = WebVideoPlayer.OfferFrom(new VideoSnapshot(VideoState.Playing, Bunny, "Big Buck Bunny", Position: 195, Duration: 635, PlaylistId: "PLabcdefghijklmnopqrstu", PlaylistEntry: 2))!;
        Assert.Equal("Resume \"Big Buck Bunny\" (entry 3) at 3:12", list.Label);
        Assert.Equal(("PLabcdefghijklmnopqrstu", 2), (list.PlaylistId, list.Entry));
    }

    [Fact]
    public void AReport_FromAPageThatIsNotThePlayer_IsAConflict()
    {
        var player = Player();
        Attach(player, Id('a'), Id('1'));
        Attach(player, Id('b'), Id('2'));
        player.Play(new VideoRequest(Bunny));

        Assert.Equal(WebOutcome.Conflict, player.Report(Id('b'), State(1), null));
        Assert.Equal(WebOutcome.Ok, player.Report(Id('a'), State(1), null));
        Assert.Equal(VideoState.Playing, player.Snapshot!.State);
    }

    [Fact]
    public void AHeldRequest_Expires_AndForgetSendsClose()
    {
        var player = Player();
        player.Play(new VideoRequest(Bunny));
        _time.Advance(YouTube.YouTubeText.HoldFor + TimeSpan.FromSeconds(1));
        player.Tick();
        Assert.Null(player.Snapshot);

        var a = Attach(player, Id('a'), Id('1'));
        Assert.DoesNotContain(a.Drain(), e => e.Name == "load");   // the expired one never goes out
        player.Play(new VideoRequest(Bunny));
        a.Drain();
        player.Forget();
        Assert.Contains(a.Drain(), e => e.Name == "close");
        Assert.Null(player.Snapshot);
    }

    [Fact]
    public void ForgetProfile_DropsTheHeldVideoAndTheOffer_WhileAPlayingVideoPlaysOn()
    {
        var player = Player();
        player.Play(new VideoRequest(Bunny));
        player.ForgetProfile();
        Assert.Null(player.Snapshot);

        var a = Attach(player, Id('a'), Id('1'));
        Assert.DoesNotContain(a.Drain(), e => e.Name == "load");
        player.Play(new VideoRequest(Other));
        player.Report(Id('a'), State(1, Other), null);
        player.ForgetProfile();
        Assert.Equal(Other, player.Snapshot?.VideoId);
        Assert.DoesNotContain(a.Drain(), e => e.Name == "close");
    }

    [Fact]
    public void AFailedReport_IsAFailedSnapshot_WithTheAppsSentence()
    {
        var player = Player();
        Attach(player, Id('a'), Id('1'));
        player.Play(new VideoRequest(Bunny));

        player.Report(Id('a'), "{\"ev\":\"failed\",\"reason\":\"api\"}", null);

        Assert.Equal(VideoState.Failed, player.Snapshot!.State);
        Assert.Equal(WebUiText.PlayerFailed("api"), player.Snapshot.Failure);
    }

    [Fact]
    public void RightAfterALoad_APausedReportWithNoDuration_IsNotPassedOn_AndVersionsCountOnStateErrorAndFailedOnly()
    {
        var player = Player();
        Attach(player, Id('a'), Id('1'));
        player.Play(new VideoRequest(Bunny));
        long opened = player.Snapshot!.Version;

        player.Report(Id('a'), State(2, duration: 0), null);   // the old video stopping, with the new id
        Assert.Equal(VideoState.Opening, player.Snapshot!.State);
        player.Report(Id('a'), "{\"ev\":\"ready\"}", null);
        Assert.Equal(opened, player.Snapshot!.Version);

        player.Report(Id('a'), State(1), null);
        Assert.Equal(VideoState.Playing, player.Snapshot!.State);
        Assert.Equal(opened + 1, player.Snapshot.Version);
        player.Report(Id('a'), "{\"ev\":\"error\",\"code\":150,\"id\":\"" + Bunny + "\"}", null);
        Assert.Equal(opened + 2, player.Snapshot!.Version);
        Assert.Equal(150, player.Snapshot.Error);
    }

    [Fact]
    public void AStoppedPlayersLateReport_NeverBringsAClosedVideoBack()
    {
        var player = Player();
        Attach(player, Id('a'), Id('1'));
        player.Play(new VideoRequest(Bunny));
        player.Report(Id('a'), State(1), null);
        player.Close();

        player.Report(Id('a'), State(5), null);   // the stop's own report
        Assert.Null(player.Snapshot);
    }

    [Fact]
    public void AFifthStream_EvictsTheOldestWaitingPage_NeverTheOwner()
    {
        var player = Player();
        var owner = Attach(player, Id('a'), Id('1'));
        var oldest = Attach(player, Id('b'), Id('2'));
        Attach(player, Id('c'), Id('3'));
        Attach(player, Id('d'), Id('4'));

        Attach(player, Id('e'), Id('5'));

        Assert.Equal(1, oldest.Ends());
        Assert.Equal(0, owner.Ends());
        Assert.Equal(WebVideoPlayer.MaxStreams, player.Streams);
        Assert.Equal(Id('a'), player.Owner);
    }

    [Fact]
    public void AReconnect_EndsTheOldStream_KeepsTheOwner_AndADuplicateGetsAFreshId()
    {
        var player = Player();
        var a = Attach(player, Id('a'), Id('1'));
        var reconnect = Attach(player, Id('a'), Id('1'));
        Assert.Equal(1, a.Ends());
        Assert.Equal(Id('a'), reconnect.Attachment.Hello.Page);
        Assert.Equal(Id('a'), player.Owner);

        a.Attachment.Dispose();   // the replaced stream's own end detaches nothing
        Assert.Equal(1, player.Streams);

        var duplicate = Attach(player, Id('a'), Id('9'));
        Assert.NotEqual(Id('a'), duplicate.Attachment.Hello.Page);
        Assert.True(WebVideoPlayer.IsId(duplicate.Attachment.Hello.Page));
        Assert.False(duplicate.Attachment.Hello.Owner);
    }

    [Fact]
    public void AFullQueue_DropsThePage_AndItsReconnectComesBackAsTheSamePage()
    {
        var player = Player();
        var a = Attach(player, Id('a'), Id('1'));
        player.Play(new VideoRequest(Bunny));
        for (int i = 0; i < WebVideoPlayer.Capacity + 5; i++)
        {
            player.Send(VideoCommand.Pause);
        }

        SpinWait.SpinUntil(() => a.Ends() == 1, TimeSpan.FromSeconds(5));
        Assert.Equal(1, a.Ends());
        Assert.Equal(0, player.Streams);

        var back = Attach(player, Id('a'), Id('1'));
        Assert.True(back.Attachment.Hello.Owner);
        Assert.NotNull(player.Snapshot);
    }

    [Fact]
    public void TheOwnersReload_ClosesItsVideo_AndSendsTheOffer_ABlipReconnectChangesNothing()
    {
        var player = Player();
        var a = Attach(player, Id('a'), Id('1'));
        player.Play(new VideoRequest(Bunny));
        player.Report(Id('a'), State(1, position: 100), null);

        var blip = Attach(player, Id('a'), Id('1'));   // a blip: no page report
        Assert.NotNull(player.Snapshot);

        a.Attachment.Dispose();
        blip.Attachment.Dispose();
        var reloaded = Attach(player, Id('a'), Id('2'));   // a reload: the old stream gone, the same id, a new load
        Assert.True(reloaded.Attachment.Hello.Owner);
        player.Report(Id('a'), "{\"ev\":\"page\"}", null);

        Assert.Null(player.Snapshot);
        Assert.Null(_seen[^1]);
        var resume = Assert.Single(reloaded.Drain(), e => e.Name == "resume");
        Assert.Contains("\"start\":97", resume.Json);
    }

    [Fact]
    public void AResumedReport_RaisesTookOver_AndFlashesThePc()
    {
        var player = Player();
        int took = 0;
        player.TookOver += () => took++;
        Attach(player, Id('a'), Id('1'));

        player.Report(Id('a'), "{\"ev\":\"resumed\"}", null);
        player.Report(Id('a'), State(1), null);

        Assert.Equal(1, took);
        Assert.Contains(WebUiText.YouTubeResumeFlash, _flashes);
        Assert.Equal(VideoState.Playing, player.Snapshot!.State);   // its own load's reports count
    }

    [Fact]
    public void Ids_AreSixteenToSixtyFourBase64UrlCharacters()
    {
        Assert.True(WebVideoPlayer.IsId(WebVideoPlayer.NewId()));
        Assert.True(WebVideoPlayer.IsId(new string('A', 16)));
        Assert.False(WebVideoPlayer.IsId(new string('A', 15)));
        Assert.False(WebVideoPlayer.IsId(new string('A', 65)));
        Assert.False(WebVideoPlayer.IsId("abcdefghijklmnop=="));
        Assert.False(WebVideoPlayer.IsId(null));
    }
}
