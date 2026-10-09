using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using NeonSidekick.Diagnostics;
using NeonSidekick.Web;

namespace NeonSidekick.YouTube;

/// <summary>Whether a hit is live: an ordinary video, a broadcast on air now, or one announced.</summary>
public enum YouTubeLive
{
    None,
    Live,
    Upcoming,
}

/// <summary>What a search looks for, and what a hit is (2026-10-08): a video, a playlist, or a channel.</summary>
public enum YouTubeKind
{
    Video,
    Playlist,
    Channel,
}

/// <summary>
/// One video a search found: its id, title and channel, when it was published, how long it runs (null for a live broadcast,
/// which has no length, or when the details call failed), how often it was watched (null when the owner hides it), and
/// whether it is live. Or a playlist or a channel (2026-10-08, <see cref="Kind"/>): its id in <see cref="Id"/>, how many videos
/// it holds (<see cref="Count"/>) and, for a channel, its subscribers (null when hidden or unknown).
/// </summary>
public sealed record YouTubeHit(string Id, string Title, string Channel, DateTimeOffset? Published, TimeSpan? Duration, long? Views, YouTubeLive Live = YouTubeLive.None, YouTubeKind Kind = YouTubeKind.Video, long? Count = null, long? Subscribers = null);

/// <summary>Why a search found nothing to show.</summary>
public enum YouTubeFailure
{
    None,

    /// <summary>No key set: nothing was asked.</summary>
    NoKey,

    /// <summary>The project's daily quota is spent (403 <c>quotaExceeded</c>).</summary>
    Quota,

    /// <summary>The key is not a key, or has expired (400 <c>keyInvalid</c>, <c>API_KEY_INVALID</c>).</summary>
    KeyInvalid,

    /// <summary>YouTube Data API v3 is not enabled on the key's project (403 <c>accessNotConfigured</c>, <c>SERVICE_DISABLED</c>).</summary>
    NotEnabled,

    /// <summary>The key's restrictions refuse this caller (an HTTP referrer, IP or app restriction, <c>forbidden</c>).</summary>
    KeyRestricted,

    /// <summary>The network mode keeps the internet off limits.</summary>
    Refused,

    /// <summary>No answer within <see cref="YouTubeDataApi.Timeout"/>.</summary>
    Timeout,

    /// <summary>A filter YouTube refused (2026-10-08: 400 <c>invalidRegionCode</c>, <c>invalidRelevanceLanguage</c>, <c>invalidParameter</c>…).</summary>
    BadFilter,

    /// <summary>No such public playlist (2026-10-08: 404 <c>playlistNotFound</c>, or none listed: private, a mix, removed).</summary>
    NotFound,

    /// <summary>Anything else: a status and YouTube's own message.</summary>
    Failed,
}

/// <summary>A search's answer: the hits, or why there are none (<see cref="Failure"/>, with YouTube's own words in <see cref="Detail"/>).</summary>
public sealed record YouTubeSearchOutcome(IReadOnlyList<YouTubeHit> Hits, YouTubeFailure Failure = YouTubeFailure.None, string Detail = "")
{
    public bool Ok => Failure == YouTubeFailure.None;

    public static YouTubeSearchOutcome Failed(YouTubeFailure failure, string detail = "") => new([], failure, detail);
}

/// <summary>What a lookup found of one video (2026-10-07): its title and channel, and its length in seconds when known (0 otherwise).</summary>
public sealed record YouTubeVideoInfo(string Id, string Title, string Author, double Duration = 0);

/// <summary>A video's title looked up by its id (2026-10-07): <see cref="IYouTubeSearch.LookupAsync"/> with the key in force; null when none was found.</summary>
public delegate Task<YouTubeVideoInfo?> YouTubeLookup(string videoId, CancellationToken cancellationToken);

/// <summary>A public playlist (2026-10-08): its id, title, channel and how many entries YouTube counts in it.</summary>
public sealed record YouTubePlaylist(string Id, string Title, string Channel, int Count);

/// <summary>
/// One entry of a playlist (2026-10-08): its place (0-based, as the player counts), the video, its title and channel. A deleted
/// or private video stays in a list as YouTube's "Deleted video" or "Private video" with no channel: <see cref="Available"/> false.
/// </summary>
public sealed record YouTubePlaylistEntry(int Position, string VideoId, string Title, string Channel, bool Available = true);

/// <summary>A playlist's listing (2026-10-08): the playlist and its first entries, or why there is none (<see cref="YouTubeSearchOutcome"/>'s failures).</summary>
public sealed record YouTubePlaylistOutcome(YouTubePlaylist? Playlist, IReadOnlyList<YouTubePlaylistEntry> Entries, YouTubeFailure Failure = YouTubeFailure.None, string Detail = "")
{
    public bool Ok => Failure == YouTubeFailure.None && Playlist is not null;

    public static YouTubePlaylistOutcome Failed(YouTubeFailure failure, string detail = "") => new(null, [], failure, detail);
}

/// <summary>The search seam (2026-10-05): <see cref="YouTubeDataApi"/>, the official API with the user's key; a stub in tests.</summary>
public interface IYouTubeSearch
{
    /// <summary>
    /// At most <paramref name="max"/> hits for <paramref name="query"/> under <paramref name="filters"/> (2026-10-08: videos, playlists
    /// or channels, filtered), asked with <paramref name="apiKey"/>.
    /// </summary>
    Task<YouTubeSearchOutcome> SearchAsync(string query, int max, YouTubeSearchFilters filters, string apiKey, CancellationToken cancellationToken);

    /// <summary>
    /// A public playlist and its first <paramref name="max"/> entries (2026-10-08), asked with <paramref name="apiKey"/>: one call for
    /// the playlist, one per 50 entries.
    /// </summary>
    Task<YouTubePlaylistOutcome> PlaylistAsync(string playlistId, int max, string apiKey, CancellationToken cancellationToken);

    /// <summary>
    /// One video's title and channel (2026-10-07, a saved video's name): YouTube's oEmbed first, which needs no key, then — when that
    /// fails and <paramref name="apiKey"/> is set — the Data API's <c>videos.list</c>. Null when neither found it; never throws.
    /// </summary>
    Task<YouTubeVideoInfo?> LookupAsync(string videoId, string? apiKey, CancellationToken cancellationToken);
}

/// <summary>
/// The YouTube Data API v3 (2026-10-05, the user's call: the official API with a key, no keyless scraping): <c>search.list</c>
/// for the videos (<c>type=video</c>, and <c>videoEmbeddable=true</c> so every hit plays in the video window — YouTube's player
/// answers an embed it refuses, a private, age-restricted or missing video alike, with error 150), then <c>videos.list</c> for
/// their lengths and view counts. A search costs 100 units of the project's 10,000 a day, the details one more: about a
/// hundred searches a day.
///
/// <para>The key travels in the <c>X-Goog-Api-Key</c> header, which Google's APIs read as the <c>key</c> parameter, so it is in
/// no URL the log might show. Through the web tools' client (<see cref="WebAccess.Http"/>), not exempt from
/// <see cref="LanPolicy"/>: under the network mode <c>local_area_network</c> the internet, this API with it, is off limits. A
/// failed details call keeps the hits without their lengths and counts. JSON read by hand over <see cref="JsonDocument"/>;
/// the titles come HTML-escaped (<c>&amp;amp;</c>, <c>&amp;#39;</c>) and are decoded.</para>
///
/// <para>A saved video's title (2026-10-07, the user's call: oEmbed, the key as a fallback) is <see cref="LookupAsync"/>'s: YouTube's
/// public oEmbed answer (<see cref="OEmbedUrl"/>, no key and no quota, the title and channel only), and only when that fails and a key
/// is set, <c>videos.list</c> with the snippet (one quota unit, the length too). The same client and network rules as the search.</para>
/// </summary>
public sealed partial class YouTubeDataApi : IYouTubeSearch
{
    public static readonly Uri Endpoint = new("https://www.googleapis.com/youtube/v3/");
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>A title lookup's own budget (2026-10-07): a save waits on it, so it is short; past it the video is saved untitled.</summary>
    public static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(8);

    /// <summary>The most one call returns: <c>search.list</c>'s own limit is 50; a tool shows fewer.</summary>
    public const int MaxResults = 50;

    /// <summary>The most entries a playlist listing reads (2026-10-08): four pages of 50, four units.</summary>
    public const int MaxPlaylistEntries = 200;

    /// <summary>The entries a playlist listing reads unless told otherwise: one page.</summary>
    public const int DefaultPlaylistEntries = 50;

    private const string Category = "YouTube";
    private readonly HttpClient _http;

    public YouTubeDataApi(HttpClient http) => _http = http ?? throw new ArgumentNullException(nameof(http));

    /// <summary>The key to search with under <paramref name="effective"/>, decrypted at use (<c>OpenAIApi.Key</c>'s way); null with none set.</summary>
    public static string? Key(Settings.AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return Settings.SettingsSecrets.Reveal(effective.YouTubeApiKey) is { Length: > 0 } key && !string.IsNullOrWhiteSpace(key) ? key.Trim() : null;
    }

    /// <summary>The search's count under <paramref name="effective"/>: the setting, held to its range (2026-10-08, one place for the tool, <c>/youtube</c> and headless).</summary>
    public static int DefaultCount(Settings.AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return Math.Clamp(effective.YouTubeSearchMaxResults, Settings.AppSettingsData.MinYouTubeSearchMaxResults, Settings.AppSettingsData.MaxYouTubeSearchMaxResults);
    }

    /// <summary>The <c>search.list</c> address for <paramref name="query"/> (no key in it). Pinned.</summary>
    public static Uri SearchUrl(string query, int max) => SearchUrl(query, max, YouTubeSearchFilters.None);

    /// <summary>
    /// The <c>search.list</c> address for <paramref name="query"/> under <paramref name="filters"/> (2026-10-08): <c>type</c> the kind,
    /// a video search still only embeddable ones; each filter added only when set (the plain address unchanged), the video-only ones
    /// (length, live, captions) only on a video search, and safe search only when it is not YouTube's own moderate. Pinned.
    /// </summary>
    public static Uri SearchUrl(string query, int max, YouTubeSearchFilters filters)
    {
        ArgumentNullException.ThrowIfNull(filters);
        bool video = filters.Kind == YouTubeKind.Video;
        var url = new System.Text.StringBuilder("search?part=snippet&type=");
        url.Append(filters.Kind switch { YouTubeKind.Playlist => "playlist", YouTubeKind.Channel => "channel", _ => "video" });
        if (video)
        {
            url.Append("&videoEmbeddable=true");
        }

        url.Append("&maxResults=").Append(Math.Clamp(max, 1, MaxResults).ToString(CultureInfo.InvariantCulture)).Append("&q=").Append(Uri.EscapeDataString(query.Trim()));
        if (YouTubeSearchFilters.ApiOrder(filters.Order) is { } order && order != "relevance")
        {
            url.Append("&order=").Append(order);
        }

        if (filters.Since is { } since)
        {
            url.Append("&publishedAfter=").Append(Uri.EscapeDataString(since.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)));
        }

        if (filters.Region.Length > 0)
        {
            url.Append("&regionCode=").Append(filters.Region);
        }

        if (filters.Language.Length > 0)
        {
            url.Append("&relevanceLanguage=").Append(filters.Language);
        }

        if (video && filters.Length is "short" or "medium" or "long")
        {
            url.Append("&videoDuration=").Append(filters.Length);
        }

        if (video && filters.Live)
        {
            url.Append("&eventType=live");
        }

        if (video && filters.Captions)
        {
            url.Append("&videoCaption=closedCaption");
        }

        if (filters.SafeSearch is "none" or "strict")
        {
            url.Append("&safeSearch=").Append(filters.SafeSearch);
        }

        return new Uri(Endpoint, url.ToString());
    }

    /// <summary>The <c>videos.list</c> address for <paramref name="ids"/>. Pinned.</summary>
    public static Uri VideosUrl(IEnumerable<string> ids) =>
        new(Endpoint, "videos?part=contentDetails,statistics&id=" + string.Join(",", ids));

    /// <summary>The <c>playlists.list</c> address for <paramref name="ids"/>' titles, channels and counts (2026-10-08). Pinned.</summary>
    public static Uri PlaylistsUrl(IEnumerable<string> ids) =>
        new(Endpoint, "playlists?part=snippet,contentDetails&id=" + string.Join(",", ids.Select(Uri.EscapeDataString)));

    /// <summary>The <c>channels.list</c> address for <paramref name="ids"/>' subscriber and video counts (2026-10-08). Pinned.</summary>
    public static Uri ChannelsUrl(IEnumerable<string> ids) =>
        new(Endpoint, "channels?part=statistics&id=" + string.Join(",", ids.Select(Uri.EscapeDataString)));

    /// <summary>The <c>playlistItems.list</c> address for a page of <paramref name="playlistId"/>'s entries (2026-10-08). Pinned.</summary>
    public static Uri PlaylistItemsUrl(string playlistId, int max, string? pageToken = null) =>
        new(Endpoint, "playlistItems?part=snippet&maxResults=" + Math.Clamp(max, 1, MaxResults).ToString(CultureInfo.InvariantCulture)
            + "&playlistId=" + Uri.EscapeDataString(playlistId) + (string.IsNullOrEmpty(pageToken) ? "" : "&pageToken=" + Uri.EscapeDataString(pageToken)));

    /// <summary>YouTube's oEmbed address for <paramref name="videoId"/>'s watch page (2026-10-07): no key. Pinned.</summary>
    public static Uri OEmbedUrl(string videoId) =>
        new("https://www.youtube.com/oembed?format=json&url=" + Uri.EscapeDataString(YouTubeIds.WatchUrl(videoId)));

    /// <summary>The <c>videos.list</c> address for one video's snippet and length (2026-10-07), the lookup's fallback. Pinned.</summary>
    public static Uri VideoInfoUrl(string videoId) =>
        new(Endpoint, "videos?part=snippet,contentDetails&id=" + Uri.EscapeDataString(videoId));

    public async Task<YouTubeVideoInfo?> LookupAsync(string videoId, string? apiKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(videoId);
        var (body, failure) = await GetAsync(OEmbedUrl(videoId), null, cancellationToken, LookupTimeout).ConfigureAwait(false);
        if (failure is null && ParseOEmbed(videoId, body!) is { } found)
        {
            return found;
        }

        string why = failure is null ? "no title in the answer" : $"{failure.Failure} {failure.Detail}";
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            DiagnosticLog.Info(Category, $"oEmbed for {videoId}: {why}; no key to ask the Data API.");
            return null;
        }

        (body, var keyed) = await GetAsync(VideoInfoUrl(videoId), apiKey, cancellationToken, LookupTimeout).ConfigureAwait(false);
        if (keyed is null && ParseVideoInfo(videoId, body!) is { } listed)
        {
            return listed;
        }

        DiagnosticLog.Info(Category, $"title of {videoId}: oEmbed {why}; videos.list {(keyed is null ? "found no such video" : $"{keyed.Failure} {keyed.Detail}")}.");
        return null;
    }

    /// <summary>An oEmbed answer's <c>title</c> and <c>author_name</c>; null without a title. Pure; pinned.</summary>
    public static YouTubeVideoInfo? ParseOEmbed(string videoId, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && Text(doc.RootElement, "title") is { Length: > 0 } title
                ? new YouTubeVideoInfo(videoId, title, Text(doc.RootElement, "author_name"))
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary><c>videos.list</c>'s one item: the snippet's title and channel (HTML-decoded, as the search's) and the length; null without it. Pure; pinned.</summary>
    public static YouTubeVideoInfo? ParseVideoInfo(string videoId, string json)
    {
        foreach (var item in Items(json))
        {
            if (Text(item, "id") == videoId && item.TryGetProperty("snippet", out var snippet) && snippet.ValueKind == JsonValueKind.Object
                && Text(snippet, "title") is { Length: > 0 } title)
            {
                double length = item.TryGetProperty("contentDetails", out var content) && content.ValueKind == JsonValueKind.Object
                    && ParseDuration(Text(content, "duration")) is { } duration ? duration.TotalSeconds : 0;
                return new YouTubeVideoInfo(videoId, WebUtility.HtmlDecode(title), WebUtility.HtmlDecode(Text(snippet, "channelTitle")), length);
            }
        }

        return null;
    }

    public async Task<YouTubeSearchOutcome> SearchAsync(string query, int max, YouTubeSearchFilters filters, string apiKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(filters);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return YouTubeSearchOutcome.Failed(YouTubeFailure.NoKey);
        }

        var (body, failure) = await GetAsync(SearchUrl(query, max, filters), apiKey, cancellationToken).ConfigureAwait(false);
        if (failure is { } refused)
        {
            DiagnosticLog.Info(Category, $"search \"{query}\": {refused.Failure} {refused.Detail}");
            return refused;
        }

        // The details call follows the kind (2026-10-08): a video's length and views, a playlist's count, a channel's subscribers
        // and videos; one unit whichever.
        var hits = ParseSearch(body!, filters.Kind);
        if (hits.Count > 0)
        {
            var ids = hits.Select(h => h.Id);
            var detailsUrl = filters.Kind switch
            {
                YouTubeKind.Playlist => PlaylistsUrl(ids),
                YouTubeKind.Channel => ChannelsUrl(ids),
                _ => VideosUrl(ids),
            };
            var (details, detailFailure) = await GetAsync(detailsUrl, apiKey, cancellationToken).ConfigureAwait(false);
            if (detailFailure is null)
            {
                hits = filters.Kind switch
                {
                    YouTubeKind.Playlist => WithCounts(hits, ParsePlaylists(details!)),
                    YouTubeKind.Channel => WithChannels(hits, ParseChannels(details!)),
                    _ => WithDetails(hits, ParseVideos(details!)),
                };
            }
            else
            {
                DiagnosticLog.Info(Category, $"details for \"{query}\": {detailFailure.Failure} {detailFailure.Detail}; the hits kept without them.");
            }
        }

        DiagnosticLog.Info(Category, $"search \"{query}\" ({filters.Kind}): {hits.Count.ToString(CultureInfo.InvariantCulture)} found.");
        return new YouTubeSearchOutcome(hits);
    }

    public async Task<YouTubePlaylistOutcome> PlaylistAsync(string playlistId, int max, string apiKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(playlistId);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return YouTubePlaylistOutcome.Failed(YouTubeFailure.NoKey);
        }

        var (body, failure) = await GetAsync(PlaylistsUrl([playlistId]), apiKey, cancellationToken).ConfigureAwait(false);
        if (failure is { } refused)
        {
            DiagnosticLog.Info(Category, $"playlist {playlistId}: {refused.Failure} {refused.Detail}");
            return YouTubePlaylistOutcome.Failed(refused.Failure, refused.Detail);
        }

        // A list playlists.list does not name (a channel's uploads may be one) is still read for its entries; its title is then its id,
        // its count what was read, and only the entries' own refusal says there is no such list.
        bool listed = ParsePlaylists(body!).TryGetValue(playlistId, out var playlist);
        if (!listed)
        {
            DiagnosticLog.Info(Category, $"playlist {playlistId}: playlists.list names none; reading its entries anyway.");
        }

        var entries = new List<YouTubePlaylistEntry>();
        string? page = null;
        int want = Math.Clamp(max, 1, MaxPlaylistEntries);
        do
        {
            (body, failure) = await GetAsync(PlaylistItemsUrl(playlistId, want - entries.Count, page), apiKey, cancellationToken).ConfigureAwait(false);
            if (failure is { } stopped)
            {
                DiagnosticLog.Info(Category, $"playlist {playlistId} entries: {stopped.Failure} {stopped.Detail}");
                if (entries.Count == 0)
                {
                    return YouTubePlaylistOutcome.Failed(!listed && stopped.Failure == YouTubeFailure.Failed ? YouTubeFailure.NotFound : stopped.Failure, stopped.Detail);
                }

                break;   // the pages read so far stand
            }

            var (read, next) = ParsePlaylistItems(body!);
            entries.AddRange(read);
            page = next;
        }
        while (page is not null && entries.Count < want);

        if (!listed && entries.Count == 0)
        {
            return YouTubePlaylistOutcome.Failed(YouTubeFailure.NotFound);
        }

        playlist ??= new YouTubePlaylist(playlistId, "", "", entries.Count);
        DiagnosticLog.Info(Category, $"playlist {playlistId}: {entries.Count.ToString(CultureInfo.InvariantCulture)} of {playlist.Count.ToString(CultureInfo.InvariantCulture)} entries read.");
        return new YouTubePlaylistOutcome(playlist, entries.Take(want).ToList());
    }

    // One GET: the body, or the outcome that says why there is none. No key (oEmbed's) sends no key header.
    private async Task<(string? Body, YouTubeSearchOutcome? Failure)> GetAsync(Uri url, string? apiKey, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(timeout ?? Timeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.TryAddWithoutValidation("X-Goog-Api-Key", apiKey.Trim());
            }
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, budget.Token).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(budget.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? (body, null) : (null, ParseError(response.StatusCode, body));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, YouTubeSearchOutcome.Failed(YouTubeFailure.Timeout));
        }
        catch (HttpRequestException ex) when (ex is NetworkRefusedException || ex.InnerException is NetworkRefusedException)
        {
            var refused = ex as NetworkRefusedException ?? (NetworkRefusedException)ex.InnerException!;
            return (null, YouTubeSearchOutcome.Failed(YouTubeFailure.Refused, refused.Message));
        }
        catch (HttpRequestException ex)
        {
            return (null, YouTubeSearchOutcome.Failed(YouTubeFailure.Failed, ex.Message));
        }
    }

    /// <summary>
    /// An error answer read: Google's <c>{"error":{"code","message","errors":[{"reason"}],"details":[{"reason"}]}}</c> into a
    /// <see cref="YouTubeFailure"/>, YouTube's message as the detail. Pure; pinned.
    /// </summary>
    public static YouTubeSearchOutcome ParseError(HttpStatusCode status, string? body)
    {
        string message = "";
        var reasons = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(body ?? "");
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                message = Text(error, "message");
                foreach (string list in new[] { "errors", "details" })
                {
                    if (error.TryGetProperty(list, out var items) && items.ValueKind == JsonValueKind.Array)
                    {
                        reasons.AddRange(items.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object).Select(i => Text(i, "reason")).Where(r => r.Length > 0));
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Not Google's JSON (a proxy's page): the status says what it can.
        }

        bool Has(params string[] names) => reasons.Any(r => names.Contains(r, StringComparer.OrdinalIgnoreCase));
        var failure = true switch
        {
            _ when Has("quotaExceeded", "dailyLimitExceeded", "rateLimitExceeded", "RATE_LIMIT_EXCEEDED") => YouTubeFailure.Quota,
            _ when Has("keyInvalid", "keyExpired", "API_KEY_INVALID", "API_KEY_EXPIRED") || (status == HttpStatusCode.BadRequest && message.Contains("API key", StringComparison.OrdinalIgnoreCase)) => YouTubeFailure.KeyInvalid,
            _ when Has("accessNotConfigured", "SERVICE_DISABLED") => YouTubeFailure.NotEnabled,
            _ when Has("ipRefererBlocked", "API_KEY_HTTP_REFERRER_BLOCKED", "API_KEY_IP_ADDRESS_BLOCKED", "API_KEY_SERVICE_BLOCKED", "API_KEY_ANDROID_APP_BLOCKED", "API_KEY_IOS_APP_BLOCKED") => YouTubeFailure.KeyRestricted,
            _ when Has("playlistNotFound", "playlistItemsNotAccessible") => YouTubeFailure.NotFound,
            _ when Has("invalidRegionCode", "invalidRelevanceLanguage", "invalidPublishedAfter", "invalidParameter", "invalidSearchFilter") => YouTubeFailure.BadFilter,
            _ => YouTubeFailure.Failed,
        };
        string status3 = ((int)status).ToString(CultureInfo.InvariantCulture);
        return YouTubeSearchOutcome.Failed(failure, message.Length > 0 ? $"HTTP {status3}: {message}" : $"HTTP {status3}");
    }

    /// <summary>
    /// <c>search.list</c>'s <c>items[]</c> of <paramref name="kind"/> (2026-10-08: a video's <c>id.videoId</c>, a playlist's
    /// <c>id.playlistId</c>, a channel's <c>id.channelId</c>; any other kind passed over) and the snippet's title, channel, date and
    /// live state. Pure; pinned.
    /// </summary>
    public static IReadOnlyList<YouTubeHit> ParseSearch(string json, YouTubeKind kind = YouTubeKind.Video)
    {
        string field = kind switch { YouTubeKind.Playlist => "playlistId", YouTubeKind.Channel => "channelId", _ => "videoId" };
        var hits = new List<YouTubeHit>();
        foreach (var item in Items(json))
        {
            if (!item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Object || Text(id, field) is not { Length: > 0 } videoId
                || !item.TryGetProperty("snippet", out var snippet) || snippet.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            DateTimeOffset? published = DateTimeOffset.TryParse(Text(snippet, "publishedAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when) ? when : null;
            var live = Text(snippet, "liveBroadcastContent") switch
            {
                "live" => YouTubeLive.Live,
                "upcoming" => YouTubeLive.Upcoming,
                _ => YouTubeLive.None,
            };
            hits.Add(new YouTubeHit(videoId, WebUtility.HtmlDecode(Text(snippet, "title")), WebUtility.HtmlDecode(Text(snippet, "channelTitle")), published, null, null, live, kind));
        }

        return hits;
    }

    /// <summary><c>playlists.list</c>'s <c>items[]</c> (2026-10-08): each id's title, channel and <c>contentDetails.itemCount</c>. Pure; pinned.</summary>
    public static IReadOnlyDictionary<string, YouTubePlaylist> ParsePlaylists(string json)
    {
        var playlists = new Dictionary<string, YouTubePlaylist>(StringComparer.Ordinal);
        foreach (var item in Items(json))
        {
            if (Text(item, "id") is not { Length: > 0 } id)
            {
                continue;
            }

            bool hasSnippet = item.TryGetProperty("snippet", out var snippet) && snippet.ValueKind == JsonValueKind.Object;
            int count = item.TryGetProperty("contentDetails", out var content) && content.ValueKind == JsonValueKind.Object
                && content.TryGetProperty("itemCount", out var items) && items.ValueKind == JsonValueKind.Number && items.TryGetInt32(out int n) ? n : 0;
            playlists[id] = new YouTubePlaylist(id, hasSnippet ? WebUtility.HtmlDecode(Text(snippet, "title")) : "", hasSnippet ? WebUtility.HtmlDecode(Text(snippet, "channelTitle")) : "", count);
        }

        return playlists;
    }

    /// <summary>
    /// <c>channels.list</c>'s <c>items[]</c> (2026-10-08): each id's subscribers (null when the channel hides them) and videos. Pure; pinned.
    /// </summary>
    public static IReadOnlyDictionary<string, (long? Subscribers, long? Videos)> ParseChannels(string json)
    {
        var channels = new Dictionary<string, (long?, long?)>(StringComparer.Ordinal);
        foreach (var item in Items(json))
        {
            if (Text(item, "id") is not { Length: > 0 } id || !item.TryGetProperty("statistics", out var stats) || stats.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            bool hidden = stats.TryGetProperty("hiddenSubscriberCount", out var flag) && flag.ValueKind == JsonValueKind.True;
            long? subscribers = !hidden && long.TryParse(Text(stats, "subscriberCount"), NumberStyles.None, CultureInfo.InvariantCulture, out long s) ? s : null;
            long? videos = long.TryParse(Text(stats, "videoCount"), NumberStyles.None, CultureInfo.InvariantCulture, out long v) ? v : null;
            channels[id] = (subscribers, videos);
        }

        return channels;
    }

    /// <summary>
    /// A <c>playlistItems.list</c> page (2026-10-08): its entries — <c>snippet.position</c>, <c>resourceId.videoId</c>, the title and
    /// <c>videoOwnerChannelTitle</c>, which a deleted or private video has none of — and the next page's token (null on the last). Pure; pinned.
    /// </summary>
    public static (IReadOnlyList<YouTubePlaylistEntry> Entries, string? NextPage) ParsePlaylistItems(string json)
    {
        var entries = new List<YouTubePlaylistEntry>();
        foreach (var item in Items(json))
        {
            if (!item.TryGetProperty("snippet", out var snippet) || snippet.ValueKind != JsonValueKind.Object
                || !snippet.TryGetProperty("resourceId", out var resource) || resource.ValueKind != JsonValueKind.Object
                || Text(resource, "videoId") is not { Length: > 0 } videoId)
            {
                continue;
            }

            int position = snippet.TryGetProperty("position", out var at) && at.ValueKind == JsonValueKind.Number && at.TryGetInt32(out int p) ? p : entries.Count;
            string owner = WebUtility.HtmlDecode(Text(snippet, "videoOwnerChannelTitle"));
            entries.Add(new YouTubePlaylistEntry(position, videoId, WebUtility.HtmlDecode(Text(snippet, "title")), owner, owner.Length > 0));
        }

        string? next = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            next = doc.RootElement.ValueKind == JsonValueKind.Object && Text(doc.RootElement, "nextPageToken") is { Length: > 0 } token ? token : null;
        }
        catch (JsonException)
        {
            // Items() said the same: no page.
        }

        return (entries, next);
    }

    /// <summary><c>videos.list</c>'s <c>items[]</c>: each id's length (<c>contentDetails.duration</c>, ISO 8601) and view count. Pure; pinned.</summary>
    public static IReadOnlyDictionary<string, (TimeSpan? Duration, long? Views)> ParseVideos(string json)
    {
        var details = new Dictionary<string, (TimeSpan?, long?)>(StringComparer.Ordinal);
        foreach (var item in Items(json))
        {
            if (Text(item, "id") is not { Length: > 0 } id)
            {
                continue;
            }

            TimeSpan? duration = item.TryGetProperty("contentDetails", out var content) && content.ValueKind == JsonValueKind.Object ? ParseDuration(Text(content, "duration")) : null;
            long? views = item.TryGetProperty("statistics", out var stats) && stats.ValueKind == JsonValueKind.Object
                && long.TryParse(Text(stats, "viewCount"), NumberStyles.None, CultureInfo.InvariantCulture, out long count) ? count : null;
            details[id] = (duration, views);
        }

        return details;
    }

    /// <summary>An ISO 8601 duration as YouTube writes it (<c>PT1H2M3S</c>, <c>P1DT2H</c>, <c>PT15S</c>); null for <c>P0D</c>/<c>PT0S</c> (a live broadcast) and anything else. Pure.</summary>
    public static TimeSpan? ParseDuration(string? text)
    {
        if (text is null || Duration().Match(text) is not { Success: true } match)
        {
            return null;
        }

        static int Part(Match m, string g) => m.Groups[g].Success ? int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture) : 0;
        var span = new TimeSpan(Part(match, "d"), Part(match, "h"), Part(match, "m"), Part(match, "s"));
        return span > TimeSpan.Zero ? span : null;
    }

    private static IReadOnlyList<YouTubeHit> WithDetails(IReadOnlyList<YouTubeHit> hits, IReadOnlyDictionary<string, (TimeSpan? Duration, long? Views)> details) =>
        hits.Select(h => details.TryGetValue(h.Id, out var d) ? h with { Duration = d.Duration, Views = d.Views } : h).ToArray();

    private static IReadOnlyList<YouTubeHit> WithCounts(IReadOnlyList<YouTubeHit> hits, IReadOnlyDictionary<string, YouTubePlaylist> playlists) =>
        hits.Select(h => playlists.TryGetValue(h.Id, out var p) ? h with { Count = p.Count } : h).ToArray();

    private static IReadOnlyList<YouTubeHit> WithChannels(IReadOnlyList<YouTubeHit> hits, IReadOnlyDictionary<string, (long? Subscribers, long? Videos)> channels) =>
        hits.Select(h => channels.TryGetValue(h.Id, out var c) ? h with { Subscribers = c.Subscribers, Count = c.Videos } : h).ToArray();

    private static List<JsonElement> Items(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
                ? items.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object).Select(i => i.Clone()).ToList()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    [GeneratedRegex(@"^P(?:(?<d>\d+)D)?(?:T(?:(?<h>\d+)H)?(?:(?<m>\d+)M)?(?:(?<s>\d+)S)?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex Duration();
}
