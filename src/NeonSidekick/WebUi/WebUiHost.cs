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

    /// <summary>
    /// A one-time sign-in link on <paramref name="host"/> (later on 2026-10-15, <see cref="WebUiOneTimeCodes"/>), or null while not serving: good once,
    /// for a minute. A host without them (a test's) has none.
    /// </summary>
    string? OneTimeLink(string host) => null;
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
/// can send. A GET of an API changes nothing but what its own stream does (the Chat stream's snapshot hands over the held drafts, the YouTube
/// tab's attaches and may claim the player): an EventSource can send no header, so a GET of <c>/api/</c> that the browser marks cross-site or
/// same-site (<c>Sec-Fetch-Site</c>, a top-level link from another page carrying the Lax cookie) is refused (<see cref="CrossSiteGet"/>, 2026-10-19,
/// the review). The handler catches everything: a failure is a 500 and a log line, never the app's end.
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

    /// <summary>
    /// The YouTube tab's page alone (2026-10-16, <c>YouTube open links in</c> <c>webui-window</c>): <see cref="ContentSecurityPolicy"/> with
    /// YouTube's player let in, and <see cref="YouTubeReferrerPolicy"/>. <b>The trust, the user's call on 2026-10-15:</b> YouTube's player
    /// script runs in the Web UI's own page on this tab (not isolated on a second port, nor driven by raw undocumented messages), so while
    /// the tab is open that script can do what the signed-in page can — send a chat line the model acts on, for one — though never read the
    /// HttpOnly sign-in cookie. So the policy is as narrow as CSP makes it: <c>script-src</c> by path, the loader
    /// (<c>/iframe_api</c>) and the player script it fetches (<c>/s/player/</c>, whose build hash changes), never all of www.youtube.com
    /// (checked 2026-10-15: the loader injects exactly that one script); <c>frame-src</c> www.youtube.com, where the player is made (no
    /// nocookie host, no <c>i.ytimg.com</c>: the page draws no thumbnail of its own). The phase 0 spike (2026-10-16) played a video, paused and
    /// played it from script and loaded a second one under exactly this policy, on the PC and an iPhone, with no violation reported. Widen it
    /// only with a violation to show for it.
    /// </summary>
    public const string YouTubeContentSecurityPolicy =
        "default-src 'self'; script-src 'self' https://www.youtube.com/iframe_api https://www.youtube.com/s/player/; style-src 'self'; img-src 'self' data: https://i.ytimg.com; frame-src https://www.youtube.com; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";

    /// <summary>The YouTube tab's referrer policy (2026-10-16): YouTube's embed needs the page's origin as the referrer, else error 153.</summary>
    public const string YouTubeReferrerPolicy = "strict-origin-when-cross-origin";

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
    private readonly WebRateLimit _limits;
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
        _limits = new WebRateLimit(_time);
        _codes = new WebUiOneTimeCodes(_time);
    }

    public int Port { get; private set; }

    private readonly WebUiOneTimeCodes _codes;

    public string? OneTimeLink(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return _binding is null || _app is null ? null : WebUiAuth.OnceLink(host, Port, _codes.Mint());
    }

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
        _codes.Clear();   // a restart leaves only dead codes (later on 2026-10-15)
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
        _codes.Clear();
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
                if (_limits.LogDue(remote, nameof(WebUiText.RefusedRemoteLogLine)))
                {
                    DiagnosticLog.Info(Category, WebUiText.RefusedRemoteLogLine(remote));
                }

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

            // The limits (2026-10-15): a tap, a question to Home Assistant, a sign-in (pasted, or the link's GET since 2026-10-19), each counted per
            // address before anything else.
            if (WebRateLimit.BucketOf(path, post, HttpMethods.IsGet(context.Request.Method)) is { } bucket && !_limits.TryTake(remote, bucket, out var retry))
            {
                response.Headers.CacheControl = "no-store";
                response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                await ResultAsync(context, StatusCodes.Status429TooManyRequests, new WebResult(false, WebUiText.SlowDown)).ConfigureAwait(false);
                return;
            }

            if (path == WebUiAuth.SignInPath && get)
            {
                await SignInAsync(context).ConfigureAwait(false);
                return;
            }

            if (path == WebUiAuth.SignInPath && post)
            {
                // The link pasted on the page (2026-10-15: an app installed from Safari keeps its own cookies), the header no other site can send.
                await PastedSignInAsync(context).ConfigureAwait(false);
                return;
            }

            if (get && path == "/theme.css")
            {
                // The profile's theme (2026-10-15), cookie-free like app.css: a plain link revalidates on each load, so the page is themed from its first paint.
                await TextAssetAsync(context, _backend.ThemeCss(), "text/css; charset=utf-8").ConfigureAwait(false);
                return;
            }

            if (get && path == "/manifest.webmanifest")
            {
                await TextAssetAsync(context, _backend.Manifest(), "application/manifest+json; charset=utf-8").ConfigureAwait(false);
                return;
            }

            string name = path.TrimStart('/');
            if (get && WebUiAssets.IsStatic(name))
            {
                await AssetAsync(context, name).ConfigureAwait(false);
                return;
            }

            bool signedIn = SignedIn(context);
            if (get && path is "/" or "/index.html" or "/shopping" or "/shopping.html" or "/home" or "/home.html" or "/youtube" or "/youtube.html")
            {
                await PageAsync(context, path, signedIn).ConfigureAwait(false);
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

            if (get && CrossSiteGet(context.Request))
            {
                await ResultAsync(context, StatusCodes.Status403Forbidden, new WebResult(false, WebUiText.PostRefused)).ConfigureAwait(false);
                return;
            }

            // A tab that is off (later on 2026-10-15) has no APIs: a 404 the page reads as "go to /", which sends it to a tab that is on.
            if (TabOf(path) is { } tab && (!_backend.Tabs().Has(tab) || (tab == WebTab.YouTube && _backend.Video is null)))
            {
                await ResultAsync(context, StatusCodes.Status404NotFound, new WebResult(false, WebUiText.TabOff(tab)) { TabOff = true }).ConfigureAwait(false);
                return;
            }

            if (get && path.StartsWith(PicturePrefix, StringComparison.Ordinal))
            {
                await PictureAsync(context, path[PicturePrefix.Length..]).ConfigureAwait(false);
                return;
            }

            if (get && path.StartsWith(EntryPrefix, StringComparison.Ordinal))
            {
                // A deferred entry opened on the page (2026-10-15): whole while the transcript holds it.
                if (long.TryParse(path[EntryPrefix.Length..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long seq) && _backend.Entry(seq) is { } entry)
                {
                    await JsonAsync(context, StatusCodes.Status200OK, entry, WebUiJsonContext.Default.WebEntry).ConfigureAwait(false);
                }
                else
                {
                    await ResultAsync(context, StatusCodes.Status404NotFound, new WebResult(false, WebUiText.EntryGone)).ConfigureAwait(false);
                }

                return;
            }

            if (get)
            {
                switch (path)
                {
                    case "/api/events":
                        await EventsAsync(context).ConfigureAwait(false);
                        return;
                    case YouTubeEventsPath when _backend.Video is { } video:
                        // The YouTube tab's own stream (2026-10-16): per page, apart from Chat's.
                        await YouTubeEventsAsync(context, video).ConfigureAwait(false);
                        return;
                    case "/api/shopping":
                        // Tagged since 2026-10-16: the page reads it every 5 s with the full body, and most reads change nothing.
                        await TaggedJsonAsync(context, _backend.Shopping(), WebUiJsonContext.Default.WebShopping).ConfigureAwait(false);
                        return;
                    case ShopBoughtPath:
                        // The 🕘 sheet's past items (2026-10-16): read each time it opens and after each change in it.
                        if (_backend.ShopBought() is { } bought)
                        {
                            response.Headers.CacheControl = "no-store";
                            await JsonAsync(context, StatusCodes.Status200OK, bought, WebUiJsonContext.Default.WebBought).ConfigureAwait(false);
                        }
                        else
                        {
                            response.StatusCode = StatusCodes.Status404NotFound;
                        }

                        return;
                    case "/api/prompts":
                        // The saved prompts sheet (later on 2026-10-15): read each time it opens.
                        response.Headers.CacheControl = "no-store";
                        await JsonAsync(context, StatusCodes.Status200OK, _backend.Prompts(), WebUiJsonContext.Default.WebPrompts).ConfigureAwait(false);
                        return;
                    case MemoryPath:
                        // The sheet's 💾 tab (2026-10-16): read each time it opens.
                        response.Headers.CacheControl = "no-store";
                        await JsonAsync(context, StatusCodes.Status200OK, _backend.Memories(), WebUiJsonContext.Default.WebMemories).ConfigureAwait(false);
                        return;
                    case YouTubeSavedPath:
                        // The YouTube tab's saved list (later on 2026-10-16): read every few seconds while open, so an unchanged one is a 304.
                        if (_backend.SavedVideos() is { } saved)
                        {
                            await TaggedJsonAsync(context, saved, WebUiJsonContext.Default.WebSavedVideos).ConfigureAwait(false);
                        }
                        else
                        {
                            response.StatusCode = StatusCodes.Status404NotFound;
                        }

                        return;
                    case HistoryPath:
                        // The message history (2026-10-16): read at each walk and as the box takes focus, so an unchanged one is a 304.
                        await TaggedJsonAsync(context, _backend.History(), WebUiJsonContext.Default.WebHistory).ConfigureAwait(false);
                        return;
                    case "/api/usage":
                        // The usage card (2026-10-15): the /usage figures on the page alone; tagged since 2026-10-16, as the open card
                        // re-reads it while a reply runs and most ticks change nothing it shows.
                        await TaggedJsonAsync(context, _backend.Usage(), WebUiJsonContext.Default.WebUsageCard).ConfigureAwait(false);
                        return;
                    case BotCheckFramePath:
                        await BotCheckFrameAsync(context).ConfigureAwait(false);
                        return;
                    case "/api/pulse":
                        // The Chat badge on the other tabs (2026-10-15).
                        await TaggedJsonAsync(context, _backend.Pulse(), WebUiJsonContext.Default.WebPulse).ConfigureAwait(false);
                        return;
                    case "/api/home" when _backend.Home is { } home:
                        // The Home tab (2026-10-15): read often, so an unchanged tab is a 304.
                        await TaggedJsonAsync(context, home.Read(), WebUiJsonContext.Default.WebHome).ConfigureAwait(false);
                        return;
                    case "/api/home/sheet" when _backend.Home is { } home:
                        if (home.Sheet(context.Request.Query["ref"].ToString()) is { } sheet)
                        {
                            await JsonAsync(context, StatusCodes.Status200OK, sheet, WebUiJsonContext.Default.WebHomeSheet).ConfigureAwait(false);
                        }
                        else
                        {
                            await ResultAsync(context, StatusCodes.Status404NotFound, new WebResult(false, WebUiText.HomeChanged)).ConfigureAwait(false);
                        }

                        return;
                    case CameraPath when _backend.Home is { } home && HttpMethods.IsGet(context.Request.Method):
                        await CameraAsync(context, home, remote).ConfigureAwait(false);
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
                    "/api/shopping/store" => await ReadAsync(context, WebUiJsonContext.Default.WebStoreRequest).ConfigureAwait(false) is { } store ? _backend.UseStore(store, remote) : null,
                    ShopAddPath => await ReadAsync(context, WebUiJsonContext.Default.WebTextBody).ConfigureAwait(false) is { } items ? _backend.ShopAdd(items, remote) : null,
                    ShopReAddPath => await ReadAsync(context, WebUiJsonContext.Default.WebShopId).ConfigureAwait(false) is { } again ? _backend.ShopReAdd(again, remote) : null,
                    ShopForgetPath => await ReadAsync(context, WebUiJsonContext.Default.WebShopId).ConfigureAwait(false) is { } past ? _backend.ShopForget(past, remote) : null,
                    ShopQuantityPath => await ReadAsync(context, WebUiJsonContext.Default.WebShopQuantity).ConfigureAwait(false) is { } qty ? _backend.ShopQuantity(qty, remote) : null,
                    ShopRenamePath => await ReadAsync(context, WebUiJsonContext.Default.WebShopRename).ConfigureAwait(false) is { } rename ? _backend.ShopRename(rename, remote) : null,
                    ShopRemovePath => await ReadAsync(context, WebUiJsonContext.Default.WebShopId).ConfigureAwait(false) is { } line ? _backend.ShopRemove(line, remote) : null,
                    ShopCheckAllPath => await ReadAsync(context, WebUiJsonContext.Default.WebShopCheckAll).ConfigureAwait(false) is { } all ? _backend.ShopCheckAll(all, remote) : null,
                    ShopClearPath => _backend.ShopClearTicked(remote),   // the body is not read, as Stop's
                    "/api/strip/select" => await ReadAsync(context, WebUiJsonContext.Default.WebStripSelect).ConfigureAwait(false) is { } pick ? _backend.SelectStripPicture(pick, remote) : null,
                    PictureDeletePath => await ReadAsync(context, WebUiJsonContext.Default.WebPictureDelete).ConfigureAwait(false) is { } gone ? _backend.DeletePicture(gone, remote) : null,
                    PromptSavePath => await ReadAsync(context, WebUiJsonContext.Default.WebTextBody).ConfigureAwait(false) is { } prompt ? _backend.SavePrompt(prompt, remote) : null,
                    PromptDeletePath => await ReadAsync(context, WebUiJsonContext.Default.WebPromptDelete).ConfigureAwait(false) is { } unsaved ? _backend.DeletePrompt(unsaved, remote) : null,
                    MemorySavePath => await ReadAsync(context, WebUiJsonContext.Default.WebTextBody).ConfigureAwait(false) is { } memory ? _backend.SaveMemory(memory, remote) : null,
                    MemoryDeletePath => await ReadAsync(context, WebUiJsonContext.Default.WebTextBody).ConfigureAwait(false) is { } forget ? _backend.DeleteMemory(forget, remote) : null,
                    MemoryEditPath => await ReadAsync(context, WebUiJsonContext.Default.WebMemoryEdit).ConfigureAwait(false) is { } edit ? _backend.EditMemory(edit, remote) : null,
                    MemoryTogglePath => await ReadAsync(context, WebUiJsonContext.Default.WebMemoryToggle).ConfigureAwait(false) is { } toggle ? _backend.ToggleMemory(toggle, remote) : null,
                    HistoryClearPath => _backend.ClearHistory(remote),   // the body is not read, as Stop's
                    LearnCancelPath => _backend.CancelLearning(remote),   // the status row's 🧠 (2026-10-17); no body either
                    QueueResumePath => _backend.ResumeQueue(remote),   // the held queue's banner (2026-10-17); no body either
                    YouTubeReportPath => await YouTubeReportAsync(context, remote).ConfigureAwait(false),
                    YouTubeSeenPath => await YouTubeSeenAsync(context, remote).ConfigureAwait(false),
                    YouTubeSavedPlayPath => await ReadAsync(context, WebUiJsonContext.Default.WebSavedPlay).ConfigureAwait(false) is { } play ? _backend.PlaySaved(play, remote) : null,
                    YouTubeSavedRemovePath => await ReadAsync(context, WebUiJsonContext.Default.WebSavedId).ConfigureAwait(false) is { } unsave ? _backend.RemoveSaved(unsave, remote) : null,
                    YouTubeSavedClearPath => _backend.ClearSaved(remote),   // the body is not read, as Stop's
                    YouTubeSavedMovePath => await ReadAsync(context, WebUiJsonContext.Default.WebSavedMove).ConfigureAwait(false) is { } move ? _backend.MoveSaved(move, remote) : null,
                    YouTubeSavedSavePath => _backend.SaveCurrent(remote),
                    BotCheckInputPath => await ReadAsync(context, WebUiJsonContext.Default.WebBotCheckInput).ConfigureAwait(false) is { } input ? await _backend.BotCheckInputAsync(input, remote, context.RequestAborted).ConfigureAwait(false) : null,
                    "/api/home/tap" or "/api/home/act" or "/api/home/scene" or "/api/home/say" or "/api/home/video" => await HomePostAsync(context, path, remote).ConfigureAwait(false),
                    _ => new WebReply(WebOutcome.NotFound),
                };
                if (reply is not { } done)
                {
                    await ResultAsync(context, StatusCodes.Status400BadRequest, new WebResult(false, WebUiText.BadRequest)).ConfigureAwait(false);
                    return;
                }

                await ResultAsync(context, StatusOf(done.Outcome), new WebResult(done.Outcome == WebOutcome.Ok, done.Message, done.AsText, done.Use)
                {
                    NeedsConfirm = done.Prompt is not null ? true : null,
                    Prompt = done.Prompt,
                    Open = done.Open,
                }).ConfigureAwait(false);
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

    /// <summary>
    /// A page (later on 2026-10-15, each tab only while its controls are on): signed out, the sign-in page; a tab that is off goes to <c>/</c>; <c>/</c>
    /// with Chat off goes to the first tab that is on, or, with none, the page that says so. A redirect is a 303 never cached.
    /// </summary>
    private async Task PageAsync(HttpContext context, string path, bool signedIn)
    {
        if (!signedIn)
        {
            await AssetAsync(context, WebUiAssets.SignInPage).ConfigureAwait(false);
            return;
        }

        var tabs = _backend.Tabs();
        if (path.StartsWith("/youtube", StringComparison.Ordinal))
        {
            // The YouTube tab (2026-10-16): its page alone lets YouTube's player in (YouTubeContentSecurityPolicy's trust).
            if (tabs.YouTube && _backend.Video is not null)
            {
                context.Response.Headers.ContentSecurityPolicy = YouTubeContentSecurityPolicy;
                context.Response.Headers["Referrer-Policy"] = YouTubeReferrerPolicy;
                await AssetAsync(context, WebUiAssets.YouTubePage).ConfigureAwait(false);
            }
            else
            {
                SeeOther(context.Response, "/");
            }

            return;
        }

        if (path.StartsWith("/shopping", StringComparison.Ordinal) || path.StartsWith("/home", StringComparison.Ordinal))
        {
            bool shopping = path.StartsWith("/shopping", StringComparison.Ordinal);
            if (shopping ? tabs.Shopping : tabs.Home)
            {
                await AssetAsync(context, shopping ? WebUiAssets.ShoppingPage : WebUiAssets.HomePage).ConfigureAwait(false);
            }
            else
            {
                SeeOther(context.Response, "/");
            }

            return;
        }

        if (tabs.Chat)
        {
            await AssetAsync(context, WebUiAssets.ChatPage).ConfigureAwait(false);
        }
        else if (tabs.FirstOtherPage is { } other)
        {
            SeeOther(context.Response, other);
        }
        else
        {
            await AssetAsync(context, WebUiAssets.NoTabsPage).ConfigureAwait(false);
        }
    }

    private static void SeeOther(HttpResponse response, string location)
    {
        response.Headers.CacheControl = "no-store";
        response.StatusCode = StatusCodes.Status303SeeOther;
        response.Headers.Location = location;
    }

    /// <summary>
    /// The tab an API belongs to (later on 2026-10-15), or null for one every page reads (<c>/api/pulse</c>). Chat's: the stream, a message, Stop, an
    /// answer, the dropdown, the usage card, a deferred entry, a picture and the strip's select; the sheet's prompts, memories and history
    /// (2026-10-16). Pure.
    /// </summary>
    public static WebTab? TabOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path is "/api/shopping" || path.StartsWith("/api/shopping/", StringComparison.Ordinal))
        {
            return WebTab.Shopping;
        }

        if (path is "/api/home" || path.StartsWith("/api/home/", StringComparison.Ordinal))
        {
            return WebTab.Home;
        }

        if (path.StartsWith("/api/youtube/", StringComparison.Ordinal))
        {
            return WebTab.YouTube;   // 2026-10-16
        }

        return path is "/api/events" or "/api/send" or "/api/stop" or "/api/answer" or "/api/complete" or "/api/usage" or "/api/strip/select" or "/api/prompts"
                or PromptSavePath or PromptDeletePath or MemoryPath or MemorySavePath or MemoryDeletePath or MemoryEditPath or MemoryTogglePath or HistoryPath or HistoryClearPath
                or LearnCancelPath or QueueResumePath
                or BotCheckFramePath or BotCheckInputPath
            || path.StartsWith(EntryPrefix, StringComparison.Ordinal) || path.StartsWith(PicturePrefix, StringComparison.Ordinal)
            ? WebTab.Chat
            : null;
    }

    /// <summary>A Home tab post (2026-10-15): its body read, the tab's side asked; null for a body that does not read, a 404 with no tab.</summary>
    private async Task<WebReply?> HomePostAsync(HttpContext context, string path, System.Net.IPAddress? remote)
    {
        if (_backend.Home is not { } home)
        {
            return new WebReply(WebOutcome.NotFound);
        }

        return path switch
        {
            "/api/home/tap" => await ReadAsync(context, WebUiJsonContext.Default.WebHomeTapRequest).ConfigureAwait(false) is { } tap ? home.Tap(tap, remote) : null,
            "/api/home/act" => await ReadAsync(context, WebUiJsonContext.Default.WebHomeActRequest).ConfigureAwait(false) is { } act ? home.Act(act, remote) : null,
            "/api/home/scene" => await ReadAsync(context, WebUiJsonContext.Default.WebHomeSceneRequest).ConfigureAwait(false) is { } scene ? home.Scene(scene, remote) : null,
            "/api/home/say" => await ReadAsync(context, WebUiJsonContext.Default.WebHomeSayRequest).ConfigureAwait(false) is { } say ? await home.SayAsync(say, remote, context.RequestAborted).ConfigureAwait(false) : null,
            _ => await ReadAsync(context, WebUiJsonContext.Default.WebHomeVideoRequest).ConfigureAwait(false) is { } video ? home.Video(video, remote) : null,
        };
    }

    /// <summary>A JSON answer with an ETag of its bytes (2026-10-15): the same answer again is a 304 with no body, for the pages that read often.</summary>
    private static async Task TaggedJsonAsync<T>(HttpContext context, T value, JsonTypeInfo<T> info)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, info);
        string etag = "\"" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))[..16] + "\"";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.ETag = etag;
        if (context.Request.Headers.IfNoneMatch == etag)
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = bytes.Length;
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>A bot check card's picture of the Browser window (2026-10-17): <c>?ask=&amp;w=</c>, tagged.</summary>
    public const string BotCheckFramePath = "/api/botcheck/frame";

    /// <summary>A bot check card's input (2026-10-17): a post of <see cref="WebBotCheckInput"/>, in its own bucket.</summary>
    public const string BotCheckInputPath = "/api/botcheck/input";

    /// <summary>The narrowest and widest picture a card may ask for, in pixels.</summary>
    public const int BotCheckMinWidth = 320;
    public const int BotCheckMaxWidth = 1600;

    /// <summary>
    /// A bot check card's picture (2026-10-17): the screen's answer for the ask and the width (clamped), its tag the ETag, an unchanged picture a
    /// 304 with no body. 403 with <i>Web UI bot check control</i> off, 404 for a check that is over, 503 when the window didn't draw in time.
    /// </summary>
    private async Task BotCheckFrameAsync(HttpContext context)
    {
        string askId = context.Request.Query["ask"].ToString();
        int width = int.TryParse(context.Request.Query["w"].ToString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int w)
            ? Math.Clamp(w, BotCheckMinWidth, BotCheckMaxWidth)
            : BotCheckMaxWidth;
        var (outcome, frame) = await _backend.BotCheckFrameAsync(askId, width, context.RequestAborted).ConfigureAwait(false);
        var response = context.Response;
        if (outcome != WebOutcome.Ok)
        {
            await ResultAsync(context, StatusOf(outcome), new WebResult(false, outcome == WebOutcome.Forbidden ? WebUiText.BotCheckControlOff : WebUiText.BotCheckGone)).ConfigureAwait(false);
            return;
        }

        if (frame is null)
        {
            response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        string etag = "\"" + frame.Tag + "\"";
        response.Headers.CacheControl = "no-store";
        response.Headers.ETag = etag;
        response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
        if (context.Request.Headers.IfNoneMatch == etag)
        {
            response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "image/jpeg";
        response.ContentLength = frame.Jpeg.Length;
        await response.Body.WriteAsync(frame.Jpeg, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>A camera's still for its sheet on the Home tab (2026-10-27): <c>?ref=</c>, a GET in the camera bucket.</summary>
    public const string CameraPath = "/api/home/camera";

    /// <summary>
    /// A camera's still (2026-10-27): asked with <c>X-Neon: 1</c> like a post, so only the page's own script gets one (an <c>&lt;img&gt;</c> another
    /// page planted sends no header); the picture never cached and kept to this origin. 404 for a reference outside the tiles' reach, 502 with
    /// Home Assistant's reason when it gave none. A HEAD fetches nothing.
    /// </summary>
    private static async Task CameraAsync(HttpContext context, IWebHome home, IPAddress? remote)
    {
        if (context.Request.Headers[CsrfHeader] != "1")
        {
            await ResultAsync(context, StatusCodes.Status403Forbidden, new WebResult(false, WebUiText.PostRefused)).ConfigureAwait(false);
            return;
        }

        var answer = await home.CameraAsync(context.Request.Query["ref"].ToString(), remote, context.RequestAborted).ConfigureAwait(false);
        if (answer.Picture is not { } picture)
        {
            var refusal = answer.Refusal ?? new WebReply(WebOutcome.NotFound);
            int status = refusal.Outcome == WebOutcome.Refused ? StatusCodes.Status502BadGateway : StatusOf(refusal.Outcome);
            await ResultAsync(context, status, new WebResult(false, refusal.Message ?? (status == StatusCodes.Status404NotFound ? WebUiText.HomeChanged : null))).ConfigureAwait(false);
            return;
        }

        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = picture.MediaType;
        response.ContentLength = picture.Bytes.Length;
        await response.Body.WriteAsync(picture.Bytes, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>Where a deferred entry is read whole: <c>/api/entry/{seq}</c> (2026-10-15).</summary>
    public const string EntryPrefix = "/api/entry/";

    /// <summary>The page's 🗑️ (later on 2026-10-15): a post of the picture's id; a Chat API in the Act bucket.</summary>
    public const string PictureDeletePath = "/api/picture/delete";

    /// <summary>The sheet's ⇪ on its 📣 tab (2026-10-16): the page's draft saved as a prompt; a Chat API in the Act bucket.</summary>
    public const string PromptSavePath = "/api/prompts/save";

    /// <summary>A prompt row's 🗑️ (2026-10-16): the prompt's id.</summary>
    public const string PromptDeletePath = "/api/prompts/delete";

    /// <summary>The sheet's 💾 tab (2026-10-16).</summary>
    public const string MemoryPath = "/api/memory";

    /// <summary>The sheet's ⇪ on its 💾 tab (2026-10-16): the page's draft saved as a memory.</summary>
    public const string MemorySavePath = "/api/memory/save";

    /// <summary>A memory row's 🗑️ (2026-10-16): the memory's text.</summary>
    public const string MemoryDeletePath = "/api/memory/delete";

    /// <summary>A memory edited on the 💾 tab (2026-10-18): its text as listed and its new text.</summary>
    public const string MemoryEditPath = "/api/memory/edit";

    /// <summary>A memory turned off or on from the 💾 tab (2026-10-18): its text and the state asked for.</summary>
    public const string MemoryTogglePath = "/api/memory/toggle";

    /// <summary>The message history (2026-10-16): ↑/↓ on the page and the sheet's 🕘 tab.</summary>
    public const string HistoryPath = "/api/history";

    /// <summary>The 🕘 tab's clear all (2026-10-16).</summary>
    public const string HistoryClearPath = "/api/history/clear";

    /// <summary>The status row's 🧠, tapped twice (2026-10-17): the running reflection canceled.</summary>
    public const string LearnCancelPath = "/api/learn/cancel";

    /// <summary>The held queue's Resume (2026-10-17): the lines held after a cancel sent.</summary>
    public const string QueueResumePath = "/api/queue/resume";

    /// <summary>The Shopping tab's 🕘 sheet (2026-10-16): what was bought before and is not on the list now.</summary>
    public const string ShopBoughtPath = "/api/shopping/bought";

    /// <summary>The add box (2026-10-16): <c>{text}</c>; the eight posts below are in the <see cref="WebRateBucket.Act"/> bucket, as a tick.</summary>
    public const string ShopAddPath = "/api/shopping/add";

    /// <summary>A past item added again (2026-10-16): <c>{id}</c>.</summary>
    public const string ShopReAddPath = "/api/shopping/readd";

    /// <summary>A past item forgotten (2026-10-16): <c>{id}</c>.</summary>
    public const string ShopForgetPath = "/api/shopping/forget";

    /// <summary>A line's quantity (2026-10-16): <c>{id, qty}</c>.</summary>
    public const string ShopQuantityPath = "/api/shopping/quantity";

    /// <summary>A line renamed (2026-10-16): <c>{id, name}</c>.</summary>
    public const string ShopRenamePath = "/api/shopping/rename";

    /// <summary>A line removed (2026-10-16): <c>{id}</c>.</summary>
    public const string ShopRemovePath = "/api/shopping/remove";

    /// <summary>Every line ticked or unticked (2026-10-16): <c>{on}</c>.</summary>
    public const string ShopCheckAllPath = "/api/shopping/checkall";

    /// <summary>The ticked lines cleared (2026-10-16); the body is not read.</summary>
    public const string ShopClearPath = "/api/shopping/clear";

    /// <summary>The YouTube tab's event stream (2026-10-16): <c>?page=&amp;load=[&amp;claim=1]</c>, per page, apart from Chat's.</summary>
    public const string YouTubeEventsPath = "/api/youtube/events";

    /// <summary>The YouTube tab's player reports (2026-10-16), in the <see cref="WebRateBucket.Report"/> bucket.</summary>
    public const string YouTubeReportPath = "/api/youtube/report";

    /// <summary>A YouTube page in view or not (later on 2026-10-16), in the <see cref="WebRateBucket.Report"/> bucket.</summary>
    public const string YouTubeSeenPath = "/api/youtube/seen";

    /// <summary>The YouTube tab's saved list (later on 2026-10-16), tagged.</summary>
    public const string YouTubeSavedPath = "/api/youtube/saved";

    /// <summary>A saved video played on the tapping page (later on 2026-10-16), in the <see cref="WebRateBucket.Act"/> bucket as the four below.</summary>
    public const string YouTubeSavedPlayPath = "/api/youtube/saved/play";

    /// <summary>A saved video removed (later on 2026-10-16).</summary>
    public const string YouTubeSavedRemovePath = "/api/youtube/saved/remove";

    /// <summary>Every saved video removed (later on 2026-10-16).</summary>
    public const string YouTubeSavedClearPath = "/api/youtube/saved/clear";

    /// <summary>A saved video moved up, down, to the top or the bottom (later on 2026-10-16).</summary>
    public const string YouTubeSavedMovePath = "/api/youtube/saved/move";

    /// <summary>The playing video saved (later on 2026-10-16).</summary>
    public const string YouTubeSavedSavePath = "/api/youtube/saved/save";

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
        WebOutcome.Forbidden => StatusCodes.Status403Forbidden,
        _ => StatusCodes.Status429TooManyRequests,
    };

    private static void SecurityHeaders(HttpResponse response)
    {
        response.Headers.ContentSecurityPolicy = ContentSecurityPolicy;
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers.XFrameOptions = "DENY";
    }

    /// <summary>
    /// A GET another site started (2026-10-19, the review): the browser's <c>Sec-Fetch-Site</c> says <c>cross-site</c> or <c>same-site</c> (a link or
    /// a redirect from another page, which carries the Lax cookie). The page's own reads say <c>same-origin</c>, and a typed address or a bookmark
    /// <c>none</c>; a browser that sends no such header (older than 2023) is let through, the token still the gate.
    /// </summary>
    private static bool CrossSiteGet(HttpRequest request) =>
        request.Headers["Sec-Fetch-Site"].ToString() is "cross-site" or "same-site";

    /// <summary>A POST a cross-site page could not have made: JSON, and the header no form can set.</summary>
    private static bool CrossSiteSafe(HttpRequest request) =>
        request.Headers[CsrfHeader] == "1"
        && request.ContentType is { } type && type.StartsWith("application/json", StringComparison.OrdinalIgnoreCase);

    private bool SignedIn(HttpContext context) =>
        _binding is { } binding && WebUiAuth.Matches(context.Request.Cookies[WebUiAuth.CookieName(Port)], binding.Token);

    private async Task SignInAsync(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (context.Request.Query.ContainsKey(WebUiAuth.OnceQuery))
        {
            await OnceSignInAsync(context).ConfigureAwait(false);
            return;
        }

        if (_binding is not { } binding || !WebUiAuth.Matches(context.Request.Query["t"].ToString(), binding.Token))
        {
            DiagnosticLog.Info(Category, WebUiText.SignInFailedLogLine(remote));
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await AssetAsync(context, WebUiAssets.SignInPage, status: StatusCodes.Status403Forbidden).ConfigureAwait(false);
            return;
        }

        SetCookie(context, binding);
        _backend.SignedIn(remote);
        // The token leaves the address bar (and the history's next entry) at once.
        context.Response.StatusCode = StatusCodes.Status303SeeOther;
        context.Response.Headers.Location = "/";
    }

    /// <summary>
    /// A one-time code's sign-in (later on 2026-10-15, <c>/webui browser</c>): a GET takes the code, good or not, and a good one sets the cookie and
    /// goes to <c>/</c>; a HEAD (a link checker, a preview) spends nothing and sets no cookie, so the browser's own GET after it still signs in.
    /// A dead code gets the sign-in page with 403, as a wrong token does. The code is never logged.
    /// </summary>
    private async Task OnceSignInAsync(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        context.Response.Headers.CacheControl = "no-store";
        if (HttpMethods.IsHead(context.Request.Method))
        {
            await AssetAsync(context, WebUiAssets.SignInPage).ConfigureAwait(false);
            return;
        }

        if (_binding is not { } binding || !_codes.Take(context.Request.Query[WebUiAuth.OnceQuery].ToString()))
        {
            DiagnosticLog.Info(Category, WebUiText.SignInFailedLogLine(remote));
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await AssetAsync(context, WebUiAssets.SignInPage, status: StatusCodes.Status403Forbidden).ConfigureAwait(false);
            return;
        }

        SetCookie(context, binding);
        _backend.SignedIn(remote);
        context.Response.StatusCode = StatusCodes.Status303SeeOther;
        context.Response.Headers.Location = "/";
    }

    private void SetCookie(HttpContext context, WebUiBinding binding) =>
        context.Response.Cookies.Append(WebUiAuth.CookieName(Port), binding.Token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            MaxAge = WebUiAuth.CookieLifetime,
            IsEssential = true,
        });

    /// <summary>
    /// A sign-in pasted on the page (2026-10-15, E8): the whole link or the bare token, as JSON with the header no other site can send (so no site
    /// can sign a browser in to its own session), checked the way the link is; the same cookie, and the page reloads where it is. Never echoes it.
    /// </summary>
    private async Task PastedSignInAsync(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        context.Response.Headers.CacheControl = "no-store";
        if (!CrossSiteSafe(context.Request))
        {
            await ResultAsync(context, StatusCodes.Status403Forbidden, new WebResult(false, WebUiText.PostRefused)).ConfigureAwait(false);
            return;
        }

        var request = await ReadAsync(context, WebUiJsonContext.Default.WebSignInRequest).ConfigureAwait(false);
        if (_binding is not { } binding || !WebUiAuth.Matches(WebUiAuth.TokenFrom(request?.Link), binding.Token))
        {
            DiagnosticLog.Info(Category, WebUiText.SignInFailedLogLine(remote));
            await ResultAsync(context, StatusCodes.Status403Forbidden, new WebResult(false, WebUiText.SignInWrong)).ConfigureAwait(false);
            return;
        }

        SetCookie(context, binding);
        _backend.SignedIn(remote);
        await ResultAsync(context, StatusCodes.Status200OK, new WebResult(true)).ConfigureAwait(false);
    }

    /// <summary>A text the app makes per request (the theme's sheet, the manifest), cookie-free, with an ETag of its bytes: unchanged, a 304.</summary>
    private static async Task TextAssetAsync(HttpContext context, string text, string type)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        string etag = bytes.Length == 0 ? "\"default\"" : "\"" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))[..16] + "\"";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.ETag = etag;
        if (context.Request.Headers.IfNoneMatch == etag)
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        context.Response.ContentType = type;
        context.Response.ContentLength = bytes.Length;
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await context.Response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
        }
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
            // One heartbeat at a time (2026-10-19, the review: a new 15 s delay for each wake, left running when an event won, piled up timers
            // and registrations on this stream's token while a reply streamed): kept across wakes and made again only once it has fired. Its
            // wait for the reader likewise, across heartbeats.
            Task? beat = null;
            Task<bool>? waiting = null;
            while (!token.IsCancellationRequested)
            {
                // Chat turned off at the PC (later on 2026-10-15): the stream ends, a backgrounded page stops getting the transcript, and its
                // reconnect reads the 404 that sends it on. Read before each batch and at each heartbeat.
                if (!_backend.Tabs().Chat)
                {
                    break;
                }

                while (reader.TryRead(out var item))
                {
                    await WriteEventAsync(response, item.Name, item.Json.Value, token).ConfigureAwait(false);
                }

                await response.Body.FlushAsync(token).ConfigureAwait(false);
                waiting ??= reader.WaitToReadAsync(token).AsTask();
                beat ??= Task.Delay(Heartbeat, _time, token);
                var first = await Task.WhenAny(waiting, beat).ConfigureAwait(false);
                if (token.IsCancellationRequested)
                {
                    break;   // the page went, the server stops, or a newer stream took this one's place
                }

                if (first == beat)
                {
                    beat = null;
                    await response.WriteAsync(": alive\n\n", token).ConfigureAwait(false);
                    continue;
                }

                bool more = await waiting.ConfigureAwait(false);
                waiting = null;
                if (!more)
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

    /// <summary>
    /// The YouTube tab's stream (2026-10-16): <c>?page=&amp;load=[&amp;claim=&lt;id&gt;][&amp;visible=1|0]</c>, each id 16 to 64 base64url
    /// characters or a 400 (a claim of <c>1</c>, the first script's, is still a claim; no <c>visible</c>, the first script's, is in view). The Chat
    /// stream's shape — no-cache, no buffering, linked to the request and the stop, the heartbeat, the tab read before each batch and at each
    /// heartbeat — over the page's own queue from the player, which decides which stream ends (an evicted waiting page, a reconnect replacing a
    /// half-open one) through the stream's own cancel. Never counted among <see cref="MaxStreams"/>: the player holds its own list.
    /// </summary>
    private async Task YouTubeEventsAsync(HttpContext context, IWebVideo video)
    {
        var response = context.Response;
        string page = context.Request.Query["page"].ToString();
        string load = context.Request.Query["load"].ToString();
        string? claim = context.Request.Query["claim"].ToString() is { Length: > 0 } c ? c : null;
        bool visible = context.Request.Query["visible"].ToString() != "0";
        if (!WebVideoPlayer.IsId(page) || !WebVideoPlayer.IsId(load) || (claim is not null && claim != WebVideoPlayer.LegacyClaim && !WebVideoPlayer.IsId(claim)))
        {
            await ResultAsync(context, StatusCodes.Status400BadRequest, new WebResult(false, WebUiText.BadRequest)).ConfigureAwait(false);
            return;
        }

        response.ContentType = "text/event-stream; charset=utf-8";
        response.Headers.CacheControl = "no-cache";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        using var mine = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _stopping.Token);
        void End()
        {
            try
            {
                mine.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // ended meanwhile
            }
        }

        using var attachment = video.Attach(page, load, claim, visible, context.Connection.RemoteIpAddress, End);
        try
        {
            var token = mine.Token;
            await WriteEventAsync(response, "hello", JsonSerializer.Serialize(attachment.Hello, WebUiJsonContext.Default.WebVideoHello), token).ConfigureAwait(false);
            await response.Body.FlushAsync(token).ConfigureAwait(false);
            var reader = attachment.Events;
            Task? beat = null;   // one at a time, as the Chat stream's (2026-10-19)
            Task<bool>? waiting = null;
            while (!token.IsCancellationRequested)
            {
                if (!_backend.Tabs().YouTube)
                {
                    break;   // the tab turned off: the page's reconnect reads the 404 that sends it on
                }

                while (reader.TryRead(out var item))
                {
                    await WriteEventAsync(response, item.Name, item.Json.Value, token).ConfigureAwait(false);
                }

                await response.Body.FlushAsync(token).ConfigureAwait(false);
                waiting ??= reader.WaitToReadAsync(token).AsTask();
                beat ??= Task.Delay(Heartbeat, _time, token);
                var first = await Task.WhenAny(waiting, beat).ConfigureAwait(false);
                if (token.IsCancellationRequested)
                {
                    break;
                }

                if (first == beat)
                {
                    beat = null;
                    await response.WriteAsync(": alive\n\n", token).ConfigureAwait(false);
                    continue;
                }

                bool more = await waiting.ConfigureAwait(false);
                waiting = null;
                if (!more)
                {
                    break;   // dropped for falling behind, or replaced: the page reconnects as the same page
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The page went, the server stops, or the player ended this stream.
        }
    }

    /// <summary>
    /// A YouTube tab's report (2026-10-16): <see cref="WebVideoReport"/> read as every body is, its page id checked and its message held to
    /// <see cref="WebVideoPlayer.MaxReportChars"/> (a 400 otherwise, null here); the player's answer, a 409 from a page that is not the player.
    /// </summary>
    private async Task<WebReply?> YouTubeReportAsync(HttpContext context, IPAddress? remote)
    {
        if (_backend.Video is not { } video)
        {
            return new WebReply(WebOutcome.NotFound);
        }

        if (await ReadAsync(context, WebUiJsonContext.Default.WebVideoReport).ConfigureAwait(false) is not { Page: { } page, Message: { } message }
            || !WebVideoPlayer.IsId(page) || message.Length > WebVideoPlayer.MaxReportChars)
        {
            return null;
        }

        return new WebReply(video.Report(page, message, remote));
    }

    /// <summary>
    /// A YouTube page in view or not (later on 2026-10-16): <see cref="WebVideoSeen"/>, its page id checked (a 400 otherwise, null here); the
    /// player's answer, a 409 from a page with no open stream. No log line: the player writes a Debug one.
    /// </summary>
    private async Task<WebReply?> YouTubeSeenAsync(HttpContext context, IPAddress? remote)
    {
        if (_backend.Video is not { } video)
        {
            return new WebReply(WebOutcome.NotFound);
        }

        if (await ReadAsync(context, WebUiJsonContext.Default.WebVideoSeen).ConfigureAwait(false) is not { Page: { } page } seen || !WebVideoPlayer.IsId(page))
        {
            return null;
        }

        return new WebReply(video.Seen(page, seen.Visible, remote));
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
