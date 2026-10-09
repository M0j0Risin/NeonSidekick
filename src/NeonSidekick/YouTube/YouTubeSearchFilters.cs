using System.Globalization;
using NeonSidekick.Settings;

namespace NeonSidekick.YouTube;

/// <summary>
/// What a search asks YouTube for beyond its words (2026-10-08, the user's ask): the kind (videos, playlists or channels), the order,
/// a video's length, how recent, the region and language, live broadcasts only, captioned videos only, and safe search. Length,
/// live and captions filter videos only (<see cref="VideoOnly"/>). <see cref="None"/> is YouTube's plain search; <see cref="Defaults"/>
/// the user's saved choices on <c>/tools</c> › YouTube (order, region, language, safe search). The words for each are the user's
/// (<see cref="Orders"/>, <see cref="Lengths"/>, <see cref="SafeSearches"/>); <see cref="ApiOrder"/> turns <c>views</c> into the API's
/// <c>viewCount</c>.
/// </summary>
public sealed record YouTubeSearchFilters
{
    /// <summary>The orders, the default first.</summary>
    public static readonly IReadOnlyList<string> Orders = ["relevance", "date", "views", "rating", "title"];

    /// <summary>A video's lengths: any, under 4 minutes, 4 to 20, over 20 (the API's own split).</summary>
    public static readonly IReadOnlyList<string> Lengths = ["any", "short", "medium", "long"];

    /// <summary>Safe search's levels; moderate is YouTube's own default.</summary>
    public static readonly IReadOnlyList<string> SafeSearches = ["none", "moderate", "strict"];

    /// <summary>The words <c>since</c> takes besides a date, and how far back each reaches.</summary>
    public static readonly IReadOnlyList<(string Word, TimeSpan Back)> Recent =
    [
        ("today", TimeSpan.FromDays(1)),
        ("week", TimeSpan.FromDays(7)),
        ("month", TimeSpan.FromDays(30)),
        ("year", TimeSpan.FromDays(365)),
    ];

    /// <summary>YouTube's plain search: videos by relevance, nothing filtered, moderate safe search.</summary>
    public static YouTubeSearchFilters None { get; } = new();

    public YouTubeKind Kind { get; init; } = YouTubeKind.Video;

    public string Order { get; init; } = "relevance";

    public string Length { get; init; } = "any";

    /// <summary>Only what was published after this; null for any time.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Two letters, upper case (<c>US</c>); empty for none.</summary>
    public string Region { get; init; } = "";

    /// <summary>A language code, lower case (<c>en</c>, <c>zh-Hans</c>); empty for none.</summary>
    public string Language { get; init; } = "";

    public bool Live { get; init; }

    public bool Captions { get; init; }

    public string SafeSearch { get; init; } = "moderate";

    /// <summary>Whether a filter only videos have is set: length, live or captions.</summary>
    public bool VideoOnly => Length != "any" || Live || Captions;

    /// <summary>The user's saved choices under <paramref name="effective"/>; anything unreadable in them is left at YouTube's own.</summary>
    public static YouTubeSearchFilters Defaults(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return new YouTubeSearchFilters
        {
            Order = Pick(effective.YouTubeSearchOrder, Orders) ?? "relevance",
            Region = NormalizeRegion(effective.YouTubeRegion) ?? "",
            Language = NormalizeLanguage(effective.YouTubeLanguage) ?? "",
            SafeSearch = Pick(effective.YouTubeSafeSearch, SafeSearches) ?? "moderate",
        };
    }

    /// <summary>The API's word for <paramref name="order"/>: <c>views</c> is <c>viewCount</c>; null for none of <see cref="Orders"/>.</summary>
    public static string? ApiOrder(string? order) => Pick(order, Orders) switch
    {
        "views" => "viewCount",
        var known => known,
    };

    /// <summary>A region code: two ASCII letters, upper-cased; empty stays empty; null for anything else.</summary>
    public static string? NormalizeRegion(string? text)
    {
        text = text?.Trim() ?? "";
        return text.Length == 0 ? "" : text.Length == 2 && text.All(char.IsAsciiLetter) ? text.ToUpperInvariant() : null;
    }

    /// <summary>
    /// A language code: two or three ASCII letters, lower-cased, with a script or region after a hyphen kept as written
    /// (<c>zh-Hans</c>, <c>pt-BR</c>); empty stays empty; null for anything else.
    /// </summary>
    public static string? NormalizeLanguage(string? text)
    {
        text = text?.Trim() ?? "";
        if (text.Length == 0)
        {
            return "";
        }

        int dash = text.IndexOf('-', StringComparison.Ordinal);
        string head = dash < 0 ? text : text[..dash];
        string tail = dash < 0 ? "" : text[(dash + 1)..];
        bool ok = head.Length is 2 or 3 && head.All(char.IsAsciiLetter) && (dash < 0 || (tail.Length is >= 2 and <= 4 && tail.All(char.IsAsciiLetterOrDigit)));
        return ok ? head.ToLowerInvariant() + (dash < 0 ? "" : "-" + tail) : null;
    }

    /// <summary>
    /// How recent <paramref name="text"/> means, from <paramref name="now"/>: <c>today</c>, <c>week</c>, <c>month</c>, <c>year</c>, or a
    /// date (<c>2026-01-31</c>, from its start, UTC); null for anything else.
    /// </summary>
    public static DateTimeOffset? ParseSince(string? text, DateTimeOffset now)
    {
        text = text?.Trim().ToLowerInvariant() ?? "";
        foreach (var (word, back) in Recent)
        {
            if (text == word)
            {
                return now.ToUniversalTime() - back;
            }
        }

        return DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
            ? new DateTimeOffset(date, TimeSpan.Zero)
            : null;
    }

    private static string? Pick(string? text, IReadOnlyList<string> words)
    {
        string wanted = text?.Trim().ToLowerInvariant() ?? "";
        return words.FirstOrDefault(w => w == wanted);
    }
}

/// <summary>
/// A search's filters as given (2026-10-08): each null when left out, so the user's saved choice (or YouTube's own) stands. The tool's
/// arguments and <c>/youtube search</c>'s switches both read into one; <see cref="Resolve"/> checks them and lays them over
/// <see cref="YouTubeSearchFilters.Defaults"/>, so the tool, <c>/youtube</c> and headless refuse the same things in the same words.
/// </summary>
public sealed record YouTubeSearchChoices(
    string? Kind = null,
    string? Order = null,
    string? Length = null,
    string? Since = null,
    string? Region = null,
    string? Language = null,
    bool? Live = null,
    bool? Captions = null,
    string? SafeSearch = null)
{
    /// <summary>The kinds as a search names them.</summary>
    public static readonly IReadOnlyList<string> Kinds = ["video", "playlist", "channel"];

    /// <summary>Nothing given: the saved choices alone.</summary>
    public static YouTubeSearchChoices None { get; } = new();

    /// <summary>
    /// The filters these choices make over <paramref name="effective"/>'s saved ones, or the error (led by <c>Error: </c>) for the first
    /// that does not read, or for a video-only filter on a playlist or channel search.
    /// </summary>
    public (YouTubeSearchFilters? Filters, string? Error) Resolve(AppSettingsData effective, DateTimeOffset now)
    {
        var filters = YouTubeSearchFilters.Defaults(effective);
        if (Kind is not null)
        {
            string kind = Kind.Trim().ToLowerInvariant().TrimEnd('s');
            if (!Kinds.Contains(kind))
            {
                return (null, YouTubeText.NotOneOf("kind", Kind, Kinds));
            }

            filters = filters with { Kind = kind switch { "playlist" => YouTubeKind.Playlist, "channel" => YouTubeKind.Channel, _ => YouTubeKind.Video } };
        }

        if (Order is not null)
        {
            if (YouTubeSearchFilters.ApiOrder(Order) is null)
            {
                return (null, YouTubeText.NotOneOf("order", Order, YouTubeSearchFilters.Orders));
            }

            filters = filters with { Order = Order.Trim().ToLowerInvariant() };
        }

        if (Length is not null)
        {
            string length = Length.Trim().ToLowerInvariant();
            if (!YouTubeSearchFilters.Lengths.Contains(length))
            {
                return (null, YouTubeText.NotOneOf("length", Length, YouTubeSearchFilters.Lengths));
            }

            filters = filters with { Length = length };
        }

        if (Since is not null)
        {
            if (YouTubeSearchFilters.ParseSince(Since, now) is not { } since)
            {
                return (null, YouTubeText.NotASince(Since));
            }

            filters = filters with { Since = since };
        }

        if (Region is not null)
        {
            if (YouTubeSearchFilters.NormalizeRegion(Region) is not { } region)
            {
                return (null, YouTubeText.NotARegion(Region));
            }

            filters = filters with { Region = region };
        }

        if (Language is not null)
        {
            if (YouTubeSearchFilters.NormalizeLanguage(Language) is not { } language)
            {
                return (null, YouTubeText.NotALanguage(Language));
            }

            filters = filters with { Language = language };
        }

        if (SafeSearch is not null)
        {
            string safe = SafeSearch.Trim().ToLowerInvariant();
            if (!YouTubeSearchFilters.SafeSearches.Contains(safe))
            {
                return (null, YouTubeText.NotOneOf("safe_search", SafeSearch, YouTubeSearchFilters.SafeSearches));
            }

            filters = filters with { SafeSearch = safe };
        }

        filters = filters with { Live = Live ?? false, Captions = Captions ?? false };
        return filters.Kind != YouTubeKind.Video && filters.VideoOnly ? (null, YouTubeText.VideoOnlyFilter) : (filters, null);
    }
}
