using System.Text.Json;
using Microsoft.Extensions.AI;
using NeonSidekick.Settings;
using NeonSidekick.Web;

namespace NeonSidekick.Llm.Tools;

/// <summary>
/// <c>web_search(query, max_results?)</c>: the top hits from the search engine the settings name
/// (<see cref="WebAccess.Engine"/>), each a title, a URL and a snippet. The result starts with a
/// header line, so the transcript's one-line note reads <c>Searched "…" (8 results, metasearch):</c>.
/// The count is the setting <c>Web search max results</c> unless the call names one, up to
/// <see cref="MaxResults"/>. No engine opens a bot check since the DuckDuckGo scrape went (2026-11-07): the metasearch skips an engine
/// that refuses.
/// </summary>
public sealed class WebSearchTool : AIFunction
{
    public const string ToolName = "web_search";
    public const string QueryArgument = "query";
    public const string MaxResultsArgument = "max_results";

    public const string SinceArgument = "since";

    public const int MinResults = AppSettingsData.MinWebSearchMaxResults;
    public const int MaxResults = AppSettingsData.MaxWebSearchMaxResults;

    private static readonly JsonElement Schema = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "query": { "type": "string", "description": "What to search for, as you would type it into a search engine." },
            "max_results": { "type": "integer", "description": "How many results to return, 1 to 20. Leave it out for the user's default." },
            "since": { "type": "string", "enum": ["day", "week", "month", "year"], "description": "Keeps only results from the past day, week, month or year. Fewer engines answer with it." }
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
    public Task<string> SearchAsync(string query, int? maxResults, CancellationToken cancellationToken) => SearchAsync(query, maxResults, null, cancellationToken);

    /// <summary>
    /// <c>since</c> as a range (2026-11-15, the web-tools plan's §4, feature idea #25): the four words trimmed and in any case, blank as not given (null, no error); anything else an
    /// error.
    /// </summary>
    public static (SearchSince? Since, string? Error) ParseSince(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        return raw.Trim().ToLowerInvariant() switch
        {
            "" => (null, null),
            "day" => (SearchSince.Day, null),
            "week" => (SearchSince.Week, null),
            "month" => (SearchSince.Month, null),
            "year" => (SearchSince.Year, null),
            _ => (null, WebText.BadSince(raw)),
        };
    }

    /// <summary>The search, results kept to <paramref name="since"/> when given.</summary>
    public async Task<string> SearchAsync(string query, int? maxResults, SearchSince? since, CancellationToken cancellationToken)
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
        var options = WebAccess.Options(effective);
        var outcome = await engine.SearchAsync(query.Trim(), count, options, cancellationToken, since).ConfigureAwait(false);

        return outcome.Ok ? WebText.Results(query.Trim(), outcome.Results, engine.Name, since, outcome.Scope) : outcome.Error;
    }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!ToolArguments.TryReadInt32(arguments, MaxResultsArgument, out var max, out var raw))
        {
            return ClockText.BadInteger(MaxResultsArgument, raw);
        }

        var (since, sinceError) = ParseSince(ToolArguments.ReadString(arguments, SinceArgument));
        if (sinceError is not null)
        {
            return sinceError;
        }

        return await SearchAsync(ToolArguments.ReadString(arguments, QueryArgument), max, since, cancellationToken).ConfigureAwait(false);
    }
}
