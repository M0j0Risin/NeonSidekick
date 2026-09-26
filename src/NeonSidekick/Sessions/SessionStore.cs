using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Sessions;

/// <summary>Who wrote a session's title: the first sent line, the model (<c>Session naming mode</c> = <c>model-written</c>) or the user (<c>/sessions title</c>, the pane's rename).</summary>
public enum TitleSource
{
    FirstLine,
    Model,
    User,
}

/// <summary>One session row without its turns: the <c>/sessions</c> list's view and a hit's header.</summary>
public sealed record SessionSummary(long Id, DateTimeOffset StartedAt, DateTimeOffset UpdatedAt, string Title, TitleSource TitleSource, string Model, int Turns);

/// <summary>
/// One completed turn as the transcript showed it: the user's text (pastes expanded), the reply's
/// raw text, how many tools the model called, and — since schema 3 (2026-09-19), the reflection's
/// telemetry — the distinct tool names in first-call order, the skills it loaded and how many
/// calls answered with an error; then the request's tokens and whether the keys cut it short.
/// The replay never shows the names (one <c>🛠️ N tool calls</c> line per turn as ever).
/// </summary>
public sealed record SessionTurn(int Ordinal, DateTimeOffset At, string UserText, string ReplyText, int ToolCalls, IReadOnlyList<string> ToolNames, IReadOnlyList<string> SkillsLoaded, int Errors, long InputTokens, long OutputTokens, bool Cancelled);

/// <summary>A whole session: the row, its turns in order and the stored history (<see cref="SessionHistory"/>).</summary>
public sealed record SessionRecord(SessionSummary Summary, IReadOnlyList<SessionTurn> Turns, string HistoryJson);

/// <summary>One search hit: the session, the best-ranked turn in it and the FTS5 snippet around the match.</summary>
public sealed record SessionHit(SessionSummary Session, int Turn, string Snippet);

/// <summary>How the stored turns used one skill (schema 3, 2026-09-19): the turns that loaded it, across how many sessions, how many of those turns hit a tool error, and the last time.</summary>
public sealed record SkillUsage(int Turns, int Sessions, int WithErrors, DateTimeOffset LastAt);

/// <summary>One reflection that wrote a skill, as the <c>reflections</c> table remembers it: when, which skill, <c>created</c> or <c>updated</c>.</summary>
public sealed record ReflectionMark(DateTimeOffset At, string Skill, string Action);

/// <summary>
/// One reflection's row for the <c>reflections</c> table (schema 3, 2026-09-19): the session and
/// turn it reflected on (null and 0 for a <c>/learn sessions</c> pass), whether the user asked for
/// it, its outcome word (<see cref="Learned"/>, <see cref="NothingOutcome"/>, <see cref="Exhausted"/>,
/// <see cref="Failed"/>), the skill and the action when it wrote one, and what it cost.
/// </summary>
public sealed record ReflectionRow(long? SessionId, int TurnOrdinal, bool Forced, string Outcome, string Skill, string Action, int Requests, long InputTokens, long OutputTokens)
{
    public const string Learned = "learned";
    public const string NothingOutcome = "nothing";
    public const string Exhausted = "exhausted";
    public const string Failed = "failed";
    public const string Created = "created";
    public const string Updated = "updated";
}

/// <summary>
/// The per-profile session store: <c>sessions.db</c> beside <c>memory.json</c>, one SQLite file
/// through <c>Microsoft.Data.Sqlite</c> with raw SQL (no ORM: reflection is off under NativeAOT).
/// A <c>sessions</c> row per conversation carrying its whole history as JSON — rewritten after
/// every turn and every compact, since a compact rewrites older turns and no per-turn slice could
/// be rebuilt — and a <c>turns</c> row per completed turn with the text the transcript showed,
/// indexed by an FTS5 table (<c>unicode61</c>) for <c>session_manager</c>'s search.
///
/// <para>One connection, opened lazily under one lock, WAL journal. <b>The store never takes down
/// a turn</b>: a file that will not open is logged once as a Warning under <c>Sessions</c> and the
/// store answers empty and ignores writes (<see cref="Available"/>); a statement that fails later
/// is logged and swallowed, the failed write lost. Timestamps are ISO-8601 UTC text, which sorts
/// and compares as text. Every write is synchronous on the caller's thread (a few ms), reads for
/// the tool run under <c>Task.Run</c> at the tool.</para>
/// </summary>
public sealed class SessionStore : IDisposable
{
    public const string FileName = "sessions.db";

    /// <summary>
    /// The store's own schema number, kept in the <c>meta</c> table. A file at any other number
    /// is refused as unavailable (<see cref="SchemaMismatchWarning"/>). 3 since 2026-09-19: the
    /// <c>turns</c> rows carry <c>tool_names</c>, <c>skills_loaded</c> and <c>errors</c> and a
    /// <c>reflections</c> table records every reflection. The 1 → 2 → 3 migrations went on
    /// 2026-09-24 (the user's call): every real file had long been at 3, and a schema that lived a
    /// day was not worth the code, the fixtures and a smoke leg. The <c>command_history</c> table (2026-09-25,
    /// <c>Keep command history</c>) left it at 3: an added table is <c>IF NOT EXISTS</c> on every open, so a file
    /// from before gains it and an older build reads past it; no row it knew changed shape.
    /// </summary>
    public const int SchemaVersion = 3;

    /// <summary>Logged (Warn) when the file's stored schema is not <see cref="SchemaVersion"/>: sessions are off, the file untouched. Pinned.</summary>
    public static string SchemaMismatchWarning(long stored) =>
        $"{FileName} is at schema {stored.ToString(CultureInfo.InvariantCulture)}, this version reads {SchemaVersion.ToString(CultureInfo.InvariantCulture)}; sessions are off until it is moved away.";

    /// <summary>The separator between the names in a <c>tool_names</c> / <c>skills_loaded</c> cell; a tool or skill name never carries one.</summary>
    public const char NameSeparator = ',';

    /// <summary>Snippet width in tokens for a search hit, FTS5's unit.</summary>
    public const int SnippetTokens = 12;

    /// <summary>How many ranked turn hits a search reads before folding them into sessions.</summary>
    public const int SearchScan = 500;

    private const string Category = "Sessions";

    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly TimeProvider _time;
    private SqliteConnection? _connection;
    private bool _failed;
    private bool _disposed;

    /// <param name="directory">The profile directory; the file is <see cref="FileName"/> under it.</param>
    /// <param name="time">The clock behind every timestamp; tests pass a fake.</param>
    public SessionStore(string directory, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _filePath = Path.Combine(Path.GetFullPath(directory), FileName);
        _time = time ?? TimeProvider.System;
    }

    public string FilePath => _filePath;

    /// <summary>False once the file failed to open (or its schema is newer than <see cref="SchemaVersion"/>): every read is empty and every write ignored from then on.</summary>
    public bool Available
    {
        get
        {
            lock (_gate)
            {
                return Open() is not null;
            }
        }
    }

    /// <summary>How many sessions are stored.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return (int)Scalar("SELECT count(*) FROM sessions", 0L);
            }
        }
    }

    /// <summary>A new session row with no turns yet; null while unavailable.</summary>
    public long? Begin(string title, string model)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(model);
        lock (_gate)
        {
            if (Open() is not { } connection)
            {
                return null;
            }

            try
            {
                string now = Stamp(_time.GetUtcNow());
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO sessions(started_at, updated_at, title, title_source, model, turns, history_json) VALUES ($started, $updated, $title, $source, $model, 0, ''); SELECT last_insert_rowid();";
                command.Parameters.AddWithValue("$started", now);
                command.Parameters.AddWithValue("$updated", now);
                command.Parameters.AddWithValue("$title", title);
                command.Parameters.AddWithValue("$source", Word(TitleSource.FirstLine));
                command.Parameters.AddWithValue("$model", model);
                long id = (long)(command.ExecuteScalar() ?? 0L);
                DiagnosticLog.Debug(Category, BegunLogLine(id, title));
                return id;
            }
            catch (SqliteException ex)
            {
                Fail("begin a session", ex);
                return null;
            }
        }
    }

    /// <summary>Appends one completed turn to session <paramref name="id"/> and bumps its turn count and <c>updated_at</c>; the names go in joined by <see cref="NameSeparator"/>.</summary>
    public void AppendTurn(long id, string userText, string replyText, int toolCalls, IReadOnlyList<string> toolNames, IReadOnlyList<string> skillsLoaded, int errors, long inputTokens, long outputTokens, bool cancelled)
    {
        ArgumentNullException.ThrowIfNull(userText);
        ArgumentNullException.ThrowIfNull(replyText);
        ArgumentNullException.ThrowIfNull(toolNames);
        ArgumentNullException.ThrowIfNull(skillsLoaded);
        lock (_gate)
        {
            if (Open() is not { } connection)
            {
                return;
            }

            try
            {
                string now = Stamp(_time.GetUtcNow());
                using var transaction = connection.BeginTransaction();
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO turns(session_id, ordinal, at, user_text, reply_text, tool_calls, tool_names, skills_loaded, errors, input_tokens, output_tokens, cancelled) VALUES ($id, (SELECT turns + 1 FROM sessions WHERE id = $id), $at, $user, $reply, $tools, $names, $skills, $errors, $input, $output, $cancelled)";
                insert.Parameters.AddWithValue("$id", id);
                insert.Parameters.AddWithValue("$at", now);
                insert.Parameters.AddWithValue("$user", userText);
                insert.Parameters.AddWithValue("$reply", replyText);
                insert.Parameters.AddWithValue("$tools", toolCalls);
                insert.Parameters.AddWithValue("$names", JoinNames(toolNames));
                insert.Parameters.AddWithValue("$skills", JoinNames(skillsLoaded));
                insert.Parameters.AddWithValue("$errors", errors);
                insert.Parameters.AddWithValue("$input", inputTokens);
                insert.Parameters.AddWithValue("$output", outputTokens);
                insert.Parameters.AddWithValue("$cancelled", cancelled ? 1 : 0);
                insert.ExecuteNonQuery();
                using var bump = connection.CreateCommand();
                bump.Transaction = transaction;
                bump.CommandText = "UPDATE sessions SET turns = turns + 1, updated_at = $updated WHERE id = $id";
                bump.Parameters.AddWithValue("$id", id);
                bump.Parameters.AddWithValue("$updated", now);
                bump.ExecuteNonQuery();
                transaction.Commit();
                DiagnosticLog.Debug(Category, AppendedLogLine(id, cancelled));
            }
            catch (SqliteException ex)
            {
                Fail("append a turn", ex);
            }
        }
    }

    /// <summary>Replaces session <paramref name="id"/>'s stored history (<see cref="SessionHistory.ToJson"/>) — after every turn and every compact.</summary>
    public void SaveHistory(long id, string historyJson)
    {
        ArgumentNullException.ThrowIfNull(historyJson);
        lock (_gate)
        {
            if (Open() is not { } connection)
            {
                return;
            }

            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE sessions SET history_json = $history WHERE id = $id";
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$history", historyJson);
                command.ExecuteNonQuery();
                DiagnosticLog.Debug(Category, HistorySavedLogLine(id, historyJson.Length));
            }
            catch (SqliteException ex)
            {
                Fail("save the history", ex);
            }
        }
    }

    /// <summary>
    /// Renames session <paramref name="id"/>. A <see cref="TitleSource.Model"/> title lands only
    /// while the row still carries its first-line title (the model's answer never overwrites a name
    /// the user typed); the other two sources always land. False when nothing changed.
    /// </summary>
    public bool SetTitle(long id, string title, TitleSource source)
    {
        ArgumentNullException.ThrowIfNull(title);
        lock (_gate)
        {
            if (Open() is not { } connection)
            {
                return false;
            }

            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = source == TitleSource.Model
                    ? "UPDATE sessions SET title = $title, title_source = $source WHERE id = $id AND title_source = $first"
                    : "UPDATE sessions SET title = $title, title_source = $source WHERE id = $id";
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$title", title);
                command.Parameters.AddWithValue("$source", Word(source));
                command.Parameters.AddWithValue("$first", Word(TitleSource.FirstLine));
                bool renamed = command.ExecuteNonQuery() > 0;
                if (renamed)
                {
                    DiagnosticLog.Debug(Category, TitledLogLine(id, title, source));
                }

                return renamed;
            }
            catch (SqliteException ex)
            {
                Fail("rename a session", ex);
                return false;
            }
        }
    }

    /// <summary>The newest <paramref name="max"/> sessions by <c>updated_at</c>, newest first (every one for 0 or less).</summary>
    public IReadOnlyList<SessionSummary> List(int max)
    {
        lock (_gate)
        {
            if (Open() is not { } connection)
            {
                return [];
            }

            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT id, started_at, updated_at, title, title_source, model, turns FROM sessions ORDER BY updated_at DESC, id DESC" + (max > 0 ? " LIMIT $max" : "");
                command.Parameters.AddWithValue("$max", max);
                using var reader = command.ExecuteReader();
                var list = new List<SessionSummary>();
                while (reader.Read())
                {
                    list.Add(ReadSummary(reader));
                }

                return list;
            }
            catch (SqliteException ex)
            {
                Fail("list the sessions", ex);
                return [];
            }
        }
    }

    /// <summary>Session <paramref name="id"/>'s summary alone — the title and its source for the rule above the input row (2026-09-18); null when there is none. <see cref="Load"/> is the heavy read.</summary>
    public SessionSummary? Summary(long id)
    {
        lock (_gate)
        {
            if (Open() is not { } connection)
            {
                return null;
            }

            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT id, started_at, updated_at, title, title_source, model, turns FROM sessions WHERE id = $id";
                command.Parameters.AddWithValue("$id", id);
                using var reader = command.ExecuteReader();
                return reader.Read() ? ReadSummary(reader) : null;
            }
            catch (SqliteException ex)
            {
                Fail("read a session", ex);
                return null;
            }
        }
    }

    /// <summary>Session <paramref name="id"/> with its turns and history; null when there is none.</summary>
    public SessionRecord? Load(long id)
    {
        lock (_gate)
        {
            if (Open() is not { } connection)
            {
                return null;
            }

            try
            {
                SessionSummary summary;
                string history;
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT id, started_at, updated_at, title, title_source, model, turns, history_json FROM sessions WHERE id = $id";
                    command.Parameters.AddWithValue("$id", id);
                    using var reader = command.ExecuteReader();
                    if (!reader.Read())
                    {
                        return null;
                    }

                    summary = ReadSummary(reader);
                    history = reader.GetString(7);
                }

                var turns = new List<SessionTurn>(summary.Turns);
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT ordinal, at, user_text, reply_text, tool_calls, tool_names, skills_loaded, errors, input_tokens, output_tokens, cancelled FROM turns WHERE session_id = $id ORDER BY ordinal";
                    command.Parameters.AddWithValue("$id", id);
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        turns.Add(new SessionTurn(reader.GetInt32(0), Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.GetInt32(4), SplitNames(reader.GetString(5)), SplitNames(reader.GetString(6)), reader.GetInt32(7), reader.GetInt64(8), reader.GetInt64(9), reader.GetInt32(10) != 0));
                    }
                }

                return new SessionRecord(summary, turns, history);
            }
            catch (SqliteException ex)
            {
                Fail("load a session", ex);
                return null;
            }
        }
    }

    /// <summary>
    /// The best <paramref name="max"/> sessions matching <paramref name="query"/> (<see cref="FtsQuery"/>:
    /// every word must occur), each with its best-ranked turn and a snippet; session <paramref name="exclude"/>
    /// (the one on screen) left out. Empty for a blank query.
    /// </summary>
    public IReadOnlyList<SessionHit> Search(string query, int max, long? exclude = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        return SearchFts(FtsQuery(query), max, exclude);
    }

    /// <summary>
    /// The OR form (2026-09-19, the reflection's evidence): the best <paramref name="max"/> sessions
    /// where ANY of <paramref name="words"/> occurs (<see cref="FtsQueryAny"/>), ranked by bm25 so the
    /// sessions sharing the most words come first; otherwise <see cref="Search"/>'s shape.
    /// </summary>
    public IReadOnlyList<SessionHit> SearchAny(IReadOnlyList<string> words, int max, long? exclude = null)
    {
        ArgumentNullException.ThrowIfNull(words);
        return SearchFts(FtsQueryAny(words), max, exclude);
    }

    private IReadOnlyList<SessionHit> SearchFts(string fts, int max, long? exclude)
    {
        if (fts.Length == 0 || max <= 0)
        {
            return [];
        }

        lock (_gate)
        {
            if (Open() is not { } connection)
            {
                return [];
            }

            try
            {
                var best = new Dictionary<long, (int Turn, string Snippet)>();
                var order = new List<long>();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT t.session_id, t.ordinal, snippet(turns_fts, -1, '', '', '…', $tokens) FROM turns_fts JOIN turns t ON t.id = turns_fts.rowid WHERE turns_fts MATCH $query ORDER BY bm25(turns_fts) LIMIT $scan";
                    command.Parameters.AddWithValue("$query", fts);
                    command.Parameters.AddWithValue("$tokens", SnippetTokens);
                    command.Parameters.AddWithValue("$scan", SearchScan);
                    using var reader = command.ExecuteReader();
                    while (reader.Read() && order.Count < max)
                    {
                        long session = reader.GetInt64(0);
                        if (session == exclude || best.ContainsKey(session))
                        {
                            continue;
                        }

                        best[session] = (reader.GetInt32(1), reader.GetString(2));
                        order.Add(session);
                    }
                }

                var hits = new List<SessionHit>(order.Count);
                foreach (long session in order)
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = "SELECT id, started_at, updated_at, title, title_source, model, turns FROM sessions WHERE id = $id";
                    command.Parameters.AddWithValue("$id", session);
                    using var reader = command.ExecuteReader();
                    if (reader.Read())
                    {
                        hits.Add(new SessionHit(ReadSummary(reader), best[session].Turn, best[session].Snippet));
                    }
                }

                return hits;
            }
            catch (SqliteException ex)
            {
                Fail("search the sessions", ex);
                return [];
            }
        }
    }

    /// <summary>Removes session <paramref name="id"/> and its turns; false when there was none.</summary>
    public bool Purge(long id)
    {
        bool purged;
        lock (_gate)
        {
            purged = Delete("WHERE session_id = $id", "WHERE id = $id", ("$id", id)) > 0;
        }

        if (purged)
        {
            DiagnosticLog.Info(Category, PurgedLogLine(id));
        }

        return purged;
    }

    /// <summary>Removes every session last updated before <paramref name="cutoff"/> (and the reflection rows of a <c>/learn sessions</c> pass from before it, which belong to no session); how many sessions went.</summary>
    public int PurgeOlderThan(DateTimeOffset cutoff)
    {
        int purged;
        lock (_gate)
        {
            purged = Delete("WHERE session_id IN (SELECT id FROM sessions WHERE updated_at < $cutoff)", "WHERE updated_at < $cutoff", ("$cutoff", Stamp(cutoff)), "WHERE session_id IS NULL AND at < $cutoff");
        }

        if (purged > 0)
        {
            DiagnosticLog.Info(Category, PurgedOlderLogLine(purged, cutoff));
        }

        return purged;
    }

    /// <summary>Removes every session; how many went.</summary>
    public int PurgeAll()
    {
        int purged;
        lock (_gate)
        {
            purged = Delete("", "", null);
        }

        if (purged > 0)
        {
            DiagnosticLog.Info(Category, PurgedAllLogLine(purged));
        }

        return purged;
    }

    /// <summary>
    /// How the stored turns used skill <paramref name="name"/> (<c>skills_loaded</c>, schema 3): null
    /// when no turn loaded it, or while unavailable. <c>instr</c> over the comma-fenced cell — never
    /// <c>LIKE</c>, whose <c>_</c> is a wildcard a skill name may carry.
    /// </summary>
    public SkillUsage? SkillUsageOf(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_gate)
        {
            if (Open() is not { } connection)
            {
                return null;
            }

            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT count(*), count(DISTINCT session_id), coalesce(sum(errors > 0), 0), max(at) FROM turns WHERE instr($fence || skills_loaded || $fence, $fence || $name || $fence) > 0";
                command.Parameters.AddWithValue("$fence", NameSeparator.ToString());
                command.Parameters.AddWithValue("$name", name);
                using var reader = command.ExecuteReader();
                if (!reader.Read() || reader.GetInt32(0) == 0)
                {
                    return null;
                }

                return new SkillUsage(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), Parse(reader.GetString(3)));
            }
            catch (SqliteException ex)
            {
                Fail("read a skill's usage", ex);
                return null;
            }
        }
    }

    /// <summary>Records one reflection (schema 3); a cancelled one is the caller's to leave out. Nothing while unavailable.</summary>
    public void RecordReflection(ReflectionRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        lock (_gate)
        {
            if (Open() is not { } connection)
            {
                return;
            }

            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO reflections(session_id, turn_ordinal, at, forced, outcome, skill, action, requests, input_tokens, output_tokens) VALUES ($session, $turn, $at, $forced, $outcome, $skill, $action, $requests, $input, $output)";
                command.Parameters.AddWithValue("$session", row.SessionId is { } id ? id : DBNull.Value);
                command.Parameters.AddWithValue("$turn", row.TurnOrdinal);
                command.Parameters.AddWithValue("$at", Stamp(_time.GetUtcNow()));
                command.Parameters.AddWithValue("$forced", row.Forced ? 1 : 0);
                command.Parameters.AddWithValue("$outcome", row.Outcome);
                command.Parameters.AddWithValue("$skill", row.Skill);
                command.Parameters.AddWithValue("$action", row.Action);
                command.Parameters.AddWithValue("$requests", row.Requests);
                command.Parameters.AddWithValue("$input", row.InputTokens);
                command.Parameters.AddWithValue("$output", row.OutputTokens);
                command.ExecuteNonQuery();
                DiagnosticLog.Info(Category, ReflectionRecordedLogLine(row));
            }
            catch (SqliteException ex)
            {
                Fail("record a reflection", ex);
            }
        }
    }

    /// <summary>The newest reflection that wrote a skill — the cooldown's mark; null when none has, or while unavailable.</summary>
    public ReflectionMark? LastReflectionWrite() => LastMark(null);

    /// <summary>The newest reflection that wrote skill <paramref name="name"/>; null when none has.</summary>
    public ReflectionMark? LastReflectionOf(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return LastMark(name);
    }

    /// <summary>How many reflections wrote skill <paramref name="name"/>.</summary>
    public int ReflectionWrites(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_gate)
        {
            if (Open() is not { } connection)
            {
                return 0;
            }

            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT count(*) FROM reflections WHERE outcome = $learned AND skill = $name";
                command.Parameters.AddWithValue("$learned", ReflectionRow.Learned);
                command.Parameters.AddWithValue("$name", name);
                return command.ExecuteScalar() is long count ? (int)count : 0;
            }
            catch (SqliteException ex)
            {
                Fail("count the reflections", ex);
                return 0;
            }
        }
    }

    private ReflectionMark? LastMark(string? name)
    {
        lock (_gate)
        {
            if (Open() is not { } connection)
            {
                return null;
            }

            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT at, skill, action FROM reflections WHERE outcome = $learned" + (name is null ? "" : " AND skill = $name") + " ORDER BY at DESC, id DESC LIMIT 1";
                command.Parameters.AddWithValue("$learned", ReflectionRow.Learned);
                command.Parameters.AddWithValue("$name", name ?? "");
                using var reader = command.ExecuteReader();
                return reader.Read() ? new ReflectionMark(Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2)) : null;
            }
            catch (SqliteException ex)
            {
                Fail("read the reflections", ex);
                return null;
            }
        }
    }

    /// <summary><c>Reflection recorded: learned docker-deploy (updated) on session 12 turn 4</c> / <c>… nothing on a /learn sessions pass</c>. Pinned.</summary>
    public static string ReflectionRecordedLogLine(ReflectionRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        string what = row.Outcome == ReflectionRow.Learned ? $"learned {row.Skill} ({row.Action})" : row.Outcome;
        string where = row.SessionId is { } id ? string.Create(CultureInfo.InvariantCulture, $"session {id} turn {row.TurnOrdinal}") : "a /learn sessions pass";
        return $"Reflection recorded: {what} on {where}";
    }

    /// <summary>The names joined by <see cref="NameSeparator"/> for a cell; empty for none. Pinned.</summary>
    public static string JoinNames(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return string.Join(NameSeparator, names);
    }

    /// <summary>A cell back into its names; empty for a blank cell.</summary>
    public static IReadOnlyList<string> SplitNames(string cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        return cell.Length == 0 ? [] : cell.Split(NameSeparator, StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary><c>Session 12 begun: "first line…"</c>. Pinned.</summary>
    public static string BegunLogLine(long id, string title) =>
        string.Create(CultureInfo.InvariantCulture, $"Session {id} begun: {LogText.Quoted(title)}");

    /// <summary><c>Session 12: turn appended</c> / <c>… (cancelled)</c>. Pinned.</summary>
    public static string AppendedLogLine(long id, bool cancelled) =>
        string.Create(CultureInfo.InvariantCulture, $"Session {id}: turn appended{(cancelled ? " (cancelled)" : "")}");

    /// <summary><c>Session 12: history saved (24,000 chars)</c>. Pinned.</summary>
    public static string HistorySavedLogLine(long id, int chars) =>
        string.Create(CultureInfo.InvariantCulture, $"Session {id}: history saved ({chars:N0} chars)");

    /// <summary><c>Session 12 titled by the model: "…"</c> / <c>… by the user</c>. Pinned.</summary>
    public static string TitledLogLine(long id, string title, TitleSource source) =>
        string.Create(CultureInfo.InvariantCulture, $"Session {id} titled by the {(source == TitleSource.Model ? "model" : source == TitleSource.User ? "user" : "first line")}: {LogText.Quoted(title)}");

    /// <summary><c>Purged session 12</c>. Pinned.</summary>
    public static string PurgedLogLine(long id) => string.Create(CultureInfo.InvariantCulture, $"Purged session {id}");

    /// <summary><c>Purged 4 sessions last updated before 2026-08-20 14:05:00</c>. Pinned.</summary>
    public static string PurgedOlderLogLine(int purged, DateTimeOffset cutoff) =>
        string.Create(CultureInfo.InvariantCulture, $"Purged {SessionText.Sessions(purged)} last updated before {cutoff.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC");

    /// <summary><c>Purged all sessions (9)</c>. Pinned.</summary>
    public static string PurgedAllLogLine(int purged) => string.Create(CultureInfo.InvariantCulture, $"Purged all sessions ({purged})");

    /// <summary>
    /// The FTS5 query for a user's words: each whitespace-separated word wrapped in double quotes
    /// (its own quotes doubled) and joined by spaces — an implicit AND, every word a phrase, so
    /// FTS5's operators (<c>*</c>, <c>-</c>, <c>NOT</c>, a bare <c>"</c>) can never make a typed
    /// question a syntax error. Empty for a blank text. Pinned.
    /// </summary>
    public static string FtsQuery(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sb = new StringBuilder();
        foreach (string word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            string bare = word.Trim('"');
            if (bare.Length == 0)
            {
                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append('"').Append(bare.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
        }

        return sb.ToString();
    }

    /// <summary>
    /// The OR form of <see cref="FtsQuery"/> (2026-09-19): each word a quoted phrase, joined by
    /// <c> OR </c> — a session sharing any word matches, one sharing more ranks higher. Empty for no words. Pinned.
    /// </summary>
    public static string FtsQueryAny(IReadOnlyList<string> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        var sb = new StringBuilder();
        foreach (string word in words)
        {
            string bare = word.Trim().Trim('"');
            if (bare.Length == 0)
            {
                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append(" OR ");
            }

            sb.Append('"').Append(bare.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
        }

        return sb.ToString();
    }

    // ── The command history (2026-09-25) ────────────────────────────────────

    /// <summary>The most lines <c>command_history</c> keeps; an append past it drops the oldest. Pinned.</summary>
    public const int CommandHistoryCap = 1000;

    /// <summary>
    /// The input line's Up/Down history as stored (2026-09-25, the user's ask: <c>Keep command history</c>): the lines
    /// oldest first, empty while the store is unavailable — or while there is no file yet, which a read never creates
    /// (every profile is read at each load; a profile that never stored a line keeps no <c>sessions.db</c> for it).
    /// </summary>
    public IReadOnlyList<string> CommandHistory()
    {
        lock (_gate)
        {
            if (NoFileYet() || Open() is not { } connection)
            {
                return [];
            }

            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT text FROM command_history ORDER BY id";
                using var reader = command.ExecuteReader();
                var lines = new List<string>();
                while (reader.Read())
                {
                    lines.Add(reader.GetString(0));
                }

                return lines;
            }
            catch (SqliteException ex)
            {
                Fail("read the command history", ex);
                return [];
            }
        }
    }

    /// <summary>How many lines <c>command_history</c> holds; 0 while unavailable.</summary>
    public int CommandHistoryCount
    {
        get
        {
            lock (_gate)
            {
                return NoFileYet() ? 0 : (int)Scalar("SELECT count(*) FROM command_history", 0L);
            }
        }
    }

    /// <summary>One sent line appended, the oldest past <see cref="CommandHistoryCap"/> dropped. The caller de-duplicates (<c>InputLine.Remember</c>).</summary>
    public void AppendCommand(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        AddCommandHistory([text], overwrite: false);
    }

    /// <summary>
    /// Lines appended in order (<c>/cmdcopy --history</c>, 2026-09-25) — or in place of every stored one when
    /// <paramref name="overwrite"/> — a line equal to the one before it skipped, as the input line's own history
    /// skips it, then the oldest past <see cref="CommandHistoryCap"/> dropped; one transaction. How many went in.
    /// </summary>
    public int AddCommandHistory(IReadOnlyList<string> lines, bool overwrite)
    {
        ArgumentNullException.ThrowIfNull(lines);
        lock (_gate)
        {
            if (Open() is not { } connection)
            {
                return 0;
            }

            try
            {
                using var transaction = connection.BeginTransaction();
                string? last = null;
                if (overwrite)
                {
                    using var wipe = connection.CreateCommand();
                    wipe.Transaction = transaction;
                    wipe.CommandText = "DELETE FROM command_history";
                    wipe.ExecuteNonQuery();
                }
                else
                {
                    using var tail = connection.CreateCommand();
                    tail.Transaction = transaction;
                    tail.CommandText = "SELECT text FROM command_history ORDER BY id DESC LIMIT 1";
                    last = tail.ExecuteScalar() as string;
                }

                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO command_history(at, text) VALUES ($at, $text)";
                var at = insert.Parameters.Add("$at", SqliteType.Text);
                var text = insert.Parameters.Add("$text", SqliteType.Text);
                at.Value = Stamp(_time.GetUtcNow());
                int added = 0;
                foreach (string line in lines)
                {
                    if (line.Length == 0 || string.Equals(line, last, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    text.Value = line;
                    insert.ExecuteNonQuery();
                    last = line;
                    added++;
                }

                using var trim = connection.CreateCommand();
                trim.Transaction = transaction;
                trim.CommandText = "DELETE FROM command_history WHERE id <= (SELECT id FROM command_history ORDER BY id DESC LIMIT 1 OFFSET $cap)";
                trim.Parameters.AddWithValue("$cap", CommandHistoryCap);
                trim.ExecuteNonQuery();
                transaction.Commit();
                return added;
            }
            catch (SqliteException ex)
            {
                Fail("write the command history", ex);
                return 0;
            }
        }
    }

    /// <summary>Every stored line removed (<c>/cmdclear</c>, and a load with <c>Keep command history</c> off); how many went.</summary>
    public int ClearCommandHistory()
    {
        lock (_gate)
        {
            if (NoFileYet() || Open() is not { } connection)
            {
                return 0;
            }

            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM command_history";
                return command.ExecuteNonQuery();
            }
            catch (SqliteException ex)
            {
                Fail("clear the command history", ex);
                return 0;
            }
        }
    }

    /// <summary>Nothing opened and no file on disk: a command-history read answers empty rather than create one. Caller holds the lock.</summary>
    private bool NoFileYet() => _connection is null && !File.Exists(_filePath);

    /// <summary>The stored form of a moment: ISO-8601 UTC, invariant, fixed width so text order is time order. Pinned.</summary>
    public static string Stamp(DateTimeOffset moment) => moment.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    /// <summary>The stored word of a <see cref="TitleSource"/>: <c>first-line</c>, <c>model</c>, <c>user</c>. Pinned.</summary>
    public static string Word(TitleSource source) => source switch
    {
        TitleSource.Model => "model",
        TitleSource.User => "user",
        _ => "first-line",
    };

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _connection?.Dispose();
            _connection = null;
        }
    }

    private static DateTimeOffset Parse(string stamp) =>
        DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var moment) ? moment : DateTimeOffset.UnixEpoch;

    private static TitleSource Source(string word) => word switch
    {
        "model" => TitleSource.Model,
        "user" => TitleSource.User,
        _ => TitleSource.FirstLine,
    };

    private static SessionSummary ReadSummary(SqliteDataReader reader) =>
        new(reader.GetInt64(0), Parse(reader.GetString(1)), Parse(reader.GetString(2)), reader.GetString(3), Source(reader.GetString(4)), reader.GetString(5), reader.GetInt32(6));

    /// <summary>
    /// The reflections of those sessions (<paramref name="turnsWhere"/> reads the same for both tables,
    /// plus <paramref name="reflectionsAlso"/> for the rows that belong to no session), then the
    /// turns, then the sessions, in one transaction; how many sessions went. Never the CASCADE's. Caller holds the lock.
    /// </summary>
    private int Delete(string turnsWhere, string sessionsWhere, (string Name, object Value)? parameter, string? reflectionsAlso = null)
    {
        if (Open() is not { } connection)
        {
            return 0;
        }

        try
        {
            using var transaction = connection.BeginTransaction();
            using var reflections = connection.CreateCommand();
            reflections.Transaction = transaction;
            reflections.CommandText = "DELETE FROM reflections " + turnsWhere;
            using var turns = connection.CreateCommand();
            turns.Transaction = transaction;
            turns.CommandText = "DELETE FROM turns " + turnsWhere;
            using var sessions = connection.CreateCommand();
            sessions.Transaction = transaction;
            sessions.CommandText = "DELETE FROM sessions " + sessionsWhere;
            if (parameter is { } p)
            {
                reflections.Parameters.AddWithValue(p.Name, p.Value);
                turns.Parameters.AddWithValue(p.Name, p.Value);
                sessions.Parameters.AddWithValue(p.Name, p.Value);
            }

            reflections.ExecuteNonQuery();
            if (reflectionsAlso is not null)
            {
                using var passes = connection.CreateCommand();
                passes.Transaction = transaction;
                passes.CommandText = "DELETE FROM reflections " + reflectionsAlso;
                if (parameter is { } q)
                {
                    passes.Parameters.AddWithValue(q.Name, q.Value);
                }

                passes.ExecuteNonQuery();
            }

            turns.ExecuteNonQuery();
            int removed = sessions.ExecuteNonQuery();
            transaction.Commit();
            return removed;
        }
        catch (SqliteException ex)
        {
            Fail("purge sessions", ex);
            return 0;
        }
    }

    private long Scalar(string sql, long fallback)
    {
        if (Open() is not { } connection)
        {
            return fallback;
        }

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar() is long value ? value : fallback;
        }
        catch (SqliteException ex)
        {
            Fail("read the store", ex);
            return fallback;
        }
    }

    /// <summary>The connection, opened on first use; null after a failed open or a schema other than <see cref="SchemaVersion"/>. Caller holds the lock.</summary>
    private SqliteConnection? Open()
    {
        if (_connection is not null)
        {
            return _connection;
        }

        if (_failed || _disposed)
        {
            return null;
        }

        SqliteConnection? connection = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _filePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
            connection.Open();
            using (var pragmas = connection.CreateCommand())
            {
                pragmas.CommandText = "PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON;";
                pragmas.ExecuteNonQuery();
            }

            // The version is read before Schema runs, so a file this code does not know is refused
            // untouched (Schema would otherwise add today's tables to it). A new file has no meta yet.
            // Two statements, not one CASE: SQLite resolves a table's name when it prepares, so a query
            // naming meta fails on a file that has none even behind a guard.
            using (var version = connection.CreateCommand())
            {
                version.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'meta'";
                bool hasMeta = version.ExecuteScalar() is long tables && tables > 0;
                version.CommandText = "SELECT value FROM meta WHERE key = 'schema'";
                if (hasMeta && version.ExecuteScalar() is long stored && stored != SchemaVersion)
                {
                    connection.Dispose();
                    _failed = true;
                    DiagnosticLog.Warn(Category, SchemaMismatchWarning(stored));
                    return null;
                }
            }

            using (var schema = connection.CreateCommand())
            {
                schema.CommandText = Schema;
                schema.ExecuteNonQuery();
            }

            _connection = connection;
            return connection;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            connection?.Dispose();
            _failed = true;
            DiagnosticLog.Warn(Category, $"Could not open {FileName}; sessions are off: {ex.Message}", ex);
            return null;
        }
    }

    private static bool Has(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM pragma_table_info($table) WHERE name = $column";
        command.Parameters.AddWithValue("$table", table);
        command.Parameters.AddWithValue("$column", column);
        return command.ExecuteScalar() is long count && count > 0;
    }

    /// <summary>The stored <c>meta</c> schema number, or -1 while the store is unavailable. A read for the tests.</summary>
    public long SchemaStored()
    {
        lock (_gate)
        {
            if (Open() is not { } connection)
            {
                return -1;
            }

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM meta WHERE key = 'schema'";
            return command.ExecuteScalar() is long v ? v : -1;
        }
    }

    /// <summary>Whether <paramref name="table"/> has a column named <paramref name="column"/> (<c>PRAGMA table_info</c>); false while the store is unavailable. For the tests.</summary>
    public bool HasColumn(string table, string column)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(column);
        lock (_gate)
        {
            if (Open() is not { } connection)
            {
                return false;
            }

            return Has(connection, table, column);
        }
    }

    private static void Fail(string what, SqliteException ex) =>
        DiagnosticLog.Error(Category, $"Could not {what} in {FileName}: {ex.Message}", ex);

    /// <summary>The whole schema, every statement <c>IF NOT EXISTS</c>: the tables, the index, then the FTS5 side (<see cref="SchemaFts"/>).</summary>
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value INTEGER NOT NULL);
        INSERT OR IGNORE INTO meta(key, value) VALUES ('schema', 3);
        CREATE TABLE IF NOT EXISTS sessions(
            id INTEGER PRIMARY KEY,
            started_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            title TEXT NOT NULL,
            title_source TEXT NOT NULL,
            model TEXT NOT NULL,
            turns INTEGER NOT NULL,
            history_json TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS sessions_updated ON sessions(updated_at);
        CREATE TABLE IF NOT EXISTS turns(
            id INTEGER PRIMARY KEY,
            session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
            ordinal INTEGER NOT NULL,
            at TEXT NOT NULL,
            user_text TEXT NOT NULL,
            reply_text TEXT NOT NULL,
            tool_calls INTEGER NOT NULL,
            tool_names TEXT NOT NULL DEFAULT '',
            skills_loaded TEXT NOT NULL DEFAULT '',
            errors INTEGER NOT NULL DEFAULT 0,
            input_tokens INTEGER NOT NULL,
            output_tokens INTEGER NOT NULL,
            cancelled INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS turns_session ON turns(session_id, ordinal);
        CREATE TABLE IF NOT EXISTS reflections(
            id INTEGER PRIMARY KEY,
            session_id INTEGER REFERENCES sessions(id) ON DELETE CASCADE,
            turn_ordinal INTEGER NOT NULL,
            at TEXT NOT NULL,
            forced INTEGER NOT NULL,
            outcome TEXT NOT NULL,
            skill TEXT NOT NULL,
            action TEXT NOT NULL,
            requests INTEGER NOT NULL,
            input_tokens INTEGER NOT NULL,
            output_tokens INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS reflections_at ON reflections(at);
        CREATE TABLE IF NOT EXISTS command_history(
            id INTEGER PRIMARY KEY,
            at TEXT NOT NULL,
            text TEXT NOT NULL);
        """ + SchemaFts;

    /// <summary>The FTS5 external-content table over <c>turns</c> and its three sync triggers.</summary>
    private const string SchemaFts = """
        CREATE VIRTUAL TABLE IF NOT EXISTS turns_fts USING fts5(user_text, reply_text, content='turns', content_rowid='id', tokenize='unicode61');
        CREATE TRIGGER IF NOT EXISTS turns_ai AFTER INSERT ON turns BEGIN
            INSERT INTO turns_fts(rowid, user_text, reply_text) VALUES (new.id, new.user_text, new.reply_text);
        END;
        CREATE TRIGGER IF NOT EXISTS turns_ad AFTER DELETE ON turns BEGIN
            INSERT INTO turns_fts(turns_fts, rowid, user_text, reply_text) VALUES ('delete', old.id, old.user_text, old.reply_text);
        END;
        CREATE TRIGGER IF NOT EXISTS turns_au AFTER UPDATE ON turns BEGIN
            INSERT INTO turns_fts(turns_fts, rowid, user_text, reply_text) VALUES ('delete', old.id, old.user_text, old.reply_text);
            INSERT INTO turns_fts(rowid, user_text, reply_text) VALUES (new.id, new.user_text, new.reply_text);
        END;
        """;
}
