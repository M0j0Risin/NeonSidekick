using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.WebUi;

/// <summary>
/// What Tailscale Serve does for one Web UI port (2026-10-31, the user's ask: <c>/webui</c> shows the HTTPS address Serve puts in front of
/// it): <paramref name="Address"/> the https address that forwards to the port (<c>https://host</c>, or <c>https://host:N</c> off 443), null when
/// none does; <paramref name="Public"/> whether Funnel opens a rule to this port to the internet (2026-11-01, the review's P1-1: any of them, not
/// just the one shown); <paramref name="OtherPorts"/> the local ports Serve's https rules forward to instead, ascending, when none forwards to
/// this one (a changed <i>Web UI port</i>), else empty. Since 2026-11-01 (the review's P2-1, P6-1, P7-4, P3-2): <paramref name="Failure"/> why
/// Serve couldn't be asked (the client never returns null), <paramref name="SubPath"/> the path of a rule that forwards to this port below
/// the root only (the page lives at <c>/</c>), and <paramref name="FreeServePort"/> the first of 443, 8443 and 10000 neither Serve nor the
/// Web UI uses, for the hint's command (null: none is free, so no command).
/// </summary>
public sealed record TailscaleServeResult(string? Address, bool Public, IReadOnlyList<int> OtherPorts, string? Failure = null, string? SubPath = null,
    int? FreeServePort = null)
{
    /// <summary>Whether Serve has an https rule in use at all (another port's, or a sub-path to this one): only then is a missing rule hinted at.</summary>
    public bool InUse => OtherPorts.Count > 0 || SubPath is not null;
}

/// <summary>
/// Reading Tailscale Serve's rules (2026-10-31, plan in-the-past-we-ve-iterative-seahorse). <see cref="Match"/> reads the JSON of
/// <c>GET /localapi/v0/serve-config</c> (Tailscale's <c>ipn.ServeConfig</c>, <c>ipn/serve.go</c>): <c>TCP</c> keyed by port, with <c>HTTPS</c> true
/// for an https rule; <c>Web</c> keyed <c>host:port</c> (never an implied 443), each with <c>Handlers</c> keyed by path; <c>AllowFunnel</c> keyed
/// like <c>Web</c>; <c>Foreground</c> keyed by session, each a whole config of its own (a <c>tailscale serve</c> run without <c>--bg</c> keeps its
/// rule there while it runs). <c>Services</c> (Tailscale Services, <c>svc:</c> names) are not read. Pure, <see cref="JsonDocument"/> only.
/// </summary>
public static class TailscaleServe
{
    /// <summary>The serve ports the hint suggests, in order (Funnel's three, so a rule made from it can be funneled or not alike).</summary>
    public static readonly IReadOnlyList<int> SuggestedServePorts = [443, 8443, 10000];

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
                return new TailscaleServeResult(null, false, [], FreeServePort: FreePort([], port));
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var found = new List<(string Host, int ServePort, bool Public)>();
            var others = new SortedSet<int>();
            var taken = new HashSet<int>();
            string? subPath = null;
            Read(root, null, port, found, others, taken, ref subPath);
            if (root.TryGetProperty("Foreground", out var foreground) && foreground.ValueKind == JsonValueKind.Object)
            {
                foreach (var session in foreground.EnumerateObject())
                {
                    if (session.Value.ValueKind == JsonValueKind.Object)
                    {
                        Read(session.Value, root, port, found, others, taken, ref subPath);
                    }
                }
            }

            int? free = FreePort(taken, port);
            if (found.Count == 0)
            {
                return new TailscaleServeResult(null, false, [.. others], SubPath: subPath, FreeServePort: free);
            }

            var best = found.OrderBy(f => f.ServePort == 443 ? 0 : 1).ThenBy(f => f.ServePort).First();
            string address = "https://" + best.Host + (best.ServePort == 443 ? "" : ":" + best.ServePort.ToString(CultureInfo.InvariantCulture));
            return new TailscaleServeResult(address, found.Any(f => f.Public), [], FreeServePort: free);
        }
    }

    /// <summary>The first of <see cref="SuggestedServePorts"/> no <c>TCP</c> key takes and that isn't the Web UI's own <paramref name="port"/>, else null.</summary>
    private static int? FreePort(HashSet<int> taken, int port)
    {
        foreach (int candidate in SuggestedServePorts)
        {
            if (candidate != port && !taken.Contains(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// One config's rules: its <c>TCP</c> keys into <paramref name="taken"/>; its <c>Web</c> rules into <paramref name="found"/> (this port at the
    /// root), <paramref name="others"/> (another local port) or <paramref name="subPath"/> (this port below the root, the first one seen).
    /// </summary>
    private static void Read(JsonElement config, JsonElement? outer, int port, List<(string Host, int ServePort, bool Public)> found, SortedSet<int> others,
        HashSet<int> taken, ref string? subPath)
    {
        if (config.TryGetProperty("TCP", out var tcp) && tcp.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in tcp.EnumerateObject())
            {
                if (int.TryParse(entry.Name, NumberStyles.None, CultureInfo.InvariantCulture, out int used))
                {
                    taken.Add(used);
                }
            }
        }

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
                || !rule.Value.TryGetProperty("Handlers", out var handlers) || handlers.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var handler in handlers.EnumerateObject())
            {
                if (handler.Value.ValueKind != JsonValueKind.Object
                    || !handler.Value.TryGetProperty("Proxy", out var proxy) || proxy.ValueKind != JsonValueKind.String
                    || LocalPort(proxy.GetString()) is not int target)
                {
                    continue;
                }

                if (handler.Name != "/")
                {
                    if (target == port)
                    {
                        subPath ??= handler.Name;
                    }
                }
                else if (target == port)
                {
                    found.Add((host, servePort, Funnel(config, rule.Name) || (outer is { } f && Funnel(f, rule.Name))));
                }
                else
                {
                    others.Add(target);
                }
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

    /// <summary>
    /// The port of a proxy target on this machine (<c>http://127.0.0.1:8765</c>, <c>localhost</c>, <c>[::1]</c>, a trailing <c>/</c> allowed), else
    /// null. Only those three hosts (2026-11-01, the review's P8-2): under <c>local</c> Kestrel's <c>ListenLocalhost</c> binds just 127.0.0.1 and
    /// ::1, so a rule to 127.0.0.2 would 502 there. Under <c>lan</c> (any address) such a rule would work but goes unseen: one rule for both
    /// reaches, since a 127.x other than .1 is rare and a link that 502s is the worse failure.
    /// </summary>
    private static int? LocalPort(string? proxy) =>
        Uri.TryCreate(proxy, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp
        && (uri.Host == "127.0.0.1" || uri.Host == "[::1]" || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        && uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.UserInfo)
            ? uri.Port
            : null;

    /// <summary>
    /// Whether Tailscale is up on this machine among <paramref name="adapters"/> (the ones that are up): Tailscale's own adapter holding a
    /// tailnet address, IPv4 or (2026-11-01, the review's P7-2) IPv6 (<see cref="WebUiController.TailnetAddresses"/>, shared with the tabs). The
    /// gate before the pipe is asked: a missing pipe makes a connect wait its whole timeout. Serve's rules outlive <c>tailscale down</c>, so the
    /// adapter, not the pipe, is what says Tailscale is in use. Pure.
    /// </summary>
    public static bool TailscaleUp(IEnumerable<WebUiAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        return WebUiController.TailnetAddresses(adapters).Any();
    }
}

/// <summary>
/// Asks Tailscale's local service for Serve's rules (2026-10-31): <c>GET /localapi/v0/serve-config</c> over the pipe
/// <c>\\.\pipe\ProtectedPrefix\Administrators\Tailscale\tailscaled</c>, the way <c>tailscale.exe</c> does (no process is started, so no new
/// process-start site), through <see cref="Docker.DockerPipe.ConnectRawAsync"/>: every connection a fresh pipe instance through the handler's
/// <c>ConnectCallback</c>. The request needs <c>Sec-Tailscale: localapi</c> and the host <c>local-tailscaled.sock</c>. The pipe is opened at
/// <see cref="TokenImpersonationLevel.Impersonation"/>: tailscaled, already SYSTEM, reads which Windows user asks (without it the answer is 401
/// "Unable to impersonate"), exactly as <c>tailscale.exe</c> lets it; it grants nothing new. Every failure (no pipe, 401, a timeout, bad JSON,
/// anything else short of running out of memory: 2026-11-01, the review's P2-1) is a result carrying its kind, never null and never logged here
/// (<see cref="TailscaleServeDetector"/> logs); a cancel of the caller's token is rethrown, never read as "no HTTPS". One client for the process.
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

    private readonly HttpClient _http;

    public TailscaleServeClient(string pipe = DefaultPipe)
    {
        string name = pipe;
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = (_, cancellationToken) =>
                Docker.DockerPipe.ConnectRawAsync(name, ConnectTimeout, TokenImpersonationLevel.Impersonation, cancellationToken),
            UseProxy = false,
            AllowAutoRedirect = false,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(5),
        };
        _http = new HttpClient(handler) { BaseAddress = new Uri("http://local-tailscaled.sock"), Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Serve's answer for <paramref name="port"/> (<see cref="TailscaleServe.Match"/>), or a result naming the failure.</summary>
    public async Task<TailscaleServeResult> FindAsync(int port, CancellationToken cancellationToken)
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
                return Failed(WebUiText.ServeFailureStatus((int)response.StatusCode));
            }

            string body = await response.Content.ReadAsStringAsync(budget.Token).ConfigureAwait(false);
            return TailscaleServe.Match(body, port) ?? Failed(WebUiText.ServeFailureNotRules);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed(WebUiText.ServeFailureTimeout);
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested && e is not OutOfMemoryException)
        {
            return Failed(WebUiText.ServeFailureError(e));
        }
    }

    private static TailscaleServeResult Failed(string kind) => new(null, false, [], Failure: kind);

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// The seam <c>Program</c> hands the screen (2026-10-31): asks <paramref name="ask"/> (the client) only while Tailscale is up
/// (<see cref="TailscaleServe.TailscaleUp"/> over <paramref name="adapters"/>, read at each ask, so a Tailscale started after the app is found),
/// and writes why there is or isn't an HTTPS address to the log, once per change of answer: the address alone, never a link or the token.
/// Since 2026-11-01 (the review's P1-16, P2-5, P2-9, P4-4, P5-2, P9-3) it alone keeps the answer and the log: one slot (one Web UI port is
/// served at a time; another port's ask replaces it), an answer reused <see cref="KeepFor"/> by the clock's timestamps (never backward), a
/// failure never kept, one ask in flight shared by every caller — run under no caller's token, so one caller's cancel leaves the others'
/// answer — and only a fresh answer logged.
/// </summary>
public sealed class TailscaleServeDetector
{
    /// <summary>How long an answer is reused: the pane's reopen after a button press doesn't ask again.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromSeconds(5);

    private const string Category = "WebUi";
    private readonly Func<IReadOnlyList<WebUiAdapter>> _adapters;
    private readonly Func<int, CancellationToken, Task<TailscaleServeResult>> _ask;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private (int Port, Task<TailscaleServeResult> Answer, long Started)? _slot;
    private string? _lastLine;

    public TailscaleServeDetector(Func<IReadOnlyList<WebUiAdapter>> adapters, Func<int, CancellationToken, Task<TailscaleServeResult>> ask,
        TimeProvider? time = null)
    {
        _adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
        _ask = ask ?? throw new ArgumentNullException(nameof(ask));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Serve's answer for <paramref name="port"/> (a failure carried in it), or null when no Tailscale adapter is up.</summary>
    public async Task<TailscaleServeResult?> FindAsync(int port, CancellationToken cancellationToken)
    {
        if (!TailscaleServe.TailscaleUp(_adapters()))
        {
            Log(WebUiText.ServeNoAdapterLogLine, info: false);
            return null;
        }

        Task<TailscaleServeResult> answer;
        TaskCompletionSource<TailscaleServeResult>? mine = null;
        lock (_gate)
        {
            if (_slot is { } slot && slot.Port == port
                && (!slot.Answer.IsCompleted
                    || (slot.Answer.IsCompletedSuccessfully && slot.Answer.Result.Failure is null && _time.GetElapsedTime(slot.Started) < KeepFor)))
            {
                answer = slot.Answer;
            }
            else
            {
                mine = new TaskCompletionSource<TailscaleServeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                answer = mine.Task;
                _slot = (port, answer, _time.GetTimestamp());
            }
        }

        if (mine is not null)
        {
            _ = AskAsync(port, mine);
        }

        return await answer.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The shared ask: under no caller's token (the client's own budget bounds it), never throwing; its answer logged when it changed.</summary>
    private async Task AskAsync(int port, TaskCompletionSource<TailscaleServeResult> answer)
    {
        TailscaleServeResult result;
        try
        {
            result = await _ask(port, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            result = new TailscaleServeResult(null, false, [], Failure: WebUiText.ServeFailureError(e));
        }

        if (result.Failure is { } failure)
        {
            Log(WebUiText.ServeAskFailedLogLine(failure), info: false);
        }
        else if (result.Address is { } address)
        {
            Log(WebUiText.HttpsAddressLogLine(address, result.Public), info: true);
        }
        else
        {
            Log(WebUiText.ServeNoRuleLogLine(port, result.OtherPorts), info: false);
        }

        answer.SetResult(result);
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
