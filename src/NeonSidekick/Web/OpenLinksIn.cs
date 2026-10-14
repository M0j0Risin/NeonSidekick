using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.Web;

/// <summary>Where a link the model opens goes (<see cref="OpenLinksIn"/>).</summary>
public enum OpenLinksInKind
{
    /// <summary>The user's default browser, through the shell.</summary>
    DefaultBrowser,

    /// <summary>The app's own Browser window, one page at a time.</summary>
    BrowserWindow,
}

/// <summary>
/// The setting <c>Web open links in</c> (2026-10-13, the user's ask and name): <c>default-browser</c> or <c>browser-window</c>, the
/// <see cref="Viewer.BrowserPrivacy"/> shape. It moves <c>open_url</c> alone (the user's call): the file tool <c>open</c>, the video
/// window's links, <c>/docker</c>'s port links and the Browser window's own open-in-default button keep the default browser. Under
/// <c>browser-window</c> a link goes to the Browser window when one can be had now, else to the default browser with the reason
/// (headless, macOS, no WebView2 runtime, a bot check waiting in it). <see cref="Resolve"/> is the one place the saved string becomes the
/// enum: a hand-edited value that is neither reads as <see cref="Default"/>, with a warning once. Pure; pinned.
/// </summary>
public static class OpenLinksIn
{
    public const string DefaultBrowser = "default-browser";
    public const string BrowserWindow = "browser-window";

    /// <summary>The user's default browser, as before the setting: the compiled default, pinned by <c>AppSettingsTests</c>.</summary>
    public const string Default = DefaultBrowser;

    /// <summary>The choices in menu order.</summary>
    public static readonly string[] Names = { DefaultBrowser, BrowserWindow };

    private const string Category = "Web";

    /// <summary>The unknown value last warned of, so <see cref="Resolve"/> warns once per value (it is read at every call).</summary>
    private static string? s_warned;

    /// <summary>Trims and ignores case; false (and <see cref="OpenLinksInKind.DefaultBrowser"/>) for anything that is not one of <see cref="Names"/>.</summary>
    public static bool TryParse(string? text, out OpenLinksInKind kind)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case DefaultBrowser: kind = OpenLinksInKind.DefaultBrowser; return true;
            case BrowserWindow: kind = OpenLinksInKind.BrowserWindow; return true;
            default: kind = OpenLinksInKind.DefaultBrowser; return false;
        }
    }

    /// <summary>The saved word for <paramref name="kind"/>.</summary>
    public static string Name(OpenLinksInKind kind) => kind == OpenLinksInKind.BrowserWindow ? BrowserWindow : DefaultBrowser;

    /// <summary>The menu hint next to a choice.</summary>
    public static string Describe(string name) => name switch
    {
        DefaultBrowser => "links the model opens (open_url) go to your default browser",
        BrowserWindow => "links the model opens go to the app's Browser window, one page at a time",
        _ => "",
    };

    /// <summary>The choice in force for <paramref name="effective"/>; an unknown saved value warns once and uses <see cref="Default"/>.</summary>
    public static OpenLinksInKind Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.WebOpenLinksIn, out var kind))
        {
            return kind;
        }

        if (!string.Equals(Interlocked.Exchange(ref s_warned, effective.WebOpenLinksIn), effective.WebOpenLinksIn, StringComparison.Ordinal))
        {
            DiagnosticLog.Warn(Category, $"{nameof(AppSettingsData.WebOpenLinksIn)}='{effective.WebOpenLinksIn}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        }

        return OpenLinksInKind.DefaultBrowser;
    }
}
