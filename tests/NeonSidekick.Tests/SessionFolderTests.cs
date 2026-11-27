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

        Raw("ALTER TABLE sessions DROP COLUMN cwd; ALTER TABLE turns DROP COLUMN model; ALTER TABLE turns DROP COLUMN output_ms; ALTER TABLE turns DROP COLUMN timed_output; ALTER TABLE turns DROP COLUMN cwd;");
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
        foreach (var (table, column) in new[] { ("sessions", "cwd"), ("turns", "model"), ("turns", "output_ms"), ("turns", "timed_output"), ("turns", "cwd"),
            ("turns", "context_tokens"), ("turns", "first_token_ms"), ("turns", "reasoning_tokens") })   // /usage's per-reply figures (2026-11-27)
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
    public void AColumnAddThatFails_LeavesSessionsWorking_WithoutTheFolder_AndSaysSoOnce_AsAWarning()
    {
        OlderFile();
        var errors = new List<DiagnosticEvent>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == "Sessions" && e.Level >= DiagnosticLevel.Warning) { errors.Add(e); } };
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

        var warned = Assert.Single(errors);   // once, though the busy file was tried twice (#102)
        Assert.Equal(DiagnosticLevel.Warning, warned.Level);
        Assert.StartsWith("Could not add the folder and model columns in sessions.db: ", warned.Message);
        Assert.StartsWith(SessionStore.ColumnsFailedWarning(""), warned.Message);
    }

    /// <summary>Another copy adding the columns between the check and the add (2026-11-22, the review's #105): its adds are as good as ours.</summary>
    [Fact]
    public void AnotherCopyAddingTheColumnsFirst_IsASuccess_NotAFailure()
    {
        OlderFile();
        var warnings = new List<DiagnosticEvent>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == "Sessions" && e.Level >= DiagnosticLevel.Warning) { warnings.Add(e); } };
        DiagnosticLog.Emitted += capture;
        try
        {
            bool raced = false;
            using var store = new SessionStore(_dir, _time)
            {
                BeforeColumnAdds = () =>
                {
                    if (raced)
                    {
                        return;
                    }

                    raced = true;
                    Raw("ALTER TABLE sessions ADD COLUMN cwd TEXT; ALTER TABLE turns ADD COLUMN model TEXT; ALTER TABLE turns ADD COLUMN output_ms INTEGER; ALTER TABLE turns ADD COLUMN timed_output INTEGER; ALTER TABLE turns ADD COLUMN cwd TEXT;");
                    throw new SqliteException("duplicate column name: cwd", 1);
                },
            };
            long id = store.Begin("new", "m", _dir)!.Value;
            Assert.Equal(_dir, store.Summary(id)!.Cwd);
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        Assert.Empty(warnings);
    }

    /// <summary>A rewind takes the folder back with the turns (2026-11-22, the review's #41): the last kept turn's.</summary>
    [Fact]
    public void ATruncate_TakesTheFolderBack_ToTheLastKeptTurns()
    {
        string first = Path.Combine(_dir, "a");
        string second = Path.Combine(_dir, "b");
        using var store = new SessionStore(_dir, _time);
        long id = store.Begin("t", "llama", first)!.Value;
        store.AppendTurn(id, "one", "r", 0, [], [], 0, 0, 0, false, cwd: first);
        store.AppendTurn(id, "two", "r", 0, [], [], 0, 0, 0, false, cwd: second);
        Assert.Equal(second, store.Summary(id)!.Cwd);
        Assert.Equal(second, RawScalar("SELECT cwd FROM turns WHERE ordinal = 2"));

        store.TruncateTurns(id, 1);

        Assert.Equal(first, store.Summary(id)!.Cwd);
    }

    /// <summary>A session whose turns were saved before they kept a folder keeps its own through a truncate (#41).</summary>
    [Fact]
    public void ATruncate_OntoTurnsWithoutAFolder_KeepsTheSessions()
    {
        string folder = Path.Combine(_dir, "a");
        using var store = new SessionStore(_dir, _time);
        long id = store.Begin("t", "llama", folder)!.Value;
        store.AppendTurn(id, "one", "r", 0, [], [], 0, 0, 0, false);
        store.AppendTurn(id, "two", "r", 0, [], [], 0, 0, 0, false);

        store.TruncateTurns(id, 1);

        Assert.Equal(folder, store.Summary(id)!.Cwd);
        Assert.Equal(1, store.Summary(id)!.Turns);
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

    /// <summary>
    /// The per-reply figures (2026-11-27, /usage's trend and records): the context, the wait per request (the average, not the sum over a
    /// reply's requests) and the reasoning are written and read back; a store whose column adds failed restores with them null.
    /// </summary>
    [Fact]
    public void EachTurn_SavesItsContextFirstTokenAndReasoning_AndARestoreReadsThemBack()
    {
        using var store = new SessionStore(_dir, _time);
        long id = store.Begin("t", "m", _dir)!.Value;
        // Two requests waiting 1 s and 3 s: 2,000 ms a request.
        var usage = new NeonSidekick.Llm.TokenUsage(100, 40, 140, 2, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(2), Reasoning: 12);
        store.AppendTurn(id, "q", "r", 0, [], [], 0, 100, 40, false, model: "m", usage: usage, contextTokens: 2_048);
        store.AppendTurn(id, "q2", "r2", 0, [], [], 0, 0, 0, false, model: "m");

        Assert.Equal(2_048L, RawScalar("SELECT context_tokens FROM turns WHERE ordinal = 1"));
        Assert.Equal(2_000L, RawScalar("SELECT first_token_ms FROM turns WHERE ordinal = 1"));
        Assert.Equal(12L, RawScalar("SELECT reasoning_tokens FROM turns WHERE ordinal = 1"));
        var turns = store.Load(id)!.Turns;
        Assert.Equal((2_048L, 2_000L, 12L), (turns[0].ContextTokens, turns[0].FirstTokenMs, turns[0].ReasoningTokens));
        Assert.Equal((2_000L, 40L), (turns[0].OutputMs, turns[0].TimedOutput));
        Assert.Equal(((long?)null, (long?)null, (long?)null), (turns[1].ContextTokens, turns[1].FirstTokenMs, turns[1].ReasoningTokens));
    }

    [Fact]
    public void AStoreWhoseColumnAddsFailed_RestoresASession_WithTheFiguresNull()
    {
        OlderFile();
        Raw(DropReplyColumns);   // every added column missing (OlderFile drops only the folder part)
        using var store = new SessionStore(_dir, _time) { BeforeColumnAdds = () => throw new SqliteException("database is locked", 5) };
        long id = store.Begin("new", "m", _dir)!.Value;
        store.AppendTurn(id, "x", "y", 0, [], [], 0, 10, 5, false, model: "m", contextTokens: 99);

        var turn = Assert.Single(store.Load(id)!.Turns);
        Assert.Equal(((long?)null, (long?)null, (string?)null), (turn.ContextTokens, turn.FirstTokenMs, turn.Model));
        Assert.Same(ModelBests.None, store.ModelBests("m"));   // no all-time columns without the figures
    }

    private const string DropReplyColumns = "ALTER TABLE turns DROP COLUMN context_tokens; ALTER TABLE turns DROP COLUMN first_token_ms; ALTER TABLE turns DROP COLUMN reasoning_tokens;";

    /// <summary>
    /// A file with the folder columns but not the reply figures, whose adds fail busy (2026-11-27, the last-3 review's #43): each turn's model
    /// and folder are still saved, the figures left out, and the warning names the reply figures, not the folder.
    /// </summary>
    [Fact]
    public void AFailedAddOfTheReplyColumns_StillSavesTheModelAndFolder()
    {
        using (var setup = new SessionStore(_dir, _time))
        {
            setup.Begin("older", "llama");
        }

        Raw(DropReplyColumns);
        var warnings = new List<DiagnosticEvent>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == "Sessions" && e.Level >= DiagnosticLevel.Warning) { warnings.Add(e); } };
        DiagnosticLog.Emitted += capture;
        try
        {
            using var store = new SessionStore(_dir, _time) { BeforeColumnAdds = () => throw new SqliteException("database is locked", 5) };
            long id = store.Begin("new", "m", _dir)!.Value;
            var usage = new TokenUsage(100, 40, 140, 1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
            Assert.Equal(1, store.AppendTurn(id, "x", "y", 0, [], [], 0, 100, 40, false, model: "qwen", usage: usage, cwd: _dir, contextTokens: 99));

            var turn = Assert.Single(store.Load(id)!.Turns);
            Assert.Equal("qwen", turn.Model);
            Assert.Equal(2_000L, turn.OutputMs);
            Assert.Equal(((long?)null, (long?)null), (turn.ContextTokens, turn.FirstTokenMs));
            Assert.Equal(_dir, store.Summary(id)!.Cwd);
            Assert.Same(ModelBests.None, store.ModelBests("qwen"));   // the records need both parts
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        var warned = Assert.Single(warnings);
        Assert.StartsWith(SessionStore.ReplyColumnsFailedWarning(""), warned.Message);
        Assert.StartsWith("Could not add the reply figure columns in sessions.db: ", warned.Message);
    }
}
