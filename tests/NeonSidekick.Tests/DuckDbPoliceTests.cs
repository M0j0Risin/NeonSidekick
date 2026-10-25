using NeonSidekick.App;
using NeonSidekick.Files;
using NeonSidekick.Settings;
using NeonSidekick.Shell;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The file guard and the shell police for DuckDB (2026-10-25): built from the families whose tools are on, a DuckDB file known by its name,
/// its <c>.wal</c>, its header and <c>duckdb.json</c>'s names, the word <c>duckdb</c> in a line or a script, and the refusal worded per family.
/// </summary>
public sealed class DuckDbPoliceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("neon-duckdb-police-").FullName;
    private readonly ManualTimeProvider _time = new();
    private static readonly DatabaseGuard Duck = new([], DatabaseFamilies.DuckDb);
    private static readonly DatabaseGuard Both = new([], DatabaseFamilies.Sqlite | DatabaseFamilies.DuckDb);

    public void Dispose() => GitAccessTests.DeleteTree(_dir);

    [Theory]
    [InlineData("lake.duckdb", true)]
    [InlineData("LAKE.DDB", true)]
    [InlineData("lake.duckdb.wal", true)]
    [InlineData(".duckdb", false)]
    [InlineData("lake.duckdbx", false)]
    [InlineData("app.db", false)]
    [InlineData("app.db-wal", false)]
    public void TheGuard_KnowsADuckDbFileByItsName_AndOnlyDuckDbsWhileOnlyItsToolsAreOn(string name, bool database) =>
        Assert.Equal(database, Duck.IsDatabaseName(name));

    [Fact]
    public void TheGuard_ForBoth_KnowsEachFamily_AndSaysWhichATokenReaches()
    {
        Assert.True(Both.IsDatabaseName("app.db"));
        Assert.True(Both.IsDatabaseName("lake.duckdb.wal"));
        Assert.Equal(DatabaseFamilies.DuckDb, Both.FamilyOf("duckdb"));
        Assert.Equal(DatabaseFamilies.DuckDb, Both.FamilyOf("lake.ddb"));
        Assert.Equal(DatabaseFamilies.Sqlite, Both.FamilyOf("sqlite3"));
        Assert.Equal(DatabaseFamilies.Sqlite, Both.FamilyOf("app.db"));
        Assert.Equal(DatabaseFamilies.DuckDb, Duck.FamilyOf("app.db"));   // only DuckDB's tools on
        Assert.Equal(DatabaseFamilies.Sqlite, DatabaseGuard.ByExtension.FamilyOf("lake.duckdb"));

        var named = new DatabaseGuard([Path.Combine(_dir, "a.data"), Path.Combine(_dir, "b.data")], DatabaseFamilies.Sqlite | DatabaseFamilies.DuckDb, [Path.Combine(_dir, "b.data")]);
        Assert.Equal(DatabaseFamilies.DuckDb, named.FamilyOf("b.data"));
        Assert.Equal(DatabaseFamilies.Sqlite, named.FamilyOf("a.data"));
        Assert.True(named.IsDatabaseFile(Path.Combine(_dir, "b.data.wal")));
    }

    [Fact]
    public void TheGuard_ReadsTheHeader_OfTheFamiliesOn()
    {
        string disguised = Path.Combine(_dir, "data.bin");
        DuckDbToolsTests.Make(disguised, "CREATE TABLE t (a INTEGER)");
        Assert.True(Duck.HasDatabaseHeader(disguised));
        Assert.True(Duck.Holds(disguised));
        Assert.False(DatabaseGuard.ByExtension.HasDatabaseHeader(disguised));
        Assert.False(Duck.HasDatabaseHeader(Path.Combine(_dir, "missing.bin")));
    }

    [Theory]
    [InlineData("duckdb lake.duckdb \"SELECT 1\"", false, "duckdb")]
    [InlineData("python -c \"import duckdb; duckdb.connect('x')\"", false, "duckdb")]
    [InlineData("copy lake.duckdb backup", false, "lake.duckdb")]
    [InlineData("del data\\lake.duckdb.wal", false, "lake.duckdb.wal")]
    [InlineData("conn = open_db(\"data/lake.ddb\")", true, "lake.ddb")]
    [InlineData("library(DuckDB)", true, "DuckDB")]
    public void Find_TheWord_AFileName_AsALineOrAScriptReadsIt(string text, bool script, string token) =>
        Assert.Equal(token, SqlitePolice.Find(text, script, Duck));

    [Theory]
    [InlineData("sqlite3 app.db")]
    [InlineData("copy app.db backup")]
    [InlineData("print('hello')")]
    public void Find_OnlyDuckDbsWords_WhileOnlyItsToolsAreOn(string text) => Assert.Null(SqlitePolice.Find(text, script: false, Duck));

    [Fact]
    public void Find_ForBoth_FindsEach_AndSqliteAloneLeavesDuckDbAlone()
    {
        Assert.Equal("sqlite3", SqlitePolice.Find("sqlite3 app.db", script: false, Both));
        Assert.Equal("duckdb", SqlitePolice.Find("duckdb lake.duckdb", script: false, Both));
        Assert.Null(SqlitePolice.Find("duckdb lake.duckdb", script: false, DatabaseGuard.ByExtension));
    }

    [WindowsFact]
    public void Judge_ReadsTheScriptFilesALineRuns()
    {
        string work = Path.Combine(_dir, "work");
        Directory.CreateDirectory(work);
        File.WriteAllText(Path.Combine(work, "load.py"), "import duckdb\nduckdb.connect('lake.duckdb').execute('INSERT INTO t VALUES (1)')\n");
        File.WriteAllText(Path.Combine(work, "fine.py"), "print('hello')\n");
        Assert.Equal(("duckdb", "load.py"), SqlitePolice.Judge("python load.py", work, work, Duck));
        Assert.Null(SqlitePolice.Judge("python fine.py", work, work, Duck));
    }

    [UnixFact]
    public void Judge_ReadsTheScriptFilesALineRuns_WithUnixSpellings()
    {
        string work = Path.Combine(_dir, "work");
        Directory.CreateDirectory(work);
        File.WriteAllText(Path.Combine(work, "load.py"), "import duckdb\nduckdb.connect('lake.duckdb').execute('INSERT INTO t VALUES (1)')\n");
        Assert.Equal(("duckdb", "load.py"), SqlitePolice.Judge("python3 ./load.py", work, work, Duck));
    }

    [Fact]
    public void TheRefusal_IsWordedPerFamily()
    {
        var request = new CommandRequest("powershell", "duckdb lake.duckdb", []);
        Assert.Equal((ShellText.DuckDbPoliced, ShellText.DuckDbShown("duckdb", null)), ShellText.FileDatabasePoliced(request, Both, "duckdb", null));
        Assert.Equal((ShellText.SqlitePoliced, ShellText.SqliteShown("sqlite3", "x.py")), ShellText.FileDatabasePoliced(request, Both, "sqlite3", "x.py"));
        Assert.True(ShellText.IsPoliced(ShellText.DuckDbPoliced));
        Assert.Contains("duckdb_query to read", ShellText.DuckDbPoliced);
        Assert.Equal("DuckDB: 'duckdb' in load.py — not run", ShellText.DuckDbShown("duckdb", "load.py"));
        Assert.Equal("police: duckdb ('duckdb') — powershell \"duckdb lake.duckdb\"", ShellText.DuckDbLogLine(request, "duckdb", null));
        Assert.Contains("SQLite or DuckDB database", FileText.DatabaseProtected("a.duckdb"));
    }

    /// <summary>The guard is built only from the families whose tools are on, and none while both are off.</summary>
    [Fact]
    public void TheScreensGuard_FollowsTheSwitches()
    {
        string profile = Path.Combine(_dir, "profile");
        Directory.CreateDirectory(Profiles.ConfigsDirectory(profile));
        Assert.Null(ChatScreen.DatabaseGuardFor(new AppSettingsData { SqliteTools = false, DuckDbTools = false }, profile, Path.Combine(_dir, "home")));
        Assert.Equal(DatabaseFamilies.Sqlite, ChatScreen.DatabaseGuardFor(new AppSettingsData { SqliteTools = true, DuckDbTools = false }, profile, Path.Combine(_dir, "home"))!.Families);
        Assert.Equal(DatabaseFamilies.DuckDb, ChatScreen.DatabaseGuardFor(new AppSettingsData { SqliteTools = false, DuckDbTools = true }, profile, Path.Combine(_dir, "home"))!.Families);
        Assert.Equal(DatabaseFamilies.Sqlite | DatabaseFamilies.DuckDb, ChatScreen.DatabaseGuardFor(new AppSettingsData { SqliteTools = true, DuckDbTools = true }, profile, Path.Combine(_dir, "home"))!.Families);

        File.WriteAllText(NeonSidekick.DuckDb.DuckDbConfigFile.ProfilePath(profile), """{ "databases": { "lake": { "path": "../lake.data" } } }""");
        var guard = ChatScreen.DatabaseGuardFor(new AppSettingsData { SqliteTools = false, DuckDbTools = true }, profile, Path.Combine(_dir, "home"))!;
        Assert.True(guard.IsDatabaseFile(Path.Combine(profile, "lake.data")));
    }

    [Fact]
    public void TheFileTools_NeverChangeADuckDbFile_WhileItsToolsAreOn()
    {
        File.WriteAllText(Path.Combine(_dir, "lake.duckdb"), "pretend");
        DuckDbToolsTests.Make(Path.Combine(_dir, "hidden.bin"), "CREATE TABLE t (a INTEGER)");
        var files = new WorkingDirectory(() => _dir, _time) { Databases = () => Duck };
        Assert.Equal(FileOutcome.DatabaseProtected, files.WriteText("lake.duckdb", "x", overwrite: true).Outcome);
        Assert.Equal(FileOutcome.DatabaseProtected, files.WriteText("new.ddb", "x", overwrite: false).Outcome);
        Assert.Equal(FileOutcome.DatabaseProtected, files.Delete("lake.duckdb").Outcome);
        Assert.Equal(FileOutcome.DatabaseProtected, files.Delete("hidden.bin").Outcome);   // by its header
        Assert.Equal(FileOutcome.Ok, files.Copy("lake.duckdb", "copy.txt", overwrite: false).Outcome);

        var open = new WorkingDirectory(() => _dir, _time) { Databases = () => DatabaseGuard.ByExtension };   // SQLite's alone
        Assert.Equal(FileOutcome.Ok, open.WriteText("lake.duckdb", "x", overwrite: true).Outcome);
    }
}
