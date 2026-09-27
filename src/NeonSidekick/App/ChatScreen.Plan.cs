using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Plans;
using NeonSidekick.Sessions;
using NeonSidekick.Settings;
using NeonSidekick.UI;

namespace NeonSidekick.App;

/// <summary>
/// Plan mode on the screen (2026-09-26, the user's ask): <c>/plan &lt;requirement&gt;</c> starts it and
/// sends the requirement; while it is on, every turn is prepared with the read-only tools alone,
/// <c>present_plan</c> and the directive (<see cref="PlanSession.Turn"/>); a presented plan is printed
/// into the reply and put to the user on <see cref="PlanApprovalMenu"/>; an approval is carried out
/// when the turn that gave it ends (<see cref="CarryOutPlanAsync"/>), in this conversation or a new
/// one. The 📝 on the strip and the input row's placeholder say it is on; the session row keeps it.
/// </summary>
internal sealed partial class ChatScreen
{
    /// <summary>What a <c>/plan</c> line asks for (<see cref="ParsePlanArgs"/>).</summary>
    public enum PlanCommand
    {
        /// <summary>Plan mode on for the argument, sent as the first message.</summary>
        Enter,

        /// <summary>More detail for the plan, sent as a message.</summary>
        Detail,

        Show,
        Approve,
        ApproveFresh,
        Cancel,

        /// <summary><c>/plan</c> with nothing, outside plan mode.</summary>
        Usage,

        /// <summary>A subcommand outside plan mode.</summary>
        NotPlanning,
    }

    /// <summary>The <c>/plan</c> list while planning: the subcommands, each with its note. Pinned.</summary>
    public static readonly IReadOnlyList<CompletionItem> PlanVerbs =
    [
        new(PlanText.ApproveWord, "approve the presented plan and carry it out here"),
        new(PlanText.ApproveWord + " " + PlanText.FreshSwitch, "approve it and carry it out in a new conversation"),
        new(PlanText.CancelWord, "leave plan mode; the plan file is kept, marked cancelled"),
        new(PlanText.ShowWord, "where the plan stands"),
    ];

    /// <summary>The /sys Tools tab's name for the group that holds <c>present_plan</c>.</summary>
    public const string PlanGroupName = "Plan";

    public const string PlanVerdictLogPrefix = "present_plan: ";

    /// <summary>
    /// What <paramref name="args"/> asks for: the subcommands (<c>show</c>, <c>approve</c>, <c>approve --fresh</c>,
    /// <c>cancel</c>, case aside) only as the whole argument; while planning, nothing is <see cref="PlanCommand.Show"/>
    /// and anything else is <see cref="PlanCommand.Detail"/>; outside it, nothing is <see cref="PlanCommand.Usage"/>, a
    /// subcommand <see cref="PlanCommand.NotPlanning"/> and anything else the requirement. Pure.
    /// </summary>
    public static PlanCommand ParsePlanArgs(string args, bool planning)
    {
        ArgumentNullException.ThrowIfNull(args);
        string words = string.Join(' ', args.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        PlanCommand? verb = words switch
        {
            "" => planning ? PlanCommand.Show : PlanCommand.Usage,
            PlanText.ShowWord => PlanCommand.Show,
            PlanText.ApproveWord => PlanCommand.Approve,
            PlanText.ApproveWord + " " + PlanText.FreshSwitch or PlanText.FreshSwitch + " " + PlanText.ApproveWord => PlanCommand.ApproveFresh,
            PlanText.CancelWord => PlanCommand.Cancel,
            _ => null,
        };
        return verb switch
        {
            { } known when planning || known == PlanCommand.Usage => known,
            not null => PlanCommand.NotPlanning,
            null => planning ? PlanCommand.Detail : PlanCommand.Enter,
        };
    }

    /// <summary>The strip with plan mode's glyph ahead of the rest while planning. Pinned.</summary>
    public static string PlanStrip(bool planning, string strip)
    {
        ArgumentNullException.ThrowIfNull(strip);
        return !planning ? strip : strip.Length == 0 ? PlanText.Glyph : PlanText.Glyph + GlyphSeparator + strip;
    }

    /// <summary>The <c>/tools</c> list as the next turn applies it: widened by every tool plan mode drops while planning (<see cref="PlanTools.Widen"/>), so <c>/sys</c> shows what is sent.</summary>
    private IReadOnlySet<string> TurnDisabled(AppSettingsData effective)
    {
        var disabled = ToolsText.DisabledSet(effective.ToolsDisabled);
        return _plan.Active
            ? PlanTools.Widen(disabled, _clockTools, _timerTools, _fileTools, _webTools, _gitTools, _shellTools, _vaultTools, _sqlTools, _comfyTools, _memoryTools, _skillTools, _sessionTools, _askTools, _mcp.Tools)
            : disabled;
    }

    /// <summary>The /sys Tools tab's groups with <c>present_plan</c>'s while planning, ahead of the questions, where the turn offers it.</summary>
    private IReadOnlyList<ToolGroup> WithPlanGroup(IReadOnlyList<ToolGroup> groups)
    {
        if (!_plan.Active || !_effective().LlmOfferTools)
        {
            return groups;
        }

        var plan = new ToolGroup(SystemPromptSummary.GroupName(PlanGroupName, 1, 1), "", [_presentPlan], true) { Label = PlanGroupName };
        int at = groups.Count > 0 && string.Equals(groups[^1].Label, "Questions", StringComparison.Ordinal) ? groups.Count - 1 : groups.Count;
        return [.. groups.Take(at), plan, .. groups.Skip(at)];
    }

    /// <summary><c>/plan …</c>, idle only (<see cref="MidTurnPolicy"/> refuses it under a reply).</summary>
    private async Task<bool> HandlePlanAsync(string args, IReadOnlyList<ImageAttachment> images, CancellationToken cancellationToken)
    {
        switch (ParsePlanArgs(args, _plan.Active))
        {
            case PlanCommand.Usage:
                _transcript.Error(PlanText.UsageError);
                return false;

            case PlanCommand.NotPlanning:
                _transcript.Error(PlanText.NotPlanningError);
                return false;

            case PlanCommand.Show:
                foreach (var line in PlanText.ShowLines(_plan))
                {
                    _transcript.Notice(line);
                }

                return false;

            case PlanCommand.Cancel:
                CancelPlan();
                return false;

            case PlanCommand.Approve or PlanCommand.ApproveFresh when _plan.Path is null:
                _transcript.Error(PlanText.NothingPresentedError);
                return false;

            case PlanCommand.Approve:
                return await CarryOutPlanAsync(fresh: false, cancellationToken).ConfigureAwait(false);

            case PlanCommand.ApproveFresh:
                return await CarryOutPlanAsync(fresh: true, cancellationToken).ConfigureAwait(false);

            case PlanCommand.Enter:
                if (!_effective().LlmOfferTools)
                {
                    _transcript.Error(PlanText.NeedsToolsError);
                    return false;
                }

                if (_session.Assistant is null)
                {
                    _transcript.Error(NoAssistantError);
                    return false;
                }

                _plan.Enter(args);
                SyncPlanChrome();
                SavePlanState();
                _transcript.Notice(PlanText.EnteredNotice);
                _transcript.User(_plan.Requirement);
                return await RunMessageAsync(_plan.Requirement, images, cancellationToken).ConfigureAwait(false);

            default:
                // More detail while planning: a message like any other, echoed since the typed line was the command.
                _transcript.User(args);
                return await RunMessageAsync(args, images, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <c>present_plan</c>'s seam, on the turn task: the saved notice and the plan itself into the reply, then the
    /// approval pane through the watcher — the <see cref="AskUserAsync"/> shape: the wait under the turn token, ESC
    /// (and a pane that fails or closes) is Keep refining, no watcher is <see cref="PlanChoice.Saved"/>.
    /// </summary>
    private async Task<PlanVerdict> PresentPlanAsync(PlanPresentation plan, CancellationToken turnToken)
    {
        _transcript.Notice(PlanText.SavedNotice(plan.Path, plan.Revision));
        _transcript.AppendDelta("\n\n" + plan.Markdown.Trim() + "\n\n");
        if (!_pane.Enabled)
        {
            DiagnosticLog.Info(AppCategory, PlanVerdictLogLine(PlanChoice.Saved));
            return new PlanVerdict(PlanChoice.Saved);
        }

        var paneToken = _paneClose?.Token ?? CancellationToken.None;
        PlanVerdict? verdict = null;
        var request = _keys.RequestPaneAsync(async () =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(paneToken, turnToken);
            try
            {
                verdict = await _planMenu.AskAsync(plan, linked.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && !linked.IsCancellationRequested)
            {
                DiagnosticLog.Error(ScreenPane.Category, "The plan approval pane failed: " + Llm.Assistant.Explain(ex), ex);
            }
        });
        try
        {
            await request.WaitAsync(turnToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!turnToken.IsCancellationRequested)
        {
            // No watcher to run the pane: saved, the user approves with /plan approve.
            DiagnosticLog.Info(AppCategory, PlanVerdictLogLine(PlanChoice.Saved));
            return new PlanVerdict(PlanChoice.Saved);
        }

        var result = verdict ?? new PlanVerdict(PlanChoice.Refine);
        DiagnosticLog.Info(AppCategory, PlanVerdictLogLine(result.Choice));
        switch (result.Choice)
        {
            case PlanChoice.Cancel:
                _transcript.Notice(PlanText.CancelledNotice(plan.Path));
                break;
            case PlanChoice.Refine when result.Feedback is { } feedback:
                _transcript.Notice(PlanText.FeedbackNotice(feedback));
                break;
        }

        return result;
    }

    /// <summary><c>present_plan: approve</c> for the --log file. Pinned.</summary>
    public static string PlanVerdictLogLine(PlanChoice choice) => PlanVerdictLogPrefix + choice.ToString().ToLowerInvariant();

    /// <summary>
    /// After a turn (<see cref="RunMessageAsync"/>): whether an approval given on the pane is to be carried out now —
    /// only when the reply ended on its own; a stopped one drops the approval with a notice, and <c>/plan approve</c>
    /// is left to start it. The placeholder follows plan mode either way (a cancel on the pane ended it).
    /// </summary>
    private bool TakePlanApproval(TurnOutcome outcome, out bool fresh)
    {
        SyncPlanChrome();
        fresh = false;
        if (_plan.Approved is not { } approval)
        {
            return false;
        }

        fresh = approval == PlanChoice.ApproveFresh;
        if (outcome == TurnOutcome.Continue && !_lastTurnCancelled)
        {
            return true;
        }

        _plan.ClearApproval();
        _transcript.Notice(PlanText.ApprovalInterruptedNotice);
        return false;
    }

    /// <summary>
    /// The approved plan carried out: the file read back (the user may have edited it since it was presented) and
    /// marked approved, plan mode off, then — with <paramref name="fresh"/> — a new conversation and the plan's text
    /// in the message, else the message naming the file; sent as a turn with every tool again. A file gone meanwhile
    /// is an error and plan mode stays on.
    /// </summary>
    private async Task<bool> CarryOutPlanAsync(bool fresh, CancellationToken cancellationToken)
    {
        if (_plan.Path is not { } path)
        {
            _transcript.Error(PlanText.NothingPresentedError);
            return false;
        }

        if (PresentPlanTool.ReadWhole(_files, path) is not { } text)
        {
            _plan.ClearApproval();
            _transcript.Error(PlanText.PlanUnreadableError(path));
            return false;
        }

        if (PresentPlanTool.MarkFile(_files, path, PlanStatus.Approved, _time.GetLocalNow(), _plan.Requirement) is { } problem)
        {
            _transcript.Warning(problem);
        }

        _plan.Exit();
        SyncPlanChrome();
        SavePlanState();
        _transcript.Notice(PlanText.ApprovedNotice(path, fresh));
        string message;
        if (fresh)
        {
            StartNewConversation();
            message = PlanText.ExecuteFreshMessage(path, PlanDocument.Body(text));
        }
        else
        {
            message = PlanText.ExecuteMessage(path);
        }

        _transcript.User(message);
        return await RunMessageAsync(message, [], cancellationToken).ConfigureAwait(false);
    }

    /// <summary><c>/plan cancel</c>: the file (when one was presented) marked cancelled, plan mode off.</summary>
    private void CancelPlan()
    {
        string? path = _plan.Path;
        if (path is not null && PresentPlanTool.MarkFile(_files, path, PlanStatus.Cancelled, _time.GetLocalNow(), _plan.Requirement) is { } problem)
        {
            _transcript.Warning(problem);
        }

        _plan.Exit();
        SyncPlanChrome();
        SavePlanState();
        _transcript.Notice(PlanText.CancelledNotice(path));
    }

    /// <summary>The conversation forgotten (<c>/new</c>, <c>/clear</c>, a profile switch …): plan mode goes with it, the file kept as it was.</summary>
    private void LeavePlanOnReset()
    {
        if (!_plan.Active)
        {
            return;
        }

        _transcript.Notice(PlanText.LeftOnResetNotice(_plan.Path));
        _plan.Exit();
        SyncPlanChrome();
    }

    /// <summary>A restored session's plan mode, said on the transcript when it was on.</summary>
    private void RestorePlan(StoredPlan? stored)
    {
        _plan.Restore(stored);
        SyncPlanChrome();
        if (_plan.Active)
        {
            foreach (var line in PlanText.ShowLines(_plan))
            {
                _transcript.Notice(line);
            }
        }
    }

    /// <summary>The input row's placeholder as plan mode stands.</summary>
    private void SyncPlanChrome() => _pane.Placeholder = _plan.Active ? PlanText.Placeholder : InputPlaceholder;

    /// <summary>Plan mode into the session row at once (an enter, a cancel, an approval), not only at the turn's end; nothing without a row.</summary>
    private void SavePlanState()
    {
        if (_session.Assistant is { } assistant)
        {
            SaveSessionHistory(assistant);
        }
    }
}
