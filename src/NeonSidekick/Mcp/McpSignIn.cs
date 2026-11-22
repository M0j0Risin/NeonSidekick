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

    /// <summary>How long one connection gets to send its request, and its answer to be written.</summary>
    internal static TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long the browser step waits (<see cref="McpOAuth.SignInTimeoutSeconds"/>); a test's seam for the budget's end.</summary>
    internal static TimeSpan Budget { get; set; } = TimeSpan.FromSeconds(McpOAuth.SignInTimeoutSeconds);

    /// <summary>The most connections answered at once (2026-11-22, the review's #80); one past it is closed unanswered.</summary>
    internal const int MaxConnections = 8;

    /// <summary>The most of the service's <c>error_description</c> a refusal carries (2026-11-22, the review's #138).</summary>
    internal const int MaxDescriptionChars = 200;

    /// <summary>
    /// Runs the browser half for <paramref name="server"/>: opens <paramref name="authorizationUri"/> through <paramref name="openBrowser"/>
    /// (a shell open that returns at once; null prints the address alone), says the address through <paramref name="notice"/>, and waits
    /// for the redirect to <paramref name="redirectUri"/>. Throws <see cref="McpSignInFailed"/> with the app's words for a refused address,
    /// a busy port, the budget running out or the service's <c>error=</c> (its words fenced); the caller's cancel as itself.
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
            throw new McpSignInFailed(McpText.SignInUrlRefused(authorizationUri.IsAbsoluteUri ? authorizationUri.Scheme : "relative",
                authorizationUri.IsAbsoluteUri ? authorizationUri.Host : ""));
        }

        // The state the SDK put in the address (2026-11-22, the review's #12): a callback without it, or with another, is a stray request.
        string? expectedState = Query(authorizationUri.Query).GetValueOrDefault("state");
        int port = redirectUri.Port;
        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            throw new McpSignInFailed(McpText.SignInPortBusy(port, ex.SocketErrorCode.ToString()), ex);
        }

        var answering = new List<Task>();
        using var answers = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var slots = new SemaphoreSlim(MaxConnections);
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
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    // Any opener's failure (2026-11-22, the review's #124): the address is printed, and the wait goes on.
                    notice?.Invoke(McpText.SignInBrowserFailed(server, address, ex.Message), true);
                }
            }

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(Budget);
            using var stop = budget.Token.Register(listener.Stop);
            using var answerBudget = budget.Token.Register(answers.Cancel);
            var done = new TaskCompletionSource<Callback>(TaskCreationOptions.RunContinuationsAsynchronously);
            string path = redirectUri.AbsolutePath;
            while (true)
            {
                var accept = listener.AcceptTcpClientAsync(budget.Token).AsTask();
                if (await Task.WhenAny(accept, done.Task).ConfigureAwait(false) == done.Task)
                {
                    _ = accept.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    break;
                }

                TcpClient client;
                try
                {
                    client = await accept.ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    if (done.Task.IsCompleted)
                    {
                        break;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    throw new McpSignInFailed(McpText.SignInTimedOut(McpOAuth.SignInTimeoutSeconds), ex);
                }

                // Each connection on its own task (2026-11-22, the review's #80): an idle preconnect never holds the callback up; at most a few at once.
                if (!slots.Wait(0))
                {
                    client.Dispose();
                    continue;
                }

                answering.Add(Task.Run(async () =>
                {
                    try
                    {
                        using (client)
                        {
                            if (await AnswerAsync(client, server, path, expectedState, answers.Token).ConfigureAwait(false) is { } answer)
                            {
                                done.TrySetResult(answer);
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        // One connection's failure is that connection's; the wait goes on.
                    }
                    finally
                    {
                        slots.Release();
                    }
                }, CancellationToken.None));
            }

            var callback = await done.Task.ConfigureAwait(false);
            return callback.Error is { } error
                ? throw new McpSignInFailed(McpText.SignInRefused(Llm.Tools.UntrustedText.Fenced(error),
                    callback.Description is { Length: > 0 } description ? Llm.Tools.UntrustedText.Fenced(Llm.Tools.UntrustedText.Cut(McpText.OneLine(description), MaxDescriptionChars)) : null))
                : callback.Result!;
        }
        finally
        {
            listener.Stop();
            listener.Dispose();
            await answers.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(answering).ConfigureAwait(false);
        }
    }

    private readonly record struct Callback(AuthorizationResult? Result, string? Error, string? Description = null);

    /// <summary>
    /// One connection: the callback's result or error once answered; null for a stray request (answered 404 or 400), one that timed out, or
    /// a callback whose <c>state</c> isn't the one this sign-in sent (#12). The page is written on its own short timeout (#59), never the
    /// wait's, so a code received at the budget's end is still handed back.
    /// </summary>
    private static async Task<Callback?> AnswerAsync(TcpClient client, string server, string path, string? expectedState, CancellationToken cancellationToken)
    {
        using var read = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        read.CancelAfter(RequestTimeout);
        var stream = client.GetStream();
        string? head;
        try
        {
            head = await ReadHeadAsync(stream, read.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
            return null;
        }

        if (head is null)
        {
            await RespondAsync(stream, 400, "Bad Request", "").ConfigureAwait(false);
            return null;
        }

        string requestLine = head.Split("\r\n", 2)[0];
        var parts = requestLine.Split(' ');
        if (parts.Length != 3 || parts[0] != "GET" || !Uri.TryCreate("http://127.0.0.1" + parts[1], UriKind.Absolute, out var target)
            || !string.Equals(target.AbsolutePath, path, StringComparison.Ordinal))
        {
            await RespondAsync(stream, 404, "Not Found", "").ConfigureAwait(false);
            return null;
        }

        var query = Query(target.Query);
        bool stateOk = expectedState is null || string.Equals(query.GetValueOrDefault("state"), expectedState, StringComparison.Ordinal);
        if (!stateOk)
        {
            await RespondAsync(stream, 404, "Not Found", "").ConfigureAwait(false);
            return null;
        }

        if (query.TryGetValue("error", out var error))
        {
            await RespondAsync(stream, 200, "OK", McpText.SignInErrorPage(server)).ConfigureAwait(false);
            return new Callback(null, error, query.GetValueOrDefault("error_description"));
        }

        if (!query.TryGetValue("code", out var code) || code.Length == 0)
        {
            await RespondAsync(stream, 404, "Not Found", "").ConfigureAwait(false);
            return null;
        }

        // A neutral page (2026-11-22, the review's #123): the code is only received here; the app says whether the sign-in finished.
        await RespondAsync(stream, 200, "OK", McpText.SignInReceivedPage(server)).ConfigureAwait(false);
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

    /// <summary>
    /// The answer, written on its own short timeout (2026-11-22, the review's #59): a browser that closed first, a write that stalls, or the
    /// listener stopping meanwhile never turns a received callback into a failure.
    /// </summary>
    private static async Task RespondAsync(NetworkStream stream, int status, string reason, string html)
    {
        byte[] body = Encoding.UTF8.GetBytes(html);
        string head = "HTTP/1.1 " + status.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + reason + "\r\n"
            + "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nConnection: close\r\nContent-Length: "
            + body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\r\n\r\n";
        using var write = new CancellationTokenSource(RequestTimeout);
        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), write.Token).ConfigureAwait(false);
            await stream.WriteAsync(body, write.Token).ConfigureAwait(false);
            await stream.FlushAsync(write.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The browser closed first, or the write stalled; the answer is still the callback's.
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
