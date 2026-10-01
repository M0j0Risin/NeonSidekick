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
        Assert.Equal((SkillScope.Global, "haiku", _time.GetUtcNow()), (created.Scope, created.Folder, created.Created));

        _time.Advance(TimeSpan.FromHours(1));
        editor.Describe("update", "", "haiku", null, "Count them twice.");
        editor.Describe("create", "global", "haiku", "Again.", "x");   // refused: no record change
        var modified = Assert.Single(_store.All());
        Assert.Equal((created.Created, _time.GetUtcNow()), (modified.Created, modified.Modified));

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
        var at = _time.GetUtcNow();
        Assert.Equal(("neon", at), (Assert.Single(_store.All()).Profile, Assert.Single(_store.All()).Created));
        _time.Advance(TimeSpan.FromDays(1));
        _records.Installed(SkillScope.Profile, dir, updated: true);
        Assert.Equal(_time.GetUtcNow(), Assert.Single(_store.All()).Modified);

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
