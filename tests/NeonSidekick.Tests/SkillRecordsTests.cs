using NeonSidekick.Llm.Tools;
using NeonSidekick.Skills;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>The skill records (2026-09-30): <c>skills.db</c>, the reconcile, the hooks and the words.</summary>
public class SkillRecordsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly SkillRecordStore _store;
    private SkillRoots _roots;
    private readonly SkillRecords _records;

    public SkillRecordsTests()
    {
        // The real layout: <home>\skills, <home>\profiles\<name>\skills.
        _roots = RootsFor("neon");
        Directory.CreateDirectory(_roots.Global);
        Directory.CreateDirectory(_roots.Profile);
        _store = new SkillRecordStore(_dir);
        _records = new SkillRecords(_store, () => _roots, _time);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private SkillRoots RootsFor(string profile) =>
        new(Path.Combine(_dir, "profiles", profile, "skills"), Path.Combine(_dir, "skills"), Path.Combine(_dir, ".agents", "skills"));

    private static string Write(string root, string folder, string? name = null)
    {
        string dir = Path.Combine(root, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, SkillCatalog.FileName), "---\nname: " + (name ?? folder) + "\ndescription: Does things.\n---\nSteps.\n");
        return dir;
    }

    /// <summary>What an app write of <paramref name="file"/> is recorded at (2026-10-02): the clock, or the file's own time when the clock is behind it.</summary>
    private DateTimeOffset WrittenAt(string file)
    {
        var written = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
        return written > _time.GetUtcNow() ? written : _time.GetUtcNow();
    }

    private SkillReconcile Reconcile()
    {
        var catalog = new SkillCatalog(() => _roots);
        catalog.Scan(external: true);
        return _records.Reconcile(catalog.Skills.Concat(catalog.Shadowed));
    }

    [Fact]
    public void Store_KeysByScopeProfileAndFolder_FolderWithoutCase_CategoryNull()
    {
        var at = _time.GetUtcNow();
        _store.Created(SkillScope.Global, "", "pdf", "pdf", at);
        _store.Created(SkillScope.Profile, "neon", "pdf", "pdf", at);
        _store.Created(SkillScope.Profile, "ada", "pdf", "pdf", at);
        _store.Used(SkillScope.Global, "ignored for global", "PDF", "pdf", at.AddDays(1));

        var all = _store.All();
        Assert.Equal(3, all.Count);
        Assert.All(all, r => Assert.Null(r.Category));
        var global = Assert.Single(all, r => r.Scope == SkillScope.Global);
        Assert.Equal("", global.Profile);
        Assert.Equal(at.AddDays(1), global.LastUsed);
        Assert.Equal(2, _store.List("neon").Count);   // the global one and neon's own
        Assert.Equal(["ada", "neon"], _store.Profiles());
        Assert.True(_store.Available);
        Assert.Equal(SkillRecordStore.SchemaVersion, 1);
    }

    [Fact]
    public void Store_CreatedStartsOver_ModifiedKeepsCreated_MoveCarriesTheMoments()
    {
        var at = _time.GetUtcNow();
        _store.Created(SkillScope.Profile, "neon", "haiku", "haiku", at);
        _store.Used(SkillScope.Profile, "neon", "haiku", "haiku", at.AddHours(1));
        _store.Modified(SkillScope.Profile, "neon", "haiku", "haiku", at.AddHours(2));
        var row = Assert.Single(_store.All());
        Assert.Equal((at, at.AddHours(2), (DateTimeOffset?)at.AddHours(1)), (row.Created, row.Modified, row.LastUsed));

        _store.Move(SkillScope.Profile, "neon", "haiku", SkillScope.Global, "", "haiku", "haiku");
        row = Assert.Single(_store.All());
        Assert.Equal((SkillScope.Global, "", at.AddHours(1)), (row.Scope, row.Profile, row.LastUsed!.Value));

        _store.Created(SkillScope.Global, "", "haiku", "haiku", at.AddDays(3));   // a new folder of the same name starts over
        Assert.Null(Assert.Single(_store.All()).LastUsed);
        Assert.True(_store.Delete(SkillScope.Global, "", "HAIKU"));
        Assert.Empty(_store.All());
    }

    [Fact]
    public void Store_ARecordedSchemaItDoesNotKnow_IsRefused()
    {
        _store.Dispose();
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Path.Combine(_dir, SkillRecordStore.FileName)))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE meta(key TEXT PRIMARY KEY, value INTEGER NOT NULL); INSERT INTO meta VALUES ('schema', 9);";
            command.ExecuteNonQuery();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        using var other = new SkillRecordStore(_dir);
        Assert.False(other.Available);
        Assert.Empty(other.All());
        Assert.Equal("skills.db is at schema 9, this version reads 1; skill records are off until it is moved away.", SkillRecordStore.SchemaMismatchWarning(9));
    }

    [Fact]
    public void Reconcile_AddsWithTheFilesTimes_FollowsAnEdit_AndDropsWhatIsGone()
    {
        string pdf = Write(_roots.Global, "pdf");
        Write(_roots.Profile, "notes", "meeting-notes");   // the frontmatter name differs from the folder
        Write(_roots.Global, ".skill-old-1234");            // the installer's parked copy: no skill
        Write(_roots.External, "outside");                  // external: never recorded
        var written = new DateTimeOffset(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);
        File.SetLastWriteTimeUtc(Path.Combine(pdf, SkillCatalog.FileName), written.UtcDateTime);

        Assert.Equal(new SkillReconcile(2, 0, 0, 0), Reconcile());
        var rows = _store.All();
        Assert.Equal(["notes", "pdf"], rows.Select(r => r.Folder).Order());
        Assert.Equal("meeting-notes", rows.Single(r => r.Folder == "notes").Name);
        Assert.Equal(written, rows.Single(r => r.Folder == "pdf").Modified);
        Assert.All(rows, r => Assert.Null(r.LastUsed));

        Assert.Equal(default, Reconcile());   // nothing changed: nothing to do
        File.SetLastWriteTimeUtc(Path.Combine(pdf, SkillCatalog.FileName), written.AddDays(5).UtcDateTime);
        Directory.Delete(Path.Combine(_roots.Profile, "notes"), recursive: true);
        Assert.Equal(new SkillReconcile(0, 1, 1, 0), Reconcile());
        Assert.Equal(written.AddDays(5), Assert.Single(_store.All()).Modified);
    }

    [Fact]
    public void Reconcile_ForgetsAVanishedProfile_AndAProfileRenameMovesItsRows()
    {
        var at = _time.GetUtcNow();
        _store.Created(SkillScope.Profile, "gone", "x", "x", at);
        Directory.CreateDirectory(Path.Combine(_dir, "profiles", "ada"));
        _store.Created(SkillScope.Profile, "ada", "y", "y", at);

        Assert.Equal(1, Reconcile().ProfilesForgotten);
        Assert.Equal(["ada"], _store.Profiles());

        _records.RenameProfile("ada", "grace");
        Assert.Equal("grace", Assert.Single(_store.All()).Profile);
        Assert.Equal("neon", _records.CurrentProfile);
        Assert.Equal("neon", SkillRecords.ProfileOf(_roots));
    }

    [Fact]
    public void Hooks_TheEditorAndTheLoader_RecordCreatedModifiedAndUsed()
    {
        var editor = new SkillEditorTool(() => _roots, () => false, edited: _records.Edited);
        editor.Describe("create", "global", "haiku", "Writes haiku.", "Count the syllables.");
        var created = Assert.Single(_store.All());
        string file = Path.Combine(_roots.Global, "haiku", SkillCatalog.FileName);
        Assert.Equal((SkillScope.Global, "haiku", WrittenAt(file)), (created.Scope, created.Folder, created.Created));

        _time.Advance(TimeSpan.FromHours(1));
        editor.Describe("update", "", "haiku", null, "Count them twice.");
        editor.Describe("create", "global", "haiku", "Again.", "x");   // refused: no record change
        var modified = Assert.Single(_store.All());
        Assert.Equal((created.Created, WrittenAt(file)), (modified.Created, modified.Modified));
        Assert.Equal(default, Reconcile());   // the app's own writes are never taken for a hand edit

        var catalog = new SkillCatalog(() => _roots);
        catalog.Scan(external: false);
        var loader = new LoadSkillTool(catalog, used: _records.Used);
        _time.Advance(TimeSpan.FromHours(1));
        loader.Describe("haiku", "missing.md");   // a file read is no use
        Assert.Null(Assert.Single(_store.All()).LastUsed);
        loader.Describe("haiku", null);
        Assert.Equal(_time.GetUtcNow(), Assert.Single(_store.All()).LastUsed);
    }

    [Fact]
    public void Hooks_InstallMoveRenameDelete_KeepTheRecordInStep()
    {
        string dir = Write(_roots.Profile, "pdf");
        _records.Installed(SkillScope.Profile, dir, updated: false);
        var at = WrittenAt(Path.Combine(dir, SkillCatalog.FileName));
        Assert.Equal(("neon", at), (Assert.Single(_store.All()).Profile, Assert.Single(_store.All()).Created));
        _time.Advance(TimeSpan.FromDays(1));
        _records.Installed(SkillScope.Profile, dir, updated: true);
        Assert.Equal(WrittenAt(Path.Combine(dir, SkillCatalog.FileName)), Assert.Single(_store.All()).Modified);

        var skill = new Skill("pdf", "", SkillScope.Profile, dir);
        _records.Moved(skill, SkillScope.Global);
        Assert.Equal((SkillScope.Global, at), (Assert.Single(_store.All()).Scope, Assert.Single(_store.All()).Created));
        _records.Renamed(skill with { Scope = SkillScope.Global }, "pdf-tools");
        Assert.Equal(("pdf-tools", at), (Assert.Single(_store.All()).Folder, Assert.Single(_store.All()).Created));
        _records.Deleted(SkillScope.Global, "pdf-tools");
        Assert.Empty(_store.All());

        _records.Used(new Skill("outside", "", SkillScope.External, Path.Combine(_roots.External, "outside")));
        Assert.Empty(_store.All());   // external: never recorded
    }

    // ── The skills' history (2026-10-02, the reflection audit) ──────────────

    private Skill SkillOf(string folder)
    {
        var catalog = new SkillCatalog(() => _roots);
        catalog.Scan(external: false);
        return catalog.Find(folder)!;
    }

    private SkillEditorTool Editor(string actor) =>
        new(() => _roots, () => false, new SkillFileAccess(_time), (roots, result) => _records.Edited(roots, result, actor));

    /// <summary>
    /// The Offered tab's version column (2026-10-07): the kept older texts counted per skill in one query, a "created" revision with no
    /// text left out, every skill with a row there (0 for none), the folder matched ignoring case, another profile's skills not in view.
    /// </summary>
    [Fact]
    public void RevisionCounts_CountTheKeptTexts_PerSkillInTheView_AndVersionsAddOne()
    {
        var at = _time.GetUtcNow();
        _store.Created(SkillScope.Profile, "neon", "haiku", "haiku", at);
        _store.Created(SkillScope.Global, "", "untouched", "untouched", at);
        _store.Created(SkillScope.Profile, "ada", "elsewhere", "elsewhere", at);
        _store.AddRevision(SkillScope.Profile, "neon", "haiku", SkillCatalog.FileName, null, SkillActors.Model, at);   // the file created: no older text
        for (int i = 0; i < 3; i++)
        {
            _store.AddRevision(SkillScope.Profile, "neon", "haiku", i == 2 ? "notes.md" : SkillCatalog.FileName, "old " + i, SkillActors.Model, at.AddMinutes(i));
        }

        _store.AddRevision(SkillScope.Profile, "ada", "elsewhere", SkillCatalog.FileName, "old", SkillActors.Model, at);

        var counts = _store.RevisionCounts("neon");
        Assert.Equal(2, counts.Count);
        Assert.Equal(3, counts[(SkillScope.Profile, "HAIKU")]);
        Assert.Equal(0, counts[(SkillScope.Global, "untouched")]);

        var versions = _records.Versions();
        Assert.Equal(4, versions(new Skill("haiku", "d", SkillScope.Profile, Path.Combine(_roots.Profile, "haiku"))));
        Assert.Equal(1, versions(new Skill("untouched", "d", SkillScope.Global, Path.Combine(_roots.Global, "untouched"))));
        Assert.Equal(1, versions(new Skill("new-one", "d", SkillScope.Profile, Path.Combine(_roots.Profile, "new-one"))));   // no row yet
        Assert.Null(versions(new Skill("ext", "d", SkillScope.External, Path.Combine(_roots.External, "ext"))));
    }

    /// <summary>A skill the editor changes keeps the replaced text, so its version moves on (2026-10-07).</summary>
    [Fact]
    public void Versions_MoveOn_WithAnEdit()
    {
        var editor = Editor(SkillActors.Model);
        editor.Describe("create", "profile", "haiku", "Writes haiku.", "Seventeen syllables.");
        var haiku = new Skill("haiku", "Writes haiku.", SkillScope.Profile, Path.Combine(_roots.Profile, "haiku"));
        Assert.Equal(1, _records.Versions()(haiku));                 // created: no older text yet

        editor.Describe("update", "", "haiku", null, "Five, seven, five.");
        Assert.Equal(2, _records.Versions()(haiku));
    }

    [Fact]
    public void Store_EventsAndRevisions_HangOffTheRow_TheNewestTenKept_AndGoWithIt()
    {
        var at = _time.GetUtcNow();
        _store.Created(SkillScope.Profile, "neon", "haiku", "haiku", at);
        Assert.True(_store.AddEvent(SkillScope.Profile, "neon", "haiku", SkillEventKinds.Used, SkillActors.Model, "neon", at, 7, 2));
        Assert.False(_store.AddEvent(SkillScope.Profile, "neon", "nothing", SkillEventKinds.Used, SkillActors.Model, "neon", at));   // no row, no event
        for (int i = 0; i < 12; i++)
        {
            _store.AddRevision(SkillScope.Profile, "neon", "haiku", SkillCatalog.FileName, "v" + i, SkillActors.Model, at.AddMinutes(i));
        }

        long id = _store.Find(SkillScope.Profile, "neon", "haiku")!.Id;
        var revisions = _store.Revisions(id);
        Assert.Equal(SkillRecordStore.MaxRevisions, revisions.Count);
        Assert.Equal(("v11", "v2"), (revisions[0].Content, revisions[^1].Content));   // newest first, the two oldest gone
        var used = Assert.Single(_store.Events(id));
        Assert.Equal((SkillEventKinds.Used, (long?)7, (int?)2), (used.Kind, used.SessionId, used.ErrorsAfter));
        Assert.Equal(new SkillUseFacts(1, 1, 1, at), _store.UseFacts(id));

        _store.Created(SkillScope.Profile, "neon", "haiku", "haiku", at.AddDays(1));   // a new folder of the same name starts over
        Assert.Empty(_store.Events(id));
        Assert.Empty(_store.Revisions(id));

        _store.AddEvent(SkillScope.Profile, "neon", "haiku", SkillEventKinds.Used, SkillActors.Model, "neon", at);
        Assert.True(_store.Delete(SkillScope.Profile, "neon", "haiku"));
        Assert.Empty(_store.Events(id));   // foreign keys on: the events went with the row
        Assert.Equal(256 * 1024, SkillRecordStore.MaxRevisionChars);
    }

    [Fact]
    public void Edited_ByAReflection_IsAnEvent_TheReplacedTextARevision_AndTheUsageLineSaysSo()
    {
        Write(_roots.Profile, "haiku");
        Reconcile();
        string before = File.ReadAllText(Path.Combine(_roots.Profile, "haiku", SkillCatalog.FileName));
        Editor(SkillActors.Reflection).Describe("update", "", "haiku", null, "Count them twice.", "Counted twice.");
        Editor(SkillActors.Reflection).DescribeWrite("haiku", "data/forms.txt", "5-7-5");

        var skill = SkillOf("haiku");
        var facts = _records.FactsOf(skill)!;
        Assert.Equal(2, facts.ReflectionWrites);
        Assert.Equal(SkillEventKinds.File, facts.LastReflectionWrite!.Kind);
        Assert.Equal("data/forms.txt", facts.LastReflectionWrite.Detail);
        Assert.Null(facts.HandEditedAt);
        Assert.Equal("written by a reflection 2× (updated " + Sessions.SessionText.Moment(_time.GetUtcNow(), _time.LocalTimeZone) + ")", _records.UsageLine(skill, _time.LocalTimeZone));
        var mark = _records.LastReflectionWrite()!;
        Assert.Equal(("haiku", SkillEventKinds.File), (mark.Skill.Name, mark.Event.Kind));

        long id = _store.Find(SkillScope.Profile, "neon", "haiku")!.Id;
        var revisions = _store.Revisions(id);
        Assert.Equal(("data/forms.txt", (string?)null), (revisions[0].Path, revisions[0].Content));   // the file was new
        Assert.Equal((SkillCatalog.FileName, before, SkillActors.Reflection), (revisions[1].Path, revisions[1].Content, revisions[1].Actor));
        Assert.Equal("Counted twice.", _store.Events(id).Single(e => e.Kind == SkillEventKinds.Updated).Detail);

        // The pane's rename: the row, its history and the mark follow.
        _records.Renamed(skill, "haiku-forms");
        Assert.Equal("haiku-forms", _records.LastReflectionWrite()!.Skill.Folder);
    }

    /// <summary>
    /// The Skills pane's revert (2026-10-04, the user's call): any kept version put back, the current text kept first and the pick kept
    /// too, so the user can go back and forth; a file the change created goes; the version the file holds is unchanged.
    /// </summary>
    [Fact]
    public void Restore_PutsAPickedVersionBack_KeepsTheCurrentText_AndThePick()
    {
        Write(_roots.Profile, "haiku");
        Reconcile();
        string path = Path.Combine(_roots.Profile, "haiku", SkillCatalog.FileName);
        string notes = Path.Combine(_roots.Profile, "haiku", "notes.md");
        string v0 = File.ReadAllText(path);
        var editor = Editor(SkillActors.Model);
        editor.Describe("update", "", "haiku", null, "Version one.");
        string v1 = File.ReadAllText(path);
        editor.Describe("update", "", "haiku", null, "Version two.");
        string v2 = File.ReadAllText(path);
        editor.DescribeWrite("haiku", "notes.md", "new");
        var skill = SkillOf("haiku");

        var list = _records.Revisions(skill);
        Assert.Equal([("notes.md", null), (SkillCatalog.FileName, v1), (SkillCatalog.FileName, v0)], list.Select(r => (r.Path, r.Content)));
        Assert.All(list, r => Assert.False(SkillRecords.IsCurrent(skill, r)));

        // The oldest: the current text is kept first, as a revert's version, and the pick stays on the list.
        var oldest = _records.Restore(skill, list[2]);
        Assert.Equal(SkillRevertOutcome.Reverted, oldest.Outcome);
        Assert.Equal(v0, File.ReadAllText(path));
        Assert.Equal("(↩️ haiku: SKILL.md is back as it was before the model's change at " + Sessions.SessionText.Moment(_time.GetUtcNow(), _time.LocalTimeZone) + ")",
            SkillRecordText.RevertText("haiku", oldest, _time.LocalTimeZone).Text);
        list = _records.Revisions(skill);
        Assert.Equal(4, list.Count);
        Assert.Equal((SkillCatalog.FileName, v2, SkillActors.Revert), (list[0].Path, list[0].Content, list[0].Actor));
        Assert.True(SkillRecords.IsCurrent(skill, list[3]));

        // Back to where it was: v0 is on the list already, so nothing new is kept.
        Assert.Equal(SkillRevertOutcome.Reverted, _records.Restore(skill, list[0]).Outcome);
        Assert.Equal(v2, File.ReadAllText(path));
        Assert.Equal(4, _records.Revisions(skill).Count);
        Assert.Equal("(↩️ haiku: SKILL.md is back as it was before a revert at " + Sessions.SessionText.Moment(_time.GetUtcNow(), _time.LocalTimeZone) + ")",
            SkillRecordText.RevertedNotice("haiku", list[0], _time.LocalTimeZone));

        var same = _records.Restore(skill, list[0]);
        Assert.Equal(SkillRevertOutcome.Unchanged, same.Outcome);
        Assert.Equal((false, SkillRecordText.UnchangedNotice("haiku")), SkillRecordText.RevertText("haiku", same, _time.LocalTimeZone));

        // A version from before the file was there removes it, its text kept first.
        var removed = _records.Restore(skill, list[1]);
        Assert.Equal(SkillRevertOutcome.Reverted, removed.Outcome);
        Assert.False(File.Exists(notes));
        Assert.StartsWith("(↩️ haiku: notes.md is removed: it did not exist before the model's change at ", SkillRecordText.RevertText("haiku", removed, _time.LocalTimeZone).Text, StringComparison.Ordinal);
        Assert.Contains(_records.Revisions(skill), r => r.Path == "notes.md" && r.Content == "new" && r.Actor == SkillActors.Revert);

        Assert.Equal(default, Reconcile());   // the restores are the app's own writes, never a hand edit
        long id = _store.Find(SkillScope.Profile, "neon", "haiku")!.Id;
        Assert.Equal(3, _store.Events(id).Count(e => e.Kind == SkillEventKinds.Reverted));
    }

    [Fact]
    public void Restore_AVersionNoLongerKept_IsRefused()
    {
        Write(_roots.Profile, "haiku");
        Reconcile();
        Editor(SkillActors.Model).Describe("update", "", "haiku", null, "Version one.");
        var skill = SkillOf("haiku");
        var gone = _records.Restore(skill, _records.Revisions(skill)[0] with { Id = 9999 });
        Assert.Equal(SkillRevertOutcome.NoRevision, gone.Outcome);
        Assert.Equal((false, SkillRecordText.VersionGoneError("haiku")), SkillRecordText.RevertText("haiku", gone, _time.LocalTimeZone));
        Assert.Empty(_records.Revisions(SkillOf("haiku") with { Scope = SkillScope.External }));
    }

    /// <summary>The hand edit written to the haiku's SKILL.md with a file time past the row's, as an editor outside the app leaves it.</summary>
    private string HandEdit(string text, int minutesAhead = 10)
    {
        string path = Path.Combine(_roots.Profile, "haiku", SkillCatalog.FileName);
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(minutesAhead));
        return path;
    }

    private IReadOnlyList<SkillRevision> HandCopies(string folder = "haiku") =>
        _store.Revisions(_store.Find(SkillScope.Profile, "neon", folder)!.Id).Where(r => r.IsHandEdit).ToList();

    /// <summary>2026-10-04 (the user's call): the reconcile keeps a hand edit's text, so a restore past the edit never loses it.</summary>
    [Fact]
    public void AHandEdit_IsAnEvent_TheReconcileKeepsACopy_AndARestoreNeverLosesIt()
    {
        Write(_roots.Profile, "haiku");
        Reconcile();
        string path = Path.Combine(_roots.Profile, "haiku", SkillCatalog.FileName);
        string v0 = File.ReadAllText(path);
        Editor(SkillActors.Reflection).Describe("update", "", "haiku", null, "The reflection's text.");
        const string Mine = "---\nname: haiku\ndescription: Does things.\n---\nMy own words.\n";
        HandEdit(Mine);

        Assert.Equal(1, Reconcile().Modified);
        var skill = SkillOf("haiku");
        Assert.NotNull(_records.FactsOf(skill)!.HandEditedAt);
        Assert.Contains("; edited by hand ", _records.UsageLine(skill, _time.LocalTimeZone), StringComparison.Ordinal);
        var copy = Assert.Single(HandCopies());
        Assert.Equal((SkillCatalog.FileName, Mine, SkillActors.User), (copy.Path, copy.Content, copy.Actor));
        var list = _records.Revisions(skill);
        Assert.Equal([copy.Id, list[1].Id], list.Select(r => r.Id));
        Assert.True(SkillRecords.IsCurrent(skill, copy));

        // Past the edit: the copy holds it already, so nothing more is kept.
        Assert.Equal(SkillRevertOutcome.Reverted, _records.Restore(skill, list[1]).Outcome);
        Assert.Equal(v0, File.ReadAllText(path));
        Assert.Equal(2, _records.Revisions(skill).Count);
        Assert.Null(_records.FactsOf(skill)!.HandEditedAt);

        // And back to the edit, with its own notice.
        var back = _records.Restore(skill, copy);
        Assert.Equal(SkillRevertOutcome.Reverted, back.Outcome);
        Assert.Equal(Mine, File.ReadAllText(path));
        Assert.Equal((true, "(↩️ haiku: SKILL.md is back to your edit of " + Sessions.SessionText.Moment(copy.At, _time.LocalTimeZone) + ")"),
            SkillRecordText.RevertText("haiku", back, _time.LocalTimeZone));
        Assert.Equal(2, _records.Revisions(skill).Count);
        Assert.Equal(default, Reconcile());
    }

    [Fact]
    public void ANewerHandEdit_ReplacesTheCopy_AndAnAppWriteDropsIt()
    {
        Write(_roots.Profile, "haiku");
        Reconcile();
        HandEdit("---\nname: haiku\ndescription: Does things.\n---\nFirst words.\n", 10);
        Reconcile();
        string path = HandEdit("---\nname: haiku\ndescription: Does things.\n---\nSecond words.\n", 20);
        Reconcile();
        Assert.Contains("Second words.", Assert.Single(HandCopies()).Content, StringComparison.Ordinal);

        // The app's write keeps the hand-edited text as its own revision: the copy goes, so the edit is listed once, not twice.
        _time.Advance(TimeSpan.FromMinutes(30));
        Editor(SkillActors.Model).Describe("update", "", "haiku", null, "The model's text.");
        Assert.Empty(HandCopies());
        var skill = SkillOf("haiku");
        var kept = Assert.Single(_records.Revisions(skill));
        Assert.Equal(SkillRevertOutcome.Reverted, _records.Restore(skill, kept).Outcome);
        Assert.Contains("Second words.", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void ACurrentTextTooLongToKeep_KeepsNoCopy_AndTheRestoreIsRefused()
    {
        Write(_roots.Profile, "haiku");
        Reconcile();
        Editor(SkillActors.Reflection).Describe("update", "", "haiku", null, "The reflection's text.");
        string mine = "---\nname: haiku\ndescription: Does things.\n---\n" + new string('x', SkillRecordStore.MaxRevisionChars) + "\n";
        string path = HandEdit(mine);
        Reconcile();
        Assert.Empty(HandCopies());

        var skill = SkillOf("haiku");
        var refused = _records.Restore(skill, Assert.Single(_records.Revisions(skill)));
        Assert.Equal(SkillRevertOutcome.NotKept, refused.Outcome);
        Assert.Equal(mine, File.ReadAllText(path));
        Assert.Equal((false, SkillRecordText.NotKeptError("haiku", SkillCatalog.FileName)), SkillRecordText.RevertText("haiku", refused, _time.LocalTimeZone));
        Assert.Single(_records.Revisions(skill));
    }

    [Fact]
    public void TurnUsed_RecordsEachLoadedSkill_WithTheErrorsAfterItsLoad()
    {
        Write(_roots.Profile, "haiku");
        Write(_roots.Global, "pdf");
        Reconcile();
        var trace = new Llm.TurnTrace();
        trace.Observe(new Llm.TurnEvent.ToolCall(LoadSkillTool.ToolName, "c1", "{}"));
        trace.Observe(new Llm.TurnEvent.ToolResult(LoadSkillTool.ToolName, "c1", "<skill_content name=\"haiku\">\nx\n</skill_content>"));
        trace.Observe(new Llm.TurnEvent.ToolCall("run_command", "c2", "{}"));
        trace.Observe(new Llm.TurnEvent.ToolResult("run_command", "c2", "Error: exit 1"));
        var catalog = new SkillCatalog(() => _roots);
        catalog.Scan(external: false);

        _records.TurnUsed(trace, catalog.Skills, 4);
        _records.TurnUsed(trace, catalog.Skills, 5);

        Assert.Equal(new SkillUseFacts(2, 2, 2, _time.GetUtcNow()), _store.UseFacts(_store.Find(SkillScope.Profile, "neon", "haiku")!.Id));
        Assert.Equal("loaded 2 times across 2 sessions, 2 followed by errors; last loaded " + Sessions.SessionText.Moment(_time.GetUtcNow(), _time.LocalTimeZone), _records.UsageLine(catalog.Find("haiku")!, _time.LocalTimeZone));
    }

    [Fact]
    public void Installed_KeepsTheOrigin_TheReplacedSkillMd_AndCountsTheReflectionsChangesSince()
    {
        string dir = Write(_roots.Profile, "pdf");
        File.WriteAllText(Path.Combine(dir, SkillProvenance.FileName), new SkillProvenance { Repo = "owner/repo" }.ToJson());
        _records.Installed(new SkillInstallResult(true, false, SkillScope.Profile, dir));
        var skill = SkillOf("pdf");
        Assert.Equal("installed from owner/repo", _records.UsageLine(skill, _time.LocalTimeZone));

        _time.Advance(TimeSpan.FromMinutes(1));
        Editor(SkillActors.Reflection).Describe("update", "", "pdf", null, "Reflected.");
        Assert.Equal(1, _records.ReflectionChangesSinceInstall(skill));
        Assert.EndsWith("; installed from owner/repo, changed by a reflection since", _records.UsageLine(skill, _time.LocalTimeZone), StringComparison.Ordinal);

        _time.Advance(TimeSpan.FromMinutes(1));
        _records.Installed(new SkillInstallResult(true, true, SkillScope.Profile, dir) { Previous = "the reflected text" });
        Assert.Equal(0, _records.ReflectionChangesSinceInstall(skill));
        var revision = _records.LatestRevision(skill)!;
        Assert.Equal(("the reflected text", SkillActors.Install), (revision.Content, revision.Actor));
        Assert.Equal("A reflection changed this skill 2× since it was installed; updating replaces that (the skill's revert in /skills brings the SKILL.md back).", SkillRecordText.ChangedSinceInstallWarning(2));
    }

    [Fact]
    public void ImportFrom_CopiesTheSessionStoresHistoryOnce_SkippingSkillsGoneSince()
    {
        Write(_roots.Profile, "haiku");
        Reconcile();
        using var sessions = new Sessions.SessionStore(Path.Combine(_dir, "profiles", "neon"), _time);
        long id = sessions.Begin("t", "m")!.Value;
        sessions.AppendTurn(id, "write a haiku", "Done.", 2, ["load_skill"], ["haiku", "gone"], 1, 1, 1, false);
        sessions.RecordReflection(new Sessions.ReflectionRow(id, 1, false, Sessions.ReflectionRow.Learned, "haiku", Sessions.ReflectionRow.Updated, 1, 1, 1));
        sessions.RecordReflection(new Sessions.ReflectionRow(id, 1, false, Sessions.ReflectionRow.Learned, "gone", Sessions.ReflectionRow.Created, 1, 1, 1));
        var catalog = new SkillCatalog(() => _roots);
        catalog.Scan(external: false);

        Assert.Equal(2, _records.ImportFrom(sessions, catalog.Skills));
        Assert.Equal(0, _records.ImportFrom(sessions, catalog.Skills));
        var facts = _records.FactsOf(catalog.Find("haiku")!)!;
        Assert.Equal(1, facts.ReflectionWrites);
        Assert.Equal(new SkillUseFacts(1, 1, 1, _time.GetUtcNow()), facts.Uses);
        Assert.Equal("imported-sessions:neon", SkillRecords.ImportFlag("neon"));
        Assert.Equal("Skill records: imported 2 events from profile \"neon\"'s sessions.db", SkillRecordText.ImportedLogLine("neon", 2));
    }

    [Fact]
    public void UnusedSince_GoesByTheLastUse_ElseTheLastChange()
    {
        var now = _time.GetUtcNow();
        _store.Insert(SkillScope.Global, "", "old-unused", "old-unused", now.AddDays(-90), now.AddDays(-60));
        _store.Insert(SkillScope.Global, "", "fresh-unused", "fresh-unused", now.AddDays(-90), now.AddDays(-2));
        _store.Insert(SkillScope.Profile, "neon", "used-lately", "used-lately", now.AddDays(-90), now.AddDays(-90));
        _store.Used(SkillScope.Profile, "neon", "used-lately", "used-lately", now.AddDays(-1));
        _store.Insert(SkillScope.Profile, "neon", "used-long-ago", "used-long-ago", now.AddDays(-90), now.AddDays(-1));
        _store.Used(SkillScope.Profile, "neon", "used-long-ago", "used-long-ago", now.AddDays(-40));
        _store.Insert(SkillScope.Profile, "ada", "another-profile", "another-profile", now.AddDays(-90), now.AddDays(-90));

        var stale = _records.UnusedSince(now.AddDays(-30));

        Assert.Equal(["old-unused", "used-long-ago"], stale.Select(r => r.Folder));   // oldest first; ada's never in neon's view
    }

    /// <summary>The dry run's table (2026-09-30, the user's ask): a header, one row per skill, every column padded to its widest cell.</summary>
    [Fact]
    public void Table_LinesTheColumnsUp()
    {
        var at = new DateTimeOffset(2026, 8, 1, 14, 5, 0, TimeSpan.Zero);
        var rows = new[]
        {
            new SkillRecord(1, SkillScope.Global, "pdf", "pdf", "pdf", null, at.AddDays(-9), at.AddDays(-9), at),
            new SkillRecord(2, SkillScope.Profile, "neon", "meeting-notes", "meeting-notes", null, at.AddDays(-9), at, null),
        };

        var table = SkillRecordText.Table(rows, TimeZoneInfo.Utc);

        Assert.Equal(
        [
            "Skill           Scope     Last used          Modified",
            "pdf             global    2026-08-01 14:05   2026-07-23 14:05",
            "meeting-notes   profile   never              2026-08-01 14:05",
        ], table);
        Assert.Equal(["Skill", "Scope", "Last used", "Modified"], SkillRecordText.TableHeaders);
        Assert.Single(SkillRecordText.Table([], TimeZoneInfo.Utc));   // the header alone
    }

    [Theory]
    [InlineData("purge list 30", SkillRecordText.PurgeKind.List, 30 * 24)]
    [InlineData("PURGE Commit 12h", SkillRecordText.PurgeKind.Commit, 12)]
    [InlineData("purge list 1d 6h", SkillRecordText.PurgeKind.List, 30)]
    [InlineData("purge", SkillRecordText.PurgeKind.Invalid, 0)]
    [InlineData("purge list", SkillRecordText.PurgeKind.Invalid, 0)]
    [InlineData("purge older 30", SkillRecordText.PurgeKind.Invalid, 0)]
    [InlineData("purge list soon", SkillRecordText.PurgeKind.Invalid, 0)]
    [InlineData("add pdf", SkillRecordText.PurgeKind.None, 0)]
    [InlineData("", SkillRecordText.PurgeKind.None, 0)]
    public void ParsePurge_ReadsListOrCommitAndAnAge(string args, SkillRecordText.PurgeKind kind, int hours)
    {
        Assert.Equal((kind, TimeSpan.FromHours(hours)), SkillRecordText.ParsePurge(args));
    }

    [Fact]
    public void Words_ArePinned()
    {
        var zone = TimeZoneInfo.Utc;
        var at = new DateTimeOffset(2026, 8, 1, 14, 5, 0, TimeSpan.Zero);
        var used = new SkillRecord(1, SkillScope.Global, "", "pdf", "pdf", null, at.AddDays(-9), at.AddDays(-9), at);
        var never = new SkillRecord(2, SkillScope.Profile, "neon", "haiku", "haiku", null, at.AddDays(-9), at, null);
        var days = TimeSpan.FromDays(30);

        Assert.Equal("pdf · global · last used 2026-08-01 14:05", SkillRecordText.Line(used, zone));
        Assert.Equal("haiku · profile · never used, modified 2026-08-01 14:05", SkillRecordText.Line(never, zone));
        Assert.Equal("(🧹 no skills unused for 30 days)", SkillRecordText.NoneNotice(days));
        Assert.Equal("(🧹 2 skills unused for 30 days; /skills purge commit 30 deletes them)", SkillRecordText.ListNotice(2, days, "30"));
        Assert.Equal("(🧹 1 skill unused for 30 days; /skills purge commit 30 deletes it)", SkillRecordText.ListNotice(1, days, "30"));
        Assert.Equal("🧹 Delete 2 skills unused for 30 days?", SkillRecordText.CommitPrompt(2, days));
        Assert.Equal("(🗑️ deleted 2 skills unused for 30 days: pdf, haiku)", SkillRecordText.PurgedNotice(["pdf", "haiku"], days));
        Assert.Equal("Could not delete the skill pdf (global): in use", SkillRecordText.PurgeFailedError(used, "in use"));
        Assert.Equal("Skills reconciled: 2 added, 1 removed, 0 modified (global + profile neon)", SkillRecordText.ReconciledLogLine(new SkillReconcile(2, 1, 0, 0), "neon"));
        Assert.Equal("Skills purge: 2 deleted, 0 failed, unused for 30 days", SkillRecordText.PurgeSummaryLogLine(2, 0, days));
        Assert.Equal("Skill purged: profile/haiku (last used never, modified 2026-08-01T14:05:00.0000000Z)", SkillRecordText.PurgedLogLine(never));
        Assert.Equal(SkillInstallText.UsageError + ", or /skills purge list|commit <age>", SkillRecordText.SkillsUsageError);
        Assert.Equal(at, never.Reference);
        Assert.Equal(at, used.Reference);
    }
}
