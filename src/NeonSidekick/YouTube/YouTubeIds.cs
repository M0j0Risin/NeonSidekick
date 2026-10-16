using System.Globalization;
using System.Text.RegularExpressions;
using NeonSidekick.Viewer;

namespace NeonSidekick.YouTube;

/// <summary>
/// A YouTube video named any way a person or a model names one (2026-10-05, the YouTube plan): the bare 11-character id, or
/// a link — <c>youtube.com/watch?v=</c>, <c>youtu.be/</c>, <c>/shorts/</c>, <c>/embed/</c>, <c>/live/</c>, <c>/v/</c>, on
/// <c>www.</c>, <c>m.</c>, <c>music.</c> or <c>youtube-nocookie.com</c> — with its start time when the link carries one
/// (<c>t=</c> or <c>start=</c>: <c>90</c>, <c>90s</c>, <c>1m30s</c>, <c>1h2m3s</c>). A time on its own (<see cref="ParseTime"/>)
/// also reads <c>1:30</c> and <c>1:02:03</c>. Pure.
/// </summary>
public static partial class YouTubeIds
{
    private static readonly string[] Hosts = ["youtube.com", "www.youtube.com", "m.youtube.com", "music.youtube.com", "youtube-nocookie.com", "www.youtube-nocookie.com"];
    private static readonly string[] PathKinds = ["shorts", "embed", "live", "v"];

    /// <summary>The video <paramref name="text"/> names, and the start a link gives (0 without one); false when it names none.</summary>
    public static bool TryParse(string? text, out string id, out double start)
    {
        id = "";
        start = 0;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (VideoRequest.IsVideoId(text))
        {
            id = text;
            return true;
        }

        string link = text.Contains("://", StringComparison.Ordinal) ? text : "https://" + text;
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        string host = uri.Host.ToLowerInvariant();
        string[] segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var query = Query(uri.Query);
        string? candidate = null;
        if (host is "youtu.be" or "www.youtu.be")
        {
            candidate = segments.FirstOrDefault();
        }
        else if (Hosts.Contains(host))
        {
            if (segments is ["watch", ..])
            {
                candidate = query.GetValueOrDefault("v");
            }
            else if (segments.Length >= 2 && PathKinds.Contains(segments[0]))
            {
                candidate = segments[1];
            }
        }

        if (!VideoRequest.IsVideoId(candidate))
        {
            return false;
        }

        id = candidate!;
        string? time = query.GetValueOrDefault("t") ?? query.GetValueOrDefault("start");
        if (time is null && uri.Fragment.StartsWith("#t=", StringComparison.Ordinal))
        {
            time = uri.Fragment[3..];
        }

        start = time is null ? 0 : ParseTime(time) ?? 0;
        return true;
    }

    /// <summary>
    /// The public playlist <paramref name="text"/> names (2026-10-08, the user's ask): a bare playlist id (<see cref="VideoRequest.IsPlaylistId"/>),
    /// a link with <c>list=</c> (<c>/playlist?list=</c>, <c>/watch?v=…&amp;list=…</c>, <c>youtu.be/…?list=…</c>), or a channel — its id
    /// <c>UC…</c> or a <c>/channel/UC…</c> link — as its uploads list (<c>UU…</c>, <see cref="Uploads"/>). The entry is a link's
    /// <c>index=</c> (1-based there, 0-based here); without one, a watch link's video is where to start (<paramref name="videoId"/>),
    /// and the start its <c>t=</c>. False when it names no playlist, a plain video link among them — and a mix (<see cref="IsMix"/>,
    /// the review of 2026-10-09): a video shared from one carries <c>&amp;list=RD…&amp;start_radio=1</c>, and such a link plays its video.
    /// </summary>
    public static bool TryParsePlaylist(string? text, out string list, out int entry, out string? videoId, out double start)
    {
        list = "";
        entry = 0;
        videoId = null;
        start = 0;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (Uploads(text) is { } uploads)
        {
            list = uploads;
            return true;
        }

        if (VideoRequest.IsPlaylistId(text))
        {
            list = text;
            return !IsMix(text);
        }

        string link = text.Contains("://", StringComparison.Ordinal) ? text : "https://" + text;
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        string host = uri.Host.ToLowerInvariant();
        if (!Hosts.Contains(host) && host is not ("youtu.be" or "www.youtu.be"))
        {
            return false;
        }

        string[] segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (Hosts.Contains(host) && segments is ["channel", var channel, ..] && Uploads(channel) is { } channelUploads)
        {
            list = channelUploads;
            return true;
        }

        var query = Query(uri.Query);
        if (query.GetValueOrDefault("list") is not { } named || !VideoRequest.IsPlaylistId(named) || IsMix(named))
        {
            return false;
        }

        list = named;
        if (int.TryParse(query.GetValueOrDefault("index"), NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index >= 1)
        {
            entry = index - 1;
        }
        else if (TryParse(text, out string id, out double at))
        {
            videoId = id;
            start = at;
        }

        return true;
    }

    /// <summary>
    /// Whether <paramref name="list"/> is a mix or radio (<c>RD…</c>, 2026-10-09, the review): YouTube makes one for the viewer as they
    /// watch, and the embedded player cannot load it as a list (the wait for its entry ran to the cap), so it is never played as one.
    /// </summary>
    public static bool IsMix(string? list) => list?.StartsWith("RD", StringComparison.Ordinal) == true;

    /// <summary>Whether <paramref name="text"/> names a mix: its id, or a link whose <c>list=</c> is one.</summary>
    public static bool NamesMix(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (IsMix(text) && VideoRequest.IsPlaylistId(text))
        {
            return true;
        }

        string link = text.Contains("://", StringComparison.Ordinal) ? text : "https://" + text;
        return Uri.TryCreate(link, UriKind.Absolute, out var uri) && IsMix(Query(uri.Query).GetValueOrDefault("list"));
    }

    /// <summary>
    /// A channel's uploads playlist (2026-10-08): YouTube names it after the channel, <c>UC…</c> becoming <c>UU…</c> (24 characters);
    /// null when <paramref name="channelId"/> is no channel id.
    /// </summary>
    public static string? Uploads(string? channelId) =>
        channelId is { Length: 24 } && channelId.StartsWith("UC", StringComparison.Ordinal) && VideoRequest.IsPlaylistId(channelId) ? "UU" + channelId[2..] : null;

    /// <summary>The playlist page of <paramref name="list"/> (2026-10-08), for <c>open_url</c>.</summary>
    public static string PlaylistUrl(string list) => "https://www.youtube.com/playlist?list=" + list;

    /// <summary>
    /// A time as seconds: <c>90</c>, <c>90.5</c>, <c>90s</c>, <c>1m30s</c>, <c>1h2m3s</c>, <c>1:30</c>, <c>1:02:03</c>; null for
    /// anything else (a negative, a minute or second field of 60 or more after a colon).
    /// </summary>
    public static double? ParseTime(string? text)
    {
        text = text?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double plain))
        {
            return plain;
        }

        if (Units().Match(text) is { Success: true } units && units.Length > 0)
        {
            return (Part(units, "h") * 3600) + (Part(units, "m") * 60) + Part(units, "s");
        }

        string[] fields = text.Split(':');
        if (fields.Length is 2 or 3 && fields.All(f => f.Length > 0 && f.All(char.IsAsciiDigit)))
        {
            var numbers = fields.Select(f => int.Parse(f, CultureInfo.InvariantCulture)).ToArray();
            if (numbers.Skip(1).Any(n => n >= 60))
            {
                return null;
            }

            return numbers.Aggregate(0.0, (total, n) => (total * 60) + n);
        }

        return null;
    }

    /// <summary><c>1:30</c>, <c>1:02:03</c>: a position the way a player shows it (whole seconds).</summary>
    public static string FormatTime(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(double.IsFinite(seconds) ? seconds : 0)));
        return span.TotalHours >= 1
            ? ((int)span.TotalHours).ToString(CultureInfo.InvariantCulture) + span.ToString(@"\:mm\:ss", CultureInfo.InvariantCulture)
            : ((int)span.TotalMinutes).ToString(CultureInfo.InvariantCulture) + span.ToString(@"\:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>The watch page of <paramref name="id"/>, for <c>open_url</c> and the hits' links.</summary>
    public static string WatchUrl(string id, double start = 0) =>
        "https://www.youtube.com/watch?v=" + id + (start >= 1 ? "&t=" + ((int)start).ToString(CultureInfo.InvariantCulture) + "s" : "");

    /// <summary>
    /// A playlist's page for a browser (2026-10-16, <c>YouTube open links in</c>'s browsers): <c>watch?v=…&amp;list=…</c> (with its <c>t=</c>)
    /// when a video is named, <c>playlist?list=…</c> from its start, else <c>watch?list=…&amp;index=n</c> at entry <paramref name="entry"/>
    /// (0-based here, 1-based in the link).
    /// </summary>
    public static string PlaylistWatchUrl(string list, int entry, string? video, double start)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (video is not null)
        {
            return WatchUrl(video, start) + "&list=" + list;
        }

        return entry <= 0 ? PlaylistUrl(list) : "https://www.youtube.com/watch?list=" + list + "&index=" + (entry + 1).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The page a browser opens for <paramref name="request"/>: its video's watch page, or its playlist's (<see cref="PlaylistWatchUrl"/>).</summary>
    public static string UrlFor(VideoRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.PlaylistId is { } list ? PlaylistWatchUrl(list, request.Entry, request.VideoId, request.Start) : WatchUrl(request.VideoId!, request.Start);
    }

    /// <summary>
    /// Whether <paramref name="host"/> is one of YouTube's own (2026-10-16): youtube.com on <c>www.</c>, <c>m.</c> or <c>music.</c>,
    /// youtube-nocookie.com and youtu.be; a look-alike (<c>youtube.com.example</c>) is not.
    /// </summary>
    public static bool IsYouTubeHost(string? host)
    {
        string h = (host ?? "").Trim().TrimEnd('.').ToLowerInvariant();
        return Hosts.Contains(h) || h is "youtu.be" or "www.youtu.be";
    }

    /// <summary>
    /// Whether <paramref name="text"/> is something the player plays (2026-10-16, open_url's and the video window's links): a video
    /// <see cref="TryParse"/> reads, or a link whose <c>list=</c> <see cref="TryParsePlaylist"/> reads. A channel's page is a page to look
    /// at, never a playable link here, though the parser reads <c>/channel/UC…</c> as its uploads.
    /// </summary>
    public static bool IsPlayable(string? text)
    {
        if (TryParse(text, out _, out _))
        {
            return true;
        }

        text = text?.Trim();
        if (string.IsNullOrEmpty(text) || VideoRequest.IsPlaylistId(text))
        {
            return false;   // a bare id is never a link
        }

        string link = text.Contains("://", StringComparison.Ordinal) ? text : "https://" + text;
        return Uri.TryCreate(link, UriKind.Absolute, out var uri) && IsYouTubeHost(uri.Host)
            && Query(uri.Query).ContainsKey("list") && TryParsePlaylist(text, out _, out _, out _, out _);
    }

    private static Dictionary<string, string> Query(string query)
    {
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=', StringComparison.Ordinal);
            string key = Uri.UnescapeDataString(equals < 0 ? pair : pair[..equals]);
            pairs.TryAdd(key, equals < 0 ? "" : Uri.UnescapeDataString(pair[(equals + 1)..]));
        }

        return pairs;
    }

    private static double Part(Match match, string group) =>
        match.Groups[group].Success ? double.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture) : 0;

    [GeneratedRegex(@"^(?:(?<h>\d+)h)?(?:(?<m>\d+)m)?(?:(?<s>\d+(?:\.\d+)?)s)?$", RegexOptions.CultureInvariant)]
    private static partial Regex Units();
}
