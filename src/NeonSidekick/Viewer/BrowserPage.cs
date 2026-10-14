namespace NeonSidekick.Viewer;

/// <summary>
/// Where the Browser window keeps its profile and what it may show (2026-10-13, the bot checks: a page the web tools could not get
/// past, shown to the user to solve). Its WebView2 profile is <c>&lt;profile&gt;\webview2-browser</c> (the home's until later that day), kept between runs (the user's call),
/// so a solved check's clearance, and a site the user signed in to there, are remembered next time — a folder of its own beside the
/// video window's <see cref="VideoPage.UserDataFolder"/>, since one user data folder admits one browser, and only with the same
/// options. Pure, portable.
/// </summary>
public static class BrowserPage
{
    /// <summary>The folder under the profile WebView2 keeps the Browser window's profile in: <c>&lt;profile&gt;\webview2-browser</c>.</summary>
    public const string FolderName = "webview2-browser";

    /// <summary>The window's size at 96 DPI before the user changes it: room for a check's widget and the page behind it.</summary>
    public const int DefaultWidth = 1100;
    public const int DefaultHeight = 800;

    /// <summary>The blank page a bare <c>/browser</c> opens.</summary>
    public const string Blank = "about:blank";

    /// <summary>
    /// The window's WebView2 profile for the app profile in <paramref name="profileDirectory"/>: <c>&lt;profile&gt;\webview2-browser</c>
    /// (later on 2026-10-13, the user's pick: each profile its own history, cookies, sign-ins and solved checks; the home's
    /// <c>webview2-browser</c> until then, left as it was).
    /// </summary>
    public static string UserDataFolder(string profileDirectory)
    {
        ArgumentNullException.ThrowIfNull(profileDirectory);
        return Path.Combine(profileDirectory, FolderName);
    }

    /// <summary>
    /// Whether a top-level navigation may go ahead in the Browser window: an http or https address with a host, or the blank page —
    /// never <c>file:</c>, <c>javascript:</c>, <c>data:</c> or a registered protocol (<c>ms-msdt:</c>, <c>search-ms:</c>), which would
    /// reach the machine rather than the web. A new window the page asks for goes the same way, in place. Pure; pinned.
    /// </summary>
    public static bool Allows(string? uri)
    {
        if (string.Equals(uri, Blank, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp)
            && parsed.Host.Length > 0;
    }

    /// <summary>
    /// The browser the window is as the web requests claim it: Edge at the WebView2 runtime's major version (<c>155.0.3485.66</c> is
    /// 155), else at <see cref="Web.BrowserIdentity.FallbackMajor"/>. WebView2's user agent is Edge's reduced one exactly (checked on
    /// runtime 155, 2026-10-13: <c>… Chrome/155.0.0.0 Safari/537.36 Edg/155.0.0.0</c>), which is what Cloudflare ties a clearance to. Pure.
    /// </summary>
    public static Web.BrowserIdentity IdentityOf(string? runtimeVersion)
    {
        string major = (runtimeVersion ?? "").Split('.')[0];
        int number = int.TryParse(major, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n) && n >= Web.BrowserIdentity.LowestMajor
            ? n
            : Web.BrowserIdentity.FallbackMajor;
        return new Web.BrowserIdentity(Web.BrowserBrand.Edge, number, Web.BrowserIdentity.CurrentPlatform);
    }

    /// <summary>The host of <paramref name="uri"/> when it is a web address, else null (the blank page, a refused scheme). Pure.</summary>
    public static string? HostOf(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp) && parsed.Host.Length > 0
            ? parsed.Host.ToLowerInvariant()
            : null;
}
