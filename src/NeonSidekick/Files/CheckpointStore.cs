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
    /// <summary>The limits <paramref name="settings"/> set.</summary>
    public static CheckpointLimits From(AppSettingsData settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new CheckpointLimits(
            settings.FileCheckpointKeepDays,
            settings.FileCheckpointMaxMegabytes * 1024L * 1024L,
            settings.FileCheckpointLargestFileMegabytes * 1024L * 1024L);
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

/// <summary>A kept checkpoint found in one store: its record (null when <see cref="Problem"/> says it can't be read).</summary>
public sealed record CheckpointFound(string Id, string Root, string Store, CheckpointRecord? Record, CheckpointProblem Problem);

/// <summary>One store on disk: the folder it keeps, its path, its size, and whether a turn is writing to it.</summary>
public sealed record CheckpointStoreInfo(string Root, string Path, long Bytes, bool Busy);

/// <summary>What a purge did: stores deleted, the bytes freed, the folders skipped as busy, those that couldn't be deleted.</summary>
public sealed record CheckpointPurgeResult(int Purged, long Bytes, IReadOnlyList<string> Busy, IReadOnlyList<string> Failed);

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

    /// <summary>Waits for every commit still running.</summary>
    Task FlushAsync();

    /// <summary>Waits at most <paramref name="wait"/> for every commit still running (the app's exit); false when one was left.</summary>
    bool FlushPending(TimeSpan wait);

    /// <summary>The kept checkpoints with these ids, in every store of the profile (a pair's twin too).</summary>
    IReadOnlyList<CheckpointFound> Find(string profileDirectory, IReadOnlyCollection<string> ids);

    /// <summary>A saved file's bytes (<see cref="CheckpointEntry.Blob"/>), or null when it is missing.</summary>
    Stream? OpenBlob(CheckpointFound found, string name);

    /// <summary>Removes these checkpoints' refs in every store (spent, or dropped by a rewind); a ref already gone is no error.</summary>
    void Remove(string profileDirectory, IReadOnlyCollection<string> ids);

    /// <summary>Removes every checkpoint of these sessions (their invariant numbers) in every store.</summary>
    void RemoveSessions(string profileDirectory, IReadOnlyCollection<string> sessions);

    /// <summary>The profile's stores.</summary>
    IReadOnlyList<CheckpointStoreInfo> Stores(string profileDirectory);

    /// <summary>Deletes the stores of <paramref name="roots"/> (null = every store), for good; a busy store is skipped and named.</summary>
    CheckpointPurgeResult Purge(string profileDirectory, IReadOnlyCollection<string>? roots, CancellationToken cancellationToken);

    /// <summary>The start-up sweep: gone folders' and empty stores deleted, the age rule and retention applied, the prune run, purges finished.</summary>
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
/// identity; the commits' author is a fixed local one. A repository is opened per operation and disposed at once, so nothing stays locked.
/// Retention keeps the newest <see cref="MaxCheckpoints"/> and <c>File checkpoints max (MB)</c>; the prune then deletes loose objects no
/// kept ref reaches, older than an hour, under <c>prune.lock</c>, and never while another turn's <c>open/</c> marker is under a day old.
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

    /// <summary>The file held while pruning.</summary>
    public const string PruneLock = "prune.lock";

    /// <summary>The folder of a turn's markers.</summary>
    public const string OpenFolder = "open";

    /// <summary>A store being purged, renamed first so nothing opens it half deleted.</summary>
    public const string PurgingSuffix = ".purging";

    /// <summary>The newest checkpoints a store keeps.</summary>
    public const int MaxCheckpoints = 50;

    /// <summary>The most characters of a folder's name in its store's name.</summary>
    public const int MaxNameChars = 40;

    private static readonly TimeSpan PruneAge = TimeSpan.FromHours(1);
    private static readonly TimeSpan MarkerAge = TimeSpan.FromHours(24);

    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly List<Task> _pending = new();
    private readonly HashSet<string> _committing = new(StringComparer.OrdinalIgnoreCase);

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
            string store = StoreFor(profileDirectory, root, create: true)!;
            string open = Path.Combine(store, OpenFolder);
            Directory.CreateDirectory(open);
            File.WriteAllText(Path.Combine(open, checkpointId), root);
            return new Writer(store, Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), checkpointId);
        }
        catch (Exception ex) when (Failure(ex))
        {
            DiagnosticLog.Warn(Category, $"Could not open the checkpoint store for {root}: {ex.Message}");
            return null;
        }
    }

    public void Commit(ICheckpointWriter writer, CheckpointRecord record, CheckpointLimits limits)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(limits);
        if (writer is not Writer ours)
        {
            return;
        }

        lock (_gate)
        {
            _committing.Add(ours.Store);
            Task task = null!;
            task = Task.Run(() =>
            {
                try
                {
                    CommitNow(ours, record, limits);
                }
                finally
                {
                    lock (_gate)
                    {
                        _committing.Remove(ours.Store);
                        _pending.Remove(task);
                    }
                }
            });
            _pending.Add(task);
        }
    }

    public void Abandon(ICheckpointWriter writer)
    {
        if (writer is Writer ours)
        {
            DropMarker(ours.Store, ours.Id);
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
                        name = (names.Count + 1).ToString("D4", CultureInfo.InvariantCulture);
                        names[id] = name;
                        tree.Add(name, repo.Lookup<Blob>(new ObjectId(id)), Mode.NonExecutableFile);
                    }

                    entry.Blob = name;
                }

                byte[] json = JsonSerializer.SerializeToUtf8Bytes(record, CheckpointJsonContext.Default.CheckpointRecord);
                using (var stream = new MemoryStream(json))
                {
                    tree.Add(JsonName, repo.ObjectDatabase.CreateBlob(stream, json.LongLength), Mode.NonExecutableFile);
                }

                var made = repo.ObjectDatabase.CreateTree(tree);
                var signature = new Signature("NeonSidekick", "checkpoints@localhost", _time.GetUtcNow());
                var commit = repo.ObjectDatabase.CreateCommit(signature, signature, MessageOf(record), made, [], prettifyMessage: false);
                repo.Refs.Add(RefPrefix + record.Session + "/" + record.Id, commit.Id, allowOverwrite: true);
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
            if (Retain(writer.Store, limits) is { Count: > 0 } removed)
            {
                Remove(profile, removed);
            }

            Prune(writer.Store);
        }
        catch (Exception ex) when (Failure(ex))
        {
            DiagnosticLog.Warn(Category, $"Could not trim the checkpoints of {record.Root}: {ex.Message}");
        }
    }

    /// <summary>The commit's message, e.g. <c>turn 12 · 3 files · patch_file, delete</c>.</summary>
    public static string MessageOf(CheckpointRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        int files = record.Entries.Select(e => e.Path).Distinct(StringComparer.Ordinal).Count();
        return string.Create(CultureInfo.InvariantCulture, $"turn {record.Turn} · {files} files · {string.Join(", ", record.Tools)}");
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

    private static CheckpointFound Read(Repository repo, string store, string id, string commitId)
    {
        string root = RootOf(store) ?? "";
        try
        {
            var commit = repo.Lookup<Commit>(new ObjectId(commitId));
            if (commit?[JsonName]?.Target is not Blob blob)
            {
                return new CheckpointFound(id, root, store, null, CheckpointProblem.Damaged);
            }

            using var stream = blob.GetContentStream();
            using var json = JsonDocument.Parse(stream);
            if (json.RootElement.TryGetProperty("version", out var version) && version.TryGetInt32(out int v) && v > CheckpointRecord.CurrentVersion)
            {
                return new CheckpointFound(id, root, store, null, CheckpointProblem.NewerVersion);
            }

            var record = json.RootElement.Deserialize(CheckpointJsonContext.Default.CheckpointRecord);
            if (record is null || record.Version < 1)
            {
                return new CheckpointFound(id, root, store, null, CheckpointProblem.Damaged);
            }

            foreach (var entry in record.Entries)
            {
                if (entry.Blob is { } name && commit[name]?.Target is not Blob)
                {
                    return new CheckpointFound(id, root, store, null, CheckpointProblem.Damaged);
                }
            }

            return new CheckpointFound(id, record.Root.Length > 0 ? record.Root : root, store, record, CheckpointProblem.None);
        }
        catch (Exception ex) when (Failure(ex) || ex is JsonException)
        {
            DiagnosticLog.Warn(Category, $"Checkpoint {id} in {store} can't be read: {ex.Message}");
            return new CheckpointFound(id, root, store, null, CheckpointProblem.Damaged);
        }
    }

    public Stream? OpenBlob(CheckpointFound found, string name)
    {
        ArgumentNullException.ThrowIfNull(found);
        ArgumentNullException.ThrowIfNull(name);
        try
        {
            using var repo = new Repository(found.Store);
            var reference = repo.Refs.FromGlob(RefPrefix + "*/" + found.Id).FirstOrDefault();
            if (reference is null || repo.Lookup<Commit>(new ObjectId(reference.TargetIdentifier))?[name]?.Target is not Blob blob)
            {
                return null;
            }

            // Copied out, so the repository closes now (a stream over libgit2's buffer needs it open): in memory up to 64 MB, else a temp file.
            Stream copy = blob.Size <= FileJournal.WholeReadBytes
                ? new MemoryStream()
                : new FileStream(Path.Combine(Path.GetTempPath(), "neon-restore-" + Guid.NewGuid().ToString("N")), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
            using (var content = blob.GetContentStream())
            {
                content.CopyTo(copy);
            }

            copy.Position = 0;
            return copy;
        }
        catch (Exception ex) when (Failure(ex))
        {
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
        RemoveWhere(profileDirectory, name =>
        {
            string rest = name[RefPrefix.Length..];
            int slash = rest.IndexOf('/', StringComparison.Ordinal);
            return slash > 0 && wanted.Contains(rest[..slash]);
        });
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
    /// always); removes the rest's refs and returns their ids, for their twins in the other stores.
    /// </summary>
    private List<string> Retain(string store, CheckpointLimits limits, int keepDays = 0)
    {
        var removed = new List<string>();
        using var repo = new Repository(store);
        var kept = new List<(string Name, string Id, DateTimeOffset When, long Bytes)>();
        foreach (var reference in repo.Refs.FromGlob(RefPrefix + "*").ToList())
        {
            string name = reference.CanonicalName;
            string id = name[(name.LastIndexOf('/') + 1)..];
            var commit = repo.Lookup<Commit>(new ObjectId(reference.TargetIdentifier));
            long bytes = 0;
            if (commit?[JsonName]?.Target is Blob blob)
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

            kept.Add((name, id, commit?.Committer.When ?? DateTimeOffset.MinValue, bytes));
        }

        kept.Sort((a, b) => b.When.CompareTo(a.When));
        long total = 0;
        var now = _time.GetUtcNow();
        for (int i = 0; i < kept.Count; i++)
        {
            total += kept[i].Bytes;
            bool aged = keepDays > 0 && kept[i].When < now - TimeSpan.FromDays(keepDays);
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
    /// Deletes the loose objects no kept ref reaches, older than an hour, under <see cref="PruneLock"/> (skipped when held), never while a
    /// turn's marker under a day old is in <see cref="OpenFolder"/>. A write time in the future counts as fresh.
    /// </summary>
    private void Prune(string store)
    {
        if (FreshMarker(store) || IsCommitting(store))
        {
            return;
        }

        string lockFile = Path.Combine(store, PruneLock);
        FileStream held;
        try
        {
            held = new FileStream(lockFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            return;
        }

        using (held)
        {
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

            var cutoff = _time.GetUtcNow() - PruneAge;
            string objects = Path.Combine(store, "objects");
            if (!Directory.Exists(objects))
            {
                return;
            }

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

        foreach (string purging in Directory.EnumerateDirectories(folder, "*" + PurgingSuffix))
        {
            DeleteStore(purging, CancellationToken.None);
        }

        var removedEverywhere = new List<string>();
        foreach (string store in StorePaths(profileDirectory))
        {
            if (FreshMarker(store) || IsCommitting(store))
            {
                continue;
            }

            try
            {
                string? root = RootOf(store);
                if (root is not null && !Directory.Exists(root) && Reachable(root))
                {
                    DiagnosticLog.Info(Category, $"{root} is gone: its checkpoints go too.");
                    DeleteStore(store, CancellationToken.None);
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
                    DeleteStore(store, CancellationToken.None);
                }
                else if (removed.Count > 0)
                {
                    Prune(store);
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

    /// <summary>Whether <paramref name="path"/>'s drive or share answers (an unplugged drive or an offline share keeps its store).</summary>
    private static bool Reachable(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && Directory.Exists(root);
        }
        catch (Exception ex) when (Failure(ex))
        {
            return false;
        }
    }

    // ---- stores, purges, profiles ----

    public IReadOnlyList<CheckpointStoreInfo> Stores(string profileDirectory)
    {
        ArgumentNullException.ThrowIfNull(profileDirectory);
        return StorePaths(profileDirectory)
            .Select(store => new CheckpointStoreInfo(RootOf(store) ?? "", store, SizeOf(store), FreshMarker(store) || IsCommitting(store)))
            .ToList();
    }

    public CheckpointPurgeResult Purge(string profileDirectory, IReadOnlyCollection<string>? roots, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profileDirectory);
        var chosen = Stores(profileDirectory)
            .Where(s => roots is null || roots.Any(r => WorkingDirectory.PathComparer.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(r)), s.Root)))
            .ToList();
        var busy = new List<string>();
        var failed = new List<string>();
        var renamed = new List<string>();
        long bytes = 0;
        foreach (var store in chosen)
        {
            if (store.Busy)
            {
                busy.Add(store.Root);
                continue;
            }

            string purging = store.Path + PurgingSuffix;
            try
            {
                Directory.Move(store.Path, purging);
                renamed.Add(purging);
                bytes += store.Bytes;
            }
            catch (Exception ex) when (Failure(ex))
            {
                DiagnosticLog.Warn(Category, $"Could not purge {store.Path}: {ex.Message}");
                failed.Add(store.Root);
            }
        }

        foreach (string purging in renamed)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;   // the sweep finishes what is left
            }

            DeleteStore(purging, cancellationToken);
        }

        return new CheckpointPurgeResult(renamed.Count, bytes, busy, failed);
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
                if (RootOf(store) is not { } root || !IsInside(oldFull, root))
                {
                    continue;
                }

                string moved = newFull + root[oldFull.Length..];
                string target = FreeName(FolderOf(newFull), moved);
                if (!string.Equals(target, store, StringComparison.OrdinalIgnoreCase))
                {
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
        string name = BaseName(full);
        for (int suffix = 1; suffix < 1000; suffix++)
        {
            string store = Path.Combine(folder, name + (suffix == 1 ? "" : "-" + suffix.ToString(CultureInfo.InvariantCulture)) + ".git");
            if (Directory.Exists(store))
            {
                if (RootOf(store) is { } named && WorkingDirectory.PathComparer.Equals(named, full))
                {
                    return store;
                }

                continue;   // an 8-hex collision: the next suffix, never the other folder's store
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

        throw new IOException("No free store name for " + full);
    }

    private static string FreeName(string folder, string root)
    {
        string name = BaseName(root);
        for (int suffix = 1; ; suffix++)
        {
            string store = Path.Combine(folder, name + (suffix == 1 ? "" : "-" + suffix.ToString(CultureInfo.InvariantCulture)) + ".git");
            if (!Directory.Exists(store) || (RootOf(store) is { } named && WorkingDirectory.PathComparer.Equals(named, root)))
            {
                return store;
            }
        }
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

    private bool FreshMarker(string store)
    {
        string open = Path.Combine(store, OpenFolder);
        try
        {
            if (!Directory.Exists(open))
            {
                return false;
            }

            var now = _time.GetUtcNow();
            return Directory.EnumerateFiles(open).Any(f => new DateTimeOffset(File.GetLastWriteTimeUtc(f), TimeSpan.Zero) > now - MarkerAge);
        }
        catch (Exception ex) when (Failure(ex))
        {
            return true;
        }
    }

    private bool IsCommitting(string store)
    {
        lock (_gate)
        {
            return _committing.Contains(store);
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

    /// <summary>A store deleted for good, its read-only objects cleared first (<see cref="FileDeletes.ClearReadOnly"/>).</summary>
    private static void DeleteStore(string store, CancellationToken cancellationToken)
    {
        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            FileDeletes.ClearReadOnly(store);
            Directory.Delete(store, recursive: true);
        }
        catch (Exception ex) when (Failure(ex))
        {
            DiagnosticLog.Warn(Category, $"Could not delete {store}: {ex.Message}");
        }
    }

    private static bool IsInside(string folder, string path) =>
        path.Length > folder.Length && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
        && (path[folder.Length] == Path.DirectorySeparatorChar || path[folder.Length] == Path.AltDirectorySeparatorChar);

    private static bool Failure(Exception ex) => WorkingDirectory.IsFileFailure(ex) || ex is LibGit2SharpException;

    /// <summary>A turn's writer: blobs into the store as the turn goes, a repository opened per save.</summary>
    private sealed class Writer(string store, string root, string id) : ICheckpointWriter, IStoreLocated
    {
        public string Store { get; } = store;

        public string Root { get; } = root;

        public string Id { get; } = id;

        public string StorePath => Store;

        public string? Save(byte[] bytes)
        {
            using var stream = new MemoryStream(bytes, writable: false);
            return Save(stream, bytes.LongLength);
        }

        public string? Save(Stream content, long length)
        {
            try
            {
                using var repo = new Repository(Store);
                return repo.ObjectDatabase.CreateBlob(content, length).Sha;
            }
            catch (Exception ex) when (Failure(ex))
            {
                DiagnosticLog.Warn(Category, $"Could not save a file into {Store}: {ex.Message}");
                return null;
            }
        }
    }
}
