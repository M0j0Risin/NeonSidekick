using Microsoft.Extensions.AI;
using NeonSidekick.Diagnostics;
using NeonSidekick.Sessions;
using NeonSidekick.Tests.Fakes;

namespace NeonSidekick.Tests;

public class SessionStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NeonSidekick.Tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly SessionStore _store;

    public SessionStoreTests()
    {
        _store = new SessionStore(_dir, _time);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private long Begin(string title = "How do I wire the Vosk model?", string model = "llama")
    {
        long? id = _store.Begin(title, model);
        Assert.NotNull(id);
        return id.Value;
    }

    [Fact]
    public void Begin_CreatesTheFile_AndTheRow_WithTheFirstLineAsItsTitle()
    {
        long id = Begin();

        Assert.True(File.Exists(Path.Combine(_dir, SessionStore.FileName)));
        Assert.True(_store.Available);
        Assert.Equal(1, _store.Count);
        var summary = Assert.Single(_store.List(0));
        Assert.Equal(id, summary.Id);
        Assert.Equal("How do I wire the Vosk model?", summary.Title);
        Assert.Equal(TitleSource.FirstLine, summary.TitleSource);
        Assert.Equal("llama", summary.Model);
        Assert.Equal(0, summary.Turns);
        Assert.Equal(_time.GetUtcNow(), summary.StartedAt);
        Assert.Equal(_time.GetUtcNow(), summary.UpdatedAt);
        // The light read for the rule above the input row (2026-09-18): the same row, no turns loaded; null for a missing id.
        Assert.Equal(summary, _store.Summary(id));
        Assert.Null(_store.Summary(id + 1));
        Assert.True(_store.SetTitle(id, "vosk-model-wiring", TitleSource.Model));
        Assert.Equal(("vosk-model-wiring", TitleSource.Model), _store.Summary(id) is { } titled ? (titled.Title, titled.TitleSource) : default);
    }

    [Fact]
    public void AppendTurn_NumbersTheTurns_BumpsTheCount_AndMovesUpdatedAt()
    {
        long id = Begin();
        _store.AppendTurn(id, "hello", "Hello there.", 0, [], [], 0, 10, 5, false);
        _time.Advance(TimeSpan.FromMinutes(3));
        _store.AppendTurn(id, "and then", "And then.", 2, [], [], 0, 20, 8, true);

        var record = _store.Load(id);
        Assert.NotNull(record);
        Assert.Equal(2, record.Summary.Turns);
        Assert.Equal(_time.GetUtcNow(), record.Summary.UpdatedAt);
        Assert.Equal(new[] { 1, 2 }, record.Turns.Select(t => t.Ordinal));
        Assert.Equal("hello", record.Turns[0].UserText);
        Assert.Equal("Hello there.", record.Turns[0].ReplyText);
        Assert.Equal(0, record.Turns[0].ToolCalls);
        Assert.False(record.Turns[0].Cancelled);
        Assert.Equal(2, record.Turns[1].ToolCalls);
        Assert.Equal(20, record.Turns[1].InputTokens);
        Assert.Equal(8, record.Turns[1].OutputTokens);
        Assert.True(record.Turns[1].Cancelled);
        Assert.Equal(_time.GetUtcNow(), record.Turns[1].At);
        Assert.Equal("", record.HistoryJson);   // nothing saved yet
    }

    [Fact]
    public void SaveHistory_ReplacesTheDocument()
    {
        long id = Begin();
        string first = SessionHistory.ToJson([new ChatMessage(ChatRole.User, "hello")]);
        string second = SessionHistory.ToJson([new ChatMessage(ChatRole.User, "hello"), new ChatMessage(ChatRole.Assistant, "hi")]);
        _store.SaveHistory(id, first);
        _store.SaveHistory(id, second);

        var record = _store.Load(id)!;
        Assert.Equal(second, record.HistoryJson);
        Assert.Equal(2, SessionHistory.FromJson(record.HistoryJson).Count);
    }

    [Fact]
    public void List_IsNewestUpdatedFirst_AndTheMaxCuts()
    {
        long a = Begin("a");
        _time.Advance(TimeSpan.FromMinutes(1));
        long b = Begin("b");
        _time.Advance(TimeSpan.FromMinutes(1));
        long c = Begin("c");
        _time.Advance(TimeSpan.FromMinutes(1));
        _store.AppendTurn(a, "x", "y", 0, [], [], 0, 0, 0, false);   // a is the newest again

        Assert.Equal(new[] { a, c, b }, _store.List(0).Select(s => s.Id));
        Assert.Equal(new[] { a, c }, _store.List(2).Select(s => s.Id));
    }

    [Fact]
    public void Load_OfAMissingId_IsNull()
    {
        Assert.Null(_store.Load(42));
        Assert.Equal(0, _store.Count);
        Assert.Empty(_store.List(0));
    }

    [Fact]
    public void SetTitle_TheModelsLandsOnAFirstLineTitleOnly_TheUsersAlways()
    {
        long id = Begin("first line");
        Assert.True(_store.SetTitle(id, "Vosk wiring", TitleSource.Model));
        Assert.Equal(("Vosk wiring", TitleSource.Model), (_store.Load(id)!.Summary.Title, _store.Load(id)!.Summary.TitleSource));
        Assert.False(_store.SetTitle(id, "Later answer", TitleSource.Model));   // no longer the first line
        Assert.Equal("Vosk wiring", _store.Load(id)!.Summary.Title);
        Assert.True(_store.SetTitle(id, "My notes", TitleSource.User));
        Assert.Equal(("My notes", TitleSource.User), (_store.Load(id)!.Summary.Title, _store.Load(id)!.Summary.TitleSource));
        Assert.False(_store.SetTitle(id, "Overwrite", TitleSource.Model));   // never over the user's
        Assert.Equal("My notes", _store.Load(id)!.Summary.Title);
        Assert.False(_store.SetTitle(999, "x", TitleSource.User));
    }

    [Fact]
    public void Search_FindsTheSessionsWhoseTurnsHoldEveryWord_BestTurnPerSession_WithASnippet()
    {
        long a = Begin("a");
        _store.AppendTurn(a, "How do I wire the Vosk model into the wake word?", "You pass the model's folder to the detector.", 0, [], [], 0, 0, 0, false);
        _store.AppendTurn(a, "and the vosk grammar?", "The grammar is a JSON list of phrases the Vosk recogniser accepts.", 1, [], [], 0, 0, 0, false);
        long b = Begin("b");
        _store.AppendTurn(b, "What is for dinner?", "Pasta, since the pantry has nothing else.", 0, [], [], 0, 0, 0, false);
        long c = Begin("c");
        _store.AppendTurn(c, "vosk again", "The Vosk model again.", 0, [], [], 0, 0, 0, false);

        var hits = _store.Search("vosk", 10);
        Assert.Equal(2, hits.Count);
        Assert.All(hits, hit => Assert.Contains(hit.Session.Id, new[] { a, c }));
        var forA = Assert.Single(hits, hit => hit.Session.Id == a);
        Assert.InRange(forA.Turn, 1, 2);
        Assert.Contains("Vosk", forA.Snippet, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("a", forA.Session.Title);
        // Every word must occur: "vosk grammar" is turn 2 of a alone.
        var grammar = Assert.Single(_store.Search("vosk grammar", 10));
        Assert.Equal((a, 2), (grammar.Session.Id, grammar.Turn));
        // The max cuts sessions, not turns; the excluded session is left out.
        Assert.Single(_store.Search("vosk", 1));
        Assert.Equal(c, Assert.Single(_store.Search("vosk", 10, exclude: a)).Session.Id);
        Assert.Empty(_store.Search("nothing-here", 10));
        Assert.Empty(_store.Search("   ", 10));
        Assert.Empty(_store.Search("vosk", 0));
    }

    [Fact]
    public void Search_NeverTripsOnFts5Syntax()
    {
        long a = Begin("a");
        _store.AppendTurn(a, "a \"quoted\" NOT question*", "reply", 0, [], [], 0, 0, 0, false);

        Assert.Single(_store.Search("\"quoted\"", 10));
        Assert.Single(_store.Search("question*", 10));   // the star is a character, not a prefix operator
        Assert.Empty(_store.Search("quest*", 10));
        Assert.Single(_store.Search("NOT", 10));
        Assert.NotNull(_store.Search("- ( ) OR", 10));   // operators alone: no throw, whatever it matches
    }

    [Theory]
    [InlineData("vosk model", "\"vosk\" \"model\"")]
    [InlineData("  spaced   out ", "\"spaced\" \"out\"")]
    [InlineData("say \"hi\"", "\"say\" \"hi\"")]
    [InlineData("it's", "\"it's\"")]
    [InlineData("a\"b", "\"a\"\"b\"")]
    [InlineData("\"\"", "")]
    [InlineData("", "")]
    public void FtsQuery_IsPinned(string text, string expected) => Assert.Equal(expected, SessionStore.FtsQuery(text));

    [Fact]
    public void Purge_RemovesOneSession_AndItsTurns_TheOthersStay()
    {
        long a = Begin("a");
        _store.AppendTurn(a, "vosk", "one", 0, [], [], 0, 0, 0, false);
        long b = Begin("b");
        _store.AppendTurn(b, "vosk", "two", 0, [], [], 0, 0, 0, false);

        Assert.True(_store.Purge(a));
        Assert.False(_store.Purge(a));
        Assert.Null(_store.Load(a));
        Assert.Equal(1, _store.Count);
        Assert.Equal(b, Assert.Single(_store.Search("vosk", 10)).Session.Id);   // the index followed
    }

    [Fact]
    public void PurgeOlderThan_GoesByTheLastTurn_AndPurgeAll_TakesEverything()
    {
        long old = Begin("old");
        _store.AppendTurn(old, "x", "y", 0, [], [], 0, 0, 0, false);
        _time.Advance(TimeSpan.FromDays(10));
        long recent = Begin("recent");
        _store.AppendTurn(recent, "x", "y", 0, [], [], 0, 0, 0, false);
        long touched = Begin("touched");
        _time.Advance(TimeSpan.FromDays(1));

        Assert.Equal(1, _store.PurgeOlderThan(_time.GetUtcNow().AddDays(-5)));
        Assert.Equal(new[] { touched, recent }, _store.List(0).Select(s => s.Id));
        Assert.Equal(0, _store.PurgeOlderThan(_time.GetUtcNow().AddDays(-5)));
        Assert.Equal(2, _store.PurgeAll());
        Assert.Equal(0, _store.Count);
        Assert.Empty(_store.Search("x", 10));
        Assert.Equal(0, _store.PurgeAll());
    }

    [Fact]
    public void AFileThatIsNotADatabase_IsOneWarning_AndAnEmptyStoreThatIgnoresWrites()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, SessionStore.FileName), "not a database at all, and long enough to have a header the library reads");
        var warnings = new List<DiagnosticEvent>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == "Sessions") { warnings.Add(e); } };
        DiagnosticLog.Emitted += capture;
        try
        {
            using var store = new SessionStore(_dir, _time);
            Assert.Null(store.Begin("x", "m"));
            Assert.False(store.Available);
            store.AppendTurn(1, "x", "y", 0, [], [], 0, 0, 0, false);
            store.SaveHistory(1, "{}");
            Assert.Empty(store.List(0));
            Assert.Null(store.Load(1));
            Assert.Empty(store.Search("x", 5));
            Assert.Equal(0, store.Count);
            Assert.False(store.Purge(1));
            Assert.Equal(0, store.PurgeAll());
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        var warning = Assert.Single(warnings);
        Assert.Equal(DiagnosticLevel.Warning, warning.Level);
        Assert.StartsWith("Could not open sessions.db; sessions are off: ", warning.Message);
    }

    [Theory]
    [InlineData(99)]
    [InlineData(2)]   // an older file is refused as well since the migrations went (2026-09-24)
    public void AnotherSchema_IsRefused_AsUnavailable_WithOneWarning(long stored)
    {
        long id = Begin();
        _store.Dispose();
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(_dir, SessionStore.FileName), Pooling = false }.ToString()))
        {
            connection.Open();
            using var bump = connection.CreateCommand();
            bump.CommandText = "UPDATE meta SET value = $v WHERE key = 'schema'";
            bump.Parameters.AddWithValue("$v", stored);
            bump.ExecuteNonQuery();
        }

        var warnings = new List<DiagnosticEvent>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == "Sessions" && e.Level >= DiagnosticLevel.Warning) { warnings.Add(e); } };
        DiagnosticLog.Emitted += capture;
        try
        {
            using var other = new SessionStore(_dir, _time);
            Assert.False(other.Available);
            Assert.Null(other.Load(id));
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        Assert.Equal(SessionStore.SchemaMismatchWarning(stored), Assert.Single(warnings).Message);
        Assert.Equal("sessions.db is at schema 2, this version reads 3; sessions are off until it is moved away.", SessionStore.SchemaMismatchWarning(2));
    }

    [Fact]
    public void AFreshStore_HasTheTelemetryColumns_NoSkillCallsColumn_AndSchemaThree()
    {
        Begin();
        Assert.False(_store.HasColumn("sessions", "skill_calls"));
        Assert.True(_store.HasColumn("sessions", "history_json"));
        Assert.True(_store.HasColumn("turns", "tool_names"));
        Assert.True(_store.HasColumn("reflections", "skill"));
        Assert.False(_store.HasColumn("nothing", "id"));
        Assert.Equal(3, _store.SchemaStored());
    }

    [Fact]
    public void AppendTurn_KeepsTheToolNames_TheSkillsLoaded_AndTheErrors_AndSkillUsageAddsThemUp()
    {
        // Schema 3 (2026-09-19): the reflection's telemetry per turn, and the per-skill sum the reflection's catalog carries.
        long a = Begin("first");
        _store.AppendTurn(a, "deploy", "done", 3, ["read_file", "edit_file"], ["docker-deploy"], 1, 10, 5, false);
        _store.AppendTurn(a, "again", "done", 1, ["read_file"], [], 0, 10, 5, false);
        _time.Advance(TimeSpan.FromMinutes(5));
        long b = Begin("second");
        _store.AppendTurn(b, "deploy again", "done", 2, ["edit_file"], ["docker-deploy", "git-push"], 0, 10, 5, false);

        var record = _store.Load(a);
        Assert.NotNull(record);
        Assert.Equal(["read_file", "edit_file"], record.Turns[0].ToolNames);
        Assert.Equal(["docker-deploy"], record.Turns[0].SkillsLoaded);
        Assert.Equal(1, record.Turns[0].Errors);
        Assert.Equal([], record.Turns[1].SkillsLoaded);

        var usage = _store.SkillUsageOf("docker-deploy");
        Assert.NotNull(usage);
        Assert.Equal((2, 2, 1), (usage.Turns, usage.Sessions, usage.WithErrors));
        Assert.Equal(_time.GetUtcNow(), usage.LastAt);
        Assert.Equal((1, 1, 0), _store.SkillUsageOf("git-push") is { } git ? (git.Turns, git.Sessions, git.WithErrors) : default);
        // A name that is a prefix or a suffix of another never matches (the cell is fenced by the separator; instr, not LIKE).
        Assert.Null(_store.SkillUsageOf("docker"));
        Assert.Null(_store.SkillUsageOf("deploy"));
        Assert.Null(_store.SkillUsageOf("git_push"));   // an underscore is a LIKE wildcard; instr reads it as itself
        Assert.Equal("read_file,edit_file", SessionStore.JoinNames(["read_file", "edit_file"]));
        Assert.Equal("", SessionStore.JoinNames([]));
        Assert.Equal(["a", "b"], SessionStore.SplitNames("a,b"));
        Assert.Empty(SessionStore.SplitNames(""));
    }

    [Fact]
    public void RecordReflection_KeepsTheRow_AndTheMarksReadTheNewestWrite()
    {
        long id = Begin();
        _store.AppendTurn(id, "deploy", "done", 4, ["read_file"], [], 0, 1, 1, false);
        var events = new List<DiagnosticEvent>();
        Action<DiagnosticEvent> capture = e => { if (e.Category == "Sessions") { events.Add(e); } };
        DiagnosticLog.Emitted += capture;
        try
        {
            Assert.Null(_store.LastReflectionWrite());
            _store.RecordReflection(new ReflectionRow(id, 1, false, ReflectionRow.NothingOutcome, "", "", 1, 100, 10));
            Assert.Null(_store.LastReflectionWrite());   // nothing written yet
            _time.Advance(TimeSpan.FromMinutes(1));
            _store.RecordReflection(new ReflectionRow(id, 1, false, ReflectionRow.Learned, "docker-deploy", ReflectionRow.Created, 2, 200, 20));
            _time.Advance(TimeSpan.FromMinutes(1));
            _store.RecordReflection(new ReflectionRow(null, 0, true, ReflectionRow.Learned, "docker-deploy", ReflectionRow.Updated, 3, 300, 30));   // a /learn sessions pass
            _time.Advance(TimeSpan.FromMinutes(1));
            _store.RecordReflection(new ReflectionRow(id, 1, true, ReflectionRow.Learned, "git-push", ReflectionRow.Created, 1, 50, 5));
        }
        finally
        {
            DiagnosticLog.Emitted -= capture;
        }

        var newest = _store.LastReflectionWrite();
        Assert.NotNull(newest);
        Assert.Equal(("git-push", ReflectionRow.Created, _time.GetUtcNow()), (newest.Skill, newest.Action, newest.At));
        var docker = _store.LastReflectionOf("docker-deploy");
        Assert.NotNull(docker);
        Assert.Equal((ReflectionRow.Updated, _time.GetUtcNow() - TimeSpan.FromMinutes(1)), (docker.Action, docker.At));
        Assert.Equal(2, _store.ReflectionWrites("docker-deploy"));
        Assert.Equal(0, _store.ReflectionWrites("nothing"));
        Assert.Null(_store.LastReflectionOf("nothing"));
        Assert.Equal([
            "Reflection recorded: nothing on session 1 turn 1",
            "Reflection recorded: learned docker-deploy (created) on session 1 turn 1",
            "Reflection recorded: learned docker-deploy (updated) on a /learn sessions pass",
            "Reflection recorded: learned git-push (created) on session 1 turn 1",
        ], events.Where(e => e.Level == DiagnosticLevel.Info).Select(e => e.Message));

        // A purge takes the session's reflections with it; the pass's row (no session) goes with an older-than purge past its time, or purge all.
        Assert.True(_store.Purge(id));
        var pass = _store.LastReflectionWrite();
        Assert.NotNull(pass);
        Assert.Equal(("docker-deploy", ReflectionRow.Updated), (pass.Skill, pass.Action));
        Assert.Equal(0, _store.PurgeOlderThan(_time.GetUtcNow() - TimeSpan.FromMinutes(5)));
        Assert.NotNull(_store.LastReflectionWrite());
        _store.PurgeOlderThan(_time.GetUtcNow());
        Assert.Null(_store.LastReflectionWrite());
    }

    [Fact]
    public void SearchAny_FindsASessionSharingAnyWord_TheMostSharedFirst()
    {
        long a = Begin("a");
        _store.AppendTurn(a, "How do I wire the Vosk model into the wake word?", "You pass the model's folder to the detector.", 0, [], [], 0, 0, 0, false);
        long b = Begin("b");
        _store.AppendTurn(b, "What is for dinner?", "Pasta.", 0, [], [], 0, 0, 0, false);
        long c = Begin("c");
        _store.AppendTurn(c, "the wake word again", "The wake word.", 0, [], [], 0, 0, 0, false);

        var hits = _store.SearchAny(["vosk", "wake"], 5);
        Assert.Equal([a, c], hits.Select(h => h.Session.Id));   // a shares both words
        Assert.Equal([c], _store.SearchAny(["vosk", "wake"], 5, exclude: a).Select(h => h.Session.Id));
        Assert.Empty(_store.SearchAny(["dinner"], 5, exclude: b));
        Assert.Empty(_store.SearchAny([], 5));
        Assert.Equal("\"vosk\" OR \"wake\"", SessionStore.FtsQueryAny(["vosk", "wake"]));
        Assert.Equal("\"a\"\"b\"", SessionStore.FtsQueryAny(["a\"b"]));
        Assert.Equal("", SessionStore.FtsQueryAny(["", " "]));
    }

    [Fact]
    public void AFreshStoreOverTheSameFile_ReadsWhatTheOldOneWrote()
    {
        long id = Begin("kept");
        _store.AppendTurn(id, "hello", "hi", 0, [], [], 0, 1, 1, false);
        _store.Dispose();

        using var again = new SessionStore(_dir, _time);
        var record = again.Load(id);
        Assert.NotNull(record);
        Assert.Equal("kept", record.Summary.Title);
        Assert.Equal("hello", Assert.Single(record.Turns).UserText);
    }

    [Fact]
    public void Dispose_ClosesTheFile_SoTheFolderCanGo()
    {
        Begin();
        _store.Dispose();
        Directory.Delete(_dir, recursive: true);
        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public void Words_AndStamps_ArePinned()
    {
        Assert.Equal("first-line", SessionStore.Word(TitleSource.FirstLine));
        Assert.Equal("model", SessionStore.Word(TitleSource.Model));
        Assert.Equal("user", SessionStore.Word(TitleSource.User));
        Assert.Equal("2026-09-11T21:05:30.0000000Z", SessionStore.Stamp(_time.GetUtcNow()));
        Assert.Equal("sessions.db", SessionStore.FileName);
        Assert.Equal(3, SessionStore.SchemaVersion);   // 1 until later on 2026-09-18, 2 until 2026-09-19
        Assert.Equal(Path.Combine(Path.GetFullPath(_dir), "sessions.db"), _store.FilePath);
    }
    [Fact]
    public void SessionLogLines_ArePinned()
    {
        Assert.Equal("Session 12 begun: \"first line\"", SessionStore.BegunLogLine(12, "first line\nmore"));
        Assert.Equal("Session 12: turn appended", SessionStore.AppendedLogLine(12, false));
        Assert.Equal("Session 12: turn appended (cancelled)", SessionStore.AppendedLogLine(12, true));
        Assert.Equal("Session 12: history saved (24,000 chars)", SessionStore.HistorySavedLogLine(12, 24000));
        Assert.Equal("Session 12 titled by the model: \"a-slug\"", SessionStore.TitledLogLine(12, "a-slug", TitleSource.Model));
        Assert.Equal("Session 12 titled by the user: \"My chat\"", SessionStore.TitledLogLine(12, "My chat", TitleSource.User));
        Assert.Equal("Purged session 12", SessionStore.PurgedLogLine(12));
        Assert.Equal("Purged 4 sessions last updated before 2026-08-20 21:05:00 UTC", SessionStore.PurgedOlderLogLine(4, new DateTimeOffset(2026, 8, 20, 14, 5, 0, TimeSpan.FromHours(-7))));
        Assert.Equal("Purged all sessions (9)", SessionStore.PurgedAllLogLine(9));
    }
}
