using System.Net;
using System.Text;

namespace NeonSidekick.Tests.Fakes;

/// <summary>What the stub saw, copied out before the request is disposed.</summary>
public sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Authorization, string? Body);

/// <summary>
/// An <see cref="HttpMessageHandler"/> that answers by URL prefix. Anything unmapped is a refused
/// connection (<see cref="HttpRequestException"/>), which is what a closed port looks like.
/// </summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly List<(string Prefix, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Handler)> _routes = new();

    public List<RecordedRequest> Requests { get; } = new();

    public StubHttpMessageHandler Map(string urlPrefix, HttpStatusCode status, string body, string mediaType = "application/json")
        => Map(urlPrefix, (_, _) => Task.FromResult(Json(status, body, mediaType)));

    public StubHttpMessageHandler Map(string urlPrefix, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        _routes.Add((urlPrefix, handler));
        return this;
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string body, string mediaType = "application/json") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    /// <summary>An OpenAI <c>/v1/models</c> payload listing <paramref name="ids"/>.</summary>
    public static string ModelsJson(params string[] ids) =>
        "{\"object\":\"list\",\"data\":[" +
        string.Join(",", ids.Select(id => "{\"id\":\"" + id + "\",\"object\":\"model\"}")) +
        "]}";

    /// <summary>A Kokoro-FastAPI <c>/v1/audio/voices</c> payload listing <paramref name="ids"/>.</summary>
    public static string VoicesJson(params string[] ids) =>
        "{\"voices\":[" + string.Join(",", ids.Select(id => "\"" + id + "\"")) + "]}";

    /// <summary>A raw-bytes response, for streamed PCM.</summary>
    public static HttpResponseMessage Bytes(HttpStatusCode status, byte[] body, string mediaType = "audio/pcm")
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
        return new HttpResponseMessage(status) { Content = content };
    }

    /// <summary>A minimal non-streaming chat completion.</summary>
    public static string CompletionJson(string content, string model = "local-model") =>
        "{\"id\":\"chatcmpl-stub\",\"object\":\"chat.completion\",\"created\":1700000000,\"model\":\"" + model + "\"," +
        "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"" + content + "\"},\"finish_reason\":\"stop\"}]," +
        "\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":4,\"total_tokens\":9}}";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Requests)
        {
            // A parallel download (2026-09-30) sends its ranged requests at once.
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body));
        }

        string url = request.RequestUri!.AbsoluteUri;
        foreach (var (prefix, handler) in _routes)
        {
            if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return await handler(request, cancellationToken);
            }
        }

        throw new HttpRequestException($"No connection could be made because the target machine actively refused it ({request.RequestUri.Authority}).");
    }
}
