using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using NeonSidekick.Llm;

namespace NeonSidekick.Files;

/// <summary>A made file kept with a saved session (2026-11-16, #41): its full path, and its turn's store ordinal when it has one.</summary>
public sealed record StoredMadeFile(string Path, int? Ordinal = null);

/// <summary>A listed made file: its full path and the token its url carries.</summary>
public readonly record struct MadeFile(string Full, string Token);

/// <summary>
/// The files the model made in this conversation (2026-11-16, plan lucid-roaming-lantern #41), for the Web UI's 🗂️ Files tab. Fed from the
/// checkpoint journal's own hooks (<see cref="FileJournal"/>'s <c>Writing</c>, <c>Moving</c>, <c>Removing</c>, <c>Placing</c>, <c>Replacing</c>),
/// which tell <see cref="Current"/> first, so a turn records with checkpoints off too; entered per tool call beside the journal
/// (<c>Assistant.InvokeWatchedAsync</c>) for the screen's main Assistant only.
/// <list type="bullet">
/// <item>The hooks run before the disk is touched, so each notes the path's state then (absent, or a length and a write time) and the call's
/// close keeps an add only where it changed: a failed or refused write lists nothing. A task the call started, writing after the close, is
/// never counted. App writes are skipped, as the journal skips them; so is anything outside today's working and images folders.</item>
/// <item>Each entry is a fact about its turn, held by the turn's key (<see cref="ConversationHistory.EnsureTurnKey"/>, 2026-11-16, the review's
/// #103 and #128: never the message itself, which kept a compacted turn's pictures alive and missed the rebuilt one a mid-turn compaction makes).
/// A path added again under the same turn moves to the newest place; under a later turn it adds a second entry, so a rewind of that turn leaves
/// the first one listed. A move adds its new path (a held path's, or the moved file's own); a remove drops nothing (the list's existence check
/// hides it, and an <c>/undo</c> brings the row back).</item>
/// <item>A camera's or the screen's picture is never listed (<see cref="Unlisted"/>, the review's #127): the Files tab can reach the internet.</item>
/// <item>The newest <see cref="Keep"/> entries are kept. Each path keeps one token while it is listed. Lock-guarded: the page reads it from a
/// Kestrel thread.</item>
/// </list>
/// </summary>
public sealed class MadeFiles
{
    /// <summary>How many entries the list keeps; the tab's footer says when older ones went.</summary>
    public const int Keep = 200;

    private static readonly AsyncLocal<Call?> s_current = new();
    private static readonly AsyncLocal<StrongBox<int>?> s_unlisted = new();

    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];
    private long _version;
    private bool _trimmed;

    /// <param name="roots">Today's working and images folders' full paths, read at each hook.</param>
    public MadeFiles(Func<IReadOnlyList<string>> roots)
    {
        Roots = roots ?? throw new ArgumentNullException(nameof(roots));
    }

    private Func<IReadOnlyList<string>> Roots { get; }

    /// <summary>Moves with every change to the list, and with anything that may show or hide a row (a remove, a file restore).</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>Whether older entries went past <see cref="Keep"/>.</summary>
    public bool Trimmed
    {
        get
        {
            lock (_gate)
            {
                return _trimmed;
            }
        }
    }

    /// <summary>The list entered on this flow (one tool call), or null.</summary>
    internal static MadeFiles? Current => s_current.Value is { Open: true } call ? call.List : null;

    /// <summary>
    /// <paramref name="list"/> for the call inside, its turn <paramref name="turn"/> (the user message); the adds are settled when the scope
    /// closes, cancellation included. Null records nothing.
    /// </summary>
    public static IDisposable Enter(MadeFiles? list, ChatMessage? turn)
    {
        var previous = s_current.Value;
        var call = list is null ? null : new Call(list, turn is null ? null : ConversationHistory.EnsureTurnKey(turn));
        s_current.Value = call;
        return new Closer(() =>
        {
            s_current.Value = previous;
            if (call is not null)
            {
                call.Open = false;
                call.List.Settle(call);
            }
        });
    }

    /// <summary>
    /// The writes inside never listed (2026-11-16, the review's #127): a camera's, the screen's or a home camera's picture is the user's private
    /// view, and the Files tab can be reached over Funnel. Not <see cref="WriteGuard.AsApp{T}"/>, which would lift the write's guard too.
    /// </summary>
    public static IDisposable Unlisted()
    {
        var previous = s_unlisted.Value;
        s_unlisted.Value = new StrongBox<int>(1);
        return new Closer(() => s_unlisted.Value = previous);
    }

    /// <summary><paramref name="write"/> run inside <see cref="Unlisted()"/>: a capture's own synchronous write.</summary>
    public static T Unlisted<T>(Func<T> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        using (Unlisted())
        {
            return write();
        }
    }

    // ---- the hooks (FileJournal's, before it records) ----

    internal static void Writing(string full)
    {
        if (Noting() is { } call)
        {
            Note(call, full, call.List.Roots());
        }
    }

    internal static void Moving(string from, string to)
    {
        if (Noting() is not { } call)
        {
            return;
        }

        // The roots read once for the lot (the review's #14). A move of a file the list never held adds the file at its new place (#106): its
        // stat is none now and the file's after the move. A folder isn't a file, so a moved folder adds nothing.
        var roots = call.List.Roots();
        var held = call.List.HeldUnder(from, to);
        foreach (string moved in held.Count > 0 ? held : [to])
        {
            Note(call, moved, roots);
        }
    }

    internal static void Removing()
    {
        if (Noting() is { } call)
        {
            call.Removed = true;
        }
    }

    internal static void Placing(IEnumerable<string> files)
    {
        if (Noting() is { } call)
        {
            // An unzip or a folder copy can name thousands: only the newest Keep could be listed, so only they are stat'ed.
            var roots = call.List.Roots();
            foreach (string full in files.TakeLast(Keep))
            {
                Note(call, full, roots);
            }
        }
    }

    private static Call? Noting() => s_current.Value is { Open: true } call && !WriteGuard.InAppWrites && s_unlisted.Value is null ? call : null;

    private static void Note(Call call, string full, IReadOnlyList<string> roots)
    {
        string path;
        try
        {
            path = Path.GetFullPath(full);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }

        if (!roots.Any(root => IsUnder(path, root)))
        {
            return;   // a vault, a share or a database export elsewhere
        }

        var before = Stat(path);
        lock (call.Pending)
        {
            call.Pending.Add((path, before));
        }
    }

    /// <summary>The held paths at or under <paramref name="from"/>, as they will be under <paramref name="to"/>.</summary>
    private List<string> HeldUnder(string from, string to)
    {
        string source, target;
        try
        {
            source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(from));
            target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(to));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return [];
        }

        lock (_gate)
        {
            return _entries.Select(e => e.Full).Distinct(WorkingDirectory.PathComparer)
                .Where(p => WorkingDirectory.PathComparer.Equals(p, source) || IsUnder(p, source))
                .Select(p => WorkingDirectory.PathComparer.Equals(p, source) ? target : Path.Join(target, WorkingDirectory.Under(source, p)))
                .ToList();
        }
    }

    /// <summary>
    /// The call's close: each noted path kept where it changed (a new file, a new length or time), in order. The disk is asked before the lock
    /// (the review's #107: a page's read never waits on it).
    /// </summary>
    private void Settle(Call call)
    {
        List<(string Full, PathStat? Before)> pending;
        lock (call.Pending)
        {
            pending = [.. call.Pending];
            call.Pending.Clear();
        }

        var changedPaths = pending.Where(p => Stat(p.Full) is { } after && after != p.Before).Select(p => p.Full).ToList();
        if (changedPaths.Count > 0)
        {
            lock (_gate)
            {
                foreach (string full in changedPaths)
                {
                    Add(full, call.TurnKey);
                }
            }
        }

        if (changedPaths.Count > 0 || call.Removed)
        {
            Interlocked.Increment(ref _version);
        }
    }

    // Under _gate.
    private void Add(string full, long? turn)
    {
        string? token = null;
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            var entry = _entries[i];
            if (!WorkingDirectory.PathComparer.Equals(entry.Full, full))
            {
                continue;
            }

            token ??= entry.Token;
            if (entry.TurnKey == turn)
            {
                _entries.RemoveAt(i);   // the same turn's file written again: one entry, at the newest place
            }
        }

        _entries.Add(new Entry(full, turn, token ?? Viewer.VideoFile.NewToken()));
        if (_entries.Count > Keep)
        {
            _entries.RemoveRange(0, _entries.Count - Keep);
            _trimmed = true;
        }
    }

    // ---- the screen's ----

    /// <summary>The conversation moved on (<c>/clear</c>, <c>/new</c>, a reset, a profile switch): nothing listed.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _trimmed = false;
        }

        Interlocked.Increment(ref _version);
    }

    /// <summary>Something outside the list may show or hide rows (an <c>/undo</c>, a rewind's files, a working folder change).</summary>
    public void Touch() => Interlocked.Increment(ref _version);

    /// <summary>
    /// A rewind: the entries of the turns it cut go, those whose message was in <paramref name="before"/> and is gone from
    /// <paramref name="after"/>. A turn compacted away was never in a cut, so its entries stay.
    /// </summary>
    public void Rewound(IReadOnlySet<ChatMessage> before, IEnumerable<ChatMessage> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var cut = KeysOf(before);
        cut.ExceptWith(KeysOf(after));
        lock (_gate)
        {
            _entries.RemoveAll(e => e.TurnKey is { } key && cut.Contains(key));
        }

        Interlocked.Increment(ref _version);
    }

    private static HashSet<long> KeysOf(IEnumerable<ChatMessage> messages) => [.. messages.Select(ConversationHistory.TurnKey).OfType<long>()];

    /// <summary>
    /// What a save keeps: each entry's path, with its turn's store ordinal only when that message is in <paramref name="history"/>, the one
    /// being saved (a Ralph pass's earlier entries hold other rows' messages, so they go with none and stay with no turn on a restore).
    /// </summary>
    public IReadOnlyList<StoredMadeFile> Saved(IEnumerable<ChatMessage> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        var ordinals = new Dictionary<long, int?>();
        foreach (var message in history)
        {
            if (ConversationHistory.TurnKey(message) is { } key)
            {
                ordinals.TryAdd(key, ConversationHistory.TurnOrdinal(message));
            }
        }

        lock (_gate)
        {
            return [.. _entries.Select(e => new StoredMadeFile(e.Full, e.TurnKey is { } key && ordinals.TryGetValue(key, out int? ordinal) ? ordinal : null))];
        }
    }

    /// <summary>
    /// A restored session's own list: each entry tied back to the restored message with its ordinal; one past the row's <paramref name="turns"/>
    /// belonged to a cut turn (a rewind truncates the turns, not the saved history) and goes; one with none stays with no turn. Fresh tokens.
    /// </summary>
    public void Restore(IEnumerable<StoredMadeFile>? stored, IEnumerable<ChatMessage> restored, int turns)
    {
        ArgumentNullException.ThrowIfNull(restored);
        var byOrdinal = new Dictionary<int, long>();
        foreach (var message in restored)
        {
            if (ConversationHistory.IsTurnStart(message) && ConversationHistory.TurnOrdinal(message) is { } ordinal)
            {
                byOrdinal.TryAdd(ordinal, ConversationHistory.EnsureTurnKey(message));
            }
        }

        lock (_gate)
        {
            _entries.Clear();
            _trimmed = false;
            foreach (var file in stored ?? [])
            {
                if (file.Ordinal is { } ordinal && ordinal > turns)
                {
                    continue;
                }

                Add(file.Path, file.Ordinal is { } at && byOrdinal.TryGetValue(at, out long key) ? key : null);
            }
        }

        Interlocked.Increment(ref _version);
    }

    /// <summary>The listed files, newest first, each path once at its newest entry's place, where <paramref name="shown"/> says so.</summary>
    public IReadOnlyList<MadeFile> Listed(Func<string, bool> shown)
    {
        ArgumentNullException.ThrowIfNull(shown);
        List<MadeFile> entries;
        lock (_gate)
        {
            entries = [.. _entries.Select(e => new MadeFile(e.Full, e.Token))];
        }

        var seen = new HashSet<string>(WorkingDirectory.PathComparer);
        var listed = new List<MadeFile>();
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            if (seen.Add(entries[i].Full) && shown(entries[i].Full))
            {
                listed.Add(entries[i]);
            }
        }

        return listed;
    }

    /// <summary>The full path the listed <paramref name="token"/> stands for, or null.</summary>
    public string? PathOf(string token)
    {
        lock (_gate)
        {
            return _entries.LastOrDefault(e => string.Equals(e.Token, token, StringComparison.Ordinal))?.Full;
        }
    }

    /// <summary>
    /// Whether <paramref name="full"/> lies inside <paramref name="root"/>, never the root itself, by the root's own case rule (2026-11-16, the
    /// review's #1 and #17: <see cref="WorkingDirectory.IsInside"/>'s, so a drive's root holds its files too).
    /// </summary>
    public static bool IsUnder(string full, string root)
    {
        ArgumentNullException.ThrowIfNull(full);
        ArgumentNullException.ThrowIfNull(root);
        return root.Length > 0 && WorkingDirectory.IsInside(root, full) && !string.Equals(full, root, CaseRule.Comparison(root))
            && !string.Equals(full, Path.TrimEndingDirectorySeparator(root), CaseRule.Comparison(root));
    }

    private static PathStat? Stat(string full)
    {
        try
        {
            var info = new FileInfo(full);
            return info.Exists ? new PathStat(info.Length, info.LastWriteTimeUtc) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private readonly record struct PathStat(long Length, DateTime Written);

    private sealed record Entry(string Full, long? TurnKey, string Token);

    private sealed class Call(MadeFiles list, long? turnKey)
    {
        public MadeFiles List { get; } = list;

        public long? TurnKey { get; } = turnKey;

        public List<(string Full, PathStat? Before)> Pending { get; } = [];

        public volatile bool Open = true;

        public volatile bool Removed;
    }

    private sealed class Closer(Action close) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                close();
            }
        }
    }
}
