namespace NeonSidekick.YouTube;

/// <summary>
/// The saved videos' names (2026-10-07, the user's ask: an id alone tells nothing). A video saved by its id or link is looked up
/// first (<see cref="IYouTubeSearch.LookupAsync"/>: oEmbed, the key as a fallback), and one still untitled — saved before this, or
/// while the lookup failed — is looked up again when the list is shown (<see cref="FillMissingAsync"/>). A lookup that finds nothing
/// leaves the id; the window's report names it the first time it plays (<see cref="YouTubeResume"/>). A saved playlist (2026-11-08)
/// is looked up the same way through its own lookup (<see cref="IYouTubeSearch.LookupPlaylistAsync"/>), which the player never
/// fills in: it reports the video's title, not the list's. A lookup that finds nothing is stamped on its row and asked again only
/// after <see cref="RetryAfter"/>, not at every showing (2026-11-08, the review: a list whose lookup always fails was asked about on
/// every read of the list).
/// </summary>
public static class YouTubeTitles
{
    /// <summary>How many lookups run at once while the list fills in.</summary>
    public const int Parallel = 4;

    /// <summary>
    /// How long a title lookup that found nothing waits before it is asked again (2026-11-08): an hour, not a day, since a lookup answers
    /// nothing both when YouTube has no such list and when there is no connection.
    /// </summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromHours(1);

    /// <summary>The lookup over <paramref name="search"/>, with the key in force when it runs (null with none: oEmbed only).</summary>
    public static YouTubeLookup Lookup(IYouTubeSearch search, Func<Settings.AppSettingsData> effective)
    {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(effective);
        return (id, cancellationToken) => search.LookupAsync(id, YouTubeDataApi.Key(effective()), cancellationToken);
    }

    /// <summary>The playlist lookup over <paramref name="search"/> (2026-11-08), with the key in force when it runs.</summary>
    public static YouTubePlaylistLookup PlaylistLookup(IYouTubeSearch search, Func<Settings.AppSettingsData> effective)
    {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(effective);
        return (id, cancellationToken) => search.LookupPlaylistAsync(id, YouTubeDataApi.Key(effective()), cancellationToken);
    }

    /// <summary>
    /// Whether <paramref name="library"/> holds a video or playlist with no title yet whose lookup is due (<see cref="YouTubeLibrary.LookupDue"/>):
    /// the list then waits on <see cref="FillMissingAsync"/>.
    /// </summary>
    public static bool AnyMissing(YouTubeLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        return library.List().Any(v => v.Title is null && library.LookupDue(v));
    }

    /// <summary>
    /// Every saved video without a title whose lookup is due looked up (<see cref="Parallel"/> at a time) and filled in, with its channel
    /// and length where it lacks them; one not found is stamped (<see cref="YouTubeLibrary.MarkTried"/>) and left as it is until
    /// <see cref="RetryAfter"/>. A saved playlist (2026-11-08) through <paramref name="playlistLookup"/> (none: left untitled). A cancelled
    /// lookup throws and stamps nothing. The number filled in.
    /// </summary>
    public static async Task<int> FillMissingAsync(YouTubeLibrary library, YouTubeLookup? lookup, YouTubePlaylistLookup? playlistLookup, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(library);
        var missing = library.List().Where(v => v.Title is null && (v.Playlist ? playlistLookup is not null : lookup is not null) && library.LookupDue(v)).ToList();
        if (missing.Count == 0)
        {
            return 0;
        }

        int filled = 0;
        await System.Threading.Tasks.Parallel.ForEachAsync(missing, new ParallelOptions { MaxDegreeOfParallelism = Parallel, CancellationToken = cancellationToken }, async (row, token) =>
        {
            bool? done = row.Playlist
                ? await playlistLookup!(row.Id, token).ConfigureAwait(false) is { } list ? library.Update(row.Id, v => Fill(v, list) with { TitleTried = null }) : null
                : await lookup!(row.Id, token).ConfigureAwait(false) is { } info ? library.Update(row.Id, v => Fill(v, info) with { TitleTried = null }) : null;
            if (done is null)
            {
                library.MarkTried(row.Id);
            }
            else if (done.Value)
            {
                Interlocked.Increment(ref filled);
            }
        }).ConfigureAwait(false);
        return filled;
    }

    /// <summary>
    /// <paramref name="list"/> with what <paramref name="info"/> knows and it lacks (2026-11-08): the title and channel (trimmed), the count,
    /// and the list's thumbnail video (the owner's pick, <see cref="YouTubeSaved.Thumbnail"/>; never its entry's video, which it need not be).
    /// </summary>
    public static YouTubeSaved Fill(YouTubeSaved list, YouTubePlaylistInfo info)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(info);
        return list with
        {
            Title = list.Title ?? YouTubeLibrary.Blank(info.Title),
            Author = list.Author ?? YouTubeLibrary.Blank(info.Author),
            Count = list.Count > 0 ? list.Count : Math.Max(0, info.Count),
            Thumbnail = list.Thumbnail ?? (Viewer.VideoRequest.IsVideoId(info.ThumbnailVideo) ? info.ThumbnailVideo : null),
        };
    }

    /// <summary><paramref name="video"/> with what <paramref name="info"/> knows and it lacks, the title and channel trimmed.</summary>
    public static YouTubeSaved Fill(YouTubeSaved video, YouTubeVideoInfo info)
    {
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(info);
        return video with
        {
            Title = video.Title ?? YouTubeLibrary.Blank(info.Title),
            Author = video.Author ?? YouTubeLibrary.Blank(info.Author),
            Duration = video.Duration > 0 ? video.Duration : info.Duration,
        };
    }
}
