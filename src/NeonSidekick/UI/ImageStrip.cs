using Spectre.Console;
using Spectre.Console.Rendering;

namespace NeonSidekick.UI;

/// <summary>
/// The tiles a message's pictures make under the user's line: each <see cref="ImageThumbnail"/>
/// as its canvas, left to right with <see cref="Gap"/> cells between, wrapped by the width the
/// strip is rendered at (the screen's, so a narrow window wraps sooner), one blank row between
/// rows. Tiles are packed by their real widths — Spectre's own <c>Columns</c> lays a grid of
/// equal columns, and a wide photo beside a tall screenshot would leave a hole. A tile shorter
/// than a later one on its row is padded beneath with spaces so that one stays aligned; nothing
/// trails after the last tile with content on a line. Pure over the canvases' own lines. Given ids (later on
/// 2026-09-24), a render also records where each tile landed on each line (<see cref="Spans"/>), so the pane can map
/// a double-click back to its picture.
///
/// <para>The strip stands <see cref="Indent"/> cells in (2026-10-03, the user's ask: "lined up with the '[' in '[Image #1]'"),
/// the width of the prompt glyph ahead of the sent line, so a picture starts under its <c>[Image #n]</c> and beside the reply's
/// indented <c>▸</c> rows. The tiles pack in what is left; a tile as wide as the window stands in only as far as it still fits.</para>
/// </summary>
public sealed class ImageStrip : IRenderable, IPictureLayout
{
    /// <summary>Cells between two tiles on a row.</summary>
    public const int Gap = 2;

    /// <summary>Cells the strip stands in: the prompt glyph's width (<see cref="InputLine.PromptGlyph"/>), so a tile starts under the sent line's text.</summary>
    public static readonly int Indent = TextCells.Width(InputLine.PromptGlyph);

    private readonly IReadOnlyList<ImageThumbnail> _thumbnails;
    private readonly IReadOnlyList<int>? _ids;
    private List<IReadOnlyList<PictureSpan>> _spans = [];

    /// <param name="ids">The screen's id for each thumbnail, in order; null records no spans.</param>
    public ImageStrip(IReadOnlyList<ImageThumbnail> thumbnails, IReadOnlyList<int>? ids = null)
    {
        _thumbnails = thumbnails ?? throw new ArgumentNullException(nameof(thumbnails));
        if (ids is not null && ids.Count != thumbnails.Count)
        {
            throw new ArgumentException("One id per thumbnail.", nameof(ids));
        }

        _ids = ids;
    }

    /// <summary>Where the tiles landed on each line of the last render: the spacer rows hold none, every other line each tile of its strip row. Empty without ids.</summary>
    public IReadOnlyList<IReadOnlyList<PictureSpan>> Spans => _spans;

    public Measurement Measure(RenderOptions options, int maxWidth)
    {
        int widest = 0;
        foreach (var thumbnail in _thumbnails)
        {
            widest = Math.Max(widest, thumbnail.Width);
        }

        return new Measurement(Math.Min(widest + Indent, maxWidth), maxWidth);
    }

    public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
    {
        ArgumentNullException.ThrowIfNull(options);
        var segments = new List<Segment>();
        var spans = new List<IReadOnlyList<PictureSpan>>();
        int placed = 0;
        bool firstRow = true;
        int widest = 0;
        foreach (var thumbnail in _thumbnails)
        {
            widest = Math.Max(widest, thumbnail.Width);
        }

        // As far in as the widest tile still fits: a fullsize box is not pushed past the window's edge.
        int indent = Math.Clamp(maxWidth - widest, 0, Indent);
        string margin = new(' ', indent);
        foreach (var row in Pack(_thumbnails, maxWidth - indent))
        {
            if (!firstRow)
            {
                // The spacer row: one space so it stays a row (the SegmentLines convention).
                segments.Add(new Segment(" "));
                segments.Add(Segment.LineBreak);
                spans.Add([]);
            }

            // The row's tiles side by side, a gap between: each one's column on every line of the row.
            var rowSpans = new List<PictureSpan>(row.Count);
            int col = indent;
            foreach (var thumbnail in row)
            {
                if (_ids is not null)
                {
                    rowSpans.Add(new PictureSpan(col, thumbnail.Width, _ids[placed]));
                }

                placed++;
                col += thumbnail.Width + Gap;
            }

            firstRow = false;
            var tiles = new List<(int Width, List<SegmentLine> Lines)>(row.Count);
            int tallest = 0;
            foreach (var thumbnail in row)
            {
                var lines = Segment.SplitLines(((IRenderable)thumbnail.ToCanvas()).Render(options, thumbnail.Width));
                tiles.Add((thumbnail.Width, lines));
                tallest = Math.Max(tallest, lines.Count);
            }

            for (int i = 0; i < tallest; i++)
            {
                // Up to the last tile with something on this line: a shorter one before it is
                // padded to its width, nothing trails after it.
                int end = tiles.FindLastIndex(tile => i < tile.Lines.Count);
                if (indent > 0)
                {
                    segments.Add(new Segment(margin));
                }

                for (int t = 0; t <= end; t++)
                {
                    var (width, lines) = tiles[t];
                    if (t > 0)
                    {
                        segments.Add(new Segment(new string(' ', Gap)));
                    }

                    if (i < lines.Count)
                    {
                        segments.AddRange(lines[i]);
                    }
                    else
                    {
                        segments.Add(new Segment(new string(' ', width)));
                    }
                }

                segments.Add(Segment.LineBreak);
                spans.Add(rowSpans);
            }
        }

        _spans = _ids is null ? [] : spans;
        return segments;
    }

    /// <summary>
    /// The rows the tiles fall into at <paramref name="maxWidth"/>: a tile joins the row while it
    /// fits after a gap, else starts the next; one wider than the width takes a row alone. Pinned.
    /// </summary>
    public static List<List<ImageThumbnail>> Pack(IReadOnlyList<ImageThumbnail> thumbnails, int maxWidth)
    {
        ArgumentNullException.ThrowIfNull(thumbnails);
        var rows = new List<List<ImageThumbnail>>();
        List<ImageThumbnail>? row = null;
        int used = 0;
        foreach (var thumbnail in thumbnails)
        {
            if (row is null || used + Gap + thumbnail.Width > maxWidth)
            {
                row = [];
                rows.Add(row);
                used = 0;
            }
            else
            {
                used += Gap;
            }

            row.Add(thumbnail);
            used += thumbnail.Width;
        }

        return rows;
    }
}
