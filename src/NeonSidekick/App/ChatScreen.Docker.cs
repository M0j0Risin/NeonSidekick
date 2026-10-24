using Microsoft.Extensions.AI;
using NeonSidekick.Diagnostics;
using NeonSidekick.Docker;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.App;

// ── Docker (2026-10-02) ───────────────────────────────────────────────────

internal sealed partial class ChatScreen
{
    /// <summary>The Docker door the tools, <c>/docker</c> and its pane share: the client for the pipe in force, the last list.</summary>
    private readonly DockerSession _docker;

    /// <summary>
    /// The Docker log window on a container (2026-10-23, the user's ask: <c>/docker logs &lt;container&gt;</c> and the <c>/docker</c>
    /// pane's logs button tail the log in a window; <c>Viewer.DockerLogWindow.Show</c>). Null = no window here: <c>/docker logs</c>
    /// shows the pane as before.
    /// </summary>
    private readonly Action<DockerLogSource>? _openDockerLogWindow;

    /// <summary>The Docker log window closed (the screen's teardown, before <see cref="_docker"/> goes); null = none here.</summary>
    private readonly Func<bool>? _closeDockerLogWindow;

    /// <summary>The ten Docker tools, built once over <see cref="_docker"/>; offered while <see cref="DockerOffered"/> says so, the changes under <c>Docker writes</c> (<see cref="DockerToolsFor"/>).</summary>
    private readonly IReadOnlyList<AIFunction> _dockerTools;

    /// <summary>
    /// The Docker tools (2026-10-02): the six reads (containers, logs, inspect, stats, resources, compose), then the four
    /// changes (lifecycle, pull, remove, prune) behind <paramref name="confirm"/> — every change asks; null where nothing can
    /// ask (headless), so there every change is refused. What a turn offers of them is <see cref="DockerToolsFor"/>'s.
    /// </summary>
    public static IReadOnlyList<AIFunction> DockerTools(DockerSession docker, Func<string, CancellationToken, Task<bool?>>? confirm) =>
    [
        new DockerContainersTool(docker),
        new DockerLogsTool(docker),
        new DockerInspectTool(docker),
        new DockerStatsTool(docker),
        new DockerResourcesTool(docker),
        new DockerComposeTool(docker),
        new DockerLifecycleTool(docker, confirm),
        new DockerPullTool(docker, confirm),
        new DockerRemoveTool(docker, confirm),
        new DockerPruneTool(docker, confirm),
    ];

    /// <summary>The Docker tools that change something (2026-10-02): offered only under <c>Docker writes</c> (<see cref="DockerToolsFor"/>).</summary>
    public static readonly IReadOnlySet<string> DockerWriteToolNames = new HashSet<string>(StringComparer.Ordinal)
    {
        DockerLifecycleTool.ToolName,
        DockerPullTool.ToolName,
        DockerRemoveTool.ToolName,
        DockerPruneTool.ToolName,
    };

    /// <summary>The Docker tools' names: their result's first line is the transcript's note (<see cref="DockerText.Note"/>).</summary>
    public static readonly IReadOnlySet<string> DockerToolNames = new HashSet<string>(StringComparer.Ordinal)
    {
        DockerContainersTool.ToolName,
        DockerLogsTool.ToolName,
        DockerInspectTool.ToolName,
        DockerStatsTool.ToolName,
        DockerResourcesTool.ToolName,
        DockerComposeTool.ToolName,
        DockerLifecycleTool.ToolName,
        DockerPullTool.ToolName,
        DockerRemoveTool.ToolName,
        DockerPruneTool.ToolName,
    };

    /// <summary>
    /// What a turn may offer of the Docker tools (2026-10-02): the reads always; the changes (<see cref="DockerWriteToolNames"/>)
    /// only while <c>Docker writes</c> is on — checked again at every call, and each still asks. Pure.
    /// </summary>
    public static IReadOnlyList<AIFunction> DockerToolsFor(IReadOnlyList<AIFunction> tools, AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(effective);
        return effective.DockerWrites ? tools : tools.Where(t => !DockerWriteToolNames.Contains(t.Name)).ToList();
    }

    /// <summary>Whether the Docker group is offered (2026-10-02): the setting <c>Docker tools</c> on, on Windows or (2026-10-11) a Mac (<see cref="DockerPolicy.IsOffered"/>).</summary>
    public static bool DockerOffered(AppSettingsData effective) => DockerPolicy.IsOffered(effective);

    /// <summary>
    /// A model's Docker change's question, on the turn task: the yes/no pane through the watcher — <see cref="ConfirmHomeAsync"/>'s
    /// way, the cursor on No, ESC a no. Null (never asked) without the pane or a watcher to run it.
    /// </summary>
    private async Task<bool?> ConfirmDockerAsync(string question, CancellationToken turnToken)
    {
        // On the PC's pane and the Web UI's page at once (2026-10-14, ConfirmBothAsync), the first answer winning.
        return await ConfirmBothAsync(question, "The Docker confirm pane failed: ", turnToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>/docker</c>'s argument list: <see cref="DockerCommand.Complete"/> over the last list read. With none yet, or an old one,
    /// a read is started in the background so the next keystroke has names; the list itself never waits on the engine.
    /// </summary>
    private IReadOnlyList<CompletionItem> DockerChoices(string argText)
    {
        if (_docker.LastIsStale && argText.Contains(' ', StringComparison.Ordinal))
        {
            _ = RefreshDockerQuietlyAsync();
        }

        return DockerCommand.Complete(_docker.Last, argText);
    }

    private async Task RefreshDockerQuietlyAsync()
    {
        try
        {
            _ = await _docker.ContainersAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DiagnosticLog.Debug(DockerText.Category, "The background read for /docker's list failed: " + ex.Message);
        }
    }

    /// <summary>
    /// <c>/docker [verb …]</c> (2026-10-02): the bare word on the pane opens <see cref="DockerMenu"/>; anything else (and the bare
    /// word without the pane) is <see cref="DockerCommand.RunAsync"/> under a spinner, its lines as notices (a failure's as an
    /// error). The user's own hand: <c>Docker writes</c> never judges it, nothing is added to the conversation.
    /// </summary>
    private async Task HandleDockerAsync(string args, CancellationToken cancellationToken)
    {
        // From the Web UI's page (later on 2026-10-14) the bare word lists, as without the pane, and a change asks first.
        if (args.Trim().Length == 0 && _pane.Enabled && !FromWebNow)
        {
            await DockerMenuFor(closeWhenDone: true).ShowAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (TakesDockerLogWindow(args))
        {
            var target = await _transcript.WithSpinnerAsync(DockerText.Working, () => DockerCommand.LogsTargetAsync(_docker, LogsRest(args), cancellationToken)).ConfigureAwait(false);
            OpenDockerLogWindow(target, _transcript);
            return;
        }

        var confirm = WebConfirmHook;
        var result = await _transcript.WithSpinnerAsync(DockerText.Working, () => DockerCommand.RunAsync(_docker, args, cancellationToken, confirm)).ConfigureAwait(false);
        await ShowDockerResultAsync(result, args, _transcript, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether a <c>/docker</c> line goes to the Docker log window (2026-10-23, the user's ask): <c>logs</c> without <c>--pane</c>,
    /// typed at the app's computer (a Web UI page keeps the list card), with a window here.
    /// </summary>
    private bool TakesDockerLogWindow(string args)
    {
        string text = args.Trim();
        string verb = text.Split(' ', 2)[0];
        return _openDockerLogWindow is not null && !FromWebNow
            && string.Equals(verb, "logs", StringComparison.OrdinalIgnoreCase)
            && !DockerCommand.ParseLogs(LogsRest(text)).Pane;
    }

    /// <summary>A <c>logs</c> line's words after the verb.</summary>
    private static string LogsRest(string args)
    {
        string text = args.Trim();
        int space = text.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? "" : text[(space + 1)..].Trim();
    }

    /// <summary>
    /// <paramref name="target"/> (<see cref="DockerCommand.LogsTargetAsync"/>'s) in the Docker log window, how it went through
    /// <paramref name="sink"/>: the refusal's sentence, the opened notice, or the window's failure. True when the window shows it.
    /// <see cref="OpenProcessWindow"/>'s shape.
    /// </summary>
    private bool OpenDockerLogWindow((DockerContainer? Container, int Tail, string? Error) target, INoticeSink sink)
    {
        if (target.Container is not { } container)
        {
            sink.Error(target.Error ?? DockerText.Usage);
            return false;
        }

        return OpenDockerLogWindow(container, target.Tail, sink);
    }

    /// <summary>The window on <paramref name="container"/> with <paramref name="tail"/> earlier lines (the typed line's, or the <c>/docker</c> pane's button).</summary>
    private bool OpenDockerLogWindow(DockerContainer container, int tail, INoticeSink sink)
    {
        if (_openDockerLogWindow is null)
        {
            sink.Error(Viewer.DockerLogWindowText.UnavailableHere);
            return false;
        }

        try
        {
            _openDockerLogWindow(new DockerLogSource(_docker, container.Id, container.Name, tail));
            sink.Notice(Viewer.DockerLogWindowText.OpenedNotice(container.Name));
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            sink.Error(Viewer.DockerLogWindowText.WindowFailedError(ex.Message));
            return false;
        }
    }

    /// <summary>
    /// The container list (<see cref="DockerMenu"/>): the bare <c>/docker</c>'s own (<paramref name="closeWhenDone"/>), or the Docker tools
    /// page's manage button's (2026-10-11), ESC back to the page and <c>logs</c> run as a chord at the idle line. Its llm button reads the
    /// effective match words and chosen containers at each pass.
    /// </summary>
    private DockerMenu DockerMenuFor(bool closeWhenDone) =>
        new(_docker, _flow, _menuPane, _web.OpenUrl, _copy,
            () => (_effective().DockerServerMatchWords ?? [], _effective().DockerServerContainers ?? []),
            closeWhenDone, closeWhenDone ? null : line => _pane.ChordsOff ? null : _pane.Chord(line),
            // Its logs button opens the Docker log window (2026-10-23, the user's ask), where there is one.
            _openDockerLogWindow is null ? null : container => OpenDockerLogWindow(container, Viewer.DockerLogWindowText.DefaultWindowLines, _transcript));

    /// <summary>
    /// <c>/docker</c> under a reply (2026-10-02, <c>/ha</c>'s way, <see cref="MidTurnPolicy(SlashCommand, bool)"/>): on the watcher, no
    /// spinner — the busy row is up already —, the lines through the flow sink; the bare word lists rather than opening the pane.
    /// </summary>
    private async Task HandleDockerMidTurnAsync(string args, CancellationToken cancellationToken)
    {
        DockerCommandResult result;
        try
        {
            if (TakesDockerLogWindow(args))
            {
                // The window on the watcher too (2026-10-23): its own thread, so the reply streams on.
                OpenDockerLogWindow(await DockerCommand.LogsTargetAsync(_docker, LogsRest(args), cancellationToken).ConfigureAwait(false), _flow);
                return;
            }

            result = await DockerCommand.RunAsync(_docker, args, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DiagnosticLog.Debug(DockerText.Category, "/docker under the reply was canceled as the reply ended.");
            return;
        }

        await ShowDockerResultAsync(result, args, _flow, cancellationToken).ConfigureAwait(false);
    }

    // ── Docker servers (2026-10-02) ──────────────────────────────────────────

    /// <summary>
    /// Switches to the chosen container <paramref name="name"/> (2026-10-02; a <c>/server</c> row, <c>/server docker:&lt;name&gt;</c>,
    /// <c>/server docker</c>'s pick): refused while Docker servers is off or the container is not chosen; else saved as the LLM URL
    /// (the model cleared when the URL changed — the container's own is taken), the reasoning picker offered, and one connect,
    /// which stops the others, starts it and waits for its model under the load's spinner. With a flag or variable overriding the
    /// URL, the save and its warning are all that happens.
    /// </summary>
    private async Task UseDockerAsync(string name, CancellationToken cancellationToken, bool reasoning = true)
    {
        var effective = _effective();
        if (_session.DockerServers is null || !OperatingSystem.IsWindows())
        {
            _transcript.Error(DockerServerText.UnavailableHere);
            return;
        }

        if (!effective.DockerServers)
        {
            _transcript.Error(DockerServerText.SwitchedOffError);
            return;
        }

        if (!DockerEndpoint.ChosenNames(effective).Contains(name, StringComparer.Ordinal))
        {
            _transcript.Error(DockerServerText.NotChosen(name));
            return;
        }

        _menu.SaveServer(DockerEndpoint.BaseUrl(name));
        if (_overriddenBy(SettingsField.LlmUrl) is not null)
        {
            return;
        }

        if (reasoning)
        {
            // Asked before the load, as for the embedded model: the walk is still on screen.
            await _menu.PickReasoningAsync("", _effective().LlmReasoning, cancellationToken).ConfigureAwait(false);
        }

        await ConnectLlmAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary><c>/server docker</c> (2026-10-02): the chosen containers' rows alone, read from the engine under a spinner; a pick switches to it.</summary>
    private async Task PickDockerServerAsync(AppSettingsData effective, CancellationToken cancellationToken)
    {
        if (_session.DockerServers is not { } servers || !OperatingSystem.IsWindows())
        {
            _transcript.Error(DockerServerText.UnavailableHere);
            return;
        }

        if (!effective.DockerServers)
        {
            _transcript.Error(DockerServerText.SwitchedOffError);
            return;
        }

        if (DockerEndpoint.ChosenNames(effective).Count == 0)
        {
            _transcript.Error(DockerServerText.NoneChosenError);
            return;
        }

        var list = await _transcript.WithSpinnerAsync(DockerText.Working, () => servers.ListAsync(effective, cancellationToken)).ConfigureAwait(false);
        if (list.Error is not null)
        {
            _transcript.Error(list.Error);
            return;
        }

        if (await _menu.PickServerAsync(LlmSession.DockerRows(list), _session.Endpoint?.BaseUrl, SettingsMenu.ServerTitle, cancellationToken).ConfigureAwait(false) is { } row
            && DockerEndpoint.ContainerOf(row.BaseUrl) is { } name)
        {
            await UseDockerAsync(name, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <c>Docker server stop on exit</c> at the screen's end (2026-10-02): off by default (the container keeps running); on, with
    /// a chosen container in use, it stops under a spinner — its own deadline, never the app's token — and the log says so.
    /// Nothing here may cut the rest of the teardown short.
    /// </summary>
    private async Task StopDockerAtExitAsync()
    {
        var effective = _effective();
        if (!effective.DockerServerStopOnExit || _session.DockerInUse is not { } name)
        {
            return;
        }

        try
        {
            var budget = TimeSpan.FromSeconds(Math.Clamp(effective.DockerServerStopTimeoutSeconds, 0, AppSettingsData.MaxDockerServerStopTimeoutSeconds)) + DockerServerHost.StopGrace;
            using var cts = new CancellationTokenSource(budget);
            var stopped = await _transcript.WithSpinnerAsync(DockerServerText.Stopping(name), () => _session.StopDockerAtExitAsync(effective, cts.Token)).ConfigureAwait(false);
            foreach (string container in stopped)
            {
                DiagnosticLog.Info(DockerText.Category, SidekickApp.DockerServerExitNotice(container));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DiagnosticLog.Warn(DockerText.Category, "Docker server stop on exit: " + Llm.Assistant.Explain(ex));
        }
    }

    /// <summary>
    /// A <c>/docker</c> result: <c>logs</c>' lines on the info pane (2026-10-04, the user's pick: up to 2000 of them), over a reply
    /// too; every other verb's lines as notices, or a failure's as errors.
    /// </summary>
    private Task ShowDockerResultAsync(DockerCommandResult result, string args, INoticeSink sink, CancellationToken cancellationToken)
    {
        if (result.Paned && !result.Failed)
        {
            return ShowLinesAsync(TreeText.PaneLabel("/docker", args), result.Lines, sink, cancellationToken);
        }

        WriteDockerResult(result, sink);
        return Task.CompletedTask;
    }

    /// <summary>A <c>/docker</c> result's lines as notices, or a failure's as errors.</summary>
    private static void WriteDockerResult(DockerCommandResult result, INoticeSink sink)
    {
        foreach (string line in result.Lines)
        {
            if (result.Failed)
            {
                sink.Error(line);
            }
            else
            {
                sink.Notice(line);
            }
        }
    }
}
