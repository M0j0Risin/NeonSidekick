using NeonSidekick.Diagnostics;
using NeonSidekick.Skills;

namespace NeonSidekick.App;

// ── /skills purge (2026-09-30) ────────────────────────────────────────────

internal sealed partial class ChatScreen
{
    /// <summary>The age as typed after <c>purge list</c> or <c>purge commit</c>, for the dry run's hint: <c>30</c>, <c>12h</c>, <c>1d 6h</c>.</summary>
    private static string AgeWords(string args) =>
        string.Join(' ', args.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Skip(2));

    /// <summary>
    /// <c>/skills purge list &lt;age&gt;</c> and <c>/skills purge commit &lt;age&gt;</c> (2026-09-30, the user's ask: purge the skills
    /// not used for a while; <c>list</c> the dry run, <c>commit</c> the delete, the user's grammar). Both reconcile first, then
    /// read the skills in this profile's view, the global ones and its own, whose last use is older than <paramref name="age"/>.
    /// A skill never loaded goes by its last change instead (<see cref="SkillRecord.Reference"/>).
    /// <list type="bullet">
    /// <item><c>list</c> prints them, one line each under a head line that names the commit, and changes nothing.</item>
    /// <item><c>commit</c> asks a yes/no whose caption lists them. A yes deletes each folder through <see cref="SkillEditor.Delete"/>,
    /// the Skills pane's own permanent delete with its guard, then its record. A folder that will not go keeps its record and is
    /// an error line, and the catalog is scanned again at the end.</item>
    /// </list>
    /// External skills are never recorded, so they are never purged.
    /// </summary>
    private async Task PurgeSkillsAsync(bool commit, TimeSpan age, string ageWords, CancellationToken cancellationToken)
    {
        ReconcileSkills();
        var now = _time.GetUtcNow();
        var cutoff = age > now - DateTimeOffset.MinValue ? DateTimeOffset.MinValue : now - age;
        var stale = _skillRecords.UnusedSince(cutoff);
        if (stale.Count == 0)
        {
            _transcript.Notice(SkillRecordText.NoneNotice(age));
            return;
        }

        var zone = _time.LocalTimeZone;
        if (!commit)
        {
            _transcript.Notice(SkillRecordText.ListNotice(stale.Count, age, ageWords));
            foreach (var record in stale)
            {
                _transcript.Notice("  " + SkillRecordText.Line(record, zone));
            }

            return;
        }

        string question = SkillRecordText.CommitPrompt(stale.Count, age);
        bool yes = _menuPane.Enabled
            ? await ConfirmWithCaptionAsync(question, string.Join("; ", stale.Select(r => SkillRecordText.Line(r, zone))), cancellationToken).ConfigureAwait(false)
            : await ConfirmAsync(question, cancellationToken).ConfigureAwait(false);
        if (!yes)
        {
            _transcript.Notice(KeptNotice);
            return;
        }

        var roots = _catalog.Roots;
        var deleted = new List<string>();
        int failed = 0;
        foreach (var record in stale)
        {
            var skill = new Skill(record.Name, "", record.Scope, Path.Combine(roots.Of(record.Scope), record.Folder));
            var result = SkillEditor.Delete(roots, skill);
            if (result.Outcome is SkillEditOutcome.Deleted or SkillEditOutcome.Missing)
            {
                // Missing: the folder went already between the reconcile and now; the record goes all the same.
                _skillRecords.Deleted(record.Scope, record.Folder);
                deleted.Add(record.Folder);
                DiagnosticLog.Info(SkillCatalog.Category, SkillRecordText.PurgedLogLine(record));
            }
            else
            {
                failed++;
                _transcript.Error(SkillRecordText.PurgeFailedError(record, result.Detail));
                DiagnosticLog.Error(SkillCatalog.Category, SkillRecordText.PurgeFailedError(record, result.Detail));
            }
        }

        _catalog.Scan(_effective().ExternalSkills);
        DiagnosticLog.Info(SkillCatalog.Category, SkillRecordText.PurgeSummaryLogLine(deleted.Count, failed, age));
        if (deleted.Count > 0)
        {
            _transcript.Notice(SkillRecordText.PurgedNotice(deleted, age));
        }
    }

    /// <summary>The yes/no on the pane with <paramref name="caption"/> under the question (the rows and keys of <see cref="SettingsMenu.ConfirmAsync"/>, the cursor on No).</summary>
    private async Task<bool> ConfirmWithCaptionAsync(string question, string caption, CancellationToken cancellationToken)
    {
        var page = new UI.MenuPage(question, SettingsMenu.ConfirmRows, SettingsMenu.ConfirmKeys) { Hotkeys = SettingsMenu.ConfirmHotkeys, Caption = caption };
        try
        {
            return await _menuPane.PickAsync(page, 0, cancellationToken).ConfigureAwait(false) is { Row: 1 };
        }
        finally
        {
            _menuPane.Close();
        }
    }
}
