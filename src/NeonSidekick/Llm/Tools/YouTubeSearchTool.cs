using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Files;
using NeonSidekick.Settings;
using NeonSidekick.YouTube;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>youtube_search(query, max?, …)</c> (2026-10-05, the YouTube plan): videos on YouTube through the Data API with the user's key
/// (<see cref="YouTubeDataApi"/>), each a title, channel, length, views, year and the id <c>youtube_play</c> takes. Only videos that
/// play embedded are listed. Offered while YouTube tools is on and a key is set; headless too. The count is the setting
/// <c>YouTube search max results</c> unless the call names one. Since 2026-10-08 (the user's ask) it also finds playlists and channels
/// (<c>kind</c>) and takes YouTube's filters — order, length, since, region, language, live, captions, safe search — each left out
/// meaning the user's saved choice (<see cref="YouTubeSearchFilters.Defaults"/>) or YouTube's own; <see cref="YouTubeSearchChoices"/>
/// checks them as <c>/youtube search</c>'s switches are checked.
/// </summary>
public sealed class YouTubeSearchTool : AIFunction
{
    public const string ToolName = "youtube_search";
    public const string QueryArgument = "query";
    public const string MaxArgument = "max";
    public const string KindArgument = "kind";
    public const string OrderArgument = "order";
    public const string LengthArgument = "length";
    public const string SinceArgument = "since";
    public const string RegionArgument = "region";
    public const string LanguageArgument = "language";
    public const string LiveArgument = "live";
    public const string CaptionsArgument = "captions";
    public const string SafeSearchArgument = "safe_search";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "query": { "type": "string", "description": "What to look for, as typed into YouTube's search box." },
            "max": { "type": "integer", "description": "How many results, 1 to 20. Leave it out for the user's default." },
            "kind": { "type": "string", "enum": ["video", "playlist", "channel"], "description": "What to find: videos (the default), playlists, or channels." },
            "order": { "type": "string", "enum": ["relevance", "date", "views", "rating", "title"], "description": "The order. Leave it out for the user's default." },
            "length": { "type": "string", "enum": ["any", "short", "medium", "long"], "description": "Videos only: short is under 4 minutes, medium 4 to 20, long over 20." },
            "since": { "type": "string", "description": "Only what was published since: today, week, month, year, or a date (2026-01-31)." },
            "region": { "type": "string", "description": "A two-letter country code (US, GB). Leave it out for the user's default." },
            "language": { "type": "string", "description": "A language code (en, es) whose results rank higher. Leave it out for the user's default." },
            "live": { "type": "boolean", "description": "Videos only: true lists only live broadcasts on air now." },
            "captions": { "type": "boolean", "description": "Videos only: true lists only videos with captions." },
            "safe_search": { "type": "string", "enum": ["none", "moderate", "strict"], "description": "How much restricted content to leave out. Leave it out for the user's default." }
          },
          "required": ["query"]
        }
        """);

    private readonly IYouTubeSearch _search;
    private readonly Func<AppSettingsData> _effective;
    private readonly TimeProvider _time;

    public YouTubeSearchTool(IYouTubeSearch search, Func<AppSettingsData> effective, TimeProvider? time = null)
    {
        _search = search ?? throw new ArgumentNullException(nameof(search));
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
        _time = time ?? TimeProvider.System;
    }

    public override string Name => ToolName;

    /// <summary>Pinned.</summary>
    public override string Description =>
        "Searches YouTube and lists videos (title, channel, length, views, year and id), or playlists or channels (kind). Use it when the user wants a video found or played; " +
        "then play the one they want with " + YouTubePlayTool.ToolName + " and its id (a playlist's or a channel's as its playlist). " +
        "Filters (order, length, since, region, language, live, captions, safe_search) are for when the user asks for them. " +
        "Each search spends some of the user's daily YouTube quota, so search once and pick from the list.";

    public override JsonElement JsonSchema => Schema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!ToolArguments.TryReadInt32(arguments, MaxArgument, out var max, out var raw))
        {
            return ClockText.BadInteger(MaxArgument, raw);
        }

        string query = ToolArguments.ReadString(arguments, QueryArgument).Trim();
        if (query.Length == 0)
        {
            return YouTubeText.EmptyQuery;
        }

        var effective = _effective();
        int count = max ?? YouTubeDataApi.DefaultCount(effective);
        if (count < AppSettingsData.MinYouTubeSearchMaxResults || count > AppSettingsData.MaxYouTubeSearchMaxResults)
        {
            return YouTubeText.BadMax;
        }

        if (!ToolArguments.TryReadBoolean(arguments, LiveArgument, out bool? live, out raw))
        {
            return FileText.BadBoolean(LiveArgument, raw);
        }

        if (!ToolArguments.TryReadBoolean(arguments, CaptionsArgument, out bool? captions, out raw))
        {
            return FileText.BadBoolean(CaptionsArgument, raw);
        }

        var choices = new YouTubeSearchChoices(
            Optional(arguments, KindArgument),
            Optional(arguments, OrderArgument),
            Optional(arguments, LengthArgument),
            Optional(arguments, SinceArgument),
            Optional(arguments, RegionArgument),
            Optional(arguments, LanguageArgument),
            live,
            captions,
            Optional(arguments, SafeSearchArgument));
        var (filters, error) = choices.Resolve(effective, _time.GetUtcNow());
        if (filters is null)
        {
            return error!;
        }

        var outcome = await _search.SearchAsync(query, count, filters, YouTubeDataApi.Key(effective) ?? "", cancellationToken).ConfigureAwait(false);
        return outcome.Ok ? YouTubeText.Results(query, outcome.Hits.Take(count).ToList(), filters.Kind) : YouTubeText.Failure(outcome.Failure, outcome.Detail);
    }

    // An optional word: null when left out or empty, so the saved choice stands.
    private static string? Optional(AIFunctionArguments arguments, string name) =>
        ToolArguments.ReadString(arguments, name).Trim() is { Length: > 0 } text ? text : null;
}
