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

    /// <summary>The page a browser without the cookie gets.</summary>
    public const string SignInPage = "signin.html";

    /// <summary>Every file, by the name it is served as (the path less its slash).</summary>
    public static readonly IReadOnlyList<string> Names = [ChatPage, ShoppingPage, SignInPage, "app.css", "chat.js", "shopping.js", "favicon.svg"];

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
        _ => "application/octet-stream",
    };

    private static IReadOnlyDictionary<string, WebAsset> Load()
    {
        var assets = new Dictionary<string, WebAsset>(StringComparer.Ordinal);
        foreach (string name in Names)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(EmbeddedText.Read("webui/" + name, "The Web UI's file"));
            string etag = "\"" + Convert.ToHexStringLower(SHA256.HashData(bytes))[..16] + "\"";
            assets[name] = new WebAsset(name, bytes, ContentTypeOf(name), etag);
        }

        return assets;
    }
}
