using System.Globalization;
using Microsoft.Data.Sqlite;
using NeonSidekick.Diagnostics;
using NeonSidekick.Sessions;

namespace NeonSidekick.Skills;

/// <summary>
/// One skill as <c>skills.db</c> remembers it (2026-09-30). <paramref name="Scope"/> is global or profile; external skills are
/// never recorded. <paramref name="Profile"/> is the profile's name for a profile skill, empty for a global one.
/// <paramref name="Folder"/> is the folder name, which is the match key. <paramref name="Name"/> is the frontmatter name when last
/// seen. <paramref name="Category"/> is null for now. The three moments are when the skill was created, last modified and last
/// loaded (null while it never was).
/// </summary>
public sealed record SkillRecord(long Id, SkillScope Scope, string Profile, string Folder, string Name, string? Category, DateTimeOffset Created, DateTimeOffset Modified, DateTimeOffset? LastUsed)
{
    /// <summary>What a purge measures age from: the last use, or the last change for a skill never loaded (the user's pick).</summary>
    public DateTimeOffset Reference => LastUsed ?? (Modified > Created ? Modified : Created);
}

/// <summary>
/// The words of <c>skill_events.kind</c> (2026-10-02, the reflection audit): what happened to a skill. <see cref="Written"/> are the
/// kinds that changed its files, the ones a reflection's cooldown and the usage line's write count read.
/// </summary>
public static class SkillEventKinds
{
    public const string Created = "created";
    public const string Updated = "updated";

    /// <summary>A supporting file written or edited beside the SKILL.md.</summary>
    public const string File = "file";

    /// <summary><c>/skills add</c> put it there, or replaced it from the same origin.</summary>
    public const string Installed = "installed";

    /// <summary>The reconcile found its SKILL.md written since the app last did: an edit the app did not make.</summary>
    public const string HandEdit = "hand-edit";

    /// <summary>A turn loaded it (one event per turn, with the errors after the load); a <c>/botchat</c> preload did too until 2026-10-04.</summary>
    public const string Used = "used";

    /// <summary><c>/skills revert</c> (or the pane's revert) put an earlier version back.</summary>
    public const string Reverted = "reverted";

    /// <summary>The kinds that changed the skill's files.</summary>
    public static readonly IReadOnlyList<string> Written = [Created, Updated, File];
}

/// <summary>The words of <c>skill_events.actor</c> and <c>skill_revisions.actor</c> (2026-10-02): who did it.</summary>
public static class SkillActors
{
    /// <summary>The main chat's <c>skill_editor</c>, the model in a turn.</summary>
    public const string Model = "model";

    /// <summary>A reflection's <c>skill_editor</c> (<see cref="SkillLearner"/>).</summary>
    public const string Reflection = "reflection";

    /// <summary>The user: an edit outside the app, a revert, a load the turn asked for.</summary>
    public const string User = "user";

    /// <summary><c>/skills add</c>.</summary>
    public const string Install = "install";
}

/// <summary>One row of <c>skill_events</c> (2026-10-02): the skill's row id, when, what, who, in which profile and session, the errors a turn hit after loading it, and a detail (a change's summary, a file's path, an install's origin).</summary>
public sealed record SkillEvent(long Id, long SkillId, DateTimeOffset At, string Kind, string Actor, string Profile, long? SessionId, int? ErrorsAfter, string Detail);

/// <summary>
/// One row of <c>skill_revisions</c> (2026-10-02): the text a file of the skill held before an app write replaced it. <paramref name="Path"/>
/// is <c>SKILL.md</c> or the supporting file's path relative to the folder; <paramref name="Content"/> is null when the write created the file.
/// </summary>
public sealed record SkillRevision(long Id, long SkillId, DateTimeOffset At, string Path, string? Content, string Actor);

/// <summary>How the turns used one skill, read from its <c>used</c> events: the loads, across how many sessions, how many of those turns hit an error after the load, and the last one.</summary>
public sealed record SkillUseFacts(int Loads, int Sessions, int FollowedByErrors, DateTimeOffset LastAt);

/// <summary>The newest write of one actor in a profile's view (the reflection's cooldown mark): the event and the skill's row as it is now, so a rename since is followed.</summary>
public sealed record SkillWriteMark(SkillEvent Event, SkillRecord Skill);

/// <summary>
/// <c>&lt;home&gt;\skills.db</c> (2026-09-30, the user's ask): one row per global or profile skill, holding where it lives, when it
/// was created, modified and last used, and a category reserved for sorting skills into subfolders later. It lives at the home
/// rather than in a profile's <c>sessions.db</c> (the user's pick). A global skill is shared by every profile, so its one row's
/// last use counts loads from any of them, and a purge from one profile never deletes a skill another one used.
/// <para>The shape is <see cref="SessionStore"/>'s: one connection opened lazily under one lock, WAL, a <c>meta</c> schema number a
/// file must match or the store stays off (one Warning), and every statement's failure logged and swallowed. The skills never
/// stop working because of the store; only their record is lost. Moments are <see cref="SessionStore.Stamp"/>'s ISO-8601 UTC
/// text, which sorts as time. Pure SQL: what to record and when is <see cref="SkillRecords"/>'.</para>
/// <para>Since 2026-10-02 (the reflection audit, the user's call: the skills' history belongs with the skills, not with the sessions a
/// purge removes) two more tables hang off a row by its id, both deleted with it (<c>foreign_keys</c> on): <c>skill_events</c>, what
/// happened to the skill and who did it (<see cref="SkillEventKinds"/>, <see cref="SkillActors"/>), and <c>skill_revisions</c>, the text
/// a file held before an app write replaced it, the newest <see cref="MaxRevisions"/> per skill, for <c>/skills revert</c>. Added tables
/// are <c>IF NOT EXISTS</c> on every open, so the schema number stays 1 (<c>sessions.db</c>'s <c>command_history</c> precedent).</para>
/// </summary>
public sealed class SkillRecordStore : IDisposable
{
    public const string FileName = "skills.db";

    /// <summary>The store's schema number in <c>meta</c>; a file at any other is refused (<see cref="SchemaMismatchWarning"/>).</summary>
    public const int SchemaVersion = 1;

    /// <summary>Logged (Warn) when the file's stored schema is not <see cref="SchemaVersion"/>: the skills go unrecorded, the file untouched. Pinned.</summary>
    public static string SchemaMismatchWarning(long stored) =>
        $"{FileName} is at schema {stored.ToString(CultureInfo.InvariantCulture)}, this version reads {SchemaVersion.ToString(CultureInfo.InvariantCulture)}; skill records are off until it is moved away.";

    private readonly object _gate = new();
    private readonly string _filePath;
    private SqliteConnection? _connection;
    private bool _failed;
    private bool _disposed;

    /// <param name="directory">The home directory; the file is <see cref="FileName"/> under it.</param>
    public SkillRecordStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _filePath = Path.Combine(Path.GetFullPath(directory), FileName);
    }

    public string FilePath => _filePath;

    /// <summary>False once the file failed to open or carries another schema: every read is empty and every write ignored from then on.</summary>
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

    /// <summary>The rows of <paramref name="profile"/>'s view: every global skill, then that profile's own, each by folder.</summary>
    public IReadOnlyList<SkillRecord> List(string profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return Read("SELECT id, scope, profile, folder, name, category, created_at, modified_at, last_used_at FROM skills WHERE scope = $global OR (scope = $profileScope AND profile = $profile) ORDER BY scope, folder",
            command =>
            {
                command.Parameters.AddWithValue("$global", SkillScopes.GlobalName);
                command.Parameters.AddWithValue("$profileScope", SkillScopes.ProfileName);
                command.Parameters.AddWithValue("$profile", profile);
            });
    }

    /// <summary>Every row, global and every profile's, for the tests and the smoke check.</summary>
    public IReadOnlyList<SkillRecord> All() =>
        Read("SELECT id, scope, profile, folder, name, category, created_at, modified_at, last_used_at FROM skills ORDER BY scope, profile, folder", null);

    /// <summary>The profile names that have rows.</summary>
    public IReadOnlyList<string> Profiles()
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
                command.CommandText = "SELECT DISTINCT profile FROM skills WHERE scope = $scope ORDER BY profile";
                command.Parameters.AddWithValue("$scope", SkillScopes.ProfileName);
                using var reader = command.ExecuteReader();
                var names = new List<string>();
                while (reader.Read())
                {
                    names.Add(reader.GetString(0));
                }

                return names;
            }
            catch (SqliteException ex)
            {
                Fail("list the profiles", ex);
                return [];
            }
        }
    }

    /// <summary>
    /// A row for a skill found on disk with none (the reconcile): its file's moments, never used, no category. False when one
    /// was there already or the store is off.
    /// </summary>
    public bool Insert(SkillScope scope, string profile, string folder, string name, DateTimeOffset created, DateTimeOffset modified) =>
        Write("insert a skill",
            "INSERT OR IGNORE INTO skills(scope, profile, folder, name, category, created_at, modified_at, last_used_at) VALUES ($scope, $profile, $folder, $name, NULL, $created, $modified, NULL)",
            scope, profile, folder, command =>
            {
                command.Parameters.AddWithValue("$name", name);
                command.Parameters.AddWithValue("$created", SessionStore.Stamp(created));
                command.Parameters.AddWithValue("$modified", SessionStore.Stamp(modified));
            }) > 0;

    /// <summary>
    /// The app made the skill (<c>skill_editor</c>'s create, a reflection's, <c>/skills add</c>'s install): created and modified at
    /// <paramref name="at"/>, never used. A row left by an earlier folder of the same name starts over, its events and revisions
    /// with it (2026-10-02): they were another skill's.
    /// </summary>
    public void Created(SkillScope scope, string profile, string folder, string name, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(folder);
        lock (_gate)
        {
            if (Open() is not { } connection)
            {
                return;
            }

            try
            {
                using var transaction = connection.BeginTransaction();
                foreach (string table in new[] { "skill_events", "skill_revisions" })
                {
                    using var clear = connection.CreateCommand();
                    clear.Transaction = transaction;
                    clear.CommandText = "DELETE FROM " + table + " WHERE skill_id IN (SELECT id FROM skills WHERE scope = $scope AND profile = $profile AND folder = $folder)";
                    Key(clear, scope, profile, folder);
                    clear.ExecuteNonQuery();
                }

                using (var upsert = connection.CreateCommand())
                {
                    upsert.Transaction = transaction;
                    upsert.CommandText = "INSERT INTO skills(scope, profile, folder, name, category, created_at, modified_at, last_used_at) VALUES ($scope, $profile, $folder, $name, NULL, $at, $at, NULL) " +
                        "ON CONFLICT(scope, profile, folder) DO UPDATE SET name = excluded.name, created_at = excluded.created_at, modified_at = excluded.modified_at, last_used_at = NULL";
                    Key(upsert, scope, profile, folder);
                    upsert.Parameters.AddWithValue("$name", name);
                    upsert.Parameters.AddWithValue("$at", SessionStore.Stamp(at));
                    upsert.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch (SqliteException ex)
            {
                Fail("record a created skill", ex);
            }
        }
    }

    /// <summary>The app changed the skill (an update, a bundled file written, an install over it): modified at <paramref name="at"/>; a skill with no row gets one created then.</summary>
    public void Modified(SkillScope scope, string profile, string folder, string name, DateTimeOffset at) =>
        Write("record a modified skill",
            "INSERT INTO skills(scope, profile, folder, name, category, created_at, modified_at, last_used_at) VALUES ($scope, $profile, $folder, $name, NULL, $at, $at, NULL) " +
            "ON CONFLICT(scope, profile, folder) DO UPDATE SET name = excluded.name, modified_at = excluded.modified_at",
            scope, profile, folder, command =>
            {
                command.Parameters.AddWithValue("$name", name);
                command.Parameters.AddWithValue("$at", SessionStore.Stamp(at));
            });

    /// <summary>The skill was loaded at <paramref name="at"/>; a skill with no row gets one, created and modified then too.</summary>
    public void Used(SkillScope scope, string profile, string folder, string name, DateTimeOffset at) =>
        Write("record a used skill",
            "INSERT INTO skills(scope, profile, folder, name, category, created_at, modified_at, last_used_at) VALUES ($scope, $profile, $folder, $name, NULL, $at, $at, $at) " +
            "ON CONFLICT(scope, profile, folder) DO UPDATE SET last_used_at = excluded.last_used_at",
            scope, profile, folder, command =>
            {
                command.Parameters.AddWithValue("$name", name);
                command.Parameters.AddWithValue("$at", SessionStore.Stamp(at));
            });

    /// <summary>A later file time the reconcile found: modified moves to <paramref name="modified"/>, the name to <paramref name="name"/>.</summary>
    public void Refresh(long id, string name, DateTimeOffset modified) =>
        WriteById("refresh a skill", "UPDATE skills SET name = $name, modified_at = $modified WHERE id = $id", id, command =>
        {
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$modified", SessionStore.Stamp(modified));
        });

    /// <summary>
    /// The pane moved the folder to the other root, or renamed it: the row follows with its moments and category, and a row
    /// already at the destination (a folder deleted outside the app) gives way. Nothing without a row.
    /// </summary>
    public void Move(SkillScope fromScope, string fromProfile, string fromFolder, SkillScope toScope, string toProfile, string toFolder, string name)
    {
        lock (_gate)
        {
            if (Open() is not { } connection)
            {
                return;
            }

            try
            {
                using var transaction = connection.BeginTransaction();
                using (var clear = connection.CreateCommand())
                {
                    clear.Transaction = transaction;
                    clear.CommandText = "DELETE FROM skills WHERE scope = $scope AND profile = $profile AND folder = $folder AND NOT (scope = $fromScope AND profile = $fromProfile AND folder = $fromFolder)";
                    Key(clear, toScope, toProfile, toFolder);
                    Key(clear, fromScope, fromProfile, fromFolder, "from");
                    clear.ExecuteNonQuery();
                }

                using (var move = connection.CreateCommand())
                {
                    move.Transaction = transaction;
                    move.CommandText = "UPDATE skills SET scope = $scope, profile = $profile, folder = $folder, name = $name WHERE scope = $fromScope AND profile = $fromProfile AND folder = $fromFolder";
                    Key(move, toScope, toProfile, toFolder);
                    Key(move, fromScope, fromProfile, fromFolder, "from");
                    move.Parameters.AddWithValue("$name", name);
                    move.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch (SqliteException ex)
            {
                Fail("move a skill", ex);
            }
        }
    }

    /// <summary>The skill's folder went (the pane's delete, a purge): its row goes. False when there was none.</summary>
    public bool Delete(SkillScope scope, string profile, string folder) =>
        Write("delete a skill", "DELETE FROM skills WHERE scope = $scope AND profile = $profile AND folder = $folder", scope, profile, folder, null) > 0;

    /// <summary>The reconcile found the row's folder gone.</summary>
    public void Delete(long id) => WriteById("delete a skill", "DELETE FROM skills WHERE id = $id", id, null);

    /// <summary>A profile that no longer exists: its rows go. The count removed.</summary>
    public int DeleteProfile(string profile) =>
        Write("delete a profile's skills", "DELETE FROM skills WHERE scope = $scope AND profile = $profile", SkillScope.Profile, profile, "", null);

    /// <summary>A profile renamed: its rows follow it, keeping their moments.</summary>
    public int RenameProfile(string from, string to)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(to);
        return Write("rename a profile's skills", "UPDATE skills SET profile = $to WHERE scope = $scope AND profile = $profile", SkillScope.Profile, from, "",
            command => command.Parameters.AddWithValue("$to", to));
    }

    /// <summary>How many revisions one skill keeps (2026-10-02): the oldest goes when an eleventh comes.</summary>
    public const int MaxRevisions = 10;

    /// <summary>The longest text a revision keeps (2026-10-02); a longer file's earlier version is not kept (a Debug line says so).</summary>
    public const int MaxRevisionChars = 256 * 1024;

    /// <summary>The row of one skill, or null when it has none (or the store is off).</summary>
    public SkillRecord? Find(SkillScope scope, string profile, string folder)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(folder);
        var rows = Read("SELECT id, scope, profile, folder, name, category, created_at, modified_at, last_used_at FROM skills WHERE scope = $scope AND profile = $profile AND folder = $folder",
            command => Key(command, scope, profile, folder));
        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>
    /// One event for the skill keyed by <paramref name="scope"/>, <paramref name="profile"/> and <paramref name="folder"/> (2026-10-02).
    /// <paramref name="eventProfile"/> is the profile it happened in (a global skill's row has none). False when the skill has no row.
    /// </summary>
    public bool AddEvent(SkillScope scope, string profile, string folder, string kind, string actor, string eventProfile, DateTimeOffset at, long? sessionId = null, int? errorsAfter = null, string detail = "") =>
        Write("record a skill event",
            "INSERT INTO skill_events(skill_id, at, kind, actor, profile, session_id, errors_after, detail) " +
            "SELECT id, $at, $kind, $actor, $eventProfile, $session, $errors, $detail FROM skills WHERE scope = $scope AND profile = $profile AND folder = $folder",
            scope, profile, folder, command => BindEvent(command, kind, actor, eventProfile, at, sessionId, errorsAfter, detail)) > 0;

    /// <summary>One event for the row <paramref name="skillId"/> (the reconcile's hand edit).</summary>
    public void AddEvent(long skillId, string kind, string actor, string eventProfile, DateTimeOffset at, string detail = "") =>
        WriteById("record a skill event",
            "INSERT INTO skill_events(skill_id, at, kind, actor, profile, session_id, errors_after, detail) SELECT id, $at, $kind, $actor, $eventProfile, NULL, NULL, $detail FROM skills WHERE id = $id",
            skillId, command => BindEvent(command, kind, actor, eventProfile, at, null, null, detail));

    private static void BindEvent(SqliteCommand command, string kind, string actor, string eventProfile, DateTimeOffset at, long? sessionId, int? errorsAfter, string detail)
    {
        command.Parameters.AddWithValue("$at", SessionStore.Stamp(at));
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$eventProfile", eventProfile);
        command.Parameters.AddWithValue("$session", sessionId is { } id ? id : DBNull.Value);
        command.Parameters.AddWithValue("$errors", errorsAfter is { } errors ? errors : DBNull.Value);
        command.Parameters.AddWithValue("$detail", detail);
    }

    /// <summary>
    /// The text <paramref name="path"/> held before an app write (2026-10-02), null for a file the write created; the skill's
    /// oldest revisions past <see cref="MaxRevisions"/> go in the same transaction. False when the skill has no row.
    /// </summary>
    public bool AddRevision(SkillScope scope, string profile, string folder, string path, string? content, string actor, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(path);
        lock (_gate)
        {
            if (Open() is not { } connection)
            {
                return false;
            }

            try
            {
                using var transaction = connection.BeginTransaction();
                int added;
                using (var insert = connection.CreateCommand())
                {
                    insert.Transaction = transaction;
                    insert.CommandText = "INSERT INTO skill_revisions(skill_id, at, path, content, actor) SELECT id, $at, $path, $content, $actor FROM skills WHERE scope = $scope AND profile = $profile AND folder = $folder";
                    Key(insert, scope, profile, folder);
                    insert.Parameters.AddWithValue("$at", SessionStore.Stamp(at));
                    insert.Parameters.AddWithValue("$path", path);
                    insert.Parameters.AddWithValue("$content", content is null ? DBNull.Value : content);
                    insert.Parameters.AddWithValue("$actor", actor);
                    added = insert.ExecuteNonQuery();
                }

                using (var trim = connection.CreateCommand())
                {
                    trim.Transaction = transaction;
                    trim.CommandText = "DELETE FROM skill_revisions WHERE skill_id IN (SELECT id FROM skills WHERE scope = $scope AND profile = $profile AND folder = $folder) " +
                        "AND id NOT IN (SELECT r.id FROM skill_revisions r JOIN skills s ON s.id = r.skill_id WHERE s.scope = $scope AND s.profile = $profile AND s.folder = $folder ORDER BY r.id DESC LIMIT $max)";
                    Key(trim, scope, profile, folder);
                    trim.Parameters.AddWithValue("$max", MaxRevisions);
                    trim.ExecuteNonQuery();
                }

                transaction.Commit();
                return added > 0;
            }
            catch (SqliteException ex)
            {
                Fail("keep a skill revision", ex);
                return false;
            }
        }
    }

    /// <summary>The skill's revisions, newest first.</summary>
    public IReadOnlyList<SkillRevision> Revisions(long skillId)
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
                command.CommandText = "SELECT id, skill_id, at, path, content, actor FROM skill_revisions WHERE skill_id = $id ORDER BY id DESC";
                command.Parameters.AddWithValue("$id", skillId);
                using var reader = command.ExecuteReader();
                var rows = new List<SkillRevision>();
                while (reader.Read())
                {
                    rows.Add(new SkillRevision(reader.GetInt64(0), reader.GetInt64(1), Parse(reader.GetString(2)), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5)));
                }

                return rows;
            }
            catch (SqliteException ex)
            {
                Fail("read the skill revisions", ex);
                return [];
            }
        }
    }

    /// <summary>A revision put back (<c>/skills revert</c>): it leaves the list, so the next revert goes one further back.</summary>
    public void DeleteRevision(long id) => WriteById("drop a skill revision", "DELETE FROM skill_revisions WHERE id = $id", id, null);

    /// <summary>
    /// The skill's events in the order they were recorded (the id's, not the moment's: a hand edit carries its file's own time, which
    /// may be read after an app write stamped by the clock).
    /// </summary>
    public IReadOnlyList<SkillEvent> Events(long skillId) =>
        ReadEvents("SELECT id, skill_id, at, kind, actor, profile, session_id, errors_after, detail FROM skill_events WHERE skill_id = $id ORDER BY id",
            command => command.Parameters.AddWithValue("$id", skillId));

    /// <summary>How the turns used the skill (its <c>used</c> events); null when nothing loaded it.</summary>
    public SkillUseFacts? UseFacts(long skillId)
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
                command.CommandText = "SELECT count(*), count(DISTINCT CASE WHEN session_id IS NOT NULL THEN profile || '#' || session_id END), coalesce(sum(errors_after > 0), 0), max(at) " +
                    "FROM skill_events WHERE skill_id = $id AND kind = $used";
                command.Parameters.AddWithValue("$id", skillId);
                command.Parameters.AddWithValue("$used", SkillEventKinds.Used);
                using var reader = command.ExecuteReader();
                if (!reader.Read() || reader.GetInt32(0) == 0)
                {
                    return null;
                }

                return new SkillUseFacts(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), Parse(reader.GetString(3)));
            }
            catch (SqliteException ex)
            {
                Fail("read a skill's uses", ex);
                return null;
            }
        }
    }

    /// <summary>
    /// The newest event of <paramref name="actor"/> that wrote a skill (<see cref="SkillEventKinds.Written"/>) in <paramref name="profile"/>'s
    /// view — every global skill and the profile's own — with the skill's row as it is now; null when there is none.
    /// </summary>
    public SkillWriteMark? LastWrite(string profile, string actor)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(actor);
        var events = ReadEvents(
            "SELECT e.id, e.skill_id, e.at, e.kind, e.actor, e.profile, e.session_id, e.errors_after, e.detail FROM skill_events e JOIN skills s ON s.id = e.skill_id " +
            "WHERE e.actor = $actor AND e.kind IN ($created, $updated, $file) AND (s.scope = $global OR (s.scope = $profileScope AND s.profile = $profile)) ORDER BY e.at DESC, e.id DESC LIMIT 1",
            command =>
            {
                command.Parameters.AddWithValue("$actor", actor);
                command.Parameters.AddWithValue("$created", SkillEventKinds.Created);
                command.Parameters.AddWithValue("$updated", SkillEventKinds.Updated);
                command.Parameters.AddWithValue("$file", SkillEventKinds.File);
                command.Parameters.AddWithValue("$global", SkillScopes.GlobalName);
                command.Parameters.AddWithValue("$profileScope", SkillScopes.ProfileName);
                command.Parameters.AddWithValue("$profile", profile);
            });
        if (events.Count == 0)
        {
            return null;
        }

        var rows = Read("SELECT id, scope, profile, folder, name, category, created_at, modified_at, last_used_at FROM skills WHERE id = $id",
            command => command.Parameters.AddWithValue("$id", events[0].SkillId));
        return rows.Count == 0 ? null : new SkillWriteMark(events[0], rows[0]);
    }

    /// <summary>Whether the <c>meta</c> flag <paramref name="key"/> is set (the one-time import's mark).</summary>
    public bool HasFlag(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_gate)
        {
            if (Open() is not { } connection)
            {
                return false;
            }

            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT count(*) FROM meta WHERE key = $key";
                command.Parameters.AddWithValue("$key", key);
                return command.ExecuteScalar() is long count && count > 0;
            }
            catch (SqliteException ex)
            {
                Fail("read a flag", ex);
                return false;
            }
        }
    }

    /// <summary>Sets the <c>meta</c> flag <paramref name="key"/>.</summary>
    public void SetFlag(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_gate)
        {
            if (Open() is not { } connection)
            {
                return;
            }

            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT OR IGNORE INTO meta(key, value) VALUES ($key, 1)";
                command.Parameters.AddWithValue("$key", key);
                command.ExecuteNonQuery();
            }
            catch (SqliteException ex)
            {
                Fail("set a flag", ex);
            }
        }
    }

    private IReadOnlyList<SkillEvent> ReadEvents(string sql, Action<SqliteCommand> bind)
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
                command.CommandText = sql;
                bind(command);
                using var reader = command.ExecuteReader();
                var rows = new List<SkillEvent>();
                while (reader.Read())
                {
                    rows.Add(new SkillEvent(
                        reader.GetInt64(0),
                        reader.GetInt64(1),
                        Parse(reader.GetString(2)),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetString(5),
                        reader.IsDBNull(6) ? null : reader.GetInt64(6),
                        reader.IsDBNull(7) ? null : reader.GetInt32(7),
                        reader.GetString(8)));
                }

                return rows;
            }
            catch (SqliteException ex)
            {
                Fail("read the skill events", ex);
                return [];
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _connection?.Dispose();
            _connection = null;
        }
    }

    private static void Key(SqliteCommand command, SkillScope scope, string profile, string folder, string prefix = "")
    {
        string p = prefix.Length == 0 ? "$" : "$" + prefix;
        command.Parameters.AddWithValue(prefix.Length == 0 ? "$scope" : p + "Scope", SkillScopes.Name(scope));
        command.Parameters.AddWithValue(prefix.Length == 0 ? "$profile" : p + "Profile", scope == SkillScope.Global ? "" : profile);
        command.Parameters.AddWithValue(prefix.Length == 0 ? "$folder" : p + "Folder", folder);
    }

    private int Write(string what, string sql, SkillScope scope, string profile, string folder, Action<SqliteCommand>? bind)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(folder);
        lock (_gate)
        {
            if (Open() is not { } connection)
            {
                return 0;
            }

            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                Key(command, scope, profile, folder);
                bind?.Invoke(command);
                return command.ExecuteNonQuery();
            }
            catch (SqliteException ex)
            {
                Fail(what, ex);
                return 0;
            }
        }
    }

    private void WriteById(string what, string sql, long id, Action<SqliteCommand>? bind)
    {
        lock (_gate)
        {
            if (Open() is not { } connection)
            {
                return;
            }

            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.Parameters.AddWithValue("$id", id);
                bind?.Invoke(command);
                command.ExecuteNonQuery();
            }
            catch (SqliteException ex)
            {
                Fail(what, ex);
            }
        }
    }

    private IReadOnlyList<SkillRecord> Read(string sql, Action<SqliteCommand>? bind)
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
                command.CommandText = sql;
                bind?.Invoke(command);
                using var reader = command.ExecuteReader();
                var rows = new List<SkillRecord>();
                while (reader.Read())
                {
                    rows.Add(new SkillRecord(
                        reader.GetInt64(0),
                        reader.GetString(1) == SkillScopes.GlobalName ? SkillScope.Global : SkillScope.Profile,
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5),
                        Parse(reader.GetString(6)),
                        Parse(reader.GetString(7)),
                        reader.IsDBNull(8) ? null : Parse(reader.GetString(8))));
                }

                return rows;
            }
            catch (SqliteException ex)
            {
                Fail("read the skills", ex);
                return [];
            }
        }
    }

    private static DateTimeOffset Parse(string stamp) =>
        DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var moment) ? moment : DateTimeOffset.UnixEpoch;

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
                // foreign_keys (2026-10-02): a row's events and revisions go with it, whoever deletes it.
                pragmas.CommandText = "PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON;";
                pragmas.ExecuteNonQuery();
            }

            // The version is read before Schema runs, so a file this code does not know is refused untouched (SessionStore's rule).
            using (var version = connection.CreateCommand())
            {
                version.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'meta'";
                bool hasMeta = version.ExecuteScalar() is long tables && tables > 0;
                version.CommandText = "SELECT value FROM meta WHERE key = 'schema'";
                if (hasMeta && version.ExecuteScalar() is long stored && stored != SchemaVersion)
                {
                    connection.Dispose();
                    _failed = true;
                    DiagnosticLog.Warn(SkillCatalog.Category, SchemaMismatchWarning(stored));
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
            DiagnosticLog.Warn(SkillCatalog.Category, $"Could not open {FileName}; skill records are off: {ex.Message}", ex);
            return null;
        }
    }

    private static void Fail(string what, SqliteException ex) =>
        DiagnosticLog.Error(SkillCatalog.Category, $"Could not {what} in {FileName}: {ex.Message}", ex);

    /// <summary>
    /// The schema, every statement <c>IF NOT EXISTS</c>. <c>folder</c> compares without case (a Windows folder name), so the key
    /// matches the file system's. <c>profile</c> is empty for a global row, so the key is unique without a NULL in it.
    /// </summary>
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value INTEGER NOT NULL);
        INSERT OR IGNORE INTO meta(key, value) VALUES ('schema', 1);
        CREATE TABLE IF NOT EXISTS skills(
            id INTEGER PRIMARY KEY,
            scope TEXT NOT NULL,
            profile TEXT NOT NULL DEFAULT '',
            folder TEXT NOT NULL COLLATE NOCASE,
            name TEXT NOT NULL,
            category TEXT,
            created_at TEXT NOT NULL,
            modified_at TEXT NOT NULL,
            last_used_at TEXT,
            UNIQUE(scope, profile, folder));
        CREATE TABLE IF NOT EXISTS skill_events(
            id INTEGER PRIMARY KEY,
            skill_id INTEGER NOT NULL REFERENCES skills(id) ON DELETE CASCADE,
            at TEXT NOT NULL,
            kind TEXT NOT NULL,
            actor TEXT NOT NULL,
            profile TEXT NOT NULL DEFAULT '',
            session_id INTEGER,
            errors_after INTEGER,
            detail TEXT NOT NULL DEFAULT '');
        CREATE INDEX IF NOT EXISTS skill_events_skill ON skill_events(skill_id, at);
        CREATE TABLE IF NOT EXISTS skill_revisions(
            id INTEGER PRIMARY KEY,
            skill_id INTEGER NOT NULL REFERENCES skills(id) ON DELETE CASCADE,
            at TEXT NOT NULL,
            path TEXT NOT NULL,
            content TEXT,
            actor TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS skill_revisions_skill ON skill_revisions(skill_id, id);
        """;
}
