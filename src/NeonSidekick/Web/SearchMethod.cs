using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.Web;

/// <summary>Which engine <c>web_search</c> asks (<see cref="WebAccess.Engine"/>).</summary>
public enum SearchProvider
{
    /// <summary>The SearXNG instance the setting <c>Web SearXNG URL</c> names (<see cref="SearxngSearch"/>).</summary>
    Searxng,

    /// <summary>The built-in metasearch, with no server to run (<see cref="BuiltInMetaSearch"/>).</summary>
    MetaSearch,
}

/// <summary>
/// The setting <c>Web search method</c>: the words the operator picks from (<c>metasearch</c>, <c>searxng</c>) and their mapping to
/// <see cref="SearchProvider"/>, the <see cref="BrowserMode"/> shape. The built-in DuckDuckGo scrape, <c>duckduckgo</c>, went on
/// 2026-11-07 (the user's call, once a published build had proven the metasearch, which asks DuckDuckGo among its engines): the word
/// is retired to <c>metasearch</c> (<see cref="Retired"/>), so a profile that saved it searches with the metasearch and saves that. The URL alone chose the engine until 2026-09-15 (the user's call): now <c>Web SearXNG URL</c>
/// keeps its value while this row switches the engine, and it is read only under <c>searxng</c>.
/// <see cref="Resolve"/> is the one place the saved string becomes the enum: a hand-edited value that
/// is none of them falls back to <see cref="Default"/> with a warning.
/// </summary>
public static class SearchMethod
{
    /// <summary>The built-in metasearch (2026-11-06, the user's call; DuckDuckGo before). The compiled default, pinned by <c>AppSettingsTests</c>. A profile keeps the method it saved.</summary>
    public const string Default = "metasearch";

    /// <summary>The methods in menu order.</summary>
    public static readonly string[] Names = { "metasearch", "searxng" };

    /// <summary>Retired words and their successors (<see cref="Settings.RetiredWords"/>): the DuckDuckGo scrape's, 2026-11-07.</summary>
    public static readonly IReadOnlyDictionary<string, string> Retired = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["duckduckgo"] = "metasearch",
    };

    private const string Category = "Web";

    /// <summary>Trims and ignores case, a retired word read as its successor; false (and <see cref="SearchProvider.MetaSearch"/>) for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out SearchProvider provider)
    {
        switch (RetiredWords.Current(text, Retired))
        {
            case "searxng": provider = SearchProvider.Searxng; return true;
            case "metasearch": provider = SearchProvider.MetaSearch; return true;
            default: provider = SearchProvider.MetaSearch; return false;
        }
    }

    /// <summary>The saved word for <paramref name="provider"/>.</summary>
    public static string Name(SearchProvider provider) => provider switch
    {
        SearchProvider.Searxng => "searxng",
        _ => "metasearch",
    };

    /// <summary>The menu hint next to a method (the user's wording, 2026-11-07). Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        "searxng" => "external SearXNG instance at Web SearXNG URL",
        "metasearch" => "built-in SearXNG port, no setup",
        _ => "",
    };

    /// <summary>The provider in force for <paramref name="effective"/>; an unknown saved value warns and uses <see cref="Default"/>.</summary>
    public static SearchProvider Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.WebSearchMethod, out var provider))
        {
            return provider;
        }

        DiagnosticLog.Warn(Category,
            $"{nameof(AppSettingsData.WebSearchMethod)}='{effective.WebSearchMethod}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        return SearchProvider.MetaSearch;
    }
}
