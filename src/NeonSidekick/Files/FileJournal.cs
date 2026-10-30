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
    internal JournalLog(string root)
    {
        Root = root;
        Recorded = new HashSet<string>(CaseRule.Comparer(root));
    }

    /// <summary>The root's full path.</summary>
    public string Root { get; }

    /// <summary>The store's writer, opened at the root's first entry; null before, or when the store could not be opened.</summary>
    public ICheckpointWriter? Writer { get; internal set; }

    internal bool Opened { get; set; }

    public List<CheckpointEntry> Entries { get; } = new();

    /// <summary>The bytes saved for this root this turn.</summary>
    public long SavedBytes { get; internal set; }

    /// <summary>
    /// The paths recorded this turn (2026-10-28, review P1-03/P1-08): a path's first state is the one a restore needs, so a second write to it
    /// saves nothing more. Compared by the root's own case rule (<see cref="CaseRule.Comparer"/>), never the platform's; a move clears it.
    /// </summary>
    internal HashSet<string> Recorded { get; }
}

/// <summary>
/// What a hook added to a journal (2026-10-28, review P4-03), so a write refused after it can take back exactly that: each entry by its log's
/// index, and the bytes it saved (0 for one not saved). The entries themselves, not where the log stood (2026-10-30, review F104): an export
/// holds its mark while other writes mark and take back, and a cut to the old counts took their entries too.
/// </summary>
internal readonly record struct JournalMark(FileJournal? Journal, IReadOnlyList<(int Log, CheckpointEntry Entry, long Bytes)>? Added)
{
    public static JournalMark None => default;
}

/// <summary>
/// A turn's file journal (2026-10-28, the checkpoints plan): before a model tool changes a file through the turn's working directory or the
/// profile's images folder, what was there goes in an ordered op log — the old bytes saved to the checkpoint store (<see cref="CheckpointOp.Content"/>),
/// or that nothing was there (<see cref="CheckpointOp.Absent"/>), a move (<see cref="CheckpointOp.Moved"/>), a folder or a link deleted — so
/// <see cref="FileRestore"/> can replay it backwards. Ambient per tool call (<see cref="Enter"/>, beside <see cref="WriteGuard.Watch"/> in
/// <c>Assistant</c>), never for the whole turn: a task the turn starts outside a tool (the key watcher, a chip's still, a mid-turn pane) is
/// the user's, not the model's. A writer counts only when its own root equals one of the journal's (<see cref="WorkingDirectory.PathComparer"/>),
/// so a share's, a vault's or a temp root is never journaled; the app's own writes (<see cref="WriteGuard.AsApp{T}"/>) aren't either. Each path
/// goes to the log of the longest root holding it (2026-10-28, review P10-04: an images folder inside the working folder was journaled twice),
/// once per turn (review P1-08). Nothing the journal does refuses or fails a write: a save that can't happen is <see cref="CheckpointEntry.NotSaved"/>,
/// logged, and the write goes ahead; a record that fails marks the checkpoint incomplete (review P1-07). <see cref="Seal"/> ends it: a write
/// after that is logged, not recorded, and marks the checkpoint incomplete.
/// </summary>
public sealed class FileJournal
{
    /// <summary>The diagnostics category.</summary>
    public const string Category = "Checkpoints";

    /// <summary>The most entries one turn records; past it the turn's checkpoint is incomplete.</summary>
    public const int MaxEntries = 5_000;

    /// <summary>The most bytes one turn saves (or the largest-file setting when higher, so one such file always fits).</summary>
    public const long MaxTurnBytes = 512L * 1024 * 1024;

    /// <summary>The most bytes one turn hashes in all (its moves and its seal), and one restore's plan; the rest keep size and write time.</summary>
    public const long MaxHashBytes = 512L * 1024 * 1024;

    /// <summary>
    /// The most bytes the seal hashes on the turn's own flow (2026-10-28, review P3-12, the user's pick: no spinner): past it a path keeps its
    /// size and write time, which <see cref="CheckpointState.Matches"/> compares when a hash is missing.
    /// </summary>
    public const long MaxSealHashBytes = 64L * 1024 * 1024;

    /// <summary>A file at most this big is read whole; a bigger one is streamed into the store.</summary>
    public const long WholeReadBytes = 64L * 1024 * 1024;

    // NotSaved reasons, kept in the JSON (internal words, never shown as they are).
    public const string TooBigReason = "too-big";
    public const string TurnCapReason = "turn-cap";
    public const string OfflineReason = "online-only";
    public const string NoRoomReason = "no-room";
    public const string FailedReason = "failed";

    private static readonly AsyncLocal<Scope?> s_current = new();

    private readonly object _gate = new();
    private readonly List<JournalLog> _logs = new();
    private readonly HashSet<WorkingDirectory> _directories = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, string> _spellings = new(StringComparer.Ordinal);
    private readonly Func<string, ICheckpointWriter?> _open;
    private readonly Func<string, long> _freeSpace;
    private readonly long _largestFile;
    private readonly int _maxEntries;
    private readonly List<string> _tools = new();

    // Each log's entry count when the running hook began (F104): an entry before it is an earlier hook's, which a skip relies on.
    private int[]? _hookCounts;
    private int _count;
    private long _saved;
    private long _hashed;
    private long? _free;
    private bool _sealed;
    private bool _incomplete;

    /// <param name="id">The checkpoint id, shared by every root's checkpoint of the turn.</param>
    /// <param name="roots">The roots whose writers are journaled (the working folder's and the images folder's).</param>
    /// <param name="open">Opens a root's store writer at its first entry; null on a store failure.</param>
    /// <param name="largestFileBytes">The <c>File checkpoints per file (MB)</c> setting in bytes.</param>
    /// <param name="freeSpace">The free bytes on a folder's drive (<see cref="DriveInfo.AvailableFreeSpace"/>; the tests' fake).</param>
    public FileJournal(string id, IEnumerable<string> roots, Func<string, ICheckpointWriter?> open, long largestFileBytes, Func<string, long>? freeSpace = null)
        : this(id, roots, open, largestFileBytes, freeSpace, MaxEntries)
    {
    }

    /// <param name="maxEntries">The entry cap (<see cref="MaxEntries"/>; lower in the tests).</param>
    internal FileJournal(string id, IEnumerable<string> roots, Func<string, ICheckpointWriter?> open, long largestFileBytes, Func<string, long>? freeSpace, int maxEntries)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(roots);
        Id = id;
        _open = open ?? throw new ArgumentNullException(nameof(open));
        _largestFile = largestFileBytes;
        _freeSpace = freeSpace ?? DriveFree;
        _maxEntries = maxEntries;
        foreach (string root in roots)
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if (!_logs.Any(l => WorkingDirectory.PathComparer.Equals(l.Root, full)))
            {
                _logs.Add(new JournalLog(full));
            }
        }
    }

    /// <summary>The checkpoint id.</summary>
    public string Id { get; }

    /// <summary>The journal entered on this flow (one tool call), or null.</summary>
    public static FileJournal? Current => s_current.Value?.Journal;

    /// <summary>
    /// The journal for the call inside (null = none); the previous one comes back on dispose. A task the call starts carries the scope with
    /// it (2026-10-28, review P4-11): once the call is over and the journal sealed, its writes are let go quietly, never kept nor warned of.
    /// </summary>
    public static IDisposable Enter(FileJournal? journal)
    {
        var previous = s_current.Value;
        var scope = journal is null ? null : new Scope(journal);
        s_current.Value = scope;
        return new Closer(() =>
        {
            if (scope is not null)
            {
                scope.Open = false;
            }

            s_current.Value = previous;
        });
    }

    /// <summary>
    /// The turn's own working directories (2026-10-28, review P11-02): one of them whose root no longer is a journal's (a <c>/cd</c> mid-turn)
    /// marks the checkpoint incomplete instead of going unjournaled unseen.
    /// </summary>
    public void Watch(params WorkingDirectory[] directories)
    {
        ArgumentNullException.ThrowIfNull(directories);
        lock (_gate)
        {
            foreach (var directory in directories)
            {
                _directories.Add(directory);
            }
        }
    }

    /// <summary>Each root's log (a copy of the list).</summary>
    public IReadOnlyList<JournalLog> Logs
    {
        get
        {
            lock (_gate)
            {
                return _logs.ToList();
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

    /// <summary>Some of the turn's changes weren't kept: a write after the seal, past <see cref="MaxEntries"/>, a record or a store that failed.</summary>
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
    internal static JournalMark Writing(WorkingDirectory directory, string full) =>
        Hook(directory, journal => journal.LogFor(full) is { } log ? journal.RecordWrite(log, full) : null);

    /// <summary>A folder at <paramref name="full"/> (and its missing parents) is about to be made.</summary>
    internal static JournalMark Making(WorkingDirectory directory, string full) =>
        Hook(directory, journal => journal.LogFor(full) is { } log ? journal.RecordMissing(log, full, directory: true) : null);

    /// <summary><paramref name="from"/> is about to move to <paramref name="to"/>: a file in the way saved, the missing folders noted, the move recorded.</summary>
    internal static JournalMark Moving(WorkingDirectory directory, string from, string to) =>
        Hook(directory, journal => journal.RecordMove(from, to));

    /// <summary><paramref name="full"/> is about to be deleted: a file's bytes, a link, or a folder with everything in it.</summary>
    internal static JournalMark Removing(WorkingDirectory directory, string full) =>
        Hook(directory, journal => journal.LogFor(full) is { } log ? journal.RecordRemove(log, full) : null);

    /// <summary>Many paths are about to be written: the <paramref name="folders"/> made where missing, then each of <paramref name="files"/>.</summary>
    internal static JournalMark Placing(WorkingDirectory directory, IEnumerable<string> folders, IEnumerable<string> files) =>
        Hook(directory, journal =>
        {
            journal.RecordPlacing(folders, files);
            return null;
        });

    /// <summary>
    /// A folder copied over the file <paramref name="file"/> in its way (2026-10-28, review P3-02): the file saved, the folder noted as made
    /// (the file still stands at the hook, so <see cref="Placing"/> alone saw nothing missing), then its folders and files.
    /// </summary>
    internal static JournalMark Replacing(WorkingDirectory directory, string file, IEnumerable<string> folders, IEnumerable<string> files) =>
        Hook(directory, journal =>
        {
            if (journal.LogFor(file) is { } log)
            {
                journal.RecordRemove(log, file);
                journal.Add(log, new CheckpointEntry { Op = CheckpointOp.Absent, Path = journal.Relative(log, file), Directory = true }, force: true);
            }

            journal.RecordPlacing(folders, files);
            return null;
        });

    /// <summary>
    /// Takes back what a hook added (2026-10-28, review P4-03): the write it stood for was refused after it (a file appeared, a link moved),
    /// so the journal never describes a write that didn't happen. A blob it saved stays for the prune. Only the hook's own entries, wherever
    /// they sit now, whatever was recorded or taken back since (2026-10-30, review F104); one a later write relied on stays, with its bytes.
    /// </summary>
    internal static void TakeBack(JournalMark mark)
    {
        if (mark.Journal is not { } journal || mark.Added is not { } added)
        {
            return;
        }

        lock (journal._gate)
        {
            for (int i = added.Count - 1; i >= 0; i--)
            {
                var (at, entry, bytes) = added[i];
                var log = journal._logs[at];
                int index = log.Entries.FindLastIndex(e => ReferenceEquals(e, entry));
                if (entry.Relied || index < 0)
                {
                    continue;
                }

                log.Entries.RemoveAt(index);
                journal._count--;
                log.SavedBytes -= bytes;
                journal._saved -= bytes;
                if (!log.Entries.Exists(e => log.Recorded.Comparer.Equals(e.Path, entry.Path)))
                {
                    log.Recorded.Remove(entry.Path);
                }
            }
        }
    }

    private static JournalMark Hook(WorkingDirectory directory, Func<FileJournal, string?> record)
    {
        if (s_current.Value is not { } scope || WriteGuard.InAppWrites)
        {
            return JournalMark.None;
        }

        var journal = scope.Journal;
        string root;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory.Root));
        }
        catch (Exception ex) when (WorkingDirectory.IsFileFailure(ex) || ex is ArgumentException or NotSupportedException)
        {
            return JournalMark.None;
        }

        lock (journal._gate)
        {
            if (journal._sealed)
            {
                if (!scope.Open)
                {
                    // A task a call started, writing after the turn ended: the user's now, not the turn's (review P4-11).
                    DiagnosticLog.Debug(Category, $"A write under {root} after checkpoint {journal.Id}'s turn ended is not the turn's.");
                    return JournalMark.None;
                }

                journal._incomplete = true;
                DiagnosticLog.Warn(Category, $"A write under {root} came after checkpoint {journal.Id} was sealed; it isn't kept.");
                return JournalMark.None;
            }

            if (!journal._logs.Any(l => WorkingDirectory.PathComparer.Equals(l.Root, root)))
            {
                if (journal._directories.Contains(directory))
                {
                    // The turn's own folder under another name now (review P11-02): its write can't be put back.
                    journal._incomplete = true;
                    DiagnosticLog.Warn(Category, $"The working folder {root} is no longer checkpoint {journal.Id}'s; its writes aren't kept.");
                }

                return JournalMark.None;
            }

            int[] counts = journal._logs.Select(l => l.Entries.Count).ToArray();
            journal._hookCounts = counts;
            try
            {
                if (record(journal) is { } problem)
                {
                    journal._incomplete = true;
                    DiagnosticLog.Warn(Category, problem);
                }
            }
            catch (Exception ex)
            {
                // Nothing the journal does fails a write (the plan's rule): logged, the write goes ahead, the checkpoint incomplete (review P1-07).
                journal._incomplete = true;
                DiagnosticLog.Warn(Category, $"The journal could not record a write under {root}: {ex.Message}");
            }
            finally
            {
                journal._hookCounts = null;
            }

            return journal.AddedSince(counts);
        }
    }

    /// <summary>
    /// The mark of the entries each log gained past <paramref name="counts"/>, under the gate (F104). A content's bytes count when its save
    /// succeeded (<see cref="CheckpointEntry.NotSaved"/> null), as <see cref="Save"/> counts them; a failed save set its size without.
    /// </summary>
    private JournalMark AddedSince(int[] counts)
    {
        var added = new List<(int, CheckpointEntry, long)>();
        for (int i = 0; i < _logs.Count; i++)
        {
            var entries = _logs[i].Entries;
            for (int at = i < counts.Length ? counts[i] : 0; at < entries.Count; at++)
            {
                var entry = entries[at];
                added.Add((i, entry, entry.Op == CheckpointOp.Content && entry.NotSaved is null ? entry.Size : 0));
            }
        }

        return new JournalMark(this, added);
    }

    /// <summary>
    /// A path the turn recorded already, so this write records nothing: its newest entry, when an earlier hook added it, is now relied on
    /// (F104) and stays if that hook is taken back. One the running hook added itself isn't: taking it back takes the whole write back.
    /// </summary>
    private void Rely(JournalLog log, string relative)
    {
        int index = log.Entries.FindLastIndex(e => log.Recorded.Comparer.Equals(e.Path, relative));
        int logIndex = _logs.IndexOf(log);
        if (index >= 0 && (_hookCounts is not { } counts || logIndex >= counts.Length || index < counts[logIndex]))
        {
            log.Entries[index].Relied = true;
        }
    }

    /// <summary>The log of the longest root holding <paramref name="full"/> (review P10-04: nested roots), or null when none does.</summary>
    private JournalLog? LogFor(string full)
    {
        JournalLog? best = null;
        foreach (var log in _logs)
        {
            if (WorkingDirectory.IsInside(log.Root, full) && (best is null || log.Root.Length > best.Root.Length))
            {
                best = log;
            }
        }

        return best;
    }

    private void RecordPlacing(IEnumerable<string> folders, IEnumerable<string> files)
    {
        foreach (string folder in folders.OrderBy(f => f.Length))
        {
            if (LogFor(folder) is { } log)
            {
                RecordMissing(log, folder, directory: true);
            }
        }

        foreach (string file in files)
        {
            if (LogFor(file) is { } log)
            {
                RecordWrite(log, file);
            }
        }
    }

    private string? RecordWrite(JournalLog log, string full)
    {
        if (Directory.Exists(full))
        {
            return null;
        }

        if (WorkingDirectory.RealLinkTarget(full) is not null)
        {
            AddLink(log, full);
            return null;
        }

        if (File.Exists(full))
        {
            AddContent(log, full);
            return null;
        }

        return RecordMissing(log, full, directory: false);
    }

    /// <summary>
    /// An <see cref="CheckpointOp.Absent"/> for each missing folder between the root and <paramref name="full"/>, outermost first, then for it
    /// unless <paramref name="self"/> is off (a move's destination: the move stands for it, review P13-01). A folder recorded this turn stops
    /// the climb: its own ancestors were noted with it (review P1-03).
    /// </summary>
    private string? RecordMissing(JournalLog log, string full, bool directory, bool self = true)
    {
        if (Capped())
        {
            return null;
        }

        var missing = new List<string>();
        for (string? at = Path.GetDirectoryName(full); at is not null && Below(log.Root, at); at = Path.GetDirectoryName(at))
        {
            if (Directory.Exists(at) || File.Exists(at) || log.Recorded.Contains(Relative(log, at)))
            {
                break;
            }

            missing.Add(at);
        }

        missing.Reverse();
        foreach (string folder in missing)
        {
            Add(log, new CheckpointEntry { Op = CheckpointOp.Absent, Path = Relative(log, folder), Directory = true });
        }

        if (self && !File.Exists(full) && !Directory.Exists(full))
        {
            Add(log, new CheckpointEntry { Op = CheckpointOp.Absent, Path = Relative(log, full), Directory = directory });
        }

        return null;
    }

    private string? RecordMove(string from, string to)
    {
        var fromLog = LogFor(from);
        var toLog = LogFor(to);
        if (fromLog is null || toLog is null)
        {
            return null;
        }

        if (!ReferenceEquals(fromLog, toLog))
        {
            // Across two roots (review P10-04): the destination noted as made, the source saved as deleted; a folder's contents can't follow.
            RecordWrite(toLog, to);
            RecordRemove(fromLog, from);
            return Directory.Exists(from) ? $"A folder moved between {fromLog.Root} and {toLog.Root} can't be put back whole." : null;
        }

        var log = fromLog;
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

            RecordMissing(log, to, directory: false, self: false);
        }

        bool isDirectory = Directory.Exists(from) && WorkingDirectory.RealLinkTarget(from) is null;
        string? hash = null;
        long size = 0;
        if (!isDirectory && File.Exists(from))
        {
            var info = new FileInfo(from);
            size = info.Length;
            // One budget for the turn's hashing, the moves' and the seal's (review P3-14): fifty 1 GB videos aren't read whole under the lock.
            if (size <= _largestFile && _hashed + size <= MaxHashBytes && !WorkingDirectory.IsOffline(from, info.Attributes))
            {
                _hashed += size;
                hash = HashOf(from);
            }
        }

        if (Add(log, new CheckpointEntry
            {
                Op = CheckpointOp.Moved,
                Path = Relative(log, from),
                To = caseOnly ? CaseRelative(log, to) : Relative(log, to),
                Directory = isDirectory,
                Size = size,
                Hash = hash,
            }, force: true))
        {
            // The paths under a move are other paths now: each recorded again at its next write, and every spelling read again.
            log.Recorded.Clear();
            _spellings.Clear();
        }

        return null;
    }

    private string? RecordRemove(JournalLog log, string full)
    {
        if (WorkingDirectory.RealLinkTarget(full) is not null)
        {
            AddLink(log, full);
            return null;
        }

        if (File.Exists(full))
        {
            AddContent(log, full);
            return null;
        }

        if (!Directory.Exists(full))
        {
            return null;
        }

        // A folder: itself, then everything in it, outer folders first, never through a link (a link inside goes as a link). The walk stops at
        // the entry cap (review P1-13): nothing past it is probed under the lock.
        var pending = new Stack<string>();
        pending.Push(full);
        while (pending.Count > 0 && !Capped())
        {
            string folder = pending.Pop();
            Add(log, new CheckpointEntry { Op = CheckpointOp.Folder, Path = Relative(log, folder), Directory = true });
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
                if (Capped())
                {
                    break;
                }

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

        // A deleted folder's names are gone: read again if made anew.
        _spellings.Clear();
        return null;
    }

    private void AddLink(JournalLog log, string full)
    {
        if (Capped())
        {
            return;
        }

        string relative = Relative(log, full);
        if (log.Recorded.Contains(relative))
        {
            Rely(log, relative);
            return;
        }

        FileSystemInfo info = Directory.Exists(full) ? new DirectoryInfo(full) : new FileInfo(full);
        Add(log, new CheckpointEntry
        {
            Op = CheckpointOp.Link,
            Path = relative,
            Target = info.LinkTarget ?? "",
            Directory = (info.Attributes & FileAttributes.Directory) != 0,
        });
    }

    private void AddContent(JournalLog log, string full)
    {
        // The cap and the path's first touch before any probe (review P1-13, P1-08): a big folder's delete past the cap reads nothing more.
        if (Capped())
        {
            return;
        }

        string relative = Relative(log, full);
        if (log.Recorded.Contains(relative))
        {
            Rely(log, relative);
            return;
        }

        var info = new FileInfo(full);
        var entry = new CheckpointEntry
        {
            Op = CheckpointOp.Content,
            Path = relative,
            Size = info.Length,
            Attributes = (int)info.Attributes,
            // Until the save says otherwise (review P10-03): a throw it didn't expect leaves a clean "not saved", never a missing blob.
            NotSaved = FailedReason,
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
                // The length the opened file has (review P11-07): a file still being written is saved as it stood, or not at all.
                using var file = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                long length = file.Length;
                entry.Size = length;
                using var hashing = new HashingStream(file, length);
                blob = writer.Save(hashing, length);
                entry.Hash = hashing.Hash();
                if (file.Length != length || hashing.Passed != length)
                {
                    DiagnosticLog.Warn(Category, $"{entry.Path} changed while it was kept for checkpoint {Id}; it isn't kept.");
                    entry.Hash = null;
                    return FailedReason;
                }
            }

            if (blob is null)
            {
                entry.Hash = null;
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
            entry.Hash = null;
            return FailedReason;
        }
    }

    /// <summary>The copy tool's own rule: the size plus <see cref="WorkingDirectory.FreeSpaceReserve"/> free, counted down, the drive asked again before a no.</summary>
    private bool Room(string root, long size)
    {
        try
        {
            string probe = _logs.FirstOrDefault(l => l.Writer is IStoreLocated)?.Writer is IStoreLocated located ? located.StorePath : root;
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

    /// <summary>The root's writer, opened at its first entry; a store that won't open marks the checkpoint incomplete (2026-10-28, review P4-01).</summary>
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

            if (log.Writer is null)
            {
                _incomplete = true;
            }
        }

        return log.Writer;
    }

    /// <summary>Whether the entry cap is reached (marking the checkpoint incomplete): checked before anything is probed (review P1-13).</summary>
    private bool Capped()
    {
        if (_count < _maxEntries)
        {
            return false;
        }

        _incomplete = true;
        return true;
    }

    /// <summary>
    /// One entry into the log, once per path per turn (review P1-08: each write to one file saved a whole copy); <paramref name="force"/> for
    /// a move and the folder a copy puts over a file, which stand beside the path's first entry.
    /// </summary>
    private bool Add(JournalLog log, CheckpointEntry entry, bool force = false)
    {
        if (Capped())
        {
            return false;
        }

        if (!log.Recorded.Add(entry.Path) && !force)
        {
            Rely(log, entry.Path);
            return false;
        }

        _count++;
        log.Entries.Add(entry);
        _ = WriterOf(log);
        return true;
    }

    /// <summary>
    /// Ends the journal: no write is recorded after it. Returns each root's after-state (what each path the log names holds now), hashing
    /// at most <see cref="MaxSealHashBytes"/> on the turn's flow, within the turn's <see cref="MaxHashBytes"/>; <paramref name="stop"/> (the app
    /// closing) stops the hashing at once and gives null.
    /// </summary>
    public IReadOnlyDictionary<string, List<CheckpointState>>? Seal(CancellationToken stop = default)
    {
        List<JournalLog> logs;
        long hashed;
        lock (_gate)
        {
            _sealed = true;
            logs = _logs.Where(l => l.Entries.Count > 0).ToList();
            hashed = _hashed;
        }

        long budget = Math.Min(MaxHashBytes, hashed + MaxSealHashBytes);
        var after = new Dictionary<string, List<CheckpointState>>(WorkingDirectory.PathComparer);
        foreach (var log in logs)
        {
            var paths = new List<string>();
            // Spellings as the disk has them already (review P8-12): ordinal, so a case-only rename's new name keeps its own state.
            var seen = new HashSet<string>(StringComparer.Ordinal);
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

                states.Add(StateOf(log.Root, path, ref hashed, budget));
            }

            after[log.Root] = states;
        }

        lock (_gate)
        {
            _hashed = hashed;
        }

        return after;
    }

    /// <summary>
    /// What stands at <paramref name="relative"/> under <paramref name="root"/> now, a file hashed while <paramref name="hashed"/> stays under
    /// the budget, its write time always kept (2026-10-28, review P1-06), <see cref="CheckpointState.UnknownWritten"/> when it can't be read.
    /// </summary>
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
            state.Written = file.LastWriteTimeUtc.Ticks;
            if (hashed + file.Length <= budget && !WorkingDirectory.IsOffline(full, file.Attributes))
            {
                hashed += file.Length;
                state.Hash = HashOf(full);
            }
        }
        catch (Exception ex) when (WorkingDirectory.IsFileFailure(ex))
        {
            // Unreadable now: kept as a file of unknown content, which a restore then counts as a conflict.
            state.Kind = CheckpointKind.File;
            state.Hash = null;
            state.Written = CheckpointState.UnknownWritten;
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

    /// <summary>
    /// <see cref="RelativeOf"/> with the folders' spellings kept for the turn (2026-10-28, review P2-13: every journaled path read every
    /// ancestor folder again, under the lock); a move or a folder's delete clears them.
    /// </summary>
    private string Relative(JournalLog log, string full)
    {
        string? folder = Path.GetDirectoryName(full);
        if (folder is null || !Below(log.Root, full))
        {
            return RelativeOf(log.Root, full);
        }

        string prefix;
        if (WorkingDirectory.PathComparer.Equals(Path.TrimEndingDirectorySeparator(folder), log.Root))
        {
            prefix = "";
        }
        else
        {
            if (!_spellings.TryGetValue(folder, out string? spelled))
            {
                spelled = RelativeOf(log.Root, folder);
                _spellings[folder] = spelled;
            }

            prefix = spelled + "/";
        }

        string name = Path.GetFileName(full);
        return prefix + (OnDisk(folder, name) ?? name);
    }

    /// <summary>A case-only rename's destination: the name as given (it isn't on the disk under that spelling yet), its folders as the disk spells them.</summary>
    private string CaseRelative(JournalLog log, string full)
    {
        string folder = Path.GetDirectoryName(full)!;
        string parent = string.Equals(Path.TrimEndingDirectorySeparator(folder), log.Root, StringComparison.OrdinalIgnoreCase) ? "" : Relative(log, folder) + "/";
        return parent + Path.GetFileName(full);
    }

    /// <summary>The full path of a stored relative path under <paramref name="root"/>.</summary>
    public static string FullOf(string root, string relative)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(relative);
        return Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>
    /// <paramref name="name"/> as <paramref name="folder"/> spells it: the exact name first (2026-10-28, review P9-01: the first case-blind
    /// match took A.txt's bytes for a.txt), and in a case-sensitive folder the name as given, which is a different file from any other case.
    /// </summary>
    private static string? OnDisk(string folder, string name)
    {
        try
        {
            if (name.IndexOfAny(['*', '?']) >= 0 || !Directory.Exists(folder) || CaseRule.IsSensitive(folder))
            {
                return null;
            }

            string? blind = null;
            foreach (string entry in Directory.EnumerateFileSystemEntries(folder, name))
            {
                string found = Path.GetFileName(entry);
                if (string.Equals(found, name, StringComparison.Ordinal))
                {
                    return found;
                }

                if (blind is null && string.Equals(found, name, StringComparison.OrdinalIgnoreCase))
                {
                    blind = found;
                }
            }

            return blind;
        }
        catch (Exception ex) when (WorkingDirectory.IsFileFailure(ex))
        {
            DiagnosticLog.Debug(Category, $"Could not read {name}'s spelling in {folder}: {ex.Message}");
        }

        return null;
    }

    /// <summary>Whether <paramref name="path"/> lies under <paramref name="root"/>, never the root itself (<see cref="WorkingDirectory.IsInside"/>'s rule).</summary>
    private static bool Below(string root, string path) =>
        WorkingDirectory.IsInside(root, path) && !WorkingDirectory.PathComparer.Equals(Path.TrimEndingDirectorySeparator(path), root);

    private static long DriveFree(string folder)
    {
        string? root = Path.GetPathRoot(Path.GetFullPath(folder));
        return string.IsNullOrEmpty(root) ? long.MaxValue : new DriveInfo(root).AvailableFreeSpace;
    }

    /// <summary>One <see cref="Enter"/>'s journal and whether its call is still running (review P4-11, <see cref="WriteGuard"/>'s pattern).</summary>
    private sealed class Scope(FileJournal journal)
    {
        public FileJournal Journal { get; } = journal;

        public volatile bool Open = true;
    }

    private sealed class Closer(Action close) : IDisposable
    {
        private Action? _close = close;

        public void Dispose()
        {
            Interlocked.Exchange(ref _close, null)?.Invoke();
        }
    }

    /// <summary>A read-only stream that hashes what passes through it (a big file saved streamed), never past <paramref name="length"/>.</summary>
    private sealed class HashingStream(Stream inner, long length) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        /// <summary>The bytes read through.</summary>
        public long Passed { get; private set; }

        public string Hash() => Convert.ToHexString(_hash.GetHashAndReset());

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position { get => Passed; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = inner.Read(buffer, offset, (int)Math.Min(count, length - Passed));
            _hash.AppendData(buffer, offset, read);
            Passed += read;
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
