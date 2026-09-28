using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.UI;

/// <summary>
/// The session's ComfyUI pictures as a strip over the pane's upper rule (later still on 2026-09-24, the user's ask: "a
/// small strip at the bottom … new images fall into the strip at the far left and push the older ones to the right").
/// The screen <see cref="Add"/>s each picture <c>generate_image</c> or <c>/imagine</c> made — the newest at index 0 —
/// and <see cref="ScreenPane"/> draws it (<see cref="Render"/>) <see cref="Rows"/> tall whenever the screen's provider
/// answers it. With the draft empty ← / → walk a highlight (<see cref="Step"/>): nothing is highlighted at first, the
/// first arrow takes the newest, → steps older, ← newer, and ← on the newest lets go (the user picked select-and-highlight
/// over a bare scroll). The highlight is an accent bar in the gap either side of the tile, so it costs no row. The
/// window of tiles drawn (<see cref="Window"/>) moves the least it must to keep the highlighted one whole. Thread-safe:
/// the screen adds from the turn's thread and steps from the line's, the pane renders under its own lock.
/// </summary>
public sealed class PictureStrip
{
    /// <summary>The strip's rows: a square picture is 12 × 12 pixels in half-blocks (the user's pick of 4 / 6 / 8).</summary>
    public const int Rows = 6;

    /// <summary>The widest a tile is drawn, in cells (a wide picture is cut to this and gets shorter than <see cref="Rows"/>).</summary>
    public const int MaxColumns = 24;

    /// <summary>The most pictures kept: past it the oldest goes (the screen's picture registry keeps its own for a double-click).</summary>
    public const int MaxPictures = 64;

    /// <summary>The least cells the strip is drawn in: one widest tile with its edge cells.</summary>
    public const int MinCells = MaxColumns + EdgeCells;

    /// <summary>The highlight's bars: left of the tile, right of it.</summary>
    public const string LeftBar = "▐";
    public const string RightBar = "▌";

    /// <summary>The marks in the edge cells when tiles are off to that side.</summary>
    public const string MoreLeft = "‹";
    public const string MoreRight = "›";

    // One cell before the first tile (its bar or the ‹), two after the last (its bar, then the ›).
    private const int LeadCells = 1;
    private const int TrailCells = 2;
    private const int EdgeCells = LeadCells + TrailCells;

    private readonly object _gate = new();
    private readonly List<(ImageThumbnail Tile, int Id)> _entries = [];
    private int _selected = -1;
    private int _first;
    private int _version;

    /// <summary>The pictures held.</summary>
    public int Count
    {
        get { lock (_gate) { return _entries.Count; } }
    }

    /// <summary>The highlighted tile's index (0 = the newest), −1 for none.</summary>
    public int Selected
    {
        get { lock (_gate) { return _selected; } }
    }

    /// <summary>The highlighted picture's id (the screen's registry), or null.</summary>
    public int? SelectedId
    {
        get { lock (_gate) { return _selected >= 0 ? _entries[_selected].Id : null; } }
    }

    /// <summary>Bumped by every change the drawing shows — a picture, the highlight, a clear — so the pane's tick sees one.</summary>
    public int Version
    {
        get { lock (_gate) { return _version; } }
    }

    /// <summary>A picture at the left, the others one along; the highlight is let go and the window goes back to the start.</summary>
    public void Add(ImageThumbnail tile, int id)
    {
        ArgumentNullException.ThrowIfNull(tile);
        lock (_gate)
        {
            _entries.Insert(0, (tile, id));
            if (_entries.Count > MaxPictures)
            {
                _entries.RemoveAt(_entries.Count - 1);
            }

            _selected = -1;
            _first = 0;
            _version++;
        }
    }

    /// <summary>Every picture gone (a new session).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            if (_entries.Count == 0)
            {
                return;
            }

            _entries.Clear();
            _selected = -1;
            _first = 0;
            _version++;
        }
    }

    /// <summary>
    /// An arrow over the strip: <paramref name="delta"/> −1 for ←, +1 for →. Nothing highlighted, either arrow takes the
    /// newest; else → the next older (none past the oldest), ← the next newer, and ← on the newest lets go. False
    /// (the key is not spent) with no pictures.
    /// </summary>
    public bool Step(int delta)
    {
        lock (_gate)
        {
            if (_entries.Count == 0)
            {
                return false;
            }

            _selected = _selected < 0 ? 0 : Math.Clamp(_selected + Math.Sign(delta), -1, _entries.Count - 1);
            _version++;
            return true;
        }
    }

    /// <summary>
    /// The newest picture whose id <paramref name="isId"/> takes highlighted (2026-09-28, the viewer's keys under <c>ComfyUI
    /// picture strip sync</c>: one file can be registered more than once, so the screen passes every id it has for it). True
    /// when the highlight moved; with no match false and the highlight left where it was (the user's call: a picture the
    /// strip does not hold is ignored).
    /// </summary>
    public bool Highlight(Func<int, bool> isId)
    {
        ArgumentNullException.ThrowIfNull(isId);
        lock (_gate)
        {
            int index = _entries.FindIndex(e => isId(e.Id));
            if (index < 0 || index == _selected)
            {
                return false;
            }

            _selected = index;
            _version++;
            return true;
        }
    }

    /// <summary>
    /// The tiles drawn in <paramref name="cells"/>: the first index and how many, from a window that started at
    /// <paramref name="first"/>. The highlighted one (<paramref name="selected"/>, −1 for none) is kept whole, the window
    /// moving the least it must; a window whose tail would all fit from an earlier start moves back (a wider window, a
    /// cleared highlight). At least one tile whenever there are any. Pure, pinned.
    /// </summary>
    public static (int First, int Count) Window(IReadOnlyList<int> widths, int selected, int first, int cells)
    {
        ArgumentNullException.ThrowIfNull(widths);
        if (widths.Count == 0)
        {
            return (0, 0);
        }

        first = Math.Clamp(first, 0, widths.Count - 1);
        if (selected >= 0 && selected < widths.Count)
        {
            if (selected < first)
            {
                first = selected;
            }

            while (first < selected && !Fits(widths, first, selected, cells))
            {
                first++;
            }
        }

        while (first > 0 && Fits(widths, first - 1, widths.Count - 1, cells))
        {
            first--;
        }

        int last = first;
        while (last + 1 < widths.Count && Fits(widths, first, last + 1, cells))
        {
            last++;
        }

        return (first, last - first + 1);
    }

    /// <summary>Whether the tiles <paramref name="from"/> to <paramref name="to"/> fit in <paramref name="cells"/> with the gaps and edge cells.</summary>
    private static bool Fits(IReadOnlyList<int> widths, int from, int to, int cells)
    {
        int used = EdgeCells + ImageStrip.Gap * (to - from);
        for (int i = from; i <= to; i++)
        {
            used += widths[i];
        }

        return used <= cells;
    }

    /// <summary>
    /// The strip at <paramref name="cells"/>: exactly <see cref="Rows"/> lines — the window's tiles left to right, a
    /// shorter one sat on the strip's bottom, <see cref="ImageStrip.Gap"/> cells between — and where each tile landed
    /// (<see cref="PictureSpan"/>, the same on every line). With <paramref name="highlight"/> (the draft empty) the
    /// highlighted tile gets <see cref="LeftBar"/> and <see cref="RightBar"/> in the cells either side; the edge cells
    /// say <see cref="MoreLeft"/> / <see cref="MoreRight"/> on the middle row when tiles are off that side. Remembers the
    /// window for the next render.
    /// </summary>
    public (List<SegmentLine> Lines, List<PictureSpan> Spans) Render(RenderOptions options, int cells, bool highlight)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<(ImageThumbnail Tile, int Id)> shown;
        int selected;
        bool moreLeft;
        bool moreRight;
        lock (_gate)
        {
            var (first, count) = Window(_entries.Select(e => e.Tile.Width).ToList(), _selected, _first, cells);
            _first = first;
            shown = _entries.GetRange(first, count);
            selected = highlight && _selected >= first && _selected < first + count ? _selected - first : -1;
            moreLeft = first > 0;
            moreRight = first + count < _entries.Count;
        }

        var tiles = shown.Select(e => Segment.SplitLines(((IRenderable)e.Tile.ToCanvas()).Render(options, e.Tile.Width))).ToList();
        var spans = new List<PictureSpan>(shown.Count);
        int col = LeadCells;
        for (int t = 0; t < shown.Count; t++)
        {
            spans.Add(new PictureSpan(col, shown[t].Tile.Width, shown[t].Id));
            col += shown[t].Tile.Width + ImageStrip.Gap;
        }

        var lines = new List<SegmentLine>(Rows);
        int middle = Rows / 2;
        for (int row = 0; row < Rows; row++)
        {
            var line = new SegmentLine();
            line.Add(selected == 0 ? new Segment(LeftBar, Theme.Accent) : row == middle && moreLeft ? new Segment(MoreLeft, Theme.DimText) : new Segment(" "));
            for (int t = 0; t < shown.Count; t++)
            {
                // Bottom-aligned: a tile shorter than the strip starts that many rows down.
                var tileLines = tiles[t];
                int at = row - (Rows - tileLines.Count);
                if (at >= 0 && at < tileLines.Count)
                {
                    line.AddRange(tileLines[at]);
                }
                else
                {
                    line.Add(new Segment(new string(' ', shown[t].Tile.Width)));
                }

                line.Add(selected == t ? new Segment(RightBar, Theme.Accent) : new Segment(" "));
                if (t + 1 < shown.Count)
                {
                    line.Add(selected == t + 1 ? new Segment(LeftBar, Theme.Accent) : new Segment(" "));
                }
            }

            if (row == middle && moreRight)
            {
                line.Add(new Segment(MoreRight, Theme.DimText));
            }

            lines.Add(line);
        }

        return (lines, spans);
    }
}
