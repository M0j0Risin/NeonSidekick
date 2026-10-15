using Microsoft.Extensions.AI;
using NeonSidekick.EventLog;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Settings;

namespace NeonSidekick.App;

// ── Event logs (2026-10-15, the event-log plan) ──────────────────────────────

internal sealed partial class ChatScreen
{
    /// <summary>The event logs' door the tools and <c>/eventlog</c> share: the ticked computers, the working directory's <c>.evtx</c> files.</summary>
    private readonly EventLogAccess _eventLog;

    /// <summary>The four event-log tools, built once over <see cref="_eventLog"/>; offered while <see cref="EventLogOffered"/> says so.</summary>
    private readonly IReadOnlyList<AIFunction> _eventLogTools;

    /// <summary>
    /// The bots' twins (2026-10-15, the plan's "who sees what"): the same four reading every computer redacted, whatever its <c>detail</c> —
    /// <see cref="BotGeneralTools"/> offers these in the main chat's place, so <c>full</c> stays the main chat's.
    /// </summary>
    private readonly IReadOnlyList<AIFunction> _eventLogBotTools;

    /// <summary>
    /// The event-log tools (2026-10-15): <c>eventlog_logs</c>, <c>eventlog_search</c>, <c>eventlog_summary</c> and <c>eventlog_event</c>, every
    /// one a read; <paramref name="forceRedacted"/> builds the twins that read every computer redacted. Shared with headless.
    /// </summary>
    public static IReadOnlyList<AIFunction> EventLogTools(EventLogAccess access, Func<AppSettingsData> effective, bool forceRedacted = false) =>
    [
        new EventLogLogsTool(access, effective, forceRedacted),
        new EventLogSearchTool(access, effective, forceRedacted),
        new EventLogSummaryTool(access, effective, forceRedacted),
        new EventLogEventTool(access, effective, forceRedacted),
    ];

    /// <summary>The event-log tools' names: their result's first line is the transcript's note (<see cref="EventLogText.Note"/>); an advisor reads a stand-in for their results.</summary>
    public static readonly IReadOnlySet<string> EventLogToolNames = new HashSet<string>(StringComparer.Ordinal)
    {
        EventLogLogsTool.ToolName,
        EventLogSearchTool.ToolName,
        EventLogSummaryTool.ToolName,
        EventLogEventTool.ToolName,
    };

    /// <summary>
    /// Whether the event-log group is offered (2026-10-15): the setting <c>Event log tools</c> on, on Windows, with a reader. With nothing
    /// ticked it still is, for the working directory's <c>.evtx</c> files alone (<c>/sys</c> says so).
    /// </summary>
    public static bool EventLogOffered(AppSettingsData effective, EventLogAccess access)
    {
        ArgumentNullException.ThrowIfNull(effective);
        ArgumentNullException.ThrowIfNull(access);
        return effective.EventLogTools && OperatingSystem.IsWindows() && access.System is not null;
    }

    /// <summary>The wizard's Test (2026-10-15): <see cref="EventLogAccess.TestAsync"/> through the screen's reader.</summary>
    private Task<(bool Ok, string Text)> TestEventLogComputerAsync(EventLogComputer computer, CancellationToken cancellationToken) =>
        EventLogAccess.TestAsync(_eventLog.System, computer, _files, _time, cancellationToken);

    /// <summary>An event's ask from <c>/eventlog</c>'s pane under a reply (2026-10-15): kept for the hook to draft after the pane phase.</summary>
    private string? _eventLogDraft;

    /// <summary>
    /// <c>/eventlog</c>'s argument list (2026-10-15): the logs, the working directory's <c>.evtx</c> files and the term keys; the ticked
    /// computers after <c>computer:</c>. Empty off Windows, where <c>/eventlog</c> says it needs Windows.
    /// </summary>
    private IReadOnlyList<UI.CompletionItem> EventLogChoices(string argText)
    {
        if (!OperatingSystem.IsWindows() || _eventLog.System is null)
        {
            return [];
        }

        var files = EventLogLogsTool.EvtxFiles(_files).Files.Select(f => f.Path).ToList();
        return EventLogCommand.Complete(argText, _eventLog.Catalog().Computers.Select(c => c.Name).ToList(), files);
    }

    /// <summary>
    /// <c>/eventlog [line]</c> (2026-10-15, the event-log plan's phase 2): on the pane, <see cref="EventLogMenu"/> — an <c>ask</c> drafted at once
    /// at the idle line, kept for after the pane phase under a reply (<paramref name="onWatcher"/>); without the pane, or from the Web UI's
    /// page, <see cref="EventLogCommand.RunAsync"/>'s lines on the list pane (a card there), a failure as an error. Works with the tools off.
    /// </summary>
    private async Task HandleEventLogAsync(string args, UI.INoticeSink sink, CancellationToken cancellationToken, bool onWatcher = false)
    {
        if (!OperatingSystem.IsWindows() || _eventLog.System is null)
        {
            sink.Error(EventLogText.NeedsWindows);
            return;
        }

        var effective = _effective();
        int limit = Math.Clamp(effective.EventLogMaxEvents, AppSettingsData.MinEventLogMaxEvents, AppSettingsData.MaxEventLogMaxEvents);
        int scanLimit = Math.Clamp(effective.EventLogScanLimit, AppSettingsData.MinEventLogScanLimit, AppSettingsData.MaxEventLogScanLimit);
        if (_pane.Enabled && !FromWebNow)
        {
            var (line, error) = EventLogCommand.Parse(args, _time.GetUtcNow(), _time.LocalTimeZone, limit);
            if (line is null)
            {
                sink.Error(error!);
                return;
            }

            var menu = new EventLogMenu(_eventLog, _effective, sink, _menuPane, _info, _copy, () => _pane.Profile.Width);
            if (await menu.ShowAsync(line, cancellationToken).ConfigureAwait(false) is { } draft)
            {
                if (onWatcher)
                {
                    _eventLogDraft = draft;
                }
                else
                {
                    FillDraft(draft, keepOld: true);
                }
            }

            return;
        }

        var result = onWatcher
            ? await EventLogCommand.RunAsync(_eventLog, args, limit, scanLimit, cancellationToken).ConfigureAwait(false)
            : await _transcript.WithSpinnerAsync(EventLogText.Working, () => EventLogCommand.RunAsync(_eventLog, args, limit, scanLimit, cancellationToken)).ConfigureAwait(false);
        if (result.Failed)
        {
            foreach (string failed in result.Lines)
            {
                sink.Error(failed);
            }

            return;
        }

        await ShowLinesAsync(TreeText.PaneLabel(EventLogCommand.Word, args), result.Lines, sink, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>After a pane phase under a reply: an event's ask (2026-10-15) goes on the draft now, as <see cref="TakePromptPick"/>'s prompt does.</summary>
    private bool TakeEventLogDraft(bool typed)
    {
        if (_eventLogDraft is not { } draft)
        {
            return true;
        }

        _eventLogDraft = null;
        FillDraft(draft, keepOld: !typed);
        return !typed;
    }
}
