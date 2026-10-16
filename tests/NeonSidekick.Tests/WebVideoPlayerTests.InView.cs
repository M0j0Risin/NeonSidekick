using System.Text.Json;
using NeonSidekick.Viewer;
using NeonSidekick.WebUi;

namespace NeonSidekick.Tests;

/// <summary>
/// Which page plays (later on 2026-10-16, the user's report: a play went to a hidden tab that had merely reconnected first, and a Play here
/// dropped the video): while nothing plays the player follows the tab shown most recently; a play goes to the tab shown most recently; a tab
/// that is playing keeps the player until a new play or a Play here; with none in view a play waits. Play here carries the video; a page's
/// first report never closes the video just given to it; a repeated claim id is a reconnect; only a hold nudges. And the saved list's
/// <see cref="WebVideoPlayer.Claim"/>.
/// </summary>
public partial class WebVideoPlayerTests
{
    private static List<string> Names(Attached page) => [.. page.Drain().Select(e => e.Name)];

    private static string Page(string load) => "{\"ev\":\"page\",\"load\":\"" + load + "\"}";

    // ── Rule 1: attaching ─────────────────────────────────────────────────

    [Fact]
    public void AnIdleOwner_HiddenOrVisible_YieldsToAPageAttachingInView_APlayingOneDoesNot()
    {
        var player = Player();
        var a = Attach(player, Id('a'), Id('1'));
        var b = Attach(player, Id('b'), Id('2'));   // in view, the owner idle (visible too): b plays
        Assert.Equal(Id('b'), player.Owner);
        Assert.True(b.Attachment.Hello.Owner);
        Assert.Contains("taken", Names(a));
        Assert.DoesNotContain("owner", Names(b));   // its hello said so

        var c = Attach(player, Id('c'), Id('3'), visible: false);   // hidden: waits
        Assert.Equal(Id('b'), player.Owner);
        Assert.False(c.Attachment.Hello.Owner);

        player.Play(new VideoRequest(Bunny));
        player.Report(Id('b'), State(1), null);
        var d = Attach(player, Id('d'), Id('4'));   // in view, but b is playing: d waits
        Assert.Equal(Id('b'), player.Owner);
        Assert.False(d.Attachment.Hello.Owner);
    }

    [Fact]
    public void TheStartupRace_TwoPagesReconnecting_TheHiddenOneFirst_EndsWithTheVisibleOneAsOwner()
    {
        var player = Player();
        Attach(player, Id('h'), Id('1'), visible: false);   // the tab in the background reconnected first
        Assert.Equal(Id('h'), player.Owner);
        Attach(player, Id('v'), Id('2'));
        Assert.Equal(Id('v'), player.Owner);

        player.Play(new VideoRequest(Bunny));
        Assert.Equal(Id('v'), player.Owner);
        Assert.Null(player.Snapshot!.Held);
    }

    [Fact]
    public void AStreamWithoutVisible_CountsAsVisible()
    {
        var player = Player();
        Attach(player, Id('a'), Id('1'), visible: false);
        player.Attach(Id('b'), Id('2'), claim: false, null, () => { });   // the first script's shape: no visible=
        Assert.Equal(Id('b'), player.Owner);
        Assert.True(player.VisibleOf(Id('b')));
    }

    // ── Rule 2 and 3: seen ────────────────────────────────────────────────

    [Fact]
    public void SeenInView_FromAWaitingPage_TakesTheIdleOwnersPlayer_NotAPlayingOnes()
    {
        var player = Player();
        var a = Attach(player, Id('a'), Id('1'));
        var b = Attach(player, Id('b'), Id('2'), visible: false);
        a.Drain();

        Assert.Equal(WebOutcome.Ok, player.Seen(Id('b'), true, null));
        Assert.Equal(Id('b'), player.Owner);
        Assert.Contains("taken", Names(a));
        Assert.Contains("owner", Names(b));

        player.Play(new VideoRequest(Bunny));   // to b, the page seen last
        player.Report(Id('b'), State(1), null);
        player.Seen(Id('a'), true, null);
        Assert.Equal(Id('b'), player.Owner);   // b plays: a coming into view moves nothing
    }

    [Fact]
    public void Seen_FromAPageWithNoStream_IsAConflict()
    {
        var player = Player();
        Assert.Equal(WebOutcome.Conflict, player.Seen(Id('a'), true, null));
    }

    [Fact]
    public void SeenHidden_FromAnIdleOwner_HandsOverToTheVisibleWaiter_AndBothOrdersOfATabSwitchEndTheSame()
    {
        // The new tab shown first, then the old one hidden.
        var player = Player();
        Attach(player, Id('a'), Id('1'));
        Attach(player, Id('b'), Id('2'), visible: false);
        player.Seen(Id('b'), true, null);
        player.Seen(Id('a'), false, null);
        Assert.Equal(Id('b'), player.Owner);

        // The old one hidden first, then the new one shown.
        var other = Player();
        Attach(other, Id('a'), Id('1'));
        Attach(other, Id('b'), Id('2'), visible: false);
        other.Seen(Id('a'), false, null);
        Assert.Equal(Id('a'), other.Owner);   // no other page in view: it stays, so a play waits for it
        other.Seen(Id('b'), true, null);
        Assert.Equal(Id('b'), other.Owner);
    }

    // ── Rule 4: plays ─────────────────────────────────────────────────────

    [Fact]
    public void APlay_WithTwoVisiblePages_GoesToTheOneShownMostRecently_EvenFromAPlayingOwner()
    {
        var player = Player();
        var a = Attach(player, Id('a'), Id('1'));
        player.Play(new VideoRequest(Bunny));
        player.Report(Id('a'), State(1), null);
        var b = Attach(player, Id('b'), Id('2'), visible: false);
        _time.Advance(TimeSpan.FromSeconds(1));
        player.Seen(Id('b'), true, null);   // shown after a
        a.Drain();

        player.Play(new VideoRequest(Other));

        Assert.Equal(Id('b'), player.Owner);
        Assert.Contains("taken", Names(a));
        Assert.Equal(["owner", "load"], Names(b));
    }

    [Fact]
    public void APlay_WithAHiddenOwnerAndTwoVisibleWaiters_GoesToTheOneSeenMostRecently()
    {
        var player = Player();
        var owner = Attach(player, Id('a'), Id('1'));
        player.Play(new VideoRequest(Other));
        player.Report(Id('a'), State(1, Other), null);
        player.Seen(Id('a'), false, null);   // playing on, hidden: kept
        var c = Attach(player, Id('c'), Id('3'));
        _time.Advance(TimeSpan.FromSeconds(1));
        var b = Attach(player, Id('b'), Id('2'));   // shown later
        Assert.Equal(Id('a'), player.Owner);
        owner.Drain();

        player.Play(new VideoRequest(Bunny));

        Assert.Equal(Id('b'), player.Owner);
        Assert.Contains("taken", Names(owner));
        Assert.Equal(["owner", "load"], Names(b));
        Assert.DoesNotContain("load", Names(c));
    }

    [Fact]
    public void APlay_WithNoPageInView_IsHeld_AHiddenOwnersOtherVideoClosed_AndItLoadsWhenOneShows()
    {
        var player = Player();
        var a = Attach(player, Id('a'), Id('1'));
        player.Play(new VideoRequest(Other));
        player.Report(Id('a'), State(1, Other), null);
        player.Seen(Id('a'), false, null);   // the phone locked, Other playing on
        a.Drain();

        player.Play(new VideoRequest(Bunny));

        Assert.Equal(new VideoHold(false, player.Snapshot!.Held!.Since, TabsHidden: true), player.Snapshot.Held);
        Assert.True(player.Waiting);
        Assert.Equal(["close"], Names(a));

        player.Seen(Id('a'), true, null);   // unlocked
        var load = Assert.Single(a.Drain(), e => e.Name == "load");
        Assert.Contains(Bunny, load.Json);
        Assert.Null(player.Snapshot!.Held);
        Assert.False(player.Waiting);
    }

    [Fact]
    public void AHeldPlay_LoadsIntoAWaiterThatComesIntoView()
    {
        var player = Player();
        Attach(player, Id('a'), Id('1'), visible: false);
        var b = Attach(player, Id('b'), Id('2'), visible: false);
        player.Play(new VideoRequest(Bunny));
        Assert.NotNull(player.Snapshot!.Held);

        player.Seen(Id('b'), true, null);

        Assert.Equal(Id('b'), player.Owner);
        Assert.Equal(["owner", "load"], Names(b));
    }

    [Fact]
    public void AnOwnerWithNoStream_CountsAsHidden_ForAPlay()
    {
        var player = Player();
        var a = Attach(player, Id('a'), Id('1'));
        a.Attachment.Dispose();   // its grace runs

        player.Play(new VideoRequest(Bunny));

        Assert.NotNull(player.Snapshot!.Held);
        Assert.False(player.Snapshot.Held!.TabsHidden);   // no stream at all
    }

    // ── Rule 6: a held request loads only into a visible owner ─────────────

    [Fact]
    public void AHeldRequest_IsNotLoadedIntoAnOwnerReconnectingHidden_NorAHiddenPagePromoted_UntilItShows()
    {
        var player = Player();
        var a = Attach(player, Id('a'), Id('1'), visible: false);
        player.Play(new VideoRequest(Bunny));
        var again = Attach(player, Id('a'), Id('1'), visible: false);   // a reconnect in the background
        Assert.DoesNotContain("load", Names(again));
        Assert.NotNull(player.Snapshot!.Held);

        var b = Attach(player, Id('b'), Id('2'), visible: false);
        again.Attachment.Dispose();
        _time.Advance(WebVideoPlayer.DetachGrace + TimeSpan.FromSeconds(1));   // b promoted, hidden
        Assert.Equal(Id('b'), player.Owner);
        Assert.DoesNotContain("load", Names(b));

        player.Seen(Id('b'), true, null);
        Assert.Contains(b.Drain(), e => e.Name == "load");
        GC.KeepAlive(a);
    }

    // ── Rule 5 and 7 ──────────────────────────────────────────────────────

    [Fact]
    public void OwnerLeft_PromotesTheVisiblePageSeenMostRecently()
    {
        var player = Player();
        var a = Attach(player, Id('a'), Id('1'));
        player.Play(new VideoRequest(Bunny));
        player.Report(Id('a'), State(1), null);
        Attach(player, Id('b'), Id('2'));   // in view, waits (a plays)
        _time.Advance(TimeSpan.FromSeconds(1));
        Attach(player, Id('c'), Id('3'));   // in view later
        Attach(player, Id('d'), Id('4'), visible: false);

        a.Attachment.Dispose();
        _time.Advance(WebVideoPlayer.DetachGrace + TimeSpan.FromSeconds(1));

        Assert.Equal(Id('c'), player.Owner);
    }

    [Fact]
    public void Eviction_TakesTheOldestHiddenWaiterFirst()
    {
        var player = Player();
        Attach(player, Id('a'), Id('1'));
        player.Play(new VideoRequest(Bunny));
        player.Report(Id('a'), State(1), null);
        var b = Attach(player, Id('b'), Id('2'));
        var c = Attach(player, Id('c'), Id('3'), visible: false);
        Attach(player, Id('d'), Id('4'));

        Attach(player, Id('e'), Id('5'));

        Assert.Equal(1, c.Ends());
        Assert.Equal(0, b.Ends());
    }

    // ── Fix 2: Play here carries the video ────────────────────────────────

    [Fact]
    public void AClaim_WhileAVideoPlays_TakesItAlong_TheHelloSaysPlay_AndItsResumeTakesOver()
    {
        var player = Player();
        int took = 0;
        player.TookOver += () => took++;
        var a = Attach(player, Id('a'), Id('1'));
        player.Play(new VideoRequest(Bunny));
        player.Report(Id('a'), State(1, position: 120), null);
        Attach(player, Id('b'), Id('2'));   // waits: a plays
        a.Drain();
        _seen.Clear();

        var b = Attach(player, Id('b'), Id('2'), claimId: Id('x'));

        Assert.Contains("taken", Names(a));
        Assert.Null(player.Snapshot);
        Assert.Equal([null], _seen);   // raised once, the close
        Assert.True(b.Attachment.Hello.Owner);
        Assert.True(b.Attachment.Hello.Play);
        Assert.Equal(117, b.Attachment.Hello.Resume!.Start);

        player.Report(Id('b'), Page(Id('2')), null);   // its first report: nothing open, nothing to close
        player.Report(Id('b'), "{\"ev\":\"resumed\"}", null);
        Assert.Equal(1, took);
        player.Report(Id('b'), State(1, position: 118), null);
        Assert.Equal(VideoState.Playing, player.Snapshot!.State);
    }

    [Fact]
    public void AClaim_WithNothingOpen_SaysNoPlay()
    {
        var player = Player();
        Attach(player, Id('a'), Id('1'));
        var b = Attach(player, Id('b'), Id('2'), claimId: Id('x'), visible: false);
        Assert.True(b.Attachment.Hello.Owner);
        Assert.False(b.Attachment.Hello.Play);
    }

    // ── Fix 3: a page's first report never closes the video just sent to it ─

    [Fact]
    public void AHeldRequestLoadedAtAttach_SurvivesThatPagesFirstReport_AReloadsStillCloses()
    {
        var player = Player();
        player.Play(new VideoRequest(Bunny));
        var a = Attach(player, Id('a'), Id('1'));
        Assert.Contains("load", Names(a));

        player.Report(Id('a'), Page(Id('1')), null);
        Assert.NotNull(player.Snapshot);
        player.Report(Id('a'), State(1, position: 100), null);
        Assert.Equal(VideoState.Playing, player.Snapshot!.State);

        a.Attachment.Dispose();
        var reloaded = Attach(player, Id('a'), Id('9'));   // a real reload: a new nonce
        player.Report(Id('a'), Page(Id('9')), null);
        Assert.Null(player.Snapshot);
        Assert.Contains(reloaded.Drain(), e => e.Name == "resume");
    }

    [Fact]
    public void APageReportWithoutLoad_BehavesAsBefore()
    {
        var player = Player();
        player.Play(new VideoRequest(Bunny));
        Attach(player, Id('a'), Id('1'));
        player.Report(Id('a'), "{\"ev\":\"page\"}", null);
        Assert.Null(player.Snapshot);   // the first script's page report: read as the reload it always was
    }

    // ── Fix 4: a repeated claim id is a reconnect ─────────────────────────

    [Fact]
    public void ARepeatedClaimId_IsAPlainReconnect_ANewOneClaims_AndOneIsAlwaysAClaim()
    {
        var player = Player();
        Attach(player, Id('a'), Id('1'));
        player.Play(new VideoRequest(Bunny));
        player.Report(Id('a'), State(1), null);
        Attach(player, Id('b'), Id('2'), claimId: Id('x'));   // B claims with X
        Assert.Equal(Id('b'), player.Owner);
        Attach(player, Id('a'), Id('1'), claimId: Id('y'));   // A claims back with Y
        Assert.Equal(Id('a'), player.Owner);

        Attach(player, Id('b'), Id('2'), claimId: Id('x'), visible: false);   // B's browser reconnects to its old URL
        Assert.Equal(Id('a'), player.Owner);

        Attach(player, Id('b'), Id('2'), claimId: Id('z'), visible: false);   // a new tap
        Assert.Equal(Id('b'), player.Owner);

        Attach(player, Id('a'), Id('1'), claim: true, visible: false);   // the first script's claim=1, every time
        Assert.Equal(Id('a'), player.Owner);
        Attach(player, Id('b'), Id('2'), claimId: Id('z'), visible: false);
        Assert.Equal(Id('a'), player.Owner);
    }

    [Fact]
    public void AfterAClaimsThenBClaims_AsReconnectWithItsOldId_NeverTakesPlaybackBack()
    {
        var player = Player();
        var first = Attach(player, Id('a'), Id('1'), claimId: Id('x'));
        _time.Advance(TimeSpan.FromSeconds(1));
        Attach(player, Id('b'), Id('2'), claimId: Id('y'));
        Assert.Equal(Id('b'), player.Owner);
        player.Play(new VideoRequest(Bunny));
        player.Report(Id('b'), State(1), null);   // B plays

        first.Attachment.Dispose();   // a blip: A's stream gone a moment, its URL's claim and visible= stale
        _time.Advance(TimeSpan.FromSeconds(3));
        Attach(player, Id('a'), Id('1'), claimId: Id('x'));

        Assert.Equal(Id('b'), player.Owner);
    }

    // ── Fix 5: only a hold nudges ─────────────────────────────────────────

    [Fact]
    public void APlayToAnOwnerInView_NeverNudges_AHeldOneNudgesOnceAfterItIsHeld_AndRaisesOnce()
    {
        var player = Player();
        Attach(player, Id('a'), Id('1'));
        player.Play(new VideoRequest(Bunny));
        Assert.Equal(0, _nudges);

        WebVideoPlayer? held = null;
        bool waitingAtNudge = false;
        held = new WebVideoPlayer(_time, () => { waitingAtNudge = held!.Waiting; return true; });
        var raised = new List<VideoSnapshot?>();
        held.Changed += raised.Add;

        held.Play(new VideoRequest(Bunny));

        Assert.True(waitingAtNudge);   // the state the nudge publishes already says Waiting
        Assert.True(held.Snapshot!.Held!.Nudged);
        Assert.True(Assert.Single(raised)!.Held!.Nudged);   // raised once, with the nudge's answer
    }

    // ── The saved list's claim (Part 1) ───────────────────────────────────

    [Fact]
    public void Claim_FromTheOwner_IsOkWithNoEvents()
    {
        var player = Player();
        var a = Attach(player, Id('a'), Id('1'));
        a.Drain();
        Assert.Equal(WebOutcome.Ok, player.Claim(Id('a'), null));
        Assert.Empty(a.Drain());
        Assert.Empty(_flashes);
    }

    [Fact]
    public void Claim_FromAWaitingPage_TellsBoth_MarksItSeen_AndThePlayThatFollowsLoadsThere()
    {
        var player = Player();
        var a = Attach(player, Id('a'), Id('1'));
        player.Play(new VideoRequest(Other));
        player.Report(Id('a'), State(1, Other), null);
        var b = Attach(player, Id('b'), Id('2'), visible: false);
        a.Drain();

        Assert.Equal(WebOutcome.Ok, player.Claim(Id('b'), null));
        Assert.Contains("taken", Names(a));
        Assert.Equal(["owner"], Names(b));
        Assert.True(player.VisibleOf(Id('b')));
        Assert.Equal([WebUiText.YouTubeClaimFlash], _flashes);
        Assert.Equal(Other, player.Snapshot?.VideoId);   // no carry: the play that follows replaces it

        player.Play(new VideoRequest(Bunny, 57));
        var load = Assert.Single(b.Drain(), e => e.Name == "load");
        Assert.Contains("\"start\":57", load.Json);
        Assert.DoesNotContain("load", Names(a));
    }

    [Fact]
    public void Claim_FromAPageWithNoStream_IsAConflict()
    {
        var player = Player();
        Attach(player, Id('a'), Id('1'));
        Assert.Equal(WebOutcome.Conflict, player.Claim(Id('b'), null));
        Assert.Equal(Id('a'), player.Owner);
    }

    [Fact]
    public void TheHello_SerializesItsPlay()
    {
        var hello = new WebVideoHello(Id('a'), WebUiText.VideoWords, true, null, 1000, Play: true);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(hello, WebUiJsonContext.Default.WebVideoHello));
        Assert.True(doc.RootElement.GetProperty("play").GetBoolean());
        Assert.Equal(WebUiText.VideoWords.Saved!.Delete, doc.RootElement.GetProperty("words").GetProperty("saved").GetProperty("delete").GetString());
    }
}
