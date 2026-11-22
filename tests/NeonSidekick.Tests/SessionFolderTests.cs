using Microsoft.Data.Sqlite;
using NeonSidekick.Diagnostics;
using NeonSidekick.Files;
using NeonSidekick.Llm;
using NeonSidekick.Sessions;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

/// <summary>
/// The store's added columns (2026-11-21, feature ideas #4 and #7): a session's folder (its last turn's), and each turn's model and timed
/// output; added to a file from before at the open with the schema left at 3, and best effort, so sessions never go off over them.
/// </summary>
public sealed class SessionFolderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string FilePath => Path.Combine(_dir, SessionStore.FolderName, SessionStore.FileName);

    private void Raw(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = FilePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private object? RawScalar(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = FilePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    /// <summary>A file as a build from before wrote it: the four columns dropped again.</summary>
    private long OlderFile()
    {
        long id;
        using (var store = new SessionStore(_dir, _time))
        {
            id = store.Begin("older", "llama")!.Value;
            store.AppendTurn(id, "hi", "hello", 0, [], [], 0, 1, 2, false);
        }

        Raw("ALTER TABLE sessions DROP COLUMN cwd; ALTER TABLE turns DROP COLUMN model; ALTER TABLE turns DROP COLUMN output_ms; ALTER TABLE turns DROP COLUMN timed_output;");
        return id;
    }

    [Fact]
    public void AFileFromBefore_GainsTheColumns_AndKeepsSchemaThree_ItsSessionsHavingNoFolder()
    {
        long id = OlderFile();
        using var store = new SessionStore(_dir, _time);

        var old = Assert.Single(store.List(0));
        Assert.Null(old.Cwd);
        Assert.Equal(SessionStore.SchemaVersion, store.SchemaStored());
        Assert.Equal(3, SessionStore.SchemaVersion);
        foreach (var (table, column) in new[] { ("sessions", "cwd"), ("turns", "model"), ("turns", "output_ms"), ("turns", "timed_output") })
        {
            Assert.True(store.HasColumn(table, column), column);
        }

        Assert.Equal(2, store.AppendTurn(id, "again", "yes", 0, [], [], 0, 1, 2, false, cwd: _dir));
        Assert.Equal(_dir, store.Summary(id)!.Cwd);
        // An older build's INSERT, naming only its own columns, still works on the file (the updater's rollback).
        Raw("INSERT INTO sessions(started_at, updated_at, title, title_source, model, turns, history_json) VALUES ('2026-01-01T00:00:00.0000000+00:00', '2026-01-01T00:00:00.0000000+00:00', 'by an older copy', 'first-line', 'm', 0, '')");
        Assert.Contains(store.List(0), s => s.Title == "by an older copy" && s.Cwd is null);
    }

    [Fact]
    public void TheFolder_IsTheLastTurns_AndEachTurnRecordsItsModelAndTimedOutput()
    {
        string first = Path.Combine(_dir, "a");
        string second = Path.Combine(_dir, "b");
        long id;
        using (var store = new SessionStore(_dir, _time))
        {
            id = store.Begin("t", "llama", first)!.Value;
            Assert.Equal(first, store.Summary(id)!.Cwd);
            var timed = new TokenUsage(Input: 10, Output: 120, Total: 130, Requests: 1, ToFirstToken: TimeSpan.FromSeconds(1), Generating: TimeSpan.FromSeconds(2), UntimedOutput: 20);
            store.AppendTurn(id, "one", "r", 0, [], [], 0, 10, 120, false, model: "qwen3", usage: timed, cwd: first);
            store.AppendTurn(id, "two", "r", 0, [], [], 0, 10, 5, false, model: "/claude", usage: new TokenUsage(1, 5, 6, 1, TimeSpan.Zero, TimeSpan.Zero), cwd: second);
            store.AppendTurn(id, "three", "r", 0, [], [], 0, 0, 0, false);   // no folder: the session keeps the last one
            Assert.Equal(second, store.Summary(id)!.Cwd);
            Assert.Equal(second, store.Load(id)!.Summary.Cwd);
        }

        Assert.Equal("qwen3", RawScalar("SELECT model FROM turns WHERE ordinal = 1"));
        Assert.Equal(2000L, RawScalar("SELECT output_ms FROM turns WHERE ordinal = 1"));
        Assert.Equal(100L, RawScalar("SELECT timed_output FROM turns WHERE ordinal = 1"));   // the output the time covers
        Assert.Equal("/claude", RawScalar("SELECT model FROM turns WHERE ordinal = 2"));
        Assert.Equal(DBNull.Value, RawScalar("SELECT output_ms FROM turns WHERE ordinal = 2"));   // untimed: no speed
        Assert.Equal(DBNull.Value, RawScalar("SELECT model FROM turns WHERE ordinal = 3"));
    }

    [Fact]
    public void AColumnAddThatFails_LeavesSessionsWorking_WithoutTheFolder_AndSaysSo()
    {
        OlderFile();
        var errors = new List<DiagnosticEvent>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == "Sessions" && e.Level == DiagnosticLevel.Error) { errors.Add(e); } };
        DiagnosticLog.Emitted += capture;
        try
        {
            // Another copy holding the write lock past the busy wait, as the hook stands for it.
            using var store = new SessionStore(_dir, _time) { BeforeColumnAdds = () => throw new SqliteException("database is locked", 5) };
            Assert.True(store.Available);
            long id = store.Begin("new", "m", _dir)!.Value;
            Assert.Equal(1, store.AppendTurn(id, "x", "y", 0, [], [], 0, 0, 0, false, model: "m", cwd: _dir));
            Assert.Null(store.Summary(id)!.Cwd);
            Assert.Equal(2, store.List(0).Count);
            Assert.False(store.HasColumn("sessions", "cwd"));
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        Assert.StartsWith("Could not add the folder and model columns in sessions.db: ", Assert.Single(errors).Message);
    }

    [Theory]
    [InlineData(@"C:\Repo\X", @"c:\repo\x\", true)]
    [InlineData(@"C:\Repo\X", @"C:\Repo\X\..\X", true)]
    [InlineData(@"C:\Repo\X", @"C:\Repo\Y", false)]
    [InlineData(null, @"C:\Repo\X", false)]
    [InlineData("", "", false)]
    public void FolderMatch_ComparesFullPaths_IgnoringCaseOnWindows(string? a, string? b, bool expected)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;   // the twin below covers the Unix side
        }

        Assert.Equal(expected, FolderMatch.Same(a, b));
    }

    [UnixFact]
    public void FolderMatch_IsExactOnUnix()
    {
        Assert.True(FolderMatch.Same("/Users/x/Repo", "/Users/x/Repo/"));
        Assert.False(FolderMatch.Same("/Users/x/Repo", "/users/x/repo"));
    }
}
