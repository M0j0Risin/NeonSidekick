namespace NeonSidekick.Viewer;

/// <summary>A picture in the thumbnail browser's folder: its full path and when it was created (the order the grid keeps).</summary>
public sealed record ThumbEntry(string Path, DateTime CreatedUtc);

/// <summary>What a key in the thumbnail browser does (<see cref="ThumbsState.ActionFor"/>).</summary>
public enum ThumbsAction
{
    None,

    /// <summary>← : the picture before the selected one.</summary>
    Left,

    /// <summary>→ : the picture after it.</summary>
    Right,

    /// <summary>↑ : the picture a row up.</summary>
    Up,

    /// <summary>↓ : the picture a row down (the last one when the row below is shorter).</summary>
    Down,

    /// <summary>PgUp: a page of rows up.</summary>
    PageUp,

    /// <summary>PgDn: a page of rows down.</summary>
    PageDown,

    /// <summary>Home: the first (oldest) picture.</summary>
    First,

    /// <summary>End: the last (newest) picture.</summary>
    Last,

    /// <summary>Enter: the selected picture opened in the picture viewer, brought forward.</summary>
    Open,

    /// <summary>F5: the folder listed again and the tiles fitted to the window again.</summary>
    Refresh,

    /// <summary>+ : bigger tiles for the session.</summary>
    ZoomIn,

    /// <summary>− : smaller tiles for the session.</summary>
    ZoomOut,

    /// <summary>The Apps key or Shift+F10: the picture menu on the selected tile.</summary>
    Menu,

    /// <summary>F11 (or a double-click on no tile): full screen on or off.</summary>
    ToggleFullScreen,

    /// <summary>Esc in full screen: back to a window.</summary>
    LeaveFullScreen,

    /// <summary>Esc in a window: the window closed.</summary>
    Close,
}

/// <summary>What a folder change did to the grid (<see cref="ThumbsState.Add"/>, <see cref="ThumbsState.Rename"/>).</summary>
public enum ThumbChange
{
    /// <summary>Nothing: the path was not one the grid lists.</summary>
    None,

    /// <summary>A new picture, appended at the end.</summary>
    Added,

    /// <summary>A picture already listed was written again: its tile is read again, in place.</summary>
    Changed,

    /// <summary>A listed picture took a new name, in place.</summary>
    Renamed,
}

/// <summary>
/// The thumbnail browser's state, with no window in it (2026-10-04, the user's ask: <c>/thumbs &lt;folder&gt;</c>, now <c>/view &lt;folder&gt; --thumbs</c>, a grid of the
/// folder's pictures beside the picture viewer). The pictures are kept oldest first by creation time, the path breaking a tie —
/// the viewer's own order (<see cref="ViewerState.Reset"/>) — so a picture that arrives (ComfyUI generating, the user's case)
/// goes on the end and no tile already drawn ever moves (the user's ask: "keep the thumbnails from jumping around"). A
/// picture written again is read again where it is; one renamed keeps its place; only a deleted one closes the gap.
///
/// <para>The tile size is no setting (the user's call): the largest square from <see cref="FitMax"/> down to <see cref="FitMin"/>
/// (logical pixels, scaled by the DPI) at which every picture fits in the window, else the smallest and the grid scrolls —
/// chosen when the window opens, is resized, goes full screen or changes DPI, and on F5, never when a picture arrives
/// (which would move every tile). Ctrl+wheel and +/− zoom for the session (<see cref="Zoom"/>, <see cref="ZoomMin"/> to
/// <see cref="ZoomMax"/>, a step of <see cref="ZoomStep"/>); a zoomed size survives resizes until F5 fits again. A zoom or a
/// refit keeps the selected tile (or the first one in view) where it was on the screen.</para>
///
/// <para>A view scrolled to the bottom of a grid already taller than the window stays at the bottom as pictures arrive, so a
/// user watching the newest keeps seeing it; a grid that fitted is never scrolled by an arrival. Everything
/// <see cref="ThumbsWindow"/> decides is decided here, tested without a window. Pure; one thread (the window's).</para>
/// </summary>
public sealed class ThumbsState
{
    /// <summary>The largest and smallest tile a fit chooses, in logical pixels (96 DPI).</summary>
    public const int FitMax = 256;
    public const int FitMin = 96;

    /// <summary>The bounds of a zoomed tile, in logical pixels.</summary>
    public const int ZoomMin = 64;
    public const int ZoomMax = 512;

    /// <summary>One zoom step's factor.</summary>
    public const double ZoomStep = 1.25;

    /// <summary>The gap round and between the tiles, and the scroll bar's width, in logical pixels.</summary>
    public const int Gap = 12;
    public const int BarWidth = 12;

    /// <summary>The sizes a tile's picture is decoded at (device pixels): the smallest that covers the tile.</summary>
    public static readonly int[] Buckets = [128, 192, 256, 384, 512, 768];

    private readonly List<ThumbEntry> _entries = [];

    /// <summary>The folder the pictures are in.</summary>
    public string Folder { get; private set; } = "";

    /// <summary>The pictures, oldest first.</summary>
    public IReadOnlyList<ThumbEntry> Entries => _entries;

    /// <summary>How many pictures the folder holds.</summary>
    public int Count => _entries.Count;

    /// <summary>The selected tile's index; null with none.</summary>
    public int? Selected { get; private set; }

    /// <summary>The selected picture's path; null with none.</summary>
    public string? SelectedPath => Selected is int index ? _entries[index].Path : null;

    /// <summary>The client area, its DPI and a caption's height (device pixels), as the window last said (<see cref="Relayout"/>).</summary>
    public int ClientWidth { get; private set; }
    public int ClientHeight { get; private set; }
    public uint Dpi { get; private set; } = 96;
    public int CaptionHeight { get; private set; }

    /// <summary>A tile's side in device pixels.</summary>
    public int Tile { get; private set; } = FitMax;

    /// <summary>Whether the size is a zoom (kept on resize) rather than a fit.</summary>
    public bool Zoomed { get; private set; }

    /// <summary>The tiles a row holds.</summary>
    public int Columns { get; private set; } = 1;

    /// <summary>How far the grid is scrolled, in device pixels.</summary>
    public int ScrollTop { get; private set; }

    /// <summary>The gap in device pixels.</summary>
    public int GapPixels => Scale(Gap, Dpi);

    /// <summary>The scroll bar's width in device pixels.</summary>
    public int BarPixels => Scale(BarWidth, Dpi);

    /// <summary>The width the tiles share: the client less the scroll bar, always kept for it so the grid never reflows.</summary>
    public int ContentWidth => Math.Max(1, ClientWidth - BarPixels);

    /// <summary>One column's and one row's step.</summary>
    public int PitchX => Tile + GapPixels;
    public int PitchY => Tile + CaptionHeight + GapPixels;

    /// <summary>How many rows the pictures take.</summary>
    public int Rows => _entries.Count == 0 ? 0 : (_entries.Count + Columns - 1) / Columns;

    /// <summary>The grid's whole height, the gaps above and below included.</summary>
    public int ContentHeight => GapPixels + Rows * PitchY;

    /// <summary>The farthest the grid scrolls.</summary>
    public int MaxScroll => Math.Max(0, ContentHeight - ClientHeight);

    /// <summary>The whole rows a page shows, at least one.</summary>
    public int PageRows => Math.Max(1, ClientHeight / Math.Max(1, PitchY));

    /// <summary>The first column's left edge: the grid centred in the content width.</summary>
    public int Left => GapPixels + Math.Max(0, (ContentWidth - GapPixels - Columns * PitchX) / 2);

    /// <summary>A folder's listing, replacing whatever was there: oldest first (the name breaking a tie), nothing selected, at the top, fitted again.</summary>
    public void Reset(string folder, IEnumerable<ThumbEntry> pictures)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(pictures);
        Folder = folder;
        _entries.Clear();
        _entries.AddRange(pictures
            .DistinctBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p.CreatedUtc)
            .ThenBy(p => p.Path, StringComparer.OrdinalIgnoreCase));
        Selected = null;
        ScrollTop = 0;
        Zoomed = false;
        Fit();
    }

    /// <summary>The index of <paramref name="path"/>, or -1.</summary>
    public int IndexOf(string path) => _entries.FindIndex(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A picture that arrived (created, renamed into the folder, or written again): a new one goes on the end, nothing else moving,
    /// the view kept at the bottom when it was there and the grid already scrolled; one already listed is
    /// <see cref="ThumbChange.Changed"/>, left where it is.
    /// </summary>
    public ThumbChange Add(string path, DateTime createdUtc)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (IndexOf(path) >= 0)
        {
            return ThumbChange.Changed;
        }

        bool following = MaxScroll > 0 && ScrollTop >= MaxScroll;
        _entries.Add(new ThumbEntry(path, createdUtc));
        if (following)
        {
            ScrollTop = MaxScroll;
        }

        return ThumbChange.Added;
    }

    /// <summary>
    /// A listed picture renamed: the new name in its place (<see cref="ThumbChange.Renamed"/>). A rename onto a name already listed (a
    /// file written whole beside it and moved over it) drops the old entry and reads the new one again (<see cref="ThumbChange.Changed"/>);
    /// an old name not listed is <see cref="Add"/>'s case.
    /// </summary>
    public ThumbChange Rename(string oldPath, string newPath, DateTime createdUtc)
    {
        ArgumentNullException.ThrowIfNull(oldPath);
        ArgumentNullException.ThrowIfNull(newPath);
        int old = IndexOf(oldPath);
        if (old < 0)
        {
            return Add(newPath, createdUtc);
        }

        if (IndexOf(newPath) >= 0)
        {
            Remove(oldPath);
            return ThumbChange.Changed;
        }

        _entries[old] = _entries[old] with { Path = newPath };
        return ThumbChange.Renamed;
    }

    /// <summary>A picture that left: gone, the tiles after it closing up; the selection kept on the same picture, or the one now in its place. True when it was listed.</summary>
    public bool Remove(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        int index = IndexOf(path);
        if (index < 0)
        {
            return false;
        }

        _entries.RemoveAt(index);
        if (Selected is int selected)
        {
            Selected = _entries.Count == 0 ? null : selected > index ? selected - 1 : Math.Min(selected, _entries.Count - 1);
        }

        ScrollTop = Math.Clamp(ScrollTop, 0, MaxScroll);
        return true;
    }

    /// <summary><paramref name="path"/> selected and scrolled into view; a path not listed (written a moment ago, its event not read yet) is added first. True when the selection changed.</summary>
    public bool Select(string path, DateTime createdUtc)
    {
        ArgumentNullException.ThrowIfNull(path);
        int index = IndexOf(path);
        if (index < 0)
        {
            Add(path, createdUtc);
            index = _entries.Count - 1;
        }

        return SelectIndex(index);
    }

    /// <summary>The tile at <paramref name="index"/> selected and scrolled into view. True when the selection changed.</summary>
    public bool SelectIndex(int index)
    {
        if (index < 0 || index >= _entries.Count)
        {
            return false;
        }

        bool changed = Selected != index;
        Selected = index;
        EnsureVisible(index);
        return changed;
    }

    /// <summary>
    /// The window's size, DPI and caption height: the tiles fitted again unless zoomed (a zoomed size carried to the new DPI), the
    /// columns counted, the selected tile (or the first in view) kept where it was on the screen.
    /// </summary>
    public void Relayout(int clientWidth, int clientHeight, uint dpi, int captionHeight)
    {
        Anchored(() =>
        {
            uint was = Dpi;
            ClientWidth = Math.Max(1, clientWidth);
            ClientHeight = Math.Max(1, clientHeight);
            Dpi = Math.Max(96u, dpi);
            CaptionHeight = Math.Max(0, captionHeight);
            if (Zoomed)
            {
                Tile = Math.Max(1, (int)Math.Round(Tile * (double)Dpi / was, MidpointRounding.AwayFromZero));
                Columns = ColumnsFor(Tile);
            }
            else
            {
                Fit();
            }
        });
    }

    /// <summary>F5's half (the other is the listing again): the zoom dropped and the tiles fitted to the window.</summary>
    public void Refit() => Anchored(() =>
    {
        Zoomed = false;
        Fit();
    });

    /// <summary>
    /// <paramref name="steps"/> zoom steps (positive bigger): the tile × <see cref="ZoomStep"/> each, kept between <see cref="ZoomMin"/>
    /// and <see cref="ZoomMax"/>. True when the size changed.
    /// </summary>
    public bool Zoom(int steps)
    {
        if (steps == 0)
        {
            return false;
        }

        double logical = Tile * 96.0 / Dpi;
        int target = (int)Math.Round(Math.Clamp(logical * Math.Pow(ZoomStep, steps), ZoomMin, ZoomMax), MidpointRounding.AwayFromZero);
        int tile = Scale(target, Dpi);
        if (tile == Tile)
        {
            return false;
        }

        Anchored(() =>
        {
            Zoomed = true;
            Tile = tile;
            Columns = ColumnsFor(Tile);
        });
        return true;
    }

    /// <summary>
    /// The largest tile from <see cref="FitMax"/> down to <see cref="FitMin"/> (logical, scaled by <paramref name="dpi"/>) at which
    /// <paramref name="count"/> pictures fit in <paramref name="contentWidth"/> × <paramref name="clientHeight"/> with
    /// <paramref name="captionHeight"/> under each; the smallest when none does (the grid scrolls), the largest for none. Pure.
    /// </summary>
    public static int FitTile(int contentWidth, int clientHeight, int count, uint dpi, int captionHeight)
    {
        int max = Scale(FitMax, dpi), min = Scale(FitMin, dpi), gap = Scale(Gap, dpi);
        if (count <= 0)
        {
            return max;
        }

        int step = Math.Max(1, Scale(4, dpi));
        for (int tile = max; tile > min; tile -= step)
        {
            int columns = Math.Max(1, (contentWidth - gap) / (tile + gap));
            int rows = (count + columns - 1) / columns;
            if (gap + (long)rows * (tile + captionHeight + gap) <= clientHeight)
            {
                return tile;
            }
        }

        return min;
    }

    /// <summary>The tile under a client point (on the picture or its caption, not the gaps); null for none.</summary>
    public int? HitTest(int x, int y)
    {
        if (x >= ContentWidth || _entries.Count == 0)
        {
            return null;
        }

        int cx = x - Left;
        int cy = y + ScrollTop - GapPixels;
        if (cx < 0 || cy < 0)
        {
            return null;
        }

        int column = cx / PitchX, row = cy / PitchY;
        if (column >= Columns || cx - column * PitchX >= Tile || cy - row * PitchY >= Tile + CaptionHeight)
        {
            return null;
        }

        int index = row * Columns + column;
        return index < _entries.Count ? index : null;
    }

    /// <summary>The tile at <paramref name="index"/>'s picture square in client coordinates (its caption is under it, <see cref="CaptionHeight"/> tall).</summary>
    public (int X, int Y, int Side) TileRect(int index)
    {
        int row = index / Columns, column = index % Columns;
        return (Left + column * PitchX, GapPixels + row * PitchY - ScrollTop, Tile);
    }

    /// <summary>The grid scrolled the least that brings the tile at <paramref name="index"/> whole into view, its gaps included.</summary>
    public void EnsureVisible(int index)
    {
        if (index < 0 || index >= _entries.Count)
        {
            return;
        }

        int row = index / Columns;
        int top = row * PitchY;
        int bottom = GapPixels + (row + 1) * PitchY;
        if (top < ScrollTop)
        {
            ScrollTop = top;
        }
        else if (bottom > ScrollTop + ClientHeight)
        {
            ScrollTop = bottom - ClientHeight;
        }

        ScrollTop = Math.Clamp(ScrollTop, 0, MaxScroll);
    }

    /// <summary>
    /// The tiles in view, then <paramref name="extraRows"/> more rows above and below (what is decoded ahead): first and last index,
    /// or (0, -1) for none.
    /// </summary>
    public (int First, int Last) Visible(int extraRows = 0)
    {
        if (_entries.Count == 0)
        {
            return (0, -1);
        }

        int firstRow = Math.Max(0, (ScrollTop - GapPixels) / PitchY - extraRows);
        int lastRow = Math.Min(Rows - 1, (ScrollTop + ClientHeight) / PitchY + extraRows);
        return (Math.Min(_entries.Count - 1, firstRow * Columns), Math.Min(_entries.Count - 1, (lastRow + 1) * Columns - 1));
    }

    /// <summary>The grid scrolled by <paramref name="pixels"/>, kept in range. True when it moved.</summary>
    public bool ScrollBy(int pixels) => ScrollTo(ScrollTop + pixels);

    /// <summary>The grid scrolled to <paramref name="top"/>, kept in range. True when it moved.</summary>
    public bool ScrollTo(int top)
    {
        int clamped = Math.Clamp(top, 0, MaxScroll);
        bool moved = clamped != ScrollTop;
        ScrollTop = clamped;
        return moved;
    }

    /// <summary>
    /// The pixels a wheel turn of <paramref name="delta"/> (120 a notch) scrolls: a row a notch, a precision touchpad's small turns
    /// added up in <paramref name="remainder"/> (delta × pitch units). Positive scrolls down (a turn toward the user is a negative delta). Pure.
    /// </summary>
    public static int WheelPixels(ref int remainder, int delta, int pitch)
    {
        // The remainder is kept in delta × pitch units, so no part of a pixel is lost or counted twice.
        long total = remainder + (long)delta * Math.Max(1, pitch);
        long pixels = total / 120;
        remainder = (int)(total - pixels * 120);
        return (int)-pixels;
    }

    /// <summary>
    /// A browsing action: the selection moved (the first tile when there was none, End's the last) and scrolled into view. True when
    /// the selection changed. Anything else does nothing here.
    /// </summary>
    public bool Apply(ThumbsAction action)
    {
        if (_entries.Count == 0)
        {
            return false;
        }

        int last = _entries.Count - 1;
        if (Selected is not int from)
        {
            return action switch
            {
                ThumbsAction.Last => SelectIndex(last),
                ThumbsAction.Left or ThumbsAction.Right or ThumbsAction.Up or ThumbsAction.Down or ThumbsAction.PageUp or ThumbsAction.PageDown or ThumbsAction.First => SelectIndex(FirstInView()),
                _ => false,
            };
        }

        int page = PageRows * Columns;
        int to = action switch
        {
            ThumbsAction.Left => Math.Max(0, from - 1),
            ThumbsAction.Right => Math.Min(last, from + 1),
            ThumbsAction.Up => from >= Columns ? from - Columns : from,
            ThumbsAction.Down => from + Columns <= last ? from + Columns : from / Columns < last / Columns ? last : from,
            ThumbsAction.PageUp => from >= page ? from - page : from % Columns,
            ThumbsAction.PageDown => from + page <= last ? from + page : Math.Min(last, (last / Columns) * Columns + from % Columns),
            ThumbsAction.First => 0,
            ThumbsAction.Last => last,
            _ => -1,
        };

        return to >= 0 && SelectIndex(to);
    }

    /// <summary>The bucket a tile of <paramref name="tilePixels"/> is decoded at: the smallest of <see cref="Buckets"/> that covers it. Pure.</summary>
    public static int Bucket(int tilePixels)
    {
        foreach (int bucket in Buckets)
        {
            if (tilePixels <= bucket)
            {
                return bucket;
            }
        }

        return Buckets[^1];
    }

    /// <summary>The window's title as things stand (<see cref="ThumbsText.Title"/>).</summary>
    public string Title() =>
        ThumbsText.Title(Folder, _entries.Count, Selected is int index ? System.IO.Path.GetFileName(_entries[index].Path) : null, (Selected ?? -1) + 1);

    // Virtual-key codes (winuser.h), the keys the window answers.
    public const int VkReturn = 0x0D;
    public const int VkEscape = 0x1B;
    public const int VkPageUp = 0x21;
    public const int VkPageDown = 0x22;
    public const int VkEnd = 0x23;
    public const int VkHome = 0x24;
    public const int VkLeft = 0x25;
    public const int VkUp = 0x26;
    public const int VkRight = 0x27;
    public const int VkDown = 0x28;
    public const int VkApps = 0x5D;
    public const int VkAdd = 0x6B;
    public const int VkSubtract = 0x6D;
    public const int VkF5 = 0x74;
    public const int VkF10 = 0x79;
    public const int VkF11 = 0x7A;
    public const int VkOemPlus = 0xBB;
    public const int VkOemMinus = 0xBD;

    /// <summary>
    /// What a key does: the arrows, PgUp/PgDn, Home/End move the selection, Enter opens it in the viewer, F5 lists and fits again,
    /// + and − (either row's) zoom, the Apps key or Shift+F10 open the picture menu, F11 is full screen, Esc leaves full screen and then
    /// closes. A key with Ctrl held is never the window's (a chord goes to the terminal). Pure.
    /// </summary>
    public static ThumbsAction ActionFor(int virtualKey, bool control, bool shift, bool fullScreen)
    {
        if (control)
        {
            return ThumbsAction.None;
        }

        return virtualKey switch
        {
            VkF10 when shift => ThumbsAction.Menu,
            VkApps => ThumbsAction.Menu,
            VkLeft => ThumbsAction.Left,
            VkRight => ThumbsAction.Right,
            VkUp => ThumbsAction.Up,
            VkDown => ThumbsAction.Down,
            VkPageUp => ThumbsAction.PageUp,
            VkPageDown => ThumbsAction.PageDown,
            VkHome => ThumbsAction.First,
            VkEnd => ThumbsAction.Last,
            VkReturn => ThumbsAction.Open,
            VkF5 => ThumbsAction.Refresh,
            VkAdd or VkOemPlus => ThumbsAction.ZoomIn,
            VkSubtract or VkOemMinus => ThumbsAction.ZoomOut,
            VkF11 => ThumbsAction.ToggleFullScreen,
            VkEscape => fullScreen ? ThumbsAction.LeaveFullScreen : ThumbsAction.Close,
            _ => ThumbsAction.None,
        };
    }

    /// <summary><paramref name="logical"/> pixels at <paramref name="dpi"/>. Pure.</summary>
    public static int Scale(int logical, uint dpi) => (int)(logical * (long)Math.Max(96u, dpi) / 96);

    private int ColumnsFor(int tile) => Math.Max(1, (ContentWidth - GapPixels) / (tile + GapPixels));

    private void Fit()
    {
        Tile = FitTile(ContentWidth, ClientHeight, _entries.Count, Dpi, CaptionHeight);
        Columns = ColumnsFor(Tile);
        ScrollTop = Math.Clamp(ScrollTop, 0, MaxScroll);
    }

    private int FirstInView() => Math.Min(_entries.Count - 1, Math.Max(0, (ScrollTop - GapPixels + PitchY - 1) / Math.Max(1, PitchY)) * Columns);

    // A change of size or zoom with the selected tile — or, with none in view, the first in view — left where it was on the screen.
    private void Anchored(Action change)
    {
        int? anchor = Selected is int selected && IsInView(selected) ? selected : _entries.Count > 0 && ClientHeight > 0 ? FirstInView() : null;
        int offset = anchor is int a ? RowTop(a) - ScrollTop : 0;
        change();
        ScrollTop = anchor is int b ? Math.Clamp(RowTop(b) - offset, 0, MaxScroll) : Math.Clamp(ScrollTop, 0, MaxScroll);
    }

    private bool IsInView(int index)
    {
        int top = RowTop(index);
        return top + Tile > ScrollTop && top < ScrollTop + ClientHeight;
    }

    private int RowTop(int index) => GapPixels + index / Columns * PitchY;
}
