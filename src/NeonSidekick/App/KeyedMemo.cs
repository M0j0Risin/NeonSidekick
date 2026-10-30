namespace NeonSidekick.App;

/// <summary>
/// One value worked out again only when its key changes (2026-10-30, review F61: a query tool's schema, its description and its save
/// sentence each asked for the save root, and each ask copied the settings twice). The key and its value are kept as one object, so a
/// reader on another thread never pairs a new key with an old value; two threads that miss at once both work it out, and either answer
/// stands.
/// </summary>
internal sealed class KeyedMemo<TKey, TValue> where TKey : IEquatable<TKey>
{
    private sealed record Entry(TKey Key, TValue Value);

    private Entry? _entry;

    /// <summary>The value for <paramref name="key"/>: the one kept when the key is the same, else <paramref name="compute"/>'s, kept.</summary>
    public TValue Get(TKey key, Func<TValue> compute)
    {
        ArgumentNullException.ThrowIfNull(compute);
        if (Volatile.Read(ref _entry) is { } kept && kept.Key.Equals(key))
        {
            return kept.Value;
        }

        var value = compute();
        Volatile.Write(ref _entry, new Entry(key, value));
        return value;
    }
}
