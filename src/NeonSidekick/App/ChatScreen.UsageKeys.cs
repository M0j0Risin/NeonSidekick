using NeonSidekick.Diagnostics;
using NeonSidekick.Llm;
using NeonSidekick.Mcp;
using NeonSidekick.UI;

namespace NeonSidekick.App;

// ── /usage › Context's rows: Space and Enter (2026-11-27, plan G3) ───────────────────────────────────────────────────────────────────────
// The Context tab's breakdown carries a row cursor (InfoTab.Stops). Space switches a tool group or an MCP server and the pane stays, its
// facts read again; Enter closes the pane on the place the row is changed (the group in /tools, /mcp, the Prompt or Tools tab, /compact
// after a yes, /settings' search for LLM preserve thinking), then /usage opens again on the same row. A visit is one ShowUsagePaneAsync:
// the groups switched off during it stay listed, dimmed, until it ends, and what the flips owe (a reconnect) is carried out once at its end.

internal sealed partial class ChatScreen
{
    // The visit's state: the facts the tab reads (reset after a flip), the groups switched off from it, the rows the stops are, the last
    // layout's start, the row to come back to after a jump, and the changes the flips owe.
    private Lazy<ContextFacts>? _usageFacts;
    private readonly List<ContextToolGroup> _usageSwitchedOff = [];
    private IReadOnlyList<ContextRow> _usageStopRows = [];
    private UsageText.StatisticsLayoutResult? _usageLayout;
    private string? _usageReturnLabel;
    private SettingsChanges _usageChanges;

    /// <summary>The facts the Context tab reads this visit, with the groups switched off from it in their places.</summary>
    private ContextFacts UsageFactsNow()
    {
        _usageFacts ??= new Lazy<ContextFacts>(() => ContextFacts() with { SwitchedOff = [.. _usageSwitchedOff] });
        return _usageFacts.Value;
    }

    /// <summary>The Context tab's content, its stops kept for the keys (the pane reads <see cref="InfoTab.Stops"/> after this build).</summary>
    private Spectre.Console.Rendering.IRenderable UsageContextTab(Sessions.ModelBests bests, string? model)
    {
        var layout = UsageText.StatisticsLayout(_session.Usage, UsageFactsNow(), _session.Usage.Replies, bests, model, UsageLineWidth());
        _usageLayout = layout;
        _usageStopRows = layout.StopRows;
        return layout.Content;
    }

    /// <summary>The stop the cursor starts on: the row a jump left from, else the layout's (the first tool group).</summary>
    private int UsageInitialStop()
    {
        if (_usageReturnLabel is { } label && _usageStopRows.ToList().FindIndex(r => r.Label == label) is int back and >= 0)
        {
            return back;
        }

        return Math.Max(0, _usageLayout?.InitialStop ?? 0);
    }

    /// <summary>
    /// <c>/usage</c>'s pane, one visit (2026-11-27): opened on <paramref name="tab"/>, and after each Enter on a row the place it opens, then the
    /// Context tab again on that row; a profile switch there ends the visit. At its end the flips' changes are carried out: at once at the idle
    /// line, handed over under a reply (<see cref="HandOver"/>). Since the last-3 review's #16 (2026-11-27) they are carried out in the
    /// <c>finally</c>, so a jump that threw still applies the flips made before it; at the idle line a failure there is logged, never thrown
    /// over the first.
    /// </summary>
    private async Task ShowUsagePaneAsync(int tab, ReplyRun reply, CancellationToken cancellationToken, int? initialLine = null)
    {
        _usageSwitchedOff.Clear();
        _usageChanges = SettingsChanges.None;
        _usageReturnLabel = null;
        try
        {
            while (true)
            {
                _usageFacts = null;
                await _info.ShowAsync(UsageText.Label, UsageTabs(reply, cancellationToken), tab, cancellationToken, initialLine: initialLine).ConfigureAwait(false);
                if (_info.ChosenStop is not { } chosen || chosen.Stop >= _usageStopRows.Count)
                {
                    break;
                }

                var row = _usageStopRows[chosen.Stop];
                _usageReturnLabel = row.Label;
                initialLine = null;
                if (await UsageJumpAsync(row, reply, cancellationToken).ConfigureAwait(false) is not { } next)
                {
                    break;
                }

                tab = next;
            }
        }
        finally
        {
            _usageFacts = null;
            _usageLayout = null;
            _usageStopRows = [];
            _usageReturnLabel = null;
            _usageSwitchedOff.Clear();
            var owed = _usageChanges;
            _usageChanges = SettingsChanges.None;
            if (owed != SettingsChanges.None)
            {
                if (reply.Ended.CanBeCanceled)
                {
                    HandOver(reply, owed);
                }
                else if (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await ApplySettingsChangesAsync(owed, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        DiagnosticLog.Error(UsageCategory, UsageOwedFailedLogLine(ex.Message), ex);
                    }
                }
            }
        }
    }

    private const string UsageCategory = "Usage";

    /// <summary>Logged when a visit's owed changes failed at its end (2026-11-27, the last-3 review's #16).</summary>
    internal static string UsageOwedFailedLogLine(string reason) => "/usage: the changes the Context tab's switches owe could not be carried out: " + reason;

    /// <summary>Where the screen's lines go: the flow sink under a reply (the watcher's), the transcript at the idle line.</summary>
    private INoticeSink UsageSink(ReplyRun reply) => reply.Ended.CanBeCanceled ? _flow : _transcript;

    /// <summary>
    /// Space or Enter on a Context row (<see cref="InfoTab.StopKey"/>). Space on a tool group flips its switch (an MCP server's row that server
    /// alone, refused under a reply as /mcp refuses it), the pane staying with fresh facts; a switch with more than on and off, or none, says
    /// so. Enter closes the pane for <see cref="UsageJumpAsync"/>, except on a row with nowhere to go. <paramref name="cancellationToken"/> is
    /// the pane's (2026-11-27, the last-3 review's #3, #41): a cancel during an MCP connect or a question stays quietly, and the pane's next
    /// read sees the token and closes, as it does for any cancel.
    /// </summary>
    private async Task<InfoStopResult> UsageStopKeyAsync(int stop, InfoStopKey key, ReplyRun reply, CancellationToken cancellationToken)
    {
        try
        {
            return await UsageStopKeyCoreAsync(stop, key, reply, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return InfoStopResult.Stay;
        }
    }

    private async Task<InfoStopResult> UsageStopKeyCoreAsync(int stop, InfoStopKey key, ReplyRun reply, CancellationToken cancellationToken)
    {
        if (stop < 0 || stop >= _usageStopRows.Count)
        {
            return InfoStopResult.Stay;
        }

        var row = _usageStopRows[stop];
        if (key == InfoStopKey.Enter)
        {
            return row.Group is not null || row.Label is UsageText.PromptLabel or UsageText.ToolsLabel or UsageText.HistoryLabel or UsageText.ThinkingLabel
                ? InfoStopResult.Close
                : InfoStopResult.Stay;
        }

        if (row.Group is not { } group)
        {
            return InfoStopResult.Stay;
        }

        var sink = UsageSink(reply);
        if (group.McpServer is { } server)
        {
            if (reply.Running)
            {
                sink.Notice(SettingsMenu.NotWhileReplyRunsNotice);
                return InfoStopResult.Stay;
            }

            bool live = await FlipMcpServerAsync(server, sink, cancellationToken).ConfigureAwait(false);
            Switched(group, live);
            return InfoStopResult.Stay;
        }

        if (group.Switch == SettingsField.McpServers)
        {
            sink.Notice(UsageText.NoSwitchNotice(group.Label, UsageText.McpCommand));
            return InfoStopResult.Stay;
        }

        if (group.Switch is not { } field)
        {
            sink.Notice(UsageText.NoSwitchNotice(group.Label, UsageText.ToolsCommand));
            return InfoStopResult.Stay;
        }

        if (!SettingsMenu.IsToggle(field))
        {
            sink.Notice(UsageText.NotASwitchNotice(group.Label));
            return InfoStopResult.Stay;
        }

        if (reply.Running && SettingsMenu.RefusedMidTurn(field))
        {
            sink.Notice(SettingsMenu.NotWhileReplyRunsNotice);
            return InfoStopResult.Stay;
        }

        if (await _menu.FlipAsync(field, cancellationToken).ConfigureAwait(false))
        {
            if (SettingsMenu.IsLlmField(field))
            {
                _usageChanges |= SettingsChanges.Llm;
            }

            Switched(group, live: row.Off);   // a group that was off is on now
        }

        return InfoStopResult.Stay;
    }

    /// <summary>
    /// A group switched from the tab: listed dimmed when it isn't <paramref name="live"/> (switched off, or an MCP server whose connect failed,
    /// 2026-11-27, the last-3 review's #41), dropped from the list when it is; the facts read again.
    /// </summary>
    private void Switched(ContextToolGroup group, bool live)
    {
        _usageSwitchedOff.RemoveAll(g => g.Label == group.Label);
        if (!live)
        {
            _usageSwitchedOff.Add(group);
        }

        _usageFacts = null;
    }

    /// <summary>
    /// An MCP server on or off from the Context tab, as /mcp's Servers tab flips it: saved, then disconnected or connected. Whether the server
    /// is live after it (2026-11-27, the last-3 review's #41): false after a stop; after a start, whether it connected. A start that failed or
    /// was cancelled saves the server off again, so the dimmed row is true to the saved state and Space tries the connect again; a cancel
    /// is rethrown after that.
    /// </summary>
    private async Task<bool> FlipMcpServerAsync(string name, INoticeSink sink, CancellationToken cancellationToken)
    {
        bool on = ToolsText.DisabledSet(_settings.Current.McpServersDisabled).Contains(name);
        _settings.Update(d => d.McpServersDisabled = ToolsText.Flip(d.McpServersDisabled, name));
        sink.Notice(McpText.ServerFlippedNotice(name, on));
        if (!on)
        {
            await _mcp.DisconnectServerAsync(name).ConfigureAwait(false);
            sink.Notice(McpText.StoppedNotice(name));
            return false;
        }

        void Undo() => _settings.Update(d => d.McpServersDisabled = ToolsText.Flip(d.McpServersDisabled, name));
        sink.Notice(McpText.ConnectingNotice(name));
        bool connected;
        try
        {
            connected = await _mcp.ConnectServerAsync(name, _effective(), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Undo();
            throw;
        }

        var after = _mcp.Servers.FirstOrDefault(s => s.Name == name);
        if (connected && after is { State: McpState.Connected })
        {
            sink.Notice(McpText.ConnectedNotice(name, after.Tools.Count));
            return true;
        }

        Undo();
        sink.Error(McpText.FailedNotice(name, after?.Detail ?? ""));
        return false;
    }

    /// <summary>
    /// What a jump's pane owes, carried out (2026-11-27, the last-3 review's #9, #16, #28): handed over under a reply (null on a profile
    /// switch, which ends the visit); at the idle line through <see cref="ApplyMenuChangesAsync"/>, a profile switch ending the visit with the
    /// old profile's owed reconnect dropped. The tab to open again, or null.
    /// </summary>
    private async Task<int?> UsageAfterJumpAsync(SettingsChanges changes, ReplyRun reply, EmbeddedLlm.EmbeddedModel? embedded, CancellationToken cancellationToken)
    {
        if (reply.Ended.CanBeCanceled)
        {
            HandOver(reply, changes, embedded);
            return changes.HasFlag(SettingsChanges.Profile) ? null : UsageText.ContextTabIndex;
        }

        if (changes.HasFlag(SettingsChanges.Profile))
        {
            _usageChanges = SettingsChanges.None;   // the old profile's reconnect is moot: the switch reconnects everything
        }

        await ApplyMenuChangesAsync(changes, embedded, cancellationToken).ConfigureAwait(false);
        return changes.HasFlag(SettingsChanges.Profile) ? null : UsageText.ContextTabIndex;
    }

    /// <summary>
    /// Enter's place for a row (2026-11-27), the pane closed: a group's switch in /tools (the whole pane for a group without one), /mcp for an
    /// MCP row, the Prompt or Tools tab, /compact after a yes (deferred to the idle line under a reply), /settings' search on <c>LLM preserve
    /// thinking</c> (never <c>open:</c>, which would flip it). The tab to open again, or null when a profile switch ended the visit.
    /// </summary>
    private async Task<int?> UsageJumpAsync(ContextRow row, ReplyRun reply, CancellationToken cancellationToken)
    {
        if (row.Group is { } group)
        {
            // Every jump's changes go one way (2026-11-27, the last-3 review's #9, #28): UsageAfterJumpAsync.
            if (group.McpServer is not null || group.Switch == SettingsField.McpServers)
            {
                var mcp = await _mcpMenu.ShowAsync(cancellationToken, reply).ConfigureAwait(false);
                return await UsageAfterJumpAsync(mcp, reply, null, cancellationToken).ConfigureAwait(false);
            }

            if (group.Switch is { } field)
            {
                var changes = await _toolsMenu.ShowSwitchAsync(field, cancellationToken, reply).ConfigureAwait(false);
                return await UsageAfterJumpAsync(changes, reply, null, cancellationToken).ConfigureAwait(false);
            }

            var whole = await _toolsMenu.ShowAsync(cancellationToken, reply).ConfigureAwait(false);
            return await UsageAfterJumpAsync(whole, reply, null, cancellationToken).ConfigureAwait(false);
        }

        switch (row.Label)
        {
            case UsageText.PromptLabel:
                return UsageText.PromptTabIndex;
            case UsageText.ToolsLabel:
                return UsageText.ToolsTabIndex;
            case UsageText.HistoryLabel:
                if (await ConfirmAsync(UsageText.CompactQuestion, cancellationToken).ConfigureAwait(false))
                {
                    if (_turnRunning)
                    {
                        // A yes under a reply waits for its end as a typed /compact does (MidTurnClass.Deferred): the same notice, queued or pending.
                        var submitted = new SubmittedLine(UsageText.CompactCommand, UsageText.CompactCommand, [], UsageText.CompactCommand);
                        Post(() => _transcript.Notice(MidTurnDeferredNotice(UsageText.CompactCommand)));
                        if (_botChatRunning || !QueueLine(submitted))
                        {
                            Pend(submitted);
                        }
                    }
                    else
                    {
                        await CompactAsync(null, null, cancellationToken).ConfigureAwait(false);
                    }
                }

                return UsageText.ContextTabIndex;
            case UsageText.ThinkingLabel:
                // Through UsageAfterJumpAsync too (2026-11-27, the last-3 review's #28): a profile switch from /settings ends the visit.
                string words = SettingsMenu.FieldName(SettingsField.LlmPreserveThinking);
                var settings = await _menu.ShowAsync(cancellationToken, reply, words: words).ConfigureAwait(false);
                return await UsageAfterJumpAsync(settings, reply, reply.Running ? null : _menu.TakePendingEmbeddedModel(), cancellationToken).ConfigureAwait(false);
            default:
                return UsageText.ContextTabIndex;
        }
    }
}
