// SPDX-License-Identifier: AGPL-3.0-or-later

namespace SearXNG.Net;

/// <summary>Result filtering asked of an engine. The values match SearXNG's <c>safesearch</c> levels.</summary>
public enum SafeSearch
{
    /// <summary>No filtering.</summary>
    Off = 0,

    /// <summary>Moderate filtering.</summary>
    Moderate = 1,

    /// <summary>Strict filtering.</summary>
    Strict = 2,
}

/// <summary>One search.</summary>
/// <param name="Query">The search terms.</param>
/// <param name="PageNumber">The page wanted, from 1. Engines without paging sit out any later page.</param>
/// <param name="SafeSearch">The filtering level.</param>
/// <param name="Locale"><c>all</c>, a language such as <c>de</c>, or a region such as <c>de-DE</c>.</param>
/// <param name="TimeoutLimit">Caps this search's timeout, as SearXNG's <c>timeout_limit</c> does; null for none.</param>
/// <param name="Engines">The names of the engines to ask, as a SearXNG query's <c>engines</c> does; null for all of them.</param>
public sealed record EngineQuery(
    string Query,
    int PageNumber = 1,
    SafeSearch SafeSearch = SafeSearch.Off,
    string Locale = EngineQuery.AllLocales,
    TimeSpan? TimeoutLimit = null,
    IReadOnlySet<string>? Engines = null)
{
    /// <summary>The locale meaning no preference.</summary>
    public const string AllLocales = "all";
}
