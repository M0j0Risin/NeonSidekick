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
/// <c>&lt;home&gt;\skills.db</c> (2026-09-30, the user's ask): one row per global or profile skill, holding where it lives, when it
/// was created, modified and last used, and a category reserved for sorting skills into subfolders later. It lives at the home
/// rather than in a profile's <c>sessions.db</c> (the user's pick). A global skill is shared by every profile, so its one row's
/// last use counts loads from any of them, and a purge from one profile never deletes a skill another one used.
/// <para>The shape is <see cref="SessionStore"/>'s: one connection opened lazily under one lock, WAL, a <c>meta</c> schema number a
/// file must match or the store stays off (one Warning), and every statement's failure logged and swallowed. The skills never
/// stop working because of the store; only their record is lost. Moments are <see cref="SessionStore.Stamp"/>'s ISO-8601 UTC
/// text, which sorts as time. Pure SQL: what to record and when is <see cref="SkillRecords"/>'.</para>
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
    /// <paramref name="at"/>, never used. A row left by an earlier folder of the same name starts over.
    /// </summary>
    public void Created(SkillScope scope, string profile, string folder, string name, DateTimeOffset at) =>
        Write("record a created skill",
            "INSERT INTO skills(scope, profile, folder, name, category, created_at, modified_at, last_used_at) VALUES ($scope, $profile, $folder, $name, NULL, $at, $at, NULL) " +
            "ON CONFLICT(scope, profile, folder) DO UPDATE SET name = excluded.name, created_at = excluded.created_at, modified_at = excluded.modified_at, last_used_at = NULL",
            scope, profile, folder, command =>
            {
                command.Parameters.AddWithValue("$name", name);
                command.Parameters.AddWithValue("$at", SessionStore.Stamp(at));
            });

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
                pragmas.CommandText = "PRAGMA journal_mode = WAL;";
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
        """;
}
