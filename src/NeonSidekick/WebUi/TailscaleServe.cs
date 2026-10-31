using System.Globalization;
using System.IO.Pipes;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.WebUi;

/// <summary>
/// What Tailscale Serve does for one Web UI port (2026-10-31, the user's ask: <c>/webui</c> shows the HTTPS address Serve puts in front of
/// it): <paramref name="Address"/> the https address that forwards to the port (<c>https://host</c>, or <c>https://host:N</c> off 443), null when
/// none does; <paramref name="Public"/> whether Funnel opens that address to the internet; <paramref name="OtherPorts"/> the local ports Serve's
/// https rules forward to instead, ascending, when none forwards to this one (a changed <i>Web UI port</i>), else empty.
/// </summary>
public sealed record TailscaleServeResult(string? Address, bool Public, IReadOnlyList<int> OtherPorts);

/// <summary>
/// Reading Tailscale Serve's rules (2026-10-31, plan in-the-past-we-ve-iterative-seahorse). <see cref="Match"/> reads the JSON of
/// <c>GET /localapi/v0/serve-config</c> (Tailscale's <c>ipn.ServeConfig</c>, <c>ipn/serve.go</c>): <c>TCP</c> keyed by port, with <c>HTTPS</c> true
/// for an https rule; <c>Web</c> keyed <c>host:port</c> (never an implied 443), each with <c>Handlers</c> keyed by path; <c>AllowFunnel</c> keyed
/// like <c>Web</c>; <c>Foreground</c> keyed by session, each a whole config of its own (a <c>tailscale serve</c> run without <c>--bg</c> keeps its
/// rule there while it runs). <c>Services</c> (Tailscale Services, <c>svc:</c> names) are not read. Pure, <see cref="JsonDocument"/> only.
/// </summary>
public static class TailscaleServe
{
    /// <summary>
    /// The https address of Serve's rule whose root handler (<c>/</c>: the page expects to live at the root) proxies to
    /// <c>http://127.0.0.1:<paramref name="port"/></c>, <c>http://localhost:…</c> or <c>http://[::1]:…</c>, on a port whose <c>TCP</c> entry is
    /// https (an <c>--http</c> rule never gives an https address). More than one: 443 first, then the lowest port. Null when the body is not
    /// JSON or not an object (JSON <c>null</c>, no rules at all, is an empty result). Pure.
    /// </summary>
    public static TailscaleServeResult? Match(string json, int port)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json ?? "");
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Null)
            {
                return new TailscaleServeResult(null, false, []);
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var found = new List<(string Host, int ServePort, bool Public)>();
            var others = new SortedSet<int>();
            Read(root, null, port, found, others);
            if (root.TryGetProperty("Foreground", out var foreground) && foreground.ValueKind == JsonValueKind.Object)
            {
                foreach (var session in foreground.EnumerateObject())
                {
                    if (session.Value.ValueKind == JsonValueKind.Object)
                    {
                        Read(session.Value, root, port, found, others);
                    }
                }
            }

            if (found.Count == 0)
            {
                others.Remove(port);
                return new TailscaleServeResult(null, false, [.. others]);
            }

            var best = found.OrderBy(f => f.ServePort == 443 ? 0 : 1).ThenBy(f => f.ServePort).First();
            string address = "https://" + best.Host + (best.ServePort == 443 ? "" : ":" + best.ServePort.ToString(CultureInfo.InvariantCulture));
            return new TailscaleServeResult(address, best.Public, []);
        }
    }

    /// <summary>One config's <c>Web</c> rules into <paramref name="found"/> (this port) and <paramref name="others"/> (another local port).</summary>
    private static void Read(JsonElement config, JsonElement? outer, int port, List<(string, int, bool)> found, SortedSet<int> others)
    {
        if (!config.TryGetProperty("Web", out var web) || web.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var rule in web.EnumerateObject())
        {
            int colon = rule.Name.LastIndexOf(':');
            if (colon <= 0 || !int.TryParse(rule.Name.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int servePort)
                || servePort is < 1 or > 65535)
            {
                continue;
            }

            string host = rule.Name[..colon];
            if (!IsHttps(config, servePort) && !(outer is { } o && IsHttps(o, servePort)))
            {
                continue;
            }

            if (rule.Value.ValueKind != JsonValueKind.Object
                || !rule.Value.TryGetProperty("Handlers", out var handlers) || handlers.ValueKind != JsonValueKind.Object
                || !handlers.TryGetProperty("/", out var handler) || handler.ValueKind != JsonValueKind.Object
                || !handler.TryGetProperty("Proxy", out var proxy) || proxy.ValueKind != JsonValueKind.String
                || LocalPort(proxy.GetString()) is not int target)
            {
                continue;
            }

            if (target == port)
            {
                found.Add((host, servePort, Funnel(config, rule.Name) || (outer is { } f && Funnel(f, rule.Name))));
            }
            else
            {
                others.Add(target);
            }
        }
    }

    private static bool IsHttps(JsonElement config, int servePort) =>
        config.TryGetProperty("TCP", out var tcp) && tcp.ValueKind == JsonValueKind.Object
        && tcp.TryGetProperty(servePort.ToString(CultureInfo.InvariantCulture), out var entry) && entry.ValueKind == JsonValueKind.Object
        && entry.TryGetProperty("HTTPS", out var https) && https.ValueKind == JsonValueKind.True;

    private static bool Funnel(JsonElement config, string key) =>
        config.TryGetProperty("AllowFunnel", out var funnel) && funnel.ValueKind == JsonValueKind.Object
        && funnel.TryGetProperty(key, out var on) && on.ValueKind == JsonValueKind.True;

    /// <summary>The port of a proxy target on this machine (<c>http://127.0.0.1:8765</c>, <c>localhost</c>, <c>[::1]</c>, a trailing <c>/</c> allowed), else null.</summary>
    private static int? LocalPort(string? proxy) =>
        Uri.TryCreate(proxy, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback
        && uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.UserInfo)
            ? uri.Port
            : null;

    /// <summary>
    /// Whether Tailscale is up on this machine among <paramref name="adapters"/> (the ones that are up): Tailscale's own adapter
    /// (<see cref="WebUiController.IsTailscaleAdapter(WebUiAdapter)"/>) holding a tailnet address. The gate before the pipe is asked: a missing
    /// pipe makes a connect wait its whole timeout. Pure.
    /// </summary>
    public static bool TailscaleUp(IEnumerable<WebUiAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        return adapters.Any(a => WebUiController.IsTailscaleAdapter(a) && a.Addresses.Any(WebUiController.IsTailnet));
    }
}

/// <summary>
/// Asks Tailscale's local service for Serve's rules (2026-10-31): <c>GET /localapi/v0/serve-config</c> over the pipe
/// <c>\\.\pipe\ProtectedPrefix\Administrators\Tailscale\tailscaled</c>, the way <c>tailscale.exe</c> does (no process is started, so no new
/// process-start site), the <see cref="Docker.DockerPipe"/> shape: every connection a fresh pipe instance through the handler's
/// <c>ConnectCallback</c>. The request needs <c>Sec-Tailscale: localapi</c> and the host <c>local-tailscaled.sock</c>. The pipe is opened at
/// <see cref="TokenImpersonationLevel.Impersonation"/>: tailscaled, already SYSTEM, reads which Windows user asks (without it the answer is 401
/// "Unable to impersonate"), exactly as <c>tailscale.exe</c> lets it; it grants nothing new. Every failure (no pipe, 401, a timeout, bad JSON)
/// is null and one Debug line per kind; a cancel of the caller's token is rethrown, never read as "no HTTPS". One client for the process.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TailscaleServeClient : IDisposable
{
    /// <summary>Tailscale's pipe, bare (the tests serve their own).</summary>
    public const string DefaultPipe = @"ProtectedPrefix\Administrators\Tailscale\tailscaled";

    /// <summary>How long a connect waits for a pipe instance.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>How long the whole request may take.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);

    private const string Category = "WebUi";
    private readonly HttpClient _http;
    private readonly object _gate = new();
    private string? _lastFailure;

    public TailscaleServeClient(string pipe = DefaultPipe)
    {
        string name = pipe;
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = (_, cancellationToken) => ConnectAsync(name, cancellationToken),
            UseProxy = false,
            AllowAutoRedirect = false,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(5),
        };
        _http = new HttpClient(handler) { BaseAddress = new Uri("http://local-tailscaled.sock"), Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Serve's answer for <paramref name="port"/> (<see cref="TailscaleServe.Match"/>), or null on any failure.</summary>
    public async Task<TailscaleServeResult?> FindAsync(int port, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(RequestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/localapi/v0/serve-config");
            request.Headers.Add("Sec-Tailscale", "localapi");
            using var response = await _http.SendAsync(request, budget.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                Failed("HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
                return null;
            }

            string body = await response.Content.ReadAsStringAsync(budget.Token).ConfigureAwait(false);
            if (TailscaleServe.Match(body, port) is not { } result)
            {
                Failed("an answer that is not Serve's rules");
                return null;
            }

            lock (_gate)
            {
                _lastFailure = null;
            }

            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Failed("no answer in time");
            return null;
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested
            && e is HttpRequestException or IOException or UnauthorizedAccessException or TimeoutException)
        {
            Failed("no connection (" + (e.InnerException ?? e).GetType().Name + ")");
            return null;
        }
    }

    private void Failed(string kind)
    {
        lock (_gate)
        {
            if (_lastFailure == kind)
            {
                return;
            }

            _lastFailure = kind;
        }

        DiagnosticLog.Debug(Category, WebUiText.ServeAskFailedLogLine(kind));
    }

    private static async ValueTask<Stream> ConnectAsync(string name, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
        try
        {
            await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// The seam <c>Program</c> hands the screen (2026-10-31): asks <paramref name="ask"/> (the client) only while Tailscale is up
/// (<see cref="TailscaleServe.TailscaleUp"/> over <paramref name="adapters"/>, read at each ask, so a Tailscale started after the app is found),
/// and writes why there is or isn't an HTTPS address to the log, once per change of answer: the address alone, never a link or the token.
/// </summary>
public sealed class TailscaleServeDetector
{
    private const string Category = "WebUi";
    private readonly Func<IReadOnlyList<WebUiAdapter>> _adapters;
    private readonly Func<int, CancellationToken, Task<TailscaleServeResult?>> _ask;
    private readonly object _gate = new();
    private string? _lastLine;

    public TailscaleServeDetector(Func<IReadOnlyList<WebUiAdapter>> adapters, Func<int, CancellationToken, Task<TailscaleServeResult?>> ask)
    {
        _adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
        _ask = ask ?? throw new ArgumentNullException(nameof(ask));
    }

    /// <summary>Serve's answer for <paramref name="port"/>, or null (no Tailscale up, or the ask failed).</summary>
    public async Task<TailscaleServeResult?> FindAsync(int port, CancellationToken cancellationToken)
    {
        if (!TailscaleServe.TailscaleUp(_adapters()))
        {
            Log(WebUiText.ServeNoAdapterLogLine, info: false);
            return null;
        }

        var result = await _ask(port, cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            // The client logged its failure; the next answer is written again.
            lock (_gate)
            {
                _lastLine = null;
            }
        }
        else if (result.Address is { } address)
        {
            Log(WebUiText.HttpsAddressLogLine(address, result.Public), info: true);
        }
        else
        {
            Log(WebUiText.ServeNoRuleLogLine(port, result.OtherPorts), info: false);
        }

        return result;
    }

    private void Log(string line, bool info)
    {
        lock (_gate)
        {
            if (_lastLine == line)
            {
                return;
            }

            _lastLine = line;
        }

        if (info)
        {
            DiagnosticLog.Info(Category, line);
        }
        else
        {
            DiagnosticLog.Debug(Category, line);
        }
    }
}
