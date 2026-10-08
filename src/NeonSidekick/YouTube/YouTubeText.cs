using System.Globalization;
using NeonSidekick.Viewer;

namespace NeonSidekick.YouTube;

/// <summary>
/// The YouTube tools' and <c>/youtube</c>'s words (2026-10-05, the YouTube plan): why a search found nothing, where a key comes
/// from, and how a hit reads. The video window's own (its title, no runtime) are <c>VideoText</c>'s. Pinned where a test says so.
/// </summary>
public static class YouTubeText
{
    /// <summary>The quota a search costs and the day's allowance, as the key's help says it.</summary>
    public const string QuotaNote = "a search costs 100 of the project's 10,000 units a day, about 100 searches";

    /// <summary>Where a key comes from: the steps, short enough for a tool result and the menu row's description.</summary>
    public const string KeySteps = "in the Google Cloud Console (console.cloud.google.com), create a project, enable YouTube Data API v3 under APIs & Services › Library, then create an API key under Credentials and restrict it to that API";

    /// <summary>No key set: the search was not asked.</summary>
    public const string NoKey = "Error: YouTube search needs a YouTube Data API key, and none is set. The user can add one as YouTube API key on the YouTube tab of /tools (or NEONSIDEKICK_YOUTUBE_API_KEY): " + KeySteps + ". Playing a video by its id or link needs no key.";

    /// <summary><c>/youtube &lt;words&gt;</c> with no key: the user's own sentence (the tool's says "the user can").</summary>
    public const string NoKeyForUser = "Searching YouTube needs a YouTube Data API key: add one as YouTube API key on the YouTube tab of /tools. To make one, " + KeySteps + ". Playing a video by its id or link needs none.";

    /// <summary>youtube_search with nothing to look for.</summary>
    public const string EmptyQuery = "Error: \"query\" is empty; say what to look for.";

    /// <summary>youtube_search's <c>max</c> out of the setting's own range (2026-10-06: here, from the bounds, rather than inline as "1 to 20").</summary>
    public static readonly string BadMax =
        "Error: \"max\" must be " + Settings.AppSettingsData.MinYouTubeSearchMaxResults.ToString(CultureInfo.InvariantCulture) + " to " +
        Settings.AppSettingsData.MaxYouTubeSearchMaxResults.ToString(CultureInfo.InvariantCulture) + ".";

    /// <summary>The spinner's label while <c>/youtube</c> searches.</summary>
    public const string Searching = "Searching YouTube…";

    /// <summary>A failed search as one sentence for the model or the chat.</summary>
    public static string Failure(YouTubeFailure failure, string detail) => failure switch
    {
        YouTubeFailure.NoKey => NoKey,
        YouTubeFailure.Quota => "Error: the YouTube API key's daily quota is used up (" + QuotaNote + "); it resets at midnight Pacific time.",
        YouTubeFailure.KeyInvalid => "Error: YouTube refused the API key as not valid or expired (" + detail + "). The user can set a new one as YouTube API key on the YouTube tab of /tools.",
        YouTubeFailure.NotEnabled => "Error: YouTube Data API v3 is not enabled on the API key's Google Cloud project (" + detail + "). The user can enable it under APIs & Services › Library in the Google Cloud Console.",
        YouTubeFailure.KeyRestricted => "Error: the API key's restrictions refuse this app (" + detail + "). In the Google Cloud Console its application restrictions should be None, and its API restrictions YouTube Data API v3.",
        YouTubeFailure.Refused => detail.Length > 0 ? detail : "Error: the network mode keeps the internet, and YouTube with it, off limits.",
        YouTubeFailure.Timeout => "Error: YouTube did not answer within " + YouTubeDataApi.Timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " seconds; try again.",
        _ => "Error: the YouTube search failed (" + (detail.Length > 0 ? detail : "no reason given") + ").",
    };

    /// <summary>A length as a player shows it: <c>3:07</c>, <c>1:02:03</c>; <c>live</c> for a broadcast, empty when unknown.</summary>
    public static string Length(YouTubeHit hit) => hit.Live switch
    {
        YouTubeLive.Live => "live",
        YouTubeLive.Upcoming => "upcoming",
        _ => hit.Duration is { } d ? YouTubeIds.FormatTime(d.TotalSeconds) : "",
    };

    /// <summary>A view count the short way: <c>950</c>, <c>12K</c>, <c>3.4M</c>, <c>1.2B</c>; empty when hidden.</summary>
    public static string Views(long? views) => views switch
    {
        null => "",
        < 1_000 => views.Value.ToString(CultureInfo.InvariantCulture),
        < 1_000_000 => Short(views.Value / 1_000d) + "K",
        < 1_000_000_000 => Short(views.Value / 1_000_000d) + "M",
        _ => Short(views.Value / 1_000_000_000d) + "B",
    };

    /// <summary>A search's answer: a header (the transcript's note) and a numbered line per video, its id last for youtube_play.</summary>
    public static string Results(string query, IReadOnlyList<YouTubeHit> hits)
    {
        if (hits.Count == 0)
        {
            return $"YouTube: no videos for \"{query}\".";
        }

        var lines = new List<string> { $"YouTube: {hits.Count.ToString(CultureInfo.InvariantCulture)} video{(hits.Count == 1 ? "" : "s")} for \"{query}\":" };
        for (int i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            var parts = new List<string> { hit.Channel, Length(hit), Views(hit.Views) is { Length: > 0 } views ? views + " views" : "", hit.Published?.Year.ToString(CultureInfo.InvariantCulture) ?? "", "id " + hit.Id };
            lines.Add($"{(i + 1).ToString(CultureInfo.InvariantCulture)}. {hit.Title} — {string.Join(" · ", parts.Where(p => p.Length > 0))}");
        }

        return string.Join('\n', lines);
    }

    /// <summary>No window open.</summary>
    public const string NoVideo = "No video is open; youtube_play opens one.";

    /// <summary>A video named no way youtube_play reads.</summary>
    public static string NotAVideo(string? text) => $"Error: \"{text}\" names no YouTube video: give its 11-character id or a youtube.com or youtu.be link.";

    /// <summary>A time youtube_play or youtube_control could not read.</summary>
    public static string NotATime(string argument, string? text) => $"Error: \"{argument}\" must be seconds or a time like 1:30, not \"{text}\".";

    /// <summary>A volume out of 0–100.</summary>
    public static string NotAVolume(string? text) => $"Error: \"value\" must be a volume from 0 to 100, not \"{text}\".";

    /// <summary>An action youtube_control does not know.</summary>
    public static string NotAnAction(string? text, IEnumerable<string> actions) => $"Error: \"action\" must be one of {string.Join(", ", actions)}, not \"{text}\".";

    /// <summary>
    /// YouTube's <c>onError</c> for a video as a sentence: 2 a bad id, 100 not found, and 101/150 — which YouTube also gives a
    /// private, age-restricted, region-blocked or missing video (the Phase 0 spike: a made-up id gave 150) — not playable embedded.
    /// Each points at open_url for the watch page.
    /// </summary>
    public static string Refused(string? id, int code)
    {
        string watch = id is { Length: > 0 } ? YouTubeIds.WatchUrl(id) : "the video's page";
        string what = code switch
        {
            2 => $"YouTube says \"{id}\" is not a valid video id",
            5 => $"the player could not play video {id} (an HTML5 player error)",
            100 => $"YouTube found no video {id} (it was removed or made private)",
            _ => $"YouTube will not play video {id} in an embedded player: its uploader may have turned embedding off, or it is private, age-restricted or unavailable",
        };
        return $"Error: {what}. open_url can open {watch} in the browser instead.";
    }

    /// <summary>
    /// What the window reports, as one line: <c>Playing "Big Buck Bunny" (Blender) at 0:31 of 10:35, volume 25%, muted · video aqz-KE-bpKQ</c>;
    /// a refusal (<see cref="Refused"/>) or the window's failure as its sentence; <see cref="NoVideo"/> with none open.
    /// </summary>
    public static string Status(VideoSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return NoVideo;
        }

        if (snapshot.State == VideoState.Failed)
        {
            return "Error: " + (snapshot.Failure ?? "the video window's browser could not start.");
        }

        if (snapshot.Error is { } code)
        {
            return Refused(snapshot.VideoId, code);
        }

        if (snapshot.State == VideoState.Opening)
        {
            return $"Opening video {snapshot.VideoId} in the video window; youtube_status says when it plays.";
        }

        string state = snapshot.State switch
        {
            VideoState.Playing => "Playing",
            VideoState.Paused => "Paused",
            VideoState.Buffering => "Buffering",
            VideoState.Ended => "Ended",
            VideoState.Cued => "Cued (press play to start)",
            _ => "Loaded, not started",
        };
        string title = string.IsNullOrWhiteSpace(snapshot.Title) ? "video " + snapshot.VideoId : $"\"{snapshot.Title}\"";
        string author = string.IsNullOrWhiteSpace(snapshot.Author) ? "" : $" ({snapshot.Author})";
        string where = snapshot.Duration > 0
            ? $" at {YouTubeIds.FormatTime(snapshot.Position)} of {YouTubeIds.FormatTime(snapshot.Duration)}"
            : snapshot.Position > 0 ? $" at {YouTubeIds.FormatTime(snapshot.Position)}" : "";
        string sound = $", volume {snapshot.Volume.ToString(CultureInfo.InvariantCulture)}%{(snapshot.Muted ? ", muted" : "")}";
        string id = string.IsNullOrWhiteSpace(snapshot.Title) ? "" : $" · video {snapshot.VideoId}";
        return state + " " + title + author + where + sound + id;
    }

    /// <summary><c>/youtube</c>'s line in the help's list: a few words, lowercase.</summary>
    public const string HelpSummary = "search and play YouTube videos";

    /// <summary>A <c>/youtube</c> verb without what it needs.</summary>
    public const string CommandUsage = "/youtube [<words> | play <id|link> [<time>] | pause | resume | seek <time> | volume <0-100> | mute | unmute | close | status | search <words>]";

    /// <summary>The picker's title over a search's hits.</summary>
    public static string PickTitle(string query) => $"YouTube: \"{query}\"";

    /// <summary>A hit as the picker's row: the title, then the channel, the length and the views.</summary>
    public static string PickRow(YouTubeHit hit)
    {
        var parts = new[] { hit.Channel, Length(hit), Views(hit.Views) is { Length: > 0 } views ? views + " views" : "" }.Where(p => p.Length > 0);
        return hit.Title + "  ·  " + string.Join(" · ", parts);
    }

    /// <summary>Headless, and off Windows: every verb but a search wants the video window.</summary>
    public const string NeedsScreen = "/youtube plays videos in the app's video window, which a headless run has none of; /youtube <words> lists what a search finds.";

    /// <summary>No video window to play in (not Windows).</summary>
    public const string NoWindow = "There is no video window here (it needs Windows); /youtube <words> still searches.";

    /// <summary>
    /// <see cref="NoWindow"/> on a Mac (2026-10-07: the window plays there too, but needs a desktop session and macOS 14 or later).
    /// </summary>
    public const string NoWindowMac = "There is no video window here (it needs a desktop session and macOS 14 or later); /youtube <words> still searches.";

    /// <summary>The window closed.</summary>
    public const string Closed = "Closed the video window.";

    /// <summary>A command sent, the window gone before it could show it.</summary>
    public const string ClosedMeanwhile = "The video window was closed.";

    private static string Short(double value) =>
        (value < 10 ? Math.Floor(value * 10) / 10 : Math.Floor(value)).ToString("0.#", CultureInfo.InvariantCulture);
}
