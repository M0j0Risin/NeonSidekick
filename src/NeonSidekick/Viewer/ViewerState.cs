namespace NeonSidekick.Viewer;

/// <summary>A picture in the viewer's folder: its full path and when it was created (the order the viewer keeps).</summary>
public sealed record ViewerEntry(string Path, DateTime CreatedUtc);

/// <summary>What a key in the viewer window does (<see cref="ViewerState.ActionFor"/>).</summary>
public enum ViewerAction
{
    None,

    /// <summary>← : the picture before the shown one (the live picture held first).</summary>
    Previous,

    /// <summary>→ : the picture after the shown one; reaching the newest follows new pictures again.</summary>
    Next,

    /// <summary>Home: the oldest picture.</summary>
    First,

    /// <summary>End: the newest picture, following again.</summary>
    Last,

    /// <summary>F11 (or a double-click): full screen on or off.</summary>
    ToggleFullScreen,

    /// <summary>Esc in full screen: back to a window.</summary>
    LeaveFullScreen,

    /// <summary>Esc in a window: the window closed.</summary>
    Close,
}

/// <summary>
/// The picture viewer's state, with no window in it (2026-09-27, the user's ask: FolderPictureViewer's behaviour inside the
/// app, opened from the ComfyUI picture strip): the folder's pictures oldest first by creation time, and which one is
/// shown — <see cref="Live"/> (the newest, and whichever arrives next) or a held index once ← or Home moved off it. A
/// new picture while live is shown; while held it is only counted, so browsing older pictures is never yanked away. A
/// picture written again (ComfyUI never does, a hand copy may) moves to the newest. A deleted one leaves the list, the
/// shown index kept on the same picture where it can be. FolderPictureViewer's double-Del delete is deliberately not
/// here: a viewer does not destroy the user's pictures. Everything <see cref="PictureWindow"/> decides is decided
/// here, so it is tested without a window. Pure; one thread (the window's).
/// </summary>
public sealed class ViewerState
{
    private readonly List<ViewerEntry> _pictures = [];

    // null = live (following the newest), else the held index.
    private int? _held;

    /// <summary>The folder the pictures are in, as shown in the title.</summary>
    public string Folder { get; private set; } = "";

    /// <summary>How many pictures the folder holds.</summary>
    public int Count => _pictures.Count;

    /// <summary>Following the newest picture (true), or held on an older one.</summary>
    public bool Live => _held is null;

    /// <summary>The shown picture's index, oldest = 0; null with no picture.</summary>
    public int? Index => _pictures.Count == 0 ? null : _held ?? _pictures.Count - 1;

    /// <summary>The shown picture's path; null with no picture.</summary>
    public string? Current => Index is int index ? _pictures[index].Path : null;

    /// <summary>The pictures, oldest first.</summary>
    public IReadOnlyList<ViewerEntry> Pictures => _pictures;

    /// <summary>A folder's listing, replacing whatever was there: sorted oldest first (the name breaking a tie), and live.</summary>
    public void Reset(string folder, IEnumerable<ViewerEntry> pictures)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(pictures);
        Folder = folder;
        _pictures.Clear();
        _pictures.AddRange(pictures
            .DistinctBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p.CreatedUtc)
            .ThenBy(p => p.Path, StringComparer.OrdinalIgnoreCase));
        _held = null;
    }

    /// <summary>
    /// A picture that arrived (created, or renamed into the folder): the newest now, the same path taken out first. True
    /// when the shown picture changed — live, or the held one was the path that moved.
    /// </summary>
    public bool Add(string path, DateTime createdUtc)
    {
        ArgumentNullException.ThrowIfNull(path);
        string? before = Current;
        int existing = IndexOf(path);
        if (existing >= 0)
        {
            RemoveAt(existing);
        }

        _pictures.Add(new ViewerEntry(path, createdUtc));
        return !string.Equals(before, Current, StringComparison.OrdinalIgnoreCase) || Live;
    }

    /// <summary>A picture that left (deleted, or renamed out): gone from the list. True when the shown picture changed.</summary>
    public bool Remove(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        int index = IndexOf(path);
        if (index < 0)
        {
            return false;
        }

        string? before = Current;
        RemoveAt(index);
        return !string.Equals(before, Current, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A browsing action (<see cref="ViewerAction.Previous"/>, <see cref="ViewerAction.Next"/>, <see cref="ViewerAction.First"/>, <see cref="ViewerAction.Last"/>); true when the shown picture or the live state changed. Anything else does nothing here.</summary>
    public bool Browse(ViewerAction action)
    {
        if (_pictures.Count == 0 || action is not (ViewerAction.Previous or ViewerAction.Next or ViewerAction.First or ViewerAction.Last))
        {
            return false;
        }

        int last = _pictures.Count - 1;
        int from = _held ?? last;
        bool wasLive = Live;
        int to = action switch
        {
            ViewerAction.Previous => Math.Max(0, from - 1),
            ViewerAction.Next => Math.Min(last, from + 1),
            ViewerAction.First => 0,
            _ => last,
        };

        // Landing on the newest follows again (FolderPictureViewer's rule); anywhere else holds.
        _held = to == last ? null : to;
        return to != from || wasLive != Live;
    }

    /// <summary>The window's title as things stand (<see cref="ViewerText.Title"/>).</summary>
    public string Title() =>
        Index is int index
            ? ViewerText.Title(System.IO.Path.GetFileName(_pictures[index].Path), index + 1, _pictures.Count, Live, Folder)
            : ViewerText.Title(null, 0, 0, true, Folder);

    // Virtual-key codes (winuser.h), the only keys the window answers.
    public const int VkEscape = 0x1B;
    public const int VkEnd = 0x23;
    public const int VkHome = 0x24;
    public const int VkLeft = 0x25;
    public const int VkRight = 0x27;
    public const int VkF11 = 0x7A;

    /// <summary>What a key does: ←/→, Home/End, F11, and Esc — out of full screen first, then the window closed. Pure.</summary>
    public static ViewerAction ActionFor(int virtualKey, bool fullScreen) => virtualKey switch
    {
        VkLeft => ViewerAction.Previous,
        VkRight => ViewerAction.Next,
        VkHome => ViewerAction.First,
        VkEnd => ViewerAction.Last,
        VkF11 => ViewerAction.ToggleFullScreen,
        VkEscape => fullScreen ? ViewerAction.LeaveFullScreen : ViewerAction.Close,
        _ => ViewerAction.None,
    };

    /// <summary>
    /// Where a picture of <paramref name="width"/> × <paramref name="height"/> is drawn in a client area of
    /// <paramref name="clientWidth"/> × <paramref name="clientHeight"/>: scaled to fit whole, up or down, aspect kept,
    /// centred (FolderPictureViewer's <c>Zoom</c>). An empty rectangle for an empty side. Pure.
    /// </summary>
    public static (int X, int Y, int Width, int Height) Fit(int width, int height, int clientWidth, int clientHeight)
    {
        if (width <= 0 || height <= 0 || clientWidth <= 0 || clientHeight <= 0)
        {
            return (0, 0, 0, 0);
        }

        double scale = Math.Min((double)clientWidth / width, (double)clientHeight / height);
        int w = Math.Max(1, (int)Math.Round(width * scale, MidpointRounding.AwayFromZero));
        int h = Math.Max(1, (int)Math.Round(height * scale, MidpointRounding.AwayFromZero));
        return ((clientWidth - w) / 2, (clientHeight - h) / 2, w, h);
    }

    private int IndexOf(string path) => _pictures.FindIndex(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));

    // Takes a picture out, the held index kept on the same picture (or the one that took its place, the last one live).
    private void RemoveAt(int index)
    {
        _pictures.RemoveAt(index);
        if (_held is not int held)
        {
            return;
        }

        if (index < held)
        {
            held--;
        }

        _held = _pictures.Count == 0 || held >= _pictures.Count - 1 ? null : Math.Max(0, held);
    }
}
