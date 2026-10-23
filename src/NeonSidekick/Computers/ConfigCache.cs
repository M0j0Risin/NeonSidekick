using System.Collections.Concurrent;

namespace NeonSidekick.Computers;

/// <summary>
/// One cached read per config file (2026-10-23, the admin review's mechanism 1: M8, R12, R13, R15, D4, V12). The admin families' files
/// (<c>services.json</c>, <c>tasks.json</c>, <c>eventlog.json</c>, <c>ad.json</c>) were read, parsed and run through the encrypt-in-place pass
/// on every turn, every pane redraw, every completion keystroke and every tool description read; now each is read again only once its stamp
/// (exists, length, last write) changes, and the app's own writes (<see cref="Sql.ConnectionsFileEdit"/>: the wizard's splices, a set
/// password, an encryption) drop it at once. A failed encryption is therefore tried, and logged, once per stamp (V12). Safe across threads (a
/// turn, a Kestrel request, completion's pool work): one lock per file, held through its load. Known limit: a same-size edit within one
/// timestamp tick (FAT's 2 s) is not seen until the next change. Process-wide; a test takes <see cref="Scope"/>.
/// </summary>
public static class ConfigCache
{
    private static ConcurrentDictionary<string, Slot> s_slots = NewSlots();

    /// <summary>
    /// The value <paramref name="load"/> made from <paramref name="path"/> under <paramref name="family"/>, read again only when the file's
    /// stamp moved. <paramref name="load"/> runs under the file's lock.
    /// </summary>
    public static T Get<T>(string family, string path, Func<T> load)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(family);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(load);
        var slots = s_slots;
        var slot = slots.GetOrAdd(Key(path) + "|" + family, _ => new Slot());
        lock (slot.Gate)
        {
            var stamp = Stamp.Of(path);
            if (slot.Filled && slot.Stamp == stamp && slot.Value is T cached)
            {
                return cached;
            }

            var value = load();
            slot.Stamp = stamp;
            slot.Value = value;
            slot.Filled = true;
            return value;
        }
    }

    /// <summary>Drops every cached read of <paramref name="path"/> (the app wrote it).</summary>
    public static void Invalidate(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string prefix = Key(path) + "|";
        foreach (string key in s_slots.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                s_slots.TryRemove(key, out _);
            }
        }
    }

    /// <summary>A test's scope: the cache empty inside, the previous one put back after (the <c>ThemeScope</c> shape).</summary>
    public static IDisposable Scope()
    {
        var previous = s_slots;
        s_slots = NewSlots();
        return new Restore(previous);
    }

    private static ConcurrentDictionary<string, Slot> NewSlots() => new(StringComparer.Ordinal);

    private static string Key(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            full = path;
        }

        return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? full.ToUpperInvariant() : full;
    }

    private sealed class Slot
    {
        public readonly object Gate = new();
        public Stamp Stamp;
        public object? Value;
        public bool Filled;
    }

    private readonly record struct Stamp(bool Exists, long Length, long WriteTicks)
    {
        public static Stamp Of(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? new Stamp(true, info.Length, info.LastWriteTimeUtc.Ticks) : default;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return default;
            }
        }
    }

    private sealed class Restore(ConcurrentDictionary<string, Slot> previous) : IDisposable
    {
        public void Dispose() => s_slots = previous;
    }
}
