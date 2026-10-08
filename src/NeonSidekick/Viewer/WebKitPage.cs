using System.Security.Cryptography;
using System.Text;

namespace NeonSidekick.Viewer;

/// <summary>
/// The video page's way across WebKit on a Mac (2026-10-07, Stage 2: YouTube playback on macOS), the pure part. <c>player.html</c>
/// stays as Windows has it, byte for byte: it talks to <c>chrome.webview</c>, WebView2's object, so a script that runs before it
/// (<see cref="Bridge"/>, added at document start in the main frame only) gives it that object over WebKit's own pieces —
/// <c>postMessage</c> to the script message handler <see cref="HandlerName"/>, and <c>addEventListener('message')</c> fed by
/// <see cref="DeliverScript"/>, which the window runs with <c>evaluateJavaScript:completionHandler:</c> and no handler (no block
/// needed). A report reaches the app as the same JSON string WebView2's carries, so <see cref="VideoMessages.Apply"/> and the
/// window's version rules hold as they are. Portable and covered on every OS.
///
/// <para>The page's origin: WebKit cannot serve https from a scheme handler, but <c>loadHTMLString:baseURL:</c> with
/// <see cref="VideoPage.Url"/> as the base gives the page that origin. Measured that day on macOS 15.7.9: the page's origin
/// <c>https://player.neonsidekick.example</c>, a secure context, YouTube's embed request sent with <c>Referer:
/// https://player.neonsidekick.example/</c>, the video playing 2 s after load with no click; with no base URL the origin was
/// <c>null</c>, no Referer went, and YouTube answered error 153. So no loopback listener is needed.</para>
/// </summary>
public static class WebKitPage
{
    /// <summary>The script message handler's name: <c>window.webkit.messageHandlers.neon</c>.</summary>
    public const string HandlerName = "neon";

    /// <summary>
    /// The page's <c>chrome.webview</c>, made before its own script runs. The listeners are the page's; the app's messages come
    /// through <c>window.__neonDeliver</c> as the objects WebView2's <c>PostWebMessageAsJson</c> hands its listeners (<c>e.data</c>).
    /// </summary>
    public const string Bridge =
        "(() => {\n" +
        "  'use strict';\n" +
        "  const listeners = [];\n" +
        "  const handler = window.webkit.messageHandlers." + HandlerName + ";\n" +
        "  window.chrome = window.chrome || {};\n" +
        "  window.chrome.webview = {\n" +
        "    postMessage: text => handler.postMessage(String(text)),\n" +
        "    addEventListener: (type, listener) => { if (type === 'message') listeners.push(listener); }\n" +
        "  };\n" +
        "  window.__neonDeliver = data => { for (const listener of listeners) listener({ data }); };\n" +
        "})();\n";

    // The namespace the store identifiers are made in: "neonSidekick" and a version, in a UUID's sixteen bytes.
    private static readonly byte[] StoreNamespace = Convert.FromHexString("6E656F6E5369646B69636B5654000001");

    /// <summary>The script that hands the page <paramref name="json"/> (a <see cref="VideoMessages"/> message, which is a JS expression too).</summary>
    public static string DeliverScript(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return "window.__neonDeliver(" + json + ")";
    }

    /// <summary>
    /// Whether a script message comes from the page itself: the main frame, over https, on <see cref="VideoPage.HostName"/>. The
    /// handler is visible to every frame (YouTube's own iframe included), where WebView2's messages are the top-level page's alone,
    /// so anything else is dropped.
    /// </summary>
    public static bool IsOurFrame(bool mainFrame, string? protocol, string? host) =>
        mainFrame
        && string.Equals(protocol, Uri.UriSchemeHttps, StringComparison.Ordinal)
        && string.Equals(host, VideoPage.HostName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The identifier of the window's WebKit data store for <paramref name="home"/> (2026-10-07, the user's call: one store per home,
    /// as Windows keeps one WebView2 profile per home). WebKit puts an identified store under
    /// <c>~/Library/WebKit/&lt;executable's name&gt;/WebsiteDataStore/&lt;identifier&gt;</c>, never under the home, so the identifier
    /// is what ties it to one: a name-based UUID (RFC 9562's version 8, SHA-256 of a namespace of the app's and the home's full path),
    /// the same for the same home on every run and another for another home. Upper-case, as <c>NSUUID</c> prints one.
    /// </summary>
    public static string StoreId(string home)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        string path = Path.GetFullPath(home);
        if (path.Length > 1)
        {
            path = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        byte[] name = Encoding.UTF8.GetBytes(path);
        byte[] input = new byte[StoreNamespace.Length + name.Length];
        StoreNamespace.CopyTo(input, 0);
        name.CopyTo(input, StoreNamespace.Length);
        byte[] hash = SHA256.HashData(input);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);   // version 8
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);   // the RFC's variant
        string hex = Convert.ToHexString(hash, 0, 16);
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }
}
