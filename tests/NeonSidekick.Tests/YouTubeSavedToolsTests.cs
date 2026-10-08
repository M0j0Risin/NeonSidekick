using Microsoft.Extensions.AI;
using NeonSidekick.App;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Plans;
using NeonSidekick.Settings;
using NeonSidekick.Tests.Fakes;
using NeonSidekick.Viewer;
using NeonSidekick.YouTube;

namespace NeonSidekick.Tests;

/// <summary>
/// The saved videos' tools (2026-10-07, the user's ask): <c>youtube_saved</c> and <c>youtube_save</c> over the profile's list, and
/// <c>youtube_play</c> resuming a saved video where it was left unless a time is named.
/// </summary>
public sealed class YouTubeSavedToolsTests : IDisposable
{
    private const string Bunny = "aqz-KE-bpKQ";
    private const string Zoo = "jNQXAC9IVRw";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly FakeVideoPlayer _player = new();
    private readonly ManualTimeProvider _time = new();
    private readonly AppSettingsData _settings = new() { YouTubeTools = true };

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private YouTubeLibrary Library() => new(_dir, _time);

    private static async Task<string> Call(AIFunction tool, params (string Name, object? Value)[] pairs) =>
        (string)(await tool.InvokeAsync(new AIFunctionArguments(pairs.ToDictionary(p => p.Name, p => p.Value))))!;

    private YouTubeSaveTool Save(IVideoPlayer? player = null) => new(Library, player ?? _player);

    private readonly StubLookup _lookup = new();

    [Fact]
    public async Task Saved_ListsThem_Numbered_WithWhereEachWasLeft_AndTheirIds()
    {
        Assert.Equal(YouTubeText.NoneSavedForModel, await Call(new YouTubeSavedTool(Library)));

        var library = Library();
        library.Add(Bunny, "Big Buck Bunny", "Blender", 635, 754 - 600);
        library.Add(Zoo);
        library.Update(Zoo, v => v with { Title = "Me at the zoo", Author = "jawed", Duration = 19, Watched = true, LastPlayed = _time.GetUtcNow() });

        Assert.Equal(
            "Saved YouTube videos (2); youtube_play resumes each where it was left:\n" +
            "1. Big Buck Bunny — Blender · at 2:34 of 10:35 · id aqz-KE-bpKQ\n" +
            "2. Me at the zoo — jawed · watched · id jNQXAC9IVRw",
            await Call(new YouTubeSavedTool(Library)));
    }

    [Fact]
    public async Task Save_AddsByLink_OrTheCurrentVideoWithWhereItIs_AndRemovesByNumberIdOrCurrent()
    {
        Assert.Equal("Saved video jNQXAC9IVRw; it resumes where it is left.", await Call(Save(), ("action", "add"), ("video", "https://www.youtube.com/watch?v=jNQXAC9IVRw")));
        Assert.Equal("video jNQXAC9IVRw is saved already (not played yet).", await Call(Save(), ("action", "add"), ("video", Zoo)));

        _player.Play(new VideoRequest(Bunny, 75));
        Assert.Equal("Saved \"Big Buck Bunny 60fps 4K - Official Blender Foundation Short Film\"; it resumes where it is left.", await Call(Save(), ("action", "ADD"), ("video", "current")));
        Assert.Equal(75, Library().Find(Bunny)!.Position);

        Assert.Equal("Removed video jNQXAC9IVRw from the saved videos.", await Call(Save(), ("action", "remove"), ("video", "1")));
        Assert.Equal("Removed \"Big Buck Bunny 60fps 4K - Official Blender Foundation Short Film\" from the saved videos.", await Call(Save(), ("action", "remove"), ("video", "current")));
        Assert.Empty(Library().List());
    }

    [Fact]
    public async Task Save_Refuses_NoVideoOpen_NotAVideo_NotSaved_AndABadAction()
    {
        Assert.Equal("Error: " + YouTubeText.NothingToSave, await Call(Save(), ("action", "add"), ("video", "current")));
        Assert.Equal("Error: " + YouTubeText.NothingToSave, await Call(new YouTubeSaveTool(Library, null), ("action", "add"), ("video", "current")));   // headless: no window
        Assert.Equal(YouTubeText.NotAVideo("lofi"), await Call(Save(), ("action", "add"), ("video", "lofi")));
        Assert.Equal(YouTubeText.NotSaved("3"), await Call(Save(), ("action", "remove"), ("video", "3")));
        Assert.Equal(YouTubeText.NotASaveAction("keep"), await Call(Save(), ("action", "keep"), ("video", Zoo)));
        Assert.Empty(Library().List());
    }

    /// <summary>A video saved by its link is looked up for its title first (2026-10-07); one already saved, or saved from the window, is not.</summary>
    [Fact]
    public async Task Save_ByLink_LooksTheTitleUp_AndSavesItUntitledWhenNoneIsFound()
    {
        _lookup.Found[Zoo] = new YouTubeVideoInfo(Zoo, "Me at the zoo", "jawed", 19);
        var save = new YouTubeSaveTool(Library, _player, _lookup.Lookup);

        Assert.Equal("Saved \"Me at the zoo\"; it resumes where it is left.", await Call(save, ("action", "add"), ("video", "https://youtu.be/jNQXAC9IVRw")));
        Assert.Equal(("Me at the zoo", "jawed", 19.0), (Library().Find(Zoo)!.Title, Library().Find(Zoo)!.Author, Library().Find(Zoo)!.Duration));
        Assert.Equal("\"Me at the zoo\" is saved already (not played yet).", await Call(save, ("action", "add"), ("video", Zoo)));
        Assert.Equal([Zoo], _lookup.Asked);                                         // saved already: not asked again

        Assert.Equal("Saved video aqz-KE-bpKQ; it resumes where it is left.", await Call(save, ("action", "add"), ("video", Bunny)));   // nothing found
        _player.Play(new VideoRequest("dQw4w9WgXcQ"));
        await Call(save, ("action", "add"), ("video", "current"));
        Assert.Equal([Zoo, Bunny], _lookup.Asked);                                  // the window names its own
    }

    /// <summary>The list fills in the titles still missing before it answers (2026-10-07); one still not found keeps its id.</summary>
    /// <summary>
    /// The pane's rows in uniform columns (2026-10-08, the user's ask): title, channel and place each starting in one column, the widths
    /// the widest over the list, a title past <see cref="YouTubeText.SavedTitleWidth"/> cut with an ellipsis; no channel column with none.
    /// </summary>
    [Fact]
    public void SavedPaneRows_LineUpInColumns_CuttingALongTitle()
    {
        var bunny = new YouTubeSaved { Id = Bunny, Title = new string('b', 70), Author = "Blender", Duration = 635, Position = 341 };
        var zoo = new YouTubeSaved { Id = Zoo, Title = "Me at the zoo", Author = "jawed" };
        var untitled = new YouTubeSaved { Id = Zoo, Watched = true };

        var columns = YouTubeText.SavedColumns([bunny, zoo, untitled]);

        Assert.Equal((YouTubeText.SavedTitleWidth, 7), columns);
        Assert.Equal(new string('b', YouTubeText.SavedTitleWidth - 1) + "…  Blender  at 5:41 of 10:35", YouTubeText.SavedPaneRow(bunny, columns.Title, columns.Channel));
        Assert.Equal("Me at the zoo".PadRight(YouTubeText.SavedTitleWidth) + "  jawed    not played yet", YouTubeText.SavedPaneRow(zoo, columns.Title, columns.Channel));
        Assert.Equal(("video " + Zoo).PadRight(YouTubeText.SavedTitleWidth) + new string(' ', 2 + 7 + 2) + "watched", YouTubeText.SavedPaneRow(untitled, columns.Title, columns.Channel));
        Assert.Equal("Me at the zoo  not played yet", YouTubeText.SavedPaneRow(zoo with { Author = null }, 13, 0));
        Assert.Equal((0, 0), YouTubeText.SavedColumns([]));
    }

    [Fact]
    public async Task Saved_LooksUpTheTitlesStillMissing_First()
    {
        Library().Add(Zoo);
        Library().Add(Bunny);
        Library().Add("dQw4w9WgXcQ", "Never Gonna Give You Up", "Rick Astley");
        _lookup.Found[Zoo] = new YouTubeVideoInfo(Zoo, "Me at the zoo", "jawed", 19);

        string list = await Call(new YouTubeSavedTool(Library, _lookup.Lookup));

        Assert.Contains("1. Me at the zoo — jawed · not played yet · id jNQXAC9IVRw", list);
        Assert.Contains("2. video aqz-KE-bpKQ · not played yet · id aqz-KE-bpKQ", list);
        Assert.Equal([Bunny, Zoo], _lookup.Asked.Order());                           // the titled one is not asked about
        Assert.Equal(0, await YouTubeTitles.FillMissingAsync(Library(), null, CancellationToken.None));
        Assert.Equal(new YouTubeSaved { Id = Zoo, Title = "Me at the zoo", Author = "jawed", Duration = 19 }, YouTubeTitles.Fill(new YouTubeSaved { Id = Zoo }, _lookup.Found[Zoo]));
    }

    [Fact]
    public async Task Play_ResumesASavedVideo_AFewSecondsBack_UnlessATimeIsNamed_OrItWasWatched()
    {
        var library = Library();
        library.Add(Bunny, position: 154);
        var play = new YouTubePlayTool(_player, () => _settings, _time, Library);

        string resumed = await Call(play, ("video", Bunny));
        Assert.Equal(151, _player.Plays[^1].Start);
        Assert.StartsWith(YouTubeText.Resumed(151) + "Playing ", resumed, StringComparison.Ordinal);
        Assert.Equal("Resumed the saved video at 2:31, a moment before where it was left. ", YouTubeText.Resumed(151));

        await Call(play, ("video", Bunny), ("start", "0:10"));
        Assert.Equal(10, _player.Plays[^1].Start);                                  // a time named wins
        await Call(play, ("video", "https://youtu.be/aqz-KE-bpKQ?t=42"));
        Assert.Equal(42, _player.Plays[^1].Start);                                  // so does a link's own

        library.Update(Bunny, v => v with { Position = 0, Watched = true });
        Assert.DoesNotContain("Resumed", await Call(play, ("video", Bunny)), StringComparison.Ordinal);
        Assert.Equal(0, _player.Plays[^1].Start);                                   // watched: from the start
        await Call(play, ("video", Zoo));
        Assert.Equal(0, _player.Plays[^1].Start);                                   // not saved
    }

    [Fact]
    public void TheGroup_AddsTheTwo_WithTheLibrary_HeadlessToo_AndPlanModeKeepsTheList()
    {
        var search = new NoSearch();
        var screen = ChatScreen.YouTubeTools(search, _player, () => _settings, _time, Library);
        var headless = ChatScreen.YouTubeTools(search, player: null, () => _settings, _time, Library);

        Assert.Equal(["youtube_search", "youtube_play", "youtube_control", "youtube_status", "youtube_saved", "youtube_save"], screen.Select(t => t.Name));
        Assert.Equal(["youtube_search", "youtube_saved", "youtube_save"], headless.Select(t => t.Name));
        Assert.Equal(ChatScreen.YouTubeToolNames.Order(), screen.Select(t => t.Name).Order());
        Assert.True(ChatScreen.YouTubeOffered(_settings, headless));                // no key, no window: the saved videos still
        Assert.All(screen, t => Assert.True(PlanTools.ReadOnly.Contains(t.Name) ^ PlanTools.Mutating.Contains(t.Name), t.Name));
        Assert.Contains("youtube_saved", PlanTools.ReadOnly);
        Assert.Contains("youtube_save", PlanTools.Mutating);
    }

    private sealed class NoSearch : IYouTubeSearch
    {
        public Task<YouTubeSearchOutcome> SearchAsync(string query, int max, string apiKey, CancellationToken cancellationToken) =>
            Task.FromResult(new YouTubeSearchOutcome([]));

        public Task<YouTubeVideoInfo?> LookupAsync(string videoId, string? apiKey, CancellationToken cancellationToken) => Task.FromResult<YouTubeVideoInfo?>(null);
    }

    /// <summary>A lookup that knows the videos in <see cref="Found"/> and remembers every id it was asked about.</summary>
    private sealed class StubLookup
    {
        public Dictionary<string, YouTubeVideoInfo> Found { get; } = [];

        public List<string> Asked { get; } = [];

        public Task<YouTubeVideoInfo?> Lookup(string videoId, CancellationToken cancellationToken)
        {
            lock (Asked)
            {
                Asked.Add(videoId);
            }

            return Task.FromResult(Found.GetValueOrDefault(videoId));
        }
    }
}
