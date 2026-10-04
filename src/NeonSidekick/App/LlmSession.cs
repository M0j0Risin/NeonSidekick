using Microsoft.Extensions.AI;
using NeonSidekick.Claude;
using NeonSidekick.Diagnostics;
using NeonSidekick.Docker;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Anthropic;
using NeonSidekick.Llm.OpenAIPlatform;
using NeonSidekick.EmbeddedLlm;
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
    /// The screen's line when discovery under <paramref name="scope"/> found nothing — or, under
    /// <see cref="ScanScope.Disabled"/>, when nothing was looked for. One line, what happened and then the ways out, with no
    /// hint under it (2026-09-30, the user's call: the line and a hint beneath it said the same thing twice, and named an
    /// environment variable a TUI user never sets). The ports stay: they say why a server elsewhere was not found. Headless
    /// has its own (<see cref="HeadlessNoServerLine"/>). Pinned by tests.
    /// </summary>
    public static string NoServerLine(ScanScope scope) => scope switch
    {
        ScanScope.Disabled => "LLM: no server set. Set LLM URL in /settings (or /server <url>), or turn on LLM server scan mode.",
        ScanScope.Remote => $"LLM: no server found on the local network (ports {LlmEndpointProbe.CandidatePortList}). Start one, or set LLM URL in /settings.",
        ScanScope.Both => $"LLM: no server found on this machine or the local network (ports {LlmEndpointProbe.CandidatePortList}). Start one, or set LLM URL in /settings.",
        _ => $"LLM: no server found on this machine (ports {LlmEndpointProbe.CandidatePortList}). Start one, or set LLM URL in /settings.",
    };

    /// <summary>
    /// <see cref="NoServerLine"/> for a headless run (2026-09-30): the same shape, the ways out a scripted run has — <c>--url</c>
    /// and <see cref="EnvironmentOverrides.LlmUrlVariable"/>; there is no <c>/settings</c> to open. Pinned by tests.
    /// </summary>
    public static string HeadlessNoServerLine(ScanScope scope) => scope switch
    {
        ScanScope.Disabled => $"LLM: no server set. Pass --url or set {EnvironmentOverrides.LlmUrlVariable}, or turn on LLM server scan mode in the profile.",
        ScanScope.Remote => $"LLM: no server found on the local network (ports {LlmEndpointProbe.CandidatePortList}). Pass --url or set {EnvironmentOverrides.LlmUrlVariable}.",
        ScanScope.Both => $"LLM: no server found on this machine or the local network (ports {LlmEndpointProbe.CandidatePortList}). Pass --url or set {EnvironmentOverrides.LlmUrlVariable}.",
        _ => $"LLM: no server found on this machine (ports {LlmEndpointProbe.CandidatePortList}). Pass --url or set {EnvironmentOverrides.LlmUrlVariable}.",
    };

    /// <summary>
    /// The screen's line when its server picker listed the installed embedded models alone and was closed with none picked
    /// (2026-09-30, the user's report: ESC there read "no URL is set and LLM server scan mode is disabled", which named
    /// neither the cause nor the models on offer, and a hint under it said the same again). One line, no hint. Pinned.
    /// </summary>
    public const string NoServerPickedLine =
        "LLM: no server picked; /server lists the installed embedded models again, or set LLM URL or LLM server scan mode in /settings.";

    /// <summary>
    /// <see cref="NoServerLine"/> for <see cref="ScanScope.Disabled"/> when the embedded models are offered and none is downloaded
    /// (2026-09-30): the embedded catalog is the third way out. The screen's alone; headless keeps its own line. One line, no hint
    /// (later that day, the user's call). Pinned.
    /// </summary>
    public const string NoEmbeddedLine =
        "LLM: no server set and no embedded model downloaded. Download one in /settings › Embedded, set LLM URL, or turn on LLM server scan mode.";

    private readonly LlmEndpointProbe _probe;
    private readonly ContextLengthProbe _contextProbe;
    private readonly ServerSamplingProbe? _samplingProbe;
    private (string Key, ServerSampling? Found)? _serverSampling;
    private readonly Func<LlmEndpoint, LlmTimeouts, IChatClient> _factory;
    private readonly TimeProvider _time;
    private readonly IEmbeddedLlm? _embedded;
    private readonly IClaudeServerHost? _claudeServer;
    private readonly Func<AppSettingsData, bool> _claudeCliOffered;
    private readonly IDockerServers? _docker;

    // The chosen Docker container this session last switched to (2026-10-02): set before the switch, since a failed one may still
    // have started it; cleared when another server is picked and the containers are stopped.
    private string? _dockerInUse;

    // The settings that container was switched to under (2026-10-02, the user's ask): a profile switch leaves it by the old
    // profile's chosen list, stop timeout and post-stop delay — the new profile's list may not name it at all, and a stop by
    // that list stopped nothing while the next model loaded beside it.
    private AppSettingsData? _dockerSettings;
    private IChatClient? _client;
    private AppSettingsData _effective = new();
    private string _apiKey = LlmEndpoint.DefaultApiKey;
    private string _configuredUrl = "";
    private int _configuredContextLength;

    // The loaded profile's Embedded servers enabled at the last connect (2026-09-29): a bot's embedded link honours it.
    private bool _embeddedEnabled = true;

    // One embedded link at a time (later on 2026-09-29): the bots link in parallel, and each once saw no server running and
    // started its own model, stopping the one before — the earlier bots left on a dead port.
    private readonly SemaphoreSlim _embeddedLinks = new(1, 1);
    private ContextLength? _detectedContextLength;
    private CancellationTokenSource? _learningCts;
    private CancellationTokenSource? _titlingCts;

    /// <param name="contextProbe">Asks the connected server for the loaded model's context window after each connect.</param>
    /// <param name="time">The clock behind the assistant's turn deadline and its usage timings; tests pass a manual one.</param>
    /// <param name="samplingProbe">Asks the connected server for its sampling defaults when the <c>/sampling</c> pane wants them (<see cref="ServerSamplingAsync"/>); null asks nothing.</param>
    /// <param name="embedded">The embedded model (2026-09-29): its catalog's <c>/server</c> rows and the server an embedded URL starts; null offers none.</param>
    /// <param name="claudeServer">The Claude CLI server's process (2026-09-30): stopped when another server is picked; null has none to stop.</param>
    /// <param name="claudeCliOffered">Whether the Claude CLI is offered for the settings (<see cref="ClaudeCliEndpoint.Offered"/>, which looks for the CLI); null offers it never.</param>
    /// <param name="dockerServers">The chosen Docker containers' switcher (2026-10-02): their <c>/server</c> rows and the switch a Docker URL makes; null offers none.</param>
    public LlmSession(LlmEndpointProbe probe, ContextLengthProbe contextProbe, Func<LlmEndpoint, LlmTimeouts, IChatClient> factory, TimeProvider? time = null, ServerSamplingProbe? samplingProbe = null, IEmbeddedLlm? embedded = null,
        IClaudeServerHost? claudeServer = null, Func<AppSettingsData, bool>? claudeCliOffered = null, IDockerServers? dockerServers = null)
    {
        _embedded = embedded;
        _docker = dockerServers;
        _claudeServer = claudeServer;
        _claudeCliOffered = claudeCliOffered ?? (_ => false);
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

    /// <summary>The embedded model's catalog, installs and server (2026-09-29); null when this session offers none.</summary>
    public IEmbeddedLlm? Embedded => _embedded;

    /// <summary>The chosen Docker containers' switcher (2026-10-02); null when this session offers none.</summary>
    public IDockerServers? DockerServers => _docker;

    /// <summary>Whether Docker servers are offered for <paramref name="effective"/>: a switcher here, and <see cref="DockerEndpoint.Offered"/>.</summary>
    public bool DockerOffered(AppSettingsData effective) => _docker is not null && DockerEndpoint.Offered(effective);

    /// <summary>The embedded server the current endpoint runs on (2026-09-29); null for any other endpoint or when it did not start.</summary>
    public EmbeddedServerInfo? EmbeddedServer { get; private set; }

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
    public Task<bool> ConnectAsync(AppSettingsData effective, CancellationToken cancellationToken) =>
        ConnectAsync(effective, phase: null, cancellationToken);

    /// <summary>
    /// <see cref="ConnectAsync(AppSettingsData, CancellationToken)"/> with a word for the spinner (2026-09-29): an embedded
    /// URL starts the embedded server (<see cref="ResolveEmbeddedAsync"/>) — the llama.cpp runtime downloaded first when
    /// missing, then the model loaded — and <paramref name="phase"/> is told each step. Any other URL stops an embedded
    /// server this session had running, so its memory is free for whatever runs next.
    /// </summary>
    public async Task<bool> ConnectAsync(AppSettingsData effective, Action<string>? phase, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(effective);
        Reconnecting();
        Remember(effective);
        DockerServedModel = null;

        // Another server than a chosen container (2026-10-02, the user's call): the running one stops first, before an embedded
        // model loads, so the GPU never holds the two — and the GPU's memory is given the post-stop delay before that load.
        bool docker = _docker is not null && DockerEndpoint.Chosen(effective);
        if (!docker)
        {
            await LeaveDockerAsync(effective, except: null, settle: EmbeddedEndpoint.Chosen(effective), phase, cancellationToken).ConfigureAwait(false);
        }
        else if (_dockerInUse is { } inUse && DockerEndpoint.ContainerOf(effective.LlmUrl) is { } target
            && !string.Equals(inUse, target, StringComparison.Ordinal) && !DockerEndpoint.ChosenNames(effective).Contains(inUse, StringComparer.Ordinal))
        {
            // A profile switch to another container (2026-10-02, the user's ask): the new profile's switch stops only the ones
            // its own list names, so the old profile's container, not among them, stops here first. One both lists name is the
            // switch's to stop, as within one profile.
            await LeaveDockerAsync(effective, except: target, settle: true, phase, cancellationToken).ConfigureAwait(false);
        }

        if (EmbeddedEndpoint.Chosen(effective))
        {
            Endpoint = await ResolveEmbeddedAsync(effective, phase, cancellationToken).ConfigureAwait(false);
            return Endpoint is { LiveUrl: not null } && await ConnectAsync(effective, Endpoint, cancellationToken).ConfigureAwait(false);
        }

        EmbeddedServer = null;
        if (_embedded?.Running is not null)
        {
            _embedded.Stop();
        }

        if (!effective.EmbeddedLlmServer)
        {
            _embedded?.StopExtras();   // the switch off (later on 2026-09-29): a multi-server botchat's extras go too
        }

        if (docker)
        {
            // A chosen container (2026-10-02): the others stopped, it started, its API waited for — after the embedded server let go.
            Endpoint = await ResolveDockerAsync(effective, phase, cancellationToken).ConfigureAwait(false);
            return Endpoint is { LiveUrl: not null } && await ConnectAsync(effective, Endpoint, cancellationToken).ConfigureAwait(false);
        }

        if (DockerEndpoint.IsDocker(effective.LlmUrl))
        {
            // Saved while the container was a Docker server; switched off or unticked, the URL stands for nothing, as the Claude CLI's.
            DiagnosticLog.Warn(Category, DockerServerText.SwitchedOffWarning);
            var blank = AppSettings.Copy(effective);
            blank.LlmUrl = "";
            blank.LlmModel = "";
            effective = blank;
        }

        if (ClaudeCliEndpoint.IsClaudeCli(effective.LlmUrl))
        {
            if (_claudeCliOffered(effective))
            {
                // The Claude CLI (2026-09-30) is asked nothing: its process starts at the first turn, with that turn's prompt and tools.
                Endpoint = ClaudeCliEndpointOf(effective.LlmModel);
                return await ConnectAsync(effective, Endpoint, cancellationToken).ConfigureAwait(false);
            }

            // Saved while it was offered; switched off (or the CLI gone) the URL stands for nothing, as the Anthropic API's.
            DiagnosticLog.Warn(Category, ClaudeCliText.NotOfferedWarning);
            var blank = AppSettings.Copy(effective);
            blank.LlmUrl = "";
            blank.LlmModel = "";
            effective = blank;
        }

        Endpoint = await _probe.ResolveAsync(effective, cancellationToken).ConfigureAwait(false);
        return Endpoint is not null && await ConnectAsync(effective, Endpoint, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The embedded model's endpoint (2026-09-29): the catalog model <c>LLM model</c> names, else the first installed one,
    /// started on the embedded server. The endpoint's base is the sentinel (<see cref="EmbeddedEndpoint.BaseUrl"/>, what the
    /// user sees), its <see cref="LlmEndpoint.LiveUrl"/> the loopback port it listens on and its key the start's own.
    /// A start that fails is logged as an error and leaves an endpoint with no live URL — the screen then knows which
    /// model was meant, and no client is built. Null when there is no embedded model to start at all.
    /// </summary>
    private async Task<LlmEndpoint?> ResolveEmbeddedAsync(AppSettingsData effective, Action<string>? phase, CancellationToken cancellationToken)
    {
        EmbeddedServer = null;
        if (_embedded is null || !EmbeddedEndpoint.Offered)
        {
            DiagnosticLog.Error(Category, EmbeddedUnavailable);
            return null;
        }

        var model = EmbeddedModelFor(effective.LlmModel);
        if (model is null)
        {
            string id = (effective.LlmModel ?? "").Trim();
            DiagnosticLog.Error(Category, id.Length == 0 ? EmbeddedLlmText.NoneInstalled
                : EmbeddedModelCatalog.Find(id, _embedded.Catalog) is { } known ? EmbeddedLlmText.NotInstalled(known)
                : EmbeddedLlmText.UnknownModel(id));
            return null;
        }

        // Another model about to load (2026-09-30, the user's ask): the old one's name leaves the hint row now, not when the new
        // one is up. The trailer reads Endpoint live, and the connect's spinner redraws it each frame. The same model, a
        // /reasoning pick's quiet reconnect, keeps it, since that start returns at once.
        if (_embedded.Running?.ModelId is not { } running || !string.Equals(running, model.Id, StringComparison.OrdinalIgnoreCase))
        {
            Endpoint = null;
        }

        try
        {
            var info = await _embedded.StartAsync(model, effective, phase, cancellationToken).ConfigureAwait(false);
            EmbeddedServer = info;
            return EmbeddedEndpointOf(info);
        }
        catch (EmbeddedLlmException ex)
        {
            // The kill switch's (2026-10-01) is the user's own act: said, never an error.
            DiagnosticLog.Write(ex.Killed ? DiagnosticLevel.Info : DiagnosticLevel.Error, Category, ex.Message);
            return new LlmEndpoint(EmbeddedEndpoint.BaseUrl, model.Id, LlmEndpoint.DefaultApiKey, EmbeddedNotRunningSource);
        }
    }

    /// <summary>
    /// A chosen container's endpoint (2026-10-02): every other chosen container stopped, this one started and waited for
    /// (<see cref="IDockerServers.SwitchToAsync"/>). The base is the sentinel, its <see cref="LlmEndpoint.LiveUrl"/> the
    /// published port that answered; the model is <c>LLM model</c> when the container lists it (or lists nothing), else the
    /// first it lists — a vLLM or SGLang container serves one — and the window is what the list published. A switch that
    /// fails is logged as an error and leaves an endpoint with no live URL, so no client is built.
    /// </summary>
    private async Task<LlmEndpoint> ResolveDockerAsync(AppSettingsData effective, Action<string>? phase, CancellationToken cancellationToken)
    {
        string name = DockerEndpoint.ContainerOf(effective.LlmUrl)!;
        string? saved = string.IsNullOrWhiteSpace(effective.LlmModel) ? null : effective.LlmModel.Trim();
        if (!string.Equals(_dockerInUse, name, StringComparison.Ordinal))
        {
            // Another container about to load: the old one's name leaves the hint row now, as the embedded model's does.
            Endpoint = null;
        }

        _dockerInUse = name;
        _dockerSettings = effective;
        var result = await _docker!.SwitchToAsync(name, effective, phase, cancellationToken).ConfigureAwait(false);
        if (!result.Ok)
        {
            DiagnosticLog.Error(Category, result.Error!);
            return new LlmEndpoint(DockerEndpoint.BaseUrl(name), saved ?? LlmEndpoint.FallbackModelId, _apiKey, DockerServerText.NotRunningSource);
        }

        var listed = result.Models.ModelIds;
        DockerServedModel = listed.Count == 1 ? listed[0] : null;
        string model = saved is not null && (listed.Count == 0 || listed.Contains(saved, StringComparer.Ordinal)) ? saved
            : listed.Count > 0 ? listed[0] : saved ?? LlmEndpoint.FallbackModelId;
        var window = result.Models.ModelsJson is { } json ? ContextLengthProbe.ParseModelsWindow(json, model) : null;
        return new LlmEndpoint(DockerEndpoint.BaseUrl(name), model, _apiKey, DockerServerText.Source(name, result.Port), window) { LiveUrl = result.LiveUrl };
    }

    /// <summary>
    /// Another server picked while a chosen container was in use (2026-10-02, the user's call): every running chosen container
    /// but <paramref name="except"/> stops, so the GPU is free for what comes next. The chosen ones are those of the settings the
    /// container was switched to under (later that day, the user's ask: a profile switch's new list may not name it), and so is
    /// the post-stop delay <paramref name="settle"/> waits when a model loads next. A container that would not stop is a
    /// warning; the connect goes on.
    /// </summary>
    private async Task LeaveDockerAsync(AppSettingsData effective, string? except, bool settle, Action<string>? phase, CancellationToken cancellationToken)
    {
        if (_docker is null || _dockerInUse is null)
        {
            return;
        }

        var left = await _docker.StopAllAsync(_dockerSettings ?? effective, except, settle, phase, cancellationToken).ConfigureAwait(false);
        _dockerInUse = null;
        _dockerSettings = null;
        if (left.Stopped.Count > 0)
        {
            DiagnosticLog.Info(Category, DockerServerText.Left(left.Stopped));
        }

        foreach (string error in left.Errors)
        {
            DiagnosticLog.Warn(Category, error);
        }
    }

    /// <summary>The chosen container this session switched to and has not left (2026-10-02); null for none.</summary>
    public string? DockerInUse => _dockerInUse;

    /// <summary>
    /// The one model the chosen container listed at the last connect (2026-10-03, the user's ask): the screen saves it as
    /// <c>LLM model</c>, so the setting names what the container serves rather than reading <c>(first listed)</c>. Null at
    /// the start of every connect, and after one to anything but a container that started and listed exactly one model (a
    /// failed switch, an empty list, several); the endpoint's own pick is unchanged by it.
    /// </summary>
    public string? DockerServedModel { get; private set; }

    /// <summary>
    /// The app's exit (2026-10-02): with <c>Docker server stop on exit</c> on and a chosen container in use, the running ones
    /// stop. The names stopped; empty when the setting is off (the container keeps running, the default) or none was in use.
    /// </summary>
    public async Task<IReadOnlyList<string>> StopDockerAtExitAsync(AppSettingsData effective, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (_docker is null || _dockerInUse is null || !effective.DockerServerStopOnExit)
        {
            return [];
        }

        // The chosen list it was switched to under: the profile now loaded may not name it (2026-10-02).
        var left = await _docker.StopAllAsync(_dockerSettings ?? effective, except: null, settle: false, null, cancellationToken).ConfigureAwait(false);
        _dockerInUse = null;
        _dockerSettings = null;
        foreach (string error in left.Errors)
        {
            DiagnosticLog.Warn(Category, error);
        }

        return left.Stopped;
    }

    /// <summary>
    /// The <c>/server</c> rows of the chosen containers (2026-10-02), in the chosen order, asked nothing but the engine's list:
    /// a stopped one is never probed. A name the engine no longer lists has a row too, its detail saying so. Pure.
    /// </summary>
    public static IReadOnlyList<LlmServer> DockerRows(DockerServerList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        return list.Chosen.Select(c => new LlmServer(DockerEndpoint.BaseUrl(c.Name), DockerEndpoint.ServerName, new ProbeResult(true, [], DockerServerText.RowDetail(c))))
            .Concat(list.Missing.Select(n => new LlmServer(DockerEndpoint.BaseUrl(n), DockerEndpoint.ServerName, new ProbeResult(false, [], DockerServerText.MissingDetail))))
            .ToList();
    }

    /// <summary>
    /// The scan's rows less the ones a running chosen container answers on (2026-10-02): its vLLM or SGLang port is found by the
    /// scan too, and picking it there would leave the one-at-a-time rule. Loopback rows only. Pure.
    /// </summary>
    public static IReadOnlyList<LlmServer> WithoutDockerPorts(IReadOnlyList<LlmServer> scanned, DockerServerList list)
    {
        ArgumentNullException.ThrowIfNull(scanned);
        ArgumentNullException.ThrowIfNull(list);
        var ports = list.Chosen.Where(DockerServerHost.Active).SelectMany(DockerServerHost.HostPortsOf).ToHashSet();
        return ports.Count == 0 ? scanned : scanned.Where(s => !(s.BaseUrl.IsLoopback && ports.Contains(s.BaseUrl.Port))).ToList();
    }

    /// <summary>
    /// The kill switch (Ctrl+Alt+X, 2026-10-01, the user's ask): every embedded server killed at once
    /// (<see cref="IEmbeddedLlm.Kill"/>), from whatever task read the key — the session's own state is left for
    /// <see cref="EmbeddedUnloaded"/> on the screen's task. The ids of the models unloaded; empty when there were none (no
    /// embedded model here, or another server in use), and the screen then does nothing.
    /// </summary>
    public IReadOnlyList<string> KillEmbedded() => _embedded?.Kill() ?? [];

    /// <summary>
    /// After <see cref="KillEmbedded"/>, on the screen's task: with the endpoint the embedded model's, the session lets it go
    /// — no endpoint (the hint row's model name goes), no assistant — so the next message says to pick a server, and
    /// <c>/server</c> loads a model again. The saved URL stays. Another endpoint (a botchat's extras were the ones killed) is
    /// left as it is.
    /// </summary>
    public void EmbeddedUnloaded()
    {
        if (Endpoint is not { } endpoint || !EmbeddedEndpoint.IsEmbedded(endpoint.BaseUrl))
        {
            return;
        }

        Reconnecting();
        EmbeddedServer = null;
        Endpoint = null;
    }

    /// <summary>
    /// The catalog model <paramref name="id"/> names when it is installed; with no id, the first installed one (so
    /// <c>--url embedded</c> alone runs something). Null when neither: an unknown id, a named model not installed, nothing installed.
    /// </summary>
    public EmbeddedModel? EmbeddedModelFor(string? id)
    {
        if (_embedded is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(id))
        {
            return EmbeddedModelCatalog.Find(id, _embedded.Catalog) is { } named && _embedded.State(named).IsInstalled ? named : null;
        }

        return _embedded.Catalog.FirstOrDefault(m => _embedded.State(m).IsInstalled);
    }

    /// <summary>
    /// The Claude CLI's endpoint (2026-09-30): the sentinel, the <c>--model</c> word (a blank one is
    /// <see cref="ClaudeCliEndpoint.DefaultModel"/>), no key, and the window every current model has until the CLI says its own.
    /// </summary>
    public static LlmEndpoint ClaudeCliEndpointOf(string? model) =>
        new(ClaudeCliEndpoint.BaseUrl, ClaudeCliEndpoint.ModelOf(model), "", ClaudeCliSource, new ContextLength(ClaudeCliEndpoint.DefaultContextWindow, ClaudeCliSource));

    /// <summary>Whether the Claude CLI is offered for <paramref name="effective"/> (2026-09-30): the switch on and the CLI found.</summary>
    public bool ClaudeCliOffered(AppSettingsData effective) => _claudeCliOffered(effective);

    /// <summary>The source phrase of the Claude CLI's endpoint and its window. Pinned.</summary>
    public const string ClaudeCliSource = "Claude CLI";

    /// <summary>
    /// The Claude CLI's <c>/server</c> row (2026-09-30), asked nothing: its models are the <c>--model</c> aliases. None when
    /// it is not offered.
    /// </summary>
    public IReadOnlyList<LlmServer> ClaudeCliRows(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return _claudeCliOffered(effective)
            ? [new LlmServer(ClaudeCliEndpoint.BaseUrl, ClaudeCliEndpoint.ServerName, new ProbeResult(true, ClaudeCliEndpoint.Models, ClaudeCliText.RowDetail))]
            : [];
    }

    /// <summary>The source phrase of an embedded endpoint whose server did not start. Pinned.</summary>
    public const string EmbeddedNotRunningSource = "embedded, not running";

    /// <summary>The error when an embedded URL is set where no embedded model can run (another OS, a session without one). Pinned.</summary>
    public const string EmbeddedUnavailable = "The LLM URL names the embedded model, which is not available here (llama.cpp's Windows x64 builds only).";

    /// <summary>
    /// The <c>/server</c> rows of the embedded model (2026-09-29): one per installed catalog model, asked nothing — every
    /// catalog model, installed or not, until later that day, when the user asked for the installed ones alone: a download
    /// starts from the catalog on <c>/settings</c>' Embedded tab. None when no embedded model is offered — no service, not
    /// Windows x64, or <paramref name="effective"/>'s <c>Embedded servers enabled</c> off (2026-09-29) — or none is installed.
    /// </summary>
    public IReadOnlyList<LlmServer> EmbeddedRows(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (_embedded is not { } embedded || !EmbeddedEndpoint.Offered || !effective.EmbeddedLlmServer)
        {
            return [];
        }

        return embedded.Catalog.Where(model => embedded.State(model).IsInstalled).Select(model =>
        {
            var state = embedded.State(model);
            var result = new ProbeResult(state.IsInstalled, [model.Id], EmbeddedLlmText.ModelDetail(model, state));
            return new LlmServer(EmbeddedEndpoint.BaseUrl, EmbeddedEndpoint.ServerName, result);
        }).ToList();
    }

    /// <summary>
    /// <see cref="Connect"/> over an endpoint already resolved, then — when neither the settings
    /// nor the server's model list named the window — the context probe's native tiers. What the
    /// screen's server pick and the session's own <see cref="ConnectAsync(AppSettingsData, CancellationToken)"/> both end in.
    /// </summary>
    public async Task<bool> ConnectAsync(AppSettingsData effective, LlmEndpoint endpoint, CancellationToken cancellationToken, Action<string>? phase = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!DockerEndpoint.IsDocker(endpoint.BaseUrl))
        {
            // A scan row picked (2026-10-02): a chosen container in use stops first, the user's call. Its server runs already: no settle.
            await LeaveDockerAsync(effective, except: null, settle: false, phase, cancellationToken).ConfigureAwait(false);
        }

        if (!Connect(effective, endpoint))
        {
            return false;
        }

        DiagnosticLog.Info(Category, ConnectedLogLine(endpoint));
        if (Assistant?.Sampling is { IsEmpty: false } sampling)
        {
            DiagnosticLog.Info(Category, SamplingLogLine(sampling));
        }

        // The Anthropic API publishes its window on the model list or nowhere: none of the native tiers live on its host; the
        // OpenAI API's comes from the model table (2026-10-03, set at the connect). The Claude CLI's is the endpoint's own
        // (2026-09-30): no host to ask at all.
        if (_configuredContextLength <= 0 && _detectedContextLength is null && !ApiKeys.IsHostedApi(endpoint.BaseUrl) && !ClaudeCliEndpoint.IsClaudeCli(endpoint.BaseUrl))
        {
            _detectedContextLength = await _contextProbe.DetectAsync(endpoint.WireUrl, endpoint.ModelId, KeyFor(endpoint), cancellationToken).ConfigureAwait(false);
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
    /// is null until <see cref="Connect"/> lands one. A chosen container in use stops before the scan (2026-10-02, the user's
    /// ask): a blank URL is another server, and after a profile switch the new list would not hide the old container's port —
    /// the scan would offer it, and the pick's own leave then stop the server just picked.
    /// </summary>
    public async Task<IReadOnlyList<LlmServer>> DiscoverAsync(AppSettingsData effective, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(effective);
        Reconnecting();
        Endpoint = null;
        Remember(effective);
        await LeaveDockerAsync(effective, except: null, settle: false, phase: null, cancellationToken).ConfigureAwait(false);
        return await WithExtraRowsAsync(effective, _probe.DiscoverAllAsync(LlmEndpoint.KeyOf(effective), extra: null, LlmScanMode.Resolve(effective), cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>/server</c>'s look-around: the servers that answer under the settings' scan mode plus
    /// <paramref name="extra"/> (the endpoint in use, when it is on a port the list does not name).
    /// The session stays as it is until a pick lands.
    /// </summary>
    public Task<IReadOnlyList<LlmServer>> ProbeServersAsync(AppSettingsData effective, Uri? extra, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return WithExtraRowsAsync(effective, _probe.DiscoverAllAsync(LlmEndpoint.KeyOf(effective), extra, LlmScanMode.Resolve(effective), cancellationToken), cancellationToken);
    }

    /// <summary>
    /// The scan's servers, then the embedded model's rows (<see cref="EmbeddedRows"/>, 2026-09-29), then — while the Claude
    /// API is offered (<see cref="ClaudeApi.Offered"/>, 2026-09-27) — its row: asked for its model list with its own
    /// key alongside the scan, never scanned for, and listed whatever it answered (the row's detail then says why), so
    /// the switch and the key are all it takes to see it; then the OpenAI API's the same way (<see cref="OpenAIApi.Offered"/>,
    /// 2026-10-03); then the Claude CLI's (<see cref="ClaudeCliRows"/>, 2026-09-30) last. The scan mode governs none of them: <c>disabled</c> still lists them.
    /// </summary>
    private async Task<IReadOnlyList<LlmServer>> WithExtraRowsAsync(AppSettingsData effective, Task<IReadOnlyList<LlmServer>> scan, CancellationToken cancellationToken)
    {
        Task<ProbeResult>? claude = ClaudeApi.Offered(effective) ? _probe.ProbeAsync(ClaudeApi.BaseUrl, ClaudeApi.Key(effective), cancellationToken) : null;
        Task<ProbeResult>? openAI = OpenAIApi.Offered(effective)
            ? _probe.ProbeAsync(OpenAIApi.BaseUrl, OpenAIApi.Key(effective), cancellationToken, OpenAIApi.HeadersFor(effective, OpenAIApi.BaseUrl))
            : null;
        Task<DockerServerList>? docker = DockerOffered(effective) ? _docker!.ListAsync(effective, cancellationToken) : null;
        IReadOnlyList<LlmServer> scanned = await scan.ConfigureAwait(false);
        var dockerRows = new List<LlmServer>();
        if (docker is not null)
        {
            // The chosen containers (2026-10-02) after the scan's rows, the ports they answer on taken out of the scan's.
            var list = await docker.ConfigureAwait(false);
            if (list.Error is not null)
            {
                DiagnosticLog.Info(Category, "Docker servers: " + list.Error);
            }
            else
            {
                scanned = WithoutDockerPorts(scanned, list);
                dockerRows.AddRange(DockerRows(list));
            }
        }

        var servers = new List<LlmServer>(scanned);
        servers.AddRange(dockerRows);
        servers.AddRange(EmbeddedRows(effective));
        if (claude is not null)
        {
            var result = await claude.ConfigureAwait(false);
            DiagnosticLog.Info(Category, $"{ClaudeApi.ServerName}: {result.Detail}.");
            servers.Add(LlmServer.From(ClaudeApi.BaseUrl, result));
        }

        if (openAI is not null)
        {
            var result = await openAI.ConfigureAwait(false);
            DiagnosticLog.Info(Category, $"{OpenAIApi.ServerName}: {result.Detail}.");
            servers.Add(LlmServer.From(OpenAIApi.BaseUrl, result));
        }

        servers.AddRange(ClaudeCliRows(effective));
        return servers;
    }

    /// <summary>One server asked by URL (<c>/server &lt;url&gt;</c>); the result says whether it answered.</summary>
    public async Task<LlmServer> ProbeServerAsync(Uri baseUrl, AppSettingsData effective, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(effective);
        var v1 = LlmEndpoint.NormalizeBaseUrl(baseUrl);
        var result = await _probe.ProbeAsync(v1, ApiKeys.For(effective, v1), cancellationToken, OpenAIApi.HeadersFor(effective, v1)).ConfigureAwait(false);
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

        // The Claude CLI's window is its models' (2026-09-30), whichever way its endpoint was built (a saved URL, a picked row);
        // the OpenAI API's the model table's (2026-10-03: its model list names none).
        _detectedContextLength = endpoint.PublishedContextLength
            ?? (ClaudeCliEndpoint.IsClaudeCli(endpoint.BaseUrl) ? new ContextLength(ClaudeCliEndpoint.DefaultContextWindow, ClaudeCliSource)
                : OpenAIApi.IsOpenAIApi(endpoint.BaseUrl) ? new ContextLength(OpenAIModelRules.ContextWindow(endpoint.ModelId), ContextLengthProbe.OpenAIModelTableSource)
                : null);
        if (!EmbeddedEndpoint.IsEmbedded(endpoint.BaseUrl))
        {
            // Another server picked (2026-09-29): the embedded one's memory is free again.
            EmbeddedServer = null;
            if (_embedded?.Running is not null)
            {
                _embedded.Stop();
            }
        }

        if (!ClaudeCliEndpoint.IsClaudeCli(endpoint.BaseUrl) && _claudeServer is { Running: true } claudeServer)
        {
            // Another server picked (2026-09-30): the Claude CLI's process goes; the session stays on disk for a pick back.
            claudeServer.Stop();
        }

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
    public Task<(BotLink? Link, string? Problem)> LinkAsync(AppSettingsData profile, CancellationToken cancellationToken) =>
        LinkAsync(profile, BotEmbeddedMode.ParentServer, cancellationToken);

    /// <summary>
    /// <see cref="LinkAsync(AppSettingsData, CancellationToken)"/> under <c>Botchat multi-embedded</c> (later on 2026-09-29, the
    /// user's ask): <paramref name="embeddedMode"/> says what a bot naming another embedded model than the running one gets —
    /// the running one, with <see cref="BotLink.SharedEmbedded"/> set for the warning, or an extra server of its own.
    /// </summary>
    public async Task<(BotLink? Link, string? Problem)> LinkAsync(AppSettingsData profile, BotEmbeddedMode embeddedMode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        (string Wanted, string Running)? shared = null;
        string? model = string.IsNullOrWhiteSpace(profile.LlmModel) ? null : profile.LlmModel.Trim();
        LlmEndpoint endpoint;
        if (string.IsNullOrWhiteSpace(profile.LlmUrl))
        {
            if (Endpoint is not { } starter)
            {
                return (null, BotLinkNoServer);
            }

            // The Anthropic API's key goes with its endpoint (2026-09-27): a borrowed Anthropic API is borrowed with it.
            // The profile's key decrypted first (2026-09-28): the file keeps it DPAPI-encrypted like the other two.
            // The embedded server's per-start key and its one model go with it too (2026-09-29).
            string own = LlmEndpoint.KeyOf(profile);
            bool embedded = EmbeddedEndpoint.IsEmbedded(starter.BaseUrl);
            bool docker = DockerEndpoint.IsDocker(starter.BaseUrl);   // a chosen container (2026-10-02): its one model goes with it, as the embedded server's
            if ((embedded || docker) && starter.LiveUrl is null)
            {
                return (null, BotLinkNoServer);
            }

            string key = ApiKeys.IsHostedApi(starter.BaseUrl) || embedded ? starter.ApiKey
                : string.IsNullOrWhiteSpace(own) || own == LlmEndpoint.DefaultApiKey ? _apiKey : own;
            endpoint = starter with { ModelId = embedded || docker ? starter.ModelId : model ?? starter.ModelId, ApiKey = key, PublishedContextLength = null };
        }
        else if (DockerEndpoint.IsDocker(profile.LlmUrl))
        {
            // A bot's own container (2026-10-02): only the one running is shared — a botchat never starts or stops one.
            string wanted = DockerEndpoint.ContainerOf(profile.LlmUrl)!;
            if (Endpoint is not { LiveUrl: not null } running || DockerEndpoint.ContainerOf(running.BaseUrl) is not { } current)
            {
                return (null, DockerServerText.BotNoneRunning);
            }

            if (!string.Equals(wanted, current, StringComparison.Ordinal))
            {
                return (null, DockerServerText.BotOtherContainer(wanted, current));
            }

            endpoint = running with { PublishedContextLength = null };
        }
        else if (ClaudeCliEndpoint.IsClaudeCli(profile.LlmUrl))
        {
            // A bot's own Claude CLI (2026-09-30): asked nothing, its requests asked beside the chat (one claude -p each).
            if (!_claudeCliOffered(profile))
            {
                return (null, ClaudeCliText.NotOfferedError);
            }

            endpoint = ClaudeCliEndpointOf(model);
        }
        else if (EmbeddedEndpoint.IsEmbedded(profile.LlmUrl))
        {
            var (embedded, problem, sharing) = await LinkEmbeddedAsync(profile, model, embeddedMode, cancellationToken).ConfigureAwait(false);
            if (embedded is null)
            {
                return (null, problem);
            }

            endpoint = embedded;
            shared = sharing;
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

            string key = ApiKeys.For(profile, v1);
            var result = await _probe.ProbeAsync(v1, key, cancellationToken, OpenAIApi.HeadersFor(profile, v1)).ConfigureAwait(false);
            if (!result.Exists)
            {
                return (null, $"{v1} did not answer ({result.Detail})");
            }

            endpoint = LlmEndpointProbe.Endpoint(LlmServer.From(v1, result), key, model, configured: true);
        }

        var timeouts = LlmTimeouts.Resolve(profile);
        try
        {
            return (new BotLink(_factory(endpoint, timeouts), endpoint, timeouts, ReasoningLevel.Resolve(profile), LlmSampling.Resolve(profile, endpoint.ModelId)) { SharedEmbedded = shared }, null);
        }
        catch (Exception ex)
        {
            return (null, "could not create the chat client: " + Llm.Assistant.Explain(ex));
        }
    }

    /// <summary>
    /// A bot's link to the embedded server (2026-09-29): a bot whose profile names the running model, or none, uses the one
    /// running; with none running it starts the model its profile names (installed only: a botchat never downloads). A bot
    /// naming another model (refused until later on 2026-09-29, the user's ask) is <paramref name="mode"/>'s:
    /// <see cref="BotEmbeddedMode.ParentServer"/> gives it the running one and the pair for the warning,
    /// <see cref="BotEmbeddedMode.MultiServer"/> an extra server of its own (<see cref="IEmbeddedLlm.StartExtraAsync"/>). One
    /// link at a time (<see cref="_embeddedLinks"/>), so under parent-server the first embedded bot's model is the one that starts.
    /// </summary>
    private async Task<(LlmEndpoint? Endpoint, string? Problem, (string Wanted, string Running)? Shared)> LinkEmbeddedAsync(AppSettingsData profile, string? model, BotEmbeddedMode mode, CancellationToken cancellationToken)
    {
        if (_embedded is null || !EmbeddedEndpoint.Offered)
        {
            return (null, EmbeddedUnavailable, null);
        }

        if (!_embeddedEnabled)
        {
            // The loaded profile's switch governs the one server, whichever profile a bot comes from (2026-09-29).
            return (null, EmbeddedLlmText.SwitchedOffError, null);
        }

        await _embeddedLinks.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_embedded.Running is { } running)
            {
                if (model is null || string.Equals(model, running.ModelId, StringComparison.OrdinalIgnoreCase))
                {
                    return (EmbeddedEndpointOf(running), null, null);
                }

                if (mode == BotEmbeddedMode.ParentServer)
                {
                    return (EmbeddedEndpointOf(running), null, (DisplayOf(model), DisplayOf(running.ModelId)));
                }
            }

            var wanted = EmbeddedModelFor(model);
            if (wanted is null)
            {
                return (null, model is not null && EmbeddedModelCatalog.Find(model, _embedded.Catalog) is { } known ? EmbeddedLlmText.NotInstalled(known) : EmbeddedLlmText.NoneInstalled, null);
            }

            try
            {
                var info = mode == BotEmbeddedMode.MultiServer
                    ? await _embedded.StartExtraAsync(wanted, profile, null, cancellationToken).ConfigureAwait(false)
                    : await _embedded.StartAsync(wanted, profile, null, cancellationToken).ConfigureAwait(false);
                return (EmbeddedEndpointOf(info), null, null);
            }
            catch (EmbeddedLlmException ex)
            {
                return (null, ex.Message, null);
            }
        }
        finally
        {
            _embeddedLinks.Release();
        }

        string DisplayOf(string id) => EmbeddedModelCatalog.Find(id, _embedded.Catalog)?.Display ?? id;
    }

    private static LlmEndpoint EmbeddedEndpointOf(EmbeddedServerInfo info) =>
        new(EmbeddedEndpoint.BaseUrl, info.ModelId, info.ApiKey, EmbeddedLlmText.Source(info)) { LiveUrl = info.BaseUrl };

    /// <summary>The key a probe of <paramref name="endpoint"/> carries: an embedded server's own per-start key, else the settings'.</summary>
    private string KeyFor(LlmEndpoint endpoint) => EmbeddedEndpoint.IsEmbedded(endpoint.BaseUrl) ? endpoint.ApiKey : _apiKey;

    /// <summary><see cref="LinkAsync"/>'s problem for a borrowed server while this session has none. Pinned.</summary>
    public const string BotLinkNoServer = "no LLM URL of its own and no server connected to borrow";

    /// <summary>What every later call needs from the settings a connect was made with.</summary>
    private void Remember(AppSettingsData effective)
    {
        Timeouts = LlmTimeouts.Resolve(effective);
        _effective = effective;
        string apiKey = LlmEndpoint.KeyOf(effective);
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? LlmEndpoint.DefaultApiKey : apiKey;
        _configuredUrl = effective.LlmUrl ?? "";
        _configuredContextLength = effective.LlmContextLength;
        _embeddedEnabled = effective.EmbeddedLlmServer;
    }

    /// <summary>
    /// Asks the current endpoint (or the configured URL when nothing is connected) for its model
    /// list. Null when there is no URL to ask.
    /// </summary>
    public Task<ProbeResult?> ListModelsAsync(CancellationToken cancellationToken)
    {
        if (EmbeddedEndpoint.IsEmbedded(Endpoint?.BaseUrl) || (Endpoint is null && _embeddedEnabled && EmbeddedEndpoint.IsEmbedded(_configuredUrl)))
        {
            // The embedded model (2026-09-29): its list is the installed catalog models, asked of no server — a pick restarts
            // the one server with the other model.
            if (_embedded is not { } embedded)
            {
                return Task.FromResult<ProbeResult?>(null);
            }

            var installed = embedded.Catalog.Where(m => embedded.State(m).IsInstalled).Select(m => m.Id).ToList();
            string detail = installed.Count == 1 ? "1 embedded model" : $"{installed.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} embedded models";
            return Task.FromResult<ProbeResult?>(new ProbeResult(installed.Count > 0, installed, detail));
        }

        if (ClaudeCliEndpoint.IsClaudeCli(Endpoint?.BaseUrl) || (Endpoint is null && _claudeCliOffered(_effective) && ClaudeCliEndpoint.IsClaudeCli(_configuredUrl)))
        {
            // The Claude CLI (2026-09-30): its list is the --model aliases, asked of no one.
            return Task.FromResult<ProbeResult?>(new ProbeResult(true, ClaudeCliEndpoint.Models, ClaudeCliText.RowDetail));
        }

        if (DockerEndpoint.IsDocker(Endpoint?.BaseUrl) || (Endpoint is null && DockerEndpoint.IsDocker(_configuredUrl)))
        {
            // A chosen container (2026-10-02): asked where it answered; not running, there is nothing to ask.
            return Endpoint?.LiveUrl is { } live ? ProbeAsync(live, cancellationToken) : Task.FromResult<ProbeResult?>(null);
        }

        Uri? url = Endpoint?.BaseUrl;
        if (url is null && !string.IsNullOrWhiteSpace(_configuredUrl) && !EmbeddedEndpoint.IsEmbedded(_configuredUrl) && !ClaudeCliEndpoint.IsClaudeCli(_configuredUrl))   // switched off: nothing to ask (2026-09-29)
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
        await _probe.ProbeAsync(url, ApiKeys.IsHostedApi(url) ? ApiKeys.For(_effective, url) : _apiKey, cancellationToken, OpenAIApi.HeadersFor(_effective, url)).ConfigureAwait(false);

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
    /// while nothing is connected, over the Anthropic API or the OpenAI API (no sampling goes there), with no probe, or when the server said
    /// nothing.
    /// </summary>
    public async Task<ServerSampling?> ServerSamplingAsync(AppSettingsData effective, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(effective);
        if (_samplingProbe is null || Endpoint is not { } endpoint || ApiKeys.IsHostedApi(endpoint.BaseUrl) || ClaudeCliEndpoint.IsClaudeCli(endpoint.BaseUrl))
        {
            return null;
        }

        string key = string.Join('|', endpoint.WireUrl.AbsoluteUri, endpoint.ModelId, effective.LlmSamplingFromHuggingFace ? "hf" : "");
        if (_serverSampling is { } cached && cached.Key == key)
        {
            return cached.Found;
        }

        var found = await _samplingProbe.DetectAsync(endpoint.WireUrl, endpoint.ModelId, KeyFor(endpoint), effective.LlmSamplingFromHuggingFace, cancellationToken).ConfigureAwait(false);
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
        _embeddedLinks.Dispose();
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

    /// <summary>
    /// The embedded model the bot's profile named and the one it got instead (later on 2026-09-29, <c>Botchat multi-embedded</c>
    /// <c>parent-server</c>): for the warning; null when it got its own.
    /// </summary>
    public (string Wanted, string Running)? SharedEmbedded { get; init; }

    public void Dispose() => Client.Dispose();
}
