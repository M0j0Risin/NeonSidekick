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

    /// <summary>
    /// A Mac's door (2026-10-18, the Mac event-log plan): the unified log and the working directory's <c>.logarchive</c> folders, its reader
    /// null off a Mac (or in a test that gave none), when the Windows door above is the one in use.
    /// </summary>
    private readonly MacLogAccess _macLog;

    /// <summary>The four event-log tools, built once over <see cref="_eventLog"/> (or <see cref="_macLog"/> on a Mac); offered while <see cref="EventLogOffered"/> says so.</summary>
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

    /// <summary>
    /// The event-log tools on a Mac (2026-10-18, the Mac event-log plan): the same four names over the unified log, every one a read;
    /// <paramref name="forceRedacted"/> builds the twins that read redacted. Shared with headless.
    /// </summary>
    public static IReadOnlyList<AIFunction> MacLogTools(MacLogAccess access, Func<AppSettingsData> effective, bool forceRedacted = false) =>
    [
        new MacLogLogsTool(access, effective, forceRedacted),
        new MacLogSearchTool(access, effective, forceRedacted),
        new MacLogSummaryTool(access, effective, forceRedacted),
        new MacLogEventTool(access, effective, forceRedacted),
    ];

    /// <summary>
    /// Whether <c>eventlog.json</c>'s <c>local</c> says <c>full</c> (2026-10-18): the detail a Mac reads its own logs under, redacted
    /// unless it does. Read afresh at each call, as the Windows door reads its computers.
    /// </summary>
    public static bool LocalFull(string profileDirectory, string storageDirectory) =>
        EventLogConfigFile.LoadCatalog(profileDirectory, storageDirectory).Computers.FirstOrDefault(c => c.IsLocal)?.Config.Full ?? false;

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

    /// <summary>
    /// <see cref="EventLogOffered(AppSettingsData, EventLogAccess)"/>, or on a Mac with a unified-log reader (2026-10-18, the Mac event-log
    /// plan): the switch on and a reader for either.
    /// </summary>
    public static bool EventLogOffered(AppSettingsData effective, EventLogAccess access, MacLogAccess? mac)
    {
        ArgumentNullException.ThrowIfNull(effective);
        return EventLogOffered(effective, access) || (effective.EventLogTools && mac?.Log is not null);
    }

    /// <summary>Whether this machine has event logs to read: Windows' through its reader, or a Mac's unified log (2026-10-18).</summary>
    private bool EventLogsHere => (OperatingSystem.IsWindows() && _eventLog.System is not null) || _macLog.Log is not null;

    /// <summary>The wizard's Test (2026-10-15): <see cref="EventLogAccess.TestAsync"/> through the screen's reader.</summary>
    private Task<(bool Ok, string Text)> TestEventLogComputerAsync(EventLogComputer computer, CancellationToken cancellationToken) =>
        EventLogAccess.TestAsync(_eventLog.System, computer, _files, _time, cancellationToken);

    /// <summary>An event's ask from <c>/eventlog</c>'s pane under a reply (2026-10-15): kept for the hook to draft after the pane phase.</summary>
    private string? _eventLogDraft;

    /// <summary>
    /// <c>/eventlog</c>'s argument list (2026-10-15): the logs, the working directory's <c>.evtx</c> files and the term keys; the ticked
    /// computers after <c>computer:</c>. Empty off Windows, where <c>/eventlog</c> says there are none to read.
    /// </summary>
    private IReadOnlyList<UI.CompletionItem> EventLogChoices(string argText)
    {
        if (_macLog.Log is not null)
        {
            return MacLogCommand.Complete(argText, _macLog.Archives());   // a Mac's logs (2026-10-18, the Mac event-log plan's phase 4)
        }

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
        if (_macLog.Log is not null)
        {
            await HandleMacLogAsync(args, sink, cancellationToken, onWatcher).ConfigureAwait(false);
            return;
        }

        if (!OperatingSystem.IsWindows() || _eventLog.System is null)
        {
            sink.Error(EventLogText.NoneHere);
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

    /// <summary>
    /// <c>/eventlog [line]</c> on a Mac (2026-10-18, the Mac event-log plan's phase 4; <see cref="HandleEventLogAsync"/>'s shape): on the
    /// pane, <see cref="MacLogMenu"/>; without the pane, or from the Web UI's page, <see cref="MacLogCommand.RunAsync"/>'s lines on the list
    /// pane. An ask is drafted at once at the idle line, kept for after the pane phase under a reply. Works with the tools off.
    /// </summary>
    private async Task HandleMacLogAsync(string args, UI.INoticeSink sink, CancellationToken cancellationToken, bool onWatcher)
    {
        var effective = _effective();
        int limit = Math.Clamp(effective.EventLogMaxEvents, AppSettingsData.MinEventLogMaxEvents, AppSettingsData.MaxEventLogMaxEvents);
        int scanLimit = Math.Clamp(effective.EventLogScanLimit, AppSettingsData.MinEventLogScanLimit, AppSettingsData.MaxEventLogScanLimit);
        if (_pane.Enabled && !FromWebNow)
        {
            var (line, error) = MacLogCommand.Parse(args, _time.GetUtcNow(), _time.LocalTimeZone);
            if (line is null)
            {
                sink.Error(error!);
                return;
            }

            var menu = new MacLogMenu(_macLog, _effective, sink, _menuPane, _info, _copy, () => _pane.Profile.Width);
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
            ? await MacLogCommand.RunAsync(_macLog, args, limit, scanLimit, cancellationToken).ConfigureAwait(false)
            : await _transcript.WithSpinnerAsync(EventLogText.Working, () => MacLogCommand.RunAsync(_macLog, args, limit, scanLimit, cancellationToken)).ConfigureAwait(false);
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
