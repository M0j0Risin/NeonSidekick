namespace NeonSidekick.YouTube;

/// <summary>
/// The saved videos' names (2026-10-07, the user's ask: an id alone tells nothing). A video saved by its id or link is looked up
/// first (<see cref="IYouTubeSearch.LookupAsync"/>: oEmbed, the key as a fallback), and one still untitled — saved before this, or
/// while the lookup failed — is looked up again when the list is shown (<see cref="FillMissingAsync"/>). A lookup that finds nothing
/// leaves the id; the window's report names it the first time it plays (<see cref="YouTubeResume"/>).
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

    /// <summary>Whether <paramref name="library"/> holds a video with no title yet: the list then waits on <see cref="FillMissingAsync"/>.</summary>
    public static bool AnyMissing(YouTubeLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        return library.List().Any(v => v.Title is null);
    }

    /// <summary>
    /// Every saved video without a title looked up (<see cref="Parallel"/> at a time) and filled in, with its channel and length
    /// where it lacks them; one not found stays as it is. The number filled in.
    /// </summary>
    public static async Task<int> FillMissingAsync(YouTubeLibrary library, YouTubeLookup? lookup, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(library);
        var missing = library.List().Where(v => v.Title is null).Select(v => v.Id).ToList();
        if (lookup is null || missing.Count == 0)
        {
            return 0;
        }

        int filled = 0;
        await System.Threading.Tasks.Parallel.ForEachAsync(missing, new ParallelOptions { MaxDegreeOfParallelism = Parallel, CancellationToken = cancellationToken }, async (id, token) =>
        {
            if (await lookup(id, token).ConfigureAwait(false) is { } info && library.Update(id, v => Fill(v, info)))
            {
                Interlocked.Increment(ref filled);
            }
        }).ConfigureAwait(false);
        return filled;
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
