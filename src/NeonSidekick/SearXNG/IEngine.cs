// SPDX-License-Identifier: AGPL-3.0-or-later

namespace SearXNG.Net;

/// <summary>One hit as an engine reported it, before merging.</summary>
/// <param name="Url">The target address.</param>
/// <param name="Title">The hit's title.</param>
/// <param name="Content">The snippet; empty when the engine gave none.</param>
public sealed record EngineResult(string Url, string Title, string Content);

/// <summary>
/// A search engine. Replaces SearXNG's runtime-loaded Python modules, which Native AOT can't load: the set of
/// engines is whatever the host compiles in and passes to <see cref="MetaSearch"/>.
/// </summary>
public interface IEngine
{
    /// <summary>The engine's name, unique among those in one <see cref="MetaSearch"/>.</summary>
    string Name { get; }

    /// <summary>The engine's first SearXNG category, which decides how results group when ordered.</summary>
    string Category { get; }

    /// <summary>Multiplies the score of this engine's results. SearXNG's default is 1.</summary>
    double Weight { get; }

    /// <summary>How long this engine may take. The search allows the longest timeout among its engines.</summary>
    TimeSpan Timeout { get; }

    /// <summary>Whether the engine can answer for a page after the first.</summary>
    bool Paging { get; }

    /// <summary>The engine's hits in its own order. Empty when it has nothing, or can't serve this query.</summary>
    /// <exception cref="EngineException">The engine was refused or its answer was unusable.</exception>
    Task<IReadOnlyList<EngineResult>> SearchAsync(EngineQuery query, HttpClient http, CancellationToken cancellationToken);
}
