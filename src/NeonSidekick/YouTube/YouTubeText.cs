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
    public const string NoKey = "Error: YouTube search needs a YouTube Data API key, and none is set. The user can add one as YouTube API key on /tools › Media › YouTube (or NEONSIDEKICK_YOUTUBE_API_KEY): " + KeySteps + ". Playing a video by its id or link needs no key.";

    /// <summary><c>/youtube &lt;words&gt;</c> with no key: the user's own sentence (the tool's says "the user can").</summary>
    public const string NoKeyForUser = "Searching YouTube needs a YouTube Data API key: add one as YouTube API key on /tools › Media › YouTube. To make one, " + KeySteps + ". Playing a video by its id or link needs none.";

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
        YouTubeFailure.KeyInvalid => "Error: YouTube refused the API key as not valid or expired (" + detail + "). The user can set a new one as YouTube API key on /tools › Media › YouTube.",
        YouTubeFailure.NotEnabled => "Error: YouTube Data API v3 is not enabled on the API key's Google Cloud project (" + detail + "). The user can enable it under APIs & Services › Library in the Google Cloud Console.",
        YouTubeFailure.KeyRestricted => "Error: the API key's restrictions refuse this app (" + detail + "). In the Google Cloud Console its application restrictions should be None, and its API restrictions YouTube Data API v3.",
        YouTubeFailure.Refused => detail.Length > 0 ? detail : "Error: the network mode keeps the internet, and YouTube with it, off limits.",
        YouTubeFailure.Timeout => "Error: YouTube did not answer within " + YouTubeDataApi.Timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " seconds; try again.",
        YouTubeFailure.BadFilter => "Error: YouTube refused the search's filters (" + (detail.Length > 0 ? detail : "no reason given") + ").",
        YouTubeFailure.NotFound => "Error: YouTube lists no such public playlist" + (detail.Length > 0 ? " (" + detail + ")" : "") + ": it may be private, removed, or a mix made for one viewer, which only plays. Playing it by its link may still work.",
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

    /// <summary>
    /// A search's answer: a header (the transcript's note) and a numbered line per hit, its id last for youtube_play: a video's
    /// <c>id</c>, a playlist's <c>playlist</c> and a channel's <c>channel</c> (2026-10-08, the other kinds).
    /// </summary>
    public static string Results(string query, IReadOnlyList<YouTubeHit> hits, YouTubeKind kind = YouTubeKind.Video)
    {
        ArgumentNullException.ThrowIfNull(hits);
        if (hits.Count == 0)
        {
            return $"YouTube: no {Noun(kind, 2)} for \"{query}\".";
        }

        var lines = new List<string> { $"YouTube: {hits.Count.ToString(CultureInfo.InvariantCulture)} {Noun(kind, hits.Count)} for \"{query}\":" };
        for (int i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            string facts = string.Join(" · ", Facts(hit, withId: true));
            lines.Add($"{(i + 1).ToString(CultureInfo.InvariantCulture)}. {hit.Title}{(hit.Kind == YouTubeKind.Channel ? " · " : " — ")}{facts}");
        }

        return string.Join('\n', lines);
    }

    /// <summary><c>video</c>/<c>videos</c>, <c>playlist</c>/<c>playlists</c>, <c>channel</c>/<c>channels</c> (2026-10-08).</summary>
    public static string Noun(YouTubeKind kind, int count) => kind switch
    {
        YouTubeKind.Playlist => count == 1 ? "playlist" : "playlists",
        YouTubeKind.Channel => count == 1 ? "channel" : "channels",
        _ => count == 1 ? "video" : "videos",
    };

    // A hit's facts after its title: a video's channel, length, views and year; a playlist's channel, count and year; a channel's
    // subscribers and videos; then its id when asked for.
    private static IEnumerable<string> Facts(YouTubeHit hit, bool withId, bool withYear = true)
    {
        string year = withYear ? hit.Published?.Year.ToString(CultureInfo.InvariantCulture) ?? "" : "";
        string count = hit.Count is { } n ? Views(n) + (n == 1 ? " video" : " videos") : "";
        var parts = hit.Kind switch
        {
            YouTubeKind.Playlist => [hit.Channel, count, year, withId ? "playlist " + hit.Id : ""],
            YouTubeKind.Channel => [Views(hit.Subscribers) is { Length: > 0 } subs ? subs + " subscribers" : "", count, withId ? "channel " + hit.Id : ""],
            _ => new[] { hit.Channel, Length(hit), Views(hit.Views) is { Length: > 0 } views ? views + " views" : "", year, withId ? "id " + hit.Id : "" },
        };
        return parts.Where(p => p.Length > 0);
    }

    /// <summary>A search filter's word not among <paramref name="words"/> (2026-10-08).</summary>
    public static string NotOneOf(string argument, string? text, IEnumerable<string> words) => $"Error: \"{argument}\" must be one of {string.Join(", ", words)}, not \"{text}\".";

    /// <summary>A <c>since</c> that is no word and no date (2026-10-08).</summary>
    public static string NotASince(string? text) => $"Error: \"since\" must be today, week, month, year or a date like 2026-01-31, not \"{text}\".";

    /// <summary>A region that is not two letters (2026-10-08).</summary>
    public static string NotARegion(string? text) => $"Error: \"region\" must be a two-letter country code like US or GB, not \"{text}\".";

    /// <summary>A language that is no language code (2026-10-08).</summary>
    public static string NotALanguage(string? text) => $"Error: \"language\" must be a language code like en, es or zh-Hans, not \"{text}\".";

    /// <summary>A video-only filter on a playlist or channel search (2026-10-08). Pinned.</summary>
    public const string VideoOnlyFilter = "Error: length, live and captions filter videos only; leave them out to search for playlists or channels.";

    /// <summary>A <c>/youtube search</c> switch it does not know (2026-10-08).</summary>
    public static string UnknownSwitch(string? text) => $"Unknown switch \"{text}\"; {SearchSwitchesNote}.";

    /// <summary>The search switches in a line, for the usage and the help (2026-10-08). Pinned.</summary>
    public const string SearchSwitchesNote = "/youtube search takes --playlists, --channels, --order <relevance|date|views|rating|title>, --length <short|medium|long>, --since <today|week|month|year|date>, --region <cc>, --lang <code>, --live, --captions and --safe <none|moderate|strict> before its words";

    /// <summary>No window open.</summary>
    public const string NoVideo = "No video is open; youtube_play opens one.";

    /// <summary>A playlist named no way youtube_play reads (2026-10-08).</summary>
    /// <summary>A mix asked for as a playlist (2026-10-09, the review): YouTube's embedded player cannot play one as a list. Pinned.</summary>
    public static string Mix(string? text) =>
        $"Error: \"{text}\" is a YouTube mix, made for one viewer as they watch; the embedded player cannot play it as a list. Play its video (the link's v=) or a playlist (PL…) instead.";

    public static string NotAPlaylist(string? text) => $"Error: \"{text}\" names no YouTube playlist: give its id (PL…), a link with list=, or a channel's id (UC…) or link for its uploads.";

    /// <summary>next, previous or entry with no playlist in the window (2026-10-08). Pinned.</summary>
    public const string NoPlaylist = "Error: the video window is not playing a playlist; youtube_play with a playlist starts one.";

    /// <summary>An entry number out of the playlist (2026-10-08).</summary>
    public static string NotAnEntry(string? text, int? count) =>
        $"Error: \"entry\" must be a number from 1{(count is { } n ? " to " + n.ToString(CultureInfo.InvariantCulture) : "")}, not \"{text}\".";

    /// <summary>youtube_playlist's <c>max</c> out of its range (2026-10-08).</summary>
    public static readonly string BadPlaylistMax = "Error: \"max\" must be 1 to " + YouTubeDataApi.MaxPlaylistEntries.ToString(CultureInfo.InvariantCulture) + ".";

    /// <summary>youtube_playlist with no playlist named and none playing (2026-10-08).</summary>
    public const string NoPlaylistNamed = "Error: name a playlist (its id or a link); none is playing to list.";

    /// <summary>
    /// youtube_playlist's answer (2026-10-08): the playlist, then a numbered line per entry — its place in the list, the number
    /// youtube_play's <c>entry</c> takes — a deleted or private one said so.
    /// </summary>
    public static string PlaylistEntries(YouTubePlaylist playlist, IReadOnlyList<YouTubePlaylistEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        ArgumentNullException.ThrowIfNull(entries);
        string by = playlist.Channel.Length > 0 ? " by " + playlist.Channel : "";
        string shown = entries.Count < playlist.Count ? $", the first {entries.Count.ToString(CultureInfo.InvariantCulture)} listed" : "";
        var lines = new List<string> { $"Playlist {Named(playlist)}{by}: {playlist.Count.ToString(CultureInfo.InvariantCulture)} {Noun(YouTubeKind.Video, playlist.Count)}{shown}; youtube_play with playlist {playlist.Id} and an entry number plays from it:" };
        foreach (var entry in entries)
        {
            string number = (entry.Position + 1).ToString(CultureInfo.InvariantCulture);
            lines.Add(entry.Available
                ? $"{number}. {entry.Title} — {entry.Channel} · id {entry.VideoId}"
                : $"{number}. ({entry.Title.ToLowerInvariant()}, which the player skips)");
        }

        return string.Join('\n', lines);
    }

    // A playlist in a sentence: its title in quotes, or its id when YouTube named none (a channel's uploads).
    private static string Named(YouTubePlaylist playlist) => playlist.Title.Length > 0 ? $"\"{playlist.Title}\"" : playlist.Id;

    /// <summary>The picker's title over a playlist's entries (2026-10-08).</summary>
    public static string PlaylistPickTitle(YouTubePlaylist playlist)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        return $"YouTube playlist: {Named(playlist)} ({playlist.Count.ToString(CultureInfo.InvariantCulture)} {Noun(YouTubeKind.Video, playlist.Count)})";
    }

    /// <summary>
    /// A playlist's entries as the picker's rows (2026-10-08; in columns 2026-10-09, the search picker's way): the entry's number
    /// right-aligned, its title and channel; a deleted or private one in brackets. Pinned.
    /// </summary>
    public static IReadOnlyList<string> PlaylistPickRows(IReadOnlyList<YouTubePlaylistEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return Table(entries.Select(entry => new Column[]
        {
            new((entry.Position + 1).ToString(CultureInfo.InvariantCulture) + ".", 0, Right: true),
            new(entry.Available ? entry.Title : "(" + entry.Title.ToLowerInvariant() + ")", SavedTitleWidth),
            new(entry.Available ? entry.Channel : "", SavedChannelWidth),
        }).ToList());
    }

    /// <summary>The footer under the playlist's picker for <paramref name="entry"/> (2026-10-08): the whole title, then its place and id.</summary>
    public static UI.MenuFooter PlaylistPickFooter(YouTubePlaylist playlist, YouTubePlaylistEntry entry)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        ArgumentNullException.ThrowIfNull(entry);
        string place = $"entry {(entry.Position + 1).ToString(CultureInfo.InvariantCulture)} of {playlist.Count.ToString(CultureInfo.InvariantCulture)}";
        return new UI.MenuFooter(entry.Title, string.Join(" · ", new[] { entry.Channel, place, "id " + entry.VideoId }.Where(p => p.Length > 0)));
    }

    /// <summary>The spinner's label while <c>/youtube playlist</c> reads a playlist (2026-10-08).</summary>
    public const string ReadingPlaylist = "Reading the playlist from YouTube…";

    /// <summary><c>/youtube playlist</c> with no key (2026-10-08): the user's own sentence.</summary>
    public const string NoKeyForPlaylist = "Listing a playlist's videos needs a YouTube Data API key: add one as YouTube API key on /tools › Media › YouTube. Playing it needs none: /youtube play <playlist link>.";

    /// <summary>The picker's save on a playlist or a channel (2026-10-08): only a video is saved.</summary>
    public static string SaveVideosOnly(YouTubeKind kind) => kind == YouTubeKind.Channel
        ? "Only a video can be saved; Enter plays this channel's uploads."
        : "Only a video can be saved; Enter plays this playlist.";

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
            return Refused(snapshot.VideoId, code) + (snapshot.Skipping ? " The playlist moves on to the next entry." : "");
        }

        if (snapshot.State == VideoState.Opening)
        {
            return snapshot.VideoId is null && snapshot.PlaylistId is { } opening
                ? $"Opening playlist {opening} in the video window; youtube_status says when it plays."
                : $"Opening video {snapshot.VideoId} in the video window; youtube_status says when it plays.";
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
        return state + " " + title + author + where + sound + id + InPlaylist(snapshot);
    }

    /// <summary>
    /// Where a playlist's video stands in it (2026-10-08): <c> · entry 3 of 25 in playlist PL…</c>, <c> · in playlist PL…</c> before the
    /// player says; empty for one video.
    /// </summary>
    public static string InPlaylist(VideoSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.PlaylistId is not { } list)
        {
            return "";
        }

        string entry = snapshot.PlaylistEntry is { } at
            ? "entry " + (at + 1).ToString(CultureInfo.InvariantCulture) + (snapshot.PlaylistCount is { } count ? " of " + count.ToString(CultureInfo.InvariantCulture) : "") + " "
            : "";
        return $" · {entry}in playlist {list}";
    }

    /// <summary><c>/youtube</c>'s line in the help's list: a few words, lowercase.</summary>
    public const string HelpSummary = "search and play YouTube videos";

    /// <summary>A <c>/youtube</c> verb without what it needs.</summary>
    public const string CommandUsage = "/youtube [<words> | play <id|link> [<time>] | play <playlist|link> [<entry>] | next | previous | entry <n> | playlist [<id|link>] | pause | resume | seek <time> | volume <0-100> | mute | unmute | close | status | save [<id|link>] | list [--clear] | unsave <n|id|link> | search [--switches] <words>]";

    // ── The saved videos (2026-10-07, the user's ask) ─────────────────────────

    /// <summary>The saved-videos pane's label. Pinned.</summary>
    public const string SavedLabel = "📺 Saved videos";

    /// <summary>
    /// The saved-videos pane's hint row (type to filter since 2026-10-07; <c>c = clear all</c> for <c>d = remove</c> on 2026-10-08, and
    /// later that day both, the user's ask). Pinned.
    /// </summary>
    public const string SavedKeys = "Enter = play · d = remove · c = clear all · " + UI.MenuFilter.TypeAndCloseKeys;

    /// <summary>
    /// A list's remove button: the highlighted row taken off after a yes/no (the skills' version list's; the saved-videos pane's until
    /// 2026-10-08, which has <see cref="RemoveSelectedButton"/> since). Two spaces after the glyph, as <c>ProcessMenu.KillButton</c>'s:
    /// Windows Terminal draws ✖ two cells wide over the one space after it.
    /// </summary>
    public const string RemoveButton = "✖  remove";

    /// <summary>
    /// The saved-videos pane's remove (2026-10-08, the user's ask and word: back beside clear all the same day it went): the highlighted
    /// video taken off after a yes/no. Pinned.
    /// </summary>
    public const string RemoveSelectedButton = "✖  remove selected";

    /// <summary>The key that is <see cref="RemoveSelectedButton"/>.</summary>
    public const char RemoveKey = 'd';

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

    /// <summary>
    /// The spinner's label while <c>/youtube</c> waits for the player to start a video or playlist, or move through one (2026-10-09:
    /// the wait, up to <see cref="YouTubeWait.Play"/>, held the screen with nothing on it, found playing a channel the page never started).
    /// </summary>
    public const string WaitingForPlayer = "Waiting for the video window…";

    /// <summary>ESC or Ctrl+C under <see cref="WaitingForPlayer"/>: only the wait stops, the window carries on (2026-10-09).</summary>
    public const string StoppedWaiting = "(stopped waiting; the video window is still open: /youtube status says where it is)";

    /// <summary>The spinner's label while <c>/youtube list</c> looks up the titles still missing (2026-10-07).</summary>
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

    /// <summary>The saved pane's title column at most (2026-10-08): a longer title is cut with an ellipsis, the footer showing it whole. Pinned.</summary>
    public const int SavedTitleWidth = 56;

    /// <summary>The saved pane's channel column at most (2026-10-08), cut the same way.</summary>
    public const int SavedChannelWidth = 24;

    /// <summary>
    /// The saved pane's title and channel columns over <paramref name="videos"/> (2026-10-08, the user's ask: uniform columns): the
    /// widest of each in cells, capped at <see cref="SavedTitleWidth"/> and <see cref="SavedChannelWidth"/>. Taken over the whole list,
    /// so a filter typed leaves the columns where they are.
    /// </summary>
    public static (int Title, int Channel) SavedColumns(IReadOnlyList<YouTubeSaved> videos)
    {
        ArgumentNullException.ThrowIfNull(videos);
        int title = videos.Count == 0 ? 0 : videos.Max(v => UI.TextCells.Width(SavedTitle(v)));
        int channel = videos.Count == 0 ? 0 : videos.Max(v => UI.TextCells.Width(v.Author ?? ""));
        return (Math.Min(title, SavedTitleWidth), Math.Min(channel, SavedChannelWidth));
    }

    /// <summary>
    /// A saved video as the pane's row (2026-10-08, the user's ask: uniform columns; <see cref="SavedRow"/> until then, which the printed
    /// list keeps): the title and the channel each padded to its column of <see cref="SavedColumns"/> (cut with an ellipsis past it),
    /// then the place, two blanks between; no channel column when no video has one. Pinned.
    /// </summary>
    public static string SavedPaneRow(YouTubeSaved video, int titleWidth, int channelWidth)
    {
        ArgumentNullException.ThrowIfNull(video);
        string row = Cell(SavedTitle(video), titleWidth) + "  ";
        if (channelWidth > 0)
        {
            row += Cell(video.Author ?? "", channelWidth) + "  ";
        }

        return row + Place(video);
    }

    private static string SavedTitle(YouTubeSaved video) => video.Title ?? "video " + video.Id;

    /// <summary><paramref name="text"/> in exactly <paramref name="width"/> cells: cut with an ellipsis, or padded with blanks.</summary>
    private static string Cell(string text, int width)
    {
        string fitted = UI.ScreenPane.Fit(text, width);
        return fitted + new string(' ', Math.Max(0, width - UI.TextCells.Width(fitted)));
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
        return new UI.MenuFooter(hit.Title, string.Join(" · ", Facts(hit, withId: true)));
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

    /// <summary>The question before the pane's remove selected.</summary>
    public static string RemovePrompt(YouTubeSaved video) => $"📺 Remove {Name(video)} from the saved videos?";

    /// <summary>The question before the pane's clear all and <c>/youtube list --clear</c> (2026-10-08). Pinned.</summary>
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

    /// <summary>
    /// A search's hits as the picker's rows, in columns (2026-10-09, the user's ask: the saved list's uniform columns, 2026-10-08): a
    /// video's title, channel, length and views; a playlist's title, channel and count; a channel's name, subscribers and videos. The
    /// title and channel are cut at <see cref="SavedTitleWidth"/> and <see cref="SavedChannelWidth"/> (the footer has them whole), the
    /// numbers right-aligned; the year and the id are the footer's. Pinned.
    /// </summary>
    public static IReadOnlyList<string> PickRows(IReadOnlyList<YouTubeHit> hits)
    {
        ArgumentNullException.ThrowIfNull(hits);
        return Table(hits.Select(hit =>
        {
            string count = hit.Count is { } n ? Views(n) + (n == 1 ? " video" : " videos") : "";
            return hit.Kind switch
            {
                YouTubeKind.Playlist => new Column[] { new(hit.Title, SavedTitleWidth), new(hit.Channel, SavedChannelWidth), new(count, 0, Right: true) },
                YouTubeKind.Channel => [new(hit.Title, SavedTitleWidth), new(Views(hit.Subscribers) is { Length: > 0 } subs ? subs + " subscribers" : "", 0, Right: true), new(count, 0, Right: true)],
                _ => [new(hit.Title, SavedTitleWidth), new(hit.Channel, SavedChannelWidth), new(Length(hit), 0, Right: true), new(Views(hit.Views) is { Length: > 0 } views ? views + " views" : "", 0, Right: true)],
            };
        }).ToList());
    }

    // One cell of a picker's table: its text, its width at most (0 for none) and whether it sits right (a number).
    private readonly record struct Column(string Text, int Cap, bool Right = false);

    // Rows of cells as lines in columns: each column as wide as its widest cell (held to its cap, cut with an ellipsis past it), two
    // blanks between, an empty column left out, the line's end trimmed.
    private static List<string> Table(IReadOnlyList<Column[]> rows)
    {
        int count = rows.Count == 0 ? 0 : rows.Max(r => r.Length);
        var widths = new int[count];
        foreach (var row in rows)
        {
            for (int i = 0; i < row.Length; i++)
            {
                int width = UI.TextCells.Width(row[i].Text);
                widths[i] = Math.Max(widths[i], row[i].Cap > 0 ? Math.Min(width, row[i].Cap) : width);
            }
        }

        return rows.Select(row => string.Join("  ", row.Select((cell, i) => (cell, i)).Where(c => widths[c.i] > 0)
            .Select(c => c.cell.Right ? RightCell(c.cell.Text, widths[c.i]) : Cell(c.cell.Text, widths[c.i]))).TrimEnd()).ToList();
    }

    // Text in exactly width cells, padded on the left (a number's column).
    private static string RightCell(string text, int width)
    {
        string fitted = UI.ScreenPane.Fit(text, width);
        return new string(' ', Math.Max(0, width - UI.TextCells.Width(fitted))) + fitted;
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
