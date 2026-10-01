using NeonSidekick.Diagnostics;

namespace NeonSidekick.Skills;

/// <summary>What one reconcile changed (<see cref="SkillRecords.Reconcile"/>).</summary>
public readonly record struct SkillReconcile(int Added, int Removed, int Modified, int ProfilesForgotten)
{
    public bool Changed => Added + Removed + Modified + ProfilesForgotten > 0;
}

/// <summary>
/// The app's record of its skills (2026-09-30, the user's ask): when each was created, modified and last used, over
/// <see cref="SkillRecordStore"/>. The writers tell it what they did:
/// <list type="bullet">
/// <item><c>skill_editor</c> and the reflection, through <see cref="Edited"/>;</item>
/// <item><c>/skills add</c>, through <see cref="Installed"/>;</item>
/// <item>the Skills pane's move, rename and delete, through <see cref="Moved"/>, <see cref="Renamed"/> and <see cref="Deleted"/>;</item>
/// <item><c>load_skill</c> and the <c>/botchat</c> preload, through <see cref="Used"/>.</item>
/// </list>
/// Anything the app did not do itself (a hand edit, an edit in the editor the pane opened, a folder dropped in or taken out)
/// is caught by <see cref="Reconcile"/> at startup, at every profile load, after an install and before a purge.
/// <para>The profile a row belongs to is the name of the folder the profile's skills root sits in
/// (<c>&lt;home&gt;\profiles\&lt;name&gt;\skills</c>), read from the roots in force (<see cref="ProfileOf"/>). A reflection decided
/// before a profile switch therefore records under the profile it wrote into. External skills are never recorded (the
/// user's pick: the app treats that folder as read-only). Each change is a Debug line under <c>Skills</c>, and a reconcile
/// that changed anything is one Info line.</para>
/// </summary>
public sealed class SkillRecords
{
    private readonly SkillRecordStore _store;
    private readonly Func<SkillRoots> _roots;
    private readonly TimeProvider _time;

    /// <param name="store">The rows.</param>
    /// <param name="roots">The roots in force: the profile's moves with a switch.</param>
    /// <param name="time">The clock behind the app's own moments; the reconcile takes the files' own.</param>
    public SkillRecords(SkillRecordStore store, Func<SkillRoots> roots, TimeProvider? time = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _roots = roots ?? throw new ArgumentNullException(nameof(roots));
        _time = time ?? TimeProvider.System;
    }

    public SkillRecordStore Store => _store;

    /// <summary>The profile <paramref name="roots"/>' profile root belongs to: the name of the folder it sits in.</summary>
    public static string ProfileOf(SkillRoots roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        return Path.GetFileName(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(roots.Profile)))) ?? "";
    }

    /// <summary>The profile of the roots in force.</summary>
    public string CurrentProfile => ProfileOf(_roots());

    /// <summary>
    /// A <c>skill_editor</c> result, the model's or a reflection's, written under <paramref name="roots"/>. A create is
    /// <see cref="SkillRecordStore.Created"/>, and an update or a bundled file written is <see cref="SkillRecordStore.Modified"/>.
    /// Every refusal is nothing. The folder is the result's name: the editor finds a skill by its folder.
    /// </summary>
    public void Edited(SkillRoots roots, SkillEditResult result)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(result);
        if (result.Scope == SkillScope.External || result.Name.Length == 0)
        {
            return;
        }

        string profile = ProfileOf(roots);
        var now = _time.GetUtcNow();
        switch (result.Outcome)
        {
            case SkillEditOutcome.Created:
                _store.Created(result.Scope, profile, result.Name, result.Name, now);
                DiagnosticLog.Debug(SkillCatalog.Category, SkillRecordText.CreatedLogLine(result.Scope, result.Name));
                break;
            case SkillEditOutcome.Updated or SkillEditOutcome.FileWritten or SkillEditOutcome.FileEdited:
                _store.Modified(result.Scope, profile, result.Name, result.Name, now);
                DiagnosticLog.Debug(SkillCatalog.Category, SkillRecordText.ModifiedLogLine(result.Scope, result.Name));
                break;
        }
    }

    /// <summary><c>/skills add</c> put a skill in <paramref name="directory"/>: created, or modified when it replaced one.</summary>
    public void Installed(SkillScope scope, string directory, bool updated)
    {
        ArgumentNullException.ThrowIfNull(directory);
        if (scope == SkillScope.External)
        {
            return;
        }

        string folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
        string profile = CurrentProfile;
        var now = _time.GetUtcNow();
        if (updated)
        {
            _store.Modified(scope, profile, folder, folder, now);
            DiagnosticLog.Debug(SkillCatalog.Category, SkillRecordText.ModifiedLogLine(scope, folder));
        }
        else
        {
            _store.Created(scope, profile, folder, folder, now);
            DiagnosticLog.Debug(SkillCatalog.Category, SkillRecordText.CreatedLogLine(scope, folder));
        }
    }

    /// <summary>The skill's instructions were loaded (<c>load_skill</c>, a <c>/botchat</c> preload): last used now.</summary>
    public void Used(Skill skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        if (skill.Scope == SkillScope.External)
        {
            return;
        }

        _store.Used(skill.Scope, CurrentProfile, skill.FolderName, skill.Name, _time.GetUtcNow());
        DiagnosticLog.Debug(SkillCatalog.Category, SkillRecordText.UsedLogLine(skill.Scope, skill.FolderName));
    }

    /// <summary>The pane moved <paramref name="skill"/> to the <paramref name="to"/> root: the row follows, keeping its moments.</summary>
    public void Moved(Skill skill, SkillScope to)
    {
        ArgumentNullException.ThrowIfNull(skill);
        string profile = CurrentProfile;
        _store.Move(skill.Scope, profile, skill.FolderName, to, profile, skill.FolderName, skill.Name);
        DiagnosticLog.Debug(SkillCatalog.Category, SkillRecordText.MovedLogLine(skill.Scope, to, skill.FolderName));
    }

    /// <summary>The pane renamed <paramref name="skill"/>'s folder and name to <paramref name="name"/>: the row follows, keeping its moments.</summary>
    public void Renamed(Skill skill, string name)
    {
        ArgumentNullException.ThrowIfNull(skill);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string profile = CurrentProfile;
        _store.Move(skill.Scope, profile, skill.FolderName, skill.Scope, profile, name, name);
        DiagnosticLog.Debug(SkillCatalog.Category, SkillRecordText.RenamedLogLine(skill.Scope, skill.FolderName, name));
    }

    /// <summary>The skill's folder was deleted (the pane, a purge): its row goes.</summary>
    public void Deleted(SkillScope scope, string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (_store.Delete(scope, CurrentProfile, folder))
        {
            DiagnosticLog.Debug(SkillCatalog.Category, SkillRecordText.DeletedLogLine(scope, folder));
        }
    }

    /// <summary>A profile was renamed: its rows follow it.</summary>
    public void RenameProfile(string from, string to)
    {
        int moved = _store.RenameProfile(from, to);
        if (moved > 0)
        {
            DiagnosticLog.Debug(SkillCatalog.Category, SkillRecordText.ProfileRenamedLogLine(from, to, moved));
        }
    }

    /// <summary>
    /// The rows brought in line with the folders on disk, the global root and the profile's. The steps:
    /// <list type="number">
    /// <item>A skill folder with no row gets one, carrying its <c>SKILL.md</c>'s creation and last-write times (the user's rule
    /// for new entries).</item>
    /// <item>A row whose <c>SKILL.md</c> was written since its modified time moves to the file's time. That catches an edit the
    /// app could not see.</item>
    /// <item>A row whose folder is gone goes.</item>
    /// <item>The rows of a profile whose folder no longer exists go too.</item>
    /// </list>
    /// Folders are matched by name without case. A dot-folder (the installer's parked copy) is no skill. <paramref name="known"/>
    /// is the catalog's skills, for their frontmatter names. A folder it does not know keeps its folder name.
    /// </summary>
    public SkillReconcile Reconcile(IEnumerable<Skill> known)
    {
        ArgumentNullException.ThrowIfNull(known);
        if (!_store.Available)
        {
            return default;
        }

        var roots = _roots();
        string profile = ProfileOf(roots);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var skill in known)
        {
            names.TryAdd(Path.TrimEndingDirectorySeparator(Path.GetFullPath(skill.Directory)), skill.Name);
        }

        var rows = _store.List(profile).ToList();
        int added = 0, removed = 0, modified = 0;
        var seen = new HashSet<long>();
        foreach (var scope in new[] { SkillScope.Global, SkillScope.Profile })
        {
            foreach (string directory in SkillCatalog.Folders(roots.Of(scope)))
            {
                string folder = Path.GetFileName(directory);
                if (folder.StartsWith('.'))
                {
                    continue;
                }

                string file = Path.Combine(directory, SkillCatalog.FileName);
                DateTimeOffset created, written;
                try
                {
                    created = new DateTimeOffset(File.GetCreationTimeUtc(file), TimeSpan.Zero);
                    written = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                string name = names.TryGetValue(directory, out string? known1) ? known1 : folder;
                var row = rows.Find(r => r.Scope == scope && string.Equals(r.Folder, folder, StringComparison.OrdinalIgnoreCase));
                if (row is null)
                {
                    if (_store.Insert(scope, profile, folder, name, created, written))
                    {
                        added++;
                        DiagnosticLog.Debug(SkillCatalog.Category, SkillRecordText.FoundLogLine(scope, folder));
                    }

                    continue;
                }

                seen.Add(row.Id);
                if (written > row.Modified || !string.Equals(name, row.Name, StringComparison.Ordinal))
                {
                    _store.Refresh(row.Id, name, written > row.Modified ? written : row.Modified);
                    if (written > row.Modified)
                    {
                        modified++;
                        DiagnosticLog.Debug(SkillCatalog.Category, SkillRecordText.ModifiedLogLine(scope, folder));
                    }
                }
            }
        }

        foreach (var row in rows.Where(r => !seen.Contains(r.Id)))
        {
            _store.Delete(row.Id);
            removed++;
            DiagnosticLog.Debug(SkillCatalog.Category, SkillRecordText.GoneLogLine(row.Scope, row.Folder));
        }

        int forgotten = 0;
        string? profilesRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(roots.Profile))));
        if (profilesRoot is not null)
        {
            foreach (string other in _store.Profiles())
            {
                if (!string.Equals(other, profile, StringComparison.OrdinalIgnoreCase) && (other.Length == 0 || !Directory.Exists(Path.Combine(profilesRoot, other))))
                {
                    forgotten += _store.DeleteProfile(other) > 0 ? 1 : 0;
                    DiagnosticLog.Debug(SkillCatalog.Category, SkillRecordText.ProfileForgottenLogLine(other));
                }
            }
        }

        var result = new SkillReconcile(added, removed, modified, forgotten);
        if (result.Changed)
        {
            DiagnosticLog.Info(SkillCatalog.Category, SkillRecordText.ReconciledLogLine(result, profile));
        }

        return result;
    }

    /// <summary>
    /// The skills in the current profile's view (global and the profile's own) not used since <paramref name="cutoff"/>,
    /// measured by <see cref="SkillRecord.Reference"/>, oldest first.
    /// </summary>
    public IReadOnlyList<SkillRecord> UnusedSince(DateTimeOffset cutoff) =>
        _store.List(CurrentProfile).Where(r => r.Reference < cutoff).OrderBy(r => r.Reference).ThenBy(r => r.Folder, StringComparer.OrdinalIgnoreCase).ToList();
}
