using System.Security.Cryptography;
using System.Text;
using NeonSidekick.App;

namespace NeonSidekick.WebUi;

/// <summary>One page or file the Web UI serves: its bytes, its media type and its ETag (the content's hash).</summary>
public sealed record WebAsset(string Name, byte[] Bytes, string ContentType, string ETag);

/// <summary>
/// The Web UI's files (2026-10-14): <c>assets/webui/*</c>, embedded as <c>webui/*</c> and served from memory — no web root, no file on
/// disk. Text files all (the favicon is an SVG), read once through <see cref="EmbeddedText"/>. Each has an ETag, so a browser re-checks
/// with <c>no-cache</c> and picks up a new build.
/// </summary>
public static class WebUiAssets
{
    /// <summary>The chat page.</summary>
    public const string ChatPage = "index.html";

    /// <summary>The shopping page.</summary>
    public const string ShoppingPage = "shopping.html";

    /// <summary>The Home tab (2026-10-15).</summary>
    public const string HomePage = "home.html";

    /// <summary>The page a browser without the cookie gets.</summary>
    public const string SignInPage = "signin.html";

    /// <summary>The YouTube tab (2026-10-16), shown while <i>Web UI YouTube controls</i> is on.</summary>
    public const string YouTubePage = "youtube.html";

    /// <summary>The player both video pages share (2026-10-16, <c>assets\youtube\player-core.js</c>, one embed read by both).</summary>
    public const string YouTubeCore = "youtube-core.js";

    /// <summary>The video-file player both video pages share (2026-11-12, <c>assets\youtube\file-core.js</c>, one embed read by both).</summary>
    public const string YouTubeFile = "youtube-file.js";

    /// <summary>
    /// The About page (2026-11-08, the user's ask): the version, the licenses and this version's source, behind ☰, served whatever the tabs.
    /// The source link sat at the bottom of every page from 2026-11-06 until then.
    /// </summary>
    public const string AboutPage = "about.html";

    /// <summary>The page <c>/</c> shows with every tab off (later on 2026-10-15): it names the settings and goes to <c>/</c> once one is on.</summary>
    public const string NoTabsPage = "notabs.html";

    /// <summary>Every file, by the name it is served as (the path less its slash).</summary>
    public static readonly IReadOnlyList<string> Names =
        [ChatPage, ShoppingPage, HomePage, SignInPage, NoTabsPage, YouTubePage, AboutPage, "app.css", "common.js", "chat.js", "shopping.js", "home.js", "signin.js", "notabs.js", "youtube.js", "about.js", YouTubeCore, YouTubeFile, "favicon.svg", "icon-192.png", "icon-512.png", "apple-touch-icon.png"];

    /// <summary>
    /// The files served without the cookie (a signed-out page loads them too): every one of <see cref="Names"/> but a page, so the style, the
    /// scripts, the icons. Never a page or an API. <c>common.js</c> since 2026-10-15, the pages' shared helpers; read off <see cref="Names"/>
    /// since 2026-11-08 (review P1-8), so a new script is listed once.
    /// </summary>
    public static bool IsStatic(string name) => !name.EndsWith(".html", StringComparison.Ordinal) && Names.Contains(name, StringComparer.Ordinal);

    private static readonly Lazy<IReadOnlyDictionary<string, WebAsset>> s_assets = new(Load);

    private static readonly Lazy<string> s_tag = new(() => Tag(Names.Select(n => s_assets.Value[n].ETag)));

    /// <summary>
    /// The tag of the files this build serves (2026-10-14): a page that has another reloads itself, so a phone left open across an upgrade
    /// runs the new script (<see cref="WebState.Assets"/>).
    /// </summary>
    public static string Current => s_tag.Value;

    /// <summary>The tag of a set of files by their ETags, in order: 16 hex characters. Pure.</summary>
    public static string Tag(IEnumerable<string> etags)
    {
        ArgumentNullException.ThrowIfNull(etags);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', etags))))[..16];
    }

    /// <summary>The file served at <paramref name="name"/>, or null.</summary>
    public static WebAsset? Find(string name) => s_assets.Value.GetValueOrDefault(name);

    /// <summary>The media type a file is served as, by its extension.</summary>
    public static string ContentTypeOf(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        _ => "application/octet-stream",
    };

    private static byte[] ReadBytes(string resource)
    {
        using var stream = typeof(WebUiAssets).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("The Web UI's file " + resource + " is not embedded.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>
    /// The About page's version and links, stamped as the file is read (2026-11-08): <c>{{version}}</c>, <c>{{source}}</c> (the version's tag)
    /// and <c>{{source-file}}</c> (a file at that tag, for the two licenses); <c>{{copyright}}</c> and <c>{{license}}</c> from <see cref="AboutText"/>
    /// too (review P1-7: one home). No other page names the version, so a signed-out visitor gets no hint of it. The ETag is taken after the stamp.
    /// </summary>
    private static byte[] StampAboutPage(string name, byte[] bytes)
    {
        if (name != AboutPage)
        {
            return bytes;
        }

        string version = SidekickApp.Version;
        return Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes)
            .Replace("{{version}}", version, StringComparison.Ordinal)
            .Replace("{{source}}", AboutText.SourceUrl(version), StringComparison.Ordinal)
            .Replace("{{source-file}}", AboutText.SourceFileUrl(version), StringComparison.Ordinal)
            .Replace("{{copyright}}", AboutText.Copyright, StringComparison.Ordinal)
            .Replace("{{license}}", AboutText.LicenseName, StringComparison.Ordinal));
    }

    private static IReadOnlyDictionary<string, WebAsset> Load()
    {
        var assets = new Dictionary<string, WebAsset>(StringComparer.Ordinal);
        foreach (string name in Names)
        {
            // The icons are bytes (2026-10-15, Add to Home Screen); the rest text.
            byte[] bytes = name.EndsWith(".png", StringComparison.Ordinal) ? ReadBytes("webui/" + name) : Encoding.UTF8.GetBytes(EmbeddedText.Read("webui/" + name, "The Web UI's file"));
            bytes = StampAboutPage(name, bytes);
            string etag = "\"" + Convert.ToHexStringLower(SHA256.HashData(bytes))[..16] + "\"";
            assets[name] = new WebAsset(name, bytes, ContentTypeOf(name), etag);
        }

        return assets;
    }
}
