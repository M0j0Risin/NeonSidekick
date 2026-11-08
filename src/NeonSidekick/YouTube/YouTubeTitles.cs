namespace NeonSidekick.YouTube;

/// <summary>
/// The saved videos' names (2026-10-07, the user's ask: an id alone tells nothing). A video saved by its id or link is looked up
/// first (<see cref="IYouTubeSearch.LookupAsync"/>: oEmbed, the key as a fallback), and one still untitled — saved before this, or
/// while the lookup failed — is looked up again when the list is shown (<see cref="FillMissingAsync"/>). A lookup that finds nothing
/// leaves the id; the window's report names it the first time it plays (<see cref="YouTubeResume"/>). A saved playlist (2026-11-08)
/// is looked up the same way through its own lookup (<see cref="IYouTubeSearch.LookupPlaylistAsync"/>), which the player never
/// fills in: it reports the video's title, not the list's.
/// </summary>
public static class YouTubeTitles
{
    /// <summary>How many lookups run at once while the list fills in.</summary>
    public const int Parallel = 4;

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

    /// <summary>Whether <paramref name="library"/> holds a video or playlist with no title yet: the list then waits on <see cref="FillMissingAsync"/>.</summary>
    public static bool AnyMissing(YouTubeLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        return library.List().Any(v => v.Title is null);
    }

    /// <summary>
    /// Every saved video without a title looked up (<see cref="Parallel"/> at a time) and filled in, with its channel and length
    /// where it lacks them; one not found stays as it is. A saved playlist (2026-11-08) through <paramref name="playlistLookup"/>
    /// (none: left untitled). The number filled in.
    /// </summary>
    public static async Task<int> FillMissingAsync(YouTubeLibrary library, YouTubeLookup? lookup, CancellationToken cancellationToken, YouTubePlaylistLookup? playlistLookup = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        var missing = library.List().Where(v => v.Title is null && (v.Playlist ? playlistLookup is not null : lookup is not null)).ToList();
        if (missing.Count == 0)
        {
            return 0;
        }

        int filled = 0;
        await System.Threading.Tasks.Parallel.ForEachAsync(missing, new ParallelOptions { MaxDegreeOfParallelism = Parallel, CancellationToken = cancellationToken }, async (row, token) =>
        {
            bool done = row.Playlist
                ? await playlistLookup!(row.Id, token).ConfigureAwait(false) is { } list && library.Update(row.Id, v => Fill(v, list))
                : await lookup!(row.Id, token).ConfigureAwait(false) is { } info && library.Update(row.Id, v => Fill(v, info));
            if (done)
            {
                Interlocked.Increment(ref filled);
            }
        }).ConfigureAwait(false);
        return filled;
    }

    /// <summary>
    /// <paramref name="list"/> with what <paramref name="info"/> knows and it lacks (2026-11-08): the title, channel and count, and the
    /// first video as its entry's while it sits at the start with none.
    /// </summary>
    public static YouTubeSaved Fill(YouTubeSaved list, YouTubePlaylistInfo info)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(info);
        return list with
        {
            Title = list.Title ?? (string.IsNullOrWhiteSpace(info.Title) ? null : info.Title),
            Author = list.Author ?? (string.IsNullOrWhiteSpace(info.Author) ? null : info.Author),
            Count = list.Count > 0 ? list.Count : Math.Max(0, info.Count),
            EntryVideo = list.EntryVideo ?? (list.Entry == 0 && Viewer.VideoRequest.IsVideoId(info.FirstVideo) ? info.FirstVideo : null),
        };
    }

    /// <summary><paramref name="video"/> with what <paramref name="info"/> knows and it lacks.</summary>
    public static YouTubeSaved Fill(YouTubeSaved video, YouTubeVideoInfo info)
    {
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(info);
        return video with
        {
            Title = video.Title ?? (string.IsNullOrWhiteSpace(info.Title) ? null : info.Title),
            Author = video.Author ?? (string.IsNullOrWhiteSpace(info.Author) ? null : info.Author),
            Duration = video.Duration > 0 ? video.Duration : info.Duration,
        };
    }
}
