using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.Web;

/// <summary>Which engine <c>web_search</c> asks (<see cref="WebAccess.Engine"/>).</summary>
public enum SearchProvider
{
    /// <summary>The built-in DuckDuckGo scrape (<see cref="DuckDuckGoSearch"/>).</summary>
    DuckDuckGo,

    /// <summary>The SearXNG instance the setting <c>Web SearXNG URL</c> names (<see cref="SearxngSearch"/>).</summary>
    Searxng,

    /// <summary>The built-in metasearch, with no server to run (<see cref="BuiltInMetaSearch"/>).</summary>
    MetaSearch,
}

/// <summary>
/// The setting <c>Browser search method</c>: the words the operator picks from (<c>metasearch</c>,
/// <c>duckduckgo</c>, <c>searxng</c>) and their mapping to <see cref="SearchProvider"/>, the <see cref="BrowserMode"/>
/// shape. The URL alone chose the engine until 2026-09-15 (the user's call): now <c>Web SearXNG URL</c>
/// keeps its value while this row switches the engine, and it is read only under <c>searxng</c>.
/// <see cref="Resolve"/> is the one place the saved string becomes the enum: a hand-edited value that
/// is none of them falls back to <see cref="Default"/> with a warning.
/// </summary>
public static class SearchMethod
{
    /// <summary>The built-in metasearch (2026-11-06, the user's call; DuckDuckGo before). The compiled default, pinned by <c>AppSettingsTests</c>. A profile keeps the method it saved.</summary>
    public const string Default = "metasearch";

    /// <summary>The methods in menu order.</summary>
    public static readonly string[] Names = { "metasearch", "duckduckgo", "searxng" };

    private const string Category = "Web";

    /// <summary>Trims and ignores case; false (and <see cref="SearchProvider.DuckDuckGo"/>) for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out SearchProvider provider)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "duckduckgo": provider = SearchProvider.DuckDuckGo; return true;
            case "searxng": provider = SearchProvider.Searxng; return true;
            case "metasearch": provider = SearchProvider.MetaSearch; return true;
            default: provider = SearchProvider.DuckDuckGo; return false;
        }
    }

    /// <summary>The saved word for <paramref name="provider"/>.</summary>
    public static string Name(SearchProvider provider) => provider switch
    {
        SearchProvider.Searxng => "searxng",
        SearchProvider.MetaSearch => "metasearch",
        _ => "duckduckgo",
    };

    /// <summary>The menu hint next to a method (the user's wording, 2026-11-07). Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        "duckduckgo" => "built-in DuckDuckGo scrape, no setup",
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
        TryParse(Default, out provider);
        return provider;
    }
}
