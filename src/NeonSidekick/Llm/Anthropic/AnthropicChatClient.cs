using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Llm.Anthropic;

/// <summary>
/// The Claude API's <see cref="IChatClient"/> (2026-09-27, the user's call: a hand-written client over
/// <see cref="HttpClient"/>, since the official SDK is not AOT-clean and this build refuses reflection). One streamed
/// <c>POST /v1/messages</c> per request: <see cref="AnthropicRequest"/> writes the body, <see cref="AnthropicStream"/>
/// reads the events back into the updates the turn loop already consumes, so <see cref="Assistant"/>, the compactor
/// and the skill learner run over it unchanged.
///
/// <para>One retry, for the API's "come back shortly" answers (429, 529, 503) before anything streamed, after the
/// <c>retry-after</c> it names (at most <see cref="MaxRetryDelay"/>); every other failure is an
/// <see cref="AnthropicApiException"/> with the API's own message. The OpenAI path retries nothing because a local
/// server that refuses is down; a busy hosted API is usually back in a second.</para>
///
/// <para>And one more, the API's documented recovery for signed thinking it will not take back (a 400 that names
/// thinking): the same request with every thinking block stripped (<see cref="AnthropicRequest.Write"/>'s
/// <c>withoutThinking</c>). The request normally sends back only the turn in flight's blocks, which nothing edits —
/// except the mid-turn prune or compact (<c>LLM tool compact type</c>), which rewrites that turn's tool results under them; the
/// model then carries on without the reasoning of the calls so far, which beats a failed turn.</para>
///
/// <para>The two are independent (2026-09-28, code review): the busy retry was the first attempt's alone, so a 429 or 529
/// answering the stripped resend failed the turn. Each now happens at most once whichever comes first, three requests at
/// most.</para>
/// </summary>
public sealed class AnthropicChatClient : IChatClient
{
    private const string Category = "Llm";

    /// <summary>The longest wait before the one retry.</summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(20);

    /// <summary>The wait before the retry when the API names none.</summary>
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(2);

    private readonly HttpClient _http;
    private readonly HttpClient? _ownedHttpClient;
    private readonly TimeProvider _time;
    private readonly Uri _messagesUrl;

    /// <param name="endpoint">The Claude API endpoint; its <see cref="LlmEndpoint.ApiKey"/> is the Claude API key.</param>
    /// <param name="requestTimeout">Per-request ceiling on an owned transport.</param>
    /// <param name="maxTokens">The output cap every request carries (the API requires one).</param>
    /// <param name="promptCaching">Whether requests carry cache breakpoints.</param>
    /// <param name="httpClient">Optional transport; tests pass one over a stub handler. It stays the caller's.</param>
    /// <param name="time">The clock the retry waits on; tests pass a manual one.</param>
    public AnthropicChatClient(LlmEndpoint endpoint, TimeSpan requestTimeout, int maxTokens, bool promptCaching, HttpClient? httpClient = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (string.IsNullOrWhiteSpace(endpoint.ModelId))
        {
            throw new ArgumentException("Model id must not be blank.", nameof(endpoint));
        }

        if (requestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout), requestTimeout, "The request timeout must be positive.");
        }

        var v1 = LlmEndpoint.NormalizeBaseUrl(endpoint.BaseUrl);
        Endpoint = endpoint with { BaseUrl = v1, ApiKey = endpoint.ApiKey?.Trim() ?? "" };
        MaxTokens = Math.Clamp(maxTokens, Settings.AppSettingsData.MinClaudeApiMaxTokens, Settings.AppSettingsData.MaxClaudeApiMaxTokens);
        PromptCaching = promptCaching;
        _messagesUrl = ClaudeApi.MessagesUrl(v1);
        _time = time ?? TimeProvider.System;
        if (httpClient is null)
        {
            _ownedHttpClient = new HttpClient { Timeout = requestTimeout };
            _http = _ownedHttpClient;
        }
        else
        {
            _http = httpClient;
        }
    }

    /// <summary>The endpoint in use, base URL normalised to <c>/v1</c>.</summary>
    public LlmEndpoint Endpoint { get; }

    /// <summary>The <c>max_tokens</c> every request carries.</summary>
    public int MaxTokens { get; }

    /// <summary>Whether requests carry cache breakpoints.</summary>
    public bool PromptCaching { get; }

    /// <inheritdoc/>
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        await GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        byte[] body = AnthropicRequest.Write(list, options, Endpoint.ModelId, MaxTokens, PromptCaching);
        var reader = new AnthropicStream(Endpoint.ModelId, ToolNames(options?.Tools));

        using var response = await SendAsync(body, () => AnthropicRequest.Write(list, options, Endpoint.ModelId, MaxTokens, PromptCaching, withoutThinking: true), cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var lines = new StreamReader(stream, Encoding.UTF8);
        string? eventName = null;
        var data = new StringBuilder();
        while (await lines.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    foreach (var update in reader.Handle(eventName, data.ToString()))
                    {
                        yield return update;
                    }
                }

                eventName = null;
                data.Clear();
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = line[6..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0)
                {
                    data.Append('\n');
                }

                data.Append(line.AsSpan(5).TrimStart());
            }
        }

        if (data.Length > 0)
        {
            foreach (var update in reader.Handle(eventName, data.ToString()))
            {
                yield return update;
            }
        }

        if (!reader.Stopped)
        {
            throw new IOException(StreamCutMessage);
        }
    }

    /// <summary>What a stream that ended before <c>message_stop</c> says. Pinned.</summary>
    public const string StreamCutMessage = "The Claude API stream ended before the reply did.";

    /// <summary>The wire names <see cref="AnthropicRequest.ToolName"/> changed, mapped back to the tools' own.</summary>
    internal static IReadOnlyDictionary<string, string> ToolNames(IList<AITool>? tools)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var function in tools?.OfType<AIFunctionDeclaration>() ?? [])
        {
            string wire = AnthropicRequest.ToolName(function.Name);
            if (!string.Equals(wire, function.Name, StringComparison.Ordinal))
            {
                map[wire] = function.Name;
            }
        }

        return map;
    }

    private async Task<HttpResponseMessage> SendAsync(byte[] body, Func<byte[]> withoutThinking, CancellationToken cancellationToken)
    {
        bool retriedBusy = false;
        bool stripped = false;
        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _messagesUrl) { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            ClaudeApi.AddHeaders(request, Endpoint.ApiKey);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            int status = (int)response.StatusCode;
            string text;
            TimeSpan delay;
            using (response)
            {
                text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                delay = RetryDelay(response);
            }

            if (!retriedBusy && IsRetryable((HttpStatusCode)status))
            {
                retriedBusy = true;
                DiagnosticLog.Warn(Category, $"The Claude API answered {status.ToString(CultureInfo.InvariantCulture)}; retrying once in {delay.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s.");
                await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!stripped && IsThinkingRefusal(status, text))
            {
                byte[] plain = withoutThinking();
                if (!plain.AsSpan().SequenceEqual(body))
                {
                    DiagnosticLog.Warn(Category, ThinkingDroppedWarning);
                    stripped = true;
                    body = plain;
                    continue;
                }
            }

            throw new AnthropicApiException(status, ErrorType(text), Assistant.ServerMessage(text));
        }
    }

    /// <summary>The log line of the thinking recovery. Pinned.</summary>
    public const string ThinkingDroppedWarning = "The Claude API refused the thinking sent back (the turn was edited under it); retrying once without it.";

    /// <summary>
    /// A 400 whose message names thinking or a signature: the one the stripped retry answers.
    ///
    /// <para>Left this wide on purpose (2026-09-28, code review, which asked for "signature" alone): the API's refusal of an
    /// edited turn does not always name the signature ("thinking blocks … cannot be modified"), and a failed turn costs more
    /// than the one wasted resend a 400 about something else gets — only when there was thinking to strip, since
    /// <see cref="SendAsync"/> resends nothing that would be the same body.</para>
    /// </summary>
    internal static bool IsThinkingRefusal(int status, string body) =>
        status == 400 && (body.Contains("thinking", StringComparison.OrdinalIgnoreCase) || body.Contains("signature", StringComparison.OrdinalIgnoreCase));

    /// <summary>429 (rate limit), 529 (overloaded) and 503: worth one more try.</summary>
    internal static bool IsRetryable(HttpStatusCode status) => (int)status is 429 or 529 or 503;

    /// <summary>The <c>retry-after</c> in seconds, clamped to <see cref="MaxRetryDelay"/>; <see cref="DefaultRetryDelay"/> without one.</summary>
    internal static TimeSpan RetryDelay(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is { } after)
        {
            var delay = after.Delta ?? (after.Date is { } date ? date - DateTimeOffset.UtcNow : DefaultRetryDelay);
            return delay < TimeSpan.Zero ? TimeSpan.Zero : delay > MaxRetryDelay ? MaxRetryDelay : delay;
        }

        return DefaultRetryDelay;
    }

    /// <summary>The <c>error.type</c> of an error body, or <c>error</c>.</summary>
    private static string ErrorType(string body)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error"u8, out var error)
                && error.ValueKind == System.Text.Json.JsonValueKind.Object
                && error.TryGetProperty("type"u8, out var type)
                && type.ValueKind == System.Text.Json.JsonValueKind.String
                    ? type.GetString() ?? "error"
                    : "error";
        }
        catch (System.Text.Json.JsonException)
        {
            return "error";
        }
    }

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(ChatClientMetadata))
        {
            return new ChatClientMetadata("anthropic", Endpoint.BaseUrl, Endpoint.ModelId);
        }

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <inheritdoc/>
    public void Dispose() => _ownedHttpClient?.Dispose();
}
