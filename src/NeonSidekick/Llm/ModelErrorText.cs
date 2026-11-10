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
        if (ContextOverflowMatch.From(ex) is { } overflow)
        {
            return TooLong(overflow);
        }

        string? where = null;
        foreach (var current in Assistant.ExceptionChain(ex))
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

    /// <summary>
    /// What to do about a request too long for the window (2026-11-10, review P3-8/P9-4): <c>/compact</c>, the tool compact mode, and the
    /// window, which a lower <c>LLM context length</c> makes the guard act within while the server's own is set on the server. Pinned.
    /// </summary>
    public const string TooLongNextStep = "/compact shrinks the conversation; LLM tool compact mode (compact or prune) shrinks a long reply's tool results; " + WindowNextStep;

    /// <summary>
    /// <see cref="TooLongNextStep"/> under the tool compact modes that shrink (compact, prune), whose retry just failed, so turning one on
    /// is no advice (2026-11-10, review P3-8). Pinned.
    /// </summary>
    public const string TooLongShrinkingNextStep = "/compact shrinks the conversation; " + WindowNextStep;

    /// <summary>The window's half of the advice (2026-11-10, review P9-4). Pinned.</summary>
    public const string WindowNextStep = "a lower LLM context length makes the guard act sooner, and the server's own window is set on the server (Embedded context size for the embedded model).";

    /// <summary>The way on when the user's message alone is past the window (2026-11-10, review P11-2/P24-2). Pinned.</summary>
    public const string MessageNextStep = "Your message and its attachments alone are past the window: send it in parts, or attach less.";

    /// <summary>The way on when the fixed part (the instructions and the tool list) alone is past the window (2026-11-10, review P3-1). Pinned.</summary>
    public const string FixedNextStep = "The instructions and tool list alone are past the window: turn off tool families in /tools, or use a larger window on the server.";

    /// <summary>
    /// A request the server refused as too long for the window, which the turn loop could not recover from (2026-11-09):
    /// <c>The request was too long for the model's context window (41,200 of 32,768 tokens). …</c>, the figures when both were parsed
    /// (<see cref="ContextOverflow.Figures"/>, the reply counted in), the reply asked for when it is what blocked the retry
    /// (<see cref="ContextOverflow.ReplyBlocks"/>), and the way on: by <paramref name="reason"/> when the turn ended without a shrink
    /// (2026-11-10), else by <paramref name="mode"/>.
    /// </summary>
    public static string TooLong(ContextOverflow overflow, ToolCompactMode? mode = null, TooLongReason reason = TooLongReason.None)
    {
        ArgumentNullException.ThrowIfNull(overflow);
        string reply = overflow is { Output: { } output } && overflow.ReplyBlocks(null)
            ? string.Create(CultureInfo.InvariantCulture, $"; the request asks for {output:N0} tokens of reply")
            : "";
        string next = reason switch
        {
            TooLongReason.Message => MessageNextStep,
            TooLongReason.Fixed => FixedNextStep,
            _ => mode is ToolCompactMode.Compact or ToolCompactMode.Prune ? TooLongShrinkingNextStep : TooLongNextStep,
        };
        return "The request was too long for the model's context window" + overflow.Figures + reply + ". " + next;
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

/// <summary>
/// Why a too-long turn ended without a shrink (2026-11-10, review P11-2/P24-2/P3-1), for <see cref="ModelErrorText.TooLong"/>'s way on:
/// none (the shrink ran and didn't fit, or no shrink applies), the user's message alone past the window, or the fixed part alone past it.
/// </summary>
public enum TooLongReason
{
    None,
    Message,
    Fixed,
}
