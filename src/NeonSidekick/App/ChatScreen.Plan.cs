using NeonSidekick.Audits;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Llm;
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

        /// <summary>
        /// <c>/plan open &lt;name&gt; --chat</c> (2026-10-18, the user's ask; <c>--chat</c> first too): the plan opened to talk it through, nothing
        /// sent; the name is the rest. <see cref="Open"/>'s resolve and fallback.
        /// </summary>
        OpenChat,

        /// <summary><c>/plan open --chat</c> alone (2026-10-18): the plans to pick one to talk through.</summary>
        ListChat,

        /// <summary>
        /// <c>/plan approve --ralph [passes]</c> while planning (later on 2026-10-18, the Ralph loop; the words in either order): the presented plan approved
        /// and run as a Ralph loop; the count as typed is the rest (empty for none).
        /// </summary>
        ApproveRalph,

        /// <summary>
        /// <c>/plan ralph [name] [passes]</c> (2026-10-18): the paused loop resumed, or the plan named run as one; the words after <c>ralph</c> are the
        /// rest. Refused while planning (the handler names <c>/plan approve --ralph</c>).
        /// </summary>
        Ralph,

        /// <summary><c>/plan ralph x 500</c> or <c>/plan approve --ralph 500</c>: a pass count out of range; the number as typed is the rest.</summary>
        BadRalphPasses,
    }

    /// <summary>The <c>/plan</c> list while planning: the subcommands, each with its note. Pinned.</summary>
    public static readonly IReadOnlyList<CompletionItem> PlanVerbs =
    [
        new(PlanText.ApproveWord, "approve the presented plan and carry it out here"),
        new(PlanText.ApproveWord + " " + PlanText.FreshSwitch, "approve it and carry it out in a new conversation"),
        new(PlanText.ApproveWord + " " + RalphText.RalphSwitch, "approve it and run it as a Ralph loop, one step per fresh conversation: /plan approve --ralph [passes]"),
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
        new(RalphText.RalphWord, "run a plan as a Ralph loop, or resume the paused one: /plan ralph [name] [passes]"),
    ];

    /// <summary>The /sys Tools tab's name for the group that holds <c>present_plan</c>.</summary>
    public const string PlanGroupName = "Plan";

    public const string PlanVerdictLogPrefix = "present_plan: ";

    /// <summary>
    /// The least room the compact checks leave for the next reply while a plan is being written (2026-10-18, the user's pick: a reopened
    /// plan's first presentation gets room too): the plan file's size over <see cref="ConversationCompactor.CharsPerToken"/>, since
    /// <c>present_plan</c> sends the plan back whole. 0 with no plan, an approved one (<c>present_plan</c> is then refused), no file yet
    /// or one the sandbox will not stat. Bytes over-count non-ASCII a little, the safe side. A file stat, so each site calls it: the
    /// top-of-message check and the turn's guard (<see cref="Assistant.TurnContextGuard.ReplyFloor"/>), headless's too.
    /// </summary>
    public static long PlanReplyFloor(PlanSession plan, WorkingDirectory files)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(files);
        return plan.Active && plan.Approved is null && plan.Path is { } path && files.TryStamp(path) is { } stamp
            ? stamp.Length / ConversationCompactor.CharsPerToken
            : 0;
    }

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
        // /plan ralph [name] [passes] (later on 2026-10-18): at most a name and a count, in either order; more words are a requirement or detail.
        // While planning only the bare word is the command (2026-10-20, a review: "/plan ralph tests" typed as detail was refused and lost): its
        // error says how a plan being planned is run; with words it is detail.
        if (first == RalphText.RalphWord && (!planning || after.Length == 0) && RalphArgs(after, out _, out _, out string badCount) is { } ralphForm)
        {
            rest = ralphForm ? after : badCount;
            return ralphForm ? PlanCommand.Ralph : PlanCommand.BadRalphPasses;
        }

        // /plan approve --ralph [passes] (later on 2026-10-18): the three words in any order, while planning.
        if (ApproveRalphArgs(trimmed, out string? ralphCount, out bool ralphBad))
        {
            rest = ralphCount ?? "";
            return !planning ? PlanCommand.NotPlanning : ralphBad ? PlanCommand.BadRalphPasses : PlanCommand.ApproveRalph;
        }

        if (first == PlanText.OpenWord)
        {
            // --chat (2026-10-18): a whole word at either end of the rest, case aside; "open chat" is still a plan named chat.
            bool chat = TakeSwitch(ref after, PlanText.ChatSwitch);
            rest = after;
            return after.Length == 0 ? chat ? PlanCommand.ListChat : PlanCommand.List : chat ? PlanCommand.OpenChat : PlanCommand.Open;
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

    /// <summary>
    /// <c>/plan ralph</c>'s words (2026-10-18): none, a name, a count, or one of each in either order is true (<paramref name="name"/> empty for none,
    /// <paramref name="passes"/> null for none); a count out of <see cref="AppSettingsData.MinRalphLoopPasses"/>–<see cref="AppSettingsData.MaxRalphLoopPasses"/>
    /// is false with the word in <paramref name="badCount"/>; anything else (more words, two names) is null — not this form. Pure.
    /// </summary>
    /// <summary>
    /// The plan <paramref name="rest"/> names when it is one word of digits and a plan has that name (2026-10-20, a review: such a name parsed as a
    /// pass count and could not be named); else null.
    /// </summary>
    internal static string? RalphDigitsPlan(WorkingDirectory files, string rest)
    {
        string word = rest.Trim();
        return word.Length > 0 && word.All(char.IsAsciiDigit) && PlanFiles.Resolve(files, word) is not null ? word : null;
    }

    internal static bool? RalphArgs(string after, out string name, out int? passes, out string badCount)
    {
        name = "";
        passes = null;
        badCount = "";
        string[] words = after.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 2)
        {
            return null;
        }

        foreach (string word in words)
        {
            bool number = word.TrimStart('-', '+').Length > 0 && word.TrimStart('-', '+').All(char.IsAsciiDigit);
            if (number && passes is null && badCount.Length == 0)
            {
                if (!int.TryParse(word, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int count)
                    || count < AppSettingsData.MinRalphLoopPasses || count > AppSettingsData.MaxRalphLoopPasses)
                {
                    badCount = word;
                    continue;
                }

                passes = count;
            }
            else if (!number && name.Length == 0)
            {
                name = word;
            }
            else
            {
                return null;
            }
        }

        return badCount.Length == 0;
    }

    /// <summary>
    /// <c>/plan approve --ralph [passes]</c>'s words in any order (2026-10-18): true when the line is <c>approve</c>, <c>--ralph</c> and at most one
    /// count; <paramref name="passes"/> the count as typed, <paramref name="bad"/> when it is out of range. Pure.
    /// </summary>
    internal static bool ApproveRalphArgs(string line, out string? passes, out bool bad)
    {
        passes = null;
        bad = false;
        var words = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length is < 2 or > 3
            || !words.Any(w => string.Equals(w, PlanText.ApproveWord, StringComparison.OrdinalIgnoreCase))
            || !words.Any(w => string.Equals(w, RalphText.RalphSwitch, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var others = words.Where(w => !string.Equals(w, PlanText.ApproveWord, StringComparison.OrdinalIgnoreCase) && !string.Equals(w, RalphText.RalphSwitch, StringComparison.OrdinalIgnoreCase)).ToList();
        if (others.Count != words.Length - 2 || others.Count > 1)
        {
            return false;
        }

        if (others.Count == 1)
        {
            string word = others[0];
            if (!(word.TrimStart('-', '+').Length > 0 && word.TrimStart('-', '+').All(char.IsAsciiDigit)))
            {
                return false;
            }

            passes = word;
            bad = !int.TryParse(word, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int count)
                || count < AppSettingsData.MinRalphLoopPasses || count > AppSettingsData.MaxRalphLoopPasses;
        }

        return true;
    }

    /// <summary>
    /// Whether <paramref name="text"/> starts or ends with <paramref name="word"/> as a whole word, case aside (2026-10-18, <c>--chat</c>); when
    /// it does, the word is taken off and the rest trimmed. Pure.
    /// </summary>
    internal static bool TakeSwitch(ref string text, string word)
    {
        string trimmed = text.Trim();
        if (string.Equals(trimmed, word, StringComparison.OrdinalIgnoreCase))
        {
            text = "";
            return true;
        }

        if (trimmed.StartsWith(word, StringComparison.OrdinalIgnoreCase) && trimmed.Length > word.Length && char.IsWhiteSpace(trimmed[word.Length]))
        {
            text = trimmed[word.Length..].Trim();
            return true;
        }

        if (trimmed.EndsWith(word, StringComparison.OrdinalIgnoreCase) && trimmed.Length > word.Length && char.IsWhiteSpace(trimmed[^(word.Length + 1)]))
        {
            text = trimmed[..^word.Length].Trim();
            return true;
        }

        text = trimmed;
        return false;
    }

    /// <summary>The strip with plan mode's glyph ahead of the rest while planning. Pinned.</summary>
    public static string PlanStrip(bool planning, string strip)
    {
        ArgumentNullException.ThrowIfNull(strip);
        return !planning ? strip : strip.Length == 0 ? PlanText.Glyph : PlanText.Glyph + GlyphSeparator + strip;
    }

    /// <summary>
    /// The <c>/tools</c> list as the next turn applies it: widened by every tool plan mode drops while planning (<see cref="PlanTools.Widen"/>), so <c>/sys</c> shows what is sent;
    /// since 2026-10-18 also by the tools that wait on the user while a gap pass may not (<see cref="GapAsksBarred"/>).
    /// </summary>
    private IReadOnlySet<string> TurnDisabled(AppSettingsData effective)
    {
        var disabled = ToolsText.DisabledSet(effective.ToolsDisabled);
        var turn = _plan.Active || _audit.InPass
            ? PlanTools.Widen(disabled, _clockTools, _timerTools, _fileTools, _imageTools, _webTools, _gitTools, _shellTools, _vaultTools, _sqlTools, _oracleTools, _uncTools, _mysqlTools, _sqliteTools, _postgresTools, _dockerTools, _eventLogTools, _comfyTools, _haTools, _youTubeTools, _shoppingTools, _memoryTools, _skillTools, _sessionTools, _askTools, _mcp.Tools)
            : disabled;
        // A code audit's pass (2026-10-18) keeps its own tools alone, as the turn's OnlyTools does.
        turn = _audit.InPass
            ? DisableAllBut(turn, AuditTools.Allowed(effective).Contains, _clockTools, _timerTools, _helpTools, _fileTools, _imageTools, _webTools, _gitTools, _shellTools, _vaultTools, _sqlTools, _oracleTools, _uncTools, _mysqlTools, _sqliteTools, _postgresTools, _dockerTools, _eventLogTools, _networkTools, _serviceTools, _taskTools, _directoryTools, _comfyTools, _haTools, _printTools, _youTubeTools, _shoppingTools, _memoryTools, _skillTools, _sessionTools, _askTools, _cameraTools, _screenTools, _neonAdvisorTools, _mcp.Tools)
            : turn;
        return GapAsksBarred(effective) || RalphAsksBarred() ? PlanTools.WithoutAsking(turn) : turn;
    }

    /// <summary>
    /// Whether this turn is a gap pass that may not wait on the user (2026-10-18, the user's ask): a pass under way and
    /// <see cref="AppSettingsData.PlanGapAskUser"/> off, read every turn. Such a pass is offered none of <see cref="PlanTools.AskingTools"/>;
    /// the turn whose pane chose <c>Close gaps…</c> (the loop still pending) and every other plan-mode turn keep them.
    /// </summary>
    private bool GapAsksBarred(AppSettingsData effective) => (_plan.Reviewing && !effective.PlanGapAskUser) || (_audit.InPass && !effective.CodeAuditAskUser);

    /// <summary>
    /// The /sys Tools tab's groups with <c>present_plan</c>'s while planning, in its alphabetical place (2026-10-04, the groups' order since;
    /// ahead of the questions, where the turn offers it, until then).
    /// </summary>
    private IReadOnlyList<ToolGroup> WithPlanGroup(IReadOnlyList<ToolGroup> groups)
    {
        if (!_effective().LlmOfferTools)
        {
            return groups;
        }

        if (RalphPassTurn() is { } pass && !_plan.Active)
        {
            // finish_step's group during a Ralph pass (2026-10-18), so /sys says what the turn sends.
            var ralph = new ToolGroup(SystemPromptSummary.GroupName(RalphGroupName, 1, 1), "", [pass.Tool], true) { Label = RalphGroupName };
            return SystemPromptSummary.SortedGroups([.. groups, ralph]);
        }

        if (_audit.Reviewing && !_plan.Active)
        {
            // present_audit's group during a code audit's pass (2026-10-18), so /sys says what the turn sends.
            var audit = new ToolGroup(SystemPromptSummary.GroupName(AuditGroupName, 1, 1), "", [_presentAudit], true) { Label = AuditGroupName };
            return SystemPromptSummary.SortedGroups([.. groups, audit]);
        }

        if (!_plan.Active)
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
        if (command is PlanCommand.Open or PlanCommand.OpenChat && PlanFiles.Resolve(_files, rest) is null)
        {
            if (!rest.Any(char.IsWhiteSpace))
            {
                _transcript.Error(PlanText.NoSuchPlanError(rest));
                // From the Web UI's page (later on 2026-10-14) the list under the error is notices: no card for a typo's help.
                return await ListPlansAsync(command == PlanCommand.OpenChat ? PlanPickMode.Talk : PlanPickMode.Open, cancellationToken, asNotices: FromWebNow).ConfigureAwait(false);
            }

            // Several words and no such plan: "/plan open the pod bay doors" is a requirement (or, while planning, detail).
            command = _plan.Active ? PlanCommand.Detail : PlanCommand.Enter;
        }

        switch (command)
        {
            case PlanCommand.List or PlanCommand.ListChat:
                return await ListPlansAsync(command == PlanCommand.ListChat ? PlanPickMode.Talk : PlanPickMode.Open, cancellationToken).ConfigureAwait(false);

            case PlanCommand.Open or PlanCommand.OpenChat:
                return await OpenPlanAsync(PlanFiles.Resolve(_files, rest)!, command == PlanCommand.OpenChat, cancellationToken).ConfigureAwait(false);

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

            case PlanCommand.BadRalphPasses when !_plan.Active && RalphDigitsPlan(_files, rest) is { } digits:
                // A plan whose name is all digits (2026-10-20, a review: it read as a pass count, out of range here, and could not be named).
                return await HandleRalphAsync(digits, null, cancellationToken).ConfigureAwait(false);

            case PlanCommand.BadRalphPasses:
                _transcript.Error(RalphText.BadPassesError(rest));
                return false;

            case PlanCommand.Ralph:
            {
                RalphArgs(rest, out string ralphName, out int? ralphPasses, out _);
                if (ralphName.Length == 0 && ralphPasses is not null && _executingPlan is not { Ralph: true } && RalphDigitsPlan(_files, rest) is { } named)
                {
                    // The same with nothing paused: a lone number that names a plan runs that plan.
                    (ralphName, ralphPasses) = (named, null);
                }

                return await HandleRalphAsync(ralphName, ralphPasses, cancellationToken).ConfigureAwait(false);
            }

            case PlanCommand.ApproveRalph when _plan.Path is null:
                _transcript.Error(PlanText.NothingPresentedError);
                return false;

            case PlanCommand.ApproveRalph:
                return await ApproveRalphAsync(rest.Length == 0 ? null : int.Parse(rest, System.Globalization.CultureInfo.InvariantCulture), cancellationToken, checksShown: false).ConfigureAwait(false);

            case PlanCommand.Ready:
                return await PlanReadyAsync(cancellationToken).ConfigureAwait(false);

            case PlanCommand.Usage:
                _transcript.Error(PlanText.UsageError);
                return false;

            case PlanCommand.NotPlanning:
                // /plan show outside plan mode (2026-10-18): a running or paused Ralph loop says where it stands; else the error as ever.
                if (!(string.Equals(args.Trim(), PlanText.ShowWord, StringComparison.OrdinalIgnoreCase) && ShowRalph()))
                {
                    _transcript.Error(PlanText.NotPlanningError);
                }

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
                return await EnterPlanAsync(args, images, cancellationToken).ConfigureAwait(false);

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
        var outcome = await AskBothAsync<PlanVerdict?>(PlanSpec(plan, PlanCheckLine(plan)), async token => await _planMenu.AskAsync(plan, token).ConfigureAwait(false),
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

    /// <summary>
    /// The Ralph loop's check line for <paramref name="plan"/> (2026-10-18, the pane's caption and the page's body): its checks (the presentation's,
    /// else the file's header), the reviewer, the budget, and the warning under <c>Shell command policy</c> ask; since later on 2026-10-18 the plan's
    /// fit problems after them (<see cref="PlanFit.Shape"/>), so the pane says them before its Ralph row is picked.
    /// </summary>
    private string PlanCheckLine(PlanPresentation plan)
    {
        var effective = _effective();
        var checks = plan.Checks ?? (PlanFiles.ReadWhole(_files, plan.Path) is { } text && PlanDocument.TryParse(text) is { } header ? new PlanChecks(header.Verify, header.Final) : null);
        return RalphText.PaneCheckLine(checks, RalphReviewerMode.Resolve(effective), effective.RalphLoopTokenBudget, Shell.CommandPolicy.Resolve(effective) == Shell.CommandPolicyMode.Ask,
            PlanFit.Shape(plan.Markdown, RalphInlineLimit()));
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
    private bool TakePlanApproval(TurnOutcome outcome, out PlanChoice choice)
    {
        SyncPlanChrome();
        choice = PlanChoice.Approve;
        if (_plan.Approved is not { } approval)
        {
            return false;
        }

        choice = approval;
        if (outcome == TurnOutcome.Continue && !_lastTurnCancelled)
        {
            return true;
        }

        _plan.ClearApproval();
        // The form that was chosen (2026-10-18): a Ralph approval names /plan approve --ralph.
        _transcript.Notice(approval == PlanChoice.ApproveRalph ? RalphText.ApprovalInterruptedNotice : PlanText.ApprovalInterruptedNotice);
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

        // Another instance runs it as a Ralph loop (2026-10-19, the fourth review): never carried out here alongside it.
        if (RalphLog.HeldElsewhere(_files, path) is { } held)
        {
            _plan.ClearApproval();
            _transcript.Error(held);
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
        IReadOnlyList<ImageAttachment> images = [];
        if (fresh)
        {
            StartNewConversation();
            string body = PlanDocument.Body(text);
            message = PlanText.ExecuteFreshMessage(path, body);
            // The plan's reference pictures (2026-10-18): a new conversation has nothing of the planning, so they go with the message.
            var effective = _effective();
            images = PlanReferences.ForRun(_files, path, body, null, effective.LlmPictureKeep, effective.LlmPictureMegabytes, ModelSeesPictures(), out string? unseen);
            if (unseen is not null)
            {
                _transcript.Notice(unseen);
            }
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
        if (images.Count > 0)
        {
            _transcript.Notice(PlanText.ReferencesAttachedNotice(images.Count));
        }

        return await RunMessageAsync(message, images, cancellationToken).ConfigureAwait(false);
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
        // The plan being carried out goes with the conversation too: nothing is left to tick it. A Ralph loop's with it (2026-10-18).
        StopRalphOnReset();
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
        StopRalphOnReset();
        _executingPlan = executing is { Path.Length: > 0 } ? executing : null;
        if (_executingPlan is { Ralph: true } ralph)
        {
            // A restored Ralph state is always paused (2026-10-18): a crash mid-pass resumes cleanly with /plan ralph.
            ralph.Paused = true;
        }

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

    // ── Plan reference pictures (2026-10-18) ──

    /// <summary>
    /// The conversation's pictures a plan may keep (<see cref="PlanReferences.FromHistory"/>), under the keep-in-sessions settings; with
    /// <paramref name="hashOnly"/> a pruned one carries its hash and no bytes (<see cref="PlanReferences.Hashed"/>: only to be named).
    /// </summary>
    private IReadOnlyList<PlanPicture> ConversationPictures(AppSettingsData effective, bool hashOnly = false) =>
        PlanReferences.FromHistory(_session.History.Messages, effective.CameraKeepInSessions, effective.ScreenKeepInSessions,
            hashOnly ? path => PictureToReload(path) is { } full ? PlanReferences.Hashed(full) : null : ReloadPicture);

    /// <summary>A pruned picture read again from its path (<see cref="PictureToReload"/>).</summary>
    private PlanPicture? ReloadPicture(string path) => PictureToReload(path) is { } full ? PlanReferences.Reload(full) : null;

    /// <summary>
    /// The full path a pruned picture is read again from: an absolute one as it is, a relative one under the working directory. Never one in the camera's or
    /// the screen capture's output folder that its keep-in-sessions setting leaves out (2026-10-19, the review: a stand-in restored from a session
    /// has lost its mark, and the folder still tells).
    /// </summary>
    private string? PictureToReload(string path)
    {
        string? full = System.IO.Path.IsPathFullyQualified(path) ? path : _files.Resolve(path, forWrite: false, out string resolved) == FileOutcome.Ok ? resolved : null;
        if (full is null)
        {
            return null;
        }

        var effective = _effective();
        return (!effective.CameraKeepInSessions && InPictureFolder(full, effective.CameraOutputFolder)) || (!effective.ScreenKeepInSessions && InPictureFolder(full, effective.ScreenOutputFolder))
            ? null
            : full;
    }

    /// <summary>
    /// Whether <paramref name="full"/> lies in the profile's picture folder <paramref name="setting"/> names (the camera's or the screen capture's):
    /// under it, or directly in it when the setting is the images folder itself (which holds the other folders too).
    /// </summary>
    private bool InPictureFolder(string full, string? setting)
    {
        string folder = NeonSidekick.Comfy.ComfyStudio.OutputFolder(setting);
        if (_images.Resolve(folder, forWrite: false, out string root) != FileOutcome.Ok)
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string file = System.IO.Path.GetFullPath(full);
        root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root));
        return folder == "."
            ? string.Equals(System.IO.Path.GetDirectoryName(file), root, comparison)
            : file.StartsWith(root + System.IO.Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>
    /// The pictures of the message whose turn is being prepared (2026-10-18): not in the history yet when the directive is built, so the
    /// directive names them with the rest. Set around <see cref="PrepareTurn"/> on the turn task; empty otherwise.
    /// </summary>
    private IReadOnlyList<ImageAttachment> _turnPictures = [];

    /// <summary>The <c>ref-N</c> names the planning directive gives the conversation's pictures (the turn's own too); null outside plan mode or with none.</summary>
    private IReadOnlyList<string>? PlanReferenceNames(AppSettingsData effective)
    {
        if (!_plan.Active)
        {
            return null;
        }

        // Hashes only for the pictures a compaction took out (2026-10-20, a review: each was read whole again every planning turn).
        var pictures = ConversationPictures(effective, hashOnly: true).Concat(_turnPictures
            .Where(i => !i.IsDocument && i.PdfPage is null && !i.Reference && (effective.CameraKeepInSessions || !i.Camera) && (effective.ScreenKeepInSessions || !i.Screen))
            .Select(i => new PlanPicture(i.Bytes, i.MediaType, i.Path))).ToList();
        return pictures.Count == 0 ? null : PlanReferences.Names(_files, _plan.Path, pictures);
    }

    /// <summary>
    /// <c>present_plan</c>'s and <c>/plan save</c>'s pictures seam (<see cref="PlanFiles.Save"/>): the conversation's pictures saved beside the plan,
    /// then the body's <c>## References</c> reconciled with them. On the turn task (the tool) or the idle line.
    /// </summary>
    private string SavePlanReferences(string path, string body)
    {
        var pictures = ConversationPictures(_effective());
        var saved = PlanReferences.Sync(_files, path, pictures);
        return PlanReferences.ReconcileSection(body, saved);
    }

    /// <summary>
    /// Whether the model in use can see pictures (2026-10-18, a run's reference pictures): not the embedded model without its projector, and not
    /// without image codecs.
    /// </summary>
    private bool ModelSeesPictures() => ImageCodecs.Available && _session.EmbeddedServer is not { Vision: false };

    /// <summary>The <c>/plan open</c> completion list: every plan under <c>.neon/plans/</c>, newest first, with its status and steps.</summary>
    private IReadOnlyList<CompletionItem> PlanChoices() =>
        PlanFiles.List(_files).Select(plan => new CompletionItem(plan.Name, PlanText.CompletionNote(plan))).ToList();

    /// <summary>
    /// <c>/plan open</c> alone (<paramref name="mode"/> <see cref="PlanPickMode.Open"/>), <c>/plan open --chat</c> (<see cref="PlanPickMode.Talk"/>)
    /// and, with nothing paused, bare <c>/plan ralph</c> (<see cref="PlanPickMode.Ralph"/>): the plans on <see cref="PlansMenu"/> (2026-10-18, the
    /// user's ask; a list on the info pane since 2026-10-04 until then), the pick opened, talked through or run in its mode. From the Web UI's
    /// page the pick is a card there too (<see cref="PickBothAsync"/>): a page pick goes in the typed mode, a PC pick keeps its button's. No
    /// plans is one line; no pane, or <paramref name="asNotices"/> (the typo path from the page), a notice per plan. True when the shell should exit.
    /// </summary>
    private async Task<bool> ListPlansAsync(PlanPickMode mode, CancellationToken cancellationToken, bool asNotices = false)
    {
        var plans = PlanFiles.List(_files);
        if (plans.Count == 0)
        {
            _transcript.Notice(PlanText.NoPlansNotice);
            return false;
        }

        if (asNotices || (!_pane.Enabled && !FromWebNow))
        {
            foreach (var plan in plans)
            {
                _transcript.Notice(PlanText.ListLine(plan));
            }

            return false;
        }

        PlanPick? pcPick = null;
        var menu = new PlansMenu(plans, _menuPane, mode, RalphButtonOffered(), _plan.Path, RalphLines(plans));
        string? chosen;
        if (FromWebNow)
        {
            var options = plans.Select(p => new PickOption(p.Path, p.Name, PlanText.CompletionNote(p))).ToList();
            chosen = await PickBothAsync(PlanText.PickerTitle, options, _plan.Path, async token =>
            {
                pcPick = await menu.ShowAsync(token).ConfigureAwait(false);
                return pcPick?.Plan.Path;
            }, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            pcPick = await menu.ShowAsync(cancellationToken).ConfigureAwait(false);
            chosen = pcPick?.Plan.Path;
        }

        if (chosen is null)
        {
            return false;
        }

        var picked = pcPick is { } pc && pc.Plan.Path == chosen ? pc.Mode : mode;
        return picked == PlanPickMode.Ralph
            ? await RalphPickedAsync(chosen, cancellationToken).ConfigureAwait(false)
            : await OpenPlanAsync(chosen, picked == PlanPickMode.Talk, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>/plan open &lt;name&gt;</c>: plan mode on over the file (a plan in progress left as it stands), its status back to
    /// draft, where it stood said, and — the user's call — a turn asking the model to read it and ask what should change.
    /// With <paramref name="chat"/> (2026-10-18, <c>--chat</c>) nothing is sent: plan mode talks it through (<see cref="PlanSession.ResumeTalking"/>).
    /// </summary>
    private async Task<bool> OpenPlanAsync(string path, bool chat, CancellationToken cancellationToken)
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

        // A plan another instance runs as a Ralph loop is never opened (2026-10-19, the fourth review: its draft status, a revision, an approve here
        // were written under the loop, which put the step edits back as a pass's and wrote its own status over the draft).
        if (RalphLog.HeldElsewhere(_files, path) is { } held)
        {
            _transcript.Error(held);
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

        // A paused Ralph loop on this plan is left (2026-10-18): the file and its log keep everything for a later /plan ralph <name>.
        LeaveRalphOnOpen(path);
        _executingPlan = null;
        _executingShown = null;
        _planDraftReply = null;
        _plan.Open(path, title, requirement, header?.Revision ?? 1, header is { Created: var created } && created != DateTimeOffset.MinValue ? created : null);
        if (chat)
        {
            _plan.ResumeTalking();
        }

        if (PlanFiles.MarkFile(_files, path, PlanStatus.Draft, _time.GetLocalNow(), requirement) is { } problem)
        {
            _transcript.Warning(problem);
        }

        SyncPlanChrome();
        SavePlanState();
        _transcript.Notice(PlanText.OpenedNotice(path, header?.Status, done, total));
        if (chat)
        {
            _transcript.Notice(PlanText.OpenChatNotice);
            return false;
        }

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
        var (saved, error) = PlanFiles.Save(_plan, _files, _time, title, reply, name, references: SavePlanReferences);
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
            case PlanChoice.ApproveRalph:
                return await ApproveRalphAsync(null, cancellationToken).ConfigureAwait(false);
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
    /// not off on <c>/tools</c> — the test the directive's advisors bullet uses. The reviewer page is shown only then. Shared by the gap
    /// review and the code audit since 2026-10-18 (<c>PlanAdvisorsOffered</c> until then).
    /// </summary>
    private bool AdvisorPassOffered()
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
        bool offered = AdvisorPassOffered();
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
    private async Task<bool> SendGapPassAsync(GapAfterTurn pass, CancellationToken cancellationToken) =>
        await SendPassAsync(pass.Message!, [], cancellationToken).ConfigureAwait(false);

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
        var outcome = await RaceAsync<PlanVerdict?>(PlanSpec(plan, PlanCheckLine(plan)), async token => await _planMenu.AskAsync(plan, token).ConfigureAwait(false),
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
        // A Ralph loop's plan is the loop's to mark (2026-10-18): this would mark it done before its final check.
        if (_executingPlan is not { Path: { } path, Ralph: false } executing)
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
