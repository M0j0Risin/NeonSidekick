using NeonSidekick.Skills;
using NeonSidekick.Web;

namespace NeonSidekick.App;

/// <summary>
/// <c>/skills add &lt;source&gt;</c> on the screen (2026-09-26, the user's ask: search and download
/// Agent Skills into the app). The flow is <see cref="SkillInstallFlow"/>'s, shared with headless;
/// this is its host over the transcript (the preview printed as a reply, the <c>/echo</c> path), the
/// spinner, <see cref="SkillInstallMenu"/>'s panes and — where no pane opens — the typed <c>y</c>
/// question (<see cref="ConfirmAsync"/>) and the hits listed as lines. Refused mid-turn
/// (<see cref="MidTurnPolicy"/>); idle only, so the panes run on the screen's own task.
/// </summary>
internal sealed partial class ChatScreen
{
    /// <summary>What followed <c>/skills</c>: <c>add &lt;source&gt;</c>, <c>purge list|commit &lt;age&gt;</c> (2026-09-30), or the usage error.</summary>
    private async Task HandleSkillsArgumentAsync(string args, CancellationToken cancellationToken)
    {
        var (purge, age) = SkillRecordText.ParsePurge(args);
        if (purge != SkillRecordText.PurgeKind.None)
        {
            if (purge == SkillRecordText.PurgeKind.Invalid)
            {
                _transcript.Error(SkillRecordText.SkillsUsageError);
                return;
            }

            await PurgeSkillsAsync(purge == SkillRecordText.PurgeKind.Commit, age, AgeWords(args), cancellationToken).ConfigureAwait(false);
            return;
        }

        string[] parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length > 0 && parts[0].Equals(SkillRecordText.RevertWord, StringComparison.OrdinalIgnoreCase))
        {
            HandleSkillRevert(parts.Length < 2 ? "" : parts[1]);
            return;
        }

        if (!parts[0].Equals(SkillInstallText.AddWord, StringComparison.OrdinalIgnoreCase) || parts.Length < 2)
        {
            _transcript.Error(SkillRecordText.SkillsUsageError);
            return;
        }

        await _skillInstall.RunAsync(parts[1], new ScreenSkillHost(this), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>/skills revert &lt;name&gt;</c> (2026-10-02, the reflection audit): the skill put back as it was before its last change the app
    /// made (<see cref="RevertSkill"/>), the notice saying what came back; a name the catalog lacks, nothing kept, or a hand edit since
    /// is an error. External skills are never recorded, so never reverted.
    /// </summary>
    private void HandleSkillRevert(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            _transcript.Error(SkillRecordText.RevertUsageError);
            return;
        }

        _catalog.Scan(_effective().ExternalSkills);
        if (_catalog.Find(name.Trim()) is not { Scope: not SkillScope.External } skill)
        {
            _transcript.Error(SkillRecordText.RevertUnknownError(name.Trim()));
            return;
        }

        var (ok, text) = SkillRecordText.RevertText(skill.Name, RevertSkill(skill), _time.LocalTimeZone);
        if (ok)
        {
            _transcript.Notice(text);
            _catalog.Scan(_effective().ExternalSkills);
        }
        else
        {
            _transcript.Error(text);
        }
    }

    /// <summary>The revert itself, the command's and the pane's: a reconcile first, so a hand edit since is known and refuses it.</summary>
    private SkillRevert RevertSkill(Skill skill)
    {
        ReconcileSkills();
        return _skillRecords.Revert(skill);
    }

    /// <summary>The flow's host on the screen.</summary>
    private sealed class ScreenSkillHost(ChatScreen screen) : ISkillInstallHost
    {
        public bool Headless => false;

        public SkillRoots Roots => screen._catalog.Roots;

        public bool External => screen._effective().ExternalSkills;

        public bool SkillsEnabled => screen._effective().AgentSkills;

        public FetchOptions FetchOptions => WebAccess.Options(screen._effective());

        public TimeProvider Time => screen._time;

        public void Notice(string text) => screen._transcript.Notice(text);

        public void Warning(string text) => screen._transcript.Warning(text);

        public void Error(string text) => screen._transcript.Error(text);

        public void Preview(string markdown)
        {
            var effective = screen._effective();
            screen._transcript.BeginAssistant(StyledReply(effective.TranscriptMarkdown, screen._pane.Enabled));
            screen._transcript.AppendDelta(markdown);
            screen._transcript.EndAssistant();
        }

        public Task<T> SpinAsync<T>(string label, Func<Task<T>> work) => screen._transcript.WithSpinnerAsync(label, work);

        public async Task<int?> PickAsync(string title, IReadOnlyList<string> rows, CancellationToken cancellationToken)
        {
            if (screen._menuPane.Enabled)
            {
                return await screen._skillInstallMenu.PickAsync(title, rows, cancellationToken).ConfigureAwait(false);
            }

            // No pane (a redirected console): the rows as lines, and nothing picked — the ids type back.
            screen._transcript.Notice(SkillInstallText.HeadlessPickHint);
            foreach (string row in rows)
            {
                screen._transcript.Notice("  " + row);
            }

            return null;
        }

        public async Task<SkillScope?> ConfirmAsync(SkillCandidate candidate, SkillSource source, SkillInstallCheck check, SkillScope? preselect, CancellationToken cancellationToken)
        {
            if (screen._menuPane.Enabled)
            {
                return await screen._skillInstallMenu.AskAsync(candidate, source, check, cancellationToken).ConfigureAwait(false);
            }

            var scope = check.Scope ?? preselect ?? SkillScope.Profile;
            return await screen.ConfirmAsync(SkillInstallText.TypedQuestion(candidate.Name, scope), cancellationToken).ConfigureAwait(false) ? scope : null;
        }

        public void Rescan() => screen._catalog.Scan(screen._effective().ExternalSkills);

        public void Installed(SkillInstallResult result) => screen._skillRecords.Installed(result);

        public int ReflectionChangesSinceInstall(SkillScope scope, string name) =>
            screen._catalog.Find(name) is { } skill && skill.Scope == scope ? screen._skillRecords.ReflectionChangesSinceInstall(skill) : 0;
    }
}
