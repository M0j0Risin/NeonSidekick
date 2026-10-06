namespace NeonSidekick.Viewer;

/// <summary>
/// The video window's page and where it lives (2026-10-05, the YouTube plan): <c>assets\youtube\player.html</c>, embedded as
/// <see cref="ResourceName"/>, is written into <c>&lt;home&gt;\webview2\site</c> and served by WebView2 from the https virtual
/// host <see cref="HostName"/> (<see cref="WebViewHost"/>, <c>SetVirtualHostNameToFolderMapping</c>). A page with an origin is
/// the point: YouTube's embeds refuse to play without a Referer (error 153), which is what file://, <c>NavigateToString</c> and
/// a null origin give; the spike played from the virtual host with the page's referrer policy alone. The <c>.example</c>
/// top-level domain is reserved (RFC 2606), so the name can never be someone's real site, and the request never leaves the
/// machine: WebView2 answers it from the folder.
///
/// <para>The user data folder beside it (<see cref="UserDataFolder"/>) keeps YouTube's consent cookies between runs. One
/// browser process may use a user data folder at a time, and only with the same options, so the smoke's probe uses a
/// temp folder of its own, never this one (<see cref="WebViewHost.Probe"/>).</para>
/// </summary>
internal static class VideoPage
{
    /// <summary>
    /// WebView2's loader, the one file of the <c>Microsoft.Web.WebView2</c> package beside the exe (<see cref="WebViewNative"/>'s
    /// imports). Here rather than there so the smoke's portable list of native files can name it.
    /// </summary>
    public const string LoaderFileName = "WebView2Loader.dll";

    public const string HostName = "player.neonsidekick.example";
    public const string ResourceName = "youtube/player.html";
    public const string FileName = "player.html";

    /// <summary>The folder under the home WebView2 keeps its profile in: <c>&lt;home&gt;\webview2</c>.</summary>
    public const string FolderName = "webview2";

    /// <summary>The page's folder inside it: <c>&lt;home&gt;\webview2\site</c>.</summary>
    public const string SiteFolderName = "site";

    /// <summary>The page's address on the virtual host.</summary>
    public static string Url => "https://" + HostName + "/" + FileName;

    public static string UserDataFolder(string home) => Path.Combine(home, FolderName);

    public static string SiteFolder(string home) => Path.Combine(home, FolderName, SiteFolderName);

    /// <summary>
    /// Whether a top-level navigation may go ahead: only to the page's own host, over https. Anything else — a link out of
    /// the player, a script's <c>location.href</c> — is cancelled (<see cref="WebViewHost"/>'s <c>NavigationStarting</c>);
    /// a link YouTube opens in a new window goes to the default browser instead.
    /// </summary>
    public static bool IsOurs(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
        && parsed.Scheme == Uri.UriSchemeHttps
        && string.Equals(parsed.Host, HostName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a new window the page asks for may go to the default browser: an absolute http or https address with a host,
    /// nothing else (2026-10-06, the code review's catch). The address goes to the shell's open, which runs whatever handler
    /// Windows has for its scheme, so a <c>window.open</c> of <c>file:</c>, <c>ms-msdt:</c>, <c>search-ms:</c> or any
    /// registered protocol would start a program on the user's machine with no prompt; YouTube's own links are all https.
    /// </summary>
    public static bool IsWebLink(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
        && (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp)
        && parsed.Host.Length > 0;

    /// <summary>The embedded page's text.</summary>
    public static string Text()
    {
        using var stream = typeof(VideoPage).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The video page " + ResourceName + " is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Writes the page into <paramref name="siteFolder"/> (made if missing) unless the file there already says the same, so a
    /// second window, or a second copy of the app, does not rewrite a file the first one's browser may be reading. Returns the
    /// page's path.
    /// </summary>
    public static string Write(string siteFolder)
    {
        Directory.CreateDirectory(siteFolder);
        string path = Path.Combine(siteFolder, FileName);
        string text = Text();
        if (!File.Exists(path) || File.ReadAllText(path) != text)
        {
            File.WriteAllText(path, text);
        }

        return path;
    }
}
