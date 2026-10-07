using NeonSidekick.Diagnostics;

namespace NeonSidekick.Skills;

/// <summary>What one reconcile changed (<see cref="SkillRecords.Reconcile"/>).</summary>
public readonly record struct SkillReconcile(int Added, int Removed, int Modified, int ProfilesForgotten)
{
    public bool Changed => Added + Removed + Modified + ProfilesForgotten > 0;
}

/// <summary>
/// What the skill records know of one skill (2026-10-02, <see cref="SkillRecords.FactsOf"/>), for the reflection's catalog and the Skills
/// pane's caption: the turns' uses, how many times a reflection wrote it and its newest such write, when the user last changed it by hand
/// (only while that is its latest change), the repository it was installed from and the reflections' writes since that install.
/// </summary>
public sealed record SkillFacts(SkillUseFacts? Uses, int ReflectionWrites, SkillEvent? LastReflectionWrite, DateTimeOffset? HandEditedAt, string? InstalledFrom, int ReflectionWritesSinceInstall)
{
    /// <summary>Nothing worth a line: never loaded, never written by a reflection, not edited by hand, not installed.</summary>
    public bool IsEmpty => Uses is null && ReflectionWrites == 0 && HandEditedAt is null && InstalledFrom is null;
}

/// <summary>What a <see cref="SkillRecords.Restore"/> came to.</summary>
public enum SkillRevertOutcome
{
    Reverted,

    /// <summary>The picked version is no longer kept (dropped past the cap since the list was shown).</summary>
    NoRevision,

    /// <summary>The file holds the picked version already: nothing written.</summary>
    Unchanged,

    /// <summary>The file's current text cannot be kept (too long, or not read): refused, the restore would lose it (2026-10-04; a hand edit no copy held until then).</summary>
    NotKept,

    /// <summary>The editor could not write it back; the edit result's detail says why.</summary>
    Failed,
}

/// <summary>The outcome, the revision it read (null for none) and the editor's result when it wrote.</summary>
public sealed record SkillRevert(SkillRevertOutcome Outcome, SkillRevision? Revision, SkillEditResult? Edit);

/// <summary>
/// The app's record of its skills (2026-09-30, the user's ask): when each was created, modified and last used, over
/// <see cref="SkillRecordStore"/>. The writers tell it what they did:
/// <list type="bullet">
/// <item><c>skill_editor</c> and the reflection, through <see cref="Edited"/>;</item>
/// <item><c>/skills add</c>, through <see cref="Installed"/>;</item>
/// <item>the Skills pane's move, rename and delete, through <see cref="Moved"/>, <see cref="Renamed"/> and <see cref="Deleted"/>;</item>
/// <item><c>load_skill</c>, through <see cref="Used"/> (the <c>/botchat</c> preload too until it went, 2026-10-04).</item>
/// </list>
/// Anything the app did not do itself (a hand edit, an edit in the editor the pane opened, a folder dropped in or taken out)
/// is caught by <see cref="Reconcile"/> at startup, at every profile load, after an install and before a purge.
/// <para>The profile a row belongs to is the name of the folder the profile's skills root sits in
/// (<c>&lt;home&gt;\profiles\&lt;name&gt;\skills</c>), read from the roots in force (<see cref="ProfileOf"/>). A reflection decided
/// before a profile switch therefore records under the profile it wrote into. External skills are never recorded (the
/// user's pick: the app treats that folder as read-only). Each change is a Debug line under <c>Skills</c>, and a reconcile
/// that changed anything is one Info line.</para>
/// <para>Since 2026-10-02 (the reflection audit) the records are also the skills' history: who wrote each change (the model, a
/// reflection, the user by hand, an install), every turn that loaded a skill with the errors after the load, and the text each app write
/// replaced and a hand edit's copy (the versions <see cref="Restore"/> puts back). The reflection reads its usage lines and its cooldown mark from here, so
/// they survive a session purge and a rename, and work with <c>Session logging</c> off.</para>
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

    /// <summary>The zone the records' moments show in: the clock's.</summary>
    public TimeZoneInfo Zone => _time.LocalTimeZone;

    /// <summary>The profile <paramref name="roots"/>' profile root belongs to: the name of the folder it sits in.</summary>
    public static string ProfileOf(SkillRoots roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        return Path.GetFileName(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(roots.Profile)))) ?? "";
    }

    /// <summary>The profile of the roots in force.</summary>
    public string CurrentProfile => ProfileOf(_roots());

    /// <summary>The main chat's <c>skill_editor</c> result (the tool's listener): <see cref="Edited(SkillRoots, SkillEditResult, string, long?)"/> with the model as the actor.</summary>
    public void Edited(SkillRoots roots, SkillEditResult result) => Edited(roots, result, SkillActors.Model);

    /// <summary>
    /// A <c>skill_editor</c> result, the model's or a reflection's (<paramref name="actor"/>), written under <paramref name="roots"/>. A
    /// create is <see cref="SkillRecordStore.Created"/>, and an update or a bundled file written is <see cref="SkillRecordStore.Modified"/>.
    /// Since 2026-10-02 each write is an event too (the summary, or the file's path, as its detail) and, when the write replaced a file,
    /// the text it held is a revision. Every refusal is nothing. The folder is the result's name: the editor finds a skill by its folder.
    /// </summary>
    public void Edited(SkillRoots roots, SkillEditResult result, string actor, long? sessionId = null)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(actor);
        if (result.Scope == SkillScope.External || result.Name.Length == 0)
        {
            return;
        }

        string profile = ProfileOf(roots);
        var now = _time.GetUtcNow();
        string kind;
        switch (result.Outcome)
        {
            case SkillEditOutcome.Created:
                _store.Created(result.Scope, profile, result.Name, result.Name, WrittenAt(roots, result.Scope, result.Name, now));
                DiagnosticLog.Debug(SkillCatalog.Category, SkillRecordText.CreatedLogLine(result.Scope, result.Name));
                kind = SkillEventKinds.Created;
                break;
            case SkillEditOutcome.Updated:
                _store.Modified(result.Scope, profile, result.Name, result.Name, WrittenAt(roots, result.Scope, result.Name, now));
                DiagnosticLog.Debug(SkillCatalog.Category, SkillRecordText.ModifiedLogLine(result.Scope, result.Name));
                kind = SkillEventKinds.Updated;
                break;
            case SkillEditOutcome.FileWritten or SkillEditOutcome.FileEdited:
                _store.Modified(result.Scope, profile, result.Name, result.Name, now);
                DiagnosticLog.Debug(SkillCatalog.Category, SkillRecordText.ModifiedLogLine(result.Scope, result.Name));
                kind = SkillEventKinds.File;
                break;
            default:
                return;
        }

        string detail = result.Path.Length == 0 ? result.Summary : result.Summary.Length == 0 ? result.Path : result.Path + ": " + result.Summary;
        _store.AddEvent(result.Scope, profile, result.Name, kind, actor, profile, now, sessionId, detail: detail);
        if (kind != SkillEventKinds.Created)
        {
            KeepRevision(result.Scope, profile, result.Name, result.Path.Length == 0 ? SkillCatalog.FileName : result.Path, result, actor, now);
        }
    }

    /// <summary>The replaced text as a revision: a file the write created is one with no text; an old text too long to keep is none (a Debug line).</summary>
    private void KeepRevision(SkillScope scope, string profile, string folder, string path, SkillEditResult result, string actor, DateTimeOffset at)
    {
        if (result.Existed && result.Previous is null)
        {
            DiagnosticLog.Debug(SkillCatalog.Category, SkillRecordText.RevisionSkippedLogLine(scope, folder, path));
            return;
        }

        if (_store.AddRevision(scope, profile, folder, path, result.Existed ? result.Previous : null, actor, at))
        {
            // The revision holds the hand-edited text now, if there was one (2026-10-04): a copy left beside it would come back twice.
            _store.DropHandCopies(scope, profile, folder, path);
        }
    }

    /// <summary>
    /// A hand edit of <paramref name="file"/> (the folder's <c>SKILL.md</c>) kept as the skill's one hand-edit copy (2026-10-04, the
    /// user's call), replacing an older one, so a revert can go back past the edit without losing it; false (a Debug line) when the text
    /// is too long to keep or cannot be read.
    /// </summary>
    private bool KeepHandCopy(SkillScope scope, string profile, string folder, string file, DateTimeOffset at)
    {
        if (ReadKeepable(file) is not { } text)
        {
            DiagnosticLog.Debug(SkillCatalog.Category, SkillRecordText.HandCopySkippedLogLine(scope, folder));
            return false;
        }

        _store.DropHandCopies(scope, profile, folder, SkillCatalog.FileName);
        return _store.AddRevision(scope, profile, folder, SkillCatalog.FileName, text, SkillActors.User, at);
    }

    /// <summary>A file's text when it is short enough to keep as a revision (<see cref="SkillRecordStore.MaxRevisionChars"/>); null when it is longer, gone or not read.</summary>
    private static string? ReadKeepable(string file)
    {
        try
        {
            // Four bytes a character at most: a longer file is never short enough, and is not read.
            if (!File.Exists(file) || new FileInfo(file).Length > SkillRecordStore.MaxRevisionChars * 4L)
            {
                return null;
            }

            string text = File.ReadAllText(file);
            return text.Length <= SkillRecordStore.MaxRevisionChars ? text : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The text <paramref name="path"/> holds in <paramref name="skill"/>'s folder, or null when it is gone or not read.</summary>
    private static string? ReadSkillFile(Skill skill, string path)
    {
        try
        {
            string file = Path.Combine(skill.Directory, path);
            return File.Exists(file) ? File.ReadAllText(file) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The moment an app write of a SKILL.md is recorded at: now, or the file's own write time when the clock is behind it, so the
    /// reconcile never takes the app's own write for a hand edit (its test is the file being newer than the row).
    /// </summary>
    private static DateTimeOffset WrittenAt(SkillRoots roots, SkillScope scope, string folder, DateTimeOffset now)
    {
        try
        {
            string file = Path.Combine(roots.Of(scope), folder, SkillCatalog.FileName);
            if (File.Exists(file))
            {
                var written = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
                return written > now ? written : now;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // The clock's moment stands.
        }

        return now;
    }

    /// <summary><c>/skills add</c> put a skill in <paramref name="directory"/>: created, or modified when it replaced one.</summary>
    public void Installed(SkillScope scope, string directory, bool updated) => Installed(new SkillInstallResult(true, updated, scope, directory));

    /// <summary>
    /// <c>/skills add</c> put a skill in place: created, or modified when it replaced one; an <c>installed</c> event carrying the origin
    /// (2026-10-02), and on an update the replaced SKILL.md as a revision.
    /// </summary>
    public void Installed(SkillInstallResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Scope == SkillScope.External)
        {
            return;
        }

        var roots = _roots();
        string folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(result.Directory));
        string profile = ProfileOf(roots);
        var now = _time.GetUtcNow();
        var at = WrittenAt(roots, result.Scope, folder, now);
        if (result.Updated)
        {
            _store.Modified(result.Scope, profile, folder, folder, at);
            DiagnosticLog.Debug(SkillCatalog.Category, SkillRecordText.ModifiedLogLine(result.Scope, folder));
            if (result.Previous is not null && _store.AddRevision(result.Scope, profile, folder, SkillCatalog.FileName, result.Previous, SkillActors.Install, now))
            {
                _store.DropHandCopies(result.Scope, profile, folder, SkillCatalog.FileName);
            }
        }
        else
        {
            _store.Created(result.Scope, profile, folder, folder, at);
            DiagnosticLog.Debug(SkillCatalog.Category, SkillRecordText.CreatedLogLine(result.Scope, folder));
        }

        string origin = SkillProvenance.Read(result.Directory)?.Repo ?? "";
        _store.AddEvent(result.Scope, profile, folder, SkillEventKinds.Installed, SkillActors.Install, profile, now, detail: origin);
    }

    /// <summary>The skill's instructions were loaded (<c>load_skill</c>): last used now.</summary>
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

    /// <summary>
    /// A turn's end (2026-10-02): one <c>used</c> event per skill the model loaded in it, found in <paramref name="skills"/> by name, with the
    /// error results after its first load (<see cref="Llm.TurnTrace.ErrorsAfterLoad"/>) and the session the turn went into
    /// (<paramref name="sessionId"/>, null with <c>Session logging</c> off). A name the catalog no longer has, or an external skill, is skipped.
    /// </summary>
    public void TurnUsed(Llm.TurnTrace trace, IEnumerable<Skill> skills, long? sessionId)
    {
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentNullException.ThrowIfNull(skills);
        if (trace.ErrorsAfterLoad.Count == 0)
        {
            return;
        }

        string profile = CurrentProfile;
        var now = _time.GetUtcNow();
        var known = skills.ToList();
        foreach (var (name, errors) in trace.ErrorsAfterLoad)
        {
            if (known.Find(s => string.Equals(s.Name, name, StringComparison.Ordinal)) is { Scope: not SkillScope.External } skill)
            {
                _store.AddEvent(skill.Scope, profile, skill.FolderName, SkillEventKinds.Used, SkillActors.Model, profile, now, sessionId, errors);
            }
        }
    }

    /// <summary>
    /// What the records know of <paramref name="skill"/> (2026-10-02): its uses, the reflections' writes, a hand edit as its latest
    /// change, where it was installed from and whether a reflection changed it since. Null for an external skill or one with no row.
    /// </summary>
    public SkillFacts? FactsOf(Skill skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        if (skill.Scope == SkillScope.External || _store.Find(skill.Scope, CurrentProfile, skill.FolderName) is not { } row)
        {
            return null;
        }

        var events = _store.Events(row.Id);
        var reflectionWrites = events.Where(e => e.Actor == SkillActors.Reflection && SkillEventKinds.Written.Contains(e.Kind)).ToList();
        var lastChange = events.LastOrDefault(e => SkillEventKinds.Written.Contains(e.Kind) || e.Kind is SkillEventKinds.HandEdit or SkillEventKinds.Installed or SkillEventKinds.Reverted);
        var installed = events.LastOrDefault(e => e.Kind == SkillEventKinds.Installed);
        string? origin = SkillProvenance.Read(skill.Directory)?.Repo;
        int sinceInstall = origin is null ? 0 : reflectionWrites.Count(e => installed is null || e.At >= installed.At);
        return new SkillFacts(
            _store.UseFacts(row.Id),
            reflectionWrites.Count,
            reflectionWrites.LastOrDefault(),
            lastChange is { Kind: SkillEventKinds.HandEdit } ? lastChange.At : null,
            string.IsNullOrEmpty(origin) ? null : origin,
            sinceInstall);
    }

    /// <summary>The usage line of <paramref name="skill"/> (<see cref="SkillText.UsageLine(SkillFacts, TimeZoneInfo)"/>), null when the records know nothing of it.</summary>
    public string? UsageLine(Skill skill, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return FactsOf(skill) is { IsEmpty: false } facts ? SkillText.UsageLine(facts, zone) : null;
    }

    /// <summary>The newest skill a reflection wrote in the current profile's view (the cooldown's mark), followed through a rename; null for none.</summary>
    public SkillWriteMark? LastReflectionWrite() => _store.LastWrite(CurrentProfile, SkillActors.Reflection);

    /// <summary>How many times a reflection changed the installed skill <paramref name="skill"/> since its install (the update page's warning).</summary>
    public int ReflectionChangesSinceInstall(Skill skill) => FactsOf(skill)?.ReflectionWritesSinceInstall ?? 0;

    /// <summary>
    /// Each skill's version as the Offered tab shows it (2026-10-07, the user's ask: <c>v4</c>): 1 for the text in place and one more
    /// per kept older text (<see cref="SkillRecordStore.RevisionCounts"/>), read once for the whole list; null for an external skill,
    /// which has no records. A skill with no row yet is <c>v1</c>.
    /// </summary>
    public Func<Skill, int?> Versions()
    {
        var counts = _store.RevisionCounts(CurrentProfile);
        return skill =>
        {
            ArgumentNullException.ThrowIfNull(skill);
            return skill.Scope == SkillScope.External ? null : 1 + counts.GetValueOrDefault((skill.Scope, skill.FolderName));
        };
    }

    /// <summary>The newest revision of <paramref name="skill"/>, or null when there is none.</summary>
    public SkillRevision? LatestRevision(Skill skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        return skill.Scope == SkillScope.External || _store.Find(skill.Scope, CurrentProfile, skill.FolderName) is not { } row
            ? null
            : _store.Revisions(row.Id).FirstOrDefault();
    }

    /// <summary>
    /// The kept versions of <paramref name="skill"/>, newest first (2026-10-04: the Skills pane's revert list, the user picking one); empty
    /// for an external skill or one with no row.
    /// </summary>
    public IReadOnlyList<SkillRevision> Revisions(Skill skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        return skill.Scope == SkillScope.External || _store.Find(skill.Scope, CurrentProfile, skill.FolderName) is not { } row
            ? []
            : _store.Revisions(row.Id);
    }

    /// <summary>Whether <paramref name="revision"/> is what its file holds now (the list's <c>current</c> mark): the same text, or no file for a version that had none.</summary>
    public static bool IsCurrent(Skill skill, SkillRevision revision)
    {
        ArgumentNullException.ThrowIfNull(skill);
        ArgumentNullException.ThrowIfNull(revision);
        return string.Equals(revision.Content, ReadSkillFile(skill, revision.Path), StringComparison.Ordinal);
    }

    /// <summary>
    /// The Skills pane's revert (2026-10-04, the user's call; <c>/skills revert &lt;name&gt;</c>, the newest version and one further back
    /// each time, until then): <paramref name="revision"/>, picked from <see cref="Revisions"/>, put back (<see cref="SkillEditor.Restore"/>).
    /// Nothing is lost (the user's pick): the file's current text is kept first as a <see cref="SkillActors.Revert"/> version unless one on
    /// the list holds it already (a hand edit's copy, an earlier revert's), and the picked version stays on the list, so the user can go back
    /// and forth. The row's modified moment moves and a <c>reverted</c> event says what came back. <see cref="SkillRevertOutcome.Unchanged"/>
    /// when the file holds it already; <see cref="SkillRevertOutcome.NotKept"/> when the current text cannot be kept (too long, or not read):
    /// the restore would lose it for good. The caller reconciles first, so a hand edit's copy is on the list.
    /// </summary>
    public SkillRevert Restore(Skill skill, SkillRevision revision)
    {
        ArgumentNullException.ThrowIfNull(skill);
        ArgumentNullException.ThrowIfNull(revision);
        var listed = Revisions(skill);
        if (!listed.Any(r => r.Id == revision.Id))
        {
            return new SkillRevert(SkillRevertOutcome.NoRevision, revision, null);
        }

        bool exists = File.Exists(Path.Combine(skill.Directory, revision.Path));
        string? current = ReadSkillFile(skill, revision.Path);
        if (exists && current is null)
        {
            return new SkillRevert(SkillRevertOutcome.NotKept, revision, null);
        }

        if (string.Equals(current, revision.Content, StringComparison.Ordinal))
        {
            return new SkillRevert(SkillRevertOutcome.Unchanged, revision, null);
        }

        var roots = _roots();
        string profile = ProfileOf(roots);
        var now = _time.GetUtcNow();
        bool held = listed.Any(r => string.Equals(r.Path, revision.Path, StringComparison.OrdinalIgnoreCase) && string.Equals(r.Content, current, StringComparison.Ordinal));
        if (!held && (current is { Length: > SkillRecordStore.MaxRevisionChars }
            || !_store.AddRevision(skill.Scope, profile, skill.FolderName, revision.Path, current, SkillActors.Revert, now)))
        {
            return new SkillRevert(SkillRevertOutcome.NotKept, revision, null);
        }

        var result = SkillEditor.Restore(roots, skill.Scope, skill.Directory, skill.FolderName, revision.Path, revision.Content);
        if (result.Outcome is not (SkillEditOutcome.Updated or SkillEditOutcome.FileWritten))
        {
            return new SkillRevert(SkillRevertOutcome.Failed, revision, result);
        }

        // The SKILL.md's own time when the clock is behind it, whichever file came back: the row never goes back past the file it watches.
        _store.Modified(skill.Scope, profile, skill.FolderName, skill.Name, WrittenAt(roots, skill.Scope, skill.FolderName, now));
        _store.AddEvent(skill.Scope, profile, skill.FolderName, SkillEventKinds.Reverted, SkillActors.User, profile, now,
            detail: SkillRecordText.RevertDetail(revision));
        DiagnosticLog.Info(SkillCatalog.Category, SkillRecordText.RevertedLogLine(skill.Scope, skill.FolderName, revision));
        return new SkillRevert(SkillRevertOutcome.Reverted, revision, result);
    }

    /// <summary>The <c>meta</c> flag of the one-time import for <paramref name="profile"/>.</summary>
    public static string ImportFlag(string profile) => "imported-sessions:" + profile;

    /// <summary>
    /// The one-time import of a profile's <c>sessions.db</c> into the events (2026-10-02, the skills' history moving to <c>skills.db</c>):
    /// every reflection that wrote a skill as a <c>created</c>/<c>updated</c> event of the reflection, and every stored turn that loaded a
    /// skill as a <c>used</c> event (the turn's errors standing in for the errors after the load, which the old rows never kept). Names are
    /// found among <paramref name="skills"/>; a skill gone since is skipped. Once per profile (<see cref="ImportFlag"/>); nothing while
    /// either store is off, and nothing (no flag either) while the profile has no <c>sessions.db</c> yet. The count of events added.
    /// </summary>
    public int ImportFrom(Sessions.SessionStore sessions, IEnumerable<Skill> skills)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(skills);
        string profile = CurrentProfile;
        string flag = ImportFlag(profile);
        // A sessions.db that is not there has nothing to give, and opening it would make one (Session logging off writes no file).
        if (!File.Exists(sessions.FilePath) || !_store.Available || !sessions.Available || _store.HasFlag(flag))
        {
            return 0;
        }

        var known = skills.Where(s => s.Scope != SkillScope.External).ToList();
        Skill? Of(string name) => known.Find(s => string.Equals(s.Name, name, StringComparison.Ordinal));
        int added = 0;
        foreach (var (at, name, action, sessionId) in sessions.LearnedReflections())
        {
            if (Of(name) is { } skill)
            {
                string kind = action == Sessions.ReflectionRow.Created ? SkillEventKinds.Created : SkillEventKinds.Updated;
                added += _store.AddEvent(skill.Scope, profile, skill.FolderName, kind, SkillActors.Reflection, profile, at, sessionId) ? 1 : 0;
            }
        }

        foreach (var (at, sessionId, names, errors) in sessions.SkillLoads())
        {
            foreach (string name in names.Distinct(StringComparer.Ordinal))
            {
                if (Of(name) is { } skill)
                {
                    added += _store.AddEvent(skill.Scope, profile, skill.FolderName, SkillEventKinds.Used, SkillActors.Model, profile, at, sessionId, errors) ? 1 : 0;
                }
            }
        }

        _store.SetFlag(flag);
        DiagnosticLog.Info(SkillCatalog.Category, SkillRecordText.ImportedLogLine(profile, added));
        return added;
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
                        // An edit the app did not make (2026-10-02): the reflection is told the text is the user's. Its text is kept
                        // as the skill's hand-edit copy (2026-10-04), so a revert can go back past it and bring it back last.
                        _store.AddEvent(row.Id, SkillEventKinds.HandEdit, SkillActors.User, profile, written);
                        KeepHandCopy(scope, profile, row.Folder, file, written);
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
