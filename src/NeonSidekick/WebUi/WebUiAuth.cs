using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NeonSidekick.Settings;

namespace NeonSidekick.WebUi;

/// <summary>
/// The Web UI's sign-in (2026-10-14, the user's pick: a generated token, its link swapped for a cookie): the token minted as 32
/// random bytes in base64url (43 characters, URL- and cookie-safe as they are; hex, 64, until later that day, the user's ask: a shorter
/// link makes a smaller QR code — a hex token still signs in), kept encrypted in the profile (<see cref="AppSettingsData.WebUiToken"/>, <see cref="SettingsSecrets"/>), the
/// link <c>/webui</c> shows (<c>/signin?t=…</c>), and the cookie a browser keeps afterwards — named per port, since a browser keys
/// cookies by host alone and two profiles on two ports must not overwrite each other's. Compared in constant time. Pure.
/// </summary>
public static class WebUiAuth
{
    /// <summary>The token's length: 32 bytes in base64url, unpadded.</summary>
    public const int TokenLength = 43;

    /// <summary>A token minted before base64url: the same 32 bytes in hex.</summary>
    public const int HexTokenLength = 64;

    /// <summary>The sign-in path; its <c>t</c> query carries the token.</summary>
    public const string SignInPath = "/signin";

    /// <summary>
    /// The token in what was pasted on the sign-in page (2026-10-15): the <c>t</c> of a whole link (<c>http://…/signin?t=…</c>), else the text itself,
    /// trimmed. Pure; the match is <see cref="Matches"/>'s.
    /// </summary>
    public static string TokenFrom(string? pasted)
    {
        string text = (pasted ?? "").Trim();
        int query = text.IndexOf("?t=", StringComparison.Ordinal);
        if (query < 0)
        {
            query = text.IndexOf("&t=", StringComparison.Ordinal);
        }

        if (query >= 0)
        {
            text = text[(query + 3)..];
            int end = text.IndexOfAny(['&', '#']);
            text = end >= 0 ? text[..end] : text;
            text = Uri.UnescapeDataString(text);
        }

        return text.Trim();
    }

    /// <summary>How long a browser keeps the cookie: a year, so a phone stays signed in; <c>/webui reset</c> is the sign-out.</summary>
    public static readonly TimeSpan CookieLifetime = TimeSpan.FromDays(365);

    /// <summary>A new token: 32 random bytes in base64url.</summary>
    public static string NewToken() => System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>Whether <paramref name="token"/> is one <see cref="NewToken"/> could have made: 43 base64url characters, or an older token's 64 hex.</summary>
    public static bool IsWellFormed(string? token) =>
        token switch
        {
            { Length: TokenLength } => token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'),
            { Length: HexTokenLength } => token.All(Uri.IsHexDigit),
            _ => false,
        };

    /// <summary>The cookie's name on <paramref name="port"/>: <c>neon_webui_8765</c>.</summary>
    public static string CookieName(int port) => "neon_webui_" + port.ToString(CultureInfo.InvariantCulture);

    /// <summary>The sign-in link for <paramref name="host"/> (an address or <c>localhost</c>) and <paramref name="port"/>.</summary>
    public static string SignInLink(string host, int port, string token) =>
        "http://" + (host.Contains(':', StringComparison.Ordinal) ? "[" + host + "]" : host) + ":" + port.ToString(CultureInfo.InvariantCulture) + SignInPath + "?t=" + token;

    /// <summary>The page's own address for <paramref name="host"/> and <paramref name="port"/>, the token left out (the notice's).</summary>
    public static string Address(string host, int port) =>
        "http://" + (host.Contains(':', StringComparison.Ordinal) ? "[" + host + "]" : host) + ":" + port.ToString(CultureInfo.InvariantCulture) + "/";

    /// <summary>Whether <paramref name="presented"/> is <paramref name="token"/>, in constant time; never for an empty token.</summary>
    public static bool Matches(string? presented, string token)
    {
        if (string.IsNullOrEmpty(token) || presented is null)
        {
            return false;
        }

        byte[] a = Encoding.UTF8.GetBytes(presented);
        byte[] b = Encoding.UTF8.GetBytes(token);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
