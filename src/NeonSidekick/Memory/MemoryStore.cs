using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NeonSidekick.Diagnostics;

namespace NeonSidekick.Memory;

/// <summary>One remembered fact and when it was saved.</summary>
public sealed class MemoryEntry
{
    public string Text { get; set; } = "";

    public DateTimeOffset SavedAt { get; set; }

    /// <summary>
    /// Kept but not offered to the model (2026-10-13, the user's ask: the <c>/memory</c> pane's disable): left out of
    /// <see cref="MemoryStore.Snapshot"/> — the system prompt, <c>recall_memory</c>, a bot's memories — while the pane still lists it,
    /// greyed, to edit, enable or forget. Written only when true, so a file without disabled memories reads as it always did.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Disabled { get; set; }
}

/// <summary>The on-disk shape of <c>memory.json</c>: a version and the entries, oldest first.</summary>
public sealed class MemoryFile
{
    /// <summary>Bumped when an entry changes meaning rather than merely gaining a field.</summary>
    public int SchemaVersion { get; set; } = 1;

    public List<MemoryEntry> Entries { get; set; } = new();
}

/// <summary>What <see cref="MemoryStore.Add"/> did.</summary>
public enum MemoryAddOutcome
{
    Added,

    /// <summary>The same text (ignoring case and whitespace) is already stored; nothing changed.</summary>
    Duplicate,

    /// <summary>The store holds <see cref="MemoryStore.MaxEntries"/> already; nothing changed.</summary>
    Full,

    /// <summary>Nothing but whitespace was offered; nothing changed.</summary>
    Empty,

    /// <summary>The file could not be written; the store is as it was and the failure is in the log.</summary>
    Failed,

    /// <summary>
    /// The same text is stored but disabled (2026-10-13, the code review): kept, not offered to the model; nothing changed. What the
    /// model's <c>save_memory</c> gets, so it is never told "already remembered" of a fact it cannot see.
    /// </summary>
    Disabled,

    /// <summary>The same text was stored disabled and is enabled again (2026-10-13): the user's own add (<c>/remember</c>, the pane's + add).</summary>
    Enabled,
}

/// <summary>What <see cref="MemoryStore.Edit"/> did (2026-10-13, the <c>/memory</c> pane's edit).</summary>
public enum MemoryEditOutcome
{
    Edited,

    /// <summary>The new text is the old one (ignoring nothing but the normalising); nothing changed.</summary>
    Unchanged,

    /// <summary>Another memory already holds the new text (ignoring case and whitespace); nothing changed.</summary>
    Duplicate,

    /// <summary>Nothing but whitespace was offered; nothing changed.</summary>
    Empty,

    /// <summary>No memory holds the old text any more (changed on disk meanwhile); nothing changed.</summary>
    Missing,
}

/// <summary>The outcome of an edit and the text as stored (or the other memory's, for a duplicate).</summary>
public readonly record struct MemoryEditResult(MemoryEditOutcome Outcome, string Text);

/// <summary>The outcome of an add and the text as it was (or would have been) stored.</summary>
public readonly record struct MemoryAddResult(MemoryAddOutcome Outcome, string Text);

/// <summary>
/// What <see cref="MemoryStore.Import"/> did: how many entries were written, how many were skipped
/// as already there (the target's, or an earlier one of the same batch), and how many did not fit
/// under <see cref="MemoryStore.MaxEntries"/>.
/// </summary>
public readonly record struct MemoryImportResult(int Added, int Duplicates, int Dropped);

/// <summary>
/// Long-term memory: a short list of facts about the user that outlives the conversation and the
/// process. One file in the profile's <c>configs</c> folder (beside its <c>profile.json</c> until 2026-10-16), loaded on first use, read again when it changed
/// outside the app (<c>/memory edit</c>, 2026-09-23) and written through on every change, so what
/// the store reports is what the disk holds.
///
/// <para>Three rules, each from the reference's conversation log. <b>One file</b>, so a clear is
/// one delete and "forgotten" is honest. <b>Stored text is flattened</b> (whitespace collapsed,
/// newlines gone, a hard length cap): recalled text goes into the system prompt and must not be
/// able to carry markdown structure or a second paragraph into it. <b>A clear that fails throws</b>:
/// reporting success for a delete that did not happen is the one failure a forget path must
/// never have. An add that fails, by contrast, reports <see cref="MemoryAddOutcome.Failed"/> and
/// leaves the store as it was.</para>
///
/// <para>Every write is synchronous and under one lock: the tool that saves a memory answers the
/// model "remembered" only once the file is on disk, and the shell's <c>/remember</c> shares the
/// same instance.</para>
/// </summary>
public sealed class MemoryStore
{
    public const string FileName = "memory.json";

    /// <summary>The ceiling on stored entries; the prompt grows with every one.</summary>
    public const int MaxEntries = 200;

    /// <summary>Characters kept per entry; longer text is cut with an ellipsis.</summary>
    public const int MaxTextLength = 300;

    private const string Category = "Memory";

    /// <summary>
    /// A change's log line: what happened and the memory's length, never its text (2026-10-19, the code review: the Web UI's audit lines left
    /// the text out because the log is shareable, and the store's own lines, written just before them, put it back).
    /// </summary>
    internal static string ChangeLogLine(string verb, string text) =>
        verb + " a memory (" + text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + " characters).";

    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly TimeProvider _time;
    private List<MemoryEntry>? _entries;

    /// <summary>
    /// The file as <see cref="_entries"/> last saw it (write time and length; null for no file), so
    /// an edit made outside the app is read back before the next use rather than written over
    /// (2026-09-23, with <c>/memory edit</c>, the user's ask). Caller holds the lock.
    /// </summary>
    private (DateTime Written, long Length)? _stamp;

    /// <summary>The stamp of a changed file that would not parse, warned about once; see <see cref="Entries"/>.</summary>
    private (DateTime Written, long Length)? _warnedStamp;

    /// <param name="directory">The settings directory; the file is <see cref="FileName"/> under it.</param>
    /// <param name="time">The clock behind <see cref="MemoryEntry.SavedAt"/>; tests pass a fake.</param>
    public MemoryStore(string directory, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _filePath = NeonSidekick.Settings.Profiles.ConfigPath(Path.GetFullPath(directory), FileName);   // <profile>\configs since 2026-10-16
        _time = time ?? TimeProvider.System;
    }

    public string FilePath => _filePath;

    /// <summary>How many memories are stored.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return Entries().Count;
            }
        }
    }

    /// <summary>The texts the model is offered, oldest first, as a copy: every stored one but the disabled (2026-10-13).</summary>
    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
        {
            return Entries().Where(e => !e.Disabled).Select(e => e.Text).ToArray();
        }
    }

    /// <summary>The stored entries with their dates, oldest first, as copies, the disabled too; the <c>/memory</c> menu's view.</summary>
    public IReadOnlyList<MemoryEntry> EntriesSnapshot()
    {
        lock (_gate)
        {
            return Entries().Select(e => new MemoryEntry { Text = e.Text, SavedAt = e.SavedAt, Disabled = e.Disabled }).ToArray();
        }
    }

    /// <summary>
    /// One memory's text changed (2026-10-13, the user's ask: the <c>/memory</c> pane's edit): <paramref name="oldText"/> matched as
    /// <see cref="Remove"/> matches, <paramref name="newText"/> normalised as <see cref="Add"/> stores it, its date and its disabled mark
    /// kept. A text another memory holds is a duplicate and nothing changes. A rewrite that fails
    /// puts the old text back and throws, as <see cref="Remove"/> does.
    /// </summary>
    public MemoryEditResult Edit(string? oldText, string? newText)
    {
        string normalized = Normalize(newText);
        string old = Normalize(oldText);
        if (normalized.Length == 0)
        {
            return new(MemoryEditOutcome.Empty, normalized);
        }

        lock (_gate)
        {
            var entries = Entries();
            var entry = entries.FirstOrDefault(e => string.Equals(e.Text, old, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                return new(MemoryEditOutcome.Missing, normalized);
            }

            if (string.Equals(entry.Text, normalized, StringComparison.Ordinal))
            {
                return new(MemoryEditOutcome.Unchanged, normalized);
            }

            if (entries.FirstOrDefault(e => !ReferenceEquals(e, entry) && string.Equals(e.Text, normalized, StringComparison.OrdinalIgnoreCase)) is { } other)
            {
                return new(MemoryEditOutcome.Duplicate, other.Text);
            }

            string before = entry.Text;
            entry.Text = normalized;
            try
            {
                Save(entries);
            }
            catch
            {
                entry.Text = before;
                throw;
            }

            DiagnosticLog.Info(Category, ChangeLogLine("Edited", normalized));
            return new(MemoryEditOutcome.Edited, normalized);
        }
    }

    /// <summary>
    /// One memory disabled or enabled again (2026-10-13, the user's ask): matched as <see cref="Remove"/> matches. False when nothing
    /// matched or it already was so, and nothing was written. A rewrite that fails puts the mark back and throws, as <see cref="Remove"/> does.
    /// </summary>
    public bool SetDisabled(string? text, bool disabled)
    {
        string normalized = Normalize(text);
        lock (_gate)
        {
            var entries = Entries();
            var entry = entries.FirstOrDefault(e => string.Equals(e.Text, normalized, StringComparison.OrdinalIgnoreCase));
            if (entry is null || entry.Disabled == disabled)
            {
                return false;
            }

            entry.Disabled = disabled;
            try
            {
                Save(entries);   // the list the entry is in: a second Entries() could read the file again and save it without the change (2026-10-13, the code review)
            }
            catch
            {
                entry.Disabled = !disabled;
                throw;
            }

            DiagnosticLog.Info(Category, ChangeLogLine(disabled ? "Disabled" : "Enabled", entry.Text));
            return true;
        }
    }

    /// <summary>
    /// Forgets one memory, matched the way <see cref="Add"/> matches a duplicate (normalised,
    /// ignoring case). False when nothing matched and nothing was written. A rewrite that fails
    /// puts the entry back and throws (<see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/>), like <see cref="Clear"/>: a removal that did not
    /// happen is never reported as done.
    /// </summary>
    public bool Remove(string? text)
    {
        string normalized = Normalize(text);
        if (normalized.Length == 0)
        {
            return false;
        }

        lock (_gate)
        {
            var entries = Entries();
            int index = entries.FindIndex(e => string.Equals(e.Text, normalized, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return false;
            }

            var entry = entries[index];
            entries.RemoveAt(index);
            try
            {
                Save(entries);
            }
            catch
            {
                entries.Insert(index, entry);
                throw;
            }

            DiagnosticLog.Info(Category, ChangeLogLine("Removed", entry.Text));
            return true;
        }
    }

    /// <summary>
    /// The text as it is stored: trimmed, every run of whitespace (newlines included) one space,
    /// cut to <see cref="MaxTextLength"/> with an ellipsis. Empty when nothing is left.
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var sb = new StringBuilder(text.Length);
        bool pendingSpace = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(c);
        }

        if (sb.Length > MaxTextLength)
        {
            sb.Length = MaxTextLength - 1;
            sb.Append('…');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Saves one memory. Never throws; the result says what happened and carries the normalised text. The same text stored but disabled
    /// (2026-10-13, the code review) is <see cref="MemoryAddOutcome.Disabled"/>, nothing changed — or, with <paramref name="enableDisabled"/>
    /// (the user's own add), enabled again, <see cref="MemoryAddOutcome.Enabled"/>. <see cref="MaxEntries"/> counts the enabled ones: the
    /// ceiling is the prompt's.
    /// </summary>
    public MemoryAddResult Add(string? text, bool enableDisabled = false)
    {
        string normalized = Normalize(text);
        if (normalized.Length == 0)
        {
            return new(MemoryAddOutcome.Empty, normalized);
        }

        lock (_gate)
        {
            var entries = Entries();
            var existing = entries.FirstOrDefault(e => string.Equals(e.Text, normalized, StringComparison.OrdinalIgnoreCase));
            if (existing is { Disabled: true })
            {
                if (!enableDisabled)
                {
                    return new(MemoryAddOutcome.Disabled, existing.Text);
                }

                if (entries.Count(e => !e.Disabled) >= MaxEntries)
                {
                    return new(MemoryAddOutcome.Full, existing.Text);
                }

                existing.Disabled = false;
                if (!TrySave(entries))
                {
                    existing.Disabled = true;
                    return new(MemoryAddOutcome.Failed, existing.Text);
                }

                DiagnosticLog.Info(Category, ChangeLogLine("Enabled", existing.Text));
                return new(MemoryAddOutcome.Enabled, existing.Text);
            }

            if (existing is not null)
            {
                return new(MemoryAddOutcome.Duplicate, existing.Text);
            }

            if (entries.Count(e => !e.Disabled) >= MaxEntries)
            {
                return new(MemoryAddOutcome.Full, normalized);
            }

            var entry = new MemoryEntry { Text = normalized, SavedAt = _time.GetUtcNow() };
            entries.Add(entry);
            if (!TrySave(entries))
            {
                entries.Remove(entry);
                return new(MemoryAddOutcome.Failed, normalized);
            }

            DiagnosticLog.Info(Category, ChangeLogLine("Remembered", normalized));
            return new(MemoryAddOutcome.Added, normalized);
        }
    }

    /// <summary>
    /// Another store's entries into this one (<c>/profile push|pull memory</c> since 2026-10-13; <c>/memory copy</c>, 2026-09-17 as <c>/memcopy</c>, until then): appended after what is
    /// here, or — <paramref name="overwrite"/> — in place of it. Each text is normalised like an
    /// <see cref="Add"/>; one that is already in the list (this store's, or an earlier entry of the
    /// batch, ignoring case) is a duplicate and skipped; past <see cref="MaxEntries"/> the rest are
    /// dropped. The source's dates are kept. One write at the end: a write that fails puts the old
    /// list back and throws (<see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>),
    /// so the caller never reports a copy that did not happen. An overwrite with nothing to write
    /// still writes: the target ends empty, as asked.
    /// </summary>
    public MemoryImportResult Import(IReadOnlyList<MemoryEntry> entries, bool overwrite)
    {
        ArgumentNullException.ThrowIfNull(entries);
        lock (_gate)
        {
            var previous = Entries();
            var merged = overwrite ? new List<MemoryEntry>() : new List<MemoryEntry>(previous);
            int added = 0, duplicates = 0, dropped = 0;
            foreach (var entry in entries)
            {
                string normalized = Normalize(entry.Text);
                if (normalized.Length == 0)
                {
                    continue;
                }

                if (merged.Any(e => string.Equals(e.Text, normalized, StringComparison.OrdinalIgnoreCase)))
                {
                    duplicates++;
                    continue;
                }

                if (merged.Count >= MaxEntries)
                {
                    dropped++;
                    continue;
                }

                merged.Add(new MemoryEntry { Text = normalized, SavedAt = entry.SavedAt, Disabled = entry.Disabled });   // a disabled one stays so (2026-10-13)
                added++;
            }

            Save(merged);   // throws; _entries still holds the previous list then
            _entries = merged;
            DiagnosticLog.Info(Category, $"Imported {added} memories ({duplicates} already there, {dropped} dropped){(overwrite ? ", replacing the previous list" : "")}.");
            return new MemoryImportResult(added, duplicates, dropped);
        }
    }

    /// <summary>
    /// Forgets everything: deletes the file and returns how many entries it held. A delete that
    /// fails throws (<see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>), and the
    /// in-memory list is kept, so the caller never reports a forget that did not happen.
    /// </summary>
    public int Clear()
    {
        lock (_gate)
        {
            int count = Entries().Count;
            if (File.Exists(_filePath))
            {
                File.Delete(_filePath);   // throws when it cannot; a missing file (or directory) is not an error
            }

            _entries = new List<MemoryEntry>();
            _stamp = null;
            if (count > 0)
            {
                DiagnosticLog.Info(Category, $"Forgot {count} memories.");
            }

            return count;
        }
    }

    /// <summary>
    /// <c>/memory edit</c>'s first step (2026-09-23, the user's ask): the file written when it is not
    /// there yet (an empty list), so the editor opens a file of the right shape rather than none.
    /// True when it already existed. A write that fails throws, as <see cref="Clear"/>'s delete does.
    /// </summary>
    public bool EnsureFile()
    {
        lock (_gate)
        {
            var entries = Entries();
            if (File.Exists(_filePath))
            {
                return true;
            }

            Save(entries);
            return false;
        }
    }

    /// <summary>
    /// The list, loaded on first use and read again whenever the file changed since (2026-09-23,
    /// <c>/memory edit</c>: the user's editor writes it behind the store's back, and the next add
    /// must build on that edit, not write the old list over it). A file deleted outside is an empty
    /// list. A changed file that will not parse keeps the last good list — warned once per version
    /// of the file — rather than the empty one a corrupt first load gets: a typo in a hand edit must
    /// not let the next add wipe every memory. Caller holds the lock.
    /// </summary>
    private List<MemoryEntry> Entries()
    {
        var stamp = Stamp();
        if (_entries is null)
        {
            _entries = Load();
            _stamp = stamp;
            return _entries;
        }

        if (stamp == _stamp)
        {
            return _entries;
        }

        if (stamp is null)
        {
            _entries = new List<MemoryEntry>();
            _stamp = null;
            return _entries;
        }

        try
        {
            _entries = Read();
            _stamp = stamp;
            DiagnosticLog.Info(Category, $"Read {FileName} again: it changed outside the app.");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            if (_warnedStamp != stamp)
            {
                _warnedStamp = stamp;
                DiagnosticLog.Warn(Category, $"Could not read the changed {FileName}; keeping the memories as they were: {ex.Message}", ex);
            }
        }

        return _entries;
    }

    /// <summary>The file's write time and length, or null when there is none.</summary>
    private (DateTime Written, long Length)? Stamp()
    {
        var info = new FileInfo(_filePath);
        return info.Exists ? (info.LastWriteTimeUtc, info.Length) : null;
    }

    /// <summary>
    /// Reads the file. Missing means empty; corrupt is logged once and also treated as empty,
    /// which means the next add overwrites it — a memory file nobody can read is not worth more
    /// than that.
    /// </summary>
    private List<MemoryEntry> Load()
    {
        if (!File.Exists(_filePath))
        {
            return new List<MemoryEntry>();
        }

        try
        {
            return Read();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warn(Category, $"Could not read {FileName}; starting with no memories: {ex.Message}", ex);
            return new List<MemoryEntry>();
        }
    }

    /// <summary>The file's entries, blank ones dropped and each normalised; throws when it cannot be read or parsed.</summary>
    private List<MemoryEntry> Read()
    {
        var file = JsonSerializer.Deserialize(File.ReadAllText(_filePath), MemoryJsonContext.Default.MemoryFile);
        var entries = file?.Entries ?? new List<MemoryEntry>();
        entries.RemoveAll(e => string.IsNullOrWhiteSpace(e.Text));
        foreach (var entry in entries)
        {
            entry.Text = Normalize(entry.Text);
        }

        return entries;
    }

    /// <summary><see cref="Save"/> for the paths that must not throw: the failure is logged and false comes back. Caller holds the lock.</summary>
    private bool TrySave(List<MemoryEntry> entries)
    {
        try
        {
            Save(entries);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Error(Category, $"Could not write {FileName}: {ex.Message}", ex);
            return false;
        }
    }

    /// <summary>Temp file, then a move over the old one: a reader never sees half a file. Throws on failure, temp file removed. Caller holds the lock.</summary>
    private void Save(List<MemoryEntry> entries)
    {
        string tempPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var file = new MemoryFile { Entries = entries };
            File.WriteAllText(tempPath, JsonSerializer.Serialize(file, MemoryJsonContext.Default.MemoryFile));
            File.Move(tempPath, _filePath, overwrite: true);
            _stamp = Stamp();
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* best effort */ }
            throw;
        }
    }
}
