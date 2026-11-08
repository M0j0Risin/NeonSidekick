using NeonSidekick.App;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Viewer;
using NeonSidekick.YouTube;

namespace NeonSidekick.Tests;

/// <summary>
/// Saved playlists' tools (2026-11-08, the user's ask): <c>youtube_save</c>'s <c>playlist</c> (or a list's link given as the video),
/// <c>youtube_saved</c> naming an untitled list through the playlist lookup, and <c>youtube_play</c> resuming a saved list at its entry and
/// place unless an entry, a time or a link's <c>index=</c> is named.
/// </summary>
public sealed partial class YouTubeSavedToolsTests
{
    private const string List = "PLabcdefghijklmnopq";
    private const string Other = "PLzyxwvutsrqponmlkj";
    private const string Channel = "UCSMOQeBJ2RAnuFungnQOxLg";

    private readonly StubPlaylistLookup _lists = new();

    private YouTubeSaveTool SaveLists() => new(Library, _player, null, null, _lists.Lookup);

    [Fact]
    public async Task Save_APlaylist_ByLinkIdChannelOrTheOnePlaying_AndRemovesIt_ByNumberLinkOrCurrent()
    {
        _lists.Found[List] = new YouTubePlaylistInfo(List, "The list", "Its channel", Zoo);
        var save = SaveLists();

        Assert.Equal("Saved the playlist \"The list\"; it resumes at the entry and place it is left at.", await Call(save, ("action", "add"), ("playlist", "https://www.youtube.com/playlist?list=" + List)));
        Assert.Equal((true, "The list", "Its channel", (string?)null, (string?)Zoo), (Library().Find(List)!.Playlist, Library().Find(List)!.Title, Library().Find(List)!.Author, Library().Find(List)!.EntryVideo, Library().Find(List)!.Thumbnail));
        Assert.Equal("The playlist \"The list\" is saved already (not played yet).", await Call(save, ("action", "add"), ("playlist", List)));
        Assert.Equal([List], _lists.Asked);   // saved already: not asked again

        Assert.Equal("Saved playlist UUSMOQeBJ2RAnuFungnQOxLg; it resumes at the entry and place it is left at.", await Call(save, ("action", "add"), ("playlist", Channel)));   // its uploads, not found: untitled
        Assert.StartsWith("Saved playlist " + Other, await Call(save, ("action", "add"), ("video", "https://www.youtube.com/playlist?list=" + Other)), StringComparison.Ordinal);   // a list's link as the video

        Assert.Equal("Error: " + YouTubeText.NoPlaylistToSave, await Call(save, ("action", "add"), ("playlist", "current")));
        _player.Play(VideoRequest.Playlist("PLcurrentcurrentcur", 1));
        Assert.StartsWith("Saved playlist PLcurrentcurrentcur", await Call(save, ("action", "add"), ("playlist", "current")), StringComparison.Ordinal);
        var playing = Library().Find("PLcurrentcurrentcur")!;
        Assert.Equal((1, (string?)Zoo, 3), (playing.Entry, playing.EntryVideo, playing.Count));   // where the list is

        Assert.Equal("Removed the playlist \"The list\" from the saved videos.", await Call(save, ("action", "remove"), ("playlist", "1")));
        Assert.Equal("Removed playlist UUSMOQeBJ2RAnuFungnQOxLg from the saved videos.", await Call(save, ("action", "remove"), ("playlist", "https://www.youtube.com/channel/" + Channel)));
        Assert.Equal("Removed playlist PLcurrentcurrentcur from the saved videos.", await Call(save, ("action", "remove"), ("playlist", "current")));
        Assert.Equal([Other], Library().List().Select(v => v.Id));
    }

    [Fact]
    public async Task Save_RefusesAMix_BothNamed_AndNoPlaylist()
    {
        var save = SaveLists();
        Assert.Equal(YouTubeText.Mix("RDabcdefghijklmnopq"), await Call(save, ("action", "add"), ("playlist", "RDabcdefghijklmnopq")));
        Assert.Equal(YouTubeText.VideoOrPlaylist, await Call(save, ("action", "add"), ("video", Zoo), ("playlist", List)));
        Assert.Equal(YouTubeText.NotAPlaylist("lofi"), await Call(save, ("action", "add"), ("playlist", "lofi")));
        Assert.Equal(YouTubeText.NotSaved(List), await Call(save, ("action", "remove"), ("playlist", List)));
        Assert.Empty(Library().List());
    }

    /// <summary>Every place the group is built passes the playlist lookup: <c>youtube_saved</c> names an untitled list (2026-11-08).</summary>
    [Fact]
    public async Task Saved_NamesAnUntitledPlaylist_ThroughTheGroupsLookup_AndANullLookupLeavesIt()
    {
        Library().AddPlaylist(List);
        Assert.Equal(0, await YouTubeTitles.FillMissingAsync(Library(), _lookup.Lookup, null, CancellationToken.None));   // no playlist lookup: left
        Assert.Null(Library().Find(List)!.Title);

        var search = new PlaylistSearch(new YouTubePlaylistInfo(List, "The list", "Its channel", Zoo));
        var saved = ChatScreen.YouTubeTools(search, _player, () => _settings, _time, Library).Single(t => t.Name == YouTubeSavedTool.ToolName);
        string listed = await Call(saved);

        Assert.Contains("1. ▤ The list — Its channel · not played yet · playlist id " + List, listed);
        Assert.Equal(((string?)null, (string?)Zoo), (Library().Find(List)!.EntryVideo, Library().Find(List)!.Thumbnail));   // the list's thumbnail, shown only
    }

    [Fact]
    public async Task Play_ResumesASavedPlaylist_AtItsEntryAndPlace_UnlessAnEntryATimeOrAnIndexIsNamed()
    {
        var library = Library();
        library.AddPlaylist(List);
        library.Update(List, v => v with { Entry = 1, EntryVideo = Zoo, Position = 100, Duration = 300, Count = 3, LastPlayed = _time.GetUtcNow() });
        var play = new YouTubePlayTool(_player, () => _settings, _time, Library);

        string resumed = await Call(play, ("playlist", List));
        Assert.Equal((List, 1, (string?)Zoo, 97.0), Request(_player.Plays[^1]));
        Assert.StartsWith(YouTubeText.ResumedEntry(1, 97), resumed, StringComparison.Ordinal);
        Assert.Equal("Resumed the saved playlist at entry 2, 1:37 in, a moment before where it was left. ", YouTubeText.ResumedEntry(1, 97));
        Assert.Equal("Resumed the saved playlist at entry 3. ", YouTubeText.ResumedEntry(2, 0));

        await Call(play, ("playlist", List), ("entry", 3));
        Assert.Equal((List, 2, (string?)null, 0.0), Request(_player.Plays[^1]));   // an entry named wins
        await Call(play, ("playlist", List), ("start", "5"));
        Assert.Equal((List, 0, (string?)null, 5.0), Request(_player.Plays[^1]));   // so does a time
        await Call(play, ("playlist", "https://www.youtube.com/watch?list=" + List + "&index=1"));
        Assert.Equal((List, 0, (string?)null, 0.0), Request(_player.Plays[^1]));   // and a link's index=, even the first
        await Call(play, ("playlist", Other));
        Assert.Equal((Other, 0, (string?)null, 0.0), Request(_player.Plays[^1]));  // not saved: from its start
    }

    [Fact]
    public async Task Play_HandsASavedPlaylistsPlace_ToABrowser()
    {
        var library = Library();
        library.AddPlaylist(List);
        library.Update(List, v => v with { Entry = 1, EntryVideo = Zoo, Position = 100 });
        VideoRequest? sent = null;
        var play = new YouTubePlayTool(_player, () => _settings, _time, Library, route: request => { sent = request; return new VideoRouted(false, "Opened it in the browser."); });

        string answer = await Call(play, ("playlist", List));

        Assert.Equal(YouTubeText.ResumedEntry(1, 97) + "Opened it in the browser.", answer);
        Assert.Equal("https://www.youtube.com/watch?v=" + Zoo + "&t=97s&list=" + List, YouTubeIds.UrlFor(sent!));
    }

    // ── The review's fixes (2026-11-08) ────────────────────────────────────────

    private void Playing(string list, int? entry, string video = Zoo, double position = 40, double duration = 300, int? count = 3) =>
        _player.Report(new VideoSnapshot(VideoState.Playing, video, "A video", "A channel", position, duration, PlaylistId: list, PlaylistEntry: entry, PlaylistCount: count));

    /// <summary>A link that names a video removes only that video, a list's only that list: never the other one by a fallback.</summary>
    [Fact]
    public async Task Remove_ByAWatchLink_TakesNeitherTheWrongVideoNorTheWrongList()
    {
        Library().Add(Zoo);
        Library().AddPlaylist(List);
        var save = SaveLists();

        Assert.Equal(YouTubeText.NotSaved("https://www.youtube.com/watch?v=" + Zoo + "&list=" + Other),
            await Call(save, ("action", "remove"), ("playlist", "https://www.youtube.com/watch?v=" + Zoo + "&list=" + Other)));
        Assert.Equal(YouTubeText.NotSaved("https://www.youtube.com/watch?v=" + Bunny + "&list=" + List),
            await Call(save, ("action", "remove"), ("video", "https://www.youtube.com/watch?v=" + Bunny + "&list=" + List)));
        Assert.Equal([Zoo, List], Library().List().Select(v => v.Id));
    }

    [Fact]
    public async Task SaveCurrent_RefusesAMix_AnUnknownPrefix_AndAListNotSayingItsEntry_AndNeitherArgument()
    {
        var save = SaveLists();
        Playing("RDabcdefghijklmnopq", 0);
        Assert.Equal(YouTubeText.Mix("RDabcdefghijklmnopq"), await Call(save, ("action", "add"), ("playlist", "current")));
        Playing("XYabcdefghijklmnopq", 0);
        Assert.Equal(YouTubeText.NotAPlaylist("XYabcdefghijklmnopq"), await Call(save, ("action", "add"), ("playlist", "current")));
        Playing(List, null);
        Assert.Equal("Error: " + YouTubeText.PlaylistNotReady, await Call(save, ("action", "add"), ("playlist", "current")));
        Assert.Equal(YouTubeText.NameVideoOrPlaylist, await Call(save, ("action", "add")));
        Assert.Equal(YouTubeText.NameVideoOrPlaylist, await Call(save, ("action", "remove")));
        Assert.Empty(Library().List());
        Assert.Throws<ArgumentException>(() => Library().AddPlaylist("XYabcdefghijklmnopq"));
    }

    [Fact]
    public async Task Save_ByLink_KeepsItsIndexVideoAndTime()
    {
        var save = SaveLists();
        await Call(save, ("action", "add"), ("playlist", "https://www.youtube.com/watch?list=" + List + "&index=3"));
        await Call(save, ("action", "add"), ("playlist", "https://www.youtube.com/watch?v=" + Zoo + "&list=" + Other + "&t=90"));

        var indexed = Library().Find(List)!;
        var timed = Library().Find(Other)!;
        Assert.Equal((2, (string?)null, 0.0), (indexed.Entry, indexed.EntryVideo, indexed.Position));
        Assert.Equal((0, (string?)Zoo, 90.0), (timed.Entry, timed.EntryVideo, timed.Position));
    }

    [Fact]
    public async Task SaveCurrent_InAnEntrysLastSeconds_MovesOn_TheLastBackToTheFirst()
    {
        var save = SaveLists();
        Playing(List, 1, position: 295);
        await Call(save, ("action", "add"), ("playlist", "current"));
        Playing(Other, 2, position: 295);
        await Call(save, ("action", "add"), ("playlist", "current"));

        var next = Library().Find(List)!;
        var wrapped = Library().Find(Other)!;
        Assert.Equal((2, (string?)null, 0.0, false), (next.Entry, next.EntryVideo, next.Position, next.Watched));
        Assert.Equal((0, (string?)null, 0.0, true), (wrapped.Entry, wrapped.EntryVideo, wrapped.Position, wrapped.Watched));
    }

    /// <summary>The place is read after the lookup while the same list plays; closed (or another list) meanwhile, the first read's place.</summary>
    [Fact]
    public async Task SaveCurrent_ReadsThePlaceAfterTheLookup_OrKeepsTheFirstRead()
    {
        Playing(List, 1, position: 40);
        var moving = new YouTubeSaveTool(Library, _player, null, null, (id, _) =>
        {
            Playing(List, 2, video: Bunny, position: 7);
            return Task.FromResult<YouTubePlaylistInfo?>(null);
        });
        await Call(moving, ("action", "add"), ("playlist", "current"));
        var later = Library().Find(List)!;
        Assert.Equal((2, (string?)Bunny, 7.0), (later.Entry, later.EntryVideo, later.Position));

        Playing(Other, 1, position: 40);
        var closing = new YouTubeSaveTool(Library, _player, null, null, (id, _) =>
        {
            _player.Report(null);
            return Task.FromResult<YouTubePlaylistInfo?>(null);
        });
        await Call(closing, ("action", "add"), ("playlist", "current"));
        var first = Library().Find(Other)!;
        Assert.Equal((1, (string?)Zoo, 40.0), (first.Entry, first.EntryVideo, first.Position));
    }

    /// <summary>A browser opens what the link names: with no entry video there is no time in it, so only the entry is claimed.</summary>
    [Fact]
    public async Task Play_HandsASavedPlaylistWithNoEntryVideo_ToABrowser_ClaimingOnlyTheEntry()
    {
        var library = Library();
        library.AddPlaylist(List);
        library.Update(List, v => v with { Entry = 2, Position = 100 });
        VideoRequest? sent = null;
        var play = new YouTubePlayTool(_player, () => _settings, _time, Library, route: request => { sent = request; return new VideoRouted(false, "Opened it in the browser."); });

        Assert.Equal(YouTubeText.ResumedEntry(2, 0) + "Opened it in the browser.", await Call(play, ("playlist", List)));
        Assert.Equal("https://www.youtube.com/watch?list=" + List + "&index=3", YouTubeIds.UrlFor(sent!));

        library.Update(List, v => v with { Entry = 0 });
        Assert.Equal("Opened it in the browser.", await Call(play, ("playlist", List)));   // from the list's start: nothing to claim
    }

    [Fact]
    public void TheCaption_WithOnlyPlaylists_CountsThemAlone()
    {
        Assert.Equal("2 saved playlists; each resumes where it was left.", YouTubeText.SavedCaption(2, 2));
        Assert.Equal("1 saved playlist; each resumes where it was left.", YouTubeText.SavedCaption(1, 1));
        Assert.Equal("1 saved video and 1 playlist; each resumes where it was left.", YouTubeText.SavedCaption(2, 1));
    }

    private static (string?, int, string?, double) Request(VideoRequest request) => (request.PlaylistId, request.Entry, request.VideoId, request.Start);

    /// <summary>A playlist lookup that knows the lists in <see cref="Found"/> and remembers every id it was asked about.</summary>
    private sealed class StubPlaylistLookup
    {
        public Dictionary<string, YouTubePlaylistInfo> Found { get; } = [];

        public List<string> Asked { get; } = [];

        public Task<YouTubePlaylistInfo?> Lookup(string playlistId, CancellationToken cancellationToken)
        {
            lock (Asked)
            {
                Asked.Add(playlistId);
            }

            return Task.FromResult(Found.GetValueOrDefault(playlistId));
        }
    }

    /// <summary>A search that finds no videos and names one playlist.</summary>
    private sealed class PlaylistSearch(YouTubePlaylistInfo list) : IYouTubeSearch
    {
        public Task<YouTubeSearchOutcome> SearchAsync(string query, int max, YouTubeSearchFilters filters, string apiKey, CancellationToken cancellationToken) =>
            Task.FromResult(new YouTubeSearchOutcome([]));

        public Task<YouTubePlaylistOutcome> PlaylistAsync(string playlistId, int max, string apiKey, CancellationToken cancellationToken) =>
            Task.FromResult(YouTubePlaylistOutcome.Failed(YouTubeFailure.NotFound));

        public Task<YouTubeVideoInfo?> LookupAsync(string videoId, string? apiKey, CancellationToken cancellationToken) => Task.FromResult<YouTubeVideoInfo?>(null);

        public Task<YouTubePlaylistInfo?> LookupPlaylistAsync(string playlistId, string? apiKey, CancellationToken cancellationToken) =>
            Task.FromResult(playlistId == list.Id ? list : null);
    }
}
