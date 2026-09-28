using Microsoft.Extensions.AI;
using NeonSidekick.Diagnostics;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Anthropic;
using NeonSidekick.Settings;
using NeonSidekick.Skills;

namespace NeonSidekick.App;

/// <summary>
/// The endpoint, chat client and <see cref="Llm.Assistant"/> for one process, and the one
/// <see cref="ConversationHistory"/> that outlives them: changing the URL or model reconnects,
/// the conversation stays. Shared by the headless REPL and the chat screen so the two modes
/// cannot drift in how they find a server.
/// </summary>
internal sealed class LlmSession : IDisposable
{
    private const string Category = "App";

    /// <summary>
    /// Printed once when discovery under <paramref name="scope"/> found nothing — or, under
    /// <see cref="ScanScope.Disabled"/>, when nothing was looked for: that line stands alone in
    /// headless, so it names every way out itself. Pinned by tests.
    /// </summary>
    public static string NoServerLine(ScanScope scope) => scope switch
    {
        ScanScope.Disabled => $"LLM: no URL is set and LLM scan mode is disabled; set {EnvironmentOverrides.LlmUrlVariable}, or the URL or the scan mode in /settings.",
        ScanScope.Remote => $"LLM: no server found on the local network (ports {LlmEndpointProbe.CandidatePortList}); set {EnvironmentOverrides.LlmUrlVariable}.",
        ScanScope.Both => $"LLM: no server found on 127.0.0.1 or the local network (ports {LlmEndpointProbe.CandidatePortList}); set {EnvironmentOverrides.LlmUrlVariable}.",
        _ => $"LLM: no server found on 127.0.0.1 ports {LlmEndpointProbe.CandidatePortList}; set {EnvironmentOverrides.LlmUrlVariable}.",
    };

    private readonly LlmEndpointProbe _probe;
    private readonly ContextLengthProbe _contextProbe;
    private readonly ServerSamplingProbe? _samplingProbe;
    private (string Key, ServerSampling? Found)? _serverSampling;
    private readonly Func<LlmEndpoint, LlmTimeouts, IChatClient> _factory;
    private readonly TimeProvider _time;
    private IChatClient? _client;
    private AppSettingsData _effective = new();
    private string _apiKey = LlmEndpoint.DefaultApiKey;
    private string _configuredUrl = "";
    private int _configuredContextLength;
    private ContextLength? _detectedContextLength;
    private CancellationTokenSource? _learningCts;
    private CancellationTokenSource? _titlingCts;

    /// <param name="contextProbe">Asks the connected server for the loaded model's context window after each connect.</param>
    /// <param name="time">The clock behind the assistant's turn deadline and its usage timings; tests pass a manual one.</param>
    /// <param name="samplingProbe">Asks the connected server for its sampling defaults when the <c>/sampling</c> pane wants them (<see cref="ServerSamplingAsync"/>); null asks nothing.</param>
    public LlmSession(LlmEndpointProbe probe, ContextLengthProbe contextProbe, Func<LlmEndpoint, LlmTimeouts, IChatClient> factory, TimeProvider? time = null, ServerSamplingProbe? samplingProbe = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _contextProbe = contextProbe ?? throw new ArgumentNullException(nameof(contextProbe));
        _samplingProbe = samplingProbe;
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _time = time ?? TimeProvider.System;
        Meter = new StreamMeter(_time);
    }

    /// <summary>The main assistant's request as it streams (2026-09-25): the busy row's <c>estimate</c> (<see cref="StreamMeter"/>); a botchat bot's assistant has none.</summary>
    public StreamMeter Meter { get; }

    /// <summary>The conversation; survives <see cref="ConnectAsync"/>.</summary>
    public ConversationHistory History { get; } = new(Llm.Assistant.DefaultSystemPrompt);

    /// <summary>The token tally; survives a reconnect as the conversation does, and its session scope outlives <c>/clear</c>.</summary>
    public TokenTally Usage { get; } = new();

    /// <summary>Where the last connect landed, even when the server did not answer; null when nothing was found.</summary>
    public LlmEndpoint? Endpoint { get; private set; }

    /// <summary>Non-null when a chat client exists.</summary>
    public Assistant? Assistant { get; private set; }

    public LlmTimeouts Timeouts { get; private set; } = LlmTimeouts.Default;

    /// <summary>
    /// The loaded model's context window: the settings' figure when they name one
    /// (<see cref="AppSettingsData.LlmContextLength"/>, no request made), else what the server's model
    /// list published (<see cref="LlmEndpoint.PublishedContextLength"/>) or the last connect's
    /// <see cref="ContextLengthProbe"/> found; null while unknown or disconnected.
    /// </summary>
    public ContextLength? ContextLength =>
        _configuredContextLength > 0 ? Llm.ContextLength.Configured(_configuredContextLength) : _detectedContextLength;

    /// <summary>The status line for a resolved endpoint.</summary>
    public static string ConnectedLine(LlmEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return $"LLM: {endpoint.BaseUrl} model={endpoint.ModelId} ({endpoint.Source})";
    }

    /// <summary>
    /// Drops the current client, resolves the endpoint for <paramref name="effective"/> (the
    /// configured URL, or the first local port that answers) and builds a new client and
    /// assistant. Returns true when an assistant is ready. A factory failure is logged as an
    /// error and leaves <see cref="Endpoint"/> set with no <see cref="Assistant"/>.
    /// </summary>
    public async Task<bool> ConnectAsync(AppSettingsData effective, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(effective);
        Reconnecting();
        Remember(effective);

        Endpoint = await _probe.ResolveAsync(effective, cancellationToken).ConfigureAwait(false);
        return Endpoint is not null && await ConnectAsync(effective, Endpoint, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <see cref="Connect"/> over an endpoint already resolved, then — when neither the settings
    /// nor the server's model list named the window — the context probe's native tiers. What the
    /// screen's server pick and the session's own <see cref="ConnectAsync(AppSettingsData, CancellationToken)"/> both end in.
    /// </summary>
    public async Task<bool> ConnectAsync(AppSettingsData effective, LlmEndpoint endpoint, CancellationToken cancellationToken)
    {
        if (!Connect(effective, endpoint))
        {
            return false;
        }

        DiagnosticLog.Info(Category, ConnectedLogLine(endpoint));
        if (Assistant?.Sampling is { IsEmpty: false } sampling)
        {
            DiagnosticLog.Info(Category, SamplingLogLine(sampling));
        }

        // The Claude API publishes its window on the model list or nowhere: none of the native tiers live on its host.
        if (_configuredContextLength <= 0 && _detectedContextLength is null && !ClaudeApi.IsClaudeApi(endpoint.BaseUrl))
        {
            _detectedContextLength = await _contextProbe.DetectAsync(endpoint.BaseUrl, endpoint.ModelId, _apiKey, cancellationToken).ConfigureAwait(false);
        }

        DiagnosticLog.Info(Category, ContextLength is { } window
            ? $"Context window: {window.Tokens.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} tokens ({window.Source})."
            : "Context window: unknown; /usage shows no percentage.");
        return true;
    }

    /// <summary>
    /// The discovery half of a connect for a blank URL, for a screen that lets the user choose:
    /// drops the current client and returns every server that answered under the settings' scan
    /// mode, in list order (<see cref="LlmEndpointProbe.DiscoverAllAsync"/>). <see cref="Endpoint"/>
    /// is null until <see cref="Connect"/> lands one.
    /// </summary>
    public Task<IReadOnlyList<LlmServer>> DiscoverAsync(AppSettingsData effective, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(effective);
        Reconnecting();
        Endpoint = null;
        Remember(effective);
        return WithClaudeApiAsync(effective, _probe.DiscoverAllAsync(effective.LlmApiKey, extra: null, LlmScanMode.Resolve(effective), cancellationToken), cancellationToken);
    }

    /// <summary>
    /// <c>/server</c>'s look-around: the servers that answer under the settings' scan mode plus
    /// <paramref name="extra"/> (the endpoint in use, when it is on a port the list does not name).
    /// The session stays as it is until a pick lands.
    /// </summary>
    public Task<IReadOnlyList<LlmServer>> ProbeServersAsync(AppSettingsData effective, Uri? extra, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return WithClaudeApiAsync(effective, _probe.DiscoverAllAsync(effective.LlmApiKey, extra, LlmScanMode.Resolve(effective), cancellationToken), cancellationToken);
    }

    /// <summary>
    /// The scan's servers and, while the Claude API is offered (<see cref="ClaudeApi.Offered"/>, 2026-09-27), its row
    /// last — asked for its model list with its own key alongside the scan, never scanned for, and listed whatever it
    /// answered (the row's detail then says why), so the switch and the key are all it takes to see it. The scan mode
    /// does not govern it: <c>disabled</c> still lists it.
    /// </summary>
    private async Task<IReadOnlyList<LlmServer>> WithClaudeApiAsync(AppSettingsData effective, Task<IReadOnlyList<LlmServer>> scan, CancellationToken cancellationToken)
    {
        if (!ClaudeApi.Offered(effective))
        {
            return await scan.ConfigureAwait(false);
        }

        var claude = _probe.ProbeAsync(ClaudeApi.BaseUrl, ClaudeApi.Key(effective), cancellationToken);
        var servers = new List<LlmServer>(await scan.ConfigureAwait(false));
        var result = await claude.ConfigureAwait(false);
        DiagnosticLog.Info(Category, $"{ClaudeApi.ServerName}: {result.Detail}.");
        servers.Add(LlmServer.From(ClaudeApi.BaseUrl, result));
        return servers;
    }

    /// <summary>One server asked by URL (<c>/server &lt;url&gt;</c>); the result says whether it answered.</summary>
    public async Task<LlmServer> ProbeServerAsync(Uri baseUrl, AppSettingsData effective, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(effective);
        var v1 = LlmEndpoint.NormalizeBaseUrl(baseUrl);
        var result = await _probe.ProbeAsync(v1, ClaudeApi.KeyFor(effective, v1), cancellationToken).ConfigureAwait(false);
        return LlmServer.From(v1, result);
    }

    /// <summary>
    /// The client half: builds the client and assistant over an endpoint already resolved (a
    /// picked or the first discovered server, through <see cref="LlmEndpointProbe.Endpoint"/>).
    /// Returns true when an assistant is ready; a factory failure is logged and leaves
    /// <see cref="Endpoint"/> set with no <see cref="Assistant"/>.
    /// </summary>
    public bool Connect(AppSettingsData effective, LlmEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(effective);
        ArgumentNullException.ThrowIfNull(endpoint);
        Reconnecting();
        Remember(effective);
        Endpoint = endpoint;
        _detectedContextLength = endpoint.PublishedContextLength;

        try
        {
            _client = _factory(Endpoint, Timeouts);
            Assistant = new Assistant(_client, History, Timeouts, time: _time, reasoning: ReasoningLevel.Resolve(effective)) { Meter = Meter, Sampling = LlmSampling.Resolve(effective, endpoint.ModelId) };
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Error(Category, "Could not create the chat client: " + Llm.Assistant.Explain(ex));
            Disconnect();
            return false;
        }
    }

    /// <summary>
    /// A second assistant over this session's one client (<c>/botchat</c>, 2026-09-24, the user's call:
    /// every bot on the starting profile's server and model — <c>Botchat LLM mode</c> <c>single</c> since 2026-09-25):
    /// <paramref name="history"/> its own, the timeouts and the reasoning effort the main assistant's as they stand, no
    /// tools. Null while nothing is connected. The client stays this session's; nothing here is disposed with the assistant.
    /// </summary>
    public Assistant? CreateAssistant(ConversationHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);
        return _client is { } client && Assistant is { } main
            ? new Assistant(client, history, Timeouts, time: _time, reasoning: main.Reasoning) { Sampling = main.Sampling }
            : null;
    }

    /// <summary>An assistant over a bot's own <paramref name="link"/> (<see cref="LinkAsync"/>): its client, timeouts, reasoning effort and sampling, no tools.</summary>
    public Assistant CreateAssistant(ConversationHistory history, BotLink link)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(link);
        return new Assistant(link.Client, history, link.Timeouts, time: _time, reasoning: link.Reasoning) { Sampling = link.Sampling };
    }

    /// <summary>
    /// A <c>/botchat</c> bot's own LLM (2026-09-25, the user's ask: <c>Botchat LLM mode</c> <c>multi</c>) from its saved
    /// <paramref name="profile"/>: its URL, model, API key, timeouts and reasoning effort. A blank URL borrows the server
    /// this session is connected to (the user's call: no scan per bot) — with the profile's model, or this session's when
    /// that is blank too, and this session's API key while the profile keeps the default one — and asks it nothing. A URL
    /// of its own is asked for its model list once; one that does not answer (or is no URL) is a problem, and the bot sits
    /// the chat out (the user's call). Its model is the profile's, else the first its server lists. The caller disposes the link.
    /// </summary>
    public async Task<(BotLink? Link, string? Problem)> LinkAsync(AppSettingsData profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        string? model = string.IsNullOrWhiteSpace(profile.LlmModel) ? null : profile.LlmModel.Trim();
        LlmEndpoint endpoint;
        if (string.IsNullOrWhiteSpace(profile.LlmUrl))
        {
            if (Endpoint is not { } starter)
            {
                return (null, BotLinkNoServer);
            }

            // The Claude API's key goes with its endpoint (2026-09-27): a borrowed Claude API is borrowed with it.
            string key = ClaudeApi.IsClaudeApi(starter.BaseUrl) ? starter.ApiKey
                : string.IsNullOrWhiteSpace(profile.LlmApiKey) || profile.LlmApiKey == LlmEndpoint.DefaultApiKey ? _apiKey : profile.LlmApiKey;
            endpoint = starter with { ModelId = model ?? starter.ModelId, ApiKey = key, PublishedContextLength = null };
        }
        else
        {
            Uri v1;
            try
            {
                v1 = LlmEndpoint.NormalizeBaseUrl(profile.LlmUrl);
            }
            catch (Exception ex) when (ex is ArgumentException or UriFormatException)
            {
                return (null, ex.Message);
            }

            string key = ClaudeApi.KeyFor(profile, v1);
            var result = await _probe.ProbeAsync(v1, key, cancellationToken).ConfigureAwait(false);
            if (!result.Exists)
            {
                return (null, $"{v1} did not answer ({result.Detail})");
            }

            endpoint = LlmEndpointProbe.Endpoint(LlmServer.From(v1, result), key, model, configured: true);
        }

        var timeouts = LlmTimeouts.Resolve(profile);
        try
        {
            return (new BotLink(_factory(endpoint, timeouts), endpoint, timeouts, ReasoningLevel.Resolve(profile), LlmSampling.Resolve(profile, endpoint.ModelId)), null);
        }
        catch (Exception ex)
        {
            return (null, "could not create the chat client: " + Llm.Assistant.Explain(ex));
        }
    }

    /// <summary><see cref="LinkAsync"/>'s problem for a borrowed server while this session has none. Pinned.</summary>
    public const string BotLinkNoServer = "no LLM URL of its own and no server connected to borrow";

    /// <summary>What every later call needs from the settings a connect was made with.</summary>
    private void Remember(AppSettingsData effective)
    {
        Timeouts = LlmTimeouts.Resolve(effective);
        _effective = effective;
        _apiKey = string.IsNullOrWhiteSpace(effective.LlmApiKey) ? LlmEndpoint.DefaultApiKey : effective.LlmApiKey;
        _configuredUrl = effective.LlmUrl ?? "";
        _configuredContextLength = effective.LlmContextLength;
    }

    /// <summary>
    /// Asks the current endpoint (or the configured URL when nothing is connected) for its model
    /// list. Null when there is no URL to ask.
    /// </summary>
    public Task<ProbeResult?> ListModelsAsync(CancellationToken cancellationToken)
    {
        Uri? url = Endpoint?.BaseUrl;
        if (url is null && !string.IsNullOrWhiteSpace(_configuredUrl))
        {
            try
            {
                url = LlmEndpoint.NormalizeBaseUrl(_configuredUrl);
            }
            catch (Exception ex) when (ex is ArgumentException or UriFormatException)
            {
                url = null;
            }
        }

        return url is null ? Task.FromResult<ProbeResult?>(null) : ProbeAsync(url, cancellationToken);
    }

    private async Task<ProbeResult?> ProbeAsync(Uri url, CancellationToken cancellationToken) =>
        await _probe.ProbeAsync(url, ClaudeApi.IsClaudeApi(url) ? ClaudeApi.Key(_effective) : _apiKey, cancellationToken).ConfigureAwait(false);

    /// <summary>The skill-learning reflection now running, if one is (<see cref="StartLearning"/>); the screen never awaits it here.</summary>
    public Task<SkillLearnResult>? Learning { get; private set; }

    /// <summary>Whether a reflection is still running: one at a time, the next qualifying turn skips.</summary>
    public bool IsLearning => Learning is { IsCompleted: false };

    /// <summary>
    /// Starts <paramref name="job"/> over the current assistant in the background, under a token
    /// linked to <paramref name="appToken"/> that <see cref="Disconnect"/> (every reconnect, the
    /// dispose) cancels first — so a request never races the client's disposal. Null with no
    /// assistant or while one still runs (<see cref="IsLearning"/>). The task never faults:
    /// <see cref="SkillLearner.RunAsync"/> answers <see cref="SkillLearnOutcome.Cancelled"/> or
    /// <see cref="SkillLearnOutcome.Failed"/> instead.
    /// </summary>
    public Task<SkillLearnResult>? StartLearning(Func<Assistant, CancellationToken, Task<SkillLearnResult>> job, CancellationToken appToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (Assistant is not { } assistant || IsLearning)
        {
            return null;
        }

        _learningCts?.Dispose();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(appToken);
        _learningCts = cts;
        Learning = Task.Run(() => job(assistant, cts.Token), CancellationToken.None);
        return Learning;
    }

    /// <summary>Cancels the running reflection, if any, without waiting for it: the request aborts and the job answers cancelled.</summary>
    public void CancelLearning()
    {
        if (_learningCts is { } cts && IsLearning)
        {
            VoiceSession.SafeCancel(cts);
        }
    }

    /// <summary>The session-title request now running, if one is (<see cref="StartTitling"/>).</summary>
    public Task? Titling { get; private set; }

    /// <summary>Whether a title request is still running: one at a time, its own slot beside the reflection's.</summary>
    public bool IsTitling => Titling is { IsCompleted: false };

    /// <summary>
    /// <see cref="StartLearning"/>'s shape for the model-written session title (2026-09-18): the
    /// job runs over the current assistant under a token <see cref="Disconnect"/> cancels first,
    /// in its own slot so a reflection never blocks it or is blocked by it. Null with no assistant
    /// or while one still runs. The job must never fault: it writes the store or leaves the first
    /// line standing.
    /// </summary>
    public Task? StartTitling(Func<Assistant, CancellationToken, Task> job, CancellationToken appToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (Assistant is not { } assistant || IsTitling)
        {
            return null;
        }

        _titlingCts?.Dispose();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(appToken);
        _titlingCts = cts;
        Titling = Task.Run(() => job(assistant, cts.Token), CancellationToken.None);
        return Titling;
    }

    private void CancelTitling()
    {
        if (_titlingCts is { } cts && IsTitling)
        {
            VoiceSession.SafeCancel(cts);
        }
    }

    /// <summary>
    /// A connect or a discovery drops the client it had: logged, then <see cref="Disconnect"/>. The
    /// exit's dispose drops one too, silently — the run's closing lines are the loop's, and headless
    /// has its console echo back by then.
    /// </summary>
    private void Reconnecting()
    {
        if (_client is not null && Endpoint is { } previous)
        {
            DiagnosticLog.Debug(Category, DisconnectedLogLine(previous));
        }

        Disconnect();
    }

    private void Disconnect()
    {
        CancelLearning();
        CancelTitling();
        Assistant = null;
        _client?.Dispose();
        _client = null;
        _detectedContextLength = null;
        _serverSampling = null;
    }

    /// <summary>
    /// The connected model's sampling defaults as the server reports them (2026-09-28, for the <c>/sampling</c> pane's
    /// <c>(server)</c> values): asked once per endpoint, model and <c>LLM sampling from Hugging Face</c> value and kept
    /// until a reconnect (<see cref="ServerSamplingProbe"/>) — so a flip of the setting asks again at the next pane. Null
    /// while nothing is connected, over the Claude API (no sampling goes there), with no probe, or when the server said
    /// nothing.
    /// </summary>
    public async Task<ServerSampling?> ServerSamplingAsync(AppSettingsData effective, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (_samplingProbe is null || Endpoint is not { } endpoint || ClaudeApi.IsClaudeApi(endpoint.BaseUrl))
        {
            return null;
        }

        string key = string.Join('|', endpoint.BaseUrl.AbsoluteUri, endpoint.ModelId, effective.LlmSamplingFromHuggingFace ? "hf" : "");
        if (_serverSampling is { } cached && cached.Key == key)
        {
            return cached.Found;
        }

        var found = await _samplingProbe.DetectAsync(endpoint.BaseUrl, endpoint.ModelId, _apiKey, effective.LlmSamplingFromHuggingFace, cancellationToken).ConfigureAwait(false);
        if (!cancellationToken.IsCancellationRequested)
        {
            _serverSampling = (key, found);
        }

        return found;
    }

    /// <summary>The connect's line in the log: <c>Connected: LLM: http://… model=… (probed)</c> — <see cref="ConnectedLine"/> after the word. Pinned.</summary>
    public static string ConnectedLogLine(LlmEndpoint endpoint) => "Connected: " + ConnectedLine(endpoint);

    /// <summary>The log line after <see cref="ConnectedLogLine"/> when the connected model has sampling set (2026-09-28): <c>Sampling: temperature 0.6 · top_k 20</c>.</summary>
    public static string SamplingLogLine(LlmSampling sampling)
    {
        ArgumentNullException.ThrowIfNull(sampling);
        return "Sampling: " + sampling.Describe();
    }

    /// <summary>The client a reconnect drops (a profile switch, <c>/server</c>, a switch that reconnects): <c>Disconnected from http://… model=…</c>; the exit's dispose says nothing. Pinned.</summary>
    public static string DisconnectedLogLine(LlmEndpoint endpoint) => $"Disconnected from {endpoint.BaseUrl} model={endpoint.ModelId}";

    public void Dispose()
    {
        Disconnect();
        _learningCts?.Dispose();
        _learningCts = null;
        _titlingCts?.Dispose();
        _titlingCts = null;
    }
}

/// <summary>
/// A <c>/botchat</c> bot's own LLM (<see cref="LlmSession.LinkAsync"/>, 2026-09-25): the client over its endpoint, and the
/// timeouts, reasoning effort and sampling of its profile (the sampling for the bot's own model, 2026-09-28). Owned by
/// the chat that made it, disposed when that chat ends.
/// </summary>
internal sealed class BotLink(IChatClient client, LlmEndpoint endpoint, LlmTimeouts timeouts, ReasoningEffort reasoning, LlmSampling? sampling = null) : IDisposable
{
    public IChatClient Client { get; } = client;

    public LlmEndpoint Endpoint { get; } = endpoint;

    public LlmTimeouts Timeouts { get; } = timeouts;

    public ReasoningEffort Reasoning { get; } = reasoning;

    public LlmSampling? Sampling { get; } = sampling;

    public void Dispose() => Client.Dispose();
}
