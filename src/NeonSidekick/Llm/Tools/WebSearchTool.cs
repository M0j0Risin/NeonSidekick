using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Settings;
using NeonSidekick.Web;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>web_search(query, max_results?)</c>: the top hits from the search engine the settings name
/// (<see cref="WebAccess.Engine"/>), each a title, a URL and a snippet. The result starts with a
/// header line, so the transcript's one-line note reads <c>Searched "…" (8 results, DuckDuckGo):</c>.
/// The count is the setting <c>Web search max results</c> unless the call names one, up to
/// <see cref="MaxResults"/>.
/// </summary>
public sealed class WebSearchTool : AIFunction
{
    public const string ToolName = "web_search";
    public const string QueryArgument = "query";
    public const string MaxResultsArgument = "max_results";

    public const int MinResults = AppSettingsData.MinWebSearchMaxResults;
    public const int MaxResults = AppSettingsData.MaxWebSearchMaxResults;

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "query": { "type": "string", "description": "What to search for, as you would type it into a search engine." },
            "max_results": { "type": "integer", "description": "How many results to return, 1 to 20. Leave it out for the user's default." }
          },
          "required": ["query"]
        }
        """);

    private readonly WebAccess _web;
    private readonly Func<AppSettingsData> _effective;

    public WebSearchTool(WebAccess web, Func<AppSettingsData> effective)
    {
        _web = web ?? throw new ArgumentNullException(nameof(web));
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
    }

    public override string Name => ToolName;

    public override string Description =>
        "Searches the web and returns the top results (title, URL, snippet). Use it for current events, prices, documentation, " +
        "anything after your training data or anything you are not sure of; then read a promising result with " + WebFetchTool.ToolName + ".";

    public override JsonElement JsonSchema => Schema;

    /// <summary>The count a call without <c>max_results</c> gets: the setting, clamped to the range a hand-edited value may have left.</summary>
    public static int DefaultCount(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return Math.Clamp(effective.WebSearchMaxResults, MinResults, MaxResults);
    }

    /// <summary>The search itself, apart from the argument reading: the result text the model reads.</summary>
    public async Task<string> SearchAsync(string query, int? maxResults, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query))
        {
            return WebText.NoQuery;
        }

        var effective = _effective();
        int count = maxResults ?? DefaultCount(effective);
        if (count < MinResults || count > MaxResults)
        {
            return WebText.BadResultCount;
        }

        var engine = _web.Engine(effective);
        var outcome = await engine.SearchAsync(query.Trim(), count, WebAccess.Options(effective), cancellationToken).ConfigureAwait(false);
        if (outcome.Ok && engine.ResultReferer is { } referer)
        {
            _web.Referers.Remember(outcome.Results, referer);
        }

        return outcome.Ok ? WebText.Results(query.Trim(), outcome.Results, engine.Name) : outcome.Error;
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!ToolArguments.TryReadInt32(arguments, MaxResultsArgument, out var max, out var raw))
        {
            return ClockText.BadInteger(MaxResultsArgument, raw);
        }

        return await SearchAsync(ToolArguments.ReadString(arguments, QueryArgument), max, cancellationToken).ConfigureAwait(false);
    }
}
