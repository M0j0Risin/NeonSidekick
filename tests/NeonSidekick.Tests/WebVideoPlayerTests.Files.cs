using System.Text.Json;
using NeonSidekick.Viewer;
using NeonSidekick.WebUi;
using NeonSidekick.YouTube;

namespace NeonSidekick.Tests;

/// <summary>
/// A video file on the YouTube tab (2026-11-12, the video-files plan): its load's address (the token, the name escaped), and its token served
/// exactly while its play is current — playing, held, or carried as the Resume offer — and dropped by a new play, a close, a forget, a hold
/// running out, or a profile switch's drop of a held one; a Resume making it current again; a file gone reading as gone.
/// </summary>
public partial class WebVideoPlayerTests : IDisposable
{
    private readonly string _files = Path.Combine(Path.GetTempPath(), "neon-webfile-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_files))
        {
            Directory.Delete(_files, recursive: true);
        }
    }

    private string FileClip(string name = "my clip #1%.mp4")
    {
        Directory.CreateDirectory(_files);
        string path = Path.Combine(_files, name);
        File.WriteAllBytes(path, [0, 1, 2, 3]);
        return path;
    }

    private static string FileState(string token, int state, double position = 0, double duration = 100) =>
        "{\"ev\":\"state\",\"file\":\"" + token + "\",\"title\":\"clip\",\"state\":" + state + ",\"position\":" + position.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + ",\"duration\":" + duration.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"volume\":100,\"muted\":false}";

    [Fact]
    public void APlayedFile_LoadsByItsAddress_AndItsTokenIsServed_UntilTheNextPlayOrAClose()
    {
        var player = Player();
        Assert.True(player.PlaysFiles);
        var a = Attach(player, Id('a'), Id('1'));
        string clip = FileClip();
        var request = VideoRequest.File(clip, start: 12);
        string token = request.FileToken!;

        player.Play(request);

        var load = JsonDocument.Parse(a.Drain().Single(e => e.Name == "load").Json).RootElement;
        Assert.Equal(token, load.GetProperty("file").GetString());
        Assert.Equal("/api/youtube/file/" + token + "/my%20clip%20%231%25.mp4", load.GetProperty("src").GetString());
        Assert.Equal("my clip #1%.mp4", load.GetProperty("name").GetString());
        Assert.Equal(12, load.GetProperty("start").GetDouble());
        Assert.Equal(new WebVideoFile(clip, "my clip #1%.mp4"), player.FileOf(token));
        Assert.Null(player.FileOf(VideoFile.NewToken()));
        Assert.Null(player.FileOf("../" + token));
        Assert.Equal("/api/youtube/file/" + token + "/a%3Fb.mp4", WebVideoPlayer.FileSrc(token, "a?b.mp4"));   // a name no Windows file has, kept whole

        Assert.Equal(WebOutcome.Ok, player.Report(Id('a'), FileState(token, 1, 13), null));
        Assert.Equal(VideoState.Playing, player.Snapshot?.State);
        Assert.Equal(token, player.Snapshot?.FileToken);
        Assert.Equal(VideoPlace.WebTab, player.Snapshot?.Place);
        Assert.Equal(WebOutcome.Ok, player.Report(Id('a'), State(2), null));   // the hidden YouTube player's report: dropped
        Assert.Equal(VideoState.Playing, player.Snapshot?.State);

        var next = VideoRequest.File(FileClip("other.webm"));
        player.Play(next);
        Assert.Null(player.FileOf(token));
        Assert.NotNull(player.FileOf(next.FileToken!));

        Assert.True(player.Close());
        Assert.Null(player.FileOf(next.FileToken!));
        Assert.Contains(a.Drain(), e => e.Name == "close");
    }

    [Fact]
    public void AHeldFile_IsServed_UntilItsHoldRunsOut()
    {
        var player = Player();
        var request = VideoRequest.File(FileClip());

        player.Play(request);

        Assert.NotNull(player.Snapshot?.Held);
        Assert.NotNull(player.FileOf(request.FileToken!));
        _time.Advance(YouTubeText.HoldFor);
        player.Tick();
        Assert.Null(player.Snapshot);
        Assert.Null(player.FileOf(request.FileToken!));
    }

    [Fact]
    public void PlayHere_CarriesTheFile_AsAnOffer_ItsResumeServesIt_AndAForgetDropsIt()
    {
        var player = Player();
        var a = Attach(player, Id('a'), Id('1'));
        var request = VideoRequest.File(FileClip());
        string token = request.FileToken!;
        player.Play(request);
        player.Report(Id('a'), FileState(token, 1, 60), null);

        var b = Attach(player, Id('b'), Id('2'), claimId: Id('c'));

        Assert.True(b.Attachment.Hello.Play);
        var offer = b.Attachment.Hello.Resume!;
        Assert.Equal(token, offer.File);
        Assert.Equal(WebVideoPlayer.FileSrc(token, "my clip #1%.mp4"), offer.Src);
        Assert.Equal("my clip #1%.mp4", offer.Name);
        Assert.Null(offer.VideoId);
        Assert.StartsWith("Resume \"my clip #1%.mp4\" at ", offer.Label, StringComparison.Ordinal);
        Assert.Null(player.Snapshot);
        Assert.NotNull(player.FileOf(token));   // the offer keeps it: the claimer's load fetches it

        Assert.Equal(WebOutcome.Ok, player.Report(Id('b'), "{\"ev\":\"resumed\"}", null));
        Assert.Equal(token, player.Snapshot?.FileToken);
        Assert.Equal(offer.Start, player.Snapshot?.Position);
        Assert.NotNull(player.FileOf(token));
        Assert.Equal(WebOutcome.Ok, player.Report(Id('b'), FileState(token, 1, offer.Start + 1), null));
        Assert.Equal(VideoState.Playing, player.Snapshot?.State);

        player.Forget();
        Assert.Null(player.FileOf(token));
        Assert.Contains(a.Drain(), e => e.Name == "taken");
    }

    /// <summary>An asked-for close drops an offered file's token, as a new play withdraws the offer and drops it too.</summary>
    [Fact]
    public void AnOfferedFile_IsDropped_ByAClose_OrANewPlay()
    {
        var player = Player();
        Attach(player, Id('a'), Id('1'));
        var request = VideoRequest.File(FileClip());
        player.Play(request);
        player.Report(Id('a'), FileState(request.FileToken!, 1, 60), null);
        Attach(player, Id('b'), Id('2'), claimId: Id('c'));
        Assert.NotNull(player.FileOf(request.FileToken!));

        player.Close();
        Assert.Null(player.Offer);
        Assert.Null(player.FileOf(request.FileToken!));

        var again = VideoRequest.File(FileClip("again.mp4"));
        player.Play(again);
        player.Report(Id('b'), FileState(again.FileToken!, 1, 60), null);
        Attach(player, Id('a'), Id('3'), claimId: Id('d'));
        Assert.NotNull(player.FileOf(again.FileToken!));
        player.Play(new VideoRequest(Bunny));
        Assert.Null(player.FileOf(again.FileToken!));
    }

    [Fact]
    public void AProfileSwitch_DropsAHeldFile_ButAPlayingOnePlaysOn()
    {
        var player = Player();
        var held = VideoRequest.File(FileClip());
        player.Play(held);
        player.ForgetProfile();
        Assert.Null(player.FileOf(held.FileToken!));

        Attach(player, Id('a'), Id('1'));
        var playing = VideoRequest.File(FileClip("b.mp4"));
        player.Play(playing);
        player.ForgetProfile();
        Assert.NotNull(player.FileOf(playing.FileToken!));
    }

    [Fact]
    public void AFileGoneUnderTheTab_ReadsAsGone()
    {
        var player = Player();
        Attach(player, Id('a'), Id('1'));
        string clip = FileClip();
        var request = VideoRequest.File(clip);
        string token = request.FileToken!;
        player.Play(request);
        File.Delete(clip);

        player.Report(Id('a'), "{\"ev\":\"error\",\"file\":\"" + token + "\",\"code\":4}", null);

        Assert.Equal(VideoFile.GoneCode, player.Snapshot?.Error);
        Assert.Equal(VideoFile.GoneCode, _seen[^1]?.Error);
    }

    [Fact]
    public void AFile_IsOfferedByTheSavedVideosRules()
    {
        var playing = new VideoSnapshot(VideoState.Playing, null, "clip.mp4", Position: 120, Duration: 600) { FileName = "clip.mp4", FileToken = VideoFile.NewToken() };

        Assert.NotNull(WebVideoPlayer.OfferFrom(playing)?.File);
        Assert.Null(WebVideoPlayer.OfferFrom(playing with { State = VideoState.Ended }));
        Assert.Null(WebVideoPlayer.OfferFrom(playing with { Error = 3 }));
        Assert.Null(WebVideoPlayer.OfferFrom(playing with { Position = 599 }));
    }
}
