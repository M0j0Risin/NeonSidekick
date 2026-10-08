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
    public const string CommandUsage = "/youtube [<words> | play <id|link> [<time>] | pause | resume | seek <time> | volume <0-100> | mute | unmute | close | status | save [<id|link>] | saved [--clear] | unsave <n|id|link> | search <words>]";

    // ── The saved videos (2026-10-07, the user's ask) ─────────────────────────

    /// <summary>The saved-videos pane's label. Pinned.</summary>
    public const string SavedLabel = "📺 Saved videos";

    /// <summary>The saved-videos pane's hint row (type to filter since 2026-10-07; <c>c = clear all</c> for <c>d = remove</c> since 2026-10-08). Pinned.</summary>
    public const string SavedKeys = "Enter = play · c = clear all · " + UI.MenuFilter.TypeAndCloseKeys;

    /// <summary>
    /// A list's remove button: the highlighted row taken off after a yes/no (the saved-videos pane's until 2026-10-08, when its
    /// button became clear all; the skills' version list's still). Two spaces after the glyph, as <c>ProcessMenu.KillButton</c>'s:
    /// Windows Terminal draws ✖ two cells wide over the one space after it.
    /// </summary>
    public const string RemoveButton = "✖  remove";

    /// <summary>The search picker's title-row button: the highlighted hit saved without playing it. Pinned.</summary>
    public const string SaveButton = "+ save";

    /// <summary>The key that is <see cref="SaveButton"/>.</summary>
    public const char SaveKey = 's';

    /// <summary>The search picker's hint row: <c>SettingsMenu.PickKeys</c> with the save key. Pinned.</summary>
    public const string PickKeys = "Enter = play · s = save · ESC = back";

    /// <summary>No saved videos to show.</summary>
    public const string NoneSaved = "No saved videos yet: /youtube save keeps the one playing, /youtube save <id|link> any other.";

    /// <summary>The model's answer with nothing saved.</summary>
    public const string NoneSavedForModel = "No saved YouTube videos; " + Llm.Tools.YouTubeSaveTool.ToolName + " adds one.";

    /// <summary><c>/youtube save</c>, or youtube_save's <c>current</c>, with no video in the window.</summary>
    public const string NothingToSave = "No video is open to save; give its id or a link.";

    /// <summary>The spinner's label while <c>/youtube save &lt;id|link&gt;</c> looks the video up (2026-10-07).</summary>
    public const string LookingUp = "Looking the video up on YouTube…";

    /// <summary>The spinner's label while <c>/youtube saved</c> looks up the titles still missing (2026-10-07).</summary>
    public const string LookingUpTitles = "Looking up the saved videos' titles…";

    /// <summary>The list could not be written (the log has why).</summary>
    public const string SaveFailed = "Could not write the saved videos (" + YouTubeLibrary.FileName + "); the log says why.";

    /// <summary>How a saved video is named in a sentence: its title in quotes once the player has told it, else its id.</summary>
    public static string Name(YouTubeSaved video)
    {
        ArgumentNullException.ThrowIfNull(video);
        return video.Title is { Length: > 0 } title ? $"\"{title}\"" : "video " + video.Id;
    }

    /// <summary>Where a saved video stands: <c>at 12:34 of 45:00</c>, <c>watched</c> (and where a rewatch was left), or <c>not played yet</c>.</summary>
    public static string Place(YouTubeSaved video)
    {
        ArgumentNullException.ThrowIfNull(video);
        string at = video.Position <= 0 ? ""
            : video.Duration > 0 ? $"at {YouTubeIds.FormatTime(video.Position)} of {YouTubeIds.FormatTime(video.Duration)}"
            : $"at {YouTubeIds.FormatTime(video.Position)}";
        if (video.Watched)
        {
            return at.Length > 0 ? "watched · " + at : "watched";
        }

        return at.Length > 0 ? at
            : video.LastPlayed is null ? "not played yet"
            : video.Duration > 0 ? "from the start · " + YouTubeIds.FormatTime(video.Duration) : "from the start";
    }

    /// <summary>A saved video as the pane's row and the printed list's: <c>Title — Channel · at 12:34 of 45:00</c>.</summary>
    public static string SavedRow(YouTubeSaved video)
    {
        ArgumentNullException.ThrowIfNull(video);
        string title = video.Title ?? "video " + video.Id;
        return title + (video.Author is { Length: > 0 } author ? " — " + author : "") + " · " + Place(video);
    }

    /// <summary>
    /// The footer under the saved list for <paramref name="video"/> (2026-10-07, the user's ask: the place is the row's end, cut first):
    /// the whole title and channel, then <c>at 12:34 of 45:00 · saved 2026-10-07 · last played 2026-10-07 21:45 · id …</c>. Pinned.
    /// </summary>
    public static UI.MenuFooter SavedFooter(YouTubeSaved video, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(zone);
        string title = (video.Title ?? "video " + video.Id) + (video.Author is { Length: > 0 } author ? " — " + author : "");
        var facts = new List<string> { Place(video), "saved " + Sessions.SessionText.Moment(video.Added, zone) };
        if (video.LastPlayed is { } played)
        {
            facts.Add("last played " + Sessions.SessionText.Moment(played, zone));
        }

        facts.Add("id " + video.Id);
        return new UI.MenuFooter(title, string.Join(" · ", facts));
    }

    /// <summary>Whether <paramref name="video"/> stays under <paramref name="filter"/>: its title, channel or id holds it (2026-10-07).</summary>
    public static bool SavedMatches(string filter, YouTubeSaved video)
    {
        ArgumentNullException.ThrowIfNull(video);
        return UI.MenuFilter.Matches(filter, video.Title ?? "", (video.Author ?? "") + " " + video.Id);
    }

    /// <summary>
    /// The footer under a search's picker for <paramref name="hit"/> (2026-10-07): the whole title, then
    /// <c>Blender · 10:35 · 21M views · 2014 · id aqz-KE-bpKQ</c>. Pinned.
    /// </summary>
    public static UI.MenuFooter PickFooter(YouTubeHit hit)
    {
        ArgumentNullException.ThrowIfNull(hit);
        var parts = new List<string> { hit.Channel, Length(hit), Views(hit.Views) is { Length: > 0 } views ? views + " views" : "", hit.Published?.Year.ToString(CultureInfo.InvariantCulture) ?? "", "id " + hit.Id };
        return new UI.MenuFooter(hit.Title, string.Join(" · ", parts.Where(p => p.Length > 0)));
    }

    /// <summary>The count over the pane's rows and the printed list.</summary>
    public static string SavedCaption(int count) =>
        $"{count.ToString(CultureInfo.InvariantCulture)} saved video{(count == 1 ? "" : "s")}; each resumes where it was left.";

    /// <summary>youtube_saved's answer: a numbered line per video, its id last for youtube_play.</summary>
    public static string SavedList(IReadOnlyList<YouTubeSaved> videos)
    {
        ArgumentNullException.ThrowIfNull(videos);
        if (videos.Count == 0)
        {
            return NoneSavedForModel;
        }

        var lines = new List<string> { $"Saved YouTube videos ({videos.Count.ToString(CultureInfo.InvariantCulture)}); youtube_play resumes each where it was left:" };
        for (int i = 0; i < videos.Count; i++)
        {
            lines.Add($"{(i + 1).ToString(CultureInfo.InvariantCulture)}. {SavedRow(videos[i])} · id {videos[i].Id}");
        }

        return string.Join('\n', lines);
    }

    /// <summary>A save done, or found done already.</summary>
    public static string Saved(YouTubeSaved video, YouTubeSaveOutcome outcome) => outcome switch
    {
        YouTubeSaveOutcome.AlreadySaved => $"{Name(video)} is saved already ({Place(video)}).",
        YouTubeSaveOutcome.Failed => "Error: " + SaveFailed,
        _ => $"Saved {Name(video)}; it resumes where it is left.",
    };

    /// <summary>A saved video taken off the list.</summary>
    public static string Unsaved(YouTubeSaved video) => $"Removed {Name(video)} from the saved videos.";

    /// <summary>The question before the pane's clear all and <c>/youtube saved --clear</c> (2026-10-08). Pinned.</summary>
    public static string ClearPrompt(int count) => count == 1
        ? "📺 Remove the one saved video? Where it was left goes with it."
        : $"📺 Remove all {Videos(count)}? Where each was left goes with them.";

    /// <summary>Every saved video taken off the list (2026-10-08). Pinned.</summary>
    public static string Cleared(int count) => $"Removed {Videos(count)}.";

    /// <summary>
    /// What a clear answers, from <see cref="YouTubeLibrary.Clear"/>'s count: <see cref="Cleared"/>, <see cref="NoneSaved"/> for none
    /// (another clear got there first), or the write's failure led by <c>Error: </c>.
    /// </summary>
    public static string ClearAnswer(int? cleared) => cleared switch
    {
        null => "Error: " + SaveFailed,
        0 => NoneSaved,
        _ => Cleared(cleared.Value),
    };

    private static string Videos(int count) =>
        $"{count.ToString(CultureInfo.InvariantCulture)} saved video{(count == 1 ? "" : "s")}";

    /// <summary>What names no saved video.</summary>
    public static string NotSaved(string? text) => $"Error: \"{text}\" is no saved video: give its number in the saved list, its id or a link to it.";

    /// <summary>youtube_play's lead when a saved video picked up where it was left.</summary>
    public static string Resumed(double seconds) => $"Resumed the saved video at {YouTubeIds.FormatTime(seconds)}, a moment before where it was left. ";

    /// <summary>youtube_save's <c>action</c> neither add nor remove.</summary>
    public static string NotASaveAction(string? text) => $"Error: \"action\" must be add or remove, not \"{text}\".";

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
