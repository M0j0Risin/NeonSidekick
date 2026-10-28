using System.Globalization;
using System.Security.Cryptography;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Files;

/// <summary>Where one checkpoint's saved bytes go as the turn runs (a store's open repository for one root, or the tests' fake).</summary>
public interface ICheckpointWriter
{
    /// <summary>The root this writer saves for.</summary>
    string Root { get; }

    /// <summary>Saves <paramref name="bytes"/>, returning the blob's id, or null when the store failed (logged).</summary>
    string? Save(byte[] bytes);

    /// <summary>Saves <paramref name="length"/> bytes read from <paramref name="content"/>, never holding them whole; null when the store failed.</summary>
    string? Save(Stream content, long length);
}

/// <summary>One root's part of a turn's journal: its writer and its op log, in the writes' order.</summary>
public sealed class JournalLog
{
    internal JournalLog(string root) => Root = root;

    /// <summary>The root's full path.</summary>
    public string Root { get; }

    /// <summary>The store's writer, opened at the root's first entry; null before, or when the store could not be opened.</summary>
    public ICheckpointWriter? Writer { get; internal set; }

    internal bool Opened { get; set; }

    public List<CheckpointEntry> Entries { get; } = new();

    /// <summary>The bytes saved for this root this turn.</summary>
    public long SavedBytes { get; internal set; }
}

/// <summary>
/// A turn's file journal (2026-10-28, the checkpoints plan): before a model tool changes a file through the turn's working directory or the
/// profile's images folder, what was there goes in an ordered op log — the old bytes saved to the checkpoint store (<see cref="CheckpointOp.Content"/>),
/// or that nothing was there (<see cref="CheckpointOp.Absent"/>), a move (<see cref="CheckpointOp.Moved"/>), a folder or a link deleted — so
/// <see cref="FileRestore"/> can replay it backwards. Ambient per tool call (<see cref="Enter"/>, beside <see cref="WriteGuard.Watch"/> in
/// <c>Assistant</c>), never for the whole turn: a task the turn starts outside a tool (the key watcher, a chip's still, a mid-turn pane) is
/// the user's, not the model's. A writer counts only when its own root equals one of the journal's (<see cref="WorkingDirectory.PathComparer"/>),
/// so a share's, a vault's or a temp root is never journaled; the app's own writes (<see cref="WriteGuard.AsApp{T}"/>) aren't either.
/// Nothing the journal does refuses or fails a write: a save that can't happen is <see cref="CheckpointEntry.NotSaved"/>, logged, and the
/// write goes ahead. <see cref="Seal"/> ends it: a write after that is logged, not recorded, and marks the checkpoint incomplete.
/// </summary>
public sealed class FileJournal
{
    /// <summary>The diagnostics category.</summary>
    public const string Category = "Checkpoints";

    /// <summary>The most entries one turn records; past it the turn's checkpoint is incomplete.</summary>
    public const int MaxEntries = 5_000;

    /// <summary>The most bytes one turn saves (or the largest-file setting when higher, so one such file always fits).</summary>
    public const long MaxTurnBytes = 512L * 1024 * 1024;

    /// <summary>The most bytes the seal hashes; the rest keep size and write time.</summary>
    public const long MaxHashBytes = 512L * 1024 * 1024;

    /// <summary>A file at most this big is read whole; a bigger one is streamed into the store.</summary>
    public const long WholeReadBytes = 64L * 1024 * 1024;

    // NotSaved reasons, kept in the JSON (internal words, never shown as they are).
    public const string TooBigReason = "too-big";
    public const string TurnCapReason = "turn-cap";
    public const string OfflineReason = "online-only";
    public const string NoRoomReason = "no-room";
    public const string FailedReason = "failed";

    private static readonly AsyncLocal<FileJournal?> s_current = new();

    private readonly object _gate = new();
    private readonly Dictionary<string, JournalLog> _logs = new(WorkingDirectory.PathComparer);
    private readonly Func<string, ICheckpointWriter?> _open;
    private readonly Func<string, long> _freeSpace;
    private readonly long _largestFile;
    private readonly List<string> _tools = new();
    private int _count;
    private long _saved;
    private long? _free;
    private bool _sealed;
    private bool _incomplete;

    /// <param name="id">The checkpoint id, shared by every root's checkpoint of the turn.</param>
    /// <param name="roots">The roots whose writers are journaled (the working folder's and the images folder's).</param>
    /// <param name="open">Opens a root's store writer at its first entry; null on a store failure.</param>
    /// <param name="largestFileBytes">The <c>File checkpoints per file (MB)</c> setting in bytes.</param>
    /// <param name="freeSpace">The free bytes on a folder's drive (<see cref="DriveInfo.AvailableFreeSpace"/>; the tests' fake).</param>
    public FileJournal(string id, IEnumerable<string> roots, Func<string, ICheckpointWriter?> open, long largestFileBytes, Func<string, long>? freeSpace = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(roots);
        Id = id;
        _open = open ?? throw new ArgumentNullException(nameof(open));
        _largestFile = largestFileBytes;
        _freeSpace = freeSpace ?? DriveFree;
        foreach (string root in roots)
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            _logs.TryAdd(full, new JournalLog(full));
        }
    }

    /// <summary>The checkpoint id.</summary>
    public string Id { get; }

    /// <summary>The journal entered on this flow (one tool call), or null.</summary>
    public static FileJournal? Current => s_current.Value;

    /// <summary>The journal for the call inside (null = none); the previous one comes back on dispose.</summary>
    public static IDisposable Enter(FileJournal? journal)
    {
        var previous = s_current.Value;
        s_current.Value = journal;
        return new Closer(() => s_current.Value = previous);
    }

    /// <summary>Each root's log (a copy of the list).</summary>
    public IReadOnlyList<JournalLog> Logs
    {
        get
        {
            lock (_gate)
            {
                return _logs.Values.ToList();
            }
        }
    }

    /// <summary>The tools the turn ran, in first-run order.</summary>
    public IReadOnlyList<string> Tools
    {
        get
        {
            lock (_gate)
            {
                return _tools.ToList();
            }
        }
    }

    /// <summary>Whether any root recorded an entry.</summary>
    public bool HasEntries
    {
        get
        {
            lock (_gate)
            {
                return _logs.Values.Any(l => l.Entries.Count > 0);
            }
        }
    }

    /// <summary>Some of the turn's changes weren't kept: a write after the seal, or past <see cref="MaxEntries"/>.</summary>
    public bool Incomplete
    {
        get
        {
            lock (_gate)
            {
                return _incomplete;
            }
        }
    }

    /// <summary>Whether the journal is sealed.</summary>
    public bool Sealed
    {
        get
        {
            lock (_gate)
            {
                return _sealed;
            }
        }
    }

    /// <summary>Notes a tool the turn ran (once each).</summary>
    public void NoteTool(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_gate)
        {
            if (!_tools.Contains(name, StringComparer.Ordinal))
            {
                _tools.Add(name);
            }
        }
    }

    // ---- the hooks WorkingDirectory calls, after Resolve(forWrite) and before the disk is touched ----

    /// <summary>A file at <paramref name="full"/> is about to be written: its old bytes saved, or that it (and its missing folders) wasn't there.</summary>
    internal static void Writing(WorkingDirectory directory, string full) => Hook(directory, (journal, log) => journal.RecordWrite(log, full));

    /// <summary>A folder at <paramref name="full"/> (and its missing parents) is about to be made.</summary>
    internal static void Making(WorkingDirectory directory, string full) => Hook(directory, (journal, log) => journal.RecordMissing(log, full, directory: true));

    /// <summary><paramref name="from"/> is about to move to <paramref name="to"/>: a file in the way saved, the missing folders noted, the move recorded.</summary>
    internal static void Moving(WorkingDirectory directory, string from, string to) => Hook(directory, (journal, log) => journal.RecordMove(log, from, to));

    /// <summary><paramref name="full"/> is about to be deleted: a file's bytes, a link, or a folder with everything in it.</summary>
    internal static void Removing(WorkingDirectory directory, string full) => Hook(directory, (journal, log) => journal.RecordRemove(log, full));

    /// <summary>Many paths are about to be written: the <paramref name="folders"/> made where missing, then each of <paramref name="files"/>.</summary>
    internal static void Placing(WorkingDirectory directory, IEnumerable<string> folders, IEnumerable<string> files) => Hook(directory, (journal, log) =>
    {
        foreach (string folder in folders.OrderBy(f => f.Length))
        {
            journal.RecordMissing(log, folder, directory: true);
        }

        foreach (string file in files)
        {
            journal.RecordWrite(log, file);
        }
    });

    private static void Hook(WorkingDirectory directory, Action<FileJournal, JournalLog> record)
    {
        if (s_current.Value is not { } journal || WriteGuard.InAppWrites)
        {
            return;
        }

        string root;
        try
        {
            root = directory.Root;
        }
        catch (Exception ex) when (WorkingDirectory.IsFileFailure(ex))
        {
            return;
        }

        lock (journal._gate)
        {
            if (!journal._logs.TryGetValue(root, out var log))
            {
                return;
            }

            if (journal._sealed)
            {
                journal._incomplete = true;
                DiagnosticLog.Warn(Category, $"A write under {root} came after checkpoint {journal.Id} was sealed; it isn't kept.");
                return;
            }

            try
            {
                record(journal, log);
            }
            catch (Exception ex)
            {
                // Nothing the journal does fails a write (the plan's rule): logged, the write goes ahead.
                DiagnosticLog.Warn(Category, $"The journal could not record a write under {root}: {ex.Message}");
            }
        }
    }

    private void RecordWrite(JournalLog log, string full)
    {
        if (Directory.Exists(full))
        {
            return;
        }

        if (WorkingDirectory.RealLinkTarget(full) is not null)
        {
            AddLink(log, full);
            return;
        }

        if (File.Exists(full))
        {
            AddContent(log, full);
            return;
        }

        RecordMissing(log, full, directory: false);
    }

    /// <summary>An <see cref="CheckpointOp.Absent"/> for each missing folder between the root and <paramref name="full"/>, outermost first, then for it.</summary>
    private void RecordMissing(JournalLog log, string full, bool directory)
    {
        var missing = new List<string>();
        for (string? at = Path.GetDirectoryName(full); at is not null && IsUnder(log.Root, at); at = Path.GetDirectoryName(at))
        {
            if (Directory.Exists(at) || File.Exists(at))
            {
                break;
            }

            missing.Add(at);
        }

        missing.Reverse();
        foreach (string folder in missing)
        {
            Add(log, new CheckpointEntry { Op = CheckpointOp.Absent, Path = RelativeOf(log.Root, folder), Directory = true });
        }

        if (!File.Exists(full) && !Directory.Exists(full))
        {
            Add(log, new CheckpointEntry { Op = CheckpointOp.Absent, Path = RelativeOf(log.Root, full), Directory = directory });
        }
    }

    private void RecordMove(JournalLog log, string from, string to)
    {
        // A case-only rename where case is blind (Transfer's rule): the "existing" destination is the same file, nothing to save.
        bool caseOnly = !string.Equals(from, to, StringComparison.Ordinal) && string.Equals(from, to, StringComparison.OrdinalIgnoreCase)
            && !CaseRule.IsSensitive(Path.GetDirectoryName(to)!);
        if (!caseOnly)
        {
            if (File.Exists(to) && !Directory.Exists(to))
            {
                if (WorkingDirectory.RealLinkTarget(to) is not null)
                {
                    AddLink(log, to);
                }
                else
                {
                    AddContent(log, to);
                }
            }

            RecordMissing(log, to, directory: false);
            // RecordMissing added an Absent for "to" itself, which the move stands for: take it back.
            if (log.Entries.Count > 0 && log.Entries[^1] is { Op: CheckpointOp.Absent } last
                && string.Equals(last.Path, RelativeOf(log.Root, to), StringComparison.Ordinal))
            {
                log.Entries.RemoveAt(log.Entries.Count - 1);
                _count--;
            }
        }

        bool isDirectory = Directory.Exists(from) && WorkingDirectory.RealLinkTarget(from) is null;
        string? hash = null;
        long size = 0;
        if (!isDirectory && File.Exists(from))
        {
            var info = new FileInfo(from);
            size = info.Length;
            if (size <= _largestFile && !WorkingDirectory.IsOffline(from, info.Attributes))
            {
                hash = HashOf(from);
            }
        }

        Add(log, new CheckpointEntry
        {
            Op = CheckpointOp.Moved,
            Path = RelativeOf(log.Root, from),
            To = caseOnly ? CaseRelative(log.Root, to) : RelativeOf(log.Root, to),
            Directory = isDirectory,
            Size = size,
            Hash = hash,
        });
    }

    private void RecordRemove(JournalLog log, string full)
    {
        if (WorkingDirectory.RealLinkTarget(full) is not null)
        {
            AddLink(log, full);
            return;
        }

        if (File.Exists(full))
        {
            AddContent(log, full);
            return;
        }

        if (!Directory.Exists(full))
        {
            return;
        }

        // A folder: itself, then everything in it, outer folders first, never through a link (a link inside goes as a link).
        var pending = new Stack<string>();
        pending.Push(full);
        while (pending.Count > 0)
        {
            string folder = pending.Pop();
            Add(log, new CheckpointEntry { Op = CheckpointOp.Folder, Path = RelativeOf(log.Root, folder), Directory = true });
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateFileSystemEntries(folder).ToList();
            }
            catch (Exception ex) when (WorkingDirectory.IsFileFailure(ex))
            {
                _incomplete = true;
                DiagnosticLog.Warn(Category, $"Could not list {folder} for the checkpoint: {ex.Message}");
                continue;
            }

            var subfolders = new List<string>();
            foreach (string child in children)
            {
                if (WorkingDirectory.RealLinkTarget(child) is not null)
                {
                    AddLink(log, child);
                }
                else if (Directory.Exists(child))
                {
                    subfolders.Add(child);
                }
                else
                {
                    AddContent(log, child);
                }
            }

            for (int i = subfolders.Count - 1; i >= 0; i--)
            {
                pending.Push(subfolders[i]);
            }
        }
    }

    private void AddLink(JournalLog log, string full)
    {
        FileSystemInfo info = Directory.Exists(full) ? new DirectoryInfo(full) : new FileInfo(full);
        Add(log, new CheckpointEntry
        {
            Op = CheckpointOp.Link,
            Path = RelativeOf(log.Root, full),
            Target = info.LinkTarget ?? "",
            Directory = (info.Attributes & FileAttributes.Directory) != 0,
        });
    }

    private void AddContent(JournalLog log, string full)
    {
        var info = new FileInfo(full);
        var entry = new CheckpointEntry
        {
            Op = CheckpointOp.Content,
            Path = RelativeOf(log.Root, full),
            Size = info.Length,
            Attributes = (int)info.Attributes,
        };

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                entry.Mode = (int)File.GetUnixFileMode(full);
            }
            catch (Exception ex) when (WorkingDirectory.IsFileFailure(ex))
            {
                DiagnosticLog.Debug(Category, $"Could not read the mode of {full}: {ex.Message}");
            }
        }

        if (MarkOfTheWeb.Read(full) is { } mark)
        {
            entry.Mark = Convert.ToBase64String(mark);
        }

        if (!Add(log, entry))
        {
            return;
        }

        entry.NotSaved = Save(log, full, info, entry);
    }

    /// <summary>The file's bytes into the store: null when saved (<see cref="CheckpointEntry.Blob"/> and <see cref="CheckpointEntry.Hash"/> set), else the reason.</summary>
    private string? Save(JournalLog log, string full, FileInfo info, CheckpointEntry entry)
    {
        long size = info.Length;
        if (WorkingDirectory.IsOffline(full, info.Attributes))
        {
            return OfflineReason;
        }

        if (size > _largestFile)
        {
            return TooBigReason;
        }

        if (_saved + size > Math.Max(MaxTurnBytes, _largestFile))
        {
            return TurnCapReason;
        }

        if (!Room(log.Root, size))
        {
            DiagnosticLog.Warn(Category, $"No room to keep {entry.Path} ({size.ToString("N0", CultureInfo.InvariantCulture)} bytes) for checkpoint {Id}.");
            return NoRoomReason;
        }

        if (WriterOf(log) is not { } writer)
        {
            return FailedReason;
        }

        try
        {
            string? blob;
            if (size <= WholeReadBytes)
            {
                byte[] bytes = RegularFiles.ReadAllBytes(full);
                entry.Size = bytes.LongLength;
                entry.Hash = Convert.ToHexString(SHA256.HashData(bytes));
                blob = writer.Save(bytes);
            }
            else
            {
                using var file = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var hashing = new HashingStream(file);
                blob = writer.Save(hashing, size);
                entry.Hash = hashing.Hash();
            }

            if (blob is null)
            {
                return FailedReason;
            }

            entry.Blob = blob;
            _saved += entry.Size;
            log.SavedBytes += entry.Size;
            if (_free is { } free)
            {
                _free = free - entry.Size;
            }

            return null;
        }
        catch (Exception ex) when (WorkingDirectory.IsFileFailure(ex))
        {
            DiagnosticLog.Warn(Category, $"Could not keep {entry.Path} for checkpoint {Id}: {ex.Message}");
            return FailedReason;
        }
    }

    /// <summary>The copy tool's own rule: the size plus <see cref="WorkingDirectory.FreeSpaceReserve"/> free, counted down, the drive asked again before a no.</summary>
    private bool Room(string root, long size)
    {
        try
        {
            string probe = _logs.Values.FirstOrDefault(l => l.Writer is IStoreLocated)?.Writer is IStoreLocated located ? located.StorePath : root;
            _free ??= _freeSpace(probe);
            if (size <= _free.Value - WorkingDirectory.FreeSpaceReserve)
            {
                return true;
            }

            _free = _freeSpace(probe);
            return size <= _free.Value - WorkingDirectory.FreeSpaceReserve;
        }
        catch (Exception ex) when (WorkingDirectory.IsFileFailure(ex))
        {
            return true;
        }
    }

    private ICheckpointWriter? WriterOf(JournalLog log)
    {
        if (!log.Opened)
        {
            log.Opened = true;
            try
            {
                log.Writer = _open(log.Root);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Warn(Category, $"Could not open the checkpoint store for {log.Root}: {ex.Message}");
            }
        }

        return log.Writer;
    }

    private bool Add(JournalLog log, CheckpointEntry entry)
    {
        if (_count >= MaxEntries)
        {
            _incomplete = true;
            return false;
        }

        _count++;
        log.Entries.Add(entry);
        _ = WriterOf(log);
        return true;
    }

    /// <summary>
    /// Ends the journal: no write is recorded after it. Returns each root's after-state (what each path the log names holds now), hashing
    /// at most <see cref="MaxHashBytes"/> in all; <paramref name="stop"/> (the app closing) stops the hashing at once and gives null.
    /// </summary>
    public IReadOnlyDictionary<string, List<CheckpointState>>? Seal(CancellationToken stop = default)
    {
        List<JournalLog> logs;
        lock (_gate)
        {
            _sealed = true;
            logs = _logs.Values.Where(l => l.Entries.Count > 0).ToList();
        }

        var after = new Dictionary<string, List<CheckpointState>>(WorkingDirectory.PathComparer);
        long hashed = 0;
        foreach (var log in logs)
        {
            var paths = new List<string>();
            var seen = new HashSet<string>(WorkingDirectory.PathComparer);
            foreach (var entry in log.Entries)
            {
                if (seen.Add(entry.Path))
                {
                    paths.Add(entry.Path);
                }

                if (entry.To is { } to && seen.Add(to))
                {
                    paths.Add(to);
                }
            }

            var states = new List<CheckpointState>(paths.Count);
            foreach (string path in paths)
            {
                if (stop.IsCancellationRequested)
                {
                    return null;
                }

                states.Add(StateOf(log.Root, path, ref hashed));
            }

            after[log.Root] = states;
        }

        return after;
    }

    /// <summary>What stands at <paramref name="relative"/> under <paramref name="root"/> now, a file hashed while <paramref name="hashed"/> stays under the budget.</summary>
    public static CheckpointState StateOf(string root, string relative, ref long hashed, long budget = MaxHashBytes)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(relative);
        string full = FullOf(root, relative);
        var state = new CheckpointState { Path = relative };
        try
        {
            if (WorkingDirectory.RealLinkTarget(full) is not null)
            {
                FileSystemInfo info = Directory.Exists(full) ? new DirectoryInfo(full) : new FileInfo(full);
                state.Kind = CheckpointKind.Link;
                state.Target = info.LinkTarget;
                return state;
            }

            if (Directory.Exists(full))
            {
                state.Kind = CheckpointKind.Folder;
                return state;
            }

            if (!File.Exists(full))
            {
                state.Kind = CheckpointKind.Absent;
                return state;
            }

            var file = new FileInfo(full);
            state.Kind = CheckpointKind.File;
            state.Size = file.Length;
            if (hashed + file.Length <= budget && !WorkingDirectory.IsOffline(full, file.Attributes))
            {
                hashed += file.Length;
                state.Hash = HashOf(full);
            }
            else
            {
                state.Written = file.LastWriteTimeUtc.Ticks;
            }
        }
        catch (Exception ex) when (WorkingDirectory.IsFileFailure(ex))
        {
            // Unreadable now: kept as a file of unknown content, which a restore then counts as a conflict.
            state.Kind = CheckpointKind.File;
            state.Written = -1;
        }

        return state;
    }

    /// <summary>A file's SHA-256 (hex), streamed.</summary>
    public static string HashOf(string full)
    {
        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary><paramref name="full"/> relative to <paramref name="root"/> with <c>/</c> separators, each part as the disk spells it.</summary>
    public static string RelativeOf(string root, string full)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(full);
        string relative = Path.GetRelativePath(root, full);
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string at = root;
        for (int i = 0; i < parts.Length; i++)
        {
            string next = Path.Combine(at, parts[i]);
            if (OnDisk(at, parts[i]) is { } named)
            {
                parts[i] = named;
            }

            at = next;
        }

        return string.Join('/', parts);
    }

    /// <summary>A case-only rename's destination: the name as given (it isn't on the disk under that spelling yet), its folders as the disk spells them.</summary>
    private static string CaseRelative(string root, string full)
    {
        string folder = Path.GetDirectoryName(full)!;
        string parent = string.Equals(folder, root, StringComparison.OrdinalIgnoreCase) ? "" : RelativeOf(root, folder) + "/";
        return parent + Path.GetFileName(full);
    }

    /// <summary>The full path of a stored relative path under <paramref name="root"/>.</summary>
    public static string FullOf(string root, string relative)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(relative);
        return Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static string? OnDisk(string folder, string name)
    {
        try
        {
            if (name.IndexOfAny(['*', '?']) >= 0 || !Directory.Exists(folder))
            {
                return null;
            }

            foreach (string entry in Directory.EnumerateFileSystemEntries(folder, name))
            {
                string found = Path.GetFileName(entry);
                if (string.Equals(found, name, StringComparison.OrdinalIgnoreCase))
                {
                    return found;
                }
            }
        }
        catch (Exception ex) when (WorkingDirectory.IsFileFailure(ex))
        {
            DiagnosticLog.Debug(Category, $"Could not read {name}'s spelling in {folder}: {ex.Message}");
        }

        return null;
    }

    private static bool IsUnder(string root, string path) =>
        path.Length > root.Length && path.StartsWith(root, WorkingDirectory.PathComparer == StringComparer.Ordinal ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)
        && (path[root.Length] == Path.DirectorySeparatorChar || path[root.Length] == Path.AltDirectorySeparatorChar || root.EndsWith(Path.DirectorySeparatorChar));

    private static long DriveFree(string folder)
    {
        string? root = Path.GetPathRoot(Path.GetFullPath(folder));
        return string.IsNullOrEmpty(root) ? long.MaxValue : new DriveInfo(root).AvailableFreeSpace;
    }

    private sealed class Closer(Action close) : IDisposable
    {
        private Action? _close = close;

        public void Dispose()
        {
            Interlocked.Exchange(ref _close, null)?.Invoke();
        }
    }

    /// <summary>A read-only stream that hashes what passes through it (a big file saved streamed).</summary>
    private sealed class HashingStream(Stream inner) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public string Hash() => Convert.ToHexString(_hash.GetHashAndReset());

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = inner.Read(buffer, offset, count);
            _hash.AppendData(buffer, offset, read);
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hash.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

/// <summary>A writer that knows where its store lives, so the journal asks that drive for free space.</summary>
public interface IStoreLocated
{
    string StorePath { get; }
}
