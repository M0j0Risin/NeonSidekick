using System.Net;
using System.Net.Sockets;
using System.Text;
using ModelContextProtocol.Authentication;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Mcp;

/// <summary>
/// One requested sign-in's browser half (2026-11-21, feature idea #32): the address checked (<see cref="McpOAuth.MayOpen"/>), a one-shot
/// listener on <c>127.0.0.1:&lt;port&gt;</c>, the browser opened, and the callback's <c>code</c>, <c>state</c> and <c>iss</c> handed back to
/// the SDK, which checks them. A stray request (a <c>/favicon.ico</c>, another path or method, a callback with neither a code nor an
/// error) gets a 404 and the wait goes on. Each request is read under a size cap and a short timeout, so a held-open connection can't
/// stall it. The listener is stopped on every end. Nothing from the query is logged or echoed back.
/// </summary>
public static class McpSignIn
{
    /// <summary>The most a request may send before it is answered 400.</summary>
    internal const int MaxRequestBytes = 16 * 1024;

    /// <summary>How long one connection gets to send its request.</summary>
    internal static TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Runs the browser half for <paramref name="server"/>: opens <paramref name="authorizationUri"/> through <paramref name="openBrowser"/>
    /// (a shell open that returns at once; null prints the address alone), says the address through <paramref name="notice"/>, and waits
    /// for the redirect to <paramref name="redirectUri"/>. Throws <see cref="InvalidOperationException"/> with the app's words for a
    /// refused address, a busy port or the service's <c>error=</c>; the caller's cancel as itself.
    /// </summary>
    public static async Task<AuthorizationResult> WaitAsync(string server, Uri authorizationUri, Uri redirectUri, Action<string>? openBrowser,
        Action<string, bool>? notice, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(authorizationUri);
        ArgumentNullException.ThrowIfNull(redirectUri);
        if (!McpOAuth.MayOpen(authorizationUri))
        {
            DiagnosticLog.Warn(McpConfigFile.Category, McpText.SignInUrlRefusedLogLine(server, authorizationUri.IsAbsoluteUri ? authorizationUri.Scheme : "relative"));
            throw new InvalidOperationException(McpText.SignInUrlRefused(authorizationUri.IsAbsoluteUri ? authorizationUri.Scheme : "relative",
                authorizationUri.IsAbsoluteUri ? authorizationUri.Host : ""));
        }

        int port = redirectUri.Port;
        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException(McpText.SignInPortBusy(port, ex.SocketErrorCode.ToString()), ex);
        }

        try
        {
            DiagnosticLog.Info(McpConfigFile.Category, McpText.SignInStartedLogLine(server, port));
            string address = authorizationUri.AbsoluteUri;
            if (openBrowser is null)
            {
                notice?.Invoke(McpText.SignInOpenedNotice(server, address), false);
            }
            else
            {
                try
                {
                    openBrowser(address);
                    notice?.Invoke(McpText.SignInOpenedNotice(server, address), false);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or PlatformNotSupportedException)
                {
                    notice?.Invoke(McpText.SignInBrowserFailed(server, address, ex.Message), true);
                }
            }

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(McpOAuth.SignInTimeoutSeconds));
            using var stop = budget.Token.Register(listener.Stop);
            string path = redirectUri.AbsolutePath;
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(budget.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new InvalidOperationException(McpText.SignInTimedOut(McpOAuth.SignInTimeoutSeconds), ex);
                }

                using (client)
                {
                    var answer = await AnswerAsync(client, server, path, budget.Token).ConfigureAwait(false);
                    if (answer is { } done)
                    {
                        return done.Error is { } error
                            ? throw new InvalidOperationException(McpText.SignInRefused(Llm.Tools.UntrustedText.Fenced(error)))
                            : done.Result!;
                    }
                }
            }
        }
        finally
        {
            listener.Stop();
            listener.Dispose();
        }
    }

    private readonly record struct Callback(AuthorizationResult? Result, string? Error);

    /// <summary>One connection: the callback's result or error once answered; null for a stray request (answered 404 or 400) or one that timed out.</summary>
    private static async Task<Callback?> AnswerAsync(TcpClient client, string server, string path, CancellationToken cancellationToken)
    {
        using var read = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        read.CancelAfter(RequestTimeout);
        var stream = client.GetStream();
        string? head;
        try
        {
            head = await ReadHeadAsync(stream, read.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        if (head is null)
        {
            await RespondAsync(stream, 400, "Bad Request", "", cancellationToken).ConfigureAwait(false);
            return null;
        }

        string requestLine = head.Split("\r\n", 2)[0];
        var parts = requestLine.Split(' ');
        if (parts.Length != 3 || parts[0] != "GET" || !Uri.TryCreate("http://127.0.0.1" + parts[1], UriKind.Absolute, out var target)
            || !string.Equals(target.AbsolutePath, path, StringComparison.Ordinal))
        {
            await RespondAsync(stream, 404, "Not Found", "", cancellationToken).ConfigureAwait(false);
            return null;
        }

        var query = Query(target.Query);
        if (query.TryGetValue("error", out var error))
        {
            await RespondAsync(stream, 200, "OK", McpText.SignInErrorPage(server), cancellationToken).ConfigureAwait(false);
            return new Callback(null, error);
        }

        if (!query.TryGetValue("code", out var code) || code.Length == 0)
        {
            await RespondAsync(stream, 404, "Not Found", "", cancellationToken).ConfigureAwait(false);
            return null;
        }

        await RespondAsync(stream, 200, "OK", McpText.SignedInPage(server), cancellationToken).ConfigureAwait(false);
        return new Callback(new AuthorizationResult
        {
            Code = code,
            State = query.GetValueOrDefault("state"),
            Iss = query.GetValueOrDefault("iss"),
        }, null);
    }

    /// <summary>The request's head (up to the blank line), or null past <see cref="MaxRequestBytes"/> or a stream that ended first.</summary>
    private static async Task<string?> ReadHeadAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxRequestBytes];
        int length = 0;
        while (length < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                return null;
            }

            length += n;
            int end = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
            if (end >= 0)
            {
                return Encoding.ASCII.GetString(buffer, 0, end);
            }
        }

        return null;
    }

    private static async Task RespondAsync(NetworkStream stream, int status, string reason, string html, CancellationToken cancellationToken)
    {
        byte[] body = Encoding.UTF8.GetBytes(html);
        string head = "HTTP/1.1 " + status.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + reason + "\r\n"
            + "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nConnection: close\r\nContent-Length: "
            + body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\r\n\r\n";
        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            // The browser closed first; the answer is still the callback's.
        }
    }

    /// <summary>The query's pairs, decoded; the first of a repeated key wins.</summary>
    internal static Dictionary<string, string> Query(string query)
    {
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=', StringComparison.Ordinal);
            string key = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
            string value = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            pairs.TryAdd(key, value);
        }

        return pairs;
    }
}
