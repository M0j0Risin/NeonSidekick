using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using NeonSidekick.Diagnostics;
using NeonSidekick.Llm;

namespace NeonSidekick.HomeAssistant;

/// <summary>One answer from Home Assistant: the body when <see cref="Ok"/>, else <see cref="Error"/> (an <c>Error:</c> sentence).</summary>
public sealed record HaReply(bool Ok, string Body, string? Error)
{
    public static HaReply Success(string body) => new(true, body, null);

    public static HaReply Failed(string error) => new(false, "", error);
}

/// <summary>
/// Home Assistant's REST API, the part the <c>ha_</c> tools and <c>/ha</c> use (2026-09-28, the user's ask: "plan an
/// integration for Home Assistant" — a Docker instance at <c>http://localhost:8123</c> with Hue lights and a Bravia TV):
/// <c>GET /api/</c> (the ping), <c>GET /api/config</c>, <c>GET /api/states</c>, <c>GET /api/states/{id}</c>,
/// <c>POST /api/services/{domain}/{service}</c> (with <c>?return_response</c> for a service that answers, such as
/// <c>todo.get_items</c>), <c>POST /api/template</c> (the areas, which the REST API has no endpoint for),
/// <c>GET /api/history/period/{start}</c> and <c>POST /api/conversation/process</c> (Assist). Every request carries the
/// long-lived access token as <c>Authorization: Bearer</c>.
///
/// <para><see cref="Comfy.ComfyClient"/>'s shape: the transport is injectable (tests pass one over a stub handler; an
/// owned one is disposed here), each call has its own linked-CTS ceiling, and nothing throws but the caller's own
/// cancellation — a failure is an <see cref="HaReply"/> with an <see cref="HaText"/> sentence. The server is the user's
/// own, like the LLM's and ComfyUI's, so the transport is a plain <see cref="HttpClient"/>, never the web tools' one that
/// <see cref="Web.LanPolicy"/> judges: under the default <c>internet</c> network mode that one refuses localhost.</para>
/// </summary>
public sealed class HaClient : IDisposable
{
    private const string Category = HaText.Category;

    private readonly HttpClient _http;
    private readonly HttpClient? _owned;
    private readonly string _token;

    /// <param name="baseUrl">The server, <c>http://host:8123</c>; a trailing slash is dropped.</param>
    /// <param name="token">The long-lived access token (plain; <see cref="HaSession.TokenOf"/> decrypts the stored one).</param>
    /// <param name="httpClient">Optional transport, the caller's to dispose. Null creates and owns one.</param>
    public HaClient(Uri baseUrl, string token, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(token);
        if (!Web.WebFetcher.IsHttp(baseUrl))
        {
            throw new ArgumentException("The Home Assistant URL must be http or https.", nameof(baseUrl));
        }

        BaseUrl = baseUrl.AbsoluteUri.TrimEnd('/');
        _token = token.Trim();
        if (httpClient is not null)
        {
            _http = httpClient;
        }
        else
        {
            // The per-call budgets are the ceilings; the client's own timeout would only race them.
            _owned = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            _http = _owned;
        }
    }

    /// <summary>The server without a trailing slash.</summary>
    public string BaseUrl { get; }

    /// <summary><c>GET /api/</c>: <c>{"message": "API running."}</c> when the server is up and the token good.</summary>
    public Task<HaReply> PingAsync(TimeSpan timeout, CancellationToken cancellationToken) => SendAsync(HttpMethod.Get, "/api/", null, timeout, cancellationToken);

    /// <summary><c>GET /api/config</c>: the version, the location's name, the time zone, the units.</summary>
    public Task<HaReply> ConfigAsync(TimeSpan timeout, CancellationToken cancellationToken) => SendAsync(HttpMethod.Get, "/api/config", null, timeout, cancellationToken);

    /// <summary><c>GET /api/states</c>: every entity's state and attributes, one array.</summary>
    public Task<HaReply> StatesAsync(TimeSpan timeout, CancellationToken cancellationToken) => SendAsync(HttpMethod.Get, "/api/states", null, timeout, cancellationToken);

    /// <summary><c>POST /api/template</c>: <paramref name="template"/> rendered, as plain text.</summary>
    public Task<HaReply> TemplateAsync(string template, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(template);
        return SendAsync(HttpMethod.Post, "/api/template", HaJson.Object(w => w.WriteString("template", template)), timeout, cancellationToken);
    }

    /// <summary>
    /// <c>POST /api/services/{domain}/{service}</c> with <paramref name="body"/> (a JSON object: <c>entity_id</c> and the
    /// service's data). <paramref name="returnResponse"/> asks for the service's own answer (<c>?return_response</c>), which a
    /// service that gives none refuses with a 400.
    /// </summary>
    public Task<HaReply> CallServiceAsync(string domain, string service, string body, bool returnResponse, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domain);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(body);
        string path = "/api/services/" + Uri.EscapeDataString(domain) + "/" + Uri.EscapeDataString(service) + (returnResponse ? "?return_response" : "");
        return SendAsync(HttpMethod.Post, path, body, timeout, cancellationToken);
    }

    /// <summary>
    /// <c>GET /api/history/period/{start}?filter_entity_id={id}&amp;end_time={end}&amp;minimal_response&amp;no_attributes</c>:
    /// the entity's states from <paramref name="start"/> to <paramref name="end"/>, one array per entity.
    /// </summary>
    public Task<HaReply> HistoryAsync(string entityId, DateTimeOffset start, DateTimeOffset end, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entityId);
        return SendAsync(HttpMethod.Get, HistoryPath(entityId, start, end), null, timeout, cancellationToken);
    }

    /// <summary>The history request's path, every part escaped, the times in UTC ISO 8601. Pure.</summary>
    public static string HistoryPath(string entityId, DateTimeOffset start, DateTimeOffset end)
    {
        ArgumentNullException.ThrowIfNull(entityId);
        return "/api/history/period/" + Uri.EscapeDataString(Iso(start))
            + "?filter_entity_id=" + Uri.EscapeDataString(entityId)
            + "&end_time=" + Uri.EscapeDataString(Iso(end))
            + "&minimal_response&no_attributes";
    }

    private static string Iso(DateTimeOffset time) => time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'+00:00'", CultureInfo.InvariantCulture);

    /// <summary><c>POST /api/conversation/process</c>: <paramref name="text"/> handed to Assist, to <paramref name="agentId"/> when one is named.</summary>
    public Task<HaReply> ConversationAsync(string text, string? agentId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        string body = HaJson.Object(w =>
        {
            w.WriteString("text", text);
            if (!string.IsNullOrWhiteSpace(agentId))
            {
                w.WriteString("agent_id", agentId.Trim());
            }
        });
        return SendAsync(HttpMethod.Post, "/api/conversation/process", body, timeout, cancellationToken);
    }

    private async Task<HaReply> SendAsync(HttpMethod method, string path, string? body, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(timeout);
            using var request = new HttpRequestMessage(method, new Uri(BaseUrl + path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            if (body is not null)
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }

            using var response = await _http.SendAsync(request, budget.Token).ConfigureAwait(false);
            string text = await response.Content.ReadAsStringAsync(budget.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                DiagnosticLog.Debug(Category, $"{method} {path} answered {(int)response.StatusCode}.");
                return HaReply.Failed(response.StatusCode == HttpStatusCode.Unauthorized ? HaText.Unauthorized : HaText.HttpError((int)response.StatusCode, PathOnly(path), text));
            }

            return HaReply.Success(text);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return HaReply.Failed(HaText.NoAnswer(LlmTimeouts.Format(timeout)));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return HaReply.Failed(HaText.Unreachable(BaseUrl, ex.Message));
        }
    }

    /// <summary>A request path without its query, for an error sentence.</summary>
    private static string PathOnly(string path)
    {
        int query = path.IndexOf('?', StringComparison.Ordinal);
        return query < 0 ? path : path[..query];
    }

    public void Dispose() => _owned?.Dispose();
}
