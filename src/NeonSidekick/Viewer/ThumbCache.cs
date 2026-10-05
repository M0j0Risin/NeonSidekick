namespace NeonSidekick.Viewer;

/// <summary>
/// The thumbnail browser's decoded tiles (2026-10-04): each picture's bitmap at the bucket it was read at
/// (<see cref="ThumbsState.Bucket"/>), or the note that it could not be read, the least recently used let go first once the
/// pixels pass <see cref="Budget"/> bytes — but never a tile the caller says is in view or about to be. A picture written again
/// is <see cref="Remove"/>d by the window and read afresh. Pure; one thread (the window's).
/// </summary>
public sealed class ThumbCache(long budget)
{
    /// <summary>The default budget: 192 MB of pixels, about 500 tiles at 256 × 256 and four bytes a pixel.</summary>
    public const long DefaultBudget = 192L * 1024 * 1024;

    private sealed class Item(LinkedListNode<string> node)
    {
        public LinkedListNode<string> Node { get; } = node;
        public ViewerBitmap? Bitmap { get; set; }
        public int Bucket { get; set; }
        public bool Failed { get; set; }
        public long Bytes => Bitmap?.Bgrx.LongLength ?? 0;
    }

    private readonly Dictionary<string, Item> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _order = [];   // the most recently used first

    /// <summary>The pixels kept before the least recently used tiles go.</summary>
    public long Budget { get; } = budget;

    /// <summary>The pixels held now, in bytes.</summary>
    public long Bytes { get; private set; }

    /// <summary>How many pictures are held (read or failed).</summary>
    public int Count => _items.Count;

    /// <summary>
    /// Whether <paramref name="path"/> needs no read for a tile of <paramref name="bucket"/>: held at that bucket or a bigger one,
    /// held whole (the picture is smaller than the bucket it was read at, so no bigger read can add anything), or known unreadable.
    /// </summary>
    public bool Satisfies(string path, int bucket) =>
        _items.TryGetValue(path, out var item)
        && (item.Failed || item.Bucket >= bucket || (item.Bitmap is { } bitmap && Math.Max(bitmap.Width, bitmap.Height) < item.Bucket));

    /// <summary>The tile held for <paramref name="path"/>, marked most recently used: its bitmap (null when it failed or is not held) and whether it failed.</summary>
    public (ViewerBitmap? Bitmap, bool Failed) Get(string path)
    {
        if (!_items.TryGetValue(path, out var item))
        {
            return (null, false);
        }

        Touch(item);
        return (item.Bitmap, item.Failed);
    }

    /// <summary><paramref name="path"/>'s tile read at <paramref name="bucket"/>: its bitmap, or null when it could not be read. Replaces what was held.</summary>
    public void Put(string path, int bucket, ViewerBitmap? bitmap)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!_items.TryGetValue(path, out var item))
        {
            item = new Item(_order.AddFirst(path));
            _items[path] = item;
        }
        else
        {
            Touch(item);
        }

        Bytes -= item.Bytes;
        item.Bitmap = bitmap;
        item.Bucket = bucket;
        item.Failed = bitmap is null;
        Bytes += item.Bytes;
    }

    /// <summary><paramref name="path"/>'s tile let go (the picture written again, renamed or gone). True when one was held.</summary>
    public bool Remove(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!_items.Remove(path, out var item))
        {
            return false;
        }

        _order.Remove(item.Node);
        Bytes -= item.Bytes;
        return true;
    }

    /// <summary>A held tile moved to a new name with its picture (a rename in place). True when one was held.</summary>
    public bool Rename(string oldPath, string newPath)
    {
        ArgumentNullException.ThrowIfNull(oldPath);
        ArgumentNullException.ThrowIfNull(newPath);
        if (!_items.TryGetValue(oldPath, out var item))
        {
            return false;
        }

        Remove(oldPath);
        Remove(newPath);
        Put(newPath, item.Bucket, item.Bitmap);
        return true;
    }

    /// <summary>The least recently used tiles let go until the pixels are within <see cref="Budget"/>, none of <paramref name="keep"/> among them.</summary>
    public void Trim(IReadOnlySet<string> keep)
    {
        ArgumentNullException.ThrowIfNull(keep);
        var node = _order.Last;
        while (Bytes > Budget && node is not null)
        {
            var previous = node.Previous;
            if (!keep.Contains(node.Value))
            {
                Remove(node.Value);
            }

            node = previous;
        }
    }

    /// <summary>Everything let go (another folder).</summary>
    public void Clear()
    {
        _items.Clear();
        _order.Clear();
        Bytes = 0;
    }

    private void Touch(Item item)
    {
        _order.Remove(item.Node);
        _order.AddFirst(item.Node);
    }
}
