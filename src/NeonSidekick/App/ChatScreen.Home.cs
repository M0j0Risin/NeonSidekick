using Microsoft.Extensions.AI;
using NeonSidekick.Diagnostics;
using NeonSidekick.HomeAssistant;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.App;

// ── Home Assistant (2026-09-28) ───────────────────────────────────────────

internal sealed partial class ChatScreen
{
    /// <summary>The Home Assistant door the tools and <c>/ha</c> share: the client for the URL and token in force, the snapshot.</summary>
    private readonly HaSession _ha;

    /// <summary>The nine Home Assistant tools, built once over <see cref="_ha"/>; offered while <see cref="HomeAssistantOffered"/> says so.</summary>
    private readonly IReadOnlyList<AIFunction> _haTools;

    /// <summary>
    /// The Home Assistant tools (2026-09-28): the three reads (overview, states, history), the four typed acts (lights, scene,
    /// media, to-do), the generic service call and Assist — the acts behind <paramref name="confirm"/> for what the policy asks
    /// about (null where nothing can ask: headless). Shared with headless.
    /// </summary>
    public static IReadOnlyList<AIFunction> HomeAssistantTools(HaSession ha, Func<string, CancellationToken, Task<bool?>>? confirm, TimeZoneInfo? zone = null) =>
    [
        new HaOverviewTool(ha),
        new HaStatesTool(ha),
        new HaHistoryTool(ha, zone),
        new HaLightsTool(ha, confirm),
        new HaSceneTool(ha, confirm),
        new HaMediaTool(ha, confirm),
        new HaTodoTool(ha, confirm),
        new HaCallServiceTool(ha, confirm),
        new HaAssistTool(ha),
    ];

    /// <summary>The Home Assistant tools' names: their result's first line is the transcript's note (<see cref="HaText.Note"/>).</summary>
    public static readonly IReadOnlySet<string> HomeAssistantToolNames = new HashSet<string>(StringComparer.Ordinal)
    {
        HaOverviewTool.ToolName,
        HaStatesTool.ToolName,
        HaHistoryTool.ToolName,
        HaLightsTool.ToolName,
        HaSceneTool.ToolName,
        HaMediaTool.ToolName,
        HaTodoTool.ToolName,
        HaCallServiceTool.ToolName,
        HaAssistTool.ToolName,
    };

    /// <summary>Whether the Home Assistant group is offered (2026-09-28): the setting <c>Home Assistant tools</c> on, an http(s) URL and a readable token.</summary>
    public static bool HomeAssistantOffered(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return effective.HomeAssistantTools && HaSession.Configured(effective);
    }

    /// <summary>
    /// An asked Home Assistant call's question, on the turn task: the yes/no pane through the watcher — the advisor's confirm
    /// (<see cref="ConfirmAdvisorAsync"/>), the cursor on No, ESC a no. Null (never asked) without the pane or a watcher to run it.
    /// </summary>
    private async Task<bool?> ConfirmHomeAsync(string question, CancellationToken turnToken)
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
                DiagnosticLog.Error(ScreenPane.Category, "The Home Assistant confirm pane failed: " + Llm.Assistant.Explain(ex), ex);
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
    /// <c>/ha</c>'s argument list: <see cref="HaCommand.Complete"/> over the last snapshot. With none yet, or an old one, a read is
    /// started in the background so the next keystroke has names; the list itself never waits on the network.
    /// </summary>
    private IReadOnlyList<CompletionItem> HomeAssistantChoices(string argText)
    {
        var last = _ha.Last;
        if (HaSession.Configured(_effective()) && (last is null || _time.GetUtcNow() - last.Taken > HaSession.SnapshotAge))
        {
            _ = RefreshHomeQuietlyAsync();
        }

        return HaCommand.Complete(last, argText);
    }

    private async Task RefreshHomeQuietlyAsync()
    {
        try
        {
            _ = await _ha.SnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DiagnosticLog.Debug(HaText.Category, "The background read for /ha's list failed: " + ex.Message);
        }
    }

    /// <summary>
    /// <c>/ha [verb …]</c> (2026-09-28): <see cref="HaCommand.RunAsync"/> under a spinner, its lines as notices (a failure's as an
    /// error). The user's own hand: no policy, no pane, nothing added to the conversation.
    /// </summary>
    private async Task HandleHomeAssistantAsync(string args, CancellationToken cancellationToken)
    {
        var result = await _transcript.WithSpinnerAsync(HaText.Working, () => HaCommand.RunAsync(_ha, args, cancellationToken)).ConfigureAwait(false);
        await ShowHomeResultAsync(result, args, _transcript, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>/ha</c> under a reply (2026-09-30, the user's ask; <see cref="MidTurnPolicy(SlashCommand, bool)"/> says why it runs on
    /// the watcher): no spinner — the busy row is up already —, the lines through the flow sink, which posts them to the turn.
    /// </summary>
    private async Task HandleHomeAssistantMidTurnAsync(string args, CancellationToken cancellationToken)
    {
        HaCommandResult result;
        try
        {
            result = await HaCommand.RunAsync(_ha, args, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The turn's end took the keys back (the interrupt's listen, the exit): the call is dropped, as a pane is closed.
            DiagnosticLog.Debug(HaText.Category, "/ha under the reply was cancelled as the reply ended.");
            return;
        }

        await ShowHomeResultAsync(result, args, _flow, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A <c>/ha</c> result: <c>states</c>' lines on the info pane (2026-10-04, the user's pick: up to 80 of them), over a reply too;
    /// every other verb's lines as notices, or a failure's as errors.
    /// </summary>
    private Task ShowHomeResultAsync(HaCommandResult result, string args, INoticeSink sink, CancellationToken cancellationToken)
    {
        if (result.Paned && !result.Failed)
        {
            return ShowLinesAsync(TreeText.PaneLabel("/ha", args), result.Lines, sink, cancellationToken);
        }

        WriteHomeResult(result, sink);
        return Task.CompletedTask;
    }

    /// <summary>A <c>/ha</c> result's lines as notices, or a failure's as errors.</summary>
    private static void WriteHomeResult(HaCommandResult result, INoticeSink sink)
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
