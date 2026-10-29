using System.Security.Cryptography;
using NeonSidekick.Files;

namespace NeonSidekick.Tests.Fakes;

/// <summary>
/// A file checkpoints' store in memory (2026-10-28, the checkpoints plan): synchronous, so a screen test that journals never makes a git
/// repository or races a background commit. Blobs are kept by their SHA-256; a committed record is found by its id until removed. The real
/// store has its own tests (<c>CheckpointStoreTests</c>).
/// </summary>
public sealed class FakeCheckpointStore : ICheckpointStore
{
    private readonly Dictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);

    /// <summary>The committed checkpoints still kept, in commit order.</summary>
    public List<CheckpointRecord> Records { get; } = [];

    /// <summary>The roots a writer was begun for, in order.</summary>
    public List<string> Begun { get; } = [];

    /// <summary>How many writers were dropped.</summary>
    public int Abandoned { get; private set; }

    /// <summary>The ids handed to <see cref="Remove"/>, in order.</summary>
    public List<string> Removed { get; } = [];

    /// <summary>The sessions handed to <see cref="RemoveSessions"/>, in order.</summary>
    public List<string> RemovedSessions { get; } = [];

    /// <summary>The profile folders swept, in order.</summary>
    public List<string> Swept { get; } = [];

    /// <summary>The roots each purge was asked for (null = every store).</summary>
    public List<IReadOnlyCollection<string>?> Purges { get; } = [];

    /// <summary>The renames asked for.</summary>
    public List<(string Old, string New)> Renames { get; } = [];

    /// <summary>When set, <see cref="Begin"/> fails (a store that can't be opened).</summary>
    public bool FailBegin { get; set; }

    /// <summary>The roots whose <see cref="Begin"/> fails while the others open (2026-10-28, review P4-01).</summary>
    public HashSet<string> FailBeginFor { get; } = new(WorkingDirectory.PathComparer);

    /// <summary>How many times <see cref="Find"/> ran (2026-10-28, review P1-12: one per <c>/undo</c>).</summary>
    public int Finds { get; private set; }

    /// <summary>When set, <see cref="Find"/> waits for it first (a slow store, so a test can stop the planning).</summary>
    public ManualResetEventSlim? FindGate { get; set; }

    public ICheckpointWriter? Begin(string profileDirectory, string root, string checkpointId)
    {
        lock (Records)
        {
            Begun.Add(root);
        }

        return FailBegin || FailBeginFor.Contains(root) ? null : new Writer(root, this);
    }

    public void Commit(ICheckpointWriter writer, CheckpointRecord record, CheckpointLimits limits)
    {
        lock (Records)
        {
            Records.Add(record);
        }
    }

    public void Abandon(ICheckpointWriter writer)
    {
        lock (Records)
        {
            Abandoned++;
        }
    }

    public Task FlushAsync() => Task.CompletedTask;

    public bool FlushPending(TimeSpan wait) => true;

    public IReadOnlyList<CheckpointFound> Find(string profileDirectory, IReadOnlyCollection<string> ids)
    {
        FindGate?.Wait(TimeSpan.FromSeconds(30));
        lock (Records)
        {
            Finds++;
            return Records.Where(r => ids.Contains(r.Id)).Select(r => new CheckpointFound(r.Id, r.Root, "fake:" + r.Root, r, CheckpointProblem.None)).ToList();
        }
    }

    public Stream? OpenBlob(CheckpointFound found, string name)
    {
        lock (Records)
        {
            return _blobs.TryGetValue(name, out byte[]? bytes) ? new MemoryStream(bytes, writable: false) : null;
        }
    }

    public void Remove(string profileDirectory, IReadOnlyCollection<string> ids)
    {
        lock (Records)
        {
            Removed.AddRange(ids);
            Records.RemoveAll(r => ids.Contains(r.Id));
        }
    }

    public void RemoveSessions(string profileDirectory, IReadOnlyCollection<string> sessions)
    {
        lock (Records)
        {
            RemovedSessions.AddRange(sessions);
            Records.RemoveAll(r => sessions.Contains(r.Session));
        }
    }

    public IReadOnlyList<CheckpointStoreInfo> Stores(string profileDirectory, IReadOnlyCollection<string>? roots = null)
    {
        lock (Records)
        {
            return Records.Where(r => roots is null || roots.Contains(r.Root, WorkingDirectory.PathComparer))
                .GroupBy(r => r.Root, WorkingDirectory.PathComparer)
                .Select(g => new CheckpointStoreInfo(g.Key, "fake:" + g.Key, Math.Max(1, g.Sum(r => r.SavedBytes)), Busy: false)).ToList();
        }
    }

    public CheckpointPurgeResult Purge(string profileDirectory, IReadOnlyCollection<string>? roots, CancellationToken cancellationToken)
    {
        lock (Records)
        {
            Purges.Add(roots);
            var gone = Stores(profileDirectory, roots).ToList();
            // The purged stores' twins go from the other stores too (2026-10-28, review L4: the real store's rule).
            var ids = Records.Where(r => gone.Any(s => WorkingDirectory.PathComparer.Equals(s.Root, r.Root))).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            Records.RemoveAll(r => ids.Contains(r.Id));
            return new CheckpointPurgeResult(gone.Count, gone.Sum(s => s.Bytes), [], []);
        }
    }

    public void Sweep(string profileDirectory, CheckpointLimits limits)
    {
        lock (Records)
        {
            Swept.Add(profileDirectory);
        }
    }

    public void RenameProfile(string oldProfileDirectory, string newProfileDirectory)
    {
        lock (Records)
        {
            Renames.Add((oldProfileDirectory, newProfileDirectory));
        }
    }

    /// <summary>Forgets every kept blob (a damaged store: the record is read, its bytes aren't there).</summary>
    public void DropBlobs()
    {
        lock (Records)
        {
            _blobs.Clear();
        }
    }

    private string Keep(byte[] bytes)
    {
        string name = Convert.ToHexString(SHA256.HashData(bytes));
        lock (Records)
        {
            _blobs[name] = bytes;
        }

        return name;
    }

    private sealed class Writer(string root, FakeCheckpointStore store) : ICheckpointWriter
    {
        public string Root { get; } = root;

        public string? Save(byte[] bytes) => store.Keep(bytes);

        public string? Save(Stream content, long length)
        {
            using var copy = new MemoryStream();
            content.CopyTo(copy);
            return store.Keep(copy.ToArray());
        }
    }
}
