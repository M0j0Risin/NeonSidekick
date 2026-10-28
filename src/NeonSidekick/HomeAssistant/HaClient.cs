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
/// A camera's still (2026-10-27, <see cref="HaClient.CameraAsync"/>): the bytes and their type when <see cref="Ok"/>, else
/// <see cref="Error"/> (an <c>Error:</c> sentence). <see cref="Status"/> is Home Assistant's answer, 0 when none came.
/// </summary>
public sealed record HaPictureReply(bool Ok, byte[] Bytes, string MediaType, int Status, string? Error)
{
    public static HaPictureReply Success(byte[] bytes, string mediaType) => new(true, bytes, mediaType, 200, null);

    public static HaPictureReply Failed(string error, int status = 0) => new(false, [], "", status, error);
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

    /// <summary>Set by <see cref="Dispose"/> before the transport goes, so a request it cut reads as replaced (2026-10-27, the review).</summary>
    private volatile bool _disposed;

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

    /// <summary>How much of a failing camera's body is read for its sentence: never the whole of it, whatever it holds.</summary>
    public const int CameraErrorBytes = 4 * 1024;

    /// <summary>The still's request path, the id escaped. Pure.</summary>
    public static string CameraPath(string entityId)
    {
        ArgumentNullException.ThrowIfNull(entityId);
        return "/api/camera_proxy/" + Uri.EscapeDataString(entityId);
    }

    /// <summary>
    /// <c>GET /api/camera_proxy/{id}</c>: a camera's still, which Home Assistant makes from whatever the integration has (2026-10-27,
    /// the home bar's camera chip, the Web UI's sheet and <c>ha_camera</c>; a Ring camera's is a frame of its latest recording).
    /// Read from the headers on, never buffered whole first: a 503, 500 or 404 is worded for a camera (<see cref="HaText.CameraOff"/>,
    /// <see cref="HaText.CameraNoPicture"/>, <see cref="HaText.NoSuchCamera"/>), another failure as <see cref="SendAsync"/> words it
    /// from at most <see cref="CameraErrorBytes"/> of its body; then the type (a 200 that is no <c>image/*</c>, a proxy's sign-in
    /// page say, is refused unread), a <c>Content-Length</c> over <see cref="HaCamera.MaxBytes"/>, and the body read under that
    /// cap, all within <paramref name="timeout"/>, so a camera that trickles its bytes still ends. A client disposed under the
    /// fetch (<see cref="HaSession.Client"/> makes a new one when the URL or token changes, and a still can take 15 s) reads as
    /// unreachable, through <see cref="Failure"/>. Never throws but the caller's cancellation.
    /// </summary>
    public async Task<HaPictureReply> CameraAsync(string entityId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        string path = CameraPath(entityId);
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(timeout);
            using var request = Request(HttpMethod.Get, path, null);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token).ConfigureAwait(false);
            int status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                string? camera = response.StatusCode switch
                {
                    HttpStatusCode.ServiceUnavailable => HaText.CameraOff,
                    HttpStatusCode.InternalServerError => HaText.CameraNoPicture,
                    HttpStatusCode.NotFound => HaText.NoSuchCamera,
                    _ => null,
                };
                if (camera is not null)
                {
                    DiagnosticLog.Debug(Category, $"GET {path} answered {status}.");
                    return HaPictureReply.Failed(camera, status);
                }

                await using var failed = await response.Content.ReadAsStreamAsync(budget.Token).ConfigureAwait(false);
                var (said, _) = await Files.StreamCap.ReadAsync(failed, CameraErrorBytes, budget.Token).ConfigureAwait(false);
                return HaPictureReply.Failed(StatusError(HttpMethod.Get, path, response.StatusCode, Encoding.UTF8.GetString(said)), status);
            }

            string type = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                return HaPictureReply.Failed(HaText.NotAPicture(type), status);
            }

            if (response.Content.Headers.ContentLength > HaCamera.MaxBytes)
            {
                return HaPictureReply.Failed(HaText.PictureTooLarge, status);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(budget.Token).ConfigureAwait(false);
            // One byte past the cap tells a picture of exactly the cap from a larger one.
            var (bytes, capped) = await Files.StreamCap.ReadAsync(stream, HaCamera.MaxBytes + 1, budget.Token).ConfigureAwait(false);
            return capped ? HaPictureReply.Failed(HaText.PictureTooLarge, status) : HaPictureReply.Success(bytes, type.ToLowerInvariant());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (Failure(ex, timeout) is { } error)
        {
            return HaPictureReply.Failed(error);
        }
    }

    private async Task<HaReply> SendAsync(HttpMethod method, string path, string? body, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(timeout);
            using var request = Request(method, path, body);
            using var response = await _http.SendAsync(request, budget.Token).ConfigureAwait(false);
            string text = await response.Content.ReadAsStringAsync(budget.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? HaReply.Success(text) : HaReply.Failed(StatusError(method, path, response.StatusCode, text));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (Failure(ex, timeout) is { } error)
        {
            return HaReply.Failed(error);
        }
    }

    private HttpRequestMessage Request(HttpMethod method, string path, string? body)
    {
        var request = new HttpRequestMessage(method, new Uri(BaseUrl + path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return request;
    }

    /// <summary>A non-2xx answer's sentence, logged: the refused token, else the status, the endpoint and the server's words.</summary>
    private static string StatusError(HttpMethod method, string path, HttpStatusCode status, string body)
    {
        DiagnosticLog.Debug(Category, $"{method} {path} answered {(int)status}.");
        return status == HttpStatusCode.Unauthorized ? HaText.Unauthorized : HaText.HttpError((int)status, PathOnly(path), body);
    }

    /// <summary>
    /// The sentence for a request that got no answer: the client was replaced under it, the budget ran out, or the server could not
    /// be reached; null for any other exception. A disposed client cancels its requests, which read as the budget running out, and a
    /// request sent after reads as .NET's own words: both say <see cref="HaText.ClientReplaced"/> instead (2026-10-27, the review).
    /// </summary>
    private string? Failure(Exception ex, TimeSpan timeout) => ex switch
    {
        _ when _disposed => HaText.Unreachable(BaseUrl, HaText.ClientReplaced),
        ObjectDisposedException => HaText.Unreachable(BaseUrl, HaText.ClientReplaced),
        OperationCanceledException => HaText.NoAnswer(LlmTimeouts.Format(timeout)),
        HttpRequestException or IOException => HaText.Unreachable(BaseUrl, ex.Message),
        _ => null,
    };

    /// <summary>A request path without its query, for an error sentence.</summary>
    private static string PathOnly(string path)
    {
        int query = path.IndexOf('?', StringComparison.Ordinal);
        return query < 0 ? path : path[..query];
    }

    public void Dispose()
    {
        _disposed = true;
        _owned?.Dispose();
    }
}
