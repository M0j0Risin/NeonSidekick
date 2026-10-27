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
    /// The Home Assistant tools (2026-09-28): the four reads (overview, states, history and, since 2026-10-27, <c>ha_camera</c>), the four
    /// typed acts (lights, scene, media, to-do), the generic service call and Assist — the acts and Assist behind <paramref name="confirm"/>
    /// for what the policy asks about (null where nothing can ask: headless). <c>ha_camera</c> keeps its stills in <paramref name="images"/>,
    /// and is left out without one. Shared with headless.
    /// </summary>
    public static IReadOnlyList<AIFunction> HomeAssistantTools(HaSession ha, Func<string, CancellationToken, Task<bool?>>? confirm, TimeZoneInfo? zone = null, Files.WorkingDirectory? images = null) =>
    [
        new HaOverviewTool(ha),
        new HaStatesTool(ha),
        new HaHistoryTool(ha, zone),
        .. images is null ? [] : new AIFunction[] { new HaCameraTool(ha, images, zone) },
        new HaLightsTool(ha, confirm),
        new HaSceneTool(ha, confirm),
        new HaMediaTool(ha, confirm),
        new HaTodoTool(ha, confirm),
        new HaCallServiceTool(ha, confirm),
        new HaAssistTool(ha, confirm),
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

    /// <summary>
    /// The Home Assistant tools a turn leaves out and why, as notes (2026-10-27, <c>ha_camera</c>; <see cref="NetworkToolNotes"/>'s way, so
    /// <c>/tools</c>, <c>/sys</c> and <c>neon_help</c>'s live line say "not offered: …" rather than "switched off on /tools"): <c>ha_camera</c>
    /// with no codecs to decode a picture (<see cref="HaText.NoPictureSupportReason"/>), or under a model that reads none
    /// (<see cref="ToolsText.BlindReason"/>, the embedded server without vision). Pure.
    /// </summary>
    public static Dictionary<string, string> HomeToolNotes(bool codecs, bool blind)
    {
        var notes = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!codecs || blind)
        {
            notes[HaCameraTool.ToolName] = SystemPromptSummary.NotOffered(!codecs ? HaText.NoPictureSupportReason : ToolsText.BlindReason);
        }

        return notes;
    }

    /// <summary>The screen's notes: <see cref="Files.ImageCodecs.Available"/> and the embedded server's vision, <see cref="ModelSeesPictures"/>' halves.</summary>
    private Dictionary<string, string> HomeToolNotes() => HomeToolNotes(Files.ImageCodecs.Available, _session.EmbeddedServer is { Vision: false });

    /// <summary>What a turn may offer of the Home Assistant tools: every one but those <paramref name="notes"/> leave out (<see cref="NetworkToolsFor"/>'s shape). Pure.</summary>
    public static IReadOnlyList<AIFunction> HomeToolsFor(IReadOnlyList<AIFunction> tools, IReadOnlyDictionary<string, string> notes)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(notes);
        return notes.Count == 0 ? tools : tools.Where(t => !notes.ContainsKey(t.Name)).ToList();
    }

    /// <summary>Whether the Home Assistant group is offered (2026-09-28): the setting <c>Home Assistant tools</c> on, an http(s) URL and a readable token.</summary>
    public static bool HomeAssistantOffered(AppSettingsData effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return effective.HomeAssistantTools && HaSession.Configured(effective);
    }

    /// <summary>
    /// An asked Home Assistant call's question, on the turn task: the yes/no pane through the watcher — an advisor's confirm
    /// (<see cref="ConfirmNeonAdvisorAsync"/>), the cursor on No, ESC a no. Null (never asked) without the pane or a watcher to run it.
    /// </summary>
    private async Task<bool?> ConfirmHomeAsync(string question, CancellationToken turnToken)
    {
        // On the PC's pane and the Web UI's page at once (2026-10-14, ConfirmBothAsync), the first answer winning.
        return await ConfirmBothAsync(question, "The Home Assistant confirm pane failed: ", turnToken).ConfigureAwait(false);
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
        // /ha stills is the stills' folder, not the server (2026-10-27): taken first, so it works with no URL or key.
        if (HaStillsCommand.TryParse(args, out var stills))
        {
            await HandleHomeStillsAsync(stills, cancellationToken).ConfigureAwait(false);
            return;
        }

        // A change from the Web UI's page asks first on the page and here (later on 2026-10-14); the PC's own hand never does.
        var ask = WebConfirmHook is { } confirm ? new HaAsk(confirm, _effective().HomeAssistantSafeServices) : null;
        var result = await _transcript.WithSpinnerAsync(HaText.Working, () => HaCommand.RunAsync(_ha, args, cancellationToken, ask)).ConfigureAwait(false);
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
            DiagnosticLog.Debug(HaText.Category, "/ha under the reply was canceled as the reply ended.");
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
