namespace NeonSidekick.Viewer;

/// <summary>
/// Where the picture viewer's reads hand their answers to the window's thread (2026-10-09, the user's report: a double-click on the
/// splash opened the viewer black, the picture coming only after a move off it and back). Opening on a picture that is not the
/// folder's newest reads twice — the newest as the folder is shown, then the picked one — and the first read, cancelled, can still
/// be decoding (<see cref="ViewerImage.Decode(byte[], string)"/> cannot stop midway) when the second is done; a single field took
/// whichever wrote last, so the stale one overwrote the picked picture and the window, finding a version not its own, drew nothing.
/// The slot keeps the newest version offered: an older read arriving later is refused. Any thread; the window takes on its own.
/// </summary>
public sealed class ViewerLoadSlot
{
    private readonly Lock _gate = new();
    private (int Version, string Path, ViewerBitmap? Bitmap)? _waiting;

    /// <summary>
    /// A read's answer (<paramref name="bitmap"/> null for an unreadable file, which still counts: the window says so): kept when the
    /// slot is empty or holds an older version. True when kept — the window is to be told; false for a stale read.
    /// </summary>
    public bool Offer(int version, string path, ViewerBitmap? bitmap)
    {
        ArgumentNullException.ThrowIfNull(path);
        lock (_gate)
        {
            if (_waiting is { } waiting && waiting.Version > version)
            {
                return false;
            }

            _waiting = (version, path, bitmap);
            return true;
        }
    }

    /// <summary>The waiting answer when it is <paramref name="current"/>'s, the slot emptied either way; null when none waits or it is another version's.</summary>
    public (string Path, ViewerBitmap? Bitmap)? Take(int current)
    {
        lock (_gate)
        {
            var waiting = _waiting;
            _waiting = null;
            return waiting is { } answer && answer.Version == current ? (answer.Path, answer.Bitmap) : null;
        }
    }
}
