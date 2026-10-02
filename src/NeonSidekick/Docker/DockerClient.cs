using System.Globalization;
using System.Net;
using System.Text;
using NeonSidekick.Diagnostics;
using NeonSidekick.Llm;

namespace NeonSidekick.Docker;

/// <summary>One answer from the engine: the body when <see cref="Ok"/> (a 2xx, or a 304 — "already so"), else <see cref="Error"/> (an <c>Error:</c> sentence).</summary>
public sealed record DockerReply(bool Ok, int Status, string Body, string? Error)
{
    public static DockerReply Success(int status, string body) => new(true, status, body, null);

    public static DockerReply Failed(string error, int status = 0) => new(false, status, "", error);

    /// <summary>Whether the engine said the thing was so already (304: a start on a running container, a stop on a stopped one).</summary>
    public bool Already => Status == (int)HttpStatusCode.NotModified;
}

/// <summary>A container's log read: its lines, and whether the read stopped at <see cref="DockerLogStream.MaxBytes"/>; or the failure.</summary>
public sealed record DockerLogs(IReadOnlyList<string> Lines, bool Capped, string? Error);

/// <summary>
/// The Docker Engine API over the engine's named pipe (2026-10-02, the user's ask: control Docker Desktop from the app,
/// <c>docker.exe</c> never started). <see cref="HomeAssistant.HaClient"/>'s shape: the transport is injectable (tests pass
/// one over a stub handler; an owned one is disposed here), each call has its own linked-CTS ceiling, and nothing throws
/// but the caller's own cancellation — a failure is a <see cref="DockerReply"/> with a <see cref="DockerText"/> sentence.
/// The owned transport is a <see cref="SocketsHttpHandler"/> whose connect opens the pipe (<see cref="DockerPipe.ConnectAsync"/>),
/// so the engine is reached as <c>http://docker/…</c> and nothing goes near the network or <see cref="Web.LanPolicy"/>.
///
/// <para>The API version is agreed once: <c>GET /version</c> (unversioned) gives the engine's range, and every later path is
/// prefixed with <see cref="PinnedApiVersion"/> while the engine takes it, else its nearest end of the range — an engine
/// older than <see cref="MinimumApiVersion"/> (Docker 20.10) is refused. The shapes read here are the pinned version's, so
/// a newer engine's changes cannot move them.</para>
/// </summary>
public sealed class DockerClient : IDisposable
{
    /// <summary>The API version the paths ask for while the engine takes it (Docker 27.x).</summary>
    public const string PinnedApiVersion = "1.47";

    /// <summary>The oldest API version taken: one-shot stats, the multiplexed log content type and the volume prune's <c>all</c> filter are 1.41–1.42's.</summary>
    public const string MinimumApiVersion = "1.41";

    /// <summary>The host every request names; the connect callback ignores it and opens the pipe.</summary>
    public const string Host = "http://docker";

    /// <summary>The most bytes of a pull's progress read (its last lines say what it came to).</summary>
    public const int MaxPullBytes = 8 * 1024 * 1024;

    private const string Category = DockerText.Category;

    private readonly HttpClient _http;
    private readonly HttpClient? _owned;
    private string? _prefix;

    /// <param name="pipe">The engine's pipe, any form <see cref="DockerPipe.Normalize"/> reads.</param>
    /// <param name="httpClient">Optional transport, the caller's to dispose. Null creates and owns one over the pipe.</param>
    public DockerClient(string pipe, HttpClient? httpClient = null)
    {
        Pipe = DockerPipe.Normalize(pipe);
        if (httpClient is not null)
        {
            _http = httpClient;
        }
        else
        {
            string name = Pipe;
            var handler = new SocketsHttpHandler
            {
                ConnectCallback = (_, cancellationToken) => DockerPipe.ConnectAsync(name, cancellationToken),
                UseProxy = false,
                AllowAutoRedirect = false,
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            };
            // The per-call budgets are the ceilings; the client's own timeout would only race them.
            _owned = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            _http = _owned;
        }
    }

    /// <summary>The pipe's bare name.</summary>
    public string Pipe { get; }

    /// <summary>The pipe as Windows writes it, for the sentences.</summary>
    public string Display => DockerPipe.Display(Pipe);

    /// <summary>The agreed path prefix (<c>/v1.47</c>) once a call has agreed it; null before.</summary>
    public string? Prefix => Volatile.Read(ref _prefix);

    /// <summary>
    /// The path prefix for an engine's range: <see cref="PinnedApiVersion"/> while it lies in it, else the range's nearest
    /// end; null for an engine below <see cref="MinimumApiVersion"/>. Pure.
    /// </summary>
    public static string? PrefixFor(string apiVersion, string minApiVersion)
    {
        if (DockerJson.ApiVersion(apiVersion) is not { } max)
        {
            return null;
        }

        var minimum = DockerJson.ApiVersion(MinimumApiVersion)!.Value;
        var pinned = DockerJson.ApiVersion(PinnedApiVersion)!.Value;
        var min = DockerJson.ApiVersion(minApiVersion) ?? max;
        if (max.CompareTo(minimum) < 0)
        {
            return null;
        }

        var use = max.CompareTo(pinned) <= 0 ? max : min.CompareTo(pinned) > 0 ? min : pinned;
        return "/v" + use.Major.ToString(CultureInfo.InvariantCulture) + "." + use.Minor.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary><c>GET /_ping</c>, unversioned: <c>OK</c> when the engine answers.</summary>
    public Task<DockerReply> PingAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, "/_ping", versioned: false, timeout, cancellationToken);

    /// <summary><c>GET /version</c>, unversioned: the engine and its API range — and the prefix agreed from it.</summary>
    public async Task<(DockerVersion? Version, string? Error)> VersionAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var reply = await SendAsync(HttpMethod.Get, "/version", versioned: false, timeout, cancellationToken).ConfigureAwait(false);
        if (!reply.Ok)
        {
            return (null, reply.Error);
        }

        if (DockerJson.Version(reply.Body) is not { } version)
        {
            return (null, DockerText.BadAnswer("/version"));
        }

        if (PrefixFor(version.ApiVersion, version.MinApiVersion) is not { } prefix)
        {
            return (version, DockerText.TooOld(version.ApiVersion));
        }

        Volatile.Write(ref _prefix, prefix);
        return (version, null);
    }

    /// <summary>A versioned <c>GET</c> (<paramref name="path"/> starts with <c>/</c>, its query escaped by the caller).</summary>
    public Task<DockerReply> GetAsync(string path, TimeSpan timeout, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, path, versioned: true, timeout, cancellationToken);

    /// <summary>A versioned <c>POST</c> with no body.</summary>
    public Task<DockerReply> PostAsync(string path, TimeSpan timeout, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, path, versioned: true, timeout, cancellationToken);

    /// <summary>A versioned <c>DELETE</c>.</summary>
    public Task<DockerReply> DeleteAsync(string path, TimeSpan timeout, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Delete, path, versioned: true, timeout, cancellationToken);

    /// <summary>
    /// <c>GET /containers/{id}/logs</c>: the last <paramref name="tail"/> lines (all for null) since <paramref name="since"/>
    /// (unix seconds), of the streams asked for, read up to <see cref="DockerLogStream.MaxBytes"/> and turned into lines.
    /// </summary>
    public async Task<DockerLogs> LogsAsync(string container, int? tail, long? since, bool timestamps, DockerLogStreams streams, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(container);
        string path = "/containers/" + Uri.EscapeDataString(container) + "/logs?stdout=" + (streams == DockerLogStreams.Stderr ? "0" : "1")
            + "&stderr=" + (streams == DockerLogStreams.Stdout ? "0" : "1")
            + "&tail=" + (tail is { } n ? n.ToString(CultureInfo.InvariantCulture) : "all")
            + (since is { } s ? "&since=" + s.ToString(CultureInfo.InvariantCulture) : "")
            + (timestamps ? "&timestamps=1" : "");
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(timeout);
            if (await PrefixAsync(timeout, budget.Token).ConfigureAwait(false) is (_, { } refused))
            {
                return new DockerLogs([], false, refused);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Host + Prefix + path));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(budget.Token).ConfigureAwait(false);
                return new DockerLogs([], false, Refusal((int)response.StatusCode, PathOnly(path), body));
            }

            string? contentType = response.Content.Headers.ContentType?.MediaType;
            await using var stream = await response.Content.ReadAsStreamAsync(budget.Token).ConfigureAwait(false);
            var (data, capped) = await DockerLogStream.ReadCappedAsync(stream, DockerLogStream.MaxBytes, budget.Token).ConfigureAwait(false);
            return new DockerLogs(DockerLogStream.Lines(data, contentType, streams), capped, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new DockerLogs([], false, DockerText.NoAnswer(LlmTimeouts.Format(timeout)));
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            return new DockerLogs([], false, Explain(ex));
        }
    }

    /// <summary>
    /// <c>POST /images/create?fromImage=…&amp;tag=…</c>: the pull, its progress lines read to the end (up to
    /// <see cref="MaxPullBytes"/>) as the body. A failure the engine reports inside the stream is the body's to say
    /// (<see cref="DockerJson.PullResult"/>).
    /// </summary>
    public async Task<DockerReply> PullAsync(string image, string tag, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(tag);
        // A digest (image@sha256:…) names the image whole: no tag then.
        string path = "/images/create?fromImage=" + Uri.EscapeDataString(image) + (tag.Length > 0 ? "&tag=" + Uri.EscapeDataString(tag) : "");
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(timeout);
            if (await PrefixAsync(timeout, budget.Token).ConfigureAwait(false) is (_, { } refused))
            {
                return DockerReply.Failed(refused);
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Host + Prefix + path));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(budget.Token).ConfigureAwait(false);
            var (data, _) = await DockerLogStream.ReadCappedAsync(stream, MaxPullBytes, budget.Token).ConfigureAwait(false);
            string body = Encoding.UTF8.GetString(data);
            return response.IsSuccessStatusCode
                ? DockerReply.Success((int)response.StatusCode, body)
                : DockerReply.Failed(Refusal((int)response.StatusCode, "/images/create", body), (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return DockerReply.Failed(DockerText.NoAnswer(LlmTimeouts.Format(timeout)));
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            return DockerReply.Failed(Explain(ex));
        }
    }

    /// <summary>The prefix agreed, asking <c>/version</c> the first time; the refusal when it cannot be.</summary>
    private async Task<(string? Prefix, string? Error)> PrefixAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (Prefix is { } known)
        {
            return (known, null);
        }

        var (_, error) = await VersionAsync(timeout, cancellationToken).ConfigureAwait(false);
        return error is null ? (Prefix, null) : (null, error);
    }

    private async Task<DockerReply> SendAsync(HttpMethod method, string path, bool versioned, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(timeout);
            string prefix = "";
            if (versioned)
            {
                var (agreed, error) = await PrefixAsync(timeout, budget.Token).ConfigureAwait(false);
                if (agreed is null)
                {
                    return DockerReply.Failed(error ?? DockerText.BadAnswer("/version"));
                }

                prefix = agreed;
            }

            using var request = new HttpRequestMessage(method, new Uri(Host + prefix + path));
            using var response = await _http.SendAsync(request, budget.Token).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(budget.Token).ConfigureAwait(false);
            int status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotModified)
            {
                return DockerReply.Success(status, body);
            }

            DiagnosticLog.Debug(Category, $"{method} {PathOnly(path)} answered {status.ToString(CultureInfo.InvariantCulture)}.");
            return DockerReply.Failed(Refusal(status, PathOnly(path), body), status);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return DockerReply.Failed(DockerText.NoAnswer(LlmTimeouts.Format(timeout)));
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            return DockerReply.Failed(Explain(ex));
        }
    }

    /// <summary>A non-2xx answer as a sentence: the engine's own message, a 404 named as such.</summary>
    private static string Refusal(int status, string path, string body) => DockerText.HttpError(status, path, DockerJson.ErrorMessage(body));

    private static bool IsTransport(Exception ex) => ex is HttpRequestException or IOException or TimeoutException or UnauthorizedAccessException;

    /// <summary>
    /// A transport failure as a sentence: no pipe (a connect that timed out, a file not found) is Docker Desktop not
    /// running; a refused open is the account's rights; anything else is said as it came.
    /// </summary>
    private string Explain(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case TimeoutException or FileNotFoundException:
                    return DockerText.EngineDown(Display);
                case UnauthorizedAccessException:
                    return DockerText.PipeDenied(Display);
            }
        }

        var root = ex;
        while (root.InnerException is not null)
        {
            root = root.InnerException;
        }

        DiagnosticLog.Debug(Category, "The engine could not be reached: " + ex.Message);
        return DockerText.Unreachable(Display, root.Message);
    }

    /// <summary>A request path without its query, for an error sentence.</summary>
    private static string PathOnly(string path)
    {
        int query = path.IndexOf('?', StringComparison.Ordinal);
        return query < 0 ? path : path[..query];
    }

    public void Dispose() => _owned?.Dispose();
}
