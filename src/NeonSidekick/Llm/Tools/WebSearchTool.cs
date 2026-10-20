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
/// <para>
/// Since 2026-10-13 DuckDuckGo's bot check (<see cref="SearchOutcome.Gate"/>) can be shown to the user in the Browser window
/// (<see cref="BotCheckSolver"/>, per <c>Web browser botcheck window</c>): the results page they let through is read for its hits, its
/// cookies kept for the next search, which is asked once more when that page had none.
/// </para>
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
    private readonly BotCheckSolver? _botCheck;

    /// <param name="botCheck">Who shows a bot check to the user (2026-10-13); null where nobody can be asked (headless).</param>
    public WebSearchTool(WebAccess web, Func<AppSettingsData> effective, BotCheckSolver? botCheck = null)
    {
        _web = web ?? throw new ArgumentNullException(nameof(web));
        _effective = effective ?? throw new ArgumentNullException(nameof(effective));
        _botCheck = botCheck;
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
        var options = WebAccess.Options(effective);
        var outcome = await engine.SearchAsync(query.Trim(), count, options, cancellationToken).ConfigureAwait(false);
        if (!outcome.Ok && outcome.Gate is { } gate && _botCheck is not null && engine is DuckDuckGoSearch duckDuckGo)
        {
            outcome = await PastBotCheckAsync(duckDuckGo, query.Trim(), count, gate, options, cancellationToken).ConfigureAwait(false);
        }

        if (outcome.Ok && engine.ResultReferer is { } referer)
        {
            _web.Referers.Remember(outcome.Results, referer);
        }

        return outcome.Ok ? WebText.Results(query.Trim(), outcome.Results, engine.Name) : outcome.Error;
    }

    /// <summary>
    /// DuckDuckGo's bot check shown to the user (2026-10-13): cleared, its cookies go to the client and the page the window got is read
    /// for its hits — the search asked once more when it had none; any other ending is the refusal with what happened added.
    /// </summary>
    private async Task<SearchOutcome> PastBotCheckAsync(DuckDuckGoSearch engine, string query, int count, BotCheckRequest gate, FetchOptions options, CancellationToken cancellationToken)
    {
        var solved = await _botCheck!(gate, cancellationToken).ConfigureAwait(false);
        if (solved is null)
        {
            return SearchOutcome.Failed(WebText.SearchRefused(engine.Name, gate.Why));
        }

        if (!solved.Cleared)
        {
            return SearchOutcome.Failed(WebText.SearchRefused(engine.Name, gate.Why + BotCheckText.For(solved)));
        }

        // Where it ended judged as the other tools' hand-offs are (2026-10-20, the code review: a redirect, or an address typed in the bar,
        // put another site's cookies in the jar and its page in the results).
        if (await _web.Fetcher.SolvedElsewhereAsync(gate.Url, solved, options, cancellationToken).ConfigureAwait(false) is { } elsewhere)
        {
            return SearchOutcome.Failed(WebText.SearchRefused(engine.Name, gate.Why + elsewhere));
        }

        _web.Fetcher.Adopt(solved.Cookies, solved.Identity, gate.Url);
        var read = engine.Outcome(query, count, solved.Html ?? "", WebText.WindowEngine, solved.Status);
        if (read.Ok && read.Results.Count > 0)
        {
            return read;
        }

        var again = await engine.SearchAsync(query, count, options, cancellationToken).ConfigureAwait(false);
        return again.Ok ? again : again with { Gate = null };
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
