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

    /// <summary>
    /// The player both pages share (2026-10-16, <c>assets\youtube\player-core.js</c>), embedded once under the Web UI's name, which serves it
    /// as is; <see cref="Text"/> puts it into this page at <see cref="CoreMarker"/>.
    /// </summary>
    public const string CoreResourceName = "webui/youtube-core.js";

    /// <summary>Where <see cref="Text"/> puts the shared player in the page.</summary>
    public const string CoreMarker = "/*@neon-player-core@*/";

    /// <summary>The folder under the home WebView2 keeps its profile in: <c>&lt;home&gt;\webview2</c>.</summary>
    public const string FolderName = "webview2";

    /// <summary>The page's folder inside it: <c>&lt;home&gt;\webview2\site</c>.</summary>
    public const string SiteFolderName = "site";

    /// <summary>The page's address on the virtual host.</summary>
    public static string Url => "https://" + HostName + "/" + FileName;

    /// <summary>
    /// The file page (2026-11-12, the video-files plan): <c>assets\youtube\file.html</c>, a video file in an HTML5 <c>&lt;video&gt;</c>,
    /// written beside <see cref="FileName"/> and loaded as a <c>file:</c> URL (<see cref="FileUrl"/>), not from the virtual host. The
    /// Phase 0 spike (a 2 GB, 58 Mbps H.264 MP4 in WebView2): from a <c>file:</c> page, the video's metadata in 7-21 ms, its first
    /// frame in 251 ms cold and 33-50 ms warm, a seek to its end in 74-80 ms and back in 38-53 ms; an H.264 MKV, a MOV and a UNC path
    /// played. A virtual host mapping played as fast, but applies only to pages navigated after it is made (one made under a loaded
    /// page gave MediaError 4), so mapping per play would mean a reload anyway, and it maps the video's whole folder. Only the host's
    /// own <c>Navigate</c> moves the view between the two pages (Chromium refuses an https page's own move to <c>file:</c>).
    /// </summary>
    public const string FilePageName = "file.html";

    public const string FileResourceName = "youtube/file.html";

    /// <summary>
    /// The file player both pages share (<c>assets\youtube\file-core.js</c>), embedded once under the Web UI's name, as the YouTube core is;
    /// <see cref="FileText"/> puts it into the file page at <see cref="FileCoreMarker"/>.
    /// </summary>
    public const string FileCoreResourceName = "webui/youtube-file.js";

    /// <summary>Where <see cref="FileText"/> puts the file player in the page.</summary>
    public const string FileCoreMarker = "/*@neon-file-core@*/";

    /// <summary>The file page's own address in <paramref name="siteFolder"/>: a <c>file:</c> URL (<see cref="FileSrc"/>).</summary>
    public static string FileUrl(string siteFolder) => FileSrc(Path.Combine(siteFolder, FilePageName));

    /// <summary>The page a request needs: the file page for a file, the YouTube player for a video or a playlist.</summary>
    public static string UrlFor(VideoRequest request, string siteFolder)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.IsFile ? FileUrl(siteFolder) : Url;
    }

    /// <summary>Whether the window, showing the file page or not (<paramref name="showingFile"/>), must navigate before it loads <paramref name="request"/>.</summary>
    public static bool NeedsSwitch(bool showingFile, VideoRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return showingFile != request.IsFile;
    }

    /// <summary>
    /// A local or UNC path as a <c>file:</c> URL, each segment escaped by hand: <c>new Uri(path).AbsoluteUri</c> leaves a literal
    /// <c>%20</c> in a name as it is (the spike, 2026-11-12: <c>clip %20.mp4</c> then named another file), so every segment goes through
    /// <see cref="Uri.EscapeDataString(string)"/>. A drive (<c>C:</c>) is kept; <c>\\server\share\…</c> becomes <c>file://server/share/…</c>;
    /// a <c>\\?\</c> prefix is dropped; a Unix path becomes <c>file:///…</c>. Throws <see cref="ArgumentException"/> on a path that isn't
    /// fully qualified.
    /// </summary>
    public static string FileSrc(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException(VideoText.NotAFullPath(path), nameof(path));
        }

        string full = path;
        if (full.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            full = @"\\" + full[8..];
        }
        else if (full.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            full = full[4..];
        }

        static string Escaped(IEnumerable<string> segments) => string.Join('/', segments.Select(Uri.EscapeDataString));
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            string[] unc = full[2..].Split('\\');
            return "file://" + unc[0] + "/" + Escaped(unc.Skip(1));
        }

        if (full.Length >= 2 && char.IsAsciiLetter(full[0]) && full[1] == ':')
        {
            string[] parts = full.Split('\\', '/');
            return "file:///" + parts[0] + "/" + Escaped(parts.Skip(1));
        }

        return "file:///" + Escaped(full.TrimStart('/').Split('/'));
    }

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
    /// <see cref="IsOurs(string?)"/> for the Windows window (2026-11-12, the video-files plan), which also shows its file page: the player
    /// host, or exactly <paramref name="siteFolder"/>'s <see cref="FilePageName"/>. The <c>file:</c> address is compared parsed — its
    /// local path unescaped, case ignored (Windows' file names), no query or fragment — so another spelling of the same page is admitted
    /// and nothing else is: no other file in the site folder, no video, no share. The page gets no file-access flag, so its script can't
    /// read a file's bytes, and a video's <c>src</c> comes only from the app.
    /// </summary>
    public static bool IsOurs(string? uri, string siteFolder)
    {
        if (IsOurs(uri))
        {
            return true;
        }

        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile || parsed.Query.Length > 0 || parsed.Fragment.Length > 0)
        {
            return false;
        }

        if (!Uri.TryCreate(FileUrl(siteFolder), UriKind.Absolute, out var page))
        {
            return false;
        }

        return string.Equals(parsed.LocalPath, page.LocalPath, StringComparison.OrdinalIgnoreCase);
    }

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

    /// <summary>The embedded page's text, the shared player put in at its marker (2026-10-16): the page as written and as the Mac loads it.</summary>
    public static string Text()
    {
        string page = Resource(ResourceName);
        if (!page.Contains(CoreMarker, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The video page " + ResourceName + " has no " + CoreMarker + " for the player.");
        }

        return page.Replace(CoreMarker, Resource(CoreResourceName), StringComparison.Ordinal);
    }

    /// <summary>The embedded file page's text, the file player put in at its marker (2026-11-12): the page as written.</summary>
    public static string FileText()
    {
        string page = Resource(FileResourceName);
        if (!page.Contains(FileCoreMarker, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The video page " + FileResourceName + " has no " + FileCoreMarker + " for the player.");
        }

        return page.Replace(FileCoreMarker, Resource(FileCoreResourceName), StringComparison.Ordinal);
    }

    private static string Resource(string name)
    {
        using var stream = typeof(VideoPage).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("The video page's " + name + " is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Writes the page into <paramref name="siteFolder"/> (made if missing) unless the file there already says the same, so a
    /// second window, or a second copy of the app, does not rewrite a file the first one's browser may be reading. The file page
    /// beside it the same way (2026-11-12). Returns the YouTube page's path.
    /// </summary>
    public static string Write(string siteFolder)
    {
        Directory.CreateDirectory(siteFolder);
        string path = WriteIfChanged(Path.Combine(siteFolder, FileName), Text());
        WriteIfChanged(Path.Combine(siteFolder, FilePageName), FileText());
        return path;
    }

    private static string WriteIfChanged(string path, string text)
    {
        if (!File.Exists(path) || File.ReadAllText(path) != text)
        {
            File.WriteAllText(path, text);
        }

        return path;
    }
}
