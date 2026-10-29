using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LibGit2Sharp;
using NeonSidekick.Diagnostics;
using NeonSidekick.Settings;

namespace NeonSidekick.Files;

/// <summary>The three checkpoint limits the settings give (2026-10-28, the user's picks), read per turn and at each sweep.</summary>
/// <param name="KeepDays">Checkpoints older than this many days are cleared at the sweep; 0 = never by age.</param>
/// <param name="MaxBytes">The saved bytes one folder's checkpoints may hold; the oldest go first past it, the newest always stays.</param>
/// <param name="LargestFileBytes">A file over it is still changed, but its bytes aren't kept.</param>
public sealed record CheckpointLimits(int KeepDays, long MaxBytes, long LargestFileBytes)
{
    /// <summary>
    /// The limits <paramref name="settings"/> set, each held to its setting's range (2026-10-28, review P3-05: a hand-edited profile's huge
    /// keep-days threw in the sweep, a max of 0 dropped all but the newest, a −1 per file kept nothing).
    /// </summary>
    public static CheckpointLimits From(AppSettingsData settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new CheckpointLimits(
            Math.Clamp(settings.FileCheckpointKeepDays, AppSettingsData.MinFileCheckpointKeepDays, AppSettingsData.MaxFileCheckpointKeepDays),
            Math.Clamp(settings.FileCheckpointMaxMegabytes, AppSettingsData.MinFileCheckpointMaxMegabytes, AppSettingsData.MaxFileCheckpointMaxMegabytes) * 1024L * 1024L,
            Math.Clamp(settings.FileCheckpointLargestFileMegabytes, AppSettingsData.MinFileCheckpointLargestFileMegabytes, AppSettingsData.MaxFileCheckpointLargestFileMegabytes) * 1024L * 1024L);
    }

    /// <summary>The compiled defaults.</summary>
    public static CheckpointLimits Default => From(new AppSettingsData());
}

/// <summary>Why a kept checkpoint can't be read by this build.</summary>
public enum CheckpointProblem
{
    None,

    /// <summary>Its JSON's version is newer than <see cref="CheckpointRecord.CurrentVersion"/>: left alone for the build that can.</summary>
    NewerVersion,

    /// <summary>Its JSON doesn't parse, or a blob it names is missing.</summary>
    Damaged,
}

/// <summary>
/// A kept checkpoint found in one store: its record (null when <see cref="Problem"/> says it can't be read), and its commit's id, which
/// <see cref="ICheckpointStore.OpenBlob"/> looks up directly (2026-10-28, review P5-05).
/// </summary>
public sealed record CheckpointFound(string Id, string Root, string Store, CheckpointRecord? Record, CheckpointProblem Problem, string? Commit = null);

/// <summary>One store on disk: the folder it keeps, its path, its size, and whether a turn is writing to it.</summary>
public sealed record CheckpointStoreInfo(string Root, string Path, long Bytes, bool Busy);

/// <summary>
/// What a purge did: stores deleted, the bytes freed, the folders skipped as busy, those that couldn't be deleted, and (2026-10-28, review
/// P13-02) those set aside but not yet deleted when the purge was stopped, which the next sweep finishes.
/// </summary>
public sealed record CheckpointPurgeResult(int Purged, long Bytes, IReadOnlyList<string> Busy, IReadOnlyList<string> Failed)
{
    /// <summary>The folders whose stores were set aside for deleting but not deleted yet (stopped): the sweep finishes them.</summary>
    public IReadOnlyList<string> Pending { get; init; } = [];
}

/// <summary>
/// The file checkpoints' store (2026-10-28, the checkpoints plan, Option 1, the user's pick): made once in <c>Program.cs</c> and handed to
/// the screen and headless like the bin, so a test passes <c>Fakes/FakeCheckpointStore</c> or none. Every call takes the profile folder, so
/// one instance serves every profile.
/// </summary>
public interface ICheckpointStore
{
    /// <summary>A writer for <paramref name="root"/>'s store (made when missing) for turn <paramref name="checkpointId"/>; null on a store failure (logged).</summary>
    ICheckpointWriter? Begin(string profileDirectory, string root, string checkpointId);

    /// <summary>Commits <paramref name="record"/> with the writer's saved blobs, then retention and the prune; in the background in the app.</summary>
    void Commit(ICheckpointWriter writer, CheckpointRecord record, CheckpointLimits limits);

    /// <summary>Drops a writer whose turn kept nothing (its marker goes; the prune clears its blobs).</summary>
    void Abandon(ICheckpointWriter writer);

    /// <summary>Waits for every commit and every queued removal still running; never faults (a failure is logged where it happens).</summary>
    Task FlushAsync();

    /// <summary>Waits at most <paramref name="wait"/> for every commit still running (the app's exit); false when one was left.</summary>
    bool FlushPending(TimeSpan wait);

    /// <summary>The kept checkpoints with these ids, in every store of the profile (a pair's twin too).</summary>
    IReadOnlyList<CheckpointFound> Find(string profileDirectory, IReadOnlyCollection<string> ids);

    /// <summary>A saved file's bytes (<see cref="CheckpointEntry.Blob"/>), streamed from the store and closing it with the stream; null when missing.</summary>
    Stream? OpenBlob(CheckpointFound found, string name);

    /// <summary>Removes these checkpoints' refs in every store (spent, or dropped by a rewind); a ref already gone is no error.</summary>
    void Remove(string profileDirectory, IReadOnlyCollection<string> ids);

    /// <summary>
    /// Removes every checkpoint of these sessions (their invariant numbers) in every store: queued behind the commits still running, so it is
    /// off the caller's flow and a commit landing after it can't leave a ref behind (2026-10-28, review P4-06, P5-13); <see cref="FlushAsync"/> waits for it.
    /// </summary>
    void RemoveSessions(string profileDirectory, IReadOnlyCollection<string> sessions);

    /// <summary>The profile's stores, only those of <paramref name="roots"/> when given (sized after the filter, 2026-10-28, review P3-13).</summary>
    IReadOnlyList<CheckpointStoreInfo> Stores(string profileDirectory, IReadOnlyCollection<string>? roots = null);

    /// <summary>
    /// Deletes the stores of <paramref name="roots"/> (null = every store), for good; a busy store is skipped and named. The purged checkpoints'
    /// twins in the other stores go too (2026-10-28, review P2-09: a pair's picture half of the purged folder's turns).
    /// </summary>
    CheckpointPurgeResult Purge(string profileDirectory, IReadOnlyCollection<string>? roots, CancellationToken cancellationToken);

    /// <summary>
    /// The sweep at a profile's load: empty stores deleted, the age rule and retention applied, the prune run, purges finished, dead markers
    /// cleared; a missing folder's store is kept as long as <see cref="CheckpointLimits.KeepDays"/>, then cleared.
    /// </summary>
    void Sweep(string profileDirectory, CheckpointLimits limits);

    /// <summary>Re-keys the stores of a renamed profile's own folders (its files and images), after the profile folder moved.</summary>
    void RenameProfile(string oldProfileDirectory, string newProfileDirectory);
}

/// <summary>
/// The real store (2026-10-28, the checkpoints plan): one bare git repository per root under <c>&lt;profile&gt;/checkpoints/&lt;name&gt;-&lt;hash&gt;.git</c>,
/// written by LibGit2Sharp's object and ref calls only (no work tree, no index, no diff, no <c>Discover</c>: the AOT rule
/// <c>SmokeChecks.Git</c> records). A turn's saved files are blobs; its checkpoint is a parentless commit of a flat tree (<c>0001</c>… →
/// blob, plus <c>neon-checkpoint.json</c>) under <c>refs/checkpoints/&lt;session&gt;/&lt;id&gt;</c>, readable with
/// <c>git --git-dir=… show &lt;ref&gt;:neon-checkpoint.json</c>. The store's local config pins <c>core.autocrlf</c>, <c>core.safecrlf</c> and
/// <c>core.logAllRefUpdates</c> off and no blob is written with a hint path, so the machine's git config can't change a byte or ask for an
/// identity; the commits' author is a fixed local one. A turn's writer keeps one repository open for its saves, closed before its commit;
/// every other operation opens one and disposes it at once, so nothing stays locked.
/// <para>Busy (2026-10-28, review P1-01, P4-05, P2-10, P1-18): this process's writers each hold a lease on their store, from
/// <see cref="Begin"/> to their commit's end or <see cref="Abandon"/>; another process's turn shows by its <c>open/</c> marker, which names its
/// process and start, so a crash's marker is dead at once (an unreadable one after a day). A store is deleted only once claimed — renamed
/// to a unique <c>.purging</c> name under the lock while nothing holds it — so a turn that just began is never deleted under it. Retention keeps
/// the newest <see cref="MaxCheckpoints"/> and <c>File checkpoints max (MB)</c>; the prune then deletes loose objects no kept ref reaches,
/// older than an hour, while nothing else writes to the store, under <c>prune.lock</c> held open exclusively (released by the system on a
/// crash, review P8-03).</para>
/// </summary>
public sealed class CheckpointStore : ICheckpointStore
{
    /// <summary>The diagnostics category.</summary>
    public const string Category = FileJournal.Category;

    /// <summary>The profile's folder of stores.</summary>
    public const string FolderName = "checkpoints";

    /// <summary>The session part of a ref with <c>Session logging</c> off.</summary>
    public const string NoSession = "none";

    /// <summary>The ref namespace.</summary>
    public const string RefPrefix = "refs/checkpoints/";

    /// <summary>The op log's name in a checkpoint's tree.</summary>
    public const string JsonName = "neon-checkpoint.json";

    /// <summary>The file naming a store's folder.</summary>
    public const string FolderFile = "folder.txt";

    /// <summary>
    /// The file a store keeps once its folder is missing (2026-10-28, review P6-02): when it was first seen gone (UTC ticks); the store goes only
    /// after <c>File checkpoints keep (days)</c> since then, and the file goes when the folder is back.
    /// </summary>
    public const string GoneFile = "gone.txt";

    /// <summary>The file held while pruning; it holds the last prune's time (UTC ticks).</summary>
    public const string PruneLock = "prune.lock";

    /// <summary>The folder of a turn's markers.</summary>
    public const string OpenFolder = "open";

    /// <summary>A store being purged, renamed first so nothing opens it half deleted.</summary>
    public const string PurgingSuffix = ".purging";

    /// <summary>The newest checkpoints a store keeps.</summary>
    public const int MaxCheckpoints = 50;

    /// <summary>The most characters of a folder's name in its store's name.</summary>
    public const int MaxNameChars = 40;

    /// <summary>The commit message's trailer words (2026-10-28, review P1-11, P5-06): what retention reads, so it never parses a whole JSON.</summary>
    public const string SavedBytesTrailer = "saved-bytes: ";

    /// <summary>The commit's own time (UTC ticks), finer than the committer's second.</summary>
    public const string TimeTrailer = "time: ";

    private const string PidWord = "pid";

    private static readonly TimeSpan PruneAge = TimeSpan.FromHours(1);
    private static readonly TimeSpan MarkerAge = TimeSpan.FromHours(24);
    private static readonly Lazy<long?> s_ourStart = new(() => RecordFiles.StartTicks(Environment.ProcessId));

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly List<Task> _pending = new();
    private readonly Dictionary<string, List<string>> _leases = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="time">The clock for commit times and the prune's and markers' ages (<c>ManualTimeProvider</c> in the tests).</param>
    public CheckpointStore(TimeProvider time)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>The profile's folder of stores.</summary>
    public static string FolderOf(string profileDirectory) => Path.Combine(profileDirectory, FolderName);

    /// <summary>
    /// A root's store name without the <c>.git</c> and suffix: its folder's name cut to <see cref="MaxNameChars"/> (a drive or share root by what
    /// it has), then 8 hex of a SHA-256 of the full path case-folded.
    /// </summary>
    public static string BaseName(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string name = Path.GetFileName(full);
        if (name.Length == 0)
        {
            name = full.TrimEnd(':', '\\', '/');
        }

        var clean = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            clean.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ' ' ? c : '_');
        }

        string part = TextCut.Safe(clean.ToString().Trim().TrimEnd('.'), MaxNameChars);
        if (part.Length == 0)
        {
            part = "folder";
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(full.ToUpperInvariant()));
        return part + "-" + Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    // ---- writing a turn ----

    public ICheckpointWriter? Begin(string profileDirectory, string root, string checkpointId)
    {
        ArgumentNullException.ThrowIfNull(profileDirectory);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(checkpointId);
        try
        {
            // The store found, the marker written and the lease taken under the one lock a claim takes (review P1-18): a sweep can't
            // delete the store between them.
            lock (_gate)
            {
                string store = StoreFor(profileDirectory, root, create: true)!;
                string open = Path.Combine(store, OpenFolder);
                Directory.CreateDirectory(open);
                File.WriteAllText(Path.Combine(open, checkpointId), MarkerText(root));
                if (!_leases.TryGetValue(store, out var ids))
                {
                    ids = new List<string>();
                    _leases[store] = ids;
                }

                ids.Add(checkpointId);
                return new Writer(store, Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), checkpointId);
            }
        }
        catch (Exception ex) when (Failure(ex))
        {
            DiagnosticLog.Warn(Category, $"Could not open the checkpoint store for {root}: {ex.Message}");
            return null;
        }
    }

    /// <summary>A marker's text: this process and its start (<see cref="RecordFiles.Line"/>), then the root.</summary>
    private static string MarkerText(string root) =>
        RecordFiles.Line(PidWord, Environment.ProcessId, s_ourStart.Value ?? 0) + root;

    public void Commit(ICheckpointWriter writer, CheckpointRecord record, CheckpointLimits limits)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(limits);
        if (writer is not Writer ours)
        {
            return;
        }

        // The entries copied now (review P11-12): the commit names its blobs on its own copy, never on the journal's.
        var copy = Copy(record);
        lock (_gate)
        {
            Task task = null!;
            task = Task.Run(() =>
            {
                try
                {
                    CommitNow(ours, copy, limits);
                }
                catch (Exception ex)
                {
                    // Background work never faults FlushAsync (review P2-07): /undo, /rewind and a purge wait on it.
                    DiagnosticLog.Warn(Category, $"Checkpoint {copy.Id} for {copy.Root} failed: {ex.Message}");
                }
                finally
                {
                    Release(ours);
                    lock (_gate)
                    {
                        _pending.Remove(task);
                    }
                }
            });
            _pending.Add(task);
        }
    }

    private static CheckpointRecord Copy(CheckpointRecord record) => new()
    {
        Version = record.Version,
        Id = record.Id,
        Root = record.Root,
        Session = record.Session,
        Turn = record.Turn,
        Message = record.Message,
        Time = record.Time,
        Tools = record.Tools.ToList(),
        Entries = record.Entries.Select(e => e.Clone()).ToList(),
        After = record.After.ToList(),
        SavedBytes = record.SavedBytes,
        Incomplete = record.Incomplete,
    };

    public void Abandon(ICheckpointWriter writer)
    {
        if (writer is Writer ours)
        {
            Release(ours);
        }
    }

    /// <summary>A writer's repository closed, its marker dropped and its lease given back; once only.</summary>
    private void Release(Writer writer)
    {
        if (!writer.TryRelease())
        {
            return;
        }

        writer.Close();
        DropMarker(writer.Store, writer.Id);
        lock (_gate)
        {
            if (_leases.TryGetValue(writer.Store, out var ids))
            {
                ids.Remove(writer.Id);
                if (ids.Count == 0)
                {
                    _leases.Remove(writer.Store);
                }
            }
        }
    }

    public Task FlushAsync()
    {
        lock (_gate)
        {
            return Task.WhenAll(_pending.ToList());
        }
    }

    public bool FlushPending(TimeSpan wait)
    {
        try
        {
            return FlushAsync().Wait(wait);
        }
        catch (AggregateException ex)
        {
            DiagnosticLog.Warn(Category, $"A checkpoint commit failed: {ex.InnerException?.Message}");
            return true;
        }
    }

    private void CommitNow(Writer writer, CheckpointRecord record, CheckpointLimits limits)
    {
        writer.Close();
        string? committed = null;
        try
        {
            using (var repo = new Repository(writer.Store))
            {
                var tree = new TreeDefinition();
                var names = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var entry in record.Entries)
                {
                    if (entry.Blob is not { } id)
                    {
                        continue;
                    }

                    if (!names.TryGetValue(id, out string? name))
                    {
                        if (repo.Lookup<Blob>(new ObjectId(id)) is not { } blob)
                        {
                            // A saved file that isn't in the store (review P2-07): not kept, never a commit that throws.
                            entry.Blob = null;
                            entry.NotSaved = FileJournal.FailedReason;
                            record.SavedBytes = Math.Max(0, record.SavedBytes - entry.Size);
                            continue;
                        }

                        name = (names.Count + 1).ToString("D4", CultureInfo.InvariantCulture);
                        names[id] = name;
                        tree.Add(name, blob, Mode.NonExecutableFile);
                    }

                    entry.Blob = name;
                }

                byte[] json = JsonSerializer.SerializeToUtf8Bytes(record, CheckpointJsonContext.Default.CheckpointRecord);
                using (var stream = new MemoryStream(json))
                {
                    tree.Add(JsonName, repo.ObjectDatabase.CreateBlob(stream, json.LongLength), Mode.NonExecutableFile);
                }

                var made = repo.ObjectDatabase.CreateTree(tree);
                var now = _time.GetUtcNow();
                var signature = new Signature("NeonSidekick", "checkpoints@localhost", now);
                var commit = repo.ObjectDatabase.CreateCommit(signature, signature, MessageOf(record, now), made, [], prettifyMessage: false);
                committed = RefPrefix + record.Session + "/" + record.Id;
                repo.Refs.Add(committed, commit.Id, allowOverwrite: true);
            }

            DiagnosticLog.Debug(Category, $"Checkpoint {record.Id} kept for {record.Root} ({record.Entries.Count.ToString(CultureInfo.InvariantCulture)} entries).");
        }
        catch (Exception ex) when (Failure(ex))
        {
            DiagnosticLog.Warn(Category, $"Could not commit checkpoint {record.Id} for {record.Root}: {ex.Message}");
        }
        finally
        {
            DropMarker(writer.Store, writer.Id);
        }

        string profile = Path.GetDirectoryName(Path.GetDirectoryName(writer.Store)!)!;
        try
        {
            var removed = Retain(writer.Store, limits, 0, committed);
            if (removed.Count > 0)
            {
                Remove(profile, removed);
            }

            // This commit's own lease still held (own 1, review P1-01): the post-commit prune never ran before.
            Prune(writer.Store, own: 1, force: removed.Count > 0);
        }
        catch (Exception ex) when (Failure(ex))
        {
            DiagnosticLog.Warn(Category, $"Could not trim the checkpoints of {record.Root}: {ex.Message}");
        }
    }

    /// <summary>
    /// The commit's message: <c>turn 12 · 3 files · patch_file, delete</c>, then (2026-10-28, review P1-11, P5-06) a blank line and the
    /// trailers retention reads — <c>saved-bytes: N</c> and <c>time: &lt;ticks&gt;</c>.
    /// </summary>
    public static string MessageOf(CheckpointRecord record, DateTimeOffset? at = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        int files = record.Entries.Select(e => e.Path).Distinct(StringComparer.Ordinal).Count();
        long ticks = (at ?? record.Time).UtcTicks;
        return string.Create(CultureInfo.InvariantCulture,
            $"turn {record.Turn} · {files} files · {string.Join(", ", record.Tools)}\n\n{SavedBytesTrailer}{record.SavedBytes}\n{TimeTrailer}{ticks}\n");
    }

    /// <summary>A trailer's number from a commit message, or null.</summary>
    private static long? Trailer(string message, string word)
    {
        foreach (string line in message.Split('\n'))
        {
            if (line.StartsWith(word, StringComparison.Ordinal)
                && long.TryParse(line.AsSpan(word.Length).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long value))
            {
                return value;
            }
        }

        return null;
    }

    // ---- reading ----

    public IReadOnlyList<CheckpointFound> Find(string profileDirectory, IReadOnlyCollection<string> ids)
    {
        ArgumentNullException.ThrowIfNull(profileDirectory);
        ArgumentNullException.ThrowIfNull(ids);
        var found = new List<CheckpointFound>();
        if (ids.Count == 0)
        {
            return found;
        }

        var wanted = new HashSet<string>(ids, StringComparer.Ordinal);
        foreach (string store in StorePaths(profileDirectory))
        {
            try
            {
                using var repo = new Repository(store);
                foreach (var reference in repo.Refs.FromGlob(RefPrefix + "*"))
                {
                    string id = reference.CanonicalName[(reference.CanonicalName.LastIndexOf('/') + 1)..];
                    if (!wanted.Contains(id))
                    {
                        continue;
                    }

                    found.Add(Read(repo, store, id, reference.TargetIdentifier));
                }
            }
            catch (Exception ex) when (Failure(ex))
            {
                DiagnosticLog.Warn(Category, $"Could not read the checkpoint store {store}: {ex.Message}");
            }
        }

        return found;
    }

    /// <summary>
    /// One checkpoint read. Its root is the store's <see cref="FolderFile"/> (2026-10-28, review P1-05: a profile's rename rewrites only that file,
    /// and the record's own root then sent the restore to the old path), the record's only when the file is missing.
    /// </summary>
    private static CheckpointFound Read(Repository repo, string store, string id, string commitId)
    {
        string root = RootOf(store) ?? "";
        try
        {
            var commit = repo.Lookup<Commit>(new ObjectId(commitId));
            if (commit?[JsonName]?.Target is not Blob blob)
            {
                return new CheckpointFound(id, root, store, null, CheckpointProblem.Damaged, commitId);
            }

            using var stream = blob.GetContentStream();
            using var json = JsonDocument.Parse(stream);
            if (json.RootElement.TryGetProperty("version", out var version) && version.TryGetInt32(out int v) && v > CheckpointRecord.CurrentVersion)
            {
                return new CheckpointFound(id, root, store, null, CheckpointProblem.NewerVersion, commitId);
            }

            var record = json.RootElement.Deserialize(CheckpointJsonContext.Default.CheckpointRecord);
            if (record is null || record.Version < 1)
            {
                return new CheckpointFound(id, root, store, null, CheckpointProblem.Damaged, commitId);
            }

            foreach (var entry in record.Entries)
            {
                if (entry.Blob is { } name && commit[name]?.Target is not Blob)
                {
                    return new CheckpointFound(id, root, store, null, CheckpointProblem.Damaged, commitId);
                }
            }

            return new CheckpointFound(id, root.Length > 0 ? root : record.Root, store, record, CheckpointProblem.None, commitId);
        }
        catch (Exception ex) when (Failure(ex) || ex is JsonException)
        {
            DiagnosticLog.Warn(Category, $"Checkpoint {id} in {store} can't be read: {ex.Message}");
            return new CheckpointFound(id, root, store, null, CheckpointProblem.Damaged, commitId);
        }
    }

    /// <summary>
    /// A saved file's bytes, streamed straight from the store (2026-10-28, review P5-05, P7-09, P8-05): the commit looked up by the id
    /// <see cref="Find"/> gave, no copy in memory or a temp file; the stream owns the repository and closes it.
    /// </summary>
    public Stream? OpenBlob(CheckpointFound found, string name)
    {
        ArgumentNullException.ThrowIfNull(found);
        ArgumentNullException.ThrowIfNull(name);
        Repository? repo = null;
        try
        {
            repo = new Repository(found.Store);
            string? commitId = found.Commit ?? repo.Refs.FromGlob(RefPrefix + "*/" + found.Id).FirstOrDefault()?.TargetIdentifier;
            if (commitId is null || repo.Lookup<Commit>(new ObjectId(commitId))?[name]?.Target is not Blob blob)
            {
                repo.Dispose();
                return null;
            }

            var owned = new OwningStream(blob.GetContentStream(), repo);
            repo = null;
            return owned;
        }
        catch (Exception ex) when (Failure(ex))
        {
            repo?.Dispose();
            DiagnosticLog.Warn(Category, $"Could not read a saved file of checkpoint {found.Id}: {ex.Message}");
            return null;
        }
    }

    // ---- removing ----

    public void Remove(string profileDirectory, IReadOnlyCollection<string> ids)
    {
        ArgumentNullException.ThrowIfNull(profileDirectory);
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return;
        }

        var wanted = new HashSet<string>(ids, StringComparer.Ordinal);
        RemoveWhere(profileDirectory, name => wanted.Contains(name[(name.LastIndexOf('/') + 1)..]));
    }

    public void RemoveSessions(string profileDirectory, IReadOnlyCollection<string> sessions)
    {
        ArgumentNullException.ThrowIfNull(profileDirectory);
        ArgumentNullException.ThrowIfNull(sessions);
        if (sessions.Count == 0)
        {
            return;
        }

        var wanted = new HashSet<string>(sessions, StringComparer.Ordinal);
        void RemoveNow() => RemoveWhere(profileDirectory, name =>
        {
            string rest = name[RefPrefix.Length..];
            int slash = rest.IndexOf('/', StringComparison.Ordinal);
            return slash > 0 && wanted.Contains(rest[..slash]);
        });

        lock (_gate)
        {
            // Behind every commit in flight (review P4-06), on the pool (P5-13): a commit landing after the purge can't leave its ref.
            var before = Task.WhenAll(_pending.ToList());
            Task task = null!;
            task = before.ContinueWith(_ =>
            {
                try
                {
                    RemoveNow();
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Warn(Category, $"Could not remove purged sessions' file checkpoints: {ex.Message}");
                }
                finally
                {
                    lock (_gate)
                    {
                        _pending.Remove(task);
                    }
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            _pending.Add(task);
        }
    }

    private void RemoveWhere(string profileDirectory, Func<string, bool> match)
    {
        foreach (string store in StorePaths(profileDirectory))
        {
            try
            {
                using var repo = new Repository(store);
                foreach (string name in repo.Refs.FromGlob(RefPrefix + "*").Select(r => r.CanonicalName).Where(match).ToList())
                {
                    try
                    {
                        repo.Refs.Remove(name);
                    }
                    catch (LibGit2SharpException ex)
                    {
                        // Another instance removed it between the lookup and the delete: logged, never a failure.
                        DiagnosticLog.Debug(Category, $"Ref {name} in {store} was already gone: {ex.Message}");
                    }
                }
            }
            catch (Exception ex) when (Failure(ex))
            {
                DiagnosticLog.Warn(Category, $"Could not remove checkpoints in {store}: {ex.Message}");
            }
        }
    }

    // ---- retention, the prune, the sweep ----

    /// <summary>
    /// Keeps the newest <see cref="MaxCheckpoints"/> and, newest first, as many as fit in <see cref="CheckpointLimits.MaxBytes"/> (the newest
    /// always); removes the rest's refs and returns their ids, for their twins in the other stores. The sizes and times come from the
    /// messages' trailers (review P5-06; the JSON only for a message without them), ordered by their ticks then the ref's name (P1-11: the
    /// committer's second tied, and an unstable sort dropped the newest); <paramref name="newest"/>, the ref just committed, is always first.
    /// </summary>
    private List<string> Retain(string store, CheckpointLimits limits, int keepDays, string? newest = null)
    {
        var removed = new List<string>();
        using var repo = new Repository(store);
        var kept = new List<(string Name, string Id, long Ticks, long Bytes)>();
        foreach (var reference in repo.Refs.FromGlob(RefPrefix + "*").ToList())
        {
            string name = reference.CanonicalName;
            string id = name[(name.LastIndexOf('/') + 1)..];
            var commit = repo.Lookup<Commit>(new ObjectId(reference.TargetIdentifier));
            string message = commit?.Message ?? "";
            long ticks = Trailer(message, TimeTrailer) ?? commit?.Committer.When.UtcTicks ?? 0;
            long? bytes = Trailer(message, SavedBytesTrailer);
            if (bytes is null && commit?[JsonName]?.Target is Blob blob)
            {
                try
                {
                    using var stream = blob.GetContentStream();
                    using var json = JsonDocument.Parse(stream);
                    if (json.RootElement.TryGetProperty("savedBytes", out var saved) && saved.TryGetInt64(out long b))
                    {
                        bytes = b;
                    }
                }
                catch (JsonException)
                {
                }
            }

            kept.Add((name, id, ticks, bytes ?? 0));
        }

        kept.Sort((a, b) =>
        {
            bool aNew = string.Equals(a.Name, newest, StringComparison.Ordinal);
            bool bNew = string.Equals(b.Name, newest, StringComparison.Ordinal);
            if (aNew != bNew)
            {
                return aNew ? -1 : 1;
            }

            int byTime = b.Ticks.CompareTo(a.Ticks);
            return byTime != 0 ? byTime : string.CompareOrdinal(b.Name, a.Name);
        });

        long total = 0;
        // The cutoff once (review P3-05), and only within the clock's range.
        long now = _time.GetUtcNow().UtcTicks;
        long? cutoff = keepDays > 0 ? now - Math.Min(DaysTicks(keepDays), now) : null;
        for (int i = 0; i < kept.Count; i++)
        {
            total += kept[i].Bytes;
            bool aged = cutoff is { } before && kept[i].Ticks < before;
            bool over = i > 0 && (i >= MaxCheckpoints || total > limits.MaxBytes);
            if (aged || over)
            {
                try
                {
                    repo.Refs.Remove(kept[i].Name);
                }
                catch (LibGit2SharpException ex)
                {
                    DiagnosticLog.Debug(Category, $"Ref {kept[i].Name} was already gone: {ex.Message}");
                }

                removed.Add(kept[i].Id);
            }
        }

        return removed;
    }

    /// <summary>
    /// Deletes the loose objects no kept ref reaches, older than an hour, while nothing but <paramref name="own"/> of this process's leases and no
    /// other process's live turn holds the store; at most once an hour unless <paramref name="force"/> (retention just removed refs). Under
    /// <see cref="PruneLock"/> held open exclusively, never deleted on close: a crash's handle goes with the process on every system (review
    /// P8-03), so a leftover file never blocks a prune. A write time in the future counts as fresh.
    /// </summary>
    private void Prune(string store, int own, bool force)
    {
        if (IsBusy(store, own))
        {
            return;
        }

        string lockFile = Path.Combine(store, PruneLock);
        FileStream held;
        try
        {
            held = new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return;
        }

        using (held)
        {
            var now = _time.GetUtcNow();
            if (!force && LastPrune(held) is { } last && now.UtcTicks - last < PruneAge.Ticks && now.UtcTicks >= last)
            {
                return;
            }

            var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var repo = new Repository(store))
            {
                foreach (var reference in repo.Refs.FromGlob(RefPrefix + "*"))
                {
                    if (repo.Lookup<Commit>(new ObjectId(reference.TargetIdentifier)) is not { } commit)
                    {
                        continue;
                    }

                    reachable.Add(commit.Sha);
                    reachable.Add(commit.Tree.Sha);
                    foreach (var entry in commit.Tree)
                    {
                        reachable.Add(entry.Target.Sha);
                    }
                }
            }

            var cutoff = now - PruneAge;
            string objects = Path.Combine(store, "objects");
            if (Directory.Exists(objects))
            {
                foreach (string folder in Directory.EnumerateDirectories(objects))
                {
                    string prefix = Path.GetFileName(folder);
                    if (prefix.Length != 2)
                    {
                        continue;
                    }

                    foreach (string file in Directory.EnumerateFiles(folder))
                    {
                        string sha = prefix + Path.GetFileName(file);
                        if (sha.Length != 40 || reachable.Contains(sha))
                        {
                            continue;
                        }

                        try
                        {
                            if (new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero) >= cutoff)
                            {
                                continue;
                            }

                            File.SetAttributes(file, FileAttributes.Normal);
                            File.Delete(file);
                        }
                        catch (Exception ex) when (Failure(ex))
                        {
                            DiagnosticLog.Debug(Category, $"Could not prune {file}: {ex.Message}");
                        }
                    }
                }
            }

            held.SetLength(0);
            held.Write(Encoding.ASCII.GetBytes(now.UtcTicks.ToString(CultureInfo.InvariantCulture)));
        }
    }

    /// <summary>The last prune's time from the held lock file, or null for none.</summary>
    private static long? LastPrune(FileStream held)
    {
        held.Position = 0;
        using var reader = new StreamReader(held, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        return long.TryParse(reader.ReadToEnd().Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long ticks) ? ticks : null;
    }

    public void Sweep(string profileDirectory, CheckpointLimits limits)
    {
        ArgumentNullException.ThrowIfNull(profileDirectory);
        ArgumentNullException.ThrowIfNull(limits);
        string folder = FolderOf(profileDirectory);
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (string purging in Directory.EnumerateDirectories(folder, "*" + PurgingSuffix).ToList())
        {
            DeleteStore(purging, CancellationToken.None);
        }

        var removedEverywhere = new List<string>();
        foreach (string store in StorePaths(profileDirectory))
        {
            DropDeadMarkers(store);
            if (IsBusy(store, 0))
            {
                continue;
            }

            try
            {
                string? root = RootOf(store);
                if (root is not null && Gone(store, root, limits))
                {
                    continue;
                }

                var removed = Retain(store, limits, limits.KeepDays);
                removedEverywhere.AddRange(removed);
                bool empty;
                using (var repo = new Repository(store))
                {
                    empty = !repo.Refs.FromGlob(RefPrefix + "*").Any();
                }

                if (empty)
                {
                    if (TryClaim(store) is { } claimed)
                    {
                        DeleteStore(claimed, CancellationToken.None);
                    }
                }
                else
                {
                    Prune(store, own: 0, force: removed.Count > 0);
                }
            }
            catch (Exception ex) when (Failure(ex))
            {
                DiagnosticLog.Warn(Category, $"Could not sweep the checkpoint store {store}: {ex.Message}");
            }
        }

        if (removedEverywhere.Count > 0)
        {
            // A pair's twin goes with it.
            Remove(profileDirectory, removedEverywhere);
        }
    }

    /// <summary>
    /// A missing folder's store (2026-10-28, review P6-02: an unplugged drive, a swapped drive letter, a Mac's unmounted volume or a moment's
    /// denial purged it for good): <see cref="GoneFile"/> notes when it was first seen gone; the store is deleted once <c>keep (days)</c> has passed
    /// since, and only while the folder's parent is there (so the folder itself, not its drive, is gone). True when the store was handled so.
    /// A folder that is back clears the note.
    /// </summary>
    private bool Gone(string store, string root, CheckpointLimits limits)
    {
        string note = Path.Combine(store, GoneFile);
        if (Directory.Exists(root))
        {
            if (File.Exists(note))
            {
                File.Delete(note);
            }

            return false;
        }

        long now = _time.GetUtcNow().UtcTicks;
        long? since = null;
        if (File.Exists(note) && long.TryParse(File.ReadAllText(note).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long ticks))
        {
            since = ticks;
        }
        else
        {
            File.WriteAllText(note, now.ToString(CultureInfo.InvariantCulture));
        }

        string? parent = Path.GetDirectoryName(root);
        bool parentThere = parent is not null && Directory.Exists(parent);
        if (since is { } first && limits.KeepDays > 0 && parentThere && now - first >= DaysTicks(limits.KeepDays))
        {
            DiagnosticLog.Info(Category, $"{root} has been gone for {limits.KeepDays.ToString(CultureInfo.InvariantCulture)} days: its checkpoints go too.");
            if (TryClaim(store) is { } claimed)
            {
                DeleteStore(claimed, CancellationToken.None);
            }
        }

        return true;
    }

    // ---- stores, purges, profiles ----

    public IReadOnlyList<CheckpointStoreInfo> Stores(string profileDirectory, IReadOnlyCollection<string>? roots = null)
    {
        ArgumentNullException.ThrowIfNull(profileDirectory);
        var wanted = roots?.Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r))).ToList();
        var stores = new List<CheckpointStoreInfo>();
        foreach (string store in StorePaths(profileDirectory))
        {
            string root = RootOf(store) ?? "";
            if (wanted is not null && !wanted.Contains(root, WorkingDirectory.PathComparer))
            {
                continue;
            }

            stores.Add(new CheckpointStoreInfo(root, store, SizeOf(store), IsBusy(store, 0)));
        }

        return stores;
    }

    public CheckpointPurgeResult Purge(string profileDirectory, IReadOnlyCollection<string>? roots, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profileDirectory);
        var busy = new List<string>();
        var failed = new List<string>();
        var claimed = new List<(string Path, string Root, long Bytes)>();
        var ids = new List<string>();
        foreach (var store in Stores(profileDirectory, roots))
        {
            // Busy asked at the claim, under the lock (review P1-18), not from the listing's snapshot.
            var refs = RefIds(store.Path);
            if (TryClaim(store.Path) is not { } path)
            {
                (IsBusy(store.Path, 0) ? busy : failed).Add(store.Root);
                continue;
            }

            claimed.Add((path, store.Root, store.Bytes));
            ids.AddRange(refs);
        }

        int purged = 0;
        long bytes = 0;
        var pending = new List<string>();
        foreach (var (path, root, size) in claimed)
        {
            if (!cancellationToken.IsCancellationRequested && DeleteStore(path, cancellationToken))
            {
                purged++;
                bytes += size;
            }
            else if (Directory.Exists(path))
            {
                pending.Add(root);   // the sweep finishes what is left
            }
        }

        if (roots is not null && ids.Count > 0)
        {
            // The purged turns' twins in the other stores (review P2-09): their picture half can't be put back alone.
            Remove(profileDirectory, ids);
        }

        return new CheckpointPurgeResult(purged, bytes, busy, failed) { Pending = pending };
    }

    /// <summary>The checkpoint ids a store's refs name (none when it can't be read).</summary>
    private static List<string> RefIds(string store)
    {
        try
        {
            using var repo = new Repository(store);
            return repo.Refs.FromGlob(RefPrefix + "*").Select(r => r.CanonicalName[(r.CanonicalName.LastIndexOf('/') + 1)..]).ToList();
        }
        catch (Exception ex) when (Failure(ex))
        {
            DiagnosticLog.Debug(Category, $"Could not list {store}'s checkpoints: {ex.Message}");
            return [];
        }
    }

    public void RenameProfile(string oldProfileDirectory, string newProfileDirectory)
    {
        ArgumentNullException.ThrowIfNull(oldProfileDirectory);
        ArgumentNullException.ThrowIfNull(newProfileDirectory);
        string oldFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(oldProfileDirectory));
        string newFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(newProfileDirectory));
        foreach (string store in StorePaths(newFull))
        {
            try
            {
                if (RootOf(store) is not { } root || !WorkingDirectory.IsInside(oldFull, root) || WorkingDirectory.PathComparer.Equals(root, oldFull))
                {
                    continue;
                }

                string moved = newFull + root[oldFull.Length..];
                var (target, exists) = Probe(FolderOf(newFull), moved);
                if (target is null)
                {
                    continue;
                }

                if (!string.Equals(target, store, StringComparison.OrdinalIgnoreCase))
                {
                    if (exists)
                    {
                        // The new name is another store's already (review P11-05): this one is left as it is, never mis-keyed.
                        DiagnosticLog.Warn(Category, $"{target} already keeps {moved}; {store} is left as it is.");
                        continue;
                    }

                    Directory.Move(store, target);
                }

                File.WriteAllText(Path.Combine(target, FolderFile), moved);
            }
            catch (Exception ex) when (Failure(ex))
            {
                DiagnosticLog.Warn(Category, $"Could not follow the profile's rename in {store}: {ex.Message}");
            }
        }
    }

    // ---- helpers ----

    /// <summary>The root's store path (made with <paramref name="create"/>), or null when it has none.</summary>
    internal static string? StoreFor(string profileDirectory, string root, bool create)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string folder = FolderOf(profileDirectory);
        var (store, exists) = Probe(folder, full);
        if (store is null)
        {
            throw new IOException("No free store name for " + full);
        }

        if (exists)
        {
            if (RootOf(store) is null)
            {
                // A store whose folder.txt never landed (a kill between the two) and no refs: adopted, never passed over for "-2" (review P11-05).
                File.WriteAllText(Path.Combine(store, FolderFile), full);
            }

            return store;
        }

        if (!create)
        {
            return null;
        }

        Directory.CreateDirectory(folder);
        Repository.Init(store, isBare: true);
        using (var repo = new Repository(store))
        {
            repo.Config.Set("core.autocrlf", false, ConfigurationLevel.Local);
            repo.Config.Set("core.safecrlf", false, ConfigurationLevel.Local);
            repo.Config.Set("core.logAllRefUpdates", false, ConfigurationLevel.Local);
        }

        File.WriteAllText(Path.Combine(store, FolderFile), full);
        return store;
    }

    /// <summary>
    /// The one name probe (2026-10-28, review P4-14: <c>StoreFor</c> and the rename's <c>FreeName</c> each had their own): the first store name
    /// of <paramref name="root"/>'s that keeps it (exists), or that is free (not), a store with no <see cref="FolderFile"/> and no refs
    /// counting as its. Null past 1,000 names.
    /// </summary>
    private static (string? Path, bool Exists) Probe(string folder, string root)
    {
        string name = BaseName(root);
        for (int suffix = 1; suffix < 1000; suffix++)
        {
            string store = Path.Combine(folder, name + (suffix == 1 ? "" : "-" + suffix.ToString(CultureInfo.InvariantCulture)) + ".git");
            if (!Directory.Exists(store))
            {
                return (store, false);
            }

            if (RootOf(store) is { } named ? WorkingDirectory.PathComparer.Equals(named, root) : RefIds(store).Count == 0)
            {
                return (store, true);
            }

            // An 8-hex collision: the next suffix, never the other folder's store.
        }

        return (null, false);
    }

    /// <summary>The folder a store keeps (its <see cref="FolderFile"/>), or null.</summary>
    internal static string? RootOf(string store)
    {
        try
        {
            string file = Path.Combine(store, FolderFile);
            return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
        }
        catch (Exception ex) when (Failure(ex))
        {
            return null;
        }
    }

    /// <summary>The profile's stores (never one being purged).</summary>
    private static List<string> StorePaths(string profileDirectory)
    {
        string folder = FolderOf(profileDirectory);
        if (!Directory.Exists(folder))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateDirectories(folder, "*.git").Where(d => d.EndsWith(".git", StringComparison.OrdinalIgnoreCase)).ToList();
        }
        catch (Exception ex) when (Failure(ex))
        {
            return [];
        }
    }

    /// <summary>
    /// Whether anything but <paramref name="own"/> of this process's leases holds <paramref name="store"/>: more leases here, or another
    /// process's live marker (<see cref="MarkerLive"/>).
    /// </summary>
    private bool IsBusy(string store, int own)
    {
        lock (_gate)
        {
            if (_leases.TryGetValue(store, out var ids) && ids.Count > own)
            {
                return true;
            }
        }

        return ForeignMarker(store);
    }

    /// <summary>Whether another process's turn (or an unreadable marker under a day old) holds the store.</summary>
    private bool ForeignMarker(string store)
    {
        string open = Path.Combine(store, OpenFolder);
        try
        {
            if (!Directory.Exists(open))
            {
                return false;
            }

            foreach (string marker in Directory.EnumerateFiles(open))
            {
                if (MarkerLive(store, marker) is true && !Ours(marker))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex) when (Failure(ex))
        {
            return true;
        }
    }

    /// <summary>
    /// Whether a marker's turn still runs (2026-10-28, review P2-10: a crash's counted for a day): this process's only while its id holds a
    /// lease, another's while that process runs with the recorded start (<see cref="RecordFiles.SameStart"/>), one that can't be read for a day.
    /// Null when the marker is gone.
    /// </summary>
    private bool? MarkerLive(string store, string marker)
    {
        string text;
        DateTime written;
        try
        {
            text = File.ReadAllText(marker);
            written = File.GetLastWriteTimeUtc(marker);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (Failure(ex))
        {
            return true;
        }

        if (!RecordFiles.Pairs(text).TryGetValue(PidWord, out var who))
        {
            return new DateTimeOffset(written, TimeSpan.Zero) > _time.GetUtcNow() - MarkerAge;
        }

        if (who.Pid == Environment.ProcessId && RecordFiles.SameStart(s_ourStart.Value, who.Ticks))
        {
            lock (_gate)
            {
                return _leases.TryGetValue(store, out var ids) && ids.Contains(Path.GetFileName(marker), StringComparer.Ordinal);
            }
        }

        return RecordFiles.SameStart(RecordFiles.StartTicks(who.Pid), who.Ticks);
    }

    private static bool Ours(string marker)
    {
        try
        {
            return RecordFiles.Pairs(File.ReadAllText(marker)).TryGetValue(PidWord, out var who)
                && who.Pid == Environment.ProcessId && RecordFiles.SameStart(s_ourStart.Value, who.Ticks);
        }
        catch (Exception ex) when (Failure(ex))
        {
            return false;
        }
    }

    /// <summary>The store's dead markers deleted (a crash's, or one whose drop failed).</summary>
    private void DropDeadMarkers(string store)
    {
        string open = Path.Combine(store, OpenFolder);
        try
        {
            if (!Directory.Exists(open))
            {
                return;
            }

            foreach (string marker in Directory.EnumerateFiles(open).ToList())
            {
                if (MarkerLive(store, marker) is false)
                {
                    RecordFiles.Remove(marker, Category);
                }
            }
        }
        catch (Exception ex) when (Failure(ex))
        {
            DiagnosticLog.Debug(Category, $"Could not clear {store}'s markers: {ex.Message}");
        }
    }

    /// <summary>
    /// A store claimed for deleting (2026-10-28, review P1-18, P8-04): under the lock that <see cref="Begin"/> takes, if nothing holds it, renamed
    /// to a unique <c>&lt;store&gt;-&lt;id&gt;.purging</c> (an older failed one's name never blocks it); a live marker another process wrote
    /// meanwhile puts it back. The claimed path, or null.
    /// </summary>
    private string? TryClaim(string store)
    {
        lock (_gate)
        {
            if (IsBusy(store, 0))
            {
                return null;
            }

            string purging = store + "-" + Guid.NewGuid().ToString("N")[..8] + PurgingSuffix;
            try
            {
                Directory.Move(store, purging);
            }
            catch (Exception ex) when (Failure(ex))
            {
                DiagnosticLog.Warn(Category, $"Could not set {store} aside to delete it: {ex.Message}");
                return null;
            }

            if (ForeignMarker(purging))
            {
                try
                {
                    Directory.Move(purging, store);
                }
                catch (Exception ex) when (Failure(ex))
                {
                    DiagnosticLog.Warn(Category, $"Could not put {store} back for the turn writing to it: {ex.Message}");
                }

                return null;
            }

            return purging;
        }
    }

    private static void DropMarker(string store, string id)
    {
        try
        {
            File.Delete(Path.Combine(store, OpenFolder, id));
        }
        catch (Exception ex) when (Failure(ex))
        {
            DiagnosticLog.Debug(Category, $"Could not drop the marker of {id}: {ex.Message}");
        }
    }

    private static long SizeOf(string store)
    {
        try
        {
            return new DirectoryInfo(store).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Sum(f => f.Length);
        }
        catch (Exception ex) when (Failure(ex))
        {
            return 0;
        }
    }

    /// <summary>A store deleted for good, its read-only objects cleared first (<see cref="FileDeletes.ClearReadOnly"/>); true when it is gone (review P13-02).</summary>
    private static bool DeleteStore(string store, CancellationToken cancellationToken)
    {
        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            FileDeletes.ClearReadOnly(store);
            Directory.Delete(store, recursive: true);
            return true;
        }
        catch (Exception ex) when (Failure(ex))
        {
            DiagnosticLog.Warn(Category, $"Could not delete {store}: {ex.Message}");
            return false;
        }
    }

    /// <summary>A number of days in ticks, never past a long (a hand-edited keep-days, review P3-05).</summary>
    private static long DaysTicks(int days) => days > 1_000_000 ? long.MaxValue : days * TimeSpan.TicksPerDay;

    private static bool Failure(Exception ex) => WorkingDirectory.IsFileFailure(ex) || ex is LibGit2SharpException;

    /// <summary>
    /// A turn's writer: blobs into the store as the turn goes, through one repository opened at the first save (2026-10-28, review P2-13: one
    /// per save), closed before the commit opens its own. It holds its store's lease until its commit ends or it is abandoned.
    /// </summary>
    private sealed class Writer(string store, string root, string id) : ICheckpointWriter, IStoreLocated
    {
        private readonly object _repoGate = new();
        private Repository? _repo;
        private int _released;

        public string Store { get; } = store;

        public string Root { get; } = root;

        public string Id { get; } = id;

        public string StorePath => Store;

        /// <summary>True the first time only: the lease is given back once.</summary>
        public bool TryRelease() => Interlocked.Exchange(ref _released, 1) == 0;

        public void Close()
        {
            lock (_repoGate)
            {
                _repo?.Dispose();
                _repo = null;
            }
        }

        public string? Save(byte[] bytes)
        {
            using var stream = new MemoryStream(bytes, writable: false);
            return Save(stream, bytes.LongLength);
        }

        public string? Save(Stream content, long length)
        {
            try
            {
                lock (_repoGate)
                {
                    _repo ??= new Repository(Store);
                    return _repo.ObjectDatabase.CreateBlob(content, length).Sha;
                }
            }
            catch (Exception ex) when (Failure(ex))
            {
                DiagnosticLog.Warn(Category, $"Could not save a file into {Store}: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>A blob's stream that closes its repository with it (review P5-05: a stream over libgit2's buffer needs the repository open).</summary>
    private sealed class OwningStream(Stream inner, IDisposable owner) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                owner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
