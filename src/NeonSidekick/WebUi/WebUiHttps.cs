using System.Net;
using NeonSidekick.Llm.Tools;

namespace NeonSidekick.WebUi;

/// <summary>
/// The HTTPS address <c>/webui</c> offers first (2026-10-31, the user's ask: Tailscale Serve's address in the app): <paramref name="Address"/>
/// (<c>https://host</c> or <c>https://host:N</c>, no trailing slash), <paramref name="Detected"/> whether Tailscale Serve's rules gave it (else the
/// setting <i>Web UI HTTPS address</i> did), <paramref name="Public"/> whether Funnel opens it to the internet (never judged for a typed one).
/// </summary>
public sealed record WebUiHttps(string Address, bool Detected, bool Public);

/// <summary>
/// The setting <i>Web UI HTTPS address</i> (2026-10-31, the user's pick: detect, plus a setting): <c>auto</c> (the default: Tailscale Serve's
/// address, detected on Windows), <c>off</c> (none), or an <c>https://host[:port]</c> address typed for a proxy of one's own (a Mac's Serve,
/// Caddy, nginx). Words matched without regard to case; a saved value that no longer parses reads as <c>auto</c>. The <see cref="WebUiReach"/> shape.
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
    /// The typed address in <paramref name="text"/> as the links use it (<c>https://host</c> or <c>https://host:N</c>, the host lowercased, 443
    /// left out): an absolute https URI with no user info, query, fragment or path beyond <c>/</c>; else null. Pure.
    /// </summary>
    public static string? Address(string? text)
    {
        string value = (text ?? "").Trim();
        if (value.IndexOfAny(['?', '#', ' ']) >= 0 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.UserInfo.Length > 0 || uri.AbsolutePath != "/" || uri.Host.Length == 0)
        {
            return null;
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>Whether <paramref name="text"/> is <c>auto</c>, <c>off</c> or a good address: what the menu's edit accepts. Pure.</summary>
    public static bool IsValid(string? text) => IsWord(text, Auto) || IsWord(text, Off) || Address(text) is not null;

    /// <summary>Whether <paramref name="saved"/> means "detect": <c>auto</c>, or anything that does not parse. Pure.</summary>
    public static bool Detects(string? saved) => !IsWord(saved, Off) && Address(saved) is null;

    /// <summary>Whether <paramref name="saved"/> is <c>off</c>. Pure.</summary>
    public static bool IsOff(string? saved) => IsWord(saved, Off);

    private static bool IsWord(string? text, string word) => string.Equals(text?.Trim(), word, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Who a Web UI request came from (2026-10-31): <paramref name="Remote"/> the browser's address, <paramref name="Through"/> the proxy it came
/// through (<see cref="TailscaleServeName"/>, or <c>the proxy at host</c>), null for a direct request; <paramref name="Funnel"/> whether Tailscale
/// Funnel brought it from the internet. Worked out once per request by <see cref="Of"/>.
/// </summary>
public sealed record WebUiOrigin(IPAddress? Remote, string? Through = null, bool Funnel = false)
{
    /// <summary>What <see cref="Through"/> holds for a request through Tailscale Serve.</summary>
    public const string TailscaleServeName = "Tailscale Serve";

    /// <summary>The most characters of a proxy's host kept in the notice.</summary>
    public const int MaxHostChars = 100;

    /// <summary>
    /// The origin of a request whose socket is <paramref name="socket"/>, from the proxy headers (2026-10-31, from Tailscale's
    /// <c>ipn/ipnlocal/serve.go</c>: Serve always sets <c>X-Forwarded-Host</c>, sets — never appends — <c>X-Forwarded-For</c> to the tailnet
    /// device's address, and marks Funnel's with <c>Tailscale-Funnel-Request: ?1</c>). The headers are read only from loopback, which only this
    /// computer can be; a browser calling the port directly from the home network is never loopback, so its headers are ignored. The address
    /// is <c>X-Forwarded-For</c>'s last entry (what a proxy on this computer saw; earlier ones could come from the browser) when it parses as
    /// an IP, else the socket's. Through Serve when <c>X-Forwarded-Host</c>'s name, its port and brackets off and a trailing dot trimmed,
    /// ends in <c>.ts.net</c> (ignoring case); otherwise through the proxy at that host, made one line and cut. Pure.
    /// </summary>
    public static WebUiOrigin Of(IPAddress? socket, string? forwardedHost, string? forwardedFor, string? funnelHeader)
    {
        if (socket is null || !IPAddress.IsLoopback(socket) || string.IsNullOrWhiteSpace(forwardedHost))
        {
            return new WebUiOrigin(socket);
        }

        string last = (forwardedFor ?? "").Split(',').Last().Trim();
        var remote = last.IndexOfAny(['.', ':']) >= 0 && IPAddress.TryParse(last, out var parsed) ? parsed : socket;
        string name = HostName(forwardedHost);
        if (name.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase))
        {
            return new WebUiOrigin(remote, TailscaleServeName, funnelHeader?.Trim() == "?1");
        }

        return new WebUiOrigin(remote, "the proxy at " + UntrustedText.Cut(UntrustedText.OneLine(forwardedHost.Trim()), MaxHostChars));
    }

    /// <summary>A Host header's name: <c>[…]</c> or a <c>:port</c> taken off, a trailing dot trimmed. Pure.</summary>
    public static string HostName(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        string text = host.Trim();
        if (text.StartsWith('['))
        {
            int close = text.IndexOf(']', StringComparison.Ordinal);
            text = close > 0 ? text[1..close] : text[1..];
        }
        else if (text.IndexOf(':', StringComparison.Ordinal) is int colon and >= 0 && text.IndexOf(':', colon + 1) < 0)
        {
            text = text[..colon];
        }

        return text.TrimEnd('.');
    }
}
