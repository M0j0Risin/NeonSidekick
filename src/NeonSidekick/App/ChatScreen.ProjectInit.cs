using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Llm.Tools;
using NeonSidekick.Plans;
using NeonSidekick.ProjectInit;
using NeonSidekick.Settings;
using NeonSidekick.Skills;
using NeonSidekick.UI;
using NeonSidekick.WebUi;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.App;

// ── /project-init (2026-11-23) ───────────────────────────────────────────────
// Claude Code's /init for NeonSidekick (the user's ask; named /project-init by the user's call of 2026-11-22): the refusals, the questions
// before the turn, the survey under Esc, a new conversation, one narrowed turn, and the Write pane present_project_notes opens inside it.

internal sealed partial class ChatScreen
{
    /// <summary>The /usage Tools tab's group for <c>present_project_notes</c> during a pass.</summary>
    public const string ProjectNotesGroupName = "Project notes";

    private readonly ProjectInitSession _projectInit = new();
    private readonly PresentProjectNotesTool _presentNotes;
    private readonly ProjectNotesApprovalMenu _notesMenu;

    /// <summary>The live skill roots, for the survey's own scan of the folder's project skills.</summary>
    private readonly Func<SkillRoots> _skillRoots;

    /// <summary>
    /// The survey's own catalog (2026-11-23, review #58: one made per run logged the shadow and change notes again), over the same live roots as
    /// <see cref="_catalog"/>, so it follows a profile switch as that one does. Its own, so project skills are listed while the setting is off.
    /// </summary>
    private readonly SkillCatalog _projectInitCatalog;

    /// <summary>
    /// The least room the turn's compact checks leave for the reply (2026-11-23): plan mode's (<see cref="PlanReplyFloor"/>) or, during a
    /// <c>/project-init</c> pass, the draft's (<see cref="ProjectInitSession.ReplyFloor"/>), whichever is larger.
    /// </summary>
    private long TurnReplyFloor() => Math.Max(PlanReplyFloor(_plan, _files), _projectInit.ReplyFloor);

    /// <summary>The turn's <c>/project-init</c> part while a pass runs, else null: its <c>ask_user</c> wording only where <c>ask_user</c> is offered.</summary>
    private PlanTurn? ProjectInitTurn(AppSettingsData effective, IReadOnlySet<string>? disabled = null) =>
        _projectInit.Turn(_presentNotes, AskUserOffered(effective, disabled), effective.LlmMaxToolIterations);

    /// <summary>
    /// Why <c>/project-init</c> can't start now, or null (2026-11-23), in <see cref="AuditRefusal"/>'s order and then its own; none waits on a
    /// model. With a conversation under way, an active audit or a plan being carried out refuses too: the new conversation would end it unasked.
    /// </summary>
    private string? ProjectInitRefusal(AppSettingsData effective)
    {
        if (_projectInit.InPass)
        {
            return ProjectInitText.RunningError;
        }

        if (_plan.Active)
        {
            return ProjectInitText.PlanningError;
        }

        if (!effective.LlmOfferTools)
        {
            return ProjectInitText.NeedsToolsError;
        }

        if (!effective.FileTools || !Without(_fileTools, TurnDisabled(effective)).Any(t => t.Name == ReadFileTool.ToolName))
        {
            return ProjectInitText.NeedsReadFileError;
        }

        if (_plan.Reviewing || _audit.InPass || InRalphPass)
        {
            return ProjectInitText.PassRunningError;
        }

        if (_botChatRunning)
        {
            return ProjectInitText.BotChatError;
        }

        if (!_pane.Enabled)
        {
            return ProjectInitText.NoPaneError;
        }

        if (_session.History.Messages.Count > 0)
        {
            if (_audit.Active)
            {
                return ProjectInitText.ActiveWorkError(ProjectInitText.AuditWork);
            }

            if (_executingPlan is not null)
            {
                return ProjectInitText.ActiveWorkError(ProjectInitText.PlanWork);
            }
        }

        // A folder the write guard keeps (the home, a skill root, the install folder): refused before any model time. Inside AllowProjectFile,
        // as the Write is, so the project-file ask alone doesn't count as a refusal.
        using (WriteGuard.AllowProjectFile())
        {
            if (WriteGuard.Judge(_files.Root, Path.Combine(_files.Root, ProjectFile.PrimaryFileName), WriteRole.Write) is { } outcome && WriteGuard.IsRefusal(outcome))
            {
                return ProjectInitText.GuardError(ProjectFile.PrimaryFileName);
            }
        }

        return null;
    }

    /// <summary>
    /// <c>/project-init [focus …]</c> (2026-11-23): the refusals; the model; which file (a question when only <c>AGENTS.md</c> or <c>CLAUDE.md</c> is
    /// there) and the long-file question; the window; the <i>Session logging</i> off question; the survey under Esc; a new conversation when one is
    /// under way; then one turn, the pass's session ended in a <c>finally</c>. Every step that stops sends nothing, and nothing before the new
    /// conversation touches the old one.
    /// </summary>
    private async Task<bool> HandleProjectInitAsync(string args, IReadOnlyList<ImageAttachment> images, CancellationToken cancellationToken)
    {
        var effective = _effective();
        if (ProjectInitRefusal(effective) is { } refused)
        {
            _transcript.Error(refused);
            return false;
        }

        if (!await ModelOrSayAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        if (await ProjectInitTargetAsync(cancellationToken).ConfigureAwait(false) is not { } target)
        {
            _transcript.Notice(ProjectInitText.CanceledBeforeNotice);
            return false;
        }

        // The file in play's length: a long one is asked about (only its first 16k are read, and Write replaces it whole). One that won't read
        // whole as text is refused (2026-11-23, review #28): it read as no file, and Write replaced it unseen.
        var inPlayRead = target.InPlay is { } inPlay ? _files.ReadText(inPlay, null, null, WorkingDirectory.MaxWriteChars) : null;
        if (inPlayRead is not null && (inPlayRead.Outcome != FileOutcome.Ok || inPlayRead.Truncated || inPlayRead.LineCut))
        {
            _transcript.Error(ProjectInitText.UnreadableNotice(target.InPlay!));
            return false;
        }

        int inPlayLength = inPlayRead?.Text.Length ?? 0;
        if (inPlayLength > ProjectFile.MaxLength
            && await ProjectInitAskAsync(ProjectInitText.LongTitle, ProjectInitText.LongCaption(target.InPlay!, inPlayLength, beside: target.Mode == ProjectInitMode.CreateBeside),
                [ProjectInitText.ContinueRow, ProjectInitText.CancelRow], ["continue", "cancel"], 1, cancellationToken).ConfigureAwait(false) != 0)
        {
            _transcript.Notice(ProjectInitText.CanceledBeforeNotice);
            return false;
        }

        // The window: the file in play and the survey's share must leave half of it for the rest, the draft among it.
        int windowTokens = _session.ContextLength?.Tokens ?? 0;
        int budget = ProjectDigest.BudgetFor(windowTokens);
        if (windowTokens > 0 && Math.Min(inPlayLength, ProjectFile.MaxLength) + budget > windowTokens * ConversationCompactor.CharsPerToken / 2)
        {
            _transcript.Error(ProjectInitText.WindowError(windowTokens));
            return false;
        }

        bool fresh = _session.History.Messages.Count > 0;
        if (fresh && !effective.SessionLogging
            && await ProjectInitAskAsync(ProjectInitText.LoggingTitle, ProjectInitText.LoggingCaption, [ProjectInitText.ContinueRow, ProjectInitText.CancelRow], ["continue", "cancel"], 1, cancellationToken).ConfigureAwait(false) != 0)
        {
            _transcript.Notice(ProjectInitText.CanceledBeforeNotice);
            return false;
        }

        if (await ProjectInitSurveyAsync(target.Mode, target.InPlay, budget, effective, cancellationToken).ConfigureAwait(false) is not { } digest)
        {
            _transcript.Notice(ProjectInitText.SurveyStoppedNotice);
            return false;
        }

        // A new conversation (the user's call, 2026-11-22): the draft can't pick up anything said earlier, or the opening memory result. The queue
        // stays (the audit's light reset): a line queued behind the command runs after the pass, in the new conversation.
        if (fresh)
        {
            DropUndoNote();
            ResetConversationLight(ProjectInitText.NewConversationNotice);
        }

        string focus = args.Trim();
        var start = new ProjectInitStart(_files.Root, target.Target, target.Mode, digest.Text, focus.Length > 0 ? focus : null, target.Hidden,
            digest.InPlayLength, digest.InPlayMasked, digest.Subfolder, digest.Cut, windowTokens);
        DiagnosticLog.Info(AppCategory, ProjectInitText.StartLogLine(target.Target, digest.Text.Length, digest.Cut,
            focus.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length, windowTokens));
        string typed = (ProjectInitText.Word + " " + focus).Trim();
        _projectInit.Begin(start);
        _customCommandTitle = typed;   // the new session's title is the typed line (LogTurn), not the generated message
        try
        {
            // The flags from this turn alone (2026-11-23, review #79): an earlier turn's cancel or failure hid the notice. Failed until the turn
            // runs, as Ralph's (2026-11-24, review #3): a message refused before its turn (no server) left false and printed "no draft".
            _lastTurnCancelled = false;
            _lastTurnFailed = true;
            _lastTurnFailure = TurnFailure.None;
            bool exit = await RunMessageAsync(ProjectInitText.Message(start.Focus), images, cancellationToken, new TurnMark(typed, AppWritten: true, IsPass: true), putBack: false).ConfigureAwait(false);

            // No answer: no draft, or a Revise with no new one (review #97); a failed write said so itself, as a refused one did (review #61).
            if (_projectInit.Answer == ProjectInitAnswer.None && !_projectInit.WriteFailed && !_projectInit.WriteRefused && !_lastTurnCancelled && !_lastTurnFailed)
            {
                _transcript.Notice(_projectInit.Presented ? ProjectInitText.NoRevisedDraftNotice : ProjectInitText.NoDraftNotice);
            }

            return exit;
        }
        finally
        {
            _projectInit.End();
            _customCommandTitle = null;
        }
    }

    /// <summary>Which file and how (2026-11-23): improve a <c>NEON.md</c>; with only <c>AGENTS.md</c> or <c>CLAUDE.md</c>, ask; else create <c>NEON.md</c>. Null when canceled.</summary>
    private async Task<(string Target, ProjectInitMode Mode, string? InPlay, string? Hidden)?> ProjectInitTargetAsync(CancellationToken cancellationToken)
    {
        string root = _files.Root;
        if (File.Exists(Path.Combine(root, ProjectFile.PrimaryFileName)))
        {
            return (ProjectFile.PrimaryFileName, ProjectInitMode.Improve, ProjectFile.PrimaryFileName, null);
        }

        if (new[] { ProjectFile.SecondaryFileName, ProjectFile.TertiaryFileName }.FirstOrDefault(n => File.Exists(Path.Combine(root, n))) is not { } other)
        {
            return (ProjectFile.PrimaryFileName, ProjectInitMode.Create, null, null);
        }

        return await ProjectInitAskAsync(ProjectInitText.TargetTitle, ProjectInitText.TargetCaption(other),
                [ProjectInitText.UpdateRow(other), ProjectInitText.CreateNeonRow, ProjectInitText.CancelRow], ["update", "create", "cancel"], 0, cancellationToken).ConfigureAwait(false) switch
        {
            0 => (other, ProjectInitMode.Improve, other, null),
            1 => (ProjectFile.PrimaryFileName, ProjectInitMode.CreateBeside, other, other),
            _ => null,
        };
    }

    /// <summary>
    /// A question before the turn (2026-11-23): no turn runs yet, so <see cref="RaceAsync"/>, as <c>/plan approve</c>'s pane is: typed at the PC
    /// the pane asks; from the page, the page and the PC. The row picked, null for Esc or the page's Cancel.
    /// </summary>
    private async Task<int?> ProjectInitAskAsync(string title, string caption, IReadOnlyList<string> rows, IReadOnlyList<string> ids, int cursor, CancellationToken cancellationToken)
    {
        var page = new MenuPage(title, rows.Select(Markup.Escape).ToList(), ProjectInitText.QuestionKeys) { Caption = caption, CaptionGrows = true };
        var spec = new WebAskSpec("confirm", title, ids.Zip(rows, (id, row) => new WebChoice(id, row)).ToList(), Body: caption);
        var outcome = await RaceAsync<int?>(spec, async token =>
        {
            try
            {
                return await _menuPane.PickAsync(page, cursor, token).ConfigureAwait(false) is { } pick && pick.Row >= 0 && pick.Row < rows.Count ? pick.Row : null;
            }
            finally
            {
                _menuPane.Close();
            }
        }, answer => ids.ToList().IndexOf(answer.Choice ?? "") is var at and >= 0 ? at : null, "The /project-init question failed: ", cancellationToken).ConfigureAwait(false);
        int? row = outcome.Value;
        return row is { } picked && string.Equals(ids[picked], "cancel", StringComparison.Ordinal) ? null : row;
    }

    /// <summary>The survey off the screen's thread under a spinner and the watch (Esc stops it); null when it was stopped.</summary>
    private async Task<ProjectDigestResult?> ProjectInitSurveyAsync(ProjectInitMode mode, string? inPlay, int budget, AppSettingsData effective, CancellationToken cancellationToken)
    {
        ProjectDigestResult? result = null;
        string? skillsOff = !effective.AgentSkills ? ProjectInitText.AgentSkillsSetting : !effective.ProjectSkills ? SettingsMenu.FieldName(SettingsField.ProjectSkills) : null;
        bool stopped;
        try
        {
            stopped = await WaitUnderWatchAsync(token => _transcript.WithSpinnerAsync(ProjectInitText.SurveyingSpinner, async () =>
            {
                result = await Task.Run(() =>
                {
                    var input = new ProjectDigestInput(_files, _git, mode, inPlay, budget) { Skills = ProjectInitSkills(token), SkillsOff = skillsOff };
                    return ProjectDigest.Build(input, token);
                }, token).ConfigureAwait(false);
                return true;
            }), KeySource.IsTurnCancel, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopped = true;
        }

        return stopped ? null : result;
    }

    /// <summary>
    /// The folder's project skills for the survey, trusted or not (2026-11-23), read by a scan of their own so the setting being off doesn't hide
    /// them: the notes are for other clients too. Names, descriptions and the folder each sits in.
    /// </summary>
    private IReadOnlyList<ProjectDigestSkill> ProjectInitSkills(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var catalog = _projectInitCatalog;
        catalog.Scan(new SkillScan(false, _projectSource));
        string working = _files.Root;
        string? repoRoot = ProjectRepoRoot();
        return catalog.All.Concat(catalog.Shadowed).Concat(catalog.ProjectPending.Select(p => p.Skill))
            .Where(s => s.Scope == SkillScope.Project)
            .Select(s => new ProjectDigestSkill(s.Name, s.Description, SkillsText.FolderLabel(s.TrustFolder ?? working, working, repoRoot)))
            .DistinctBy(s => (s.Name, s.Folder))
            .ToList();
    }

    /// <summary>
    /// <c>present_project_notes</c>' pane (2026-11-23, inside the call): the draft printed, then the Write pane on the PC and the page at once
    /// (<see cref="AskBothAsync"/>), the first answer winning. Null when nobody could be asked (no pane, no watcher): nothing is written.
    /// </summary>
    private async Task<ProjectNotesVerdict?> ReviewProjectNotesAsync(ProjectNotesDraft draft, CancellationToken turnToken)
    {
        var start = _projectInit.Start;
        _transcript.AppendDelta("\n\n" + draft.Content.Trim() + "\n\n");
        if (!_pane.Enabled || start is null)
        {
            _transcript.Notice(ProjectInitText.NoPaneNotice);
            return null;
        }

        var effective = _effective();
        string? off = NotesOff(effective);
        string? turnOn = off is not null && NotesOffFields(effective).All(f => _overriddenBy(f) is null) ? off : null;
        var caption = ProjectInitText.CaptionLines(draft, start, off, Path.GetFileName(_files.Root));
        var outcome = await AskBothAsync<ProjectNotesVerdict?>(NotesSpec(draft, caption, turnOn), async token => await _notesMenu.AskAsync(draft, caption, turnOn, token).ConfigureAwait(false),
            NotesVerdictOf, "The project notes pane failed: ", turnToken).ConfigureAwait(false);
        if (outcome.NeverAsked)
        {
            _transcript.Notice(ProjectInitText.NoPaneNotice);
            return null;
        }

        var verdict = outcome.Value ?? new ProjectNotesVerdict(ProjectNotesChoice.Cancel);
        switch (verdict.Choice)
        {
            case ProjectNotesChoice.Cancel:
                _transcript.Notice(ProjectInitText.CanceledNotice(draft.Target));
                break;
            case ProjectNotesChoice.Revise when verdict.Feedback is { } feedback:
                _transcript.Notice(PlanText.FeedbackNotice(feedback));
                break;
        }

        return verdict;
    }

    /// <summary>After a Write (2026-11-23): the notice, Write and turn on's settings, or a failed write's error (the settings left alone).</summary>
    private void OnProjectNotesWritten(ProjectNotesVerdict verdict, ProjectNotesDraft draft, WriteResult wrote)
    {
        if (wrote.Outcome != FileOutcome.Ok)
        {
            _transcript.Error(ProjectInitText.WriteFailedNotice(FileText.Wrote(wrote)));
            return;
        }

        string? turnedOn = null;
        var effective = _effective();
        if (verdict.Choice == ProjectNotesChoice.WriteAndTurnOn && NotesOff(effective) is { } off)
        {
            // Only what is off now (2026-11-23, review #101): one on through a flag or an env var keeps its saved value.
            var fields = NotesOffFields(effective).ToList();
            _settings.Update(d =>
            {
                if (fields.Contains(SettingsField.AgentSkills))
                {
                    d.AgentSkills = true;
                }

                if (fields.Contains(SettingsField.ProjectFile))
                {
                    d.ProjectFile = true;
                }
            });
            turnedOn = off;
        }

        _transcript.Notice(draft.Unchanged && turnedOn is null ? ProjectInitText.UnchangedNotice(draft.Target) : ProjectInitText.WroteNotice(draft.Target, draft.Content.Length, turnedOn));
    }

    /// <summary>
    /// The draft's View (2026-11-23): the change uncut (the info pane scrolls), or the whole draft for a new file, rendered as markdown on a first
    /// tab since 2026-11-24 (the user's ask, as the Web UI's notes card shows it) with its text on Source; a warning over the rendering when the
    /// draft holds link-definition lines it hides (later that day, the last-7 review's #7, the user's pick: both Views).
    /// </summary>
    private Task ViewProjectNotesAsync(ProjectNotesDraft draft, CancellationToken cancellationToken)
    {
        IRenderable content = draft.Diff is { } diff ? new DiffView(null, diff, int.MaxValue)
            : new Text(draft.Unchanged ? ProjectInitText.NoChanges(draft.Target) : draft.Content);
        string label = ProjectInitText.ViewLabel(draft.Target);
        IReadOnlyList<InfoTab> tabs = draft.Diff is null && !draft.Unchanged
            ? [new InfoTab(SkillsText.RenderedTab, () => Skills.SkillView.Warned(UI.Markdown.MarkdownView.Of(draft.Content), draft.Content)), new InfoTab(SkillsText.SourceTab, () => content)]
            : [new InfoTab(label, () => content)];
        return _info.ShowAsync(label, tabs, 0, cancellationToken);
    }

    /// <summary>What keeps NeonSidekick from reading the notes: <c>Agent skills</c>, <c>Project file</c>, both, or null.</summary>
    private static string? NotesOff(AppSettingsData effective) => (effective.AgentSkills, effective.ProjectFile) switch
    {
        (false, false) => ProjectInitText.BothOff,
        (false, true) => ProjectInitText.AgentSkillsSetting,
        (true, false) => ProjectInitText.ProjectFileSetting,
        _ => null,
    };

    private static IEnumerable<SettingsField> NotesOffFields(AppSettingsData effective)
    {
        if (!effective.AgentSkills)
        {
            yield return SettingsField.AgentSkills;
        }

        if (!effective.ProjectFile)
        {
            yield return SettingsField.ProjectFile;
        }
    }

    /// <summary>
    /// The page's form of the Write pane (2026-11-23): the caption's lines, the draft whole, the same choices; Revise takes words. The page renders
    /// the draft with the same Markdig pipeline, so a draft holding link-definition lines gets the warning as the caption's last line (2026-11-24,
    /// the last-7 review's #7).
    /// </summary>
    internal static WebAskSpec NotesSpec(ProjectNotesDraft draft, IReadOnlyList<string> caption, string? turnOn)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var choices = new List<WebChoice> { new(ProjectInitText.WriteId, ProjectInitText.WriteRow(draft.Target)) };
        if (turnOn is not null)
        {
            choices.Add(new WebChoice(ProjectInitText.WriteOnId, ProjectInitText.WriteAndTurnOnRow(draft.Target, turnOn)));
        }

        choices.Add(new WebChoice(ProjectInitText.ReviseId, ProjectInitText.ReviseRow));
        choices.Add(new WebChoice(ProjectInitText.CancelId, ProjectInitText.NotesCancelRow));
        int hidden = UI.Markdown.MarkdigParser.LinkDefinitionCount(draft.Content);
        IReadOnlyList<string> lines = hidden > 0 ? [.. caption, SkillsText.HiddenLinesWarning(hidden, source: false)] : caption;
        return new WebAskSpec("notes", ProjectInitText.NotesTitle, choices, Lines: lines, Markdown: draft.Content, TextChoice: ProjectInitText.ReviseId);
    }

    /// <summary>The page's answer as the pane would give it; a Revise with blank words is a Revise with none.</summary>
    internal static ProjectNotesVerdict NotesVerdictOf(WebAnswerRequest answer) => answer.Choice switch
    {
        ProjectInitText.WriteId => new ProjectNotesVerdict(ProjectNotesChoice.Write),
        ProjectInitText.WriteOnId => new ProjectNotesVerdict(ProjectNotesChoice.WriteAndTurnOn),
        ProjectInitText.ReviseId => new ProjectNotesVerdict(ProjectNotesChoice.Revise, (answer.Text ?? "").ReplaceLineEndings(" ").Trim() is { Length: > 0 } words ? words : null),
        _ => new ProjectNotesVerdict(ProjectNotesChoice.Cancel),
    };
}
