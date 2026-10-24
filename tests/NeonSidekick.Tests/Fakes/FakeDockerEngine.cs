using System.Net;
using System.Net.Http.Headers;
using System.Threading.Channels;
using NeonSidekick.Docker;
using NeonSidekick.Settings;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// A Docker engine for the Docker log window's feed (2026-10-23): containers by name (id, state, start time) answered by an
/// inspect by id or name, each log follow and each events read a <see cref="LiveStream"/> handed to the test as it is asked for,
/// a refusal status for the logs, and <see cref="Down"/> for an engine that cannot be reached. Over
/// <see cref="StubHttpMessageHandler"/>, as the other Docker tests' engines are.
/// </summary>
public sealed class FakeDockerEngine : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Container> _containers = new(StringComparer.Ordinal);
    private readonly Channel<Opened> _follows = Channel.CreateUnbounded<Opened>();
    private readonly Channel<Opened> _events = Channel.CreateUnbounded<Opened>();
    private int _requests;

    public FakeDockerEngine(Func<AppSettingsData>? settings = null)
    {
        var stub = new StubHttpMessageHandler();
        stub.Map(DockerClient.Host, (request, _) => Task.FromResult(Answer(request)));
        var data = settings ?? (() => new AppSettingsData());
        Session = new DockerSession(data, pipe => new DockerClient(pipe, new HttpClient(stub), transport: DockerTransport.Pipe));
    }

    /// <summary>One container's state as the inspect answers it.</summary>
    public sealed record Container(string Id, string Status, string StartedAt);

    /// <summary>A follow or events read the feed opened: its path and query, and the body the test writes into.</summary>
    public sealed record Opened(string PathAndQuery, LiveStream Body);

    /// <summary>The session the feed reads through.</summary>
    public DockerSession Session { get; }

    /// <summary>Whether every request fails as an engine that is not running.</summary>
    public volatile bool Down;

    /// <summary>A status the logs answer with instead of a stream (501: a logging driver that cannot be read); null streams.</summary>
    public HttpStatusCode? LogsStatus { get; set; }

    /// <summary>How many requests have come.</summary>
    public int Requests => Volatile.Read(ref _requests);

    public void Set(string name, string id, string status = "running", string startedAt = "2026-10-23T10:00:00.000000001Z")
    {
        lock (_gate)
        {
            _containers[name] = new Container(id, status, startedAt);
        }
    }

    public void Remove(string name)
    {
        lock (_gate)
        {
            _containers.Remove(name);
        }
    }

    /// <summary>The next log follow the feed opens (ten seconds at most).</summary>
    public Task<Opened> NextFollowAsync() => ReadAsync(_follows);

    /// <summary>The next events read the feed opens.</summary>
    public Task<Opened> NextEventsAsync() => ReadAsync(_events);

    /// <summary>Whether a follow has been opened and not taken yet.</summary>
    public bool FollowWaiting => _follows.Reader.Count > 0;

    private static async Task<Opened> ReadAsync(Channel<Opened> channel)
    {
        using var cap = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await channel.Reader.ReadAsync(cap.Token);
    }

    /// <summary>A container event's line, as <c>/events</c> writes it.</summary>
    public static string Event(string action, string id, string name) =>
        "{\"Type\":\"container\",\"Action\":\"" + action + "\",\"Actor\":{\"ID\":\"" + id + "\",\"Attributes\":{\"name\":\"" + name + "\"}}}\n";

    private HttpResponseMessage Answer(HttpRequestMessage request)
    {
        Interlocked.Increment(ref _requests);
        if (Down)
        {
            throw new HttpRequestException("No connection could be made because the target machine actively refused it (docker).");
        }

        string path = request.RequestUri!.AbsolutePath;
        if (path == "/version")
        {
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"Version":"29.0.0","ApiVersion":"1.47","MinAPIVersion":"1.24","Os":"linux","Arch":"amd64"}""");
        }

        string pathAndQuery = request.RequestUri.PathAndQuery;
        if (path.StartsWith("/v1.47/containers/", StringComparison.Ordinal) && path.EndsWith("/json", StringComparison.Ordinal))
        {
            string key = Uri.UnescapeDataString(path["/v1.47/containers/".Length..^"/json".Length]);
            lock (_gate)
            {
                foreach (var (name, c) in _containers)
                {
                    if (name == key || c.Id == key)
                    {
                        return StubHttpMessageHandler.Json(HttpStatusCode.OK, "{\"Id\":\"" + c.Id + "\",\"Name\":\"/" + name + "\",\"State\":{\"Status\":\"" + c.Status + "\",\"ExitCode\":0,\"StartedAt\":\"" + c.StartedAt + "\"},\"HostConfig\":{},\"NetworkSettings\":{}}");
                    }
                }
            }

            return StubHttpMessageHandler.Json(HttpStatusCode.NotFound, "{\"message\":\"No such container: " + key + "\"}");
        }

        if (path.StartsWith("/v1.47/containers/", StringComparison.Ordinal) && path.EndsWith("/logs", StringComparison.Ordinal))
        {
            if (LogsStatus is { } status)
            {
                return StubHttpMessageHandler.Json(status, "{\"message\":\"configured logging driver does not support reading\"}");
            }

            return Streamed(_follows, pathAndQuery, DockerLogStream.MultiplexedType);
        }

        if (path == "/v1.47/events")
        {
            return Streamed(_events, pathAndQuery, "application/json");
        }

        throw new HttpRequestException("No route in the fake engine: " + pathAndQuery);
    }

    private static HttpResponseMessage Streamed(Channel<Opened> channel, string pathAndQuery, string mediaType)
    {
        var body = new LiveStream();
        channel.Writer.TryWrite(new Opened(pathAndQuery, body));
        var content = new StreamContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    public void Dispose() => Session.Dispose();
}
