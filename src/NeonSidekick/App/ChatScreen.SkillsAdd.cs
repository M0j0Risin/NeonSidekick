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
        if (!parts[0].Equals(SkillInstallText.AddWord, StringComparison.OrdinalIgnoreCase) || parts.Length < 2)
        {
            _transcript.Error(SkillRecordText.SkillsUsageError);
            return;
        }

        await _skillInstall.RunAsync(parts[1], new ScreenSkillHost(this), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The Skills pane's revert list (2026-10-04, the user's call: the pane's row, <c>/skills revert &lt;name&gt;</c> gone): a reconcile
    /// first, so a hand edit since is known and its copy is on the list, then every kept version, newest first.
    /// </summary>
    private IReadOnlyList<SkillRevision> SkillVersions(Skill skill)
    {
        ReconcileSkills();
        return _skillRecords.Revisions(skill);
    }

    /// <summary>The version picked on the Skills pane put back (<see cref="SkillRecords.Restore"/>), after a reconcile as the list had.</summary>
    private SkillRevert RestoreSkill(Skill skill, SkillRevision revision)
    {
        ReconcileSkills();
        return _skillRecords.Restore(skill, revision);
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
