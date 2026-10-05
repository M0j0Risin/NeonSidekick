using System.IO.Compression;
using NeonSidekick.Files;
using NeonSidekick.Shell;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The SQLite police and the file tools' database guard (2026-10-05, after a model under yolo wrote a script that inserted into a
/// database SQLite mode kept read-only): what counts as a database file, what in a line or a script reaches SQLite, and every
/// file-tool change to a database refused while the guard is set.
/// </summary>
public sealed class SqlitePoliceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("neon-sqlite-police-").FullName;
    private readonly ManualTimeProvider _time = new();

    public void Dispose() => GitAccessTests.DeleteTree(_dir);

    [Theory]
    [InlineData("app.db", true)]
    [InlineData("APP.SQLITE3", true)]
    [InlineData("data.db3", true)]
    [InlineData("x.sqlite", true)]
    [InlineData("app.db-wal", true)]
    [InlineData("app.db-journal", true)]
    [InlineData("app.sqlite-shm", true)]
    [InlineData(".db", false)]
    [InlineData("app.dbx", false)]
    [InlineData("notes.txt", false)]
    [InlineData("app.db.bak", false)]
    public void TheGuard_KnowsADatabaseByItsName(string name, bool database) => Assert.Equal(database, DatabaseGuard.HasDatabaseName(name));

    [Fact]
    public void TheGuard_KnowsANamedFile_WhateverItEndsIn_AndItsSiblings()
    {
        string named = Path.Combine(_dir, "shop.data");
        var guard = new DatabaseGuard([named]);
        Assert.True(guard.IsDatabaseFile(named));
        Assert.True(guard.IsDatabaseFile(named + "-wal"));
        Assert.True(guard.IsDatabaseFile(Path.Combine(_dir, "other.db")));
        Assert.False(guard.IsDatabaseFile(Path.Combine(_dir, "other.data")));
        Assert.Equal(["shop.data"], guard.NamedFileNames);
    }

    [Theory]
    [InlineData("sqlite3 app.db \".tables\"", false, "sqlite3")]
    [InlineData("python -c \"import sqlite3; sqlite3.connect('x')\"", false, "sqlite3")]
    [InlineData("Add-Type -Path .\\System.Data.SQLite.dll", false, "System.Data.SQLite.dll")]
    [InlineData("copy app.db backup", false, "app.db")]
    [InlineData("del data\\app.db-wal", false, "app.db-wal")]
    [InlineData("import better_sqlite3", true, "better_sqlite3")]
    [InlineData("const db = require('node:sqlite')", true, "node:sqlite")]
    [InlineData("conn = open_db(\"data/app.db\")", true, "app.db")]
    [InlineData("path = r'C:\\work\\x.db3'", true, "x.db3")]
    [InlineData("conn = connect('notes.db')", true, "notes.db")]
    public void Find_TheWord_AFileName_AsALineOrAScriptReadsIt(string text, bool script, string token) =>
        Assert.Equal(token, SqlitePolice.Find(text, script, DatabaseGuard.ByExtension));

    [Theory]
    [InlineData("self.db = make()\nself.db.commit()")]
    [InlineData("x = conn.db3.value")]
    [InlineData("print('hello')")]
    public void Find_InAScript_AnAttributeIsNoFile(string text) => Assert.Null(SqlitePolice.Find(text, script: true, DatabaseGuard.ByExtension));

    [Fact]
    public void Find_ANamedDatabase_ByItsPath_OrItsName()
    {
        var guard = new DatabaseGuard([@"D:\data\shop.data"]);
        Assert.Equal("shop.data", SqlitePolice.Find(@"type D:\data\shop.data", script: false, guard));
        Assert.Equal("shop.data", SqlitePolice.Find("open('D:/data/shop.data', 'rb')", script: true, guard));
        Assert.Equal("shop.data", SqlitePolice.Find("cat shop.data", script: false, guard));
        Assert.Null(SqlitePolice.Find("cat myshop.data2", script: false, guard));
    }

    [Fact]
    public void Judge_ReadsTheScriptFilesALineRuns()
    {
        string work = Path.Combine(_dir, "work");
        Directory.CreateDirectory(work);
        File.WriteAllText(Path.Combine(work, "insert.py"), "import sqlite3\nsqlite3.connect('shop.db').execute('INSERT INTO t VALUES (1)')\n");
        File.WriteAllText(Path.Combine(work, "fine.py"), "print('hello')\n");
        File.WriteAllText(Path.Combine(_dir, "outside.py"), "import sqlite3\n");

        Assert.Equal(("sqlite3", "insert.py"), SqlitePolice.Judge("python insert.py", work, work, DatabaseGuard.ByExtension));
        Assert.Equal(("sqlite3", "insert.py"), SqlitePolice.Judge("py -3 \".\\insert.py\" --go", work, work, DatabaseGuard.ByExtension));
        Assert.Null(SqlitePolice.Judge("python fine.py", work, work, DatabaseGuard.ByExtension));
        Assert.Null(SqlitePolice.Judge("python missing.py", work, work, DatabaseGuard.ByExtension));
        Assert.Null(SqlitePolice.Judge(@"python ..\outside.py", work, work, DatabaseGuard.ByExtension));   // the outside-paths police's, not read here

        File.WriteAllText(Path.Combine(work, "big.py"), "import sqlite3\n" + new string('#', (int)SqlitePolice.MaxScriptBytes));
        Assert.Null(SqlitePolice.Judge("python big.py", work, work, DatabaseGuard.ByExtension));   // past the cap, passed over
    }

    [Fact]
    public void TheWording()
    {
        Assert.Equal("SQLite: 'sqlite3' — not run", ShellText.SqliteShown("sqlite3", null));
        Assert.Equal("SQLite: 'sqlite3' in insert.py — not run", ShellText.SqliteShown("sqlite3", "insert.py"));
        Assert.True(ShellText.IsPoliced(ShellText.SqlitePoliced));
        Assert.Contains("sqlite_query to read", ShellText.SqlitePoliced);
        Assert.StartsWith("Error: 'a.db' is or holds a SQLite database", FileText.DatabaseProtected("a.db"));
    }

    // ── The file tools' guard ────────────────────────────────────────────

    private WorkingDirectory Guarded(bool on = true)
    {
        var files = new WorkingDirectory(() => _dir, _time);
        files.Databases = () => on ? DatabaseGuard.ByExtension : null;
        return files;
    }

    [Fact]
    public void TheFileTools_NeverChangeADatabase_ButReadAndCopyFromOne()
    {
        File.WriteAllText(Path.Combine(_dir, "a.db"), "pretend");
        Directory.CreateDirectory(Path.Combine(_dir, "data"));
        File.WriteAllText(Path.Combine(_dir, "data", "b.sqlite"), "pretend");
        var files = Guarded();

        Assert.Equal(FileOutcome.DatabaseProtected, files.WriteText("a.db", "x", overwrite: true).Outcome);
        Assert.Equal(FileOutcome.DatabaseProtected, files.WriteText("new.db3", "x", overwrite: false).Outcome);
        Assert.Equal(FileOutcome.DatabaseProtected, files.AppendText("a.db", "x").Outcome);
        Assert.Equal(FileOutcome.DatabaseProtected, files.EditText("a.db", "pretend", "changed").Outcome);
        Assert.Equal(FileOutcome.DatabaseProtected, files.Delete("a.db").Outcome);
        Assert.Equal(FileOutcome.DatabaseProtected, files.Delete("data").Outcome);   // a folder holding one
        Assert.Equal(FileOutcome.DatabaseProtected, files.Move("a.db", "b.txt", overwrite: false).Outcome);
        Assert.Equal(FileOutcome.DatabaseProtected, files.Move("data", "elsewhere", overwrite: false).Outcome);
        Assert.Equal(FileOutcome.DatabaseProtected, files.Move("plain.txt", "a.db", overwrite: true).Outcome);
        File.WriteAllText(Path.Combine(_dir, "plain.txt"), "plain");
        Assert.Equal(FileOutcome.DatabaseProtected, files.Copy("plain.txt", "a.db", overwrite: true).Outcome);
        Assert.Equal("pretend", File.ReadAllText(Path.Combine(_dir, "a.db")));

        // Reading, and copying from one, change nothing.
        Assert.Equal(FileOutcome.Ok, files.Copy("a.db", "a-copy.txt", overwrite: false).Outcome);
        Assert.Equal(FileOutcome.Ok, files.Resolve("a.db", forWrite: false, out _));

        // An archive with an entry landing on a database: nothing extracted.
        string zip = Path.Combine(_dir, "in.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(archive.CreateEntry("readme.txt").Open()))
            {
                w.Write("hi");
            }

            using (var w = new StreamWriter(archive.CreateEntry("x.sqlite").Open()))
            {
                w.Write("db");
            }
        }

        Assert.Equal(FileOutcome.DatabaseProtected, files.Unzip("in.zip", "out", overwrite: false).Outcome);
        Assert.False(Directory.Exists(Path.Combine(_dir, "out")));

        // The guard off (the SQLite tools off): all of it goes.
        var open = Guarded(on: false);
        Assert.Equal(FileOutcome.Ok, open.WriteText("a.db", "x", overwrite: true).Outcome);
        Assert.Equal(FileOutcome.Ok, open.Delete("data").Outcome);
        Assert.Equal(FileOutcome.Ok, new WorkingDirectory(() => _dir, _time).Delete("a.db").Outcome);
    }
}
