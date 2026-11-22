namespace NeonSidekick.Mcp;

/// <summary>
/// The sign-in's fixed facts (2026-11-21, feature idea #32: OAuth for hosted <c>url</c> servers such as Linear and Notion, which take
/// nothing else). The SDK runs the protocol (discovery, dynamic registration, PKCE, the token exchange and refresh); the app supplies the
/// redirect listener (<see cref="McpSignIn"/>), the token store (<see cref="McpTokenStore"/>) and when a sign-in may happen at all.
/// </summary>
public static class McpOAuth
{
    /// <summary>
    /// The redirect port when an entry names none (<c>http://127.0.0.1:8766/callback</c>): beside the Web UI's 8765, easy to remember, below
    /// the dynamic range Windows often reserves, and one fixed port, so a pre-registered app has one address to register.
    /// </summary>
    public const int DefaultRedirectPort = 8766;

    /// <summary>How long a requested sign-in waits for the browser (seconds); the connect's budget then, not <c>MCP connect timeout</c>.</summary>
    public const int SignInTimeoutSeconds = 300;

    /// <summary>The client name a dynamically registered client is given.</summary>
    public const string ClientName = "NeonSidekick";

    /// <summary>The redirect address for <paramref name="port"/>.</summary>
    public static Uri RedirectUri(int port) => new("http://127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/callback");

    /// <summary>
    /// Whether the app may open <paramref name="url"/> for a sign-in (a safety fact: it comes from the server's own metadata, and the shell
    /// opens whatever it's given): an absolute <c>https</c> address, or <c>http</c> on this computer (the loopback, for a test server).
    /// Anything else, <c>file:</c>, <c>ms-settings:</c>, an app's protocol, is refused and never opened or printed.
    /// </summary>
    public static bool MayOpen(Uri? url) =>
        url is { IsAbsoluteUri: true }
        && (url.Scheme == Uri.UriSchemeHttps || (url.Scheme == Uri.UriSchemeHttp && url.IsLoopback));

    /// <summary>
    /// Whether <paramref name="ex"/>, or one it wraps, is the server's HTTP 401: a server that wants a sign-in. The SDK's transport throws
    /// that first <see cref="HttpRequestException"/> as it is (a 401 never falls back to SSE); a wrap above it is walked.
    /// </summary>
    public static bool IsUnauthorized(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized })
            {
                return true;
            }

            if (e is AggregateException aggregate && aggregate.InnerExceptions.Any(IsUnauthorized))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether <paramref name="ex"/>, or one it wraps, is <see cref="McpSignInNeeded"/>.</summary>
    public static bool IsSignInNeeded(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is McpSignInNeeded)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Thrown by the authorization callback when nobody asked for a sign-in (a start, a reload, a reconnect, headless): the SDK fell through to a
/// new authorization, and the row reads needs sign-in instead of a browser opening on its own.
/// </summary>
public sealed class McpSignInNeeded : Exception
{
    public McpSignInNeeded()
        : base("a sign-in is needed")
    {
    }

    public McpSignInNeeded(string message)
        : base(message)
    {
    }

    public McpSignInNeeded(string message, Exception inner)
        : base(message, inner)
    {
    }
}
