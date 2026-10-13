using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.Web;

/// <summary>How <c>web_fetch</c> gets a page (<see cref="WebFetcher.FetchAsync"/>).</summary>
public enum FetchEngine
{
    /// <summary>The HTTP client first; the headless browser when the page came back blocked or as a script shell.</summary>
    Default,

    /// <summary>The HTTP client alone.</summary>
    HttpClient,

    /// <summary>The headless browser for every page.</summary>
    Chromium,
}

/// <summary>
/// The setting <c>Web browser mode</c>: the three words the operator picks from (<c>auto</c>,
/// <c>http</c>, <c>browser</c>) and their mapping to <see cref="FetchEngine"/>, the
/// <see cref="Llm.LlmScanMode"/> shape. <see cref="Resolve"/> is the one place the saved string
/// becomes the enum: a hand-edited value that is none of them falls back to <see cref="Default"/>
/// with a warning.
/// </summary>
public static class BrowserMode
{
    /// <summary>The client, then the browser when needed. The compiled default, pinned by <c>AppSettingsTests</c>.</summary>
    public const string Default = "auto";

    /// <summary>The modes in menu order.</summary>
    public static readonly string[] Names = { "auto", "http", "browser" };

    /// <summary>The words retired on 2026-10-13 (the settings verbiage pass, the user's call), still read: old → new (<see cref="RetiredWords"/>).</summary>
    internal static readonly IReadOnlyDictionary<string, string> Retired = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["default"] = "auto",
        ["httpclient"] = "http",
        ["chromium"] = "browser",
    };

    private const string Category = "Web";

    /// <summary>Trims and ignores case; false (and <see cref="FetchEngine.Default"/>) for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out FetchEngine engine)
    {
        switch (RetiredWords.Current(text, Retired))
        {
            case "auto": engine = FetchEngine.Default; return true;
            case "http": engine = FetchEngine.HttpClient; return true;
            case "browser": engine = FetchEngine.Chromium; return true;
            default: engine = FetchEngine.Default; return false;
        }
    }

    /// <summary>The saved word for <paramref name="engine"/>.</summary>
    public static string Name(FetchEngine engine) => engine switch
    {
        FetchEngine.HttpClient => "http",
        FetchEngine.Chromium => "browser",
        _ => "auto",
    };

    /// <summary>
    /// The mode after <paramref name="mode"/> in <see cref="Names"/>' order, the last wrapping to the first (later on 2026-10-13, the Web
    /// tools page's one cycling button); a value that is none of them goes to the first, <see cref="Default"/>. Pure; pinned.
    /// </summary>
    public static string Next(string? mode)
    {
        if (!TryParse(mode, out var engine))
        {
            return Names[0];
        }

        int at = Array.IndexOf(Names, Name(engine));
        return Names[(at + 1) % Names.Length];
    }

    /// <summary>The menu hint next to a mode. Pinned.</summary>
    public static string Describe(string name) => name switch
    {
        "auto" => "the HTTP client, then a headless browser when a page is blocked or empty",
        "http" => "the HTTP client alone, with browser-like headers",
        "browser" => "a headless Edge, Chrome or Brave for every page",
        _ => "",
    };

    /// <summary>The engine in force for <paramref name="effective"/>; an unknown saved value warns and uses <see cref="Default"/>.</summary>
    public static FetchEngine Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.WebBrowserMode, out var engine))
        {
            return engine;
        }

        DiagnosticLog.Warn(Category,
            $"{nameof(AppSettingsData.WebBrowserMode)}='{effective.WebBrowserMode}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        TryParse(Default, out engine);
        return engine;
    }
}
