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

    /// <summary>Whether the Docker group is offered (2026-10-02): the setting <c>Docker tools</c> on, on Windows (<see cref="DockerPolicy.IsOffered"/>).</summary>
    public static bool DockerOffered(AppSettingsData effective) => DockerPolicy.IsOffered(effective);

    /// <summary>
    /// A model's Docker change's question, on the turn task: the yes/no pane through the watcher — <see cref="ConfirmHomeAsync"/>'s
    /// way, the cursor on No, ESC a no. Null (never asked) without the pane or a watcher to run it.
    /// </summary>
    private async Task<bool?> ConfirmDockerAsync(string question, CancellationToken turnToken)
    {
        if (!_pane.Enabled)
        {
            return null;
        }

        var paneToken = _paneClose?.Token ?? CancellationToken.None;
        bool? yes = null;
        var pending = _keys.RequestPaneAsync(async () =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(paneToken, turnToken);
            try
            {
                yes = await _menu.ConfirmAsync(question, linked.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && !linked.IsCancellationRequested)
            {
                DiagnosticLog.Error(ScreenPane.Category, "The Docker confirm pane failed: " + Llm.Assistant.Explain(ex), ex);
            }
        });
        try
        {
            await pending.WaitAsync(turnToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!turnToken.IsCancellationRequested)
        {
            // No watcher to run the pane: never asked.
            return null;
        }

        return yes ?? false;
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
        if (args.Trim().Length == 0 && _pane.Enabled)
        {
            var menu = new DockerMenu(_docker, _flow, _menuPane, _web.OpenUrl, _copy);
            await menu.ShowAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var result = await _transcript.WithSpinnerAsync(DockerText.Working, () => DockerCommand.RunAsync(_docker, args, cancellationToken)).ConfigureAwait(false);
        WriteDockerResult(result, _transcript);
    }

    /// <summary>
    /// <c>/docker</c> under a reply (2026-10-02, <c>/ha</c>'s way, <see cref="MidTurnPolicy(SlashCommand, bool)"/>): on the watcher, no
    /// spinner — the busy row is up already —, the lines through the flow sink; the bare word lists rather than opening the pane.
    /// </summary>
    private async Task HandleDockerMidTurnAsync(string args, CancellationToken cancellationToken)
    {
        DockerCommandResult result;
        try
        {
            result = await DockerCommand.RunAsync(_docker, args, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DiagnosticLog.Debug(DockerText.Category, "/docker under the reply was cancelled as the reply ended.");
            return;
        }

        WriteDockerResult(result, _flow);
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
