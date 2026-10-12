using System.Text;
using NeonSidekick.App;
using NeonSidekick.HomeAssistant;
using Spectre.Console;

namespace NeonSidekick.UI;

/// <summary>What a cell of the home bar is: the status glyph, a chip (<see cref="HomeSpan.Index"/> into the chips), or a page arrow.</summary>
public enum HomeTarget
{
    Status,
    Chip,
    Prev,
    Next,
}

/// <summary>One clickable run of the home bar: where it starts and how wide it is, and what it is.</summary>
public readonly record struct HomeSpan(int Start, int Cells, HomeTarget Target, int Index = -1);

/// <summary>
/// The home bar's row as the pane writes it: styled runs, the clickable spans, how many pages the chips took, and the key the
/// pane compares with the drawn one — the text <em>and</em> the styles, since a light changing colour changes no text.
/// </summary>
public sealed record HomeRow(IReadOnlyList<PerfSegment> Segments, IReadOnlyList<HomeSpan> Spans, int Pages, string Key);

/// <summary>
/// The home bar's row (2026-10-12, the user's ask: a bar of Home Assistant rooms and devices under the perf bar): the status
/// glyph <see cref="HomeBarText.StatusGlyph"/> at the left in the feed's colour, then the chips (<see cref="HomeChips"/>) side by side, each
/// with a blank at either end — <c> 💡 Desk 80% </c> — so two blanks part one name from the next (a third, a gap between
/// the chips, went on 2026-10-12, the user's call, once the chips lost their fills). Left-aligned (a row of things to press reads from its start, unlike the
/// meters). Too narrow a window shortens the names (<see cref="HomeChip.Short"/>), then drops them (the glyph and the value),
/// then splits the chips into pages behind <see cref="HomeBarText.PrevGlyph"/>/<see cref="HomeBarText.NextGlyph"/>. Pure.
/// </summary>
public static class HomeBar
{
    /// <summary>How a chip is written: its whole name, its short one, or none.</summary>
    public enum Fit
    {
        Full,
        Short,
        Tight,
    }

    /// <summary>
    /// The row for <paramref name="chips"/> in <paramref name="cells"/> cells, page <paramref name="page"/> when they need
    /// pages (clamped). Null — no row — with no chips.
    /// </summary>
    public static HomeRow? Render(IReadOnlyList<HomeChip> chips, HaLiveStatus status, int cells, int page = 0)
    {
        ArgumentNullException.ThrowIfNull(chips);
        if (chips.Count == 0)
        {
            return null;
        }

        int room = Math.Max(0, cells - StatusCells);
        foreach (var fit in new[] { Fit.Full, Fit.Short, Fit.Tight })
        {
            var texts = chips.Select(c => ChipText(c, fit)).ToList();
            if (texts.Sum(t => TextCells.Width(t)) <= room)
            {
                return Build(chips, texts, status, 0, chips.Count, pages: 1, page: 0);
            }
        }

        // Pages: the tight chips, as many as fit between the arrows on each; one wider than a page is cut to it (the review's
        // finding, 2026-10-12: a playing title on a narrow window wrapped the row and threw the pane's rows off by one). A
        // window with no room for a page between the arrows shows the status glyph alone.
        int pageRoom = room - ArrowCells * 2;
        if (pageRoom < MinPageCells)
        {
            return Build(chips, [], status, 0, 0, pages: 1, page: 0);
        }

        var tight = chips.Select(c => ChipText(c, Fit.Tight)).Select(t => TextCells.Width(t) > pageRoom ? HomeChips.Clip(t, pageRoom) : t).ToList();
        var starts = Pages(tight, pageRoom);
        int shown = Math.Clamp(page, 0, starts.Count - 1);
        int end = shown + 1 < starts.Count ? starts[shown + 1] : chips.Count;
        return Build(chips, tight, status, starts[shown], end, starts.Count, shown);
    }

    /// <summary>The status glyph and the blank after it.</summary>
    private const int StatusCells = 2;

    /// <summary>An arrow and the blank beside it.</summary>
    private const int ArrowCells = 2;

    /// <summary>The narrowest page worth drawing: a glyph chip's blank, glyph and blank, and a cell to spare.</summary>
    private const int MinPageCells = 5;

    /// <summary>A chip's text: <c> glyph name value </c>, the name per <paramref name="fit"/>, the mark after. Pure.</summary>
    public static string ChipText(HomeChip chip, Fit fit)
    {
        ArgumentNullException.ThrowIfNull(chip);
        var text = new StringBuilder(" ").Append(chip.Glyph);
        string name = fit switch
        {
            Fit.Full => chip.Label ?? chip.Name,
            Fit.Short => chip.Short,
            _ => "",
        };
        if (name.Length > 0)
        {
            text.Append(' ').Append(name);
        }

        string value = chip.Value.Trim();
        if (value.Length > 0)
        {
            text.Append(' ').Append(value);
        }

        string mark = chip.Mark switch
        {
            HomeChipMark.Pending => HomeBarText.PendingMark,
            HomeChipMark.Failed => HomeBarText.FailedMark,
            _ => "",
        };
        return text.Append(mark.Length > 0 ? mark : " ").ToString();
    }

    /// <summary>Where each page starts, for chips of <paramref name="texts"/>' widths in <paramref name="room"/> cells (a chip wider than a page is a page alone). Pure.</summary>
    public static List<int> Pages(IReadOnlyList<string> texts, int room)
    {
        var starts = new List<int> { 0 };
        int used = 0;
        for (int i = 0; i < texts.Count; i++)
        {
            int width = TextCells.Width(texts[i]);
            if (used > 0 && used + width > room)
            {
                starts.Add(i);
                used = TextCells.Width(texts[i]);
                continue;
            }

            used += width;
        }

        return starts;
    }

    /// <summary>The colour of the status glyph: the accent while live, dim while connecting, warn while retrying, bad when refused or not set up.</summary>
    public static Style StatusStyle(HaLiveStatus status) => status switch
    {
        HaLiveStatus.Live => Theme.Accent,
        HaLiveStatus.Connecting => Theme.DimText,
        HaLiveStatus.Retrying => new Style(Theme.Warn),
        _ => new Style(Theme.Bad),
    };

    /// <summary>The span under column <paramref name="x"/> (0-based in the row), or null for a blank. Pure.</summary>
    public static HomeSpan? HitAt(IReadOnlyList<HomeSpan> spans, int x)
    {
        ArgumentNullException.ThrowIfNull(spans);
        foreach (var span in spans)
        {
            if (x >= span.Start && x < span.Start + span.Cells)
            {
                return span;
            }
        }

        return null;
    }

    private static HomeRow Build(IReadOnlyList<HomeChip> chips, IReadOnlyList<string> texts, HaLiveStatus status, int from, int to, int pages, int page)
    {
        var segments = new List<PerfSegment>();
        var spans = new List<HomeSpan>();
        int column = 0;

        Add(HomeBarText.StatusGlyph, StatusStyle(status), new HomeSpan(0, 1, HomeTarget.Status));
        Add(" ", Style.Plain, null);
        if (pages > 1)
        {
            Add(HomeBarText.PrevGlyph, page > 0 ? Theme.Accent : Theme.DimText, page > 0 ? new HomeSpan(column, 1, HomeTarget.Prev) : null);
            Add(" ", Style.Plain, null);
        }

        for (int i = from; i < to; i++)
        {
            var chip = chips[i];
            int width = TextCells.Width(texts[i]);
            Add(texts[i], chip.Style, new HomeSpan(column, width, HomeTarget.Chip, i));
        }

        if (pages > 1)
        {
            Add(" ", Style.Plain, null);
            Add(HomeBarText.NextGlyph, page < pages - 1 ? Theme.Accent : Theme.DimText, page < pages - 1 ? new HomeSpan(column, 1, HomeTarget.Next) : null);
        }

        var key = new StringBuilder();
        foreach (var segment in segments)
        {
            key.Append(segment.Text).Append('\u0001').Append(segment.Style.ToMarkup()).Append('\u0002');
        }

        return new HomeRow(segments, spans, pages, key.ToString());

        void Add(string text, Style style, HomeSpan? span)
        {
            if (span is { } s)
            {
                spans.Add(s with { Start = column });
            }

            segments.Add(new PerfSegment(text, style));
            column += TextCells.Width(text);
        }
    }
}
