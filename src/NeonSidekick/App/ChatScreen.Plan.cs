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

        /// <summary><c>/plan open &lt;name&gt;</c> (2026-09-26, round two): an old plan picked up; the name is the rest.</summary>
        Open,

        /// <summary><c>/plan open</c> alone: the plans under <c>.neon/plans/</c>.</summary>
        List,

        /// <summary><c>/plan save [name]</c> while planning: the last reply kept as the plan.</summary>
        Save,

        /// <summary>
        /// <c>/plan gaps [model|advisor|any] [passes]</c> while planning (2026-10-13): the gap-closing loop; the words are the rest
        /// (<see cref="PlanText.ParseGapsArgs"/>), a pass count capping this run instead of <c>LLM plan gap passes</c> (2026-10-17).
        /// </summary>
        CloseGaps,

        /// <summary><c>/plan gaps &lt;word&gt;</c> with a word that names no reviewer; the word is the rest.</summary>
        UnknownReviewer,

        /// <summary><c>/plan gaps 25</c> (2026-10-17): a pass count out of range; the number as typed is the rest.</summary>
        BadGapPasses,

        /// <summary><c>/plan chat</c> alone (2026-10-17): plan mode on to talk a plan through first, or talking again while planning; nothing sent.</summary>
        Chat,

        /// <summary><c>/plan ready</c> alone while planning (2026-10-17): the plan asked for now, or its pane opened on the current revision.</summary>
        Ready,
    }

    /// <summary>The <c>/plan</c> list while planning: the subcommands, each with its note. Pinned.</summary>
    public static readonly IReadOnlyList<CompletionItem> PlanVerbs =
    [
        new(PlanText.ApproveWord, "approve the presented plan and carry it out here"),
        new(PlanText.ApproveWord + " " + PlanText.FreshSwitch, "approve it and carry it out in a new conversation"),
        new(PlanText.CancelWord, "leave plan mode; the plan file is kept, marked canceled"),
        new(PlanText.OpenWord, "pick up a plan under .neon/plans/: /plan open <name>, or alone to list them"),
        new(PlanText.SaveWord, "keep the last reply as the plan and ask for approval: /plan save [name]"),
        new(PlanText.GapsWord, "review the presented plan pass after pass until no gaps are left: /plan gaps [model|advisor|any|confirm] [passes]"),
        new(PlanText.ChatWord, "talk the plan through again; the model won't present until you ask"),
        new(PlanText.ReadyWord, "ask for the plan now, or open its approval pane once one is presented"),
        new(PlanText.ShowWord, "where the plan stands"),
    ];

    /// <summary>The <c>/plan gaps</c> list (2026-10-13): the reviewer words, each with its note. Pinned.</summary>
    public static readonly IReadOnlyList<CompletionItem> PlanGapsReviewers =
    [
        new(PlanText.GapsWord + " " + PlanText.ReviewerWord(PlanReviewer.Model), "the planning model reviews alone"),
        new(PlanText.GapsWord + " " + PlanText.ReviewerWord(PlanReviewer.Advisor), "an advisor whose role fits best finds the gaps each pass"),
        new(PlanText.GapsWord + " " + PlanText.ReviewerWord(PlanReviewer.Any), "the model reviews, asking advisors only where a role fits"),
        new(PlanText.GapsWord + " " + PlanText.ReviewerWord(PlanReviewer.Confirm), "the model closes the gaps; an advisor checks once it finds none"),
    ];

    /// <summary>The <c>/plan</c> list outside plan mode: <c>open</c> and <c>chat</c> (2026-10-17) alone — the rest is a requirement, free text.</summary>
    public static readonly IReadOnlyList<CompletionItem> PlanVerbsIdle =
    [
        new(PlanText.OpenWord, "pick up a plan under .neon/plans/: /plan open <name>, or alone to list them"),
        new(PlanText.ChatWord, "talk a plan through before it's written; /plan ready when you want it"),
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
    public static PlanCommand ParsePlanArgs(string args, bool planning) => ParsePlanArgs(args, planning, out _);

    /// <summary>
    /// <see cref="ParsePlanArgs(string, bool)"/> with the two words that take an argument (2026-09-26, round two):
    /// <c>open</c> — in or out of plan mode — is <see cref="PlanCommand.List"/> alone and <see cref="PlanCommand.Open"/>
    /// with a name (<paramref name="rest"/>, as typed; the handler falls back to a requirement or detail when no plan has
    /// that name and it is several words); <c>save</c> while planning is <see cref="PlanCommand.Save"/> alone or with one
    /// word, the name. Pure.
    /// </summary>
    public static PlanCommand ParsePlanArgs(string args, bool planning, out string rest)
    {
        ArgumentNullException.ThrowIfNull(args);
        rest = "";
        string trimmed = args.Trim();
        int space = trimmed.IndexOfAny([' ', '\t']);
        string first = (space < 0 ? trimmed : trimmed[..space]).ToLowerInvariant();
        string after = space < 0 ? "" : trimmed[(space + 1)..].Trim();
        if (first == PlanText.OpenWord)
        {
            rest = after;
            return after.Length == 0 ? PlanCommand.List : PlanCommand.Open;
        }

        if (planning && first == PlanText.SaveWord && !after.Contains(' ', StringComparison.Ordinal) && !after.Contains('\t', StringComparison.Ordinal))
        {
            rest = after;
            return PlanCommand.Save;
        }

        // /plan gaps (2026-10-13): alone, a reviewer, a pass count or one of each (2026-10-17), the open rule; other words stay
        // detail (or a requirement) as before.
        if (first == PlanText.GapsWord)
        {
            var gaps = PlanText.ParseGapsArgs(after, out _, out _, out string word);
            if (gaps != GapsArgs.NotGaps)
            {
                if (!planning)
                {
                    return PlanCommand.NotPlanning;
                }

                rest = gaps == GapsArgs.Ok ? after : word;
                return gaps switch
                {
                    GapsArgs.Ok => PlanCommand.CloseGaps,
                    GapsArgs.Unknown => PlanCommand.UnknownReviewer,
                    _ => PlanCommand.BadGapPasses,
                };
            }
        }

        string words = string.Join(' ', args.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        // /plan chat (2026-10-17): alone, in plan mode or out of it; with words after it, a requirement or detail as before.
        if (words == PlanText.ChatWord)
        {
            return PlanCommand.Chat;
        }

        PlanCommand? verb = words switch
        {
            "" => planning ? PlanCommand.Show : PlanCommand.Usage,
            PlanText.ReadyWord => PlanCommand.Ready,
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
            ? PlanTools.Widen(disabled, _clockTools, _timerTools, _fileTools, _imageTools, _webTools, _gitTools, _shellTools, _vaultTools, _sqlTools, _oracleTools, _uncTools, _mysqlTools, _sqliteTools, _postgresTools, _dockerTools, _eventLogTools, _comfyTools, _haTools, _youTubeTools, _shoppingTools, _memoryTools, _skillTools, _sessionTools, _askTools, _mcp.Tools)
            : disabled;
    }

    /// <summary>
    /// The /sys Tools tab's groups with <c>present_plan</c>'s while planning, in its alphabetical place (2026-10-04, the groups' order since;
    /// ahead of the questions, where the turn offers it, until then).
    /// </summary>
    private IReadOnlyList<ToolGroup> WithPlanGroup(IReadOnlyList<ToolGroup> groups)
    {
        if (!_plan.Active || !_effective().LlmOfferTools)
        {
            return groups;
        }

        var plan = new ToolGroup(SystemPromptSummary.GroupName(PlanGroupName, 1, 1), "", [_presentPlan], true) { Label = PlanGroupName };
        return SystemPromptSummary.SortedGroups([.. groups, plan]);
    }

    /// <summary><c>/plan …</c>, idle only (<see cref="MidTurnPolicy"/> refuses it under a reply).</summary>
    private async Task<bool> HandlePlanAsync(string args, IReadOnlyList<ImageAttachment> images, CancellationToken cancellationToken)
    {
        var command = ParsePlanArgs(args, _plan.Active, out string rest);
        if (command == PlanCommand.Open && PlanFiles.Resolve(_files, rest) is null)
        {
            if (!rest.Any(char.IsWhiteSpace))
            {
                _transcript.Error(PlanText.NoSuchPlanError(rest));
                // From the Web UI's page (later on 2026-10-14) the list under the error is notices: no card for a typo's help.
                await ListPlansAsync(cancellationToken, asNotices: FromWebNow).ConfigureAwait(false);
                return false;
            }

            // Several words and no such plan: "/plan open the pod bay doors" is a requirement (or, while planning, detail).
            command = _plan.Active ? PlanCommand.Detail : PlanCommand.Enter;
        }

        switch (command)
        {
            case PlanCommand.List:
                await ListPlansAsync(cancellationToken).ConfigureAwait(false);
                return false;

            case PlanCommand.Open:
                return await OpenPlanAsync(PlanFiles.Resolve(_files, rest)!, cancellationToken).ConfigureAwait(false);

            case PlanCommand.Save:
                return await SavePlanReplyAsync(rest.Length == 0 ? null : rest, cancellationToken).ConfigureAwait(false);

            case PlanCommand.UnknownReviewer:
                _transcript.Error(PlanText.UnknownReviewerError(rest));
                return false;

            case PlanCommand.BadGapPasses:
                _transcript.Error(PlanText.GapPassesRangeError(rest));
                return false;

            case PlanCommand.CloseGaps when _plan.Path is null:
                _transcript.Error(PlanText.NothingPresentedError);
                return false;

            case PlanCommand.CloseGaps:
            {
                PlanText.ParseGapsArgs(rest, out var reviewer, out int? passes, out _);
                return await StartGapsAsync(reviewer ?? PlanReviewer.Model, passes, cancellationToken).ConfigureAwait(false);
            }

            case PlanCommand.Chat:
                return EnterPlanChat();

            case PlanCommand.Ready:
                return await PlanReadyAsync(cancellationToken).ConfigureAwait(false);

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
                _executingPlan = null;
                _planDraftReply = null;
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

        // On the PC's pane and the Web UI's page at once (2026-10-14, AskBothAsync), the first answer winning; Close gaps stays the PC's.
        var outcome = await AskBothAsync<PlanVerdict?>(PlanSpec(plan), async token => await _planMenu.AskAsync(plan, token).ConfigureAwait(false),
            answer => PlanVerdictOf(answer), "The plan approval pane failed: ", turnToken).ConfigureAwait(false);
        if (outcome.NeverAsked)
        {
            // No watcher to run the pane: saved, the user approves with /plan approve.
            DiagnosticLog.Info(AppCategory, PlanVerdictLogLine(PlanChoice.Saved));
            return new PlanVerdict(PlanChoice.Saved);
        }

        var result = outcome.Value ?? new PlanVerdict(PlanChoice.Refine);
        DiagnosticLog.Info(AppCategory, PlanVerdictLogLine(result.Choice, result.Reviewer));
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

    /// <summary><see cref="PlanVerdictLogLine(PlanChoice)"/> with the reviewer of a Close gaps… (2026-10-13): <c>present_plan: closegaps (advisor)</c>. Pinned.</summary>
    public static string PlanVerdictLogLine(PlanChoice choice, PlanReviewer? reviewer) =>
        PlanVerdictLogLine(choice) + (reviewer is { } who ? " (" + PlanText.ReviewerWord(who) + ")" : "");

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

        if (PlanFiles.ReadWhole(_files, path) is not { } text)
        {
            _plan.ClearApproval();
            _transcript.Error(PlanText.PlanUnreadableError(path));
            return false;
        }

        if (PlanFiles.MarkFile(_files, path, PlanStatus.Approved, _time.GetLocalNow(), _plan.Requirement) is { } problem)
        {
            _transcript.Warning(problem);
        }

        string requirement = _plan.Requirement;
        _plan.Exit();
        _planDraftReply = null;
        SyncPlanChrome();
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

        // Tracked until every step is ticked (2026-09-26, round two): CheckPlanProgress after each turn marks it done or incomplete.
        _executingPlan = new StoredPlan { Path = path, Requirement = requirement };
        _executingShown = null;
        SavePlanState();

        _transcript.User(message);
        return await RunMessageAsync(message, [], cancellationToken).ConfigureAwait(false);
    }

    /// <summary><c>/plan cancel</c>: the file (when one was presented) marked cancelled, plan mode off.</summary>
    private void CancelPlan()
    {
        string? path = _plan.Path;
        if (path is not null && PlanFiles.MarkFile(_files, path, PlanStatus.Cancelled, _time.GetLocalNow(), _plan.Requirement) is { } problem)
        {
            _transcript.Warning(problem);
        }

        _plan.Exit();
        _planDraftReply = null;
        SyncPlanChrome();
        SavePlanState();
        _transcript.Notice(PlanText.CancelledNotice(path));
    }

    /// <summary>The conversation forgotten (<c>/new</c>, <c>/clear</c>, a profile switch …): plan mode goes with it, the file kept as it was.</summary>
    private void LeavePlanOnReset()
    {
        // The plan being carried out goes with the conversation too: nothing is left to tick it.
        _executingPlan = null;
        _executingShown = null;
        _planDraftReply = null;
        if (!_plan.Active)
        {
            return;
        }

        _transcript.Notice(PlanText.LeftOnResetNotice(_plan.Path));
        _plan.Exit();
        SyncPlanChrome();
    }

    /// <summary>A restored session's plan mode, said on the transcript when it was on.</summary>
    private void RestorePlan(StoredPlan? stored, StoredPlan? executing)
    {
        _executingPlan = executing is { Path.Length: > 0 } ? executing : null;
        _executingShown = null;
        _planDraftReply = null;
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

    // ── Round two (2026-09-26): open, list, save, the progress check, the hint ──

    /// <summary>The approved plan being carried out, until every step is ticked; the session row keeps it.</summary>
    private StoredPlan? _executingPlan;

    /// <summary>The ticked count last said of <see cref="_executingPlan"/>, so an unchanged count is not said again.</summary>
    private (int Done, int Total)? _executingShown;

    /// <summary>A planning reply that reads as a plan the model never presented: what <c>/plan save</c> keeps.</summary>
    private string? _planDraftReply;

    /// <summary>The chat log's length when the last planning reply was looked at, so a reply is hinted once.</summary>
    private int _planLogSeen = -1;

    /// <summary>The <c>/plan open</c> completion list: every plan under <c>.neon/plans/</c>, newest first, with its status and steps.</summary>
    private IReadOnlyList<CompletionItem> PlanChoices() =>
        PlanFiles.List(_files).Select(plan => new CompletionItem(plan.Name, PlanText.CompletionNote(plan))).ToList();

    /// <summary><c>/plan open</c> alone: the plans under <c>.neon/plans/</c>, one notice each, or that there are none.</summary>
    private Task ListPlansAsync(CancellationToken cancellationToken, bool asNotices = false)
    {
        var plans = PlanFiles.List(_files);
        if (plans.Count == 0)
        {
            _transcript.Notice(PlanText.NoPlansNotice);
            return Task.CompletedTask;
        }

        if (asNotices)
        {
            foreach (var plan in plans)
            {
                _transcript.Notice(PlanText.ListLine(plan));
            }

            return Task.CompletedTask;
        }

        // On the info pane since 2026-10-04 (the user's pick); none is still the one line.
        return ShowLinesAsync("/plan " + PlanText.OpenWord, plans.Select(PlanText.ListLine).ToList(), _transcript, cancellationToken);
    }

    /// <summary>
    /// <c>/plan open &lt;name&gt;</c>: plan mode on over the file (a plan in progress left as it stands), its status back to
    /// draft, where it stood said, and — the user's call — a turn asking the model to read it and ask what should change.
    /// </summary>
    private async Task<bool> OpenPlanAsync(string path, CancellationToken cancellationToken)
    {
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

        if (PlanFiles.ReadWhole(_files, path) is not { } text)
        {
            _transcript.Error(PlanText.PlanUnreadableError(path));
            return false;
        }

        var header = PlanDocument.TryParse(text);
        string body = PlanDocument.Body(text);
        var (done, total) = PlanDocument.Progress(body);
        string title = PlanDocument.FirstHeading(body) ?? System.IO.Path.GetFileNameWithoutExtension(path);
        string requirement = header is { Requirement.Length: > 0 } ? header.Requirement : title;
        if (_plan.Active && !string.Equals(_plan.Path, path, StringComparison.Ordinal))
        {
            _transcript.Notice(PlanText.SwitchedNotice(_plan.Path));
        }

        _executingPlan = null;
        _executingShown = null;
        _planDraftReply = null;
        _plan.Open(path, title, requirement, header?.Revision ?? 1, header is { Created: var created } && created != DateTimeOffset.MinValue ? created : null);
        if (PlanFiles.MarkFile(_files, path, PlanStatus.Draft, _time.GetLocalNow(), requirement) is { } problem)
        {
            _transcript.Warning(problem);
        }

        SyncPlanChrome();
        SavePlanState();
        _transcript.Notice(PlanText.OpenedNotice(path, header?.Status, done, total));
        string message = PlanText.OpenMessage(path, done, total);
        _transcript.User(message);
        return await RunMessageAsync(message, [], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>/plan save [name]</c>: the planning reply the hint pointed at saved as the plan (<see cref="PlanFiles.Save"/>),
    /// printed, and put to the user on the approval pane at the idle line — the verdict acted on as the tool's would be:
    /// approve carries it out, cancel ends plan mode, changes asked for are sent as the next message.
    /// </summary>
    private async Task<bool> SavePlanReplyAsync(string? name, CancellationToken cancellationToken)
    {
        // Only the reply the hint pointed at: a question or a status line is never a plan to keep.
        if (_planDraftReply is not { } reply)
        {
            _transcript.Error(PlanText.NothingToSaveError);
            return false;
        }

        string title = PlanDocument.FirstHeading(reply) ?? _plan.Requirement;
        var (saved, error) = PlanFiles.Save(_plan, _files, _time, title, reply, name);
        if (saved is null)
        {
            _transcript.Error(PlanText.CouldNotSaveResult(error ?? ""));
            return false;
        }

        _planDraftReply = null;
        SavePlanState();
        _transcript.Notice(PlanText.SavedNotice(saved.Path, saved.Revision));
        var verdict = _pane.Enabled || FromWebNow ? await AskPlanAtIdleAsync(saved, cancellationToken).ConfigureAwait(false) : null;
        return await ActOnVerdictAsync(saved, verdict, PlanText.SavedFeedbackMessage, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A verdict given on the approval pane at the idle line (<c>/plan save</c>'s, and a gap review's that stopped short, 2026-10-13)
    /// acted on as the tool's would be: approve carries the plan out, cancel ends plan mode, Close gaps… starts the loop, changes
    /// asked for are sent as the next message (<paramref name="feedbackMessage"/> words it); ESC, no feedback or no pane leave it saved.
    /// </summary>
    private async Task<bool> ActOnVerdictAsync(PlanPresentation plan, PlanVerdict? verdict, Func<string, string, string> feedbackMessage, CancellationToken cancellationToken)
    {
        DiagnosticLog.Info(AppCategory, PlanVerdictLogLine(verdict?.Choice ?? PlanChoice.Saved, verdict?.Reviewer));
        switch (verdict?.Choice)
        {
            case PlanChoice.Approve:
                return await CarryOutPlanAsync(fresh: false, cancellationToken).ConfigureAwait(false);
            case PlanChoice.ApproveFresh:
                return await CarryOutPlanAsync(fresh: true, cancellationToken).ConfigureAwait(false);
            case PlanChoice.Cancel:
                CancelPlan();
                return false;
            case PlanChoice.CloseGaps:
                return await StartGapsAsync(verdict.Reviewer ?? PlanReviewer.Model, null, cancellationToken).ConfigureAwait(false);
            case PlanChoice.Refine when verdict?.Feedback is { } feedback:
                string message = feedbackMessage(plan.Path, feedback);
                _transcript.User(message);
                return await RunMessageAsync(message, [], cancellationToken).ConfigureAwait(false);
            default:
                // ESC, no feedback, or no pane: saved and still planning; /plan approve starts it.
                foreach (var line in PlanText.ShowLines(_plan))
                {
                    _transcript.Notice(line);
                }

                return false;
        }
    }

    // ── The gap-closing loop (2026-10-13, the user's ask: Close gaps… on the approval pane, /plan gaps) ──

    /// <summary>What a turn of the loop leaves to do once it ends: the next pass's message to send, or the pane to put the plan back on.</summary>
    private sealed record GapAfterTurn(string? Message, PlanPresentation? Pane);

    /// <summary>
    /// Whether <c>neon_advisor</c> is what the turn would offer (2026-10-13): <c>Advisors enabled</c>, an advisor listed, and the tool
    /// not off on <c>/tools</c> — the test the directive's advisors bullet uses. The reviewer page is shown only then.
    /// </summary>
    private bool PlanAdvisorsOffered()
    {
        var effective = _effective();
        return AdvisorsOffered(effective) && Without(_neonAdvisorTools, TurnDisabled(effective)).Count > 0;
    }

    /// <summary>
    /// A fresh loop for <paramref name="reviewer"/> over the presented plan, its first pass sent: <c>/plan gaps</c> and an idle-line verdict.
    /// <paramref name="passes"/> is <c>/plan gaps</c>' count (2026-10-17), else <c>LLM plan gap passes</c>.
    /// </summary>
    private async Task<bool> StartGapsAsync(PlanReviewer reviewer, int? passes, CancellationToken cancellationToken)
    {
        if (_plan.Path is not { } path)
        {
            _transcript.Error(PlanText.NothingPresentedError);
            return false;
        }

        var loop = _plan.StartClosing(reviewer, passes ?? _effective().PlanGapPasses, _effective().PlanGapCleanPasses);
        return await SendGapPassAsync(BeginGapPass(loop, path, ReadPlanBody(path)), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The next pass begun over <paramref name="body"/>: its one-line message (the reviewer the model alone when no advisor is offered). Since
    /// 2026-10-17 the pass's own reviewer (<see cref="PlanGapLoop.PassReviewer"/>), its meter, a confirming pass's wording, and — as pass 1
    /// begins — the notice that a short limit lowered the clean count.
    /// </summary>
    private GapAfterTurn BeginGapPass(PlanGapLoop loop, string path, string? body)
    {
        bool offered = PlanAdvisorsOffered();
        loop.BeginPass(body, PlanMeter(), offered);
        if (loop.Pass == 1 && loop.CleanLowered)
        {
            _transcript.Notice(PlanText.CleanLoweredNotice(loop.Limit, loop.CleanAsked));
        }

        var reviewer = offered ? loop.PassReviewer : PlanReviewer.Model;
        return new GapAfterTurn(PlanText.GapPassMessage(path, loop.Pass, loop.Limit, reviewer, loop.CleanStreak), null);
    }

    /// <summary>The usage now, for a gap pass's cost (2026-10-17): the main model's tokens less the reflections', the advisors', the screen's clock.</summary>
    private PlanPassMeter PlanMeter()
    {
        var usage = _session.Usage;
        return new PlanPassMeter(_time.GetUtcNow(), usage.Session.Total - usage.Learning.Total, usage.Advisors.Total);
    }

    /// <summary>A pass's message sent as a turn, the <see cref="CarryOutPlanAsync"/> shape.</summary>
    private async Task<bool> SendGapPassAsync(GapAfterTurn pass, CancellationToken cancellationToken)
    {
        string message = pass.Message!;
        _transcript.User(message);
        return await RunMessageAsync(message, [], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The plan file's body, header aside; null when it cannot be read.</summary>
    private string? ReadPlanBody(string path) => PlanFiles.ReadWhole(_files, path) is { } text ? PlanDocument.Body(text) : null;

    /// <summary>
    /// After a turn (<see cref="RunMessageAsync"/>, ahead of <see cref="TakePlanApproval"/>) while a loop runs: a loop chosen on the pane
    /// sends its first pass once the reply ended on its own; a pass is judged (<see cref="PlanGapLoop.Next"/>), said, and either followed
    /// by the next or stopped — a convergence said (the pane was shown inside the turn), a limit or a stall putting the plan back on the
    /// pane at the idle line, anything else saying how to go on. Null when nothing follows.
    /// </summary>
    private GapAfterTurn? TakeGapPass(TurnOutcome outcome)
    {
        bool onItsOwn = outcome == TurnOutcome.Continue && !_lastTurnCancelled && !_lastTurnFailed;
        string? path = _plan.Path;
        if (_plan.TakeFinished(out string? finishedPath) is { } finished)
        {
            // A review that converged inside the turn (2026-10-17): its pane may have started another review or ended plan mode, so it was
            // moved aside; said and recorded here, once, plan mode on or off, before the review its pane started (still pending) goes on.
            string? finishedBody = finishedPath is null ? null : ReadPlanBody(finishedPath);
            finished.Next(endedOnItsOwn: true, userSpoke: false, finishedBody, PlanMeter());
            EndGapReview(finished, PlanGapStop.Converged, finishedPath, finishedBody);
        }

        if (_plan.Closing is not { } loop)
        {
            return null;
        }

        if (loop.Pending)
        {
            // A message queued under the reply goes first, as it does between passes (2026-10-14, the review: the first pass went
            // ahead of it, and the loop then stopped for it after that pass).
            if (onItsOwn && path is not null && QueueDrains && _queue.Count > 0)
            {
                _plan.StopClosing();
                _transcript.Notice(PlanText.GapsYieldedNotice);
                return null;
            }

            if (onItsOwn && path is not null)
            {
                return BeginGapPass(loop, path, ReadPlanBody(path));
            }

            _plan.StopClosing();
            _transcript.Notice(PlanText.GapsDroppedNotice);
            return null;
        }

        string? body = path is null ? null : ReadPlanBody(path);
        var stop = loop.Next(onItsOwn, QueueDrains && _queue.Count > 0, body, PlanMeter());
        var cost = loop.History.Count > 0 ? loop.History[^1] : null;
        bool advisorAsked = _lastTrace is { } trace && trace.ToolNames.Contains(Llm.Tools.NeonAdvisorTool.ToolName, StringComparer.Ordinal);
        if (loop.PassGaps.Count > 0)
        {
            DiagnosticLog.Info(AppCategory, PlanText.PassLogLine(loop.Pass, _plan.Revision, loop.PassGaps, advisorAsked, loop.PassMinor, cost));
            foreach (string line in PlanText.PassNoticeLines(loop.Pass, loop.Limit, _plan.Revision, loop.PassGaps, loop.PassMinor))
            {
                _transcript.Notice(line);
            }
        }
        else if (loop.PassClean && !loop.Converged)
        {
            // A clean pass short of the streak (2026-10-17): the log every time, the notice only when another pass follows (a stop says its own).
            DiagnosticLog.Info(AppCategory, PlanText.CleanPassLogLine(loop.Pass, loop.CleanCount, loop.CleanPasses, loop.PassChecked, loop.PassMinor, advisorAsked, cost));
            if (stop is null)
            {
                foreach (string line in PlanText.CleanPassNoticeLines(loop.Pass, loop.Limit, loop.CleanCount, loop.CleanPasses, loop.PassChecked, loop.PassMinor))
                {
                    _transcript.Notice(line);
                }
            }
        }

        if (stop is not { } reason)
        {
            return BeginGapPass(loop, path!, body);
        }

        _plan.StopClosing();
        return EndGapReview(loop, reason, path, body);
    }

    /// <summary>
    /// A review over (2026-10-17): the summary of its passes, the <c>review:</c> line for a review that ended on its own (converged, cut short,
    /// at its limit, stalled) written from the file as it now stands, then the stop's own notice — and for a limit, a cut-short or a stall the
    /// plan back on the pane at the idle line (or where it stands, with no pane).
    /// </summary>
    private GapAfterTurn? EndGapReview(PlanGapLoop loop, PlanGapStop reason, string? path, string? body)
    {
        foreach (string line in PlanText.ReviewSummaryLines(loop.History, reason, loop.CleanPasses))
        {
            _transcript.Notice(line);
        }

        if (path is not null && body is not null && reason is PlanGapStop.Converged or PlanGapStop.Unconfirmed or PlanGapStop.Limit or PlanGapStop.Stalled)
        {
            string review = PlanText.ReviewLine(loop.Pass, loop.Closed, reason, loop.CleanPasses, PlanDocument.Fingerprint(body));
            if (PlanFiles.MarkReview(_files, path, review, _time.GetLocalNow(), _plan.Requirement) is { } problem)
            {
                _transcript.Warning(problem);
            }
        }

        if (reason == PlanGapStop.Converged)
        {
            bool advisorAsked = _lastTrace is { } trace && trace.ToolNames.Contains(Llm.Tools.NeonAdvisorTool.ToolName, StringComparer.Ordinal);
            DiagnosticLog.Info(AppCategory, PlanText.CleanPassLogLine(loop.Pass, loop.CleanCount, loop.CleanPasses, loop.PassChecked, loop.PassMinor, advisorAsked, loop.History.Count > 0 ? loop.History[^1] : null));
            _transcript.Notice(PlanText.ConvergedNotice(path ?? "", loop.Pass, loop.Closed, loop.CleanPasses));
            foreach (string line in PlanText.ReportLines(loop.PassChecked, loop.PassMinor))
            {
                _transcript.Notice(line);
            }

            return null;
        }

        _transcript.Notice(PlanText.GapStopNotice(reason, loop.Pass, loop.Closed));
        if (reason is not (PlanGapStop.Limit or PlanGapStop.Stalled or PlanGapStop.Unconfirmed) || path is null)
        {
            return null;
        }

        if (_pane.Enabled)
        {
            return new GapAfterTurn(null, new PlanPresentation(_plan.Title ?? PlanDocument.FirstHeading(body ?? "") ?? path, path, _plan.Revision, body ?? "") { Note = PlanText.StoppedNote(reason, loop.Pass, loop.PassGaps.Count, loop.CleanCount, loop.CleanPasses) });
        }

        // No pane to put it back on: where the plan stands, /plan approve the way on.
        foreach (var line in PlanText.ShowLines(_plan))
        {
            _transcript.Notice(line);
        }

        return null;
    }

    /// <summary>
    /// <c>/plan chat</c> (2026-10-17, the user's ask): outside plan mode, plan mode on with no requirement and talking, nothing sent — the
    /// first message opens it; while planning, talking again. <see cref="PlanCommand.Enter"/>'s checks and resets.
    /// </summary>
    private bool EnterPlanChat()
    {
        if (_plan.Active)
        {
            _plan.ResumeTalking();
            SyncPlanChrome();
            SavePlanState();
            _transcript.Notice(PlanText.ChatResumedNotice);
            return false;
        }

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

        _plan.EnterChat();
        _executingPlan = null;
        _planDraftReply = null;
        SyncPlanChrome();
        SavePlanState();
        _transcript.Notice(PlanText.ChatEnteredNotice);
        return false;
    }

    /// <summary>
    /// <c>/plan ready</c> (2026-10-17, the user's pick): with nothing presented yet, or while talking, talking ends and <see cref="PlanText.ReadyMessage"/>
    /// is sent as a turn; with a plan presented, its approval pane opens on the current revision with no turn (where it stands, with no pane).
    /// Before the first message of a <c>/plan chat</c> there is nothing to plan yet.
    /// </summary>
    private async Task<bool> PlanReadyAsync(CancellationToken cancellationToken)
    {
        if (_plan.Requirement.Length == 0)
        {
            _transcript.Error(PlanText.ReadyTooSoonError);
            return false;
        }

        if (_plan.Path is not { } path || _plan.Talking)
        {
            _plan.StopTalking();
            SyncPlanChrome();
            SavePlanState();
            _transcript.User(PlanText.ReadyMessage);
            return await RunMessageAsync(PlanText.ReadyMessage, [], cancellationToken).ConfigureAwait(false);
        }

        if (PlanFiles.ReadWhole(_files, path) is not { } text)
        {
            _transcript.Error(PlanText.PlanUnreadableError(path));
            return false;
        }

        string body = PlanDocument.Body(text);
        var plan = new PlanPresentation(_plan.Title ?? PlanDocument.FirstHeading(body) ?? path, path, _plan.Revision, body);
        if (_pane.Enabled || FromWebNow)
        {
            return await AskStoppedPlanAsync(plan, cancellationToken).ConfigureAwait(false);
        }

        foreach (var line in PlanText.ShowLines(_plan))
        {
            _transcript.Notice(line);
        }

        return false;
    }

    /// <summary>A loop that stopped short (its limit, a stall): the plan back on the approval pane at the idle line, the verdict acted on.</summary>
    private async Task<bool> AskStoppedPlanAsync(PlanPresentation plan, CancellationToken cancellationToken)
    {
        var verdict = await AskPlanAtIdleAsync(plan, cancellationToken).ConfigureAwait(false);
        return await ActOnVerdictAsync(plan, verdict, PlanText.RevisionFeedbackMessage, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The approval pane at the idle line (<c>/plan save</c>'s, a gap loop's that stopped short): from a command or a message the Web UI's page
    /// sent (later on 2026-10-14), on the page as well, the first answer winning; from the PC, the pane alone as ever. Close gaps stays the PC's.
    /// </summary>
    private async Task<PlanVerdict?> AskPlanAtIdleAsync(PlanPresentation plan, CancellationToken cancellationToken)
    {
        var outcome = await RaceAsync<PlanVerdict?>(PlanSpec(plan), async token => await _planMenu.AskAsync(plan, token).ConfigureAwait(false),
            answer => PlanVerdictOf(answer), "The plan pane failed: ", cancellationToken).ConfigureAwait(false);
        return outcome.Value;
    }

    /// <summary>
    /// After a turn (<see cref="RunMessageAsync"/>) while an approved plan is being carried out: its ticked steps counted
    /// and the file marked — every step ticked is done (and the tracking ends), some left is incomplete, said when the
    /// count moved or the turn was stopped; a plan with no task lines is left approved, a file gone is let go.
    /// </summary>
    private void CheckPlanProgress(bool interrupted)
    {
        if (_executingPlan is not { Path: { } path } executing)
        {
            return;
        }

        if (PlanFiles.ReadWhole(_files, path) is not { } text)
        {
            DiagnosticLog.Info(AppCategory, PlanProgressGoneLogLine(path));
            _executingPlan = null;
            SavePlanState();
            return;
        }

        var progress = PlanDocument.Progress(PlanDocument.Body(text));
        if (progress.Total == 0)
        {
            _executingPlan = null;
            SavePlanState();
            return;
        }

        var header = PlanDocument.TryParse(text);
        if (progress.Done == progress.Total)
        {
            if (PlanFiles.MarkFile(_files, path, PlanStatus.Done, _time.GetLocalNow(), executing.Requirement, progress) is { } problem)
            {
                _transcript.Warning(problem);
            }

            _transcript.Notice(PlanText.DoneNotice(path));
            _transcript.Notice(PlanText.ComesTogetherNotice);   // 2026-10-17, the user's ask: its own line, right after the done line
            _executingPlan = null;
            _executingShown = null;
            SavePlanState();
            return;
        }

        if (header is not { Status: PlanStatus.Incomplete } || header.StepsDone != progress.Done || header.StepsTotal != progress.Total)
        {
            if (PlanFiles.MarkFile(_files, path, PlanStatus.Incomplete, _time.GetLocalNow(), executing.Requirement, progress) is { } problem)
            {
                _transcript.Warning(problem);
            }
        }

        if (interrupted || _executingShown != progress)
        {
            _transcript.Notice(PlanText.IncompleteNotice(path, progress.Done, progress.Total));
            _executingShown = progress;
        }
    }

    /// <summary><c>plan: .neon/plans/x.md is gone; no longer tracked</c> for the --log file.</summary>
    public static string PlanProgressGoneLogLine(string path) => $"plan: {path} is gone; no longer tracked";

    /// <summary>
    /// After a planning turn that ended on its own: a reply that reads as a plan (<see cref="PlanDocument.LooksLikePlan"/>)
    /// from a turn that never called <c>present_plan</c> earns the hint, once, and is what <c>/plan save</c> keeps.
    /// </summary>
    private void CheckUnpresentedPlan(TurnOutcome outcome)
    {
        int seen = _log.Count;
        if (seen == _planLogSeen)
        {
            return;
        }

        _planLogSeen = seen;
        if (!_plan.Active || _plan.Approved is not null || outcome != TurnOutcome.Continue || _lastTurnCancelled || _lastTurnFailed
            || _lastTrace is not { } trace || trace.ToolNames.Contains(PresentPlanTool.ToolName, StringComparer.Ordinal)
            || _log.LastReply is not { } reply || !PlanDocument.LooksLikePlan(reply))
        {
            return;
        }

        _planDraftReply = reply;
        // Talking it through (2026-10-17): the reply is kept for /plan save, but the hint stays quiet until the user is ready.
        if (!_plan.Talking)
        {
            _transcript.Notice(PlanText.UnpresentedHint);
        }
    }

    /// <summary>The input row's placeholder as plan mode stands.</summary>
    private void SyncPlanChrome() => _pane.Placeholder = !_plan.Active ? InputPlaceholder : _plan.Talking ? PlanText.TalkingPlaceholder : PlanText.Placeholder;

    /// <summary>Plan mode into the session row at once (an enter, a cancel, an approval), not only at the turn's end; nothing without a row.</summary>
    private void SavePlanState()
    {
        if (_session.Assistant is { } assistant)
        {
            SaveSessionHistory(assistant);
        }
    }
}
