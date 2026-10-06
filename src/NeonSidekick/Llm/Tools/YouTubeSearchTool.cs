using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Settings;
using NeonSidekick.YouTube;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>youtube_search(query, max?)</c> (2026-10-05, the YouTube plan): videos on YouTube through the Data API with the user's key
/// (<see cref="YouTubeDataApi"/>), each a title, channel, length, views, year and the id <c>youtube_play</c> takes. Only videos that
/// play embedded are listed. Offered while YouTube tools is on and a key is set; headless too. The count is the setting
/// <c>YouTube search max results</c> unless the call names one.
/// </summary>
public sealed class YouTubeSearchTool : AIFunction
{
    public const string ToolName = "youtube_search";
    public const string QueryArgument = "query";
    public const string MaxArgument = "max";

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "query": { "type": "string", "description": "What to look for, as typed into YouTube's search box." },
            "max": { "type": "integer", "description": "How many videos, 1 to 20. Leave it out for the user's default." }
          },
          "required": ["query"]
        }
        """);

    private readonly IYouTubeSearch _search;
    private readonly Func<AppSettingsData> _effective;

    public YouTubeSearchTool(IYouTubeSearch search, Func<AppSettingsData> effective)
    {
        _search = search ?? throw new ArgumentNullException(nameof(search));
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
    }

    public override string Name => ToolName;

    /// <summary>Pinned.</summary>
    public override string Description =>
        "Searches YouTube and lists videos (title, channel, length, views, year and id). Use it when the user wants a video found or played; " +
        "then play the one they want with " + YouTubePlayTool.ToolName + " and its id. Each search spends some of the user's daily YouTube quota, so search once and pick from the list.";

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
        int count = max ?? Math.Clamp(effective.YouTubeSearchMaxResults, AppSettingsData.MinYouTubeSearchMaxResults, AppSettingsData.MaxYouTubeSearchMaxResults);
        if (count < AppSettingsData.MinYouTubeSearchMaxResults || count > AppSettingsData.MaxYouTubeSearchMaxResults)
        {
            return YouTubeText.BadMax;
        }

        var outcome = await _search.SearchAsync(query, count, YouTubeDataApi.Key(effective) ?? "", cancellationToken).ConfigureAwait(false);
        return outcome.Ok ? YouTubeText.Results(query, outcome.Hits.Take(count).ToList()) : YouTubeText.Failure(outcome.Failure, outcome.Detail);
    }
}
