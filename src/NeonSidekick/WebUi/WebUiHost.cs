using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.WebUi;

/// <summary>
/// Where and how the Web UI serves: who may reach it, the port, the token a browser signs in with. <paramref name="Address"/> overrides
/// the interface (the tests bind <see cref="IPAddress.Loopback"/> on port 0).
/// </summary>
public sealed record WebUiBinding(WebUiAudience Audience, int Port, string Token, IPAddress? Address = null);

/// <summary>The Web UI's server, the seam the screen holds (<see cref="WebUiHost"/>; a fake in the screen tests).</summary>
public interface IWebUiHost : IAsyncDisposable
{
    /// <summary>The port it listens on once started (the real one when the binding asked for 0).</summary>
    int Port { get; }

    /// <summary>Starts serving; throws <see cref="IOException"/> or <see cref="SocketException"/> when the port cannot be bound.</summary>
    Task StartAsync(WebUiBinding binding, CancellationToken cancellationToken);

    /// <summary>Stops serving: every event stream ends, open requests get <see cref="StopTimeout"/> to finish.</summary>
    Task StopAsync();
}

/// <summary>
/// The Web UI's HTTP server (2026-10-14, the user's pick: Kestrel). Built as small as ASP.NET Core allows and with nothing it does
/// by itself: <c>WebApplication.CreateEmptyBuilder</c> (no configuration sources, so no <c>ASPNETCORE_*</c> variable is read; no logging
/// provider, since the TUI owns the console) and <c>UseKestrelCore</c>, the content root the app's home, a lifetime that never touches the
/// console (<see cref="WebUiLifetime"/>), and one hand-routed <see cref="RequestDelegate"/> — no minimal-API lambdas, so no Request Delegate
/// Generator and no reflection. HTTP/1.1 only (no HTTP/3, so no msquic). <c>local</c> listens on loopback, <c>lan</c> on every interface
/// and refuses a browser whose address is not private (<see cref="Web.LanPolicy.IsPrivate"/>; defense in depth — NAT loopback, Docker or
/// Tailscale can present a private address, so the token is the real gate). Every page and API but <c>/health</c>, <c>/signin</c> and
/// the static files needs the cookie (<see cref="WebUiAuth"/>); every POST needs JSON and <c>X-Neon: 1</c>, which no cross-site form
/// can send, and every GET is side-effect free. The handler catches everything: a failure is a 500 and a log line, never the app's end.
/// No process-start site, no native DLL.
/// </summary>
public sealed class WebUiHost : IWebUiHost
{
    /// <summary>The most bytes a request body may hold.</summary>
    public const long MaxBodyBytes = 64 * 1024;

    /// <summary>The most connections open at once.</summary>
    public const long MaxConnections = 32;

    /// <summary>The most event streams open at once; one more closes the oldest (the same browser's first).</summary>
    public const int MaxStreams = 8;

    /// <summary>The header every POST carries (with the value <c>1</c>).</summary>
    public const string CsrfHeader = "X-Neon";

    /// <summary>What every response carries.</summary>
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";

    /// <summary>How long a stop waits for open requests before it lets them go.</summary>
    public static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How often an idle event stream says it is alive.</summary>
    public static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(15);

    private const string Category = "WebUi";

    private readonly IWebUiBackend _backend;
    private readonly string _contentRoot;
    private readonly TimeProvider _time;
    private readonly Func<IPAddress, bool> _allowRemote;
    private readonly object _gate = new();
    private readonly List<OpenStream> _streams = [];
    private readonly PictureServer _pictures = new();
    private WebApplication? _app;
    private WebUiBinding? _binding;
    private CancellationTokenSource _stopping = new();

    /// <param name="backend">The app's side of every request.</param>
    /// <param name="contentRoot">An existing folder the host may call its content root (the app's home); nothing is read from it.</param>
    /// <param name="time">The clock for the heartbeat and the stop's wait.</param>
    /// <param name="allowRemote">Whether a <c>lan</c> browser at an address may connect; <see cref="Web.LanPolicy.IsPrivate"/> by default (a seam for the tests).</param>
    public WebUiHost(IWebUiBackend backend, string contentRoot, TimeProvider? time = null, Func<IPAddress, bool>? allowRemote = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _contentRoot = contentRoot ?? throw new ArgumentNullException(nameof(contentRoot));
        _time = time ?? TimeProvider.System;
        _allowRemote = allowRemote ?? Web.LanPolicy.IsPrivate;
    }

    public int Port { get; private set; }

    public async Task StartAsync(WebUiBinding binding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (_app is not null)
        {
            throw new InvalidOperationException("The Web UI host is running already.");
        }

        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { ContentRootPath = _contentRoot, ApplicationName = "NeonSidekick" });
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IHostLifetime, WebUiLifetime>();
        builder.WebHost.UseKestrelCore().ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = MaxBodyBytes;
            options.Limits.MaxConcurrentConnections = MaxConnections;
            static void Http1(ListenOptions listen) => listen.Protocols = HttpProtocols.Http1;
            if (binding.Address is { } address)
            {
                options.Listen(address, binding.Port, Http1);
            }
            else if (binding.Audience == WebUiAudience.Local)
            {
                options.ListenLocalhost(binding.Port, Http1);
            }
            else
            {
                options.ListenAnyIP(binding.Port, Http1);
            }
        });

        var app = builder.Build();
        ((IApplicationBuilder)app).Run(HandleAsync);
        _binding = binding;
        _stopping = new CancellationTokenSource();
        try
        {
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            _binding = null;
            throw;
        }

        _app = app;
        Port = BoundPort(app) ?? binding.Port;
    }

    public async Task StopAsync()
    {
        if (_app is not { } app)
        {
            return;
        }

        _app = null;
        await _stopping.CancelAsync().ConfigureAwait(false);
        using var wait = new CancellationTokenSource(StopTimeout, _time);
        try
        {
            await app.StopAsync(wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The requests still open after the wait are let go.
        }

        await app.DisposeAsync().ConfigureAwait(false);
        _stopping.Dispose();
        _binding = null;
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private static int? BoundPort(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
        foreach (string address in addresses ?? [])
        {
            if (Uri.TryCreate(address.Replace("://+", "://localhost", StringComparison.Ordinal).Replace("://*", "://localhost", StringComparison.Ordinal), UriKind.Absolute, out var uri))
            {
                return uri.Port;
            }
        }

        return null;
    }

    // ── The requests ─────────────────────────────────────────────────────────

    private async Task HandleAsync(HttpContext context)
    {
        var response = context.Response;
        try
        {
            SecurityHeaders(response);
            var remote = context.Connection.RemoteIpAddress;
            if (_binding is { Audience: WebUiAudience.LocalAreaNetwork } && remote is not null && !_allowRemote(remote))
            {
                DiagnosticLog.Info(Category, WebUiText.RefusedRemoteLogLine(remote));
                response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            string path = context.Request.Path.Value ?? "/";
            bool get = HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method);
            bool post = HttpMethods.IsPost(context.Request.Method);
            if (path == "/health")
            {
                await TextAsync(context, "ok").ConfigureAwait(false);
                return;
            }

            if (path == WebUiAuth.SignInPath && get)
            {
                await SignInAsync(context).ConfigureAwait(false);
                return;
            }

            string name = path.TrimStart('/');
            if (get && name is "app.css" or "chat.js" or "shopping.js" or "favicon.svg")
            {
                await AssetAsync(context, name).ConfigureAwait(false);
                return;
            }

            bool signedIn = SignedIn(context);
            if (get && path is "/" or "/index.html" or "/shopping" or "/shopping.html")
            {
                string page = !signedIn ? WebUiAssets.SignInPage : path.StartsWith("/shopping", StringComparison.Ordinal) ? WebUiAssets.ShoppingPage : WebUiAssets.ChatPage;
                await AssetAsync(context, page).ConfigureAwait(false);
                return;
            }

            if (!path.StartsWith("/api/", StringComparison.Ordinal))
            {
                response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            response.Headers.CacheControl = "no-store";
            if (!signedIn)
            {
                await ResultAsync(context, StatusCodes.Status401Unauthorized, new WebResult(false, WebUiText.SignInNeeded)).ConfigureAwait(false);
                return;
            }

            if (get && path.StartsWith(PicturePrefix, StringComparison.Ordinal))
            {
                await PictureAsync(context, path[PicturePrefix.Length..]).ConfigureAwait(false);
                return;
            }

            if (get)
            {
                switch (path)
                {
                    case "/api/events":
                        await EventsAsync(context).ConfigureAwait(false);
                        return;
                    case "/api/shopping":
                        await JsonAsync(context, StatusCodes.Status200OK, _backend.Shopping(), WebUiJsonContext.Default.WebShopping).ConfigureAwait(false);
                        return;
                }
            }
            else if (post)
            {
                if (!CrossSiteSafe(context.Request))
                {
                    await ResultAsync(context, StatusCodes.Status403Forbidden, new WebResult(false, WebUiText.PostRefused)).ConfigureAwait(false);
                    return;
                }

                if (path == "/api/complete")
                {
                    // The dropdown (later on 2026-10-14): its own answer, not a post's sentence.
                    if (await ReadAsync(context, WebUiJsonContext.Default.WebCompleteRequest).ConfigureAwait(false) is not { } complete)
                    {
                        await ResultAsync(context, StatusCodes.Status400BadRequest, new WebResult(false, WebUiText.BadRequest)).ConfigureAwait(false);
                        return;
                    }

                    await JsonAsync(context, StatusCodes.Status200OK, _backend.Complete(complete), WebUiJsonContext.Default.WebCompletion).ConfigureAwait(false);
                    return;
                }

                WebReply? reply = path switch
                {
                    "/api/send" => await ReadAsync(context, WebUiJsonContext.Default.WebSendRequest).ConfigureAwait(false) is { } send ? _backend.Send(send, remote) : null,
                    "/api/stop" => _backend.Stop(remote),
                    "/api/answer" => await ReadAsync(context, WebUiJsonContext.Default.WebAnswerRequest).ConfigureAwait(false) is { } answer ? _backend.Answer(answer, remote) : null,
                    "/api/shopping/check" => await ReadAsync(context, WebUiJsonContext.Default.WebCheckRequest).ConfigureAwait(false) is { } check ? _backend.Check(check, remote) : null,
                    _ => new WebReply(WebOutcome.NotFound),
                };
                if (reply is not { } done)
                {
                    await ResultAsync(context, StatusCodes.Status400BadRequest, new WebResult(false, WebUiText.BadRequest)).ConfigureAwait(false);
                    return;
                }

                await ResultAsync(context, StatusOf(done.Outcome), new WebResult(done.Outcome == WebOutcome.Ok, done.Message, done.AsText, done.Use)).ConfigureAwait(false);
                return;
            }

            response.StatusCode = StatusCodes.Status404NotFound;
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException e)
        {
            if (!response.HasStarted)
            {
                response.StatusCode = e.StatusCode;
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested || _stopping.IsCancellationRequested)
        {
            // The browser went, or the server stops.
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            DiagnosticLog.Error(Category, WebUiText.RequestFailedLogLine(context.Request.Path.Value ?? "", e), e);
            if (!response.HasStarted)
            {
                response.StatusCode = StatusCodes.Status500InternalServerError;
            }
        }
    }

    /// <summary>Where the pictures are served: <c>/api/picture/{run}/{id}/{thumb|mid|full}</c>.</summary>
    public const string PicturePrefix = "/api/picture/";

    /// <summary>
    /// A picture (2026-10-14): the run's tag first (another run's address names nothing), then the id the transcript holds now, then the size;
    /// anything else a 404 with no line in the transcript. The page never names a path: the id is the screen's.
    /// </summary>
    private async Task PictureAsync(HttpContext context, string rest)
    {
        string[] parts = rest.Split('/');
        if (parts.Length != 3 || !string.Equals(parts[0], _backend.PictureRun, StringComparison.Ordinal)
            || !int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int id)
            || PictureServer.SizeOf(parts[2]) is not { } size || _backend.Picture(id) is not { } source)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await _pictures.ServeAsync(context, source, size).ConfigureAwait(false);
    }

    private static int StatusOf(WebOutcome outcome) => outcome switch
    {
        WebOutcome.Ok => StatusCodes.Status200OK,
        WebOutcome.Refused => StatusCodes.Status422UnprocessableEntity,
        WebOutcome.Conflict => StatusCodes.Status409Conflict,
        WebOutcome.NotFound => StatusCodes.Status404NotFound,
        _ => StatusCodes.Status429TooManyRequests,
    };

    private static void SecurityHeaders(HttpResponse response)
    {
        response.Headers.ContentSecurityPolicy = ContentSecurityPolicy;
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers.XFrameOptions = "DENY";
    }

    /// <summary>A POST a cross-site page could not have made: JSON, and the header no form can set.</summary>
    private static bool CrossSiteSafe(HttpRequest request) =>
        request.Headers[CsrfHeader] == "1"
        && request.ContentType is { } type && type.StartsWith("application/json", StringComparison.OrdinalIgnoreCase);

    private bool SignedIn(HttpContext context) =>
        _binding is { } binding && WebUiAuth.Matches(context.Request.Cookies[WebUiAuth.CookieName(Port)], binding.Token);

    private async Task SignInAsync(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (_binding is not { } binding || !WebUiAuth.Matches(context.Request.Query["t"].ToString(), binding.Token))
        {
            DiagnosticLog.Info(Category, WebUiText.SignInFailedLogLine(remote));
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await AssetAsync(context, WebUiAssets.SignInPage, status: StatusCodes.Status403Forbidden).ConfigureAwait(false);
            return;
        }

        context.Response.Cookies.Append(WebUiAuth.CookieName(Port), binding.Token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            MaxAge = WebUiAuth.CookieLifetime,
            IsEssential = true,
        });
        _backend.SignedIn(remote);
        // The token leaves the address bar (and the history's next entry) at once.
        context.Response.StatusCode = StatusCodes.Status303SeeOther;
        context.Response.Headers.Location = "/";
    }

    private static async Task AssetAsync(HttpContext context, string name, int status = StatusCodes.Status200OK)
    {
        if (WebUiAssets.Find(name) is not { } asset)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.ETag = asset.ETag;
        if (status == StatusCodes.Status200OK && context.Request.Headers.IfNoneMatch == asset.ETag)
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        context.Response.StatusCode = status;
        context.Response.ContentType = asset.ContentType;
        context.Response.ContentLength = asset.Bytes.Length;
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await context.Response.Body.WriteAsync(asset.Bytes, context.RequestAborted).ConfigureAwait(false);
        }
    }

    private static Task TextAsync(HttpContext context, string text)
    {
        context.Response.ContentType = "text/plain; charset=utf-8";
        return context.Response.WriteAsync(text, context.RequestAborted);
    }

    private static async Task<T?> ReadAsync<T>(HttpContext context, JsonTypeInfo<T> info)
        where T : class
    {
        try
        {
            return await JsonSerializer.DeserializeAsync(context.Request.Body, info, context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Task ResultAsync(HttpContext context, int status, WebResult result) =>
        JsonAsync(context, status, result, WebUiJsonContext.Default.WebResult);

    private static async Task JsonAsync<T>(HttpContext context, int status, T value, JsonTypeInfo<T> info)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(context.Response.Body, value, info, context.RequestAborted).ConfigureAwait(false);
    }

    // ── The event stream ─────────────────────────────────────────────────────

    private async Task EventsAsync(HttpContext context)
    {
        var response = context.Response;
        response.ContentType = "text/event-stream; charset=utf-8";
        response.Headers.CacheControl = "no-cache";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        using var mine = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _stopping.Token);
        var stream = new OpenStream(context.Request.Cookies[WebUiAuth.CookieName(Port)] ?? "", mine);
        Admit(stream);
        // Subscribed before the snapshot: an event between the two is sent twice at worst, and the page skips what it has.
        using var subscription = _backend.Events.Subscribe();
        try
        {
            var token = mine.Token;
            await WriteEventAsync(response, "snapshot", JsonSerializer.Serialize(_backend.Snapshot(), WebUiJsonContext.Default.WebSnapshot), token).ConfigureAwait(false);
            await response.Body.FlushAsync(token).ConfigureAwait(false);
            var reader = subscription.Reader;
            while (!token.IsCancellationRequested)
            {
                while (reader.TryRead(out var item))
                {
                    await WriteEventAsync(response, item.Name, item.Json.Value, token).ConfigureAwait(false);
                }

                await response.Body.FlushAsync(token).ConfigureAwait(false);
                var waiting = reader.WaitToReadAsync(token).AsTask();
                var beat = Task.Delay(Heartbeat, _time, token);
                var first = await Task.WhenAny(waiting, beat).ConfigureAwait(false);
                if (token.IsCancellationRequested)
                {
                    break;   // the page went, the server stops, or a newer stream took this one's place
                }

                if (first == beat)
                {
                    await response.WriteAsync(": alive\n\n", token).ConfigureAwait(false);
                    continue;
                }

                if (!await waiting.ConfigureAwait(false))
                {
                    break;   // dropped for falling behind: the page reconnects and starts from a snapshot
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The page went, the server stops, or a newer stream took this one's place.
        }
        finally
        {
            Release(stream);
        }
    }

    private static Task WriteEventAsync(HttpResponse response, string name, string json, CancellationToken cancellationToken) =>
        response.WriteAsync("event: " + name + "\ndata: " + json + "\n\n", Encoding.UTF8, cancellationToken);

    /// <summary>One open event stream: whose (the cookie) and how to end it.</summary>
    private sealed record OpenStream(string Cookie, CancellationTokenSource Cts);

    private void Admit(OpenStream stream)
    {
        OpenStream? oldest = null;
        lock (_gate)
        {
            _streams.Add(stream);
            if (_streams.Count > MaxStreams)
            {
                oldest = _streams.FirstOrDefault(s => s != stream && s.Cookie == stream.Cookie) ?? _streams[0];
                _streams.Remove(oldest);
            }
        }

        if (oldest is not null)
        {
            try
            {
                oldest.Cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // ended meanwhile
            }
        }
    }

    private void Release(OpenStream stream)
    {
        lock (_gate)
        {
            _streams.Remove(stream);
        }
    }

    /// <summary>The event streams open now (tests).</summary>
    internal int OpenStreams
    {
        get
        {
            lock (_gate)
            {
                return _streams.Count;
            }
        }
    }

    /// <summary>The running host's services (tests: the lifetime registered).</summary>
    internal IServiceProvider? Services => _app?.Services;
}
