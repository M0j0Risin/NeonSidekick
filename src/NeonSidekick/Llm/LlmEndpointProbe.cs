using System.Globalization;
using System.Net;
using System.Text.Json;
using NeonSidekick.Diagnostics;
using NeonSidekick.Llm.Anthropic;
using NeonSidekick.Settings;

namespace NeonSidekick.Llm;

/// <summary>What one <c>GET /v1/models</c> found.</summary>
/// <param name="Exists">An OpenAI-compatible API answered (200, or 401/403 asking for a key).</param>
/// <param name="ModelIds">Chat-capable model ids the server listed; empty when it answered 401/403 or listed none.</param>
/// <param name="Detail">A human phrase for logs and the status line.</param>
/// <param name="OwnedBy">The first non-blank <c>owned_by</c> the list carried (what the server calls itself), else null.</param>
/// <param name="ModelsJson">The 200 body itself, for a second reader (<see cref="ContextLengthProbe.ParseModelsWindow"/>: vLLM and SGLang publish the context window on it); null when the server gave no list.</param>
public readonly record struct ProbeResult(bool Exists, IReadOnlyList<string> ModelIds, string Detail, string? OwnedBy = null, string? ModelsJson = null)
{
    public static ProbeResult Missing(string detail) => new(false, Array.Empty<string>(), detail);
}

/// <summary>
/// Finds the OpenAI-compatible server: the configured URL if there is one, otherwise the usual
/// ports — on this machine, on every other machine of the local network, or both, per the
/// <c>LLM scan mode</c> setting (<see cref="LlmScanMode"/>, <see cref="LanHosts"/>) — each asked
/// for its model list.
///
/// <para><b>One HTTP request per candidate and deliberately no TCP pre-check.</b> A raw
/// <c>TcpClient</c> connect to <c>localhost</c> resolves to both <c>::1</c> and <c>127.0.0.1</c>
/// and does not fall back between them the way <see cref="HttpClient"/> does — LM Studio binds
/// IPv4 only, so the pre-check rejected a perfectly healthy server before the real request was
/// ever made. Connection-refused fails fast over HTTP anyway. The candidates name
/// <c>127.0.0.1</c> outright for the same reason. <b>[scar]</b></para>
///
/// <para>A TCP connect alone is also not enough: port 8080 is a popular default, so a dev server
/// on the same box answers the socket, the probe passes, and the first real chat throws. Asking
/// for <c>/v1/models</c> is what proves the thing on the port speaks the protocol.</para>
///
/// <para>Never throws. A server that dies after the probe still fails at request time; that
/// residual race is accepted.</para>
/// </summary>
public sealed class LlmEndpointProbe
{
    private const string Category = "Llm";

    /// <summary>
    /// Three seconds, not one: this is a real HTTP round trip, and a server that has just started
    /// or holds many models can take longer than a socket connect to answer. Paid once at startup,
    /// in parallel across the candidates, so the worst case is one timeout, not five.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    /// <summary>LM Studio, vLLM, SGLang, llama.cpp, Ollama, Unsloth — in the order they are preferred when several answer.</summary>
    public static readonly int[] CandidatePorts = { 1234, 8000, 30000, 8080, 11434, 8888 };

    /// <summary>The candidate base URLs, already normalised to <c>/v1</c>.</summary>
    public static readonly IReadOnlyList<Uri> CandidateBaseUrls =
        CandidatePorts.Select(p => new Uri("http://127.0.0.1:" + p.ToString(CultureInfo.InvariantCulture) + "/v1")).ToArray();

    private readonly HttpClient _http;
    private readonly Func<LanHosts> _lanHosts;

    /// <param name="http">The transport; tests pass one over a stub handler. The probe applies its own per-call timeout.</param>
    /// <param name="timeout">Per-call ceiling; defaults to <see cref="DefaultTimeout"/>. Tests shorten it.</param>
    /// <param name="lanHosts">The other machines on the network, asked only under <see cref="ScanScope.Remote"/> / <see cref="ScanScope.Both"/>; defaults to <see cref="LanHosts.Discover"/>. Tests pass a fixed list.</param>
    public LlmEndpointProbe(HttpClient http, TimeSpan? timeout = null, Func<LanHosts>? lanHosts = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _lanHosts = lanHosts ?? LanHosts.Discover;
        Timeout = timeout ?? DefaultTimeout;
        if (Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), Timeout, "The probe timeout must be positive.");
        }
    }

    /// <summary>The per-call ceiling in force.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>The Claude API's model-list ceiling when <see cref="Timeout"/> is shorter (2026-09-27): a hosted API over the internet, not a local port.</summary>
    public static readonly TimeSpan ClaudeApiTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The comma-separated port list, for the "nothing found" message.</summary>
    public static string CandidatePortList =>
        string.Join(", ", CandidatePorts.Select(p => p.ToString(CultureInfo.InvariantCulture)));

    /// <summary>The base URL of one candidate port on one host, normalised to <c>/v1</c>.</summary>
    public static Uri CandidateUrl(IPAddress host, int port)
    {
        ArgumentNullException.ThrowIfNull(host);
        return new Uri("http://" + host + ":" + port.ToString(CultureInfo.InvariantCulture) + "/v1");
    }

    /// <summary>
    /// The candidates under <paramref name="scope"/>: <see cref="CandidateBaseUrls"/> for
    /// <see cref="ScanScope.Local"/>; every <see cref="CandidatePorts"/> entry on every
    /// <see cref="LanHosts.Hosts"/> address, host-major (a machine's servers adjacent), for
    /// <see cref="ScanScope.Remote"/>; the local list first, then the network, for
    /// <see cref="ScanScope.Both"/>; nothing at all for <see cref="ScanScope.Disabled"/>. The network
    /// is read only when the scope includes it.
    /// </summary>
    public (IReadOnlyList<Uri> Urls, LanHosts Network) Candidates(ScanScope scope)
    {
        if (!LlmScanMode.Scans(scope))
        {
            return ([], LanHosts.None);
        }

        var network = LlmScanMode.IncludesNetwork(scope) ? _lanHosts() : LanHosts.None;
        var urls = new List<Uri>();
        if (scope != ScanScope.Remote)
        {
            urls.AddRange(CandidateBaseUrls);
        }

        foreach (var host in network.Hosts)
        {
            foreach (int port in CandidatePorts)
            {
                urls.Add(CandidateUrl(host, port));
            }
        }

        return (urls, network);
    }

    /// <summary>The one log line a network scan leaves in place of a miss per host. Pinned.</summary>
    public static string ScanSummary(LanHosts network, int answered)
    {
        ArgumentNullException.ThrowIfNull(network);
        string where = network.Subnets.Count == 0 ? "no local network" : string.Join(", ", network.Subnets);
        return $"Scanned {network.Hosts.Count.ToString(CultureInfo.InvariantCulture)} hosts on {where} (ports {CandidatePortList}): {answered.ToString(CultureInfo.InvariantCulture)} answered.";
    }

    /// <summary>
    /// One <c>GET {v1}/models</c>. 200 → exists with the chat models it listed; 401/403 → exists
    /// (an API that wants a key is still an API); anything else, a timeout or a refused connection
    /// → does not exist, with the reason in <see cref="ProbeResult.Detail"/>.
    /// </summary>
    public async Task<ProbeResult> ProbeAsync(Uri baseUrl, string? apiKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        var v1 = LlmEndpoint.NormalizeBaseUrl(baseUrl);

        // The Claude API (2026-09-27): its own key headers and a page size that lists every model at once, and a
        // longer ceiling — it is across the internet, not on a port of this network.
        bool claude = ClaudeApi.IsClaudeApi(v1);
        var modelsUrl = claude ? ClaudeApi.ModelsUrl(v1) : LlmEndpoint.ModelsUrl(v1);

        try
        {
            // A per-call ceiling on a startup probe, not the turn budget; the linked-CTS rule
            // is about the turn path, where cancellation means barge-in.
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(claude && Timeout < ClaudeApiTimeout ? ClaudeApiTimeout : Timeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
            if (claude)
            {
                ClaudeApi.AddHeaders(request, apiKey);
            }
            else if (!string.IsNullOrWhiteSpace(apiKey))
            {
                // TryAddWithoutValidation: a key with characters HttpHeaders would reject is the
                // server's problem, not a reason to skip the probe.
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey.Trim());
            }

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, budget.Token).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync(budget.Token).ConfigureAwait(false);
                var (ids, ownedBy) = ParseModels(body);
                return new ProbeResult(true, ids, ids.Count == 1 ? "1 chat model" : $"{ids.Count.ToString(CultureInfo.InvariantCulture)} chat models", ownedBy, body);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new ProbeResult(true, Array.Empty<string>(), claude
                    ? $"{(int)response.StatusCode} on /v1/models; check Claude API key"
                    : $"{(int)response.StatusCode} on /v1/models; the server wants a key");
            }

            return ProbeResult.Missing($"{(int)response.StatusCode} on /v1/models; not an OpenAI-compatible server");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ProbeResult.Missing("cancelled");
        }
        catch (OperationCanceledException)
        {
            return ProbeResult.Missing($"no answer within {LlmTimeouts.Format(Timeout)}");
        }
        catch (Exception ex)
        {
            return ProbeResult.Missing(ex.Message);
        }
    }

    /// <summary>
    /// Probes every candidate in parallel and returns the first <em>in list order</em> that
    /// exists, so a slow LM Studio still beats a fast Ollama. Null when nothing answered. What
    /// headless and a console without menus use; the chat screen asks <see cref="DiscoverAllAsync"/>
    /// and lets the user choose when several answer.
    /// </summary>
    public async Task<LlmEndpoint?> DiscoverAsync(string? apiKey, ScanScope scope, CancellationToken cancellationToken)
    {
        var servers = await DiscoverAllAsync(apiKey, extra: null, scope, cancellationToken).ConfigureAwait(false);
        return servers.Count == 0 ? null : Endpoint(servers[0], apiKey, configuredModel: null, configured: false);
    }

    /// <summary>
    /// Probes every candidate of <paramref name="scope"/> (<see cref="Candidates"/>) in parallel
    /// and returns each one that exists, in list order — the rows of the server picker.
    /// <paramref name="extra"/> (the endpoint in use, which may sit on a port the list does not
    /// name) is probed alongside and appended last when it is not already a candidate and answers
    /// — never a loopback one under <see cref="ScanScope.Remote"/>: the scope's rule wins, and
    /// <c>remote</c> means this machine's servers stay out even when one of them is in use.
    /// Empty when nothing answered.
    ///
    /// <para>A network scan is one wave: a /24 is 253 hosts × six ports, every request in flight at
    /// once under the per-call ceiling, so the whole thing costs one timeout (three or four seconds)
    /// rather than minutes — a strict network monitor may notice the burst; that is the price of a
    /// scan with no pre-check. A remote miss leaves no log line of its own (1,500 of them would bury
    /// the log); the scan leaves <see cref="ScanSummary"/> instead.</para>
    ///
    /// <para>Under <see cref="ScanScope.Disabled"/> nothing is asked — not even <paramref name="extra"/>
    /// (a bare <c>/server</c> hands it the endpoint in use): the answer is empty, no request, no log line.</para>
    /// </summary>
    public async Task<IReadOnlyList<LlmServer>> DiscoverAllAsync(string? apiKey, Uri? extra, ScanScope scope, CancellationToken cancellationToken)
    {
        if (!LlmScanMode.Scans(scope))
        {
            return [];
        }

        var (candidates, network) = Candidates(scope);
        var urls = new List<Uri>(candidates);
        if (extra is not null)
        {
            var v1 = LlmEndpoint.NormalizeBaseUrl(extra);

            // The Claude API is never scanned for: its row is the session's to add, with its own key (2026-09-27). Nor is the
            // embedded model's sentinel (2026-09-29): its rows are the session's too, and the sentinel is no place to ask.
            if (!urls.Contains(v1) && !(scope == ScanScope.Remote && v1.IsLoopback) && !ClaudeApi.IsClaudeApi(v1) && !EmbeddedLlm.EmbeddedEndpoint.IsEmbedded(v1)) urls.Add(v1);
        }

        var tasks = urls.Select(u => ProbeAsync(u, apiKey, cancellationToken)).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        // The network's candidates sit after the local ones and before the extra: [remoteStart, remoteEnd).
        int remoteStart = scope == ScanScope.Remote ? 0 : CandidateBaseUrls.Count;
        int remoteEnd = remoteStart + network.Hosts.Count * CandidatePorts.Length;

        var found = new List<LlmServer>();
        int remoteAnswered = 0;
        for (int i = 0; i < results.Length; i++)
        {
            bool remote = i >= remoteStart && i < remoteEnd;
            if (results[i].Exists)
            {
                DiagnosticLog.Info(Category, $"Found an OpenAI-compatible server at {urls[i]} ({results[i].Detail}).");
                found.Add(LlmServer.From(urls[i], results[i]));
                if (remote) remoteAnswered++;
            }
            else if (!remote)
            {
                DiagnosticLog.Debug(Category, $"{urls[i]}: {results[i].Detail}");
            }
        }

        if (LlmScanMode.IncludesNetwork(scope))
        {
            DiagnosticLog.Info(Category, ScanSummary(network, remoteAnswered));
        }

        return found;
    }

    /// <summary>
    /// The endpoint for a probed server: the ONE place a probe answer becomes what the client
    /// talks to. The model is <paramref name="configuredModel"/> when given, else the first the
    /// server listed, else <see cref="LlmEndpoint.FallbackModelId"/>; the source phrase sits after
    /// the model on the status line and names where it came from: <c>configured</c> for a model
    /// the settings name, <c>first listed</c> for one the server's list supplied (the settings
    /// pane's own words), <c>configured, not answering</c> when the URL gave no list, and
    /// <c>probed &lt;url&gt;</c> for a discovered server. The context window the list published for
    /// the model, if any, rides along (<see cref="LlmEndpoint.PublishedContextLength"/>): the same
    /// payload read twice, no second request.
    /// </summary>
    public static LlmEndpoint Endpoint(LlmServer server, string? apiKey, string? configuredModel, bool configured)
    {
        ArgumentNullException.ThrowIfNull(server);
        string source = configured
            ? !server.Result.Exists ? "configured, not answering" : configuredModel is null ? "first listed" : "configured"
            : "probed " + server.BaseUrl;
        string modelId = configuredModel ?? FirstOrFallback(server.Result.ModelIds);
        var published = server.Result.ModelsJson is { } json ? ContextLengthProbe.ParseModelsWindow(json, modelId) : null;
        return new LlmEndpoint(server.BaseUrl, modelId, NormalizeKey(apiKey), source, published);
    }

    /// <summary>
    /// The endpoint the app will talk to, from the effective settings. A configured URL is an
    /// instruction: it is probed once for its model list and used even when it does not answer
    /// (the first turn then reports the failure honestly), and no other URL is tried — probing
    /// elsewhere after a configured server fails would connect to one the user did not ask for.
    /// An empty URL means discover under the saved scan mode — null with no request when the mode is
    /// <c>disabled</c>. A configured model id always wins over a listed one.
    /// </summary>
    public async Task<LlmEndpoint?> ResolveAsync(AppSettingsData effective, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(effective);
        string? configuredModel = string.IsNullOrWhiteSpace(effective.LlmModel) ? null : effective.LlmModel.Trim();

        if (EmbeddedLlm.EmbeddedEndpoint.Chosen(effective))
        {
            // The embedded model's sentinel (2026-09-29) is started, not probed: LlmSession does that before it gets here.
            DiagnosticLog.Error(Category, "The LLM URL names the embedded model, which this mode cannot start.");
            return null;
        }

        if (EmbeddedLlm.EmbeddedEndpoint.SwitchedOff(effective))
        {
            // Saved while the embedded model was on (2026-09-29); switched off, the URL stands for nothing, like the Claude API's below.
            DiagnosticLog.Warn(Category, EmbeddedLlm.EmbeddedLlmText.SwitchedOffWarning);
        }
        else if (ClaudeApi.IsClaudeApi(effective.LlmUrl) && !ClaudeApi.Offered(effective))
        {
            // Saved while the Claude API was on (2026-09-27); with it off or keyless the URL stands for nothing, and the
            // settings' scan finds a server as a blank URL would.
            DiagnosticLog.Warn(Category, ClaudeApiText.NotOfferedWarning);
        }
        else if (!string.IsNullOrWhiteSpace(effective.LlmUrl))
        {
            Uri v1;
            try
            {
                v1 = LlmEndpoint.NormalizeBaseUrl(effective.LlmUrl);
            }
            catch (ArgumentException ex)
            {
                DiagnosticLog.Error(Category, $"The configured LLM URL is not usable: {ex.Message}");
                return null;
            }

            string key = ClaudeApi.KeyFor(effective, v1);
            var result = await ProbeAsync(v1, key, cancellationToken).ConfigureAwait(false);
            if (!result.Exists)
            {
                DiagnosticLog.Warn(Category, $"{v1} did not answer /v1/models ({result.Detail}); using it anyway because it was configured.");
            }

            return Endpoint(LlmServer.From(v1, result), key, configuredModel, configured: true);
        }

        string apiKey = LlmEndpoint.KeyOf(effective);
        var servers = await DiscoverAllAsync(apiKey, extra: null, LlmScanMode.Resolve(effective), cancellationToken).ConfigureAwait(false);
        return servers.Count == 0 ? null : Endpoint(servers[0], apiKey, configuredModel, configured: false);
    }

    /// <summary>
    /// Chat-capable ids from an OpenAI <c>/v1/models</c> payload, in server order, distinct.
    /// Embedding models are skipped: they appear in the same list, cannot serve a chat completion,
    /// and picking one trades a clear "model not found" for a baffling failure mid-turn.
    /// <see cref="JsonDocument"/> parsing is not reflection-based, so this needs no serializer context.
    /// </summary>
    public static IReadOnlyList<string> ParseChatModelIds(string modelsJson) => ParseModels(modelsJson).Ids;

    /// <summary>
    /// <see cref="ParseChatModelIds"/> plus the first non-blank <c>owned_by</c> in the list — read
    /// from every entry, embeddings included, because it names the server, not the model.
    /// </summary>
    public static (IReadOnlyList<string> Ids, string? OwnedBy) ParseModels(string modelsJson)
    {
        var ids = new List<string>();
        string? ownedBy = null;
        if (string.IsNullOrWhiteSpace(modelsJson))
        {
            return (ids, ownedBy);
        }

        try
        {
            using var document = JsonDocument.Parse(modelsJson);
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return (ids, ownedBy);
            }

            foreach (var entry in data.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (ownedBy is null && entry.TryGetProperty("owned_by", out var owner) && owner.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(owner.GetString()))
                {
                    ownedBy = owner.GetString()!.Trim();
                }

                if (!entry.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var value = id.GetString();
                if (string.IsNullOrWhiteSpace(value)) continue;
                if (value.Contains("embed", StringComparison.OrdinalIgnoreCase)) continue;
                if (ids.Contains(value, StringComparer.Ordinal)) continue;
                ids.Add(value);
            }
        }
        catch (JsonException)
        {
            // A server that answers 200 with a non-JSON body still exists; it just listed nothing.
        }

        return (ids, ownedBy);
    }

    private static string FirstOrFallback(IReadOnlyList<string> ids) =>
        ids.Count > 0 ? ids[0] : LlmEndpoint.FallbackModelId;

    private static string NormalizeKey(string? apiKey) =>
        string.IsNullOrWhiteSpace(apiKey) ? LlmEndpoint.DefaultApiKey : apiKey.Trim();
}
