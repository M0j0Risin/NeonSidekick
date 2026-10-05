namespace NeonSidekick.Viewer;

/// <summary>A picture in the viewer's folder: its full path and when it was created (the order the viewer keeps).</summary>
public sealed record ViewerEntry(string Path, DateTime CreatedUtc);

/// <summary>What a key in the viewer window does (<see cref="ViewerState.ActionFor"/>).</summary>
public enum ViewerAction
{
    None,

    /// <summary>→ : the picture older than the shown one (the live picture held first). The viewer runs newest at the left since
    /// 2026-10-03 (the user's ask), as the picture strip does; → was the newer one before.</summary>
    Older,

    /// <summary>← : the picture newer than the shown one; reaching the newest follows new pictures again.</summary>
    Newer,

    /// <summary>End: the oldest picture (Home's until 2026-10-03).</summary>
    Oldest,

    /// <summary>Home: the newest picture, following again (End's until 2026-10-03).</summary>
    Newest,

    /// <summary>F11 (or a double-click): full screen on or off.</summary>
    ToggleFullScreen,

    /// <summary>Esc in full screen: back to a window.</summary>
    LeaveFullScreen,

    /// <summary>Esc in a window: the window closed.</summary>
    Close,

    /// <summary>Del: the first arms (<see cref="ViewerState.PressDelete"/>), a second within <see cref="ViewerState.DeleteArmMilliseconds"/> on the same picture deletes it.</summary>
    Delete,

    /// <summary>F9: the slide show started or stopped.</summary>
    ToggleSlideShow,

    /// <summary>F10: the slide show's order, the folder's or random.</summary>
    ToggleShuffle,

    /// <summary>↑ during the slide show: a second more per slide.</summary>
    LongerSlides,

    /// <summary>↓ during the slide show: a second less per slide.</summary>
    ShorterSlides,

    /// <summary>Esc during the slide show: the show stopped (the window stays).</summary>
    StopSlideShow,

    /// <summary>The Apps key or Shift+F10 (2026-10-04): the picture menu on the shown picture, as a right-click opens it.</summary>
    Menu,
}

/// <summary>
/// The picture viewer's state, with no window in it (2026-09-27, the user's ask: FolderPictureViewer's behaviour inside the
/// app, opened from the ComfyUI picture strip): the folder's pictures oldest first by creation time, and which one is
/// shown — <see cref="Live"/> (the newest, and whichever arrives next) or a held index once → or End moved off it. A
/// new picture while live is shown; while held it is only counted, so browsing older pictures is never yanked away. A
/// picture written again (ComfyUI never does, a hand copy may) moves to the newest. A deleted one leaves the list, the
/// shown index kept on the same picture where it can be. FolderPictureViewer's double-Del delete, first left out, is
/// here since later on 2026-09-27 (the user's call): Del twice deletes the shown picture — permanently, not to the
/// Recycle Bin — the first Del arming it for <see cref="DeleteArmMilliseconds"/> with a hint in the title, and any other
/// key, the shown picture changing or the time running out disarming it. Everything <see cref="PictureWindow"/> decides
/// is decided here, so it is tested without a window. The slide show (later on 2026-09-27, the user's ask): F9 starts
/// and stops it, it loops until stopped, <see cref="DefaultSlideSeconds"/> a slide with ↑ / ↓ a second more or less, the
/// folder's order or, after F10, a random one that shows every picture once a round; Esc stops it before it leaves full
/// screen or closes. Since 2026-10-03 (the user's ask) the keys, the title and the slide show run newest at the left, the
/// picture strip's way: ← newer, → older, Home the newest (live), End the oldest, the title counting the newest 1, and the
/// slide show stepping older; the list itself is still kept oldest first. Pure; one thread (the window's).
/// </summary>
public sealed class ViewerState
{
    private readonly List<ViewerEntry> _pictures = [];

    // null = live (following the newest), else the held index.
    private int? _held;

    // The picture the first Del armed, and when (milliseconds on the caller's clock); null = not armed.
    private string? _armedPath;
    private long _armedAt;

    // The pictures the shuffled slide show has not shown yet this round.
    private readonly List<string> _bag = [];

    /// <summary>A slide's time before ↑ / ↓, and its bounds.</summary>
    public const int DefaultSlideSeconds = 5;
    public const int MinSlideSeconds = 1;
    public const int MaxSlideSeconds = 60;

    /// <summary>Whether the slide show is running.</summary>
    public bool SlideShow { get; private set; }

    /// <summary>How long each slide is shown.</summary>
    public int SlideSeconds { get; private set; } = DefaultSlideSeconds;

    /// <summary>The slide show in random order (F10) rather than the folder's.</summary>
    public bool Shuffle { get; private set; }

    /// <summary>How long the first Del stays armed: a second Del within it deletes the picture. 3 s at first, 2 s since
    /// 2026-09-28 (the user's call).</summary>
    public const uint DeleteArmMilliseconds = 2000;

    /// <summary>Whether a Del armed the shown picture (the title shows the hint). Moving off the picture drops it.</summary>
    public bool DeleteArmed => _armedPath is not null && string.Equals(_armedPath, Current, StringComparison.OrdinalIgnoreCase);

    /// <summary>The folder the pictures are in, as shown in the title.</summary>
    public string Folder { get; private set; } = "";

    /// <summary>How many pictures the folder holds.</summary>
    public int Count => _pictures.Count;

    /// <summary>Following the newest picture (true), or held on an older one.</summary>
    public bool Live => _held is null;

    /// <summary>The shown picture's index, oldest = 0; null with no picture.</summary>
    public int? Index => _pictures.Count == 0 ? null : _held ?? _pictures.Count - 1;

    /// <summary>Whether ← would move (2026-10-03, the viewer's <c>&lt;</c> button, <see cref="ViewerNav"/>): a picture shown that is not the newest.</summary>
    public bool CanNewer => Index is int index && index < _pictures.Count - 1;

    /// <summary>Whether → would move (2026-10-03, the <c>&gt;</c> button): a picture shown that is not the oldest.</summary>
    public bool CanOlder => Index is > 0;

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
        _armedPath = null;
        SlideShow = false;
        _bag.Clear();
    }

    /// <summary>
    /// A slide-show action: <see cref="ViewerAction.ToggleSlideShow"/>, <see cref="ViewerAction.StopSlideShow"/>,
    /// <see cref="ViewerAction.ToggleShuffle"/> (remembered with the show off too), <see cref="ViewerAction.LongerSlides"/> and
    /// <see cref="ViewerAction.ShorterSlides"/> (only while it runs, clamped). True when anything changed.
    /// </summary>
    public bool Slides(ViewerAction action)
    {
        switch (action)
        {
            case ViewerAction.ToggleSlideShow:
                SlideShow = !SlideShow;
                _bag.Clear();
                return true;
            case ViewerAction.StopSlideShow:
                bool was = SlideShow;
                SlideShow = false;
                return was;
            case ViewerAction.ToggleShuffle:
                Shuffle = !Shuffle;
                _bag.Clear();
                return true;
            case ViewerAction.LongerSlides or ViewerAction.ShorterSlides when SlideShow:
                int before = SlideSeconds;
                SlideSeconds = Math.Clamp(SlideSeconds + (action == ViewerAction.LongerSlides ? 1 : -1), MinSlideSeconds, MaxSlideSeconds);
                return SlideSeconds != before;
            default:
                return false;
        }
    }

    /// <summary>
    /// The slide show's next picture: in order the one older than the shown one, a step to the right, the oldest wrapping to
    /// the newest (live there, as <see cref="Browse"/> is, so a picture that arrived meanwhile is the slide after the oldest;
    /// the show ran oldest to newest until 2026-10-03, the user's call); shuffled a random one not shown yet this round and
    /// never the shown one, the round refilled when it runs out. False with fewer than two pictures.
    /// </summary>
    public bool NextSlide(Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        if (_pictures.Count < 2 || Index is not int from)
        {
            return false;
        }

        int to;
        if (Shuffle)
        {
            string current = _pictures[from].Path;
            _bag.RemoveAll(p => IndexOf(p) < 0 || string.Equals(p, current, StringComparison.OrdinalIgnoreCase));
            if (_bag.Count == 0)
            {
                _bag.AddRange(_pictures.Select(p => p.Path).Where(p => !string.Equals(p, current, StringComparison.OrdinalIgnoreCase)));
            }

            int pick = random.Next(_bag.Count);
            to = IndexOf(_bag[pick]);
            _bag.RemoveAt(pick);
        }
        else
        {
            to = from == 0 ? _pictures.Count - 1 : from - 1;
        }

        _held = to == _pictures.Count - 1 ? null : to;
        return true;
    }

    /// <summary>
    /// A Del pressed at <paramref name="nowMilliseconds"/>: the shown picture's path when a Del armed that same picture no
    /// more than <see cref="DeleteArmMilliseconds"/> before (disarmed; the caller deletes it), else null with the shown
    /// picture armed from now. Null with no picture.
    /// </summary>
    public string? PressDelete(long nowMilliseconds)
    {
        string? current = Current;
        if (current is null)
        {
            _armedPath = null;
            return null;
        }

        if (DeleteArmed && nowMilliseconds - _armedAt <= DeleteArmMilliseconds)
        {
            _armedPath = null;
            return current;
        }

        _armedPath = current;
        _armedAt = nowMilliseconds;
        return null;
    }

    /// <summary>Any arming dropped; true when the shown picture was armed (the title changes).</summary>
    public bool Disarm()
    {
        bool was = DeleteArmed;
        _armedPath = null;
        return was;
    }

    /// <summary>
    /// A picture that arrived (created, or renamed into the folder): the newest now, the same path taken out first. True
    /// when the shown picture changed — live, or the held one was the path that moved.
    ///
    /// <para>The path taken out is not <see cref="RemoveAt"/>'s (2026-09-28, code review): that one goes live when the held
    /// index becomes the last, so holding the second newest while the newest was written again pulled the view to it. Here the
    /// path goes straight back on the end, so the held index only shifts down past it and never becomes the last.</para>
    /// </summary>
    public bool Add(string path, DateTime createdUtc)
    {
        ArgumentNullException.ThrowIfNull(path);
        string? before = Current;
        int existing = IndexOf(path);
        if (existing >= 0)
        {
            _pictures.RemoveAt(existing);
            if (_held is int held && existing < held)
            {
                _held = held - 1;
            }
        }

        _pictures.Add(new ViewerEntry(path, createdUtc));
        return !string.Equals(before, Current, StringComparison.OrdinalIgnoreCase) || Live;
    }

    /// <summary>Whether <paramref name="path"/> is one of the folder's listed pictures.</summary>
    public bool Contains(string path) => IndexOf(path) >= 0;

    /// <summary>
    /// A listed picture written again in place (2026-10-04: the picture menu's <c>overwrite-original</c>, a replace that arrives as a
    /// rename onto the name, or a write's own change): it stays where it is — <see cref="Add"/> would move it to the newest and a held
    /// window would slide off it. True when it is the shown one, which is then read again.
    /// </summary>
    public bool Touched(string path) => string.Equals(Current, path, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The pictures a wheel turn of <paramref name="delta"/> (120 a notch) steps (2026-10-04, the user's ask: the wheel browses the
    /// viewer): one a notch, a precision touchpad's small turns added up in <paramref name="remainder"/>. Positive is a turn away from
    /// the user, which steps newer (←); negative steps older (→), as a list scrolls down. Pure.
    /// </summary>
    public static int WheelSteps(ref int remainder, int delta)
    {
        remainder += delta;
        int steps = remainder / 120;
        remainder -= steps * 120;
        return steps;
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

    /// <summary>A browsing action (<see cref="ViewerAction.Older"/>, <see cref="ViewerAction.Newer"/>, <see cref="ViewerAction.Oldest"/>, <see cref="ViewerAction.Newest"/>); true when the shown picture or the live state changed. Anything else does nothing here.</summary>
    public bool Browse(ViewerAction action)
    {
        if (_pictures.Count == 0 || action is not (ViewerAction.Older or ViewerAction.Newer or ViewerAction.Oldest or ViewerAction.Newest))
        {
            return false;
        }

        int last = _pictures.Count - 1;
        int from = _held ?? last;
        bool wasLive = Live;
        int to = action switch
        {
            ViewerAction.Older => Math.Max(0, from - 1),
            ViewerAction.Newer => Math.Min(last, from + 1),
            ViewerAction.Oldest => 0,
            _ => last,
        };

        // Landing on the newest follows again (FolderPictureViewer's rule); anywhere else holds.
        _held = to == last ? null : to;
        return to != from || wasLive != Live;
    }

    /// <summary>
    /// A picture shown on request (2026-09-27, the user's call: a double-clicked picture opens the viewer on its folder, held
    /// on that picture): <paramref name="path"/> held, or live when it is the newest. A path not in the list yet (written a
    /// moment ago, its watcher event not drained) is added first as the newest, from <paramref name="createdUtc"/>. True when
    /// the shown picture or the live state changed.
    /// </summary>
    public bool Select(string path, DateTime createdUtc)
    {
        ArgumentNullException.ThrowIfNull(path);
        string? before = Current;
        bool wasLive = Live;
        int index = IndexOf(path);
        if (index < 0)
        {
            _pictures.Add(new ViewerEntry(path, createdUtc));
            index = _pictures.Count - 1;
        }

        _held = index == _pictures.Count - 1 ? null : index;
        return !string.Equals(before, Current, StringComparison.OrdinalIgnoreCase) || wasLive != Live;
    }

    /// <summary>The window's title as things stand (<see cref="ViewerText.Title"/>): the newest is 1, the strip's count (2026-10-03).</summary>
    public string Title() =>
        Index is int index
            ? ViewerText.Title(System.IO.Path.GetFileName(_pictures[index].Path), _pictures.Count - index, _pictures.Count, Live, Folder, DeleteArmed, SlideShow ? SlideSeconds : null, Shuffle)
            : ViewerText.Title(null, 0, 0, true, Folder);

    // Virtual-key codes (winuser.h), the only keys the window answers.
    public const int VkEscape = 0x1B;
    public const int VkEnd = 0x23;
    public const int VkHome = 0x24;
    public const int VkLeft = 0x25;
    public const int VkRight = 0x27;
    public const int VkUp = 0x26;
    public const int VkDown = 0x28;
    public const int VkDelete = 0x2E;
    public const int VkApps = 0x5D;
    public const int VkF9 = 0x78;
    public const int VkF10 = 0x79;
    public const int VkF11 = 0x7A;

    /// <summary>What a key does: ← newer / → older, Home newest / End oldest (the strip's way since 2026-10-03), F9–F11, ↑/↓, Del, the Apps key or Shift+F10 the picture menu (2026-10-04), and Esc — the slide show stopped first, then out of full screen, then the window closed. Pure.</summary>
    public static ViewerAction ActionFor(int virtualKey, bool fullScreen, bool slideShow = false, bool shift = false) => virtualKey switch
    {
        VkEscape when slideShow => ViewerAction.StopSlideShow,
        VkF10 when shift => ViewerAction.Menu,
        VkApps => ViewerAction.Menu,
        VkF9 => ViewerAction.ToggleSlideShow,
        VkF10 => ViewerAction.ToggleShuffle,
        VkUp => ViewerAction.LongerSlides,
        VkDown => ViewerAction.ShorterSlides,
        VkLeft => ViewerAction.Newer,
        VkRight => ViewerAction.Older,
        VkHome => ViewerAction.Newest,
        VkEnd => ViewerAction.Oldest,
        VkF11 => ViewerAction.ToggleFullScreen,
        VkDelete => ViewerAction.Delete,
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

    /// <summary>
    /// Whether a move of (<paramref name="dx"/>, <paramref name="dy"/>) pixels from a press has left the system's drag
    /// rectangle of <paramref name="dragWidth"/> × <paramref name="dragHeight"/> (SM_CXDRAG / SM_CYDRAG) centred on it, as
    /// <c>DragDetect</c> judges it: the viewer's drag out starts there (2026-09-28). Pure.
    /// </summary>
    public static bool PastDragThreshold(int dx, int dy, int dragWidth, int dragHeight) =>
        Math.Abs(dx) > dragWidth / 2 || Math.Abs(dy) > dragHeight / 2;

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
