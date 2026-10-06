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
