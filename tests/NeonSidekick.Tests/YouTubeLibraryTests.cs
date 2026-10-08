using NeonSidekick.Tests.Fakes;
using NeonSidekick.Viewer;
using NeonSidekick.YouTube;

namespace NeonSidekick.Tests;

/// <summary>
/// The saved videos (2026-10-07, the user's ask): <see cref="YouTubeLibrary"/>, the profile's <c>youtube.json</c> — adding, removing,
/// naming one, where a play resumes — and <see cref="YouTubeResume"/>, which keeps each saved video's place from the window's
/// reports (<see cref="FakeVideoPlayer.Report"/>): on a pause, the end, a switch, a close, and every 15 s while playing.
/// </summary>
public sealed class YouTubeLibraryTests : IDisposable
{
    private const string Bunny = "aqz-KE-bpKQ";
    private const string Zoo = "jNQXAC9IVRw";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly FakeVideoPlayer _player = new();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private YouTubeLibrary Library() => new(_dir, _time);

    // ── The list ─────────────────────────────────────────────────────────────

    [Fact]
    public void Missing_IsEmpty_AndAnAddIsReadBackByAnotherInstance_InTheOrderSaved()
    {
        Assert.Empty(Library().List());
        Assert.False(File.Exists(Path.Combine(_dir, YouTubeLibrary.FileName)));

        Assert.Equal(YouTubeSaveOutcome.Added, Library().Add(Bunny, "Big Buck Bunny", "Blender", 635, 120));
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(YouTubeSaveOutcome.Added, Library().Add(Zoo));

        var videos = Library().List();
        Assert.Equal([Bunny, Zoo], videos.Select(v => v.Id));
        Assert.Equal(new YouTubeSaved { Id = Bunny, Title = "Big Buck Bunny", Author = "Blender", Duration = 635, Position = 120, Added = ManualTimeProvider.DefaultUtcNow }, videos[0]);
        Assert.Null(videos[1].Title);
        Assert.Null(videos[1].LastPlayed);
    }

    [Fact]
    public void AddingASavedVideo_KeepsItsPlaceAndPosition_AndOnlyFillsWhatItLacked()
    {
        var library = Library();
        library.Add(Zoo);
        library.Add(Bunny, position: 200);
        library.Add("dQw4w9WgXcQ");

        Assert.Equal(YouTubeSaveOutcome.AlreadySaved, library.Add(Bunny, "Big Buck Bunny", "Blender", 635, position: 10));

        var videos = Library().List();
        Assert.Equal([Zoo, Bunny, "dQw4w9WgXcQ"], videos.Select(v => v.Id));
        Assert.Equal(("Big Buck Bunny", "Blender", 635.0, 200.0), (videos[1].Title, videos[1].Author, videos[1].Duration, videos[1].Position));
        Assert.Equal(YouTubeSaveOutcome.AlreadySaved, library.Add(Bunny, "Another title"));
        Assert.Equal("Big Buck Bunny", library.Find(Bunny)!.Title);   // a known title stays
    }

    [Fact]
    public void Remove_TakesItOff_AndAVideoNotSavedIsFalse()
    {
        var library = Library();
        library.Add(Bunny);
        library.Add(Zoo);

        Assert.True(library.Remove(Bunny));
        Assert.False(library.Remove(Bunny));
        Assert.Equal([Zoo], Library().List().Select(v => v.Id));
    }

    /// <summary><c>/youtube saved --clear</c> and the pane's clear all (2026-10-08): every video goes; with none, nothing is written.</summary>
    [Fact]
    public void Clear_TakesThemAllOff_AndWithNoneWritesNothing()
    {
        Assert.Equal(0, Library().Clear());
        Assert.False(File.Exists(Path.Combine(_dir, YouTubeLibrary.FileName)));

        var library = Library();
        library.Add(Bunny);
        library.Add(Zoo);

        Assert.Equal(2, library.Clear());
        Assert.Empty(Library().List());
        Assert.Equal(0, library.Clear());
    }

    [Fact]
    public void Resolve_ReadsANumber_AnId_OrALink()
    {
        var library = Library();
        library.Add(Bunny);
        library.Add(Zoo);

        Assert.Equal(Zoo, library.Resolve("2")!.Id);
        Assert.Equal(Bunny, library.Resolve(" 1 ")!.Id);
        Assert.Equal(Zoo, library.Resolve(Zoo)!.Id);
        Assert.Equal(Bunny, library.Resolve("https://youtu.be/aqz-KE-bpKQ?t=42")!.Id);
        Assert.Null(library.Resolve("3"));
        Assert.Null(library.Resolve("0"));
        Assert.Null(library.Resolve("dQw4w9WgXcQ"));     // a video, but not saved
        Assert.Null(library.Resolve("lofi"));
    }

    [Fact]
    public void ResumeAt_IsThePlaceLessThreeSeconds_AndTheStartForNoPlaceOrNoneSaved()
    {
        var library = Library();
        library.Add(Bunny, position: 100);
        library.Add(Zoo, position: YouTubeLibrary.ResumeMinimum - 1);

        Assert.Equal(100 - YouTubeLibrary.ResumeRewind, library.ResumeAt(Bunny));
        Assert.Equal(0, library.ResumeAt(Zoo));                  // too near the start to be a place
        Assert.Equal(0, library.ResumeAt("dQw4w9WgXcQ"));        // not saved
        Assert.True(YouTubeLibrary.Finished(626, 635));
        Assert.False(YouTubeLibrary.Finished(624, 635));
        Assert.False(YouTubeLibrary.Finished(5, 0));             // no length known: never finished
    }

    [Fact]
    public void AnUnreadableFile_IsEmpty_AndTheNextSaveWritesOverIt_AndAnEditByHandIsSeen()
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, YouTubeLibrary.FileName);
        File.WriteAllText(path, "{ not json");
        var library = Library();

        Assert.Empty(library.List());
        Assert.Equal(YouTubeSaveOutcome.Added, library.Add(Bunny));
        Assert.Equal([Bunny], Library().List().Select(v => v.Id));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));

        // Written by something else (another instance, the user's editor): read as it is now, at once.
        File.WriteAllText(path, "{ \"Videos\": [ { \"Id\": \"" + Zoo + "\" }, { \"Id\": \"not-an-id\" } ] }");
        Assert.Equal([Zoo], library.List().Select(v => v.Id));   // an entry with no video id is dropped
    }

    // ── The resume ───────────────────────────────────────────────────────────

    private YouTubeLibrary Watching(params string[] saved)
    {
        var library = Library();
        foreach (string id in saved)
        {
            library.Add(id);
        }

        _ = new YouTubeResume(_player, Library, _time);
        return library;
    }

    private void Playing(string id, double position) =>
        _player.Report(new VideoSnapshot(VideoState.Playing, id, "Big Buck Bunny", "Blender", position, 635));

    [Fact]
    public void WhilePlaying_ThePlaceIsWritten_AtTheFirstReport_ThenEveryFifteenSeconds()
    {
        var library = Watching(Bunny);

        Playing(Bunny, 0);
        Assert.Equal(("Big Buck Bunny", "Blender", 635.0, 0.0), (library.Find(Bunny)!.Title, library.Find(Bunny)!.Author, library.Find(Bunny)!.Duration, library.Find(Bunny)!.Position));
        Assert.Equal(ManualTimeProvider.DefaultUtcNow, library.Find(Bunny)!.LastPlayed);

        _time.Advance(TimeSpan.FromSeconds(10));
        Playing(Bunny, 10);
        Assert.Equal(0, library.Find(Bunny)!.Position);         // within the 15 s: not yet

        _time.Advance(YouTubeResume.SaveEvery);
        Playing(Bunny, 25);
        Assert.Equal(25, library.Find(Bunny)!.Position);
    }

    [Fact]
    public void APause_ASwitch_AndTheWindowsClose_WriteWhereItWasLeft()
    {
        var library = Watching(Bunny, Zoo);
        Playing(Bunny, 0);
        _player.Report(new VideoSnapshot(VideoState.Paused, Bunny, Position: 42.5, Duration: 635));
        Assert.Equal(42.5, library.Find(Bunny)!.Position);

        Playing(Bunny, 50);
        Playing(Bunny, 61);                                      // within the 15 s: kept for the switch
        _player.Report(VideoSnapshot.Opening(Zoo));
        Assert.Equal(61, library.Find(Bunny)!.Position);         // switched away: where it was left

        _player.Report(new VideoSnapshot(VideoState.Playing, Zoo, "Me at the zoo", "jawed", 3, 19));
        _player.Report(new VideoSnapshot(VideoState.Playing, Zoo, "Me at the zoo", "jawed", 7, 19));
        _player.CloseByUser();
        Assert.Equal(7, library.Find(Zoo)!.Position);            // the window's last report, kept for its close
    }

    [Fact]
    public void TheEnd_OrAStopInTheLastTenSeconds_StartsItOver_AndMarksItWatched()
    {
        var library = Watching(Bunny, Zoo);
        Playing(Bunny, 600);
        _player.Report(new VideoSnapshot(VideoState.Ended, Bunny, Position: 635, Duration: 635));
        Assert.Equal((0.0, true), (library.Find(Bunny)!.Position, library.Find(Bunny)!.Watched));
        Assert.Equal(0, library.ResumeAt(Bunny));

        _player.Report(new VideoSnapshot(VideoState.Playing, Zoo, Position: 12, Duration: 19));
        _player.Report(new VideoSnapshot(VideoState.Paused, Zoo, Position: 12, Duration: 19));
        Assert.Equal((0.0, true), (library.Find(Zoo)!.Position, library.Find(Zoo)!.Watched));

        // Watched once, a rewatch's place is kept as any other.
        Playing(Bunny, 0);
        _player.Report(new VideoSnapshot(VideoState.Paused, Bunny, Position: 90, Duration: 635));
        Assert.Equal((90.0, true), (library.Find(Bunny)!.Position, library.Find(Bunny)!.Watched));
    }

    [Fact]
    public void AVideoNotSaved_IsLeftAlone_AsAreTheOpeningTheRefusalAndACuedOne()
    {
        var library = Watching(Bunny);
        _player.Report(new VideoSnapshot(VideoState.Playing, Zoo, Position: 3, Duration: 19));
        _player.Report(new VideoSnapshot(VideoState.Paused, Zoo, Position: 4, Duration: 19));
        Assert.Null(library.Find(Zoo));

        _player.Report(new VideoSnapshot(VideoState.Cued, Bunny, Position: 80, Duration: 635));
        _player.Report(new VideoSnapshot(VideoState.Paused, Bunny, Position: 90, Duration: 635, Error: 150));
        _player.CloseByUser();
        Assert.Equal((0.0, (DateTimeOffset?)null), (library.Find(Bunny)!.Position, library.Find(Bunny)!.LastPlayed));
    }
}
