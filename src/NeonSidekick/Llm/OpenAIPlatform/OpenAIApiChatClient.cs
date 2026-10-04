using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using NeonSidekick.Diagnostics;
using NeonSidekick.Llm.Anthropic;

namespace NeonSidekick.Llm.OpenAIPlatform;

/// <summary>
/// The OpenAI API's <see cref="IChatClient"/> (2026-10-03): one streamed <c>POST /v1/responses</c> per request, written by
/// <see cref="OpenAIRequest"/> and read back by <see cref="OpenAIStream"/> — <see cref="AnthropicChatClient"/>'s shape, a
/// hand-written client over <see cref="HttpClient"/>, since the SDK's Responses client and the MEAI adapter over it are
/// evaluation-only and this project suppresses nothing.
///
/// <para>The first cut spoke Chat Completions through <see cref="OpenAICompatibleChatClient"/>; the live sweep the same day
/// found GPT-5.4 and newer refuse function tools beside any <c>reasoning_effort</c> but <c>none</c> there ("use /v1/responses
/// or set reasoning_effort to 'none'"), and GPT-6 Sol, 6.1 Sol and Astra — no <c>none</c> — every tool at all. The user's call:
/// the Responses API for every model, which also streams a readable summary of the reasoning.</para>
///
/// <para>The key is a Bearer token; the organization and project go as the <c>OpenAI-Organization</c> / <c>OpenAI-Project</c>
/// headers when set. One retry for the API's "come back shortly" answers (429 but a spent quota, 500, 502, 503) before anything
/// streamed, after the <c>retry-after</c> it names; and one more, without the reasoning items, when the API refuses the
/// encrypted reasoning sent back (another organization's key, an edited turn). Every other failure is an
/// <see cref="OpenAIApiException"/> with the API's own message.</para>
/// </summary>
public sealed class OpenAIApiChatClient : IChatClient
{
    private const string Category = "Llm";

    private readonly HttpClient _http;
    private readonly HttpClient? _ownedHttpClient;
    private readonly TimeProvider _time;
    private readonly Uri _responsesUrl;
    private readonly string? _organization;
    private readonly string? _project;

    /// <param name="endpoint">The OpenAI API endpoint, its key the OpenAI API's own (<see cref="ApiKeys.For"/>).</param>
    /// <param name="requestTimeout">Per-request ceiling on an owned transport.</param>
    /// <param name="maxTokens"><c>OpenAI API max tokens</c>: 0 sends none, else clamped to the setting's range.</param>
    /// <param name="organization">The <c>OpenAI-Organization</c> header, or null for none.</param>
    /// <param name="project">The <c>OpenAI-Project</c> header, or null for none.</param>
    /// <param name="httpClient">Optional transport; tests pass one over a stub handler. It stays the caller's.</param>
    /// <param name="time">The clock the retry waits on; tests pass a manual one.</param>
    public OpenAIApiChatClient(LlmEndpoint endpoint, TimeSpan requestTimeout, int maxTokens = 0, string? organization = null, string? project = null, HttpClient? httpClient = null, TimeProvider? time = null)
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
        MaxTokens = maxTokens <= 0 ? 0 : Math.Clamp(maxTokens, Settings.AppSettingsData.MinOpenAIApiMaxTokens, Settings.AppSettingsData.MaxOpenAIApiMaxTokens);
        _organization = string.IsNullOrWhiteSpace(organization) ? null : organization.Trim();
        _project = string.IsNullOrWhiteSpace(project) ? null : project.Trim();
        _responsesUrl = OpenAIApi.ResponsesUrl(v1);
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

    /// <summary>The <c>max_output_tokens</c> every request carries, or 0 for none.</summary>
    public int MaxTokens { get; }

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
        byte[] body = OpenAIRequest.Write(list, options, Endpoint.ModelId, MaxTokens);
        var reader = new OpenAIStream(Endpoint.ModelId, AnthropicChatClient.ToolNames(options?.Tools));

        using var response = await SendAsync(body, () => OpenAIRequest.Write(list, options, Endpoint.ModelId, MaxTokens, withoutReasoning: true), cancellationToken).ConfigureAwait(false);
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

    /// <summary>What a stream that ended before <c>response.completed</c> says. Pinned.</summary>
    public const string StreamCutMessage = "The OpenAI API stream ended before the reply did.";

    /// <summary>The log line of the reasoning recovery. Pinned.</summary>
    public const string ReasoningDroppedWarning = "The OpenAI API refused the reasoning sent back; retrying once without it.";

    private async Task<HttpResponseMessage> SendAsync(byte[] body, Func<byte[]> withoutReasoning, CancellationToken cancellationToken)
    {
        bool retriedBusy = false;
        bool stripped = false;
        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _responsesUrl) { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            OpenAIApi.AddHeaders(request, Endpoint.ApiKey, _organization, _project);
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
                delay = AnthropicChatClient.RetryDelay(response);
            }

            if (!retriedBusy && IsRetryable(status, text))
            {
                retriedBusy = true;
                DiagnosticLog.Warn(Category, $"The OpenAI API answered {status.ToString(CultureInfo.InvariantCulture)}; retrying once in {delay.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s.");
                await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!stripped && IsReasoningRefusal(status, text))
            {
                byte[] plain = withoutReasoning();
                if (!plain.AsSpan().SequenceEqual(body))
                {
                    DiagnosticLog.Warn(Category, ReasoningDroppedWarning);
                    stripped = true;
                    body = plain;
                    continue;
                }
            }

            throw new OpenAIApiException(status, ErrorCode(text), Assistant.ServerMessage(text));
        }
    }

    /// <summary>429 (a rate limit, not a spent quota, which a retry cannot help), 500, 502 and 503: worth one more try.</summary>
    internal static bool IsRetryable(int status, string body) =>
        status is 500 or 502 or 503 || (status == 429 && !body.Contains("insufficient_quota", StringComparison.Ordinal));

    /// <summary>A 400 whose message names the encrypted content or a reasoning item: the one the stripped retry answers.</summary>
    internal static bool IsReasoningRefusal(int status, string body) =>
        status == 400 && (body.Contains("encrypted", StringComparison.OrdinalIgnoreCase) || body.Contains("reasoning", StringComparison.OrdinalIgnoreCase));

    /// <summary>The <c>error.code</c> of an error body, else its <c>error.type</c>, else <c>error</c>.</summary>
    private static string ErrorCode(string body)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error"u8, out var error) && error.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (string name in new[] { "code", "type" })
                {
                    if (error.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String && value.GetString() is { Length: > 0 } word)
                    {
                        return word;
                    }
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }

        return "error";
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
            return new ChatClientMetadata("openai", Endpoint.BaseUrl, Endpoint.ModelId);
        }

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    /// <inheritdoc/>
    public void Dispose() => _ownedHttpClient?.Dispose();
}
