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

    /// <summary>
    /// The one working folder this memory is for (2026-11-21, feature idea #6: a project's facts out of another repo's prompt): the full path,
    /// no trailing separator (<see cref="Files.FolderMatch.Normal"/>); null for everywhere. Written only when set, so an older file reads as
    /// everywhere and an older build ignores it. A folder from another PC never matches, so its memory stays dormant there.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Folder { get; set; }

    /// <summary>A copy, every field.</summary>
    public MemoryEntry Copy() => new() { Text = Text, SavedAt = SavedAt, Disabled = Disabled, Folder = Folder };
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

    /// <summary>The same text was stored disabled and is enabled again (2026-10-13): the user's own add (<c>/memory remember</c>, the pane's + remember).</summary>
    Enabled,

    /// <summary>A folder was given that isn't a full path on this system (2026-11-22, the review's #107); nothing changed, never saved as everywhere.</summary>
    FolderRefused,
}

/// <summary>What <see cref="MemoryStore.SetFolder"/> did (2026-11-22, the review's #26).</summary>
public enum MemoryScopeOutcome
{
    Moved,

    /// <summary>It already was so; nothing written.</summary>
    Unchanged,

    /// <summary>No memory holds the text; nothing written.</summary>
    Missing,

    /// <summary>The move would offer a folder more than <see cref="MemoryStore.MaxEntries"/>; it stays where it was.</summary>
    Full,

    /// <summary>The folder isn't a full path on this system (#107); nothing written.</summary>
    Refused,
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

/// <summary>
/// The outcome of an add and the text as it was (or would have been) stored. Since 2026-11-22 (the review): <paramref name="FileFull"/> a full
/// that is the file's ceiling (#26); <paramref name="WidenedFrom"/> the folder a duplicate was kept for until this save widened it (#34);
/// <paramref name="CoversHere"/> a save for this folder of one kept everywhere, left so (#20).
/// </summary>
public readonly record struct MemoryAddResult(MemoryAddOutcome Outcome, string Text, string? Folder = null, bool FileFull = false, string? WidenedFrom = null, bool CoversHere = false);

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
/// model "remembered" only once the file is on disk, and the shell's <c>/memory remember</c> shares the
/// same instance.</para>
/// </summary>
public sealed class MemoryStore
{
    public const string FileName = "memory.json";

    /// <summary>
    /// The ceiling on the memories one folder is offered (the everywhere ones and its own, enabled); the prompt grows with every one. Since
    /// 2026-11-22 (the review's #26) a folder's own count against its view alone, so 200 in one repo never stop an add in another.
    /// </summary>
    public const int MaxEntries = 200;

    /// <summary>The ceiling on every entry in the file, disabled and other folders' too (2026-11-22, #26), so <c>memory.json</c> stays bounded.</summary>
    public const int MaxStoredEntries = 1000;

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

    /// <summary>
    /// The texts the model is offered, oldest first, as a copy: every stored one but the disabled (2026-10-13), and since 2026-11-21 (feature
    /// idea #6) only the everywhere ones and those for <paramref name="folder"/> (<see cref="Files.FolderMatch.Same"/>); a folder-only one ends
    /// with <see cref="MemoryPrompt.FolderOnlyMark"/>, so the model doesn't take a project's rule for one about the user. A null folder (a bot,
    /// a tool built without one) offers the everywhere ones alone.
    /// </summary>
    public IReadOnlyList<string> Snapshot(string? folder = null)
    {
        lock (_gate)
        {
            return Entries().Where(e => !e.Disabled && (e.Folder is null || Files.FolderMatch.Same(e.Folder, folder)))
                .Select(e => e.Folder is null ? e.Text : e.Text + MemoryPrompt.FolderOnlyMark).ToArray();
        }
    }

    /// <summary>
    /// One memory's folder set (2026-11-21, feature idea #6): <paramref name="folder"/> made <see cref="Files.FolderMatch.Normal"/>, or null for
    /// everywhere. A rewrite that fails puts the folder back and throws, as <see cref="Remove"/> does. Since 2026-11-22 (the review): a folder that
    /// won't normalize is refused, never everywhere (#107); the same folder is compared as folders are (#92); a move that would offer some folder
    /// more than <see cref="MaxEntries"/> is <see cref="MemoryScopeOutcome.Full"/> and moves nothing (#26).
    /// </summary>
    public MemoryScopeOutcome SetFolder(string? text, string? folder)
    {
        string normalized = Normalize(text);
        string? where = Files.FolderMatch.Normal(folder);
        if (where is null && folder is not null)
        {
            return MemoryScopeOutcome.Refused;
        }

        lock (_gate)
        {
            var entries = Entries();
            var entry = entries.FirstOrDefault(e => string.Equals(e.Text, normalized, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                return MemoryScopeOutcome.Missing;
            }

            if (where is null ? entry.Folder is null : Files.FolderMatch.Same(entry.Folder, where))
            {
                return MemoryScopeOutcome.Unchanged;
            }

            // Everywhere to a folder is counted there already; any other move of an offered memory is checked where it lands.
            if (!entry.Disabled && (where is null || entry.Folder is not null) && !Fits(entries.Where(e => !ReferenceEquals(e, entry)), where))
            {
                return MemoryScopeOutcome.Full;
            }

            string? before = entry.Folder;
            entry.Folder = where;
            try
            {
                Save(entries);
            }
            catch
            {
                entry.Folder = before;
                throw;
            }

            DiagnosticLog.Info(Category, ChangeLogLine(where is null ? "Widened" : "Scoped", entry.Text));
            return MemoryScopeOutcome.Moved;
        }
    }

    /// <summary>
    /// Whether one more memory for <paramref name="folder"/> (null: everywhere) fits under <see cref="MaxEntries"/> (2026-11-22, the review's #26):
    /// a folder's add against the everywhere ones and its own; an everywhere add against the everywhere ones and the largest folder's own, as it
    /// joins every folder's view. Disabled ones are never offered, so never counted. Caller holds the lock.
    /// </summary>
    private static bool Fits(IEnumerable<MemoryEntry> entries, string? folder)
    {
        var live = entries.Where(e => !e.Disabled).ToList();
        int everywhere = live.Count(e => e.Folder is null);
        int own = folder is not null
            ? live.Count(e => e.Folder is not null && Files.FolderMatch.Same(e.Folder, folder))
            : live.Where(e => e.Folder is not null).GroupBy(e => Files.FolderMatch.Normal(e.Folder) ?? e.Folder!, Files.WorkingDirectory.PathComparer).Select(g => g.Count()).DefaultIfEmpty(0).Max();
        return everywhere + own < MaxEntries;
    }

    /// <summary>
    /// A stored folder as compared (2026-11-22, the review's #92): normalized; one that won't normalize here (saved on the other system, #78) kept
    /// as written, so it matches no folder and stays "another folder", never everywhere; a blank one is everywhere.
    /// </summary>
    private static string? StoredFolder(string? folder) => string.IsNullOrWhiteSpace(folder) ? null : Files.FolderMatch.Normal(folder) ?? folder;

    /// <summary>
    /// Every add's way in (2026-11-22, the review's #34, #88, #20): <see cref="Add"/>, and then a save for everywhere (<paramref name="scoped"/>
    /// false) of a fact kept for a folder other than <paramref name="currentFolder"/> widens it (<see cref="SetFolder"/> null), the result saying
    /// so, or full when that would pass the ceiling; a save for this folder of one kept everywhere stays everywhere, said so. Never throws: a
    /// widen that couldn't be written is <see cref="MemoryAddOutcome.Failed"/>. Named apart from the file's private save.
    /// </summary>
    public MemoryAddResult Remember(string? text, string? currentFolder, bool scoped, bool enableDisabled = false)
    {
        var result = Add(text, enableDisabled, scoped ? currentFolder ?? "" : null);
        if (result.Outcome != MemoryAddOutcome.Duplicate)
        {
            return result;
        }

        if (scoped && result.Folder is null)
        {
            return result with { CoversHere = true };
        }

        if (scoped || result.Folder is not { } kept || Files.FolderMatch.Same(kept, currentFolder))
        {
            return result;
        }

        try
        {
            return SetFolder(result.Text, null) switch
            {
                MemoryScopeOutcome.Moved or MemoryScopeOutcome.Unchanged => result with { Folder = null, WidenedFrom = kept },
                MemoryScopeOutcome.Full => result with { Outcome = MemoryAddOutcome.Full },
                _ => result,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Error(Category, $"Could not write {FileName}: {ex.Message}", ex);
            return result with { Outcome = MemoryAddOutcome.Failed };
        }
    }

    /// <summary>The stored entries with their dates, oldest first, as copies, the disabled too; the <c>/memory</c> menu's view.</summary>
    public IReadOnlyList<MemoryEntry> EntriesSnapshot()
    {
        lock (_gate)
        {
            return Entries().Select(e => e.Copy()).ToArray();
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
    /// ceiling is the prompt's: since 2026-11-22 (the review's #26) per folder (<see cref="Fits"/>), with <see cref="MaxStoredEntries"/> over the file.
    /// </summary>
    /// <param name="folder">The one working folder the memory is for (2026-11-21, feature idea #6, <c>/memory remember --here</c> and
    /// <c>save_memory</c>'s <c>here</c>); null for everywhere. A duplicate answers with the saved copy's folder, so a caller can say where it applies.
    /// One given that isn't a full path here is <see cref="MemoryAddOutcome.FolderRefused"/> (#107). A disabled one the user's add enables again
    /// takes it (#20): it wasn't offered anywhere.</param>
    public MemoryAddResult Add(string? text, bool enableDisabled = false, string? folder = null)
    {
        string normalized = Normalize(text);
        if (normalized.Length == 0)
        {
            return new(MemoryAddOutcome.Empty, normalized);
        }

        string? where = Files.FolderMatch.Normal(folder);
        if (where is null && folder is not null)
        {
            return new(MemoryAddOutcome.FolderRefused, normalized);
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

                string? lands = where ?? existing.Folder;
                if (!Fits(entries, lands))
                {
                    return new(MemoryAddOutcome.Full, existing.Text);
                }

                string? was = existing.Folder;
                existing.Disabled = false;
                existing.Folder = lands;
                if (!TrySave(entries))
                {
                    existing.Disabled = true;
                    existing.Folder = was;
                    return new(MemoryAddOutcome.Failed, existing.Text);
                }

                DiagnosticLog.Info(Category, ChangeLogLine("Enabled", existing.Text));
                return new(MemoryAddOutcome.Enabled, existing.Text, existing.Folder);
            }

            if (existing is not null)
            {
                return new(MemoryAddOutcome.Duplicate, existing.Text, existing.Folder);
            }

            if (entries.Count >= MaxStoredEntries)
            {
                return new(MemoryAddOutcome.Full, normalized, FileFull: true);
            }

            if (!Fits(entries, where))
            {
                return new(MemoryAddOutcome.Full, normalized);
            }

            var entry = new MemoryEntry { Text = normalized, SavedAt = _time.GetUtcNow(), Folder = where };
            entries.Add(entry);
            if (!TrySave(entries))
            {
                entries.Remove(entry);
                return new(MemoryAddOutcome.Failed, normalized);
            }

            DiagnosticLog.Info(Category, ChangeLogLine("Remembered", normalized));
            return new(MemoryAddOutcome.Added, normalized, entry.Folder);
        }
    }

    /// <summary>
    /// Another store's entries into this one (<c>/profile push|pull memory</c> since 2026-10-13; <c>/memory copy</c>, 2026-09-17 as <c>/memcopy</c>, until then): appended after what is
    /// here, or — <paramref name="overwrite"/> — in place of it. Each text is normalised like an
    /// <see cref="Add"/>; one that is already in the list (this store's, or an earlier entry of the
    /// batch, ignoring case) is a duplicate and skipped; one past <see cref="Add"/>'s ceilings (per folder, and the file's
    /// <see cref="MaxStoredEntries"/>, disabled ones counted there too, 2026-11-22, the review's #26) is dropped. The source's dates are kept. One write at the end: a write that fails puts the old
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

                string? folder = StoredFolder(entry.Folder);   // #92
                if (merged.Count >= MaxStoredEntries || (!entry.Disabled && !Fits(merged, folder)))
                {
                    dropped++;
                    continue;
                }

                merged.Add(new MemoryEntry { Text = normalized, SavedAt = entry.SavedAt, Disabled = entry.Disabled, Folder = folder });   // a disabled one stays so (2026-10-13), a folder's stays its (2026-11-21)
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
            entry.Folder = StoredFolder(entry.Folder);   // #92: a hand edit's trailing separator or case still this folder
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
