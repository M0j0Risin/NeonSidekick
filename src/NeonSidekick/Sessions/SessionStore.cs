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

/// <summary>
/// One session row without its turns: the <c>/sessions</c> list's view and a hit's header. <paramref name="Cwd"/> (2026-11-21, feature idea
/// #4) is the working folder of its last turn, resolved; null for a session saved before, or while the store couldn't add the column.
/// </summary>
public sealed record SessionSummary(long Id, DateTimeOffset StartedAt, DateTimeOffset UpdatedAt, string Title, TitleSource TitleSource, string Model, int Turns, string? Cwd = null);

/// <summary>
/// One completed turn as the transcript showed it: the user's text (pastes expanded), the reply's
/// raw text, how many tools the model called, and — since schema 3 (2026-09-19), the reflection's
/// telemetry — the distinct tool names in first-call order, the skills it loaded and how many
/// calls answered with an error; then the request's tokens and whether the keys cut it short.
/// The replay never shows the names (one <c>🛠️ N tool calls</c> line per turn as ever).
/// </summary>
public sealed record SessionTurn(int Ordinal, DateTimeOffset At, string UserText, string ReplyText, int ToolCalls, IReadOnlyList<string> ToolNames, IReadOnlyList<string> SkillsLoaded, int Errors, long InputTokens, long OutputTokens, bool Cancelled, IReadOnlyList<Web.WebSource>? Sources = null)
{
    /// <summary>
    /// The reply's figures for <c>/usage</c>'s trend (2026-11-27, seeded at a restore): the model that answered, its timed output
    /// (<see cref="OutputMs"/> over <see cref="TimedOutput"/> tokens), the context at its last request, the wait for a first token a
    /// request, and its reasoning tokens. Null for an older row, or a store whose column adds failed.
    /// </summary>
    public string? Model { get; init; }

    public long? OutputMs { get; init; }

    public long? TimedOutput { get; init; }

    public long? ContextTokens { get; init; }

    public long? FirstTokenMs { get; init; }

    public long? ReasoningTokens { get; init; }

    /// <summary>
    /// What the turn took from the web (2026-11-15, the web-tools plan's §2, <see cref="Llm.TurnTrace.Sources"/>), from <c>turn_sources</c>:
    /// the 📚 line a load or a rewind redraws and the export's list. Empty for an older row, or a turn that read nothing.
    /// </summary>
    public IReadOnlyList<Web.WebSource> SourceList => Sources ?? [];
}

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

    /// <summary>The user said no to the reflection's write (2026-10-12, <c>Reflection confirm mode</c>); the skill names what it would have written.</summary>
    public const string Declined = "declined";
    public const string Created = "created";
    public const string Updated = "updated";
}

/// <summary>
/// The per-profile session store: <c>sessions.db</c> under the profile's <c>db\sessions</c> (<see cref="FolderName"/>), one SQLite file
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
public sealed partial class SessionStore : IDisposable
{
    public const string FileName = "sessions.db";

    /// <summary>
    /// The folder under the profile that holds <see cref="FileName"/> and its WAL siblings: <c>db\sessions</c> since 2026-10-10 (the
    /// user's call), the profile's root until then. The user's own files were moved by hand once; since 2026-10-11 (the review) a file still
    /// at the root moves itself at the first open (<see cref="LegacyStoreMove"/>). Pinned.
    /// </summary>
    public static readonly string FolderName = Path.Combine("db", "sessions");

    /// <summary>
    /// The store's own schema number, kept in the <c>meta</c> table. A file at any other number
    /// is refused as unavailable (<see cref="SchemaMismatchWarning"/>). 3 since 2026-09-19: the
    /// <c>turns</c> rows carry <c>tool_names</c>, <c>skills_loaded</c> and <c>errors</c> and a
    /// <c>reflections</c> table records every reflection. The 1 → 2 → 3 migrations went on
    /// 2026-09-24 (the user's call): every real file had long been at 3, and a schema that lived a
    /// day was not worth the code, the fixtures and a smoke leg. The <c>command_history</c> table (2026-09-25,
    /// <c>Keep message history</c>) left it at 3: an added table is <c>IF NOT EXISTS</c> on every open, so a file
    /// from before gains it and an older build reads past it; no row it knew changed shape.
    /// <c>turn_sources</c> (2026-11-15, the web-tools plan's §2: each turn's pages, downloads and searches) followed the same way.
    /// The added columns (2026-11-21, feature ideas #4 and #7: <c>sessions.cwd</c>, <c>turns.model</c>, <c>turns.output_ms</c>,
    /// <c>turns.timed_output</c>; <c>turns.cwd</c> since 2026-11-22, the review's #41) leave it at 3 too: nullable, added at the open, and every
    /// older build names its columns in each INSERT and SELECT, so it reads the file as before. That matters: the updater's rollback can bring an
    /// older copy back, which refuses any other number.
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

    /// <summary>Whether the added columns are there for this run (2026-11-21): false after an add that failed, so writes and reads leave them out.</summary>
    private bool _extraColumns;

    /// <summary>
    /// Run inside the column adds' guard, before them: a test throws a <see cref="SqliteException"/> here to stand for another copy holding
    /// the write lock past the busy wait, which no file can be made to do on cue. Null in the app.
    /// </summary>
    internal Action? BeforeColumnAdds { get; init; }
    private bool _disposed;

    /// <param name="directory">The profile directory; the file is <see cref="FileName"/> in its <see cref="FolderName"/>, made at the first open.</param>
    /// <param name="time">The clock behind every timestamp; tests pass a fake.</param>
    private long _version;

    /// <summary>
    /// Moves with every write to the sessions or their turns (2026-11-16, the review's #15 and #78): a begun session, a turn, a cut, a saved
    /// history, a title, a purge. The Web UI's Sessions tab keys its list and its tag on it.
    /// </summary>
    public long Version => Interlocked.Read(ref _version);

    public SessionStore(string directory, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        string full = Path.GetFullPath(directory);
        // A file an older build left at the profile's root moves into the folder once (2026-10-11, the review); read in place if it cannot.
        _filePath = LegacyStoreMove.Resolve(Path.Combine(full, FileName), Path.Combine(full, FolderName, FileName), Category);
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

    /// <summary>A new session row with no turns yet; null while unavailable. <paramref name="cwd"/> (2026-11-21) is the working folder, resolved.</summary>
    public long? Begin(string title, string model, string? cwd = null)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(model);
        lock (_gate)
        {
            Interlocked.Increment(ref _version);
            if (Open() is not { } connection)
            {
                return null;
            }

            try
            {
                string now = Stamp(_time.GetUtcNow());
                using var command = connection.CreateCommand();
                command.CommandText = _extraColumns
                    ? "INSERT INTO sessions(started_at, updated_at, title, title_source, model, turns, history_json, cwd) VALUES ($started, $updated, $title, $source, $model, 0, '', $cwd); SELECT last_insert_rowid();"
                    : "INSERT INTO sessions(started_at, updated_at, title, title_source, model, turns, history_json) VALUES ($started, $updated, $title, $source, $model, 0, ''); SELECT last_insert_rowid();";
                command.Parameters.AddWithValue("$cwd", (object?)cwd ?? DBNull.Value);
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
                SaveFailed("begin a session", ex);
                return null;
            }
        }
    }

    /// <summary>
    /// Appends one completed turn to session <paramref name="id"/> and bumps its turn count and <c>updated_at</c>; the names go in joined by <see cref="NameSeparator"/>.
    /// Returns the new row's ordinal (2026-09-30: the screen stamps it on the turn's message for <c>/rewind</c>, <see cref="Llm.ConversationHistory.TurnOrdinalKey"/>), or null while the store is unavailable or the write failed.
    /// </summary>
    /// <remarks>
    /// <paramref name="sources"/> (2026-11-15, the web-tools plan's §2) go in <c>turn_sources</c> in the same transaction, one row each in
    /// order; a <see cref="Web.WebSourceKind.Search"/> with no URL or title, so replay and export count searches without storing a query.
    /// </remarks>
    /// <para>
    /// <paramref name="model"/>, <paramref name="usage"/> and <paramref name="cwd"/> (2026-11-21, feature ideas #4 and #7): the model that
    /// answered, the turn's timed output (<see cref="Llm.TokenUsage.Generating"/> and the tokens it covers) for <c>/usage</c> › Models, and
    /// the working folder, which becomes the session's (the folder of its last turn). Null leaves each column empty.
    /// </para>
    /// <para>
    /// <paramref name="contextTokens"/> (2026-11-27, <c>/usage</c>'s trend and records): the prompt at the reply's last request; the wait a
    /// request (<see cref="Llm.TokenUsage.AverageToFirstToken"/>, not the waits summed over the reply's round trips) and the reasoning tokens
    /// come from <paramref name="usage"/>. Null for another model's context (<c>/claude</c>, a bot) or a reply that reported nothing.
    /// </para>
    public int? AppendTurn(long id, string userText, string replyText, int toolCalls, IReadOnlyList<string> toolNames, IReadOnlyList<string> skillsLoaded, int errors, long inputTokens, long outputTokens, bool cancelled, IReadOnlyList<Web.WebSource>? sources = null,
        string? model = null, Llm.TokenUsage? usage = null, string? cwd = null, long? contextTokens = null)
    {
        ArgumentNullException.ThrowIfNull(userText);
        ArgumentNullException.ThrowIfNull(replyText);
        ArgumentNullException.ThrowIfNull(toolNames);
        ArgumentNullException.ThrowIfNull(skillsLoaded);
        lock (_gate)
        {
            Interlocked.Increment(ref _version);
            if (Open() is not { } connection)
            {
                return null;
            }

            try
            {
                string now = Stamp(_time.GetUtcNow());
                using var transaction = connection.BeginTransaction();
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                // The new row's id comes back with the insert (2026-11-16, the web-tools review's #93), for its sources.
                insert.CommandText = _extraColumns
                    ? "INSERT INTO turns(session_id, ordinal, at, user_text, reply_text, tool_calls, tool_names, skills_loaded, errors, input_tokens, output_tokens, cancelled, model, output_ms, timed_output, cwd, context_tokens, first_token_ms, reasoning_tokens) VALUES ($id, (SELECT turns + 1 FROM sessions WHERE id = $id), $at, $user, $reply, $tools, $names, $skills, $errors, $input, $output, $cancelled, $model, $ms, $timed, $cwd, $context, $first, $reasoning) RETURNING id"
                    : "INSERT INTO turns(session_id, ordinal, at, user_text, reply_text, tool_calls, tool_names, skills_loaded, errors, input_tokens, output_tokens, cancelled) VALUES ($id, (SELECT turns + 1 FROM sessions WHERE id = $id), $at, $user, $reply, $tools, $names, $skills, $errors, $input, $output, $cancelled) RETURNING id";
                var (ms, timed) = Speed(usage);
                insert.Parameters.AddWithValue("$model", string.IsNullOrEmpty(model) ? DBNull.Value : model);
                insert.Parameters.AddWithValue("$ms", (object?)ms ?? DBNull.Value);
                insert.Parameters.AddWithValue("$timed", (object?)timed ?? DBNull.Value);
                insert.Parameters.AddWithValue("$cwd", (object?)cwd ?? DBNull.Value);   // each turn's folder, for a truncate (#41)
                long? firstMs = usage?.AverageToFirstToken is { } wait ? (long)Math.Round(wait.TotalMilliseconds) : null;
                insert.Parameters.AddWithValue("$context", (object?)contextTokens ?? DBNull.Value);   // 2026-11-27, /usage's trend
                insert.Parameters.AddWithValue("$first", (object?)firstMs ?? DBNull.Value);
                insert.Parameters.AddWithValue("$reasoning", (object?)usage?.Reasoning ?? DBNull.Value);
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
                long turnId = (long)insert.ExecuteScalar()!;
                if (sources is { Count: > 0 })
                {
                    InsertSources(connection, transaction, turnId, sources);
                }

                using var bump = connection.CreateCommand();
                bump.Transaction = transaction;
                bump.CommandText = _extraColumns && cwd is not null
                    ? "UPDATE sessions SET turns = turns + 1, updated_at = $updated, cwd = $cwd WHERE id = $id"
                    : "UPDATE sessions SET turns = turns + 1, updated_at = $updated WHERE id = $id";
                bump.Parameters.AddWithValue("$cwd", (object?)cwd ?? DBNull.Value);
                bump.Parameters.AddWithValue("$id", id);
                bump.Parameters.AddWithValue("$updated", now);
                bump.ExecuteNonQuery();
                using var read = connection.CreateCommand();
                read.Transaction = transaction;
                read.CommandText = "SELECT turns FROM sessions WHERE id = $id";
                read.Parameters.AddWithValue("$id", id);
                object? turns = read.ExecuteScalar();
                transaction.Commit();
                DiagnosticLog.Debug(Category, AppendedLogLine(id, cancelled));
                return turns is long ordinal ? (int)ordinal : null;
            }
            catch (SqliteException ex)
            {
                SaveFailed("append a turn", ex);
                return null;
            }
        }
    }

    // The turn just inserted's sources (2026-11-15), keyed by its row id, in order.
    private static void InsertSources(SqliteConnection connection, SqliteTransaction transaction, long turn, IReadOnlyList<Web.WebSource> sources)
    {
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        var seq = insert.Parameters.Add("$seq", SqliteType.Integer);
        var kind = insert.Parameters.Add("$kind", SqliteType.Text);
        var url = insert.Parameters.Add("$url", SqliteType.Text);
        var title = insert.Parameters.Add("$title", SqliteType.Text);

        insert.CommandText = "INSERT INTO turn_sources(turn_id, seq, kind, url, title) VALUES ($turn, $seq, $kind, $url, $title)";
        insert.Parameters.AddWithValue("$turn", turn);
        for (int i = 0; i < sources.Count; i++)
        {
            seq.Value = i;
            kind.Value = SourceKindName(sources[i].Kind);
            url.Value = sources[i].Url;
            title.Value = sources[i].Title;
            insert.ExecuteNonQuery();
        }
    }

    /// <summary>A source kind's name in <c>turn_sources.kind</c>. Contract: an older or newer build reads these.</summary>
    /// <remarks>Every kind by name (2026-11-16, the web-tools review's #190): a new one must be given its word, never stored as a page.</remarks>
    public static string SourceKindName(Web.WebSourceKind kind) => kind switch
    {
        Web.WebSourceKind.Page => "page",
        Web.WebSourceKind.Download => "download",
        Web.WebSourceKind.Search => "search",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "a web source kind with no stored name"),
    };

    /// <summary><see cref="SourceKindName"/> read back; null for a name this build doesn't know, which is skipped.</summary>
    public static Web.WebSourceKind? SourceKindOf(string name) => name switch
    {
        "page" => Web.WebSourceKind.Page,
        "download" => Web.WebSourceKind.Download,
        "search" => Web.WebSourceKind.Search,
        _ => null,
    };

    /// <summary>
    /// Cuts session <paramref name="id"/> back to its first <paramref name="keepThrough"/> turns (2026-09-30, <c>/rewind</c>), in one
    /// transaction: the <c>turns</c> rows past that ordinal are deleted (the <c>turns_ad</c> trigger takes them out of the search
    /// index), the turn count is set to the last ordinal left (the row count, ordinals running 1, 2, 3…) so the next <see cref="AppendTurn"/> carries on from there, and
    /// <c>updated_at</c> moves. The reflections stay. They record skills that were really written, and those skills outlive the
    /// rewind, so <c>/skills</c>' learned counts stay true. Returns how many rows went, 0 while unavailable.
    /// </summary>
    public int TruncateTurns(long id, int keepThrough)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(keepThrough);
        lock (_gate)
        {
            Interlocked.Increment(ref _version);
            if (Open() is not { } connection)
            {
                return 0;
            }

            try
            {
                using var transaction = connection.BeginTransaction();

                // The turns' sources first, by hand as every other child goes (2026-11-16, the web-tools review's #160): never the CASCADE's.
                using var sources = connection.CreateCommand();
                sources.Transaction = transaction;
                sources.CommandText = "DELETE FROM turn_sources WHERE turn_id IN (SELECT id FROM turns WHERE session_id = $id AND ordinal > $keep)";
                sources.Parameters.AddWithValue("$id", id);
                sources.Parameters.AddWithValue("$keep", keepThrough);
                sources.ExecuteNonQuery();
                using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM turns WHERE session_id = $id AND ordinal > $keep";
                delete.Parameters.AddWithValue("$id", id);
                delete.Parameters.AddWithValue("$keep", keepThrough);
                int removed = delete.ExecuteNonQuery();
                using var count = connection.CreateCommand();
                count.Transaction = transaction;
                // The folder goes back with the turns (2026-11-22, the review's #41): the last kept turn's that has one; a session saved before turns
                // kept theirs keeps its own.
                count.CommandText = _extraColumns
                    ? "UPDATE sessions SET turns = COALESCE((SELECT max(ordinal) FROM turns WHERE session_id = $id), 0), cwd = COALESCE((SELECT cwd FROM turns WHERE session_id = $id AND cwd IS NOT NULL ORDER BY ordinal DESC LIMIT 1), cwd), updated_at = $updated WHERE id = $id"
                    : "UPDATE sessions SET turns = COALESCE((SELECT max(ordinal) FROM turns WHERE session_id = $id), 0), updated_at = $updated WHERE id = $id";
                count.Parameters.AddWithValue("$id", id);
                count.Parameters.AddWithValue("$updated", Stamp(_time.GetUtcNow()));
                count.ExecuteNonQuery();
                transaction.Commit();
                DiagnosticLog.Debug(Category, TruncatedLogLine(id, keepThrough, removed));
                return removed;
            }
            catch (SqliteException ex)
            {
                SaveFailed("truncate a session", ex);
                return 0;
            }
        }
    }

    /// <summary>Replaces session <paramref name="id"/>'s stored history (<see cref="SessionHistory.ToJson"/>) — after every turn and every compact.</summary>
    public void SaveHistory(long id, string historyJson)
    {
        ArgumentNullException.ThrowIfNull(historyJson);
        lock (_gate)
        {
            Interlocked.Increment(ref _version);
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
                SaveFailed("save the history", ex);
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
            Interlocked.Increment(ref _version);
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
                command.CommandText = "SELECT " + SummaryColumns + " FROM sessions ORDER BY updated_at DESC, id DESC" + (max > 0 ? " LIMIT $max" : "");
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
                command.CommandText = "SELECT " + SummaryColumns + " FROM sessions WHERE id = $id";
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
                    command.CommandText = "SELECT " + SummaryColumns + ", history_json FROM sessions WHERE id = $id";
                    command.Parameters.AddWithValue("$id", id);
                    using var reader = command.ExecuteReader();
                    if (!reader.Read())
                    {
                        return null;
                    }

                    summary = ReadSummary(reader);
                    history = reader.GetString(8);
                }

                var turns = new List<SessionTurn>(summary.Turns);
                using (var command = connection.CreateCommand())
                {
                    // The trend's figures (2026-11-27) only when the columns are there; NULL in their places otherwise, as SummaryColumns does for cwd.
                    command.CommandText = "SELECT ordinal, at, user_text, reply_text, tool_calls, tool_names, skills_loaded, errors, input_tokens, output_tokens, cancelled, "
                        + (_extraColumns ? "model, output_ms, timed_output, context_tokens, first_token_ms, reasoning_tokens" : "NULL, NULL, NULL, NULL, NULL, NULL")
                        + " FROM turns WHERE session_id = $id ORDER BY ordinal";
                    command.Parameters.AddWithValue("$id", id);
                    using var reader = command.ExecuteReader();
                    long? Long(int i) => reader.IsDBNull(i) ? null : reader.GetInt64(i);
                    while (reader.Read())
                    {
                        turns.Add(new SessionTurn(reader.GetInt32(0), Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.GetInt32(4), SplitNames(reader.GetString(5)), SplitNames(reader.GetString(6)), reader.GetInt32(7), reader.GetInt64(8), reader.GetInt64(9), reader.GetInt32(10) != 0)
                        {
                            Model = reader.IsDBNull(11) ? null : reader.GetString(11),
                            OutputMs = Long(12),
                            TimedOutput = Long(13),
                            ContextTokens = Long(14),
                            FirstTokenMs = Long(15),
                            ReasoningTokens = Long(16),
                        });
                    }
                }

                // Each turn's sources (2026-11-15, the web-tools plan's §2), joined by ordinal in the same read.
                var sources = new Dictionary<int, List<Web.WebSource>>();
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT t.ordinal, s.kind, s.url, s.title FROM turn_sources s JOIN turns t ON t.id = s.turn_id WHERE t.session_id = $id ORDER BY t.ordinal, s.seq";
                    command.Parameters.AddWithValue("$id", id);
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        if (SourceKindOf(reader.GetString(1)) is { } kind)
                        {
                            int ordinal = reader.GetInt32(0);
                            if (!sources.TryGetValue(ordinal, out var list))
                            {
                                sources[ordinal] = list = [];
                            }

                            list.Add(new Web.WebSource(kind, reader.GetString(2), reader.GetString(3)));
                        }
                    }
                }

                if (sources.Count > 0)
                {
                    for (int i = 0; i < turns.Count; i++)
                    {
                        if (sources.TryGetValue(turns[i].Ordinal, out var list))
                        {
                            turns[i] = turns[i] with { Sources = list };
                        }
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
    /// <remarks>
    /// <paramref name="marks"/> (2026-11-16, the Web UI's Sessions tab): with a pair given, a hit's <see cref="SessionHit.Snippet"/> is instead
    /// the whole text of the first column holding a match (the user's line, else the reply), each match between the pair, bound as parameters,
    /// never spliced: the caller cleans the text (a saved picture's label keeps its full path, which FTS5's own snippet could cut in half) and
    /// cuts its own window. Without one, the snippet is FTS5's, as <c>session_manager</c> and <c>/learn sessions</c> read it.
    /// <paramref name="accept"/> (2026-11-16, the review's #8, #21 and #40): with marks, which marked column counts, the user's line before the
    /// reply (null: one holding the opener). The best turns are ranked first with no highlight, then highlighted one at a time in rank order, a
    /// session's first accepted turn its hit, so a match the caller rejects (inside a saved path) never hides the same session's real one lower.
    /// </remarks>
    public IReadOnlyList<SessionHit> Search(string query, int max, long? exclude = null, (string Open, string Close)? marks = null, Func<string, bool>? accept = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        return SearchFts(FtsQuery(query), max, exclude, marks, accept);
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

    private IReadOnlyList<SessionHit> SearchFts(string fts, int max, long? exclude, (string Open, string Close)? marks = null, Func<string, bool>? accept = null)
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
                if (marks is { } pair)
                {
                    MarkedHits(connection, fts, max, exclude, pair, accept, best, order);
                }
                else
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = "SELECT t.session_id, t.ordinal, snippet(turns_fts, -1, '', '', '…', $tokens) FROM turns_fts JOIN turns t ON t.id = turns_fts.rowid WHERE turns_fts MATCH $query ORDER BY bm25(turns_fts) LIMIT $scan";
                    command.Parameters.AddWithValue("$query", fts);
                    command.Parameters.AddWithValue("$scan", SearchScan);
                    command.Parameters.AddWithValue("$tokens", SnippetTokens);
                    using var reader = command.ExecuteReader();
                    while (reader.Read() && order.Count < max)
                    {
                        long session = reader.GetInt64(0);
                        if (session == exclude || best.ContainsKey(session))
                        {
                            continue;
                        }

                        best[session] = (reader.GetInt32(1), reader.IsDBNull(2) ? "" : reader.GetString(2));
                        order.Add(session);
                    }
                }

                var hits = new List<SessionHit>(order.Count);
                foreach (long session in order)
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = "SELECT " + SummaryColumns + " FROM sessions WHERE id = $id";
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

    /// <summary>
    /// The marked search's hits (the review's #8, #21 and #40): the best <see cref="SearchScan"/> turns read by rank alone (no highlight, the
    /// reader closed), then walked in that order, each new session's turn highlighted on its own and its first column <paramref name="accept"/>
    /// takes (the user's line, then the reply) kept; a session none of whose turns pass is left out. Stops at <paramref name="max"/>. Caller
    /// holds the lock.
    /// </summary>
    private static void MarkedHits(SqliteConnection connection, string fts, int max, long? exclude, (string Open, string Close) pair,
        Func<string, bool>? accept, Dictionary<long, (int Turn, string Snippet)> best, List<long> order)
    {
        var ranked = new List<(long Row, long Session, int Ordinal)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT turns_fts.rowid, t.session_id, t.ordinal FROM turns_fts JOIN turns t ON t.id = turns_fts.rowid WHERE turns_fts MATCH $query ORDER BY bm25(turns_fts) LIMIT $scan";
            command.Parameters.AddWithValue("$query", fts);
            command.Parameters.AddWithValue("$scan", SearchScan);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                ranked.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2)));
            }
        }

        accept ??= text => text.Contains(pair.Open, StringComparison.Ordinal);
        using var highlight = connection.CreateCommand();
        highlight.CommandText = "SELECT highlight(turns_fts, 0, $open, $close), highlight(turns_fts, 1, $open, $close) FROM turns_fts WHERE turns_fts MATCH $query AND rowid = $id";
        highlight.Parameters.AddWithValue("$query", fts);
        highlight.Parameters.AddWithValue("$open", pair.Open);
        highlight.Parameters.AddWithValue("$close", pair.Close);
        var id = highlight.Parameters.Add("$id", SqliteType.Integer);
        foreach (var (row, session, ordinal) in ranked)
        {
            if (order.Count >= max)
            {
                break;
            }

            if (session == exclude || best.ContainsKey(session))
            {
                continue;
            }

            id.Value = row;
            using var reader = highlight.ExecuteReader();
            if (!reader.Read())
            {
                continue;
            }

            string? taken = null;
            for (int column = 0; column < 2 && taken is null; column++)
            {
                string text = reader.IsDBNull(column) ? "" : reader.GetString(column);
                if (accept(text))
                {
                    taken = text;
                }
            }

            if (taken is not null)
            {
                best[session] = (ordinal, taken);
                order.Add(session);
            }
        }
    }

    /// <summary>
    /// The sessions a purge deleted, by id (2026-10-28, the checkpoints plan: their file checkpoints go with them), from <see cref="Purge"/>,
    /// <see cref="PurgeOlderThan"/> and <see cref="PurgeAll"/>, so every site that deletes sessions (the commands, the retention purge, the
    /// sessions pane's Del) is covered. Raised on the purging thread after the store's lock is let go; never with an empty list.
    /// </summary>
    public event Action<IReadOnlyList<long>>? Purged;

    /// <summary>Removes session <paramref name="id"/> and its turns; false when there was none.</summary>
    public bool Purge(long id)
    {
        bool purged;
        var ids = Purged is null ? null : new List<long>();   // read only for a listener (2026-10-28, review P10-06)
        lock (_gate)
        {
            purged = Delete("WHERE session_id = $id", "WHERE id = $id", ("$id", id), ids: ids) > 0;
        }

        if (purged)
        {
            DiagnosticLog.Info(Category, PurgedLogLine(id));
        }

        RaisePurged(ids);
        return purged;
    }

    private void RaisePurged(List<long>? ids)
    {
        if (ids is { Count: > 0 })
        {
            Purged?.Invoke(ids);
        }
    }

    /// <summary>Removes every session last updated before <paramref name="cutoff"/> (and the reflection rows of a <c>/learn sessions</c> pass from before it, which belong to no session); how many sessions went.</summary>
    public int PurgeOlderThan(DateTimeOffset cutoff)
    {
        int purged;
        var ids = Purged is null ? null : new List<long>();   // read only for a listener (2026-10-28, review P10-06)
        lock (_gate)
        {
            purged = Delete("WHERE session_id IN (SELECT id FROM sessions WHERE updated_at < $cutoff)", "WHERE updated_at < $cutoff", ("$cutoff", Stamp(cutoff)), "WHERE session_id IS NULL AND at < $cutoff", ids);
        }

        if (purged > 0)
        {
            DiagnosticLog.Info(Category, PurgedOlderLogLine(purged, cutoff));
        }

        RaisePurged(ids);
        return purged;
    }

    /// <summary>Removes every session; how many went.</summary>
    public int PurgeAll()
    {
        int purged;
        var ids = Purged is null ? null : new List<long>();   // read only for a listener (2026-10-28, review P10-06)
        lock (_gate)
        {
            purged = Delete("", "", null, ids: ids);
        }

        if (purged > 0)
        {
            DiagnosticLog.Info(Category, PurgedAllLogLine(purged));
        }

        RaisePurged(ids);
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

    /// <summary>
    /// Every reflection that wrote a skill, oldest first (2026-10-02): what <c>skills.db</c>'s one-time import reads, the skills' history
    /// moving there (<c>SkillRecords.ImportFrom</c>). Empty while unavailable.
    /// </summary>
    public IReadOnlyList<(DateTimeOffset At, string Skill, string Action, long? SessionId)> LearnedReflections()
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
                command.CommandText = "SELECT at, skill, action, session_id FROM reflections WHERE outcome = $learned AND skill <> '' ORDER BY at, id";
                command.Parameters.AddWithValue("$learned", ReflectionRow.Learned);
                using var reader = command.ExecuteReader();
                var rows = new List<(DateTimeOffset, string, string, long?)>();
                while (reader.Read())
                {
                    rows.Add((Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetInt64(3)));
                }

                return rows;
            }
            catch (SqliteException ex)
            {
                Fail("read the reflections", ex);
                return [];
            }
        }
    }

    /// <summary>Every stored turn that loaded a skill, oldest first (2026-10-02, the same import): when, the session, the names, the turn's errors.</summary>
    public IReadOnlyList<(DateTimeOffset At, long SessionId, IReadOnlyList<string> Skills, int Errors)> SkillLoads()
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
                command.CommandText = "SELECT at, session_id, skills_loaded, errors FROM turns WHERE skills_loaded <> '' ORDER BY at, id";
                using var reader = command.ExecuteReader();
                var rows = new List<(DateTimeOffset, long, IReadOnlyList<string>, int)>();
                while (reader.Read())
                {
                    rows.Add((Parse(reader.GetString(0)), reader.GetInt64(1), SplitNames(reader.GetString(2)), reader.GetInt32(3)));
                }

                return rows;
            }
            catch (SqliteException ex)
            {
                Fail("read the skill loads", ex);
                return [];
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

    /// <summary><c>Session 12: turn appended</c> / <c>… (canceled)</c>. Pinned.</summary>
    public static string AppendedLogLine(long id, bool cancelled) =>
        string.Create(CultureInfo.InvariantCulture, $"Session {id}: turn appended{(cancelled ? " (canceled)" : "")}");

    /// <summary><c>Session 12: rewound to turn 3 (2 turns removed)</c> (2026-09-30, <c>/rewind</c>). Pinned.</summary>
    public static string TruncatedLogLine(long id, int keepThrough, int removed) =>
        string.Create(CultureInfo.InvariantCulture, $"Session {id}: rewound to turn {keepThrough} ({removed} turn{(removed == 1 ? "" : "s")} removed)");

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

    // ── The message history (2026-09-25) ────────────────────────────────────

    /// <summary>
    /// The most lines <c>command_history</c> keeps; an append past it drops the oldest, and so does each open (an older file's longer
    /// history cut once). The input line's list keeps the same cap (<c>InputLine</c>). 1,000 until 2026-10-17, when it became 100 (the
    /// user's call, the list in memory capped with it). Pinned.
    /// </summary>
    public const int CommandHistoryCap = 100;

    /// <summary>
    /// The input line's Up/Down history as stored (2026-09-25, the user's ask: <c>Keep message history</c>): the lines
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
                Fail("read the message history", ex);
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

    /// <summary>
    /// Lines appended in order (<c>/cmdcopy --history</c>, 2026-09-25; <c>/profile push|pull history</c> since 2026-10-09) — or in place of every stored one when
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

                TrimCommandHistory(connection, transaction);
                transaction.Commit();
                return added;
            }
            catch (SqliteException ex)
            {
                Fail("write the message history", ex);
                return 0;
            }
        }
    }

    /// <summary>Every stored line removed (<c>/msgclear</c>, and a load with <c>Keep message history</c> off); how many went.</summary>
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
                Fail("clear the message history", ex);
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
            // A private delete's tidy still owed is tried once more first (2026-11-26, the review's #74): Open() refuses a disposed store, and the
            // connection is about to close. A failure is logged and the close goes on.
            try
            {
                if (_tidyOwed && Open() is { } owed)
                {
                    Tidy(owed);
                }
            }
            catch (Exception ex) when (ex is SqliteException or InvalidOperationException or IOException)
            {
                DiagnosticLog.Warn(Category, TidyFailedWarning(ex.Message));
            }

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
        new(reader.GetInt64(0), Parse(reader.GetString(1)), Parse(reader.GetString(2)), reader.GetString(3), Source(reader.GetString(4)), reader.GetString(5), reader.GetInt32(6),
            reader.IsDBNull(7) ? null : reader.GetString(7));

    /// <summary>The summary's columns, the folder eighth (a NULL in its place while the store couldn't add it). Caller holds the lock.</summary>
    private string SummaryColumns => "id, started_at, updated_at, title, title_source, model, turns, " + (_extraColumns ? "cwd" : "NULL");

    /// <summary>A turn's timed output for the speed columns: the milliseconds generating and the tokens they cover, or nulls when untimed.</summary>
    private static (long? Ms, long? Timed) Speed(Llm.TokenUsage? usage)
    {
        if (usage is not { } u || u.Generating <= TimeSpan.Zero)
        {
            return (null, null);
        }

        long timed = u.Output - u.UntimedOutput;
        return timed > 0 ? ((long)u.Generating.TotalMilliseconds, timed) : (null, null);
    }

    /// <summary>
    /// The reflections of those sessions (<paramref name="turnsWhere"/> reads the same for both tables,
    /// plus <paramref name="reflectionsAlso"/> for the rows that belong to no session), then the
    /// turns, then the sessions, in one transaction; how many sessions went. Never the CASCADE's. Caller holds the lock.
    /// </summary>
    private int Delete(string turnsWhere, string sessionsWhere, (string Name, object Value)? parameter, string? reflectionsAlso = null, List<long>? ids = null)
    {
        Interlocked.Increment(ref _version);
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
            // The turns' sources (2026-11-16, the web-tools review's #160): turn_sources has no session_id, so through its turns.
            using var sources = connection.CreateCommand();
            sources.Transaction = transaction;
            sources.CommandText = "DELETE FROM turn_sources WHERE turn_id IN (SELECT id FROM turns " + turnsWhere + ")";
            using var turns = connection.CreateCommand();
            turns.Transaction = transaction;
            turns.CommandText = "DELETE FROM turns " + turnsWhere;
            using var sessions = connection.CreateCommand();
            sessions.Transaction = transaction;
            sessions.CommandText = "DELETE FROM sessions " + sessionsWhere;
            if (parameter is { } p)
            {
                reflections.Parameters.AddWithValue(p.Name, p.Value);
                sources.Parameters.AddWithValue(p.Name, p.Value);
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

            sources.ExecuteNonQuery();
            turns.ExecuteNonQuery();
            var gone = new List<long>();
            if (ids is not null)
            {
                // The ids the delete takes (2026-10-28, SessionStore.Purged), read in the same transaction.
                using var select = connection.CreateCommand();
                select.Transaction = transaction;
                select.CommandText = "SELECT id FROM sessions " + sessionsWhere;
                if (parameter is { } s)
                {
                    select.Parameters.AddWithValue(s.Name, s.Value);
                }

                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    gone.Add(reader.GetInt64(0));
                }
            }

            int removed = sessions.ExecuteNonQuery();
            transaction.Commit();
            ids?.AddRange(gone);
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

            // The added columns (2026-11-21, feature ideas #4 and #7), best effort as the trim below: another copy holding the write lock
            // past the busy wait fails the add, and sessions must not go off over it. Without them the writes and reads leave them out for
            // this run, and the next open tries again.
            _extraColumns = AddColumns(connection, BeforeColumnAdds);

            // A history longer than the cap (a file from before 2026-10-17, when the cap went from 1,000 to 100) cut at the open, so the
            // counts, the reads and a profile copy see the cap at once. Best effort: a failed trim never turns sessions off.
            try
            {
                TrimCommandHistory(connection, null);
            }
            catch (SqliteException ex)
            {
                Fail("trim the message history", ex);
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

    /// <summary>
    /// The columns added at the open (2026-11-21; <c>turns.cwd</c> 2026-11-22, the review's #41; the reply's context, first-token wait and
    /// reasoning 2026-11-27, for <c>/usage</c>'s trend and records), each nullable.
    /// </summary>
    private static readonly (string Table, string Column, string Type)[] AddedColumns =
    [
        ("sessions", "cwd", "TEXT"), ("turns", "model", "TEXT"), ("turns", "output_ms", "INTEGER"), ("turns", "timed_output", "INTEGER"), ("turns", "cwd", "TEXT"),
        ("turns", "context_tokens", "INTEGER"), ("turns", "first_token_ms", "INTEGER"), ("turns", "reasoning_tokens", "INTEGER"),
    ];

    /// <summary>
    /// <see cref="AddedColumns"/>, each added when missing, in one transaction (the store's first column adds, 2026-11-21). Since 2026-11-22 (the
    /// review's #105 and #102): another copy that added them between the check and the add is a success, a busy file or a duplicate column is
    /// tried once more, and a failure is logged once, as a warning (sessions go on without the folder). False when they couldn't be added.
    /// </summary>
    private static bool AddColumns(SqliteConnection connection, Action? before)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                before?.Invoke();
                var missing = AddedColumns.Where(c => !Has(connection, c.Table, c.Column)).ToList();
                if (missing.Count == 0)
                {
                    return true;
                }

                using var transaction = connection.BeginTransaction();
                foreach (var (table, column, type) in missing)
                {
                    using var add = connection.CreateCommand();
                    add.Transaction = transaction;
                    add.CommandText = "ALTER TABLE " + table + " ADD COLUMN " + column + " " + type;
                    add.ExecuteNonQuery();
                }

                transaction.Commit();
                return true;
            }
            catch (SqliteException ex)
            {
                if (AllAdded(connection))
                {
                    return true;   // another copy's adds: as good as ours
                }

                bool again = ex.SqliteErrorCode is 5 or 6 || ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase);
                if (again && attempt == 1)
                {
                    continue;
                }

                DiagnosticLog.Warn(Category, ColumnsFailedWarning(ex.Message), ex);
                return false;
            }
        }
    }

    /// <summary>Logged (Warn, once an open) when the columns couldn't be added (#102). Pinned.</summary>
    public static string ColumnsFailedWarning(string reason) => $"Could not add the folder and model columns in {FileName}: {reason}";

    private static bool AllAdded(SqliteConnection connection)
    {
        try
        {
            return AddedColumns.All(c => Has(connection, c.Table, c.Column));
        }
        catch (SqliteException)
        {
            return false;
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

    /// <summary><c>command_history</c> cut to the newest <see cref="CommandHistoryCap"/> lines: after each append and at each open.</summary>
    private static void TrimCommandHistory(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var trim = connection.CreateCommand();
        trim.Transaction = transaction;
        trim.CommandText = "DELETE FROM command_history WHERE id <= (SELECT id FROM command_history ORDER BY id DESC LIMIT 1 OFFSET $cap)";
        trim.Parameters.AddWithValue("$cap", CommandHistoryCap);
        trim.ExecuteNonQuery();
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

    private int _saveFailures;
    private string? _lastSaveFailure;

    /// <summary>
    /// How many writes of a session's row failed (2026-11-22, the review's #125): a begin, a turn, a history save, a truncate; never a read, the
    /// message history's trim at open, or a store that never opened. Headless counts a rise as a failed run.
    /// </summary>
    public int SaveFailures => Volatile.Read(ref _saveFailures);

    /// <summary>The last such write's reason, for the run's summary.</summary>
    public string? LastSaveFailure => Volatile.Read(ref _lastSaveFailure);

    private void SaveFailed(string what, SqliteException ex)
    {
        Fail(what, ex);
        Volatile.Write(ref _lastSaveFailure, ex.Message);
        Interlocked.Increment(ref _saveFailures);
    }

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
        CREATE TABLE IF NOT EXISTS turn_sources(
            turn_id INTEGER NOT NULL REFERENCES turns(id) ON DELETE CASCADE,
            seq INTEGER NOT NULL,
            kind TEXT NOT NULL,
            url TEXT NOT NULL,
            title TEXT NOT NULL,
            PRIMARY KEY (turn_id, seq));
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
