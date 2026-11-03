using System.Net;
using Microsoft.AspNetCore.Http;
using NeonSidekick.Llm.Tools;

namespace NeonSidekick.WebUi;

/// <summary>
/// The HTTPS address <c>/webui</c> offers first (2026-10-31, the user's ask: Tailscale Serve's address in the app): <paramref name="Address"/>
/// (<c>https://host</c> or <c>https://host:N</c>, no trailing slash), <paramref name="Detected"/> whether Tailscale Serve's rules gave it (else the
/// setting <i>Web UI HTTPS address</i> did), <paramref name="Public"/> whether Funnel opens it to the internet (never judged for a typed one).
/// </summary>
public sealed record WebUiHttps(string Address, bool Detected, bool Public);

/// <summary>
/// What <c>/webui</c> shows of HTTPS on one pass (2026-11-01, the review's P5-7): <paramref name="Https"/> the address offered first, if any, and
/// <paramref name="ServeHint"/> the one line saying why there is none or what is off (a Serve rule for another port or at a sub-path, a failed ask,
/// a broken saved value), else null. Lines are split on <c>\n</c>.
/// </summary>
public sealed record WebUiHttpsState(WebUiHttps? Https, string? ServeHint)
{
    /// <summary>No HTTPS address and nothing to say.</summary>
    public static WebUiHttpsState None { get; } = new(null, null);
}

/// <summary>
/// The setting <i>Web UI HTTPS address</i> (2026-10-31, the user's pick: detect, plus a setting): <c>auto</c> (the default: Tailscale Serve's
/// address, detected on Windows), <c>off</c> (none), or an <c>https://host[:port]</c> address typed for a proxy of one's own (a Mac's Serve,
/// Caddy, nginx). Words matched without regard to case; blank reads as <c>auto</c>, and a saved value that no longer parses is detected as
/// <c>auto</c> is, with a hint on the pane saying so (2026-11-01, the review's P4-3). A typed address is used as typed and never checked: the
/// setting is the PC's alone, and its row says to type only an address that reaches this computer (P4-1). The <see cref="WebUiReach"/> shape.
/// </summary>
public static class WebUiHttpsSetting
{
    /// <summary>Detect Tailscale Serve's address. The compiled default.</summary>
    public const string Auto = "auto";

    /// <summary>No HTTPS tab.</summary>
    public const string Off = "off";

    /// <summary>The compiled default, pinned by <c>AppSettingsTests</c>.</summary>
    public const string Default = Auto;

    /// <summary>
    /// The longest address taken (2026-11-01, the review's P8-1): its sign-in link (<c>/signin?t=</c> and 43 characters) is then at most 152
    /// characters, QR version 8 at Medium, 49 modules: a code that fits the pane.
    /// </summary>
    public const int MaxAddressChars = 99;

    /// <summary>
    /// The typed address in <paramref name="text"/> as the links use it (<c>https://host</c> or <c>https://host:N</c>, the host lowercased, 443
    /// left out): an absolute https URI with no user info, query, fragment or path beyond <c>/</c>, a port other than 0 (2026-11-01, the review's
    /// P2-6), and at most <see cref="MaxAddressChars"/> characters as used; else null. Pure.
    /// </summary>
    public static string? Address(string? text)
    {
        string value = (text ?? "").Trim();
        if (value.IndexOfAny(['?', '#', ' ']) >= 0 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.UserInfo.Length > 0 || uri.AbsolutePath != "/" || uri.Host.Length == 0 || uri.Port == 0)
        {
            return null;
        }

        string address = uri.GetLeftPart(UriPartial.Authority);
        return address.Length <= MaxAddressChars ? address : null;
    }

    /// <summary>Whether <paramref name="text"/> is <c>auto</c>, <c>off</c> or a good address: what the menu's edit accepts. Pure.</summary>
    public static bool IsValid(string? text) => IsWord(text, Auto) || IsWord(text, Off) || Address(text) is not null;

    /// <summary>Whether <paramref name="saved"/> is <c>auto</c> or blank (2026-11-01: a value that is neither a word nor an address is not). Pure.</summary>
    public static bool IsAuto(string? saved) => string.IsNullOrWhiteSpace(saved) || IsWord(saved, Auto);

    /// <summary>Whether <paramref name="saved"/> is <c>off</c>. Pure.</summary>
    public static bool IsOff(string? saved) => IsWord(saved, Off);

    private static bool IsWord(string? text, string word) => string.Equals(text?.Trim(), word, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Who a Web UI request came from (2026-10-31): <paramref name="Remote"/> the browser's address, <paramref name="Through"/> the proxy it came
/// through (<see cref="WebUiText.TailscaleServeName"/>, <c>the proxy at host</c>, or <see cref="WebUiText.ProxyOnThisComputer"/>), null for a direct request;
/// <paramref name="Funnel"/> whether Tailscale Funnel brought it from the internet. Worked out once per request by <see cref="Of"/>.
/// </summary>
public sealed record WebUiOrigin(IPAddress? Remote, string? Through = null, bool Funnel = false)
{
    /// <summary>The most characters of a proxy's host kept in the notice.</summary>
    public const int MaxHostChars = 100;

    /// <summary>
    /// The origin of a request whose socket is <paramref name="socket"/>, from the proxy headers (2026-10-31, from Tailscale's
    /// <c>ipn/ipnlocal/serve.go</c>, checked again 2026-11-01: Serve always sets <c>X-Forwarded-Host</c> to the Host the browser sent, sets —
    /// never appends — <c>X-Forwarded-For</c> to the device's address, and sets <c>Tailscale-Funnel-Request: ?1</c> on Funnel's alone, deleting
    /// a client's own). The headers are read only from loopback, which only this computer can be; a browser calling the port directly from the
    /// home network is never loopback, so its headers are ignored. The address is <c>X-Forwarded-For</c>'s last entry (what a proxy on this
    /// computer saw; earlier ones could come from the browser), read by <see cref="IPEndPoint.TryParse(string, out IPEndPoint?)"/> so a port
    /// after it is dropped (2026-11-01, the review's P3-4), else the socket's; an <c>X-Forwarded-For</c> alone (no host) is read too, through
    /// <see cref="WebUiText.ProxyOnThisComputer"/> (P2-3). The host is <c>X-Forwarded-Host</c>'s first entry, its name read as a Host header
    /// is (<see cref="HostName"/>, P1-7). Through Serve when that name ends in <c>.ts.net</c> (ignoring case) or the forwarded address is a
    /// tailnet one (a MagicDNS short name, P7-5); otherwise through the proxy at that name, made one line and cut. A forged
    /// "through Tailscale Serve" still needs the token to sign in. Pure.
    /// </summary>
    public static WebUiOrigin Of(IPAddress? socket, string? forwardedHost, string? forwardedFor, string? funnelHeader)
    {
        if (socket is null || !IPAddress.IsLoopback(socket))
        {
            return new WebUiOrigin(socket);
        }

        var forwarded = Forwarded(forwardedFor);
        var remote = forwarded ?? socket;
        if (string.IsNullOrWhiteSpace(forwardedHost))
        {
            return forwarded is null ? new WebUiOrigin(socket) : new WebUiOrigin(remote, WebUiText.ProxyOnThisComputer);
        }

        string name = HostName(forwardedHost.Split(',')[0]);
        if (name.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase) || (forwarded is not null && WebUiController.IsTailnet(forwarded)))
        {
            return new WebUiOrigin(remote, WebUiText.TailscaleServeName, funnelHeader?.Trim() == "?1");
        }

        return new WebUiOrigin(remote, WebUiText.ProxyAt(UntrustedText.Cut(UntrustedText.OneLine(name), MaxHostChars)));
    }

    /// <summary><c>X-Forwarded-For</c>'s last entry as an address (a port after it dropped), or null when it isn't one. Pure.</summary>
    private static IPAddress? Forwarded(string? forwardedFor)
    {
        string last = (forwardedFor ?? "").Split(',').Last().Trim();
        return last.IndexOfAny(['.', ':']) >= 0 && IPEndPoint.TryParse(last, out var endPoint) ? endPoint.Address : null;
    }

    /// <summary>
    /// A Host header's name (2026-11-01, the review's P1-7, P2-11: read as ASP.NET reads a Host, <see cref="HostString.Host"/>): the port taken
    /// off, an IPv6 address's brackets too, a trailing dot trimmed. Pure.
    /// </summary>
    public static string HostName(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        string text = new HostString(host.Trim()).Host;
        if (text.StartsWith('[') && text.EndsWith(']'))
        {
            text = text[1..^1];
        }

        return text.TrimEnd('.');
    }
}
