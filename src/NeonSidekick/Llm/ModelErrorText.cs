using System.ClientModel;
using System.Globalization;
using System.Net.Http;
using System.Net.Sockets;

namespace NeonSidekick.Llm;

/// <summary>
/// A failed model request said in one line with what to do next (2026-10-04, the UI review: the transcript read
/// <c>Model error: ClientResultException: No connection could be made… -&gt; SocketException: No connection could be made…</c>,
/// twice over and with no way on). <see cref="Readable"/> knows the failures a user can act on — the server not reachable
/// (refused, a name that does not resolve, no answer), the key refused, no such model, the rate limit, the server's own
/// failure — and leaves every other to <see cref="Assistant.Explain"/>'s chain; the chain always goes to the log. Pure;
/// the words are pinned.
/// </summary>
public static class ModelErrorText
{
    /// <summary>What to do about a server that cannot be reached. Pinned.</summary>
    public const string UnreachableNextStep = "/server picks another; /settings › Servers › Common › LLM URL changes it.";

    /// <summary>What to do about a refused key. Pinned.</summary>
    public const string KeyNextStep = "/settings › Servers › Http › LLM API key sets it (/settings › Servers › Anthropic or OpenAI for theirs).";

    /// <summary>What to do about a model the server does not have. Pinned.</summary>
    public const string ModelNextStep = "/model picks one the server lists.";

    /// <summary>The server could not be reached: <c>Could not reach the LLM server at 127.0.0.1:9 (connection refused).</c> Pinned.</summary>
    public static string Unreachable(string? where, string why) =>
        "Could not reach the LLM server" + (string.IsNullOrEmpty(where) ? "" : " at " + where) + " (" + why + "). " + UnreachableNextStep;

    /// <summary>
    /// The one line for <paramref name="ex"/>'s failure, or null when it is none of the kinds this knows (the caller keeps the
    /// chain then). Walks the inner exceptions (and an aggregate's first) for the socket error or the server's status.
    /// </summary>
    public static string? Readable(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        // A context overflow first (2026-11-09), on the whole chain: LM Studio's 500 would otherwise read as the server failing on its side,
        // and the two APIs' exceptions as nothing this knows.
        if (Assistant.LooksLikeContextOverflow(ex) is { } overflow)
        {
            return TooLong(overflow);
        }

        string? where = null;
        for (var current = ex; current is not null; current = current is AggregateException { InnerExceptions.Count: > 0 } aggregate ? aggregate.InnerExceptions[0] : current.InnerException)
        {
            if (current is HttpRequestException http)
            {
                where ??= Where(http.Message);
            }

            if (current is SocketException socket)
            {
                return socket.SocketErrorCode switch
                {
                    SocketError.ConnectionRefused => Unreachable(where, "connection refused"),
                    SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => Unreachable(where, "the name did not resolve"),
                    SocketError.TimedOut or SocketError.HostUnreachable or SocketError.NetworkUnreachable => Unreachable(where, "no answer"),
                    SocketError.ConnectionReset or SocketError.ConnectionAborted => Unreachable(where, "the connection was dropped"),
                    _ => null,
                };
            }

            if (current is ClientResultException { Status: > 0 } failed)
            {
                string detail = Assistant.ServerDetail(failed) ?? "HTTP " + failed.Status.ToString(CultureInfo.InvariantCulture);
                return failed.Status switch
                {
                    401 or 403 => "The LLM server refused the key (" + detail + "). " + KeyNextStep,
                    404 => "The LLM server has no such model or endpoint (" + detail + "). " + ModelNextStep,
                    429 => "The LLM server is limiting requests (" + detail + "); wait a moment and send again.",
                    >= 500 and < 600 => "The LLM server failed on its side (" + detail + "); send again, or " + UnreachableNextStep,
                    _ => null,
                };
            }
        }

        return null;
    }

    /// <summary>What to do about a request too long for the window. Pinned.</summary>
    public const string TooLongNextStep = "/compact shrinks the conversation; LLM tool compact mode (compact or prune) shrinks a long reply's tool results; LLM context length sets the window.";

    /// <summary>
    /// A request the server refused as too long for the window, which the turn loop could not recover from (2026-11-09):
    /// <c>The request was too long for the model's context window (41,200 of 32,768 tokens). …</c>, the figures when both were parsed, and
    /// the reply's reservation when it is what blocked the retry (half the limit or more, the loop's own test).
    /// </summary>
    public static string TooLong(ContextOverflow overflow)
    {
        ArgumentNullException.ThrowIfNull(overflow);
        string figures = overflow is { Sent: { } sent, Limit: { } limit } ? string.Create(CultureInfo.InvariantCulture, $" ({sent:N0} of {limit:N0} tokens)") : "";
        string reply = overflow is { Output: { } output, Limit: { } window } && output * 2 >= window
            ? string.Create(CultureInfo.InvariantCulture, $"; the server keeps {output:N0} tokens for the reply")
            : "";
        return "The request was too long for the model's context window" + figures + reply + ". " + TooLongNextStep;
    }

    /// <summary>The <c>host:port</c> an <see cref="HttpRequestException"/>'s message ends with in parentheses, or null.</summary>
    private static string? Where(string message)
    {
        int open = message.LastIndexOf('(');
        if (open < 0 || !message.EndsWith(')'))
        {
            return null;
        }

        string inside = message[(open + 1)..^1];
        return inside.Length > 0 && inside.Length <= 255 && inside.Contains(':', StringComparison.Ordinal) && !inside.Contains(' ', StringComparison.Ordinal) ? inside : null;
    }
}
