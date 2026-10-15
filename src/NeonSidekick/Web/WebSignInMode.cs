using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.Web;

/// <summary>How the default browser is signed in to the Web UI (<see cref="WebSignInMode"/>).</summary>
public enum WebSignInModeKind
{
    /// <summary>A link good once, for a minute: the browser's history keeps a dead code, never the token.</summary>
    OneTimeLink,

    /// <summary>The usual sign-in link, the token in it (the history and the browser's launch may keep it).</summary>
    UrlWithToken,
}

/// <summary>
/// The setting <c>Web browser sign in mode</c> (later on 2026-10-15, the user's ask and words): how <c>/webui browser</c> and a double-click on
/// <c>/webui</c>'s link sign the default browser in — <c>one-time-link</c> (the default) or <c>url-with-token</c>. The app's own Browser window
/// uses the usual link, or a one-time link under <i>Web browser privacy</i> <c>keep-history</c>. The <see cref="OpenLinksIn"/> shape: an unknown
/// word reads as <see cref="Default"/>, with a warning once. Pure; pinned.
/// </summary>
public static class WebSignInMode
{
    public const string OneTimeLink = "one-time-link";
    public const string UrlWithToken = "url-with-token";

    public const string Default = OneTimeLink;

    /// <summary>The choices in menu order.</summary>
    public static readonly string[] Names = { OneTimeLink, UrlWithToken };

    private const string Category = "Web";

    private static string? s_warned;

    public static bool TryParse(string? text, out WebSignInModeKind kind)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case OneTimeLink: kind = WebSignInModeKind.OneTimeLink; return true;
            case UrlWithToken: kind = WebSignInModeKind.UrlWithToken; return true;
            default: kind = WebSignInModeKind.OneTimeLink; return false;
        }
    }

    public static string Name(WebSignInModeKind kind) => kind == WebSignInModeKind.UrlWithToken ? UrlWithToken : OneTimeLink;

    /// <summary>The menu hint next to a choice, in the user's words.</summary>
    public static string Describe(string name) => name switch
    {
        OneTimeLink => "external browser authenticates with a one-time link",
        UrlWithToken => "external browser receives the token directly",
        _ => "",
    };

    public static WebSignInModeKind Resolve(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (TryParse(effective.WebBrowserSignInMode, out var kind))
        {
            return kind;
        }

        if (!string.Equals(Interlocked.Exchange(ref s_warned, effective.WebBrowserSignInMode), effective.WebBrowserSignInMode, StringComparison.Ordinal))
        {
            DiagnosticLog.Warn(Category, $"{nameof(AppSettingsData.WebBrowserSignInMode)}='{effective.WebBrowserSignInMode}' is not one of {string.Join(", ", Names)}. Using {Default}.");
        }

        return WebSignInModeKind.OneTimeLink;
    }
}
