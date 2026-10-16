using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.YouTube;

/// <summary>Where a YouTube video goes (<see cref="YouTubeOpenLinksIn"/>).</summary>
public enum YouTubeOpenLinksInKind
{
    /// <summary>The app's own video window, as before the setting.</summary>
    YouTubeWindow,

    /// <summary>The app's Browser window, one page at a time.</summary>
    BrowserWindow,

    /// <summary>The user's default browser, through the shell.</summary>
    DefaultBrowser,

    /// <summary>The Web UI's YouTube tab, on the user's phone or browser.</summary>
    WebUiWindow,
}

/// <summary>
/// The setting <c>YouTube open links in</c> (2026-10-16, the user's ask and words): <c>youtube-window</c>, <c>browser-window</c>,
/// <c>default-browser</c> or <c>webui-window</c>, the <see cref="Web.OpenLinksIn"/> shape. It moves every play (youtube_play,
/// <c>/youtube play</c> and its pickers), <c>open_url</c> on a YouTube video or playlist link while YouTube tools are on and a player
/// exists, and the video or playlist links the video window opens. <c>webui-window</c> falls back to the video window while the Web UI
/// isn't serving, <c>browser-window</c> to the default browser where there is no Browser window. <see cref="Resolve"/> is the one place
/// the saved string becomes the enum: a hand-edited value that is none of them reads as <see cref="Default"/>, with a warning once. Pure; pinned.
/// </summary>
public static class YouTubeOpenLinksIn
{
    public const string YouTubeWindow = "youtube-window";
    public const string BrowserWindow = "browser-window";
    public const string DefaultBrowser = "default-browser";
    public const string WebUiWindow = "webui-window";

    /// <summary>The video window, as before the setting: the compiled default, pinned by <c>AppSettingsTests</c>.</summary>
    public const string Default = YouTubeWindow;

    /// <summary>The choices in menu order.</summary>
    public static readonly string[] Names = { YouTubeWindow, BrowserWindow, DefaultBrowser, WebUiWindow };

    private const string Category = "YouTube";

    /// <summary>The unknown value last warned of, so <see cref="Resolve"/> warns once per value (it is read at every play).</summary>
    private static string? s_warned;

    /// <summary>Trims and ignores case; false (and <see cref="YouTubeOpenLinksInKind.YouTubeWindow"/>) for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out YouTubeOpenLinksInKind kind)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case YouTubeWindow: kind = YouTubeOpenLinksInKind.YouTubeWindow; return true;
            case BrowserWindow: kind = YouTubeOpenLinksInKind.BrowserWindow; return true;
            case DefaultBrowser: kind = YouTubeOpenLinksInKind.DefaultBrowser; return true;
            case WebUiWindow: kind = YouTubeOpenLinksInKind.WebUiWindow; return true;
            default: kind = YouTubeOpenLinksInKind.YouTubeWindow; return false;
        }
    }

    /// <summary>The saved word for <paramref name="kind"/>.</summary>
    public static string Name(YouTubeOpenLinksInKind kind) => kind switch
    {
        YouTubeOpenLinksInKind.BrowserWindow => BrowserWindow,
        YouTubeOpenLinksInKind.DefaultBrowser => DefaultBrowser,
        YouTubeOpenLinksInKind.WebUiWindow => WebUiWindow,
        _ => YouTubeWindow,
    };

    /// <summary>The menu hint next to a choice.</summary>
    public static string Describe(string name) => name switch
    {
        YouTubeWindow => "videos play in the app's video window",
        BrowserWindow => "videos open in the app's Browser window, out of the model's control",
        DefaultBrowser => "videos open in your default browser, out of the model's control",
        WebUiWindow => "videos play in the Web UI's YouTube tab, on your phone or browser",
        _ => "",
    };

    /// <summary>The choice in force for <paramref name="effective"/>; an unknown saved value warns once and uses <see cref="Default"/>.</summary>
    public static YouTubeOpenLinksInKind Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.YouTubeOpenLinksIn, out var kind))
        {
            return kind;
        }

        if (!string.Equals(Interlocked.Exchange(ref s_warned, effective.YouTubeOpenLinksIn), effective.YouTubeOpenLinksIn, StringComparison.Ordinal))
        {
            DiagnosticLog.Warn(Category, $"{nameof(AppSettingsData.YouTubeOpenLinksIn)}='{effective.YouTubeOpenLinksIn}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        }

        return YouTubeOpenLinksInKind.YouTubeWindow;
    }
}
