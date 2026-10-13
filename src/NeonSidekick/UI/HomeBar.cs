using System.Text;
using NeonSidekick.App;
using NeonSidekick.HomeAssistant;
using Spectre.Console;

namespace NeonSidekick.UI;

/// <summary>What a cell of the home bar is: a chip (<see cref="HomeSpan.Index"/> into the chips) or a page arrow (the status glyph went on 2026-10-12).</summary>
public enum HomeTarget
{
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
/// The home bar's row (2026-10-12, the user's ask: a bar of Home Assistant rooms and devices under the perf bar): the chips
/// (<see cref="HomeChips"/>) side by side, one blank between two — <c>💡 Desk 80% 💡 Hall</c> — since later still on 2026-10-13 (the
/// user's ask: glyph-only chips pinned left line up under the tool bar's glyphs, three cells apart from column 0; until then each chip
/// had a blank at either end, two between names, and a third, a gap between the chips, went on 2026-10-12 once the chips lost their
/// fills). A chip's mark (<see cref="HomeBarText.PendingMark"/>, <see cref="HomeBarText.FailedMark"/>) stands in the blank after it.
/// Centred on the row as the meters are (<see cref="PerfBar"/>), the odd cell at the right; left-aligned behind a ⌂ status glyph
/// until later on 2026-10-12, when the user had the glyph, its note and its double-click to the picker taken off (the chips
/// still go dim while the feed is down; Enter on <c>Home bar</c> and <c>/homebar edit</c> open the picker). Too narrow a window
/// shortens the names (<see cref="HomeChip.Short"/>), then drops them (the glyph and the value), then splits the chips into
/// pages behind <see cref="HomeBarText.PrevGlyph"/>/<see cref="HomeBarText.NextGlyph"/>. Since later on 2026-10-13 (the user's ask: the
/// home bar pinned as the tool bar and the hint bar are, with a third group) the chips are in three groups, left, center and right, the
/// center winning the room (<see cref="Render(IReadOnlyList{HomeChip}, IReadOnlyList{HomeChip}, IReadOnlyList{HomeChip}, int, int)"/>). Pure.
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
    /// pages (clamped). Null — no row — with no chips. Every chip in the center, as the whole bar was until later on 2026-10-13.
    /// </summary>
    public static HomeRow? Render(IReadOnlyList<HomeChip> chips, int cells, int page = 0) => Render([], chips, [], cells, page);

    /// <summary>
    /// The row for chips pinned <paramref name="left"/>, <paramref name="center"/> and <paramref name="right"/> (later on 2026-10-13, the
    /// user's ask) in <paramref name="cells"/> cells: the left group from the row's start, the right group ending on its last cell, the
    /// center centred on the row and pushed aside by them, <see cref="GroupGap"/> at least between two groups. Too narrow a row (the
    /// user's calls): the names shrink first, every chip's together (<see cref="Fit"/>); then the right group's chips go, from its inner
    /// end, then the left group's, from its; then the center pages behind the arrows alone (the left group, or the right, when there is
    /// no center). A chip that goes leaves nothing behind (no count). A span's <see cref="HomeSpan.Index"/> is into the three in that
    /// order. Null — no row — with no chips. Pure.
    /// </summary>
    public static HomeRow? Render(IReadOnlyList<HomeChip> left, IReadOnlyList<HomeChip> center, IReadOnlyList<HomeChip> right, int cells, int page = 0)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(center);
        ArgumentNullException.ThrowIfNull(right);
        var chips = new List<HomeChip>(left.Count + center.Count + right.Count);
        chips.AddRange(left);
        chips.AddRange(center);
        chips.AddRange(right);
        if (chips.Count == 0)
        {
            return null;
        }

        int room = Math.Max(0, cells);
        var whole = new[] { Trim(chips, 0, left.Count), Trim(chips, left.Count, center.Count), Trim(chips, left.Count + center.Count, right.Count) };
        foreach (var fit in new[] { Fit.Full, Fit.Short, Fit.Tight })
        {
            var texts = chips.Select(c => ChipText(c, fit)).ToList();
            if (Width(chips, texts, whole) <= room)
            {
                return Lay(chips, texts, whole, room);
            }
        }

        // Room for the center (later on 2026-10-13): the tight chips, the right group giving way from its inner end, then the left.
        var tight = chips.Select(c => ChipText(c, Fit.Tight)).ToList();
        var groups = whole.Select(g => g.ToList()).ToArray();
        foreach (int side in (int[])[2, 0])
        {
            while (groups[side].Count > 0 && Width(chips, tight, groups) > room)
            {
                groups[side].RemoveAt(side == 2 ? 0 : groups[side].Count - 1);
                groups[side] = Trim(chips, groups[side]);
            }
        }

        if (groups.Any(g => g.Count > 0) && Width(chips, tight, groups) <= room)
        {
            return Lay(chips, tight, groups, room);
        }

        // Pages: the tight chips, as many as fit between the arrows on each; one wider than a page is cut to it (the review's
        // finding, 2026-10-12: a playing title on a narrow window wrapped the row and threw the pane's rows off by one). A
        // window with no room for a page between the arrows gets an empty row (the status glyph alone until the glyph went).
        // Since the groups (later on 2026-10-13) the center alone pages; the left group, or else the right, with no center.
        int pageRoom = room - ArrowCells * 2;
        var pager = new[] { whole[1], whole[0], whole[2] }.FirstOrDefault(g => g.Count > 0);
        if (pageRoom < MinPageCells || pager is null)
        {
            return Build(chips, [], room, 0, 0, pages: 1, page: 0);
        }

        int from = pager[0];
        int to = pager[^1] + 1;
        var cut = tight.Select(t => TextCells.Width(t) > pageRoom ? HomeChips.Clip(t, pageRoom) : t).ToList();
        var starts = Pages(cut.GetRange(from, to - from), pageRoom, chips.Skip(from).Take(to - from).Select(Gap).ToList()).Select(s => s + from).ToList();
        int shown = Math.Clamp(page, 0, starts.Count - 1);
        int end = shown + 1 < starts.Count ? starts[shown + 1] : to;
        return Build(chips, cut, room, starts[shown], end, starts.Count, shown);
    }

    /// <summary>The least blank between two groups (later on 2026-10-13; 4 since the chips lost their edge blanks later still that day, the least the groups were apart until then).</summary>
    public const int GroupGap = 4;

    // The indices [from, from + count) of a group, without a divider at either end or beside another.
    private static List<int> Trim(IReadOnlyList<HomeChip> chips, int from, int count) => Trim(chips, Enumerable.Range(from, count).ToList());

    private static List<int> Trim(IReadOnlyList<HomeChip> chips, List<int> group)
    {
        var kept = new List<int>(group.Count);
        foreach (int i in group)
        {
            if (chips[i].Divider && (kept.Count == 0 || chips[kept[^1]].Divider))
            {
                continue;
            }

            kept.Add(i);
        }

        while (kept.Count > 0 && chips[kept[^1]].Divider)
        {
            kept.RemoveAt(kept.Count - 1);
        }

        return kept;
    }

    // The groups' cells together, GroupGap between two that draw.
    private static int Width(IReadOnlyList<HomeChip> chips, IReadOnlyList<string> texts, IReadOnlyList<List<int>> groups)
    {
        var drawn = groups.Where(g => g.Count > 0).ToList();
        return drawn.Sum(g => RunCells(chips, texts, g)) + GroupGap * Math.Max(0, drawn.Count - 1);
    }

    // A run of chips' cells (later still on 2026-10-13): their texts and the blank between two, none after a chip whose mark stands in it.
    private static int RunCells(IReadOnlyList<HomeChip> chips, IReadOnlyList<string> texts, IEnumerable<int> run)
    {
        int cells = 0;
        int owed = 0;
        foreach (int i in run)
        {
            cells += owed + TextCells.Width(texts[i]);
            owed = Gap(chips[i]);
        }

        return cells;
    }

    // The blank after a chip ahead of the next one: none when its mark stands there.
    private static int Gap(HomeChip chip) => !chip.Divider && chip.Mark is HomeChipMark.Pending or HomeChipMark.Failed ? 0 : 1;

    // The three groups on one row of cells (later on 2026-10-13): the left from column 0, the right ending on the last cell, the center
    // centred between them, the odd cell at its right; the center alone is exactly the row Build draws for one page.
    private static HomeRow Lay(IReadOnlyList<HomeChip> chips, IReadOnlyList<string> texts, IReadOnlyList<List<int>> groups, int cells)
    {
        int Cells(List<int> group) => RunCells(chips, texts, group);
        var (left, center, right) = (groups[0], groups[1], groups[2]);
        int rightStart = cells - Cells(right);
        int centerStart = 0;
        if (center.Count > 0)
        {
            int min = left.Count > 0 ? Cells(left) + GroupGap : 0;
            int max = (right.Count > 0 ? rightStart - GroupGap : cells) - Cells(center);
            centerStart = Math.Clamp((cells - Cells(center)) / 2, min, Math.Max(min, max));
        }

        var row = new Writer();
        foreach (var (group, start) in new[] { (left, 0), (center, centerStart), (right, rightStart) })
        {
            if (group.Count == 0)
            {
                continue;
            }

            row.PadTo(start);
            foreach (int i in group)
            {
                row.Chip(chips[i], texts[i], i);
            }
        }

        return row.Done(pages: 1);
    }

    /// <summary>An arrow and the blank beside it.</summary>
    private const int ArrowCells = 2;

    /// <summary>The narrowest page worth drawing: a glyph chip's glyph and a cell to spare.</summary>
    private const int MinPageCells = 3;

    /// <summary>
    /// <paramref name="chips"/> without the dividers that would show nothing between (2026-10-13, the user's ask: a divider stands only
    /// between two chips): none at either end, none beside another. Pure.
    /// </summary>
    public static List<HomeChip> ShownDividers(IReadOnlyList<HomeChip> chips)
    {
        ArgumentNullException.ThrowIfNull(chips);
        var kept = new List<HomeChip>(chips.Count);
        foreach (var chip in chips)
        {
            if (chip.Divider && (kept.Count == 0 || kept[^1].Divider))
            {
                continue;
            }

            kept.Add(chip);
        }

        while (kept.Count > 0 && kept[^1].Divider)
        {
            kept.RemoveAt(kept.Count - 1);
        }

        return kept;
    }

    /// <summary>A chip's text: <c>glyph name value</c>, the name per <paramref name="fit"/>, the mark after; a bare chip's glyph alone (<see cref="HomeChip.Bare"/>); a divider's <c>│</c> (2026-10-13). No blank at either end since later still on 2026-10-13: the row puts one between two chips. Pure.</summary>
    public static string ChipText(HomeChip chip, Fit fit)
    {
        ArgumentNullException.ThrowIfNull(chip);
        if (chip.Divider)
        {
            return chip.Glyph;
        }

        var text = new StringBuilder(chip.Glyph);
        if (chip.Bare)
        {
            fit = Fit.Tight;
            chip = chip with { Value = "" };
        }

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
        return text.Append(mark).ToString();
    }

    /// <summary>
    /// Where each page starts, for chips of <paramref name="texts"/>' widths in <paramref name="room"/> cells, a blank between two on a
    /// page (later still on 2026-10-13), or <paramref name="gaps"/>' cells after each (none after a mark); a chip wider than a page is a page alone. Pure.
    /// </summary>
    public static List<int> Pages(IReadOnlyList<string> texts, int room, IReadOnlyList<int>? gaps = null)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var starts = new List<int> { 0 };
        int used = 0;
        int owed = 0;
        for (int i = 0; i < texts.Count; i++)
        {
            int width = TextCells.Width(texts[i]);
            if (used > 0 && used + owed + width > room)
            {
                starts.Add(i);
                used = width;
            }
            else
            {
                used += owed + width;
            }

            owed = gaps is null ? 1 : gaps[i];
        }

        return starts;
    }

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

    // The row of chips [from, to) in cells, centred (2026-10-12): a blank run ahead of it of half the room left, the odd cell at
    // the right where the pane's erase blanks the rest, so the spans start where the chips are drawn. An empty row stays empty.
    private static HomeRow Build(IReadOnlyList<HomeChip> chips, IReadOnlyList<string> texts, int cells, int from, int to, int pages, int page)
    {
        // A divider at a page's edge shows nothing between (2026-10-13): left off the page.
        while (from < to && chips[from].Divider)
        {
            from++;
        }

        while (to > from && chips[to - 1].Divider)
        {
            to--;
        }

        int drawn = (pages > 1 ? ArrowCells * 2 : 0) + RunCells(chips, texts, Enumerable.Range(from, Math.Max(0, to - from)));

        var row = new Writer();
        if (to > from)
        {
            row.PadTo((cells - drawn) / 2);
        }

        if (pages > 1)
        {
            row.Add(HomeBarText.PrevGlyph, page > 0 ? Theme.Accent : Theme.DimText, page > 0 ? new HomeSpan(0, 1, HomeTarget.Prev) : null);
            row.Add(" ", Style.Plain, null);
        }

        for (int i = from; i < to; i++)
        {
            row.Chip(chips[i], texts[i], i);
        }

        if (pages > 1)
        {
            row.Add(" ", Style.Plain, null);
            row.Add(HomeBarText.NextGlyph, page < pages - 1 ? Theme.Accent : Theme.DimText, page < pages - 1 ? new HomeSpan(0, 1, HomeTarget.Next) : null);
        }

        return row.Done(pages);
    }

    // The row as it is written: the styled runs, the spans where they start, and the column the next run starts at; the blank the
    // last chip owes the next one of its run (later still on 2026-10-13), dropped by anything else written.
    private sealed class Writer
    {
        private readonly List<PerfSegment> _segments = [];
        private readonly List<HomeSpan> _spans = [];
        private int _column;
        private int _owed;

        // Blanks up to column (none when the row is there already); a new run starts.
        public void PadTo(int column)
        {
            if (column > _column)
            {
                Add(new string(' ', column - _column), Style.Plain, null);
            }

            _owed = 0;
        }

        // A chip's text in its style, after the blank between it and the chip before; a divider is a blank to a click (2026-10-13),
        // and so is the blank between two chips.
        public void Chip(HomeChip chip, string text, int index)
        {
            if (_owed > 0)
            {
                Write(new string(' ', _owed), Style.Plain, null);
            }

            Write(text, chip.Style, chip.Divider ? null : new HomeSpan(0, TextCells.Width(text), HomeTarget.Chip, index));
            _owed = Gap(chip);
        }

        public void Add(string text, Style style, HomeSpan? span)
        {
            _owed = 0;
            Write(text, style, span);
        }

        private void Write(string text, Style style, HomeSpan? span)
        {
            if (span is { } s)
            {
                _spans.Add(s with { Start = _column });
            }

            _segments.Add(new PerfSegment(text, style));
            _column += TextCells.Width(text);
        }

        public HomeRow Done(int pages)
        {
            var key = new StringBuilder();
            foreach (var segment in _segments)
            {
                key.Append(segment.Text).Append('\u0001').Append(segment.Style.ToMarkup()).Append('\u0002');
            }

            return new HomeRow(_segments, _spans, pages, key.ToString());
        }
    }
}
