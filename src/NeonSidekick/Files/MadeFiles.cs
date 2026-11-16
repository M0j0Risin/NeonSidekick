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
/// <item>Each entry is a fact about its turn (the user message, by reference). A path added again under the same turn moves to the newest
/// place; under a later turn it adds a second entry, so a rewind of that turn leaves the first one listed. A move of a held path adds its new
/// path; a remove drops nothing (the list's existence check hides it, and an <c>/undo</c> brings the row back).</item>
/// <item>The newest <see cref="Keep"/> entries are kept. Each path keeps one token while it is listed. Lock-guarded: the page reads it from a
/// Kestrel thread.</item>
/// </list>
/// </summary>
public sealed class MadeFiles
{
    /// <summary>How many entries the list keeps; the tab's footer says when older ones went.</summary>
    public const int Keep = 200;

    private static readonly AsyncLocal<Call?> s_current = new();

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
        var call = list is null ? null : new Call(list, turn);
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

    // ---- the hooks (FileJournal's, before it records) ----

    internal static void Writing(string full)
    {
        if (Noting() is { } call)
        {
            call.List.Note(call, full);
        }
    }

    internal static void Moving(string from, string to)
    {
        if (Noting() is not { } call)
        {
            return;
        }

        foreach (string moved in call.List.HeldUnder(from, to))
        {
            call.List.Note(call, moved);
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
            foreach (string full in files.TakeLast(Keep))
            {
                call.List.Note(call, full);
            }
        }
    }

    private static Call? Noting() => s_current.Value is { Open: true } call && !WriteGuard.InAppWrites ? call : null;

    private void Note(Call call, string full)
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

        if (!Roots().Any(root => IsUnder(path, root)))
        {
            return;   // a vault, a share or a database export elsewhere
        }

        lock (call.Pending)
        {
            call.Pending.Add((path, Stat(path)));
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
                .Select(p => WorkingDirectory.PathComparer.Equals(p, source) ? target : target + p[source.Length..])
                .ToList();
        }
    }

    /// <summary>The call's close: each noted path kept where it changed (a new file, a new length or time), in order.</summary>
    private void Settle(Call call)
    {
        List<(string Full, PathStat? Before)> pending;
        lock (call.Pending)
        {
            pending = [.. call.Pending];
            call.Pending.Clear();
        }

        lock (_gate)
        {
            bool changed = call.Removed;
            foreach (var (full, before) in pending)
            {
                if (Stat(full) is { } after && after != before)
                {
                    Add(full, call.Turn);
                    changed = true;
                }
            }

            if (changed)
            {
                _version++;
            }
        }
    }

    // Under _gate.
    private void Add(string full, ChatMessage? turn)
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
            if (ReferenceEquals(entry.Turn, turn))
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
            _version++;
        }
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
        var kept = new HashSet<ChatMessage>(after, ReferenceEqualityComparer.Instance);
        lock (_gate)
        {
            _entries.RemoveAll(e => e.Turn is { } turn && before.Contains(turn) && !kept.Contains(turn));
            _version++;
        }
    }

    /// <summary>
    /// What a save keeps: each entry's path, with its turn's store ordinal only when that message is in <paramref name="history"/>, the one
    /// being saved (a Ralph pass's earlier entries hold other rows' messages, so they go with none and stay with no turn on a restore).
    /// </summary>
    public IReadOnlyList<StoredMadeFile> Saved(IEnumerable<ChatMessage> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        var held = new HashSet<ChatMessage>(history, ReferenceEqualityComparer.Instance);
        lock (_gate)
        {
            return [.. _entries.Select(e => new StoredMadeFile(e.Full, e.Turn is { } turn && held.Contains(turn) ? ConversationHistory.TurnOrdinal(turn) : null))];
        }
    }

    /// <summary>
    /// A restored session's own list: each entry tied back to the restored message with its ordinal; one past the row's <paramref name="turns"/>
    /// belonged to a cut turn (a rewind truncates the turns, not the saved history) and goes; one with none stays with no turn. Fresh tokens.
    /// </summary>
    public void Restore(IEnumerable<StoredMadeFile>? stored, IEnumerable<ChatMessage> restored, int turns)
    {
        ArgumentNullException.ThrowIfNull(restored);
        var byOrdinal = new Dictionary<int, ChatMessage>();
        foreach (var message in restored)
        {
            if (ConversationHistory.IsTurnStart(message) && ConversationHistory.TurnOrdinal(message) is { } ordinal)
            {
                byOrdinal.TryAdd(ordinal, message);
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

                Add(file.Path, file.Ordinal is { } at && byOrdinal.TryGetValue(at, out var turn) ? turn : null);
            }

            _version++;
        }
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

    /// <summary>Whether <paramref name="full"/> lies inside <paramref name="root"/> (compared as the file system does).</summary>
    public static bool IsUnder(string full, string root)
    {
        ArgumentNullException.ThrowIfNull(full);
        ArgumentNullException.ThrowIfNull(root);
        string trimmed = Path.TrimEndingDirectorySeparator(root);
        if (trimmed.Length == 0 || full.Length <= trimmed.Length + 1)
        {
            return false;
        }

        char separator = full[trimmed.Length];
        return (separator == Path.DirectorySeparatorChar || separator == Path.AltDirectorySeparatorChar)
            && WorkingDirectory.PathComparer.Equals(full[..trimmed.Length], trimmed);
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

    private sealed record Entry(string Full, ChatMessage? Turn, string Token);

    private sealed class Call(MadeFiles list, ChatMessage? turn)
    {
        public MadeFiles List { get; } = list;

        public ChatMessage? Turn { get; } = turn;

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
