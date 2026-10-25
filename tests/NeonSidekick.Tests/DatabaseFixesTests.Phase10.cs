using NeonSidekick.Files;
using NeonSidekick.Shell;

namespace NeonSidekick.Tests;

/// <summary>
/// Phase 10 of the database review's fixes (2026-10-24): the shell police's database rules and the file guard — the extensions and words
/// the police know, names read as Windows opens them, the script files a line or a script runs, the lookups that reach nothing, hosts
/// matched where a host stands, and a database found by its content beside its journal and inside a folder.
/// </summary>
public sealed class DatabasePoliceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("neon-dbfix-police-").FullName;

    public void Dispose() => GitAccessTests.DeleteTree(_dir);

    private static readonly ServerDatabaseGuard.Family Postgres =
        new("PostgreSQL", "postgres_query", "postgres_execute", ServerDatabasePolice.PostgresWords, [.. ServerDatabasePolice.Hosts("db,localhost", 5432)]);

    private static readonly ServerDatabaseGuard.Family SqlServer = new("SQL Server", "sql_query", "sql_execute", ServerDatabasePolice.SqlServerWords, []);

    private static readonly ServerDatabaseGuard.Family Oracle = new("Oracle", "oracle_query", "oracle_execute", ServerDatabasePolice.OracleWords, [.. ServerDatabasePolice.OracleHosts("test")]);

    private static readonly ServerDatabaseGuard Servers = new([Postgres, SqlServer, Oracle]);

    // ── The SQLite police ───────────────────────────────────────────────────

    [Theory]
    [InlineData("copy x app.s3db")]                    // A9: every extension the guard knows
    [InlineData("type data.sqlitedb")]
    [InlineData("del *.db")]                           // G15: a wildcard
    [InlineData("del app?.db")]
    [InlineData("python -c \"import apsw\"")]          // G15: a module with no sqlite in its name
    [InlineData("type app.db.")]                       // Q1: trailing dots Windows drops
    [InlineData("type app.db::$DATA")]                 // Q1: a stream
    [InlineData("type my(1).db")]                      // Q1: punctuation before the extension
    [InlineData("type a+b.db")]
    public void Sqlite_ALine_Reaches(string line) => Assert.NotNull(SqlitePolice.Judge(line, _dir, _dir, DatabaseGuard.ByExtension));

    [Theory]
    [InlineData("grep -rn sqlite src")]                // Q5: a lookup reaches nothing
    [InlineData("git commit -m \"sqlite: fix the reader\"")]
    [InlineData("echo done")]
    public void Sqlite_ALine_ReachesNothing(string line) => Assert.Null(SqlitePolice.Judge(line, _dir, _dir, DatabaseGuard.ByExtension));

    [Fact]
    public void Sqlite_AScriptName_ReadAsWindowsOpensIt()
    {
        Assert.NotNull(SqlitePolice.Find("open('app.db. ')", script: true, DatabaseGuard.ByExtension));   // Q1
        Assert.NotNull(SqlitePolice.Find("open(\"my(1).sl3\")", script: true, DatabaseGuard.ByExtension)); // A9, Q1
        Assert.Null(SqlitePolice.Find("self.db.execute(q)", script: true, DatabaseGuard.ByExtension));
    }

    [Fact]
    public void Scripts_TheOnesALineRuns_AreRead()
    {
        // Q2: an option with a value before the script; cmd's lookup of an extensionless command; a script that runs another.
        File.WriteAllText(Path.Combine(_dir, "job"), "import sqlite3");
        File.WriteAllText(Path.Combine(_dir, "run.cmd"), "@echo off");
        File.WriteAllText(Path.Combine(_dir, "outer.py"), "import subprocess\nsubprocess.run(['python', 'inner.py'])");
        File.WriteAllText(Path.Combine(_dir, "inner.py"), "import sqlite3");

        Assert.Equal("job", Assert.Single(SqlitePolice.ScriptFiles("python -X utf8 job", _dir, _dir)).Name);
        Assert.Equal("run.cmd", Assert.Single(SqlitePolice.ScriptFiles("run --fast", _dir, _dir)).Name);
        Assert.Equal("run.cmd", Assert.Single(SqlitePolice.ScriptFiles("cmd /c run", _dir, _dir)).Name);
        Assert.Equal(["outer.py", "inner.py"], SqlitePolice.ScriptFiles("python outer.py", _dir, _dir).Select(s => s.Name));
        Assert.Equal(("sqlite3", "inner.py"), SqlitePolice.Judge("python outer.py", _dir, _dir, DatabaseGuard.ByExtension));
        Assert.Empty(SqlitePolice.ScriptFiles("python -c \"print(1)\" job", _dir, _dir));   // an inline command ends the search
    }

    // ── The server-database police ──────────────────────────────────────────

    [Theory]
    [InlineData("node -e \"require('tedious')\"", "SQL Server")]                                    // A14
    [InlineData("npm run x && node -e \"require('pg')\"", "PostgreSQL")]
    [InlineData("Driver={SQL Server};Server=x", "SQL Server")]                                       // G12
    [InlineData("Provider=MSOLEDBSQL;Data Source=x", "SQL Server")]
    [InlineData("Invoke-DbaQuery -SqlInstance x", "SQL Server")]
    [InlineData("java -cp ojdbc11.jar App", "Oracle")]                                              // Q4
    [InlineData("vacuumdb --all", "PostgreSQL")]                                                    // I8
    [InlineData("pg_isready -h x", "PostgreSQL")]
    [InlineData("sqlldr control=x.ctl", "Oracle")]
    [InlineData("nc -h db 5432", "PostgreSQL")]                                                     // Q6: a one-label host where a host stands
    [InlineData("connect to sales@test", "Oracle")]
    [InlineData("python app.py --host localhost --port 5432", "PostgreSQL")]                       // A14: a loopback host and port apart
    public void Servers_ALine_Reaches(string line, string family) => Assert.Equal(family, ServerDatabasePolice.Judge(line, _dir, _dir, Servers)?.Family);

    [Theory]
    [InlineData("cd db && npm test")]                                                               // Q6
    [InlineData("grep -rn psql src")]                                                               // Q5
    [InlineData("git commit -m \"psql: quote the name\"")]
    [InlineData("python -c \"import math; print(math.exp(1))\"")]
    public void Servers_ALine_ReachesNothing(string line) => Assert.Null(ServerDatabasePolice.Judge(line, _dir, _dir, Servers));

    [Fact]
    public void Servers_TheHosts_AreReadAsTheDriversReadThem()
    {
        Assert.Equal(["db1", "db2"], ServerDatabasePolice.Hosts("db1, db2", 5432));                            // D6
        Assert.Equal(["localhost:5433", "localhost,5433", "db2"], ServerDatabasePolice.Hosts("localhost:5433,db2", 5432));
        Assert.Equal(["sqlbox"], ServerDatabasePolice.SqlServerHosts(@"np:\\sqlbox\pipe\sql\query"));         // B8
        Assert.Equal(["sqlbox"], ServerDatabasePolice.SqlServerHosts("admin:sqlbox"));
        Assert.Equal(["orahost"], ServerDatabasePolice.OracleHosts("tcps://orahost:2484/svc"));               // D6
        Assert.Equal(["h1", "h2"], ServerDatabasePolice.OracleHosts("h1,h2:1521/svc"));
        Assert.Equal(["x"], ServerDatabasePolice.OracleHosts("(DESCRIPTION=(ADDRESS=(HOST=x)(PORT=99999999999)))"));   // B7: no throw
    }

    // ── The file guard ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("app.db.", true)]                      // Q1
    [InlineData("app.db  ", true)]
    [InlineData("app.db:stream", true)]
    [InlineData("notes.txt.", false)]
    public void TheGuard_ReadsANameAsWindowsOpensIt(string name, bool database) => Assert.Equal(database, DatabaseGuard.HasDatabaseName(name));

    [Fact]
    public void TheGuard_ANamedFile_InAnotherNormalForm_IsThatFile()
    {
        // Q8: a decomposed é names the same file as a composed one.
        string composed = Path.Combine(_dir, "café.data");
        var guard = new DatabaseGuard([composed]);
        Assert.True(guard.IsDatabaseFile(Path.Combine(_dir, "café.data-wal")));
    }

    [Fact]
    public void TheGuard_FindsADatabaseByContent_BesideItsJournal_AndInAFolder()
    {
        string data = Path.Combine(_dir, "store.bin");
        File.WriteAllBytes(data, [.. "SQLite format 3\0"u8.ToArray(), .. new byte[496]]);   // one 512-byte page, the smallest database
        Assert.True(DatabaseGuard.HoldsSqlite(data + "-wal"));                                                 // J3
        Assert.False(DatabaseGuard.HoldsSqlite(Path.Combine(_dir, "other.bin-wal")));

        string folder = Path.Combine(_dir, "nested");
        Directory.CreateDirectory(folder);
        File.Copy(data, Path.Combine(folder, "plain"));
        Assert.Equal(FileOutcome.DatabaseProtected, WorkingDirectory.HoldsGuarded(folder, git: false, DatabaseGuard.ByExtension, CancellationToken.None));   // B6
    }
}
