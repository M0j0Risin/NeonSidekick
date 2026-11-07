// SPDX-License-Identifier: AGPL-3.0-or-later

namespace SearXNG.Net.Results;

/// <summary>A hit after merging: one address, with everything the engines that found it said about it.</summary>
public sealed class MergedResult
{
    private readonly List<int> _positions = [];
    private readonly HashSet<string> _engines = new(StringComparer.Ordinal);

    internal MergedResult(string engine, ParsedUrl url, string title, string content)
    {
        FirstEngine = engine;
        Parsed = url;
        Url = url.GetUrl();
        Title = title;
        Content = content;
    }

    /// <summary>The target address, using a secure scheme if any engine reported one.</summary>
    public string Url { get; private set; }

    /// <summary>The longest title any engine gave.</summary>
    public string Title { get; private set; }

    /// <summary>The longest snippet any engine gave.</summary>
    public string Content { get; private set; }

    /// <summary>The ranking score: higher is better.</summary>
    public double Score { get; internal set; }

    /// <summary>The engines that found this address.</summary>
    public IReadOnlyCollection<string> Engines => _engines;

    /// <summary>This address's positions in the engines' lists, from 1.</summary>
    public IReadOnlyList<int> Positions => _positions;

    internal string FirstEngine { get; }

    internal ParsedUrl Parsed { get; private set; }

    internal void AddSighting(string engine, int position)
    {
        _engines.Add(engine);
        _positions.Add(position);
    }

    /// <summary>SearXNG's <c>merge_two_main_results</c>.</summary>
    internal void MergeFrom(string otherTitle, string otherContent, ParsedUrl otherUrl)
    {
        if (ResultNormalizer.CodePoints(otherContent) > ResultNormalizer.CodePoints(Content))
        {
            Content = otherContent;
        }

        if (ResultNormalizer.CodePoints(otherTitle) > ResultNormalizer.CodePoints(Title))
        {
            Title = otherTitle;
        }

        if (!Parsed.Scheme.EndsWith('s') && otherUrl.Scheme.EndsWith('s'))
        {
            Parsed = Parsed with { Scheme = otherUrl.Scheme };
            Url = Parsed.GetUrl();
        }
    }
}

/// <summary>
/// Collects engines' hits, merges duplicates and orders the outcome: a port of the main-result path of SearXNG's
/// <c>searx/results.py</c>. Answers, infoboxes and plugin priorities aren't ported, as no engine here produces them.
/// </summary>
internal sealed class ResultContainer
{
    // Every result here shares SearXNG's default template and has no image, so only the category tells groups apart.
    private const string TemplateAndImage = ":default.html:";
    private const int MaxGroupCount = 8;
    private const int MaxGroupDistance = 20;

    private readonly IReadOnlyDictionary<string, IEngine> _engines;
    private readonly Dictionary<string, MergedResult> _byKey = new(StringComparer.Ordinal);
    private readonly List<MergedResult> _inOrder = [];

    public ResultContainer(IReadOnlyDictionary<string, IEngine> engines) => _engines = engines;

    /// <summary>Adds one engine's hits, in that engine's order.</summary>
    public void Extend(string engine, IEnumerable<EngineResult> results)
    {
        int position = 0;
        foreach (var result in results)
        {
            var url = ResultNormalizer.NormalizeUrl(result.Url);
            if (url is null)
            {
                continue;
            }

            var title = ResultNormalizer.NormalizeText(result.Title, ResultNormalizer.MaximumTitleLength);
            var content = ResultNormalizer.NormalizeText(result.Content, ResultNormalizer.MaximumContentLength);
            if (content == title)
            {
                content = "";
            }

            position++;

            // Equal when everything but the scheme is equal, so http and https sightings of a page merge.
            var key = string.Join('|', url.Netloc, url.Path, url.Params, url.Query, url.Fragment);
            if (_byKey.TryGetValue(key, out var merged))
            {
                merged.MergeFrom(title, content, url);
            }
            else
            {
                merged = new MergedResult(engine, url, title, content);
                _byKey[key] = merged;
                _inOrder.Add(merged);
            }

            merged.AddSighting(engine, position);
        }
    }

    /// <summary>The merged results, best first: <c>get_ordered_results</c>'s score sort, then its grouping by category.</summary>
    public IReadOnlyList<MergedResult> GetOrderedResults()
    {
        foreach (var result in _inOrder)
        {
            result.Score = CalculateScore(result);
        }

        // OrderByDescending is stable, as Python's sorted(reverse=True) is.
        var grouped = new List<MergedResult>(_inOrder.Count);
        var groups = new Dictionary<string, Group>(StringComparer.Ordinal);
        foreach (var result in _inOrder.OrderByDescending(r => r.Score))
        {
            var category = (_engines.TryGetValue(result.FirstEngine, out var engine) ? engine.Category : "") + TemplateAndImage;
            if (groups.TryGetValue(category, out var group) && group.Count > 0 && grouped.Count - group.Index < MaxGroupDistance)
            {
                // Join the earlier results of this category, and shift every group at or past the insertion point.
                int index = group.Index;
                grouped.Insert(index, result);
                foreach (var other in groups.Values)
                {
                    if (other.Index >= index)
                    {
                        other.Index++;
                    }
                }

                group.Count--;
            }
            else
            {
                grouped.Add(result);
                groups[category] = new Group { Index = grouped.Count, Count = MaxGroupCount };
            }
        }

        return grouped;
    }

    /// <summary>SearXNG's <c>calculate_score</c>: the engines' weights times the sightings, divided by each position in turn.</summary>
    private double CalculateScore(MergedResult result)
    {
        double weight = 1.0;
        foreach (var name in result.Engines)
        {
            if (_engines.TryGetValue(name, out var engine))
            {
                weight *= engine.Weight;
            }
        }

        weight *= result.Positions.Count;
        return result.Positions.Sum(position => weight / position);
    }

    private sealed class Group
    {
        public int Index { get; set; }

        public int Count { get; set; }
    }
}
