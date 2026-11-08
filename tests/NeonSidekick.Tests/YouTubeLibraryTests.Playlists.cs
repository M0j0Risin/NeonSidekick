using NeonSidekick.Tests.Fakes;
using NeonSidekick.Viewer;
using NeonSidekick.YouTube;

namespace NeonSidekick.Tests;

/// <summary>
/// Saved playlists (2026-11-08, the user's ask): in the same list as the videos, marked <see cref="YouTubeSaved.Playlist"/>, resuming at
/// their entry and the place in it (<see cref="YouTubeLibrary.ResumePlaylist"/>), kept by <see cref="YouTubeResume"/> from the player's
/// reports: an entry seen to the end moves the list on, the last back to the first and the list watched.
/// </summary>
public sealed partial class YouTubeLibraryTests
{
    private const string List = "PLabcdefghijklmnopq";
    private const string Song = "dQw4w9WgXcQ";

    private void Entry(VideoState state, string video, int? entry, double position, int? count = 3, double duration = 300) =>
        _player.Report(new VideoSnapshot(state, video, "A video's title", "A video's channel", position, duration, PlaylistId: List, PlaylistEntry: entry, PlaylistCount: count));

    private YouTubeLibrary WatchingList()
    {
        var library = Library();
        library.AddPlaylist(List, "The list", "Its channel");
        _ = new YouTubeResume(_player, Library, _time);
        return library;
    }

    [Fact]
    public void AddPlaylist_SavesARow_AndASecondAddKeepsItsPlace_OnlyFillingBlanks()
    {
        var library = Library();
        library.Add(Bunny);
        Assert.Equal(YouTubeSaveOutcome.Added, library.AddPlaylist(List, count: 0, entryVideo: Zoo));
        var saved = library.Find(List)!;
        Assert.Equal((true, 0, Zoo, 0, (string?)null), (saved.Playlist, saved.Entry, saved.EntryVideo, saved.Count, saved.Title));
        Assert.Equal([Bunny, List], library.List().Select(v => v.Id));

        library.Update(List, v => v with { Entry = 2, EntryVideo = Song, Position = 40 });
        Assert.Equal(YouTubeSaveOutcome.AlreadySaved, library.AddPlaylist(List, "The list", "Its channel", 24, 0, Zoo));
        saved = library.Find(List)!;
        Assert.Equal(("The list", "Its channel", 24, 2, Song, 40.0), (saved.Title, saved.Author, saved.Count, saved.Entry, saved.EntryVideo, saved.Position));
    }

    [Fact]
    public void AVideosRow_IsWrittenAsBefore_WithNoneOfThePlaylistFields()
    {
        var library = Library();
        library.Add(Bunny, "Bunny");
        string text = File.ReadAllText(library.FilePath);
        Assert.DoesNotContain("Playlist", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Entry", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Count", text, StringComparison.Ordinal);

        library.AddPlaylist(List, count: 5);
        text = File.ReadAllText(library.FilePath);
        Assert.Contains("\"Playlist\": true", text, StringComparison.Ordinal);
        Assert.Contains("\"Count\": 5", text, StringComparison.Ordinal);
        Assert.Equal(2, Library().List().Count);
    }

    [Fact]
    public void Resolve_ReadsAPlaylistsId_LinkOrNumber_AWatchLinkMatchingItsVideoFirst()
    {
        var library = Library();
        library.Add(Bunny);
        library.AddPlaylist(List);

        Assert.Equal(List, library.Resolve(List)!.Id);
        Assert.Equal(List, library.Resolve("https://www.youtube.com/playlist?list=" + List)!.Id);
        Assert.Equal(List, library.Resolve("2")!.Id);
        Assert.Equal(Bunny, library.Resolve("https://www.youtube.com/watch?v=" + Bunny + "&list=" + List)!.Id);   // the video first
        Assert.Equal(List, library.Resolve("https://www.youtube.com/watch?v=" + Zoo + "&list=" + List)!.Id);     // then the list
        Assert.Null(library.Resolve("PLzzzzzzzzzzzzzzzzz"));
    }

    [Fact]
    public void ResumePlaylist_IsTheEntryItsVideoAndThePlaceLessThreeSeconds_HeldBelowTheCount()
    {
        var library = Library();
        Assert.Equal((0, (string?)null, 0.0), library.ResumePlaylist(List));   // not saved
        library.AddPlaylist(List);
        Assert.Equal((0, (string?)null, 0.0), library.ResumePlaylist(List));

        library.Update(List, v => v with { Entry = 3, EntryVideo = Zoo, Position = 100 });
        Assert.Equal((3, (string?)Zoo, 97.0), library.ResumePlaylist(List));

        library.Update(List, v => v with { Position = 4 });
        Assert.Equal((3, (string?)Zoo, 0.0), library.ResumePlaylist(List));   // under the minimum: the entry's start

        library.Update(List, v => v with { Count = 2 });
        Assert.Equal(1, library.ResumePlaylist(List).Entry);   // the list shrank: its last entry
        Assert.Equal(0, library.ResumeAt(List));                // a video's resume never reads a list's place
    }

    [Fact]
    public void Load_KeepsPlaylists_DropsMixesAndWrongIds_AndCleansAnEntryVideoOfTheWrongShape()
    {
        Directory.CreateDirectory(NeonSidekick.Settings.Profiles.ConfigsDirectory(_dir));
        string path = NeonSidekick.Settings.Profiles.ConfigPath(_dir, YouTubeLibrary.FileName);
        File.WriteAllText(path, "{ \"Videos\": [ "
            + "{ \"Id\": \"" + List + "\", \"Playlist\": true, \"Entry\": 2, \"EntryVideo\": \"not a video\", \"Count\": -4 }, "
            + "{ \"Id\": \"RDabcdefghijklmnopq\", \"Playlist\": true }, "
            + "{ \"Id\": \"" + Bunny + "\", \"Playlist\": true }, "
            + "{ \"Id\": \"" + List + "x\" }, "
            + "{ \"Id\": \"" + Zoo + "\", \"Entry\": -1 } ] }");

        var listed = Library().List();
        Assert.Equal([List, Zoo], listed.Select(v => v.Id));
        Assert.Equal((2, (string?)null, 0), (listed[0].Entry, listed[0].EntryVideo, listed[0].Count));
        Assert.Equal((2, (string?)null, 0.0), Library().ResumePlaylist(List));   // no throw on the play: the entry by its number
        Assert.Equal(0, listed[1].Entry);
    }

    [Fact]
    public void Import_CarriesPlaylists_AndDropsAMix()
    {
        var library = Library();
        YouTubeSaved[] theirs =
        [
            new() { Id = List, Playlist = true, Title = "The list", Entry = 4, EntryVideo = Zoo, Position = 30, Count = 9 },
            new() { Id = "RDabcdefghijklmnopq", Playlist = true },
            new() { Id = Bunny },
        ];

        Assert.Equal(new YouTubeImport(YouTubeImportOutcome.Done, 2, 0), library.Import(theirs, overwrite: false));
        Assert.Equal([List, Bunny], library.List().Select(v => v.Id));
        Assert.Equal(theirs[0], library.Find(List));
    }

    // ── The resume of a saved playlist ─────────────────────────────────────────

    [Fact]
    public void ASavedListsEntry_IsWritten_WithItsVideoAndPlace_NeverItsTitle()
    {
        var library = WatchingList();
        Entry(VideoState.Playing, Zoo, 1, 20);
        var saved = library.Find(List)!;
        Assert.Equal((1, Zoo, 3, 20.0, 300.0), (saved.Entry, saved.EntryVideo, saved.Count, saved.Position, saved.Duration));
        Assert.Equal(("The list", "Its channel"), (saved.Title, saved.Author));
        Assert.Equal(ManualTimeProvider.DefaultUtcNow, saved.LastPlayed);

        Entry(VideoState.Paused, Zoo, 1, 61, count: null);   // a count the player can't give keeps the saved one
        Assert.Equal((61.0, 3), (library.Find(List)!.Position, library.Find(List)!.Count));
        Assert.Equal((1, (string?)Zoo, 58.0), library.ResumePlaylist(List));
        Assert.Null(library.Find(Zoo));   // the entry's video is no saved video
    }

    [Fact]
    public void AReportWithNoEntry_WritesNothing()
    {
        var library = WatchingList();
        Entry(VideoState.Playing, Zoo, 2, 20);
        Entry(VideoState.Paused, Song, null, 5);
        Assert.Equal((2, Zoo, 20.0), (library.Find(List)!.Entry, library.Find(List)!.EntryVideo, library.Find(List)!.Position));
    }

    [Fact]
    public void AnEntrySeenToTheEnd_MovesOnFromItsStart_AndTheLastGoesBackToTheFirst_Watched()
    {
        var library = WatchingList();
        Entry(VideoState.Playing, Zoo, 0, 250);
        Entry(VideoState.Ended, Zoo, 0, 300);
        var saved = library.Find(List)!;
        Assert.Equal((1, (string?)null, 0.0, false), (saved.Entry, saved.EntryVideo, saved.Position, saved.Watched));   // the old id cleared: no replay of it

        Entry(VideoState.Playing, Song, 2, 10);
        Entry(VideoState.Paused, Song, 2, 295);   // a stop in the last ten seconds of the last entry
        saved = library.Find(List)!;
        Assert.Equal((0, (string?)null, 0.0, true), (saved.Entry, saved.EntryVideo, saved.Position, saved.Watched));
        Assert.Equal((0, (string?)null, 0.0), library.ResumePlaylist(List));
    }

    [Fact]
    public void TheThrottle_TellsTwoEntriesApart()
    {
        var library = WatchingList();
        Entry(VideoState.Playing, Zoo, 0, 0);
        _time.Advance(TimeSpan.FromSeconds(5));
        Entry(VideoState.Playing, Zoo, 0, 5);
        Assert.Equal(0, library.Find(List)!.Position);   // within the 15 s

        Entry(VideoState.Playing, Song, 1, 1);           // the next entry, inside the same 15 s
        Assert.Equal((1, Song, 1.0), (library.Find(List)!.Entry, library.Find(List)!.EntryVideo, library.Find(List)!.Position));
    }

    [Fact]
    public void AListNotSaved_IsLeftAlone()
    {
        var library = Library();
        library.Add(Zoo);
        _ = new YouTubeResume(_player, Library, _time);
        Entry(VideoState.Playing, Zoo, 0, 20);
        Entry(VideoState.Paused, Zoo, 0, 30);
        Assert.Equal([Zoo], library.List().Select(v => v.Id));
        Assert.Equal((0.0, (DateTimeOffset?)null), (library.Find(Zoo)!.Position, library.Find(Zoo)!.LastPlayed));
    }
}
