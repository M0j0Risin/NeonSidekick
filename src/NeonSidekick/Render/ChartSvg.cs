using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NeonSidekick.Files;
using NeonSidekick.Office;

namespace NeonSidekick.Render;

/// <summary>A chart's kind.</summary>
public enum ChartKind
{
    Bar,
    Line,
    Pie,
}

/// <summary>What to draw and how (2026-11-16, plan sharded-hugging-puppy #9): the kind, its titles, a bar's direction and stacking, the ground, the width in CSS pixels.</summary>
public sealed record ChartSpec(ChartKind Kind, string? Title = null, string? XLabel = null, string? YLabel = null, bool Horizontal = false, bool Stacked = false, bool Dark = false, int Width = 1200);

/// <summary>One series: its name and a value per label (NaN where the cell was blank: a gap).</summary>
public sealed record ChartSeries(string Name, IReadOnlyList<double> Values);

/// <summary>
/// The labels along the category axis and the series over them, row for row. <see cref="Times"/> holds each label's moment when every x cell
/// is a date or a date-time (2026-11-17, the admin-themes-render review's #197): a line chart places its points by time.
/// </summary>
public sealed record ChartData(IReadOnlyList<string> Labels, IReadOnlyList<ChartSeries> Series, IReadOnlyList<DateTime>? Times = null);

/// <summary>
/// The rows of a table as <c>render_chart</c> reads them (2026-11-16): an <see cref="ISheetRows"/> that keeps the header and the typed cells
/// (<see cref="TableSource"/>'s column judging), stopping at <see cref="ChartSvg.MaxPoints"/> rows with <see cref="Cut"/> set.
/// </summary>
public sealed partial class ChartTable : ISheetRows
{
    private readonly int _max;

    public ChartTable(int max = ChartSvg.MaxPoints)
    {
        _max = max;
    }

    public IReadOnlyList<string> Columns { get; private set; } = [];

    public List<IReadOnlyList<XlsxValue>> Rows { get; } = [];

    /// <summary>Whether rows past the cap were left out.</summary>
    public bool Cut { get; private set; }

    public string? Begin(IReadOnlyList<string> columns)
    {
        Columns = [.. columns];
        return columns.Count == 0 ? RenderText.NoRows : null;
    }

    public bool Add(IReadOnlyList<XlsxValue> row)
    {
        if (Rows.Count >= _max)
        {
            Cut = true;
            return false;
        }

        Rows.Add([.. row]);
        return true;
    }

    /// <summary>
    /// The chart's data: <paramref name="x"/>'s cells as the labels, each of <paramref name="y"/> a series of numbers (any case names a column;
    /// a blank cell is a gap). Null with <paramref name="error"/> naming an unknown column, one the header names twice (2026-11-17, #220: only
    /// a column the call names is judged, so a sheet with an unused repeated header still charts), a column <paramref name="y"/> names twice,
    /// or the first cell that is no number, by its data row (#136).
    /// </summary>
    public ChartData? Data(string x, IReadOnlyList<string> y, out string? error)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        error = null;
        if (Rows.Count == 0)
        {
            error = RenderText.NoRows;
            return null;
        }

        int xi = Index(x, out int matches);
        if (xi < 0)
        {
            error = xi == -2 ? RenderText.AmbiguousColumn(x, matches) : RenderText.NoColumn(x, Columns);
            return null;
        }

        for (int i = 1; i < y.Count; i++)
        {
            for (int j = 0; j < i; j++)
            {
                if (SameName(y[i], y[j]))
                {
                    error = RenderText.RepeatedY(y[i]);
                    return null;
                }
            }
        }

        // Seconds only when some cell has them (#172), so a minute-level axis isn't lengthened.
        bool seconds = Rows.Any(r => xi < r.Count && r[xi] is { Kind: XlsxKind.DateTime } cell && cell.When.Ticks % TimeSpan.TicksPerMinute != 0);
        var labels = Rows.Select(r => xi < r.Count ? Label(r[xi], seconds) : "").ToList();
        var times = Rows.TrueForAll(r => xi < r.Count && r[xi].Kind is XlsxKind.Date or XlsxKind.DateTime) ? Rows.Select(r => r[xi].When).ToList() : null;
        var series = new List<ChartSeries>();
        foreach (string name in y)
        {
            int yi = Index(name, out matches);
            if (yi < 0)
            {
                error = yi == -2 ? RenderText.AmbiguousColumn(name, matches) : RenderText.NoColumn(name, Columns);
                return null;
            }

            var values = new List<double>(Rows.Count);
            for (int r = 0; r < Rows.Count; r++)
            {
                var cell = yi < Rows[r].Count ? Rows[r][yi] : XlsxValue.Blank;
                if (Number(cell) is not { } value)
                {
                    error = RenderText.NotANumber(Columns[yi], r + 1, Label(cell));
                    return null;
                }

                values.Add(value);
            }

            series.Add(new ChartSeries(Columns[yi], values));
        }

        return new ChartData(labels, series, times);
    }

    // The column a name means (trimmed, any case): its index, -1 for none, -2 when the header names it more than once (#220). A walk over
    // Columns with no copy (#180).
    private int Index(string name, out int matches)
    {
        matches = 0;
        int found = -1;
        for (int i = 0; i < Columns.Count; i++)
        {
            if (SameName(Columns[i], name))
            {
                found = found < 0 ? i : found;
                matches++;
            }
        }

        return matches > 1 ? -2 : found;
    }

    private static bool SameName(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A cell as a label: a number in its shortest invariant form, a date as ISO (a date-time with its seconds when <paramref name="seconds"/>),
    /// a time of day and a duration as a table's file writes them (2026-11-17, #155), text as it is.
    /// </summary>
    public static string Label(XlsxValue cell, bool seconds = false) => cell.Kind switch
    {
        XlsxKind.Blank => "",
        XlsxKind.Number => cell.Exact ?? cell.Number.ToString("G15", CultureInfo.InvariantCulture),
        XlsxKind.Bool => cell.Number != 0 ? "true" : "false",
        XlsxKind.Date => cell.When.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        XlsxKind.DateTime => cell.When.ToString(seconds ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        XlsxKind.Time => TextRows.TimeText(cell.Number),
        XlsxKind.Duration => TextRows.DurationText(cell.Number),
        _ => cell.Text ?? "",
    };

    /// <summary>A cell as a number: NaN for a blank (a gap), null for anything that reads as none.</summary>
    private static double? Number(XlsxValue cell) => cell.Kind switch
    {
        XlsxKind.Blank => double.NaN,
        XlsxKind.Number => cell.Number,
        XlsxKind.Text => NumberOf(cell.Text ?? ""),
        _ => null,
    };

    /// <summary>
    /// A text cell as a number (2026-11-17, #107): NaN for a blank, else the invariant number after one leading currency symbol and one trailing
    /// <c>%</c> are taken off (12% is 12, $5 is 5, -$5 is -5); commas only as thousands groups (1,234 is 1234; 1,2 is not a number, where
    /// <see cref="NumberStyles.AllowThousands"/> would read 12). Null for anything else, or a number past a double.
    /// </summary>
    public static double? NumberOf(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string s = text.Trim();
        if (s.Length == 0)
        {
            return double.NaN;
        }

        if (s.EndsWith('%'))
        {
            s = s[..^1].TrimEnd();
        }

        if (s.Length > 1 && s[0] is '+' or '-' && IsCurrency(s[1]))
        {
            s = s[0] + s[2..].TrimStart();
        }
        else if (s.Length > 0 && IsCurrency(s[0]))
        {
            s = s[1..].TrimStart();
        }

        if (s.Contains(',', StringComparison.Ordinal))
        {
            int end = s.IndexOfAny(['.', 'e', 'E']);
            if (!Grouped().IsMatch(end < 0 ? s : s[..end]))
            {
                return null;
            }

            s = s.Replace(",", "", StringComparison.Ordinal);
        }

        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && double.IsFinite(parsed) ? parsed : null;
    }

    private static bool IsCurrency(char c) => CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.CurrencySymbol;

    [GeneratedRegex(@"^[+-]?\d{1,3}(,\d{3})+$", RegexOptions.CultureInvariant)]
    private static partial Regex Grouped();
}

/// <summary>
/// A bar, line or pie chart as SVG (2026-11-16, plan sharded-hugging-puppy #9, the user's call: the app draws charts itself, the browser only
/// turns them into pixels): nice tick steps, a legend for two or more series, long labels cut with an ellipsis and slanted on a crowded axis, a
/// pie's smallest slices folded into Other past what its legend holds, a line chart of dates placed by time, and a categorical palette held to
/// contrast against its ground. A chart that would come out empty or unreadable is refused with what to change (2026-11-17, the review's #24,
/// #48, #67, #83, #185, #218). Every number written is invariant and every text escaped: a table's cells are untrusted and never become
/// markup. Pure.
/// </summary>
public static class ChartSvg
{
    public const int MaxPoints = 1000;
    public const int MaxSeries = 12;
    public const int MaxSlices = 12;

    /// <summary>The smallest plot area drawn, in CSS pixels; less is refused (#83).</summary>
    public const int MinPlotWidth = 80;

    public const int MinPlotHeight = 60;

    /// <summary>The most ticks a time axis has.</summary>
    public const int MaxTimeTicks = 8;

    /// <summary>The chart's height for its width: a 5 by 3 frame.</summary>
    public static int HeightFor(int width) => (int)Math.Round(width * 0.6);

    /// <summary>The series colors on a white ground, each at least 3:1 against it.</summary>
    public static readonly IReadOnlyList<string> LightPalette =
        ["#2563EB", "#DC2626", "#047857", "#B45309", "#7C3AED", "#DB2777", "#0E7490", "#4D7C0F", "#C2410C", "#4338CA", "#0F766E", "#9333EA"];

    /// <summary>The series colors on <see cref="PictureColors.Dark"/>'s ground, each at least 3:1 against it.</summary>
    public static readonly IReadOnlyList<string> DarkPalette =
        ["#60A5FA", "#F87171", "#34D399", "#FBBF24", "#A78BFA", "#F472B6", "#22D3EE", "#A3E635", "#FB923C", "#818CF8", "#2DD4BF", "#C084FC"];

    private const string Font = "'Segoe UI', system-ui, -apple-system, 'Helvetica Neue', Arial, sans-serif";
    private const double CharWidth = 7.2;   // a 13px label's average advance

    // The steps a time axis tries, smallest first, then months, then years (1, 2, 5 × a power of ten).
    private static readonly TimeSpan[] ClockSteps =
    [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(1), TimeSpan.FromHours(3), TimeSpan.FromHours(6), TimeSpan.FromHours(12),
        TimeSpan.FromDays(1), TimeSpan.FromDays(2), TimeSpan.FromDays(7),
    ];

    private static readonly int[] MonthSteps = [1, 3, 6];

    /// <summary>
    /// The SVG of <paramref name="data"/> drawn as <paramref name="spec"/> says, or null with <paramref name="refused"/> saying what to change.
    /// <paramref name="folded"/> counts a pie's slices put in Other, <paramref name="dropped"/> those left out at zero or blank (#219).
    /// </summary>
    public static string? Svg(ChartSpec spec, ChartData data, out int folded, out int dropped, out string? refused)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(data);
        folded = 0;
        dropped = 0;
        refused = null;

        // The data's cases, before any layout.
        if (data.Series.Count == 0 || data.Series.All(s => s.Values.All(double.IsNaN)))
        {
            refused = RenderText.NothingToDraw;
            return null;
        }

        (double Min, double Max, double Step) scale = default;
        if (spec.Kind == ChartKind.Pie)
        {
            double total = data.Series[0].Values.Where(v => v > 0).Sum();
            if (double.IsInfinity(total))
            {
                refused = RenderText.NumbersTooLarge;
                return null;
            }

            if (!(total > 0))
            {
                refused = RenderText.PieNothingAbove0;
                return null;
            }
        }
        else
        {
            data = spec.Kind == ChartKind.Line && data.Times is { } times && times.Count == data.Labels.Count && times.Count > 0 && times.Min() < times.Max()
                ? ByTime(data)
                : data with { Times = null };
            var (lowest, highest) = Range(spec, data);
            scale = NiceScale(lowest, highest);
            if (!double.IsFinite(highest - lowest) || !double.IsFinite(scale.Max - scale.Min) || !double.IsFinite(scale.Step) || !(scale.Step > 0))
            {
                refused = RenderText.NumbersTooLarge;
                return null;
            }
        }

        int width = spec.Width, height = HeightFor(width);
        var palette = spec.Dark ? DarkPalette : LightPalette;
        var colors = PictureColors.For(spec.Dark);
        var svg = new StringBuilder();
        svg.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"").Append(N(width)).Append("\" height=\"").Append(N(height))
            .Append("\" viewBox=\"0 0 ").Append(N(width)).Append(' ').Append(N(height)).Append("\" font-family=\"").Append(Font).Append("\">");
        svg.Append("<rect width=\"100%\" height=\"100%\" fill=\"").Append(colors.Ground).Append("\"/>");
        double top = 16;
        if (!string.IsNullOrWhiteSpace(spec.Title))
        {
            Text(svg, width / 2.0, top + 20, Cut(spec.Title, (width - 40) / 10.0), colors.Ink, 20, "middle", bold: true);
            top += 36;
        }

        if (spec.Kind == ChartKind.Pie)
        {
            Pie(svg, data, palette, colors, width, height, top, out folded, out dropped);
            return svg.Append("</svg>").ToString();
        }

        var names = data.Series.Select(s => s.Name).ToList();
        if (names.Count > 1)
        {
            top = Legend(svg, names, palette, colors.Ink, width, top);
        }

        if (Axes(svg, spec, data, scale, palette, colors, width, height, top) is { } tooSmall)
        {
            refused = tooSmall;
            return null;
        }

        return svg.Append("</svg>").ToString();
    }

    /// <summary>
    /// Round ticks over <paramref name="min"/>..<paramref name="max"/>: a step of 1, 2, 2.5 or 5 times a power of ten giving about
    /// <paramref name="ticks"/> intervals, and the range widened to whole steps. A flat range opens around its value.
    /// </summary>
    public static (double Min, double Max, double Step) NiceScale(double min, double max, int ticks = 5)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max))
        {
            return (0, 1, 0.2);
        }

        if (max < min)
        {
            (min, max) = (max, min);
        }

        if (max == min)
        {
            double pad = min == 0 ? 1 : Math.Abs(min) * 0.1;
            min -= min >= 0 && min - pad < 0 ? min : pad;
            max += pad;
        }

        double raw = (max - min) / Math.Max(1, ticks);
        double power = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double step = new[] { 1, 2, 2.5, 5, 10 }.Select(m => m * power).First(s => s >= raw);
        double lo = Math.Floor(min / step) * step, hi = Math.Ceiling(max / step) * step;
        return (lo, hi, step);
    }

    /// <summary>
    /// A tick's label: as many decimals as the step needs (up to 15, judged relative to the step, 2026-11-17, #68: ticks 1e-7 apart all read
    /// "1"), invariant, no grouping; past 12 characters, E notation with the significant digits the step needs.
    /// </summary>
    public static string Tick(double value, double step)
    {
        // The fewest decimals that write the step exactly (2.5 takes one, 0.25 two, 20 none, 1e-7 seven).
        int decimals = 0;
        while (decimals < 15)
        {
            double scaled = step * Math.Pow(10, decimals);
            if (Math.Abs(scaled - Math.Round(scaled)) <= 1e-6 * Math.Abs(scaled))
            {
                break;
            }

            decimals++;
        }

        if (Math.Abs(value) < step / 1e6)
        {
            value = 0;
        }

        string text = value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        if (text.Length <= 12 && Math.Abs(value) < 1e12)
        {
            return text;
        }

        int digits = value == 0 ? 1 : Math.Clamp((int)Math.Floor(Math.Log10(Math.Abs(value))) - (int)Math.Floor(Math.Log10(Math.Abs(step))) + 1, 1, 15);
        return value.ToString(digits == 1 ? "0E+0" : "0." + new string('#', digits - 1) + "E+0", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A pie slice's share as its legend writes it (2026-11-17, #221): rounded first, then whole percents from 10 up, one decimal below;
    /// ">99.9%" for a slice that would read 100 beside others, "&lt;0.1%" for one that would read 0.
    /// </summary>
    public static string Percent(double share, bool alone)
    {
        double percent = share * 100;
        double tenth = Math.Round(percent, 1);
        if (!alone && tenth >= 100)
        {
            return ">99.9%";
        }

        if (tenth <= 0)
        {
            return "<0.1%";
        }

        if (tenth >= 10)
        {
            double whole = Math.Round(percent);
            return (whole >= 100 && !alone ? tenth.ToString("F1", CultureInfo.InvariantCulture) : whole.ToString("F0", CultureInfo.InvariantCulture)) + "%";
        }

        return tenth.ToString("F1", CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>
    /// A time axis's ticks over <paramref name="first"/>..<paramref name="last"/> (2026-11-17, #197): the smallest step of seconds, minutes,
    /// hours, days, months or years giving at most <see cref="MaxTimeTicks"/>, from the first whole step at or after <paramref name="first"/>,
    /// each labeled for the step (the date written in full on the first tick and wherever it changes).
    /// </summary>
    public static IReadOnlyList<(DateTime At, string Label)> TimeTicks(DateTime first, DateTime last)
    {
        long span = Math.Max(0, (last - first).Ticks);
        foreach (var step in ClockSteps)
        {
            if (span / step.Ticks < MaxTimeTicks)
            {
                return ClockTicks(first, last, step);
            }
        }

        double months = span / (double)TimeSpan.TicksPerDay / 30.436875;
        foreach (int m in MonthSteps)
        {
            if (months / m < MaxTimeTicks)
            {
                return CalendarTicks(first, last, m);
            }
        }

        int years = 1;
        for (int power = 1; months / 12 / years >= MaxTimeTicks && years < 10_000; power *= 10)
        {
            foreach (int m in new[] { 1, 2, 5 })
            {
                years = m * power;
                if (months / 12 / years < MaxTimeTicks)
                {
                    break;
                }
            }
        }

        return CalendarTicks(first, last, 12 * years);
    }

    /// <summary><paramref name="text"/> cut to <paramref name="chars"/> characters with an ellipsis, never between a surrogate pair (#51).</summary>
    public static string Cut(string text, double chars)
    {
        ArgumentNullException.ThrowIfNull(text);
        int max = Math.Max(2, (int)chars);
        string flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length <= max ? flat : TextCut.Safe(flat, max - 1).TrimEnd() + "…";
    }

    /// <summary>
    /// Text made safe in XML: what XML can't hold dropped first (a lone surrogate as U+FFFD, 2026-11-17, #204), the five escaped, and a tab or
    /// line break a space.
    /// </summary>
    public static string Escape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string clean = XlsxValue.XmlSafe(text);
        var safe = new StringBuilder(clean.Length);
        foreach (char c in clean)
        {
            safe.Append(c switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                '\'' => "&#39;",
                _ when char.IsControl(c) => " ",
                _ => c.ToString(),
            });
        }

        return safe.ToString();
    }

    // The points in time order, each series' values and the labels moved alongside (a line chart of dates, #197).
    private static ChartData ByTime(ChartData data)
    {
        var times = data.Times!;
        int[] order = [.. Enumerable.Range(0, times.Count).OrderBy(i => times[i]).ThenBy(i => i)];
        return new ChartData(
            [.. order.Select(i => data.Labels[i])],
            [.. data.Series.Select(s => new ChartSeries(s.Name, [.. order.Select(i => s.Values[i])]))],
            [.. order.Select(i => times[i])]);
    }

    // The value range: each value, or each label's stack of positives up and negatives down; zero always on the scale for bars.
    private static (double Lowest, double Highest) Range(ChartSpec spec, ChartData data)
    {
        bool stacked = spec.Kind == ChartKind.Bar && spec.Stacked && data.Series.Count > 1;
        double lowest = 0, highest = 0;
        bool any = false;
        for (int i = 0; i < data.Labels.Count; i++)
        {
            double up = 0, down = 0;
            foreach (var s in data.Series)
            {
                double v = s.Values[i];
                if (double.IsNaN(v))
                {
                    continue;
                }

                if (stacked)
                {
                    if (v >= 0)
                    {
                        up += v;
                    }
                    else
                    {
                        down += v;
                    }
                }
                else
                {
                    lowest = any ? Math.Min(lowest, v) : (spec.Kind == ChartKind.Bar ? Math.Min(0, v) : v);
                    highest = any ? Math.Max(highest, v) : (spec.Kind == ChartKind.Bar ? Math.Max(0, v) : v);
                }

                any = true;
            }

            if (stacked)
            {
                lowest = Math.Min(lowest, down);
                highest = Math.Max(highest, up);
            }
        }

        return (lowest, highest);
    }

    private static double Legend(StringBuilder svg, IReadOnlyList<string> names, IReadOnlyList<string> palette, string ink, int width, double top)
    {
        double x = 20, y = top + 14;
        for (int i = 0; i < names.Count; i++)
        {
            string label = Cut(names[i], 24);
            double itemWidth = 22 + (label.Length * CharWidth) + 18;
            if (x + itemWidth > width - 20 && x > 20)
            {
                x = 20;
                y += 22;
            }

            svg.Append("<rect x=\"").Append(F(x)).Append("\" y=\"").Append(F(y - 10)).Append("\" width=\"14\" height=\"14\" rx=\"3\" fill=\"").Append(palette[i % palette.Count]).Append("\"/>");
            Text(svg, x + 20, y + 2, label, ink, 13, "start");
            x += itemWidth;
        }

        return y + 16;
    }

    // The axes, grid and marks; the refusal when the plot or the bars come out too small to read (#83, #218), with nothing drawn that counts.
    private static string? Axes(StringBuilder svg, ChartSpec spec, ChartData data, (double Min, double Max, double Step) scale, IReadOnlyList<string> palette, PictureColors colors, int width, int height, double top)
    {
        int count = data.Labels.Count;
        bool horizontal = spec.Kind == ChartKind.Bar && spec.Horizontal;
        bool stacked = spec.Kind == ChartKind.Bar && spec.Stacked && data.Series.Count > 1;
        var times = spec.Kind == ChartKind.Line ? data.Times : null;
        var (min, max, step) = scale;
        string ink = colors.Ink, quiet = colors.Quiet, grid = colors.Grid;

        // Horizontal bars run the values along the bottom and the labels down the side, so the titles swap with them (#22).
        string? bottomTitle = horizontal ? spec.YLabel : spec.XLabel;
        string? leftTitle = horizontal ? spec.XLabel : spec.YLabel;

        string[] labels = data.Labels.Select(l => l.Length == 0 ? " " : l).ToArray();
        double longest = labels.Length == 0 ? 0 : labels.Max(l => Math.Min(l.Length, 24)) * CharWidth;
        string[] ticks = Enumerable.Range(0, (int)Math.Round((max - min) / step) + 1).Select(k => Tick(min + (k * step), step)).ToArray();
        double tickWidth = ticks.Max(t => t.Length) * CharWidth;

        double left = 20 + (leftTitle is { Length: > 0 } ? 24 : 0) + (horizontal ? Math.Min(longest, width * 0.3) : tickWidth) + 10;
        double right = width - 24;
        double bottomRoom = 36 + (bottomTitle is { Length: > 0 } ? 24 : 0);
        double plotWidth = right - left;
        bool slant = false;
        if (!horizontal && times is null && count > 0)
        {
            // Slanted only when the labels left after thinning would touch (#139); then the first one's slanted reach is room on the left (#35).
            double firstBand = plotWidth / count;
            int shown = Math.Max(1, (int)Math.Ceiling(count / Math.Max(1.0, plotWidth / 14)));
            slant = longest > (firstBand * shown) - 6;
            if (slant)
            {
                double reach = Math.Cos(40 * Math.PI / 180) * Math.Min(labels[0].Length, 24) * CharWidth;
                left = Math.Max(left, 8 + reach - (firstBand / 2));
                plotWidth = right - left;
                bottomRoom += Math.Min(longest, 160) * 0.65;
            }
        }

        double bottom = height - bottomRoom;
        double plotTop = top + 10;
        double plotHeight = bottom - plotTop;
        if (plotWidth < MinPlotWidth || plotHeight < MinPlotHeight)
        {
            return RenderText.PlotTooSmall;
        }

        double band = count == 0 ? 0 : (horizontal ? plotHeight : plotWidth) / count;
        double inner = band * 0.78;
        int perBand = stacked ? 1 : data.Series.Count;
        double thickness = inner / perBand;
        if (spec.Kind == ChartKind.Bar && thickness < 1)
        {
            return RenderText.BarsTooThin(count, perBand);
        }

        // The value axis: grid lines and tick labels.
        double ValueAt(double v) => horizontal
            ? left + ((v - min) / (max - min) * plotWidth)
            : bottom - ((v - min) / (max - min) * plotHeight);
        for (int k = 0; k < ticks.Length; k++)
        {
            double v = min + (k * step), at = ValueAt(v);
            if (horizontal)
            {
                svg.Append(Line(at, plotTop, at, bottom, grid, 1));
                Text(svg, at, bottom + 18, ticks[k], quiet, 12, "middle");
            }
            else
            {
                svg.Append(Line(left, at, right, at, grid, 1));
                Text(svg, left - 8, at + 4, ticks[k], quiet, 12, "end");
            }
        }

        double zero = ValueAt(Math.Clamp(0, min, max));
        svg.Append(horizontal ? Line(zero, plotTop, zero, bottom, quiet, 1.2) : Line(left, zero, right, zero, quiet, 1.2));

        // The category axis: by time on a line chart of dates (#197), else a band per label.
        DateTime earliest = times is null ? default : times[0];
        double span = times is null ? 1 : (times[^1] - times[0]).Ticks;
        double TimeAt(DateTime when) => left + ((when - earliest).Ticks / span * plotWidth);
        double CategoryAt(int i) => times is not null ? TimeAt(times[i]) : horizontal ? plotTop + (band * (i + 0.5)) : left + (band * (i + 0.5));
        if (times is not null)
        {
            foreach (var (at, label) in TimeTicks(times[0], times[^1]))
            {
                svg.Append(Line(TimeAt(at), bottom, TimeAt(at), bottom + 4, quiet, 1));
                Text(svg, TimeAt(at), bottom + 18, label, ink, 12, "middle");
            }
        }
        else
        {
            int every = Math.Max(1, (int)Math.Ceiling(count / Math.Max(1.0, horizontal ? plotHeight / 16 : plotWidth / 14)));
            for (int i = 0; i < count; i += every)
            {
                string label = Cut(labels[i], horizontal ? Math.Min(longest, width * 0.3) / CharWidth : 24);
                double at = CategoryAt(i);
                if (horizontal)
                {
                    Text(svg, left - 8, at + 4, label, ink, 12, "end");
                }
                else if (slant)
                {
                    svg.Append("<text x=\"").Append(F(at)).Append("\" y=\"").Append(F(bottom + 14)).Append("\" fill=\"").Append(ink).Append("\" font-size=\"12\" text-anchor=\"end\" transform=\"rotate(-40 ")
                        .Append(F(at)).Append(' ').Append(F(bottom + 14)).Append(")\">").Append(Escape(label)).Append("</text>");
                }
                else
                {
                    Text(svg, at, bottom + 18, label, ink, 12, "middle");
                }
            }
        }

        if (bottomTitle is { Length: > 0 })
        {
            Text(svg, (left + right) / 2, height - 12, Cut(bottomTitle, plotWidth / CharWidth), quiet, 13, "middle");
        }

        if (leftTitle is { Length: > 0 })
        {
            double cy = (plotTop + bottom) / 2;
            svg.Append("<text x=\"20\" y=\"").Append(F(cy)).Append("\" fill=\"").Append(quiet).Append("\" font-size=\"13\" text-anchor=\"middle\" transform=\"rotate(-90 20 ")
                .Append(F(cy)).Append(")\">").Append(Escape(Cut(leftTitle, plotHeight / CharWidth))).Append("</text>");
        }

        if (spec.Kind == ChartKind.Line)
        {
            for (int s = 0; s < data.Series.Count; s++)
            {
                string color = palette[s % palette.Count];
                var values = data.Series[s].Values;
                var path = new StringBuilder();
                bool pen = false;
                for (int i = 0; i < count; i++)
                {
                    double v = values[i];
                    if (double.IsNaN(v))
                    {
                        pen = false;
                        continue;
                    }

                    path.Append(pen ? " L" : " M").Append(F(CategoryAt(i))).Append(' ').Append(F(ValueAt(v)));
                    pen = true;
                }

                svg.Append("<path d=\"").Append(path.ToString().Trim()).Append("\" fill=\"none\" stroke=\"").Append(color).Append("\" stroke-width=\"2.5\" stroke-linejoin=\"round\" stroke-linecap=\"round\"/>");

                // A dot on every point up to 60, and past that on a point standing alone between gaps, which no line reaches (#49).
                for (int i = 0; i < count; i++)
                {
                    double v = values[i];
                    bool alone = (i == 0 || double.IsNaN(values[i - 1])) && (i == count - 1 || double.IsNaN(values[i + 1]));
                    if (!double.IsNaN(v) && (count <= 60 || alone))
                    {
                        svg.Append("<circle cx=\"").Append(F(CategoryAt(i))).Append("\" cy=\"").Append(F(ValueAt(v))).Append("\" r=\"").Append(count <= 60 ? "3.5" : "3").Append("\" fill=\"").Append(color).Append("\"/>");
                    }
                }
            }

            return null;
        }

        // Bars: side by side in a band, or stacked in one.
        for (int i = 0; i < count; i++)
        {
            double up = 0, down = 0;
            for (int s = 0; s < data.Series.Count; s++)
            {
                double v = data.Series[s].Values[i];
                if (double.IsNaN(v))
                {
                    continue;
                }

                double from, to;
                if (stacked)
                {
                    from = v >= 0 ? up : down;
                    to = from + v;
                    if (v >= 0)
                    {
                        up = to;
                    }
                    else
                    {
                        down = to;
                    }
                }
                else
                {
                    from = 0;
                    to = v;
                }

                double a = ValueAt(Math.Clamp(from, min, max)), b = ValueAt(Math.Clamp(to, min, max));
                double offset = CategoryAt(i) - (inner / 2) + (stacked ? 0 : s * thickness);
                string color = palette[s % palette.Count];
                double length = Math.Abs(b - a);
                svg.Append("<rect ");
                svg.Append(horizontal
                    ? $"x=\"{F(Math.Min(a, b))}\" y=\"{F(offset)}\" width=\"{F(length)}\" height=\"{F(Math.Max(1, thickness - 1))}\""
                    : $"x=\"{F(offset)}\" y=\"{F(Math.Min(a, b))}\" width=\"{F(Math.Max(1, thickness - 1))}\" height=\"{F(length)}\"");
                svg.Append(" fill=\"").Append(color).Append("\"/>");
            }
        }

        return null;
    }

    private static void Pie(StringBuilder svg, ChartData data, IReadOnlyList<string> palette, PictureColors colors, int width, int height, double top, out int folded, out int dropped)
    {
        var values = data.Series[0].Values;
        var slices = new List<(string Label, double Value)>();
        dropped = 0;
        for (int i = 0; i < values.Count; i++)
        {
            if (values[i] > 0)
            {
                slices.Add((data.Labels[i], values[i]));
            }
            else
            {
                dropped++;   // zero or blank: no slice to draw (#219)
            }
        }

        // The legend's rows fit the height (#23): at least 16 pixels a row, at most MaxSlices; more slices fold into Other.
        double room = height - top - 24;
        int rows = Math.Clamp((int)Math.Floor(room / 16), 1, MaxSlices);
        folded = 0;
        if (slices.Count > rows)
        {
            // The largest kept in their own order (by position, so two equal slices are never confused); the rest summed as Other, into a
            // kept slice already named Other when there is one (#171).
            var kept = Enumerable.Range(0, slices.Count).OrderByDescending(i => slices[i].Value).ThenBy(i => i).Take(rows - 1).ToHashSet();
            double rest = Enumerable.Range(0, slices.Count).Where(i => !kept.Contains(i)).Sum(i => slices[i].Value);
            folded = slices.Count - kept.Count;
            var shown = Enumerable.Range(0, slices.Count).Where(kept.Contains).Select(i => slices[i]).ToList();
            int other = shown.FindIndex(s => string.Equals(s.Label.Trim(), RenderText.Other, StringComparison.OrdinalIgnoreCase));
            if (other >= 0)
            {
                shown[other] = (shown[other].Label, shown[other].Value + rest);
            }
            else
            {
                shown.Add((RenderText.Other, rest));
            }

            slices = shown;
        }

        double pitch = Math.Min(24, room / Math.Max(1, slices.Count));
        double total = slices.Sum(s => s.Value);
        double radius = Math.Min(width * 0.28, (height - top - 24) / 2);
        double cx = 24 + radius + (width * 0.04), cy = top + 12 + radius;
        double angle = -Math.PI / 2;
        for (int i = 0; i < slices.Count; i++)
        {
            string color = palette[i % palette.Count];
            double share = total <= 0 ? 0 : slices[i].Value / total;
            if (share >= 0.9999)
            {
                svg.Append("<circle cx=\"").Append(F(cx)).Append("\" cy=\"").Append(F(cy)).Append("\" r=\"").Append(F(radius)).Append("\" fill=\"").Append(color).Append("\"/>");
            }
            else if (share > 0)
            {
                double end = angle + (share * 2 * Math.PI);
                svg.Append("<path d=\"M").Append(F(cx)).Append(' ').Append(F(cy))
                    .Append(" L").Append(F(cx + (radius * Math.Cos(angle)))).Append(' ').Append(F(cy + (radius * Math.Sin(angle))))
                    .Append(" A").Append(F(radius)).Append(' ').Append(F(radius)).Append(" 0 ").Append(share > 0.5 ? '1' : '0').Append(" 1 ")
                    .Append(F(cx + (radius * Math.Cos(end)))).Append(' ').Append(F(cy + (radius * Math.Sin(end))))
                    .Append(" Z\" fill=\"").Append(color).Append("\" stroke=\"").Append(colors.Ground).Append("\" stroke-opacity=\"0.6\" stroke-width=\"1\"/>");   // the seam in the ground's color (#140)
                angle = end;
            }

            // The legend beside it: the swatch, the label and its share.
            double lx = cx + radius + 40, ly = top + 24 + (i * pitch);
            svg.Append("<rect x=\"").Append(F(lx)).Append("\" y=\"").Append(F(ly - 11)).Append("\" width=\"14\" height=\"14\" rx=\"3\" fill=\"").Append(color).Append("\"/>");
            Text(svg, lx + 22, ly + 1, Cut(slices[i].Label, Math.Max(8, (width - lx - 90) / CharWidth)) + "  " + Percent(share, slices.Count == 1), colors.Ink, 13, "start");
        }
    }

    // Seconds to days: aligned within the day (an hour step on the hour), a day step from the first midnight.
    private static List<(DateTime, string)> ClockTicks(DateTime first, DateTime last, TimeSpan step)
    {
        DateTime start;
        if (step < TimeSpan.FromDays(1))
        {
            long into = (first - first.Date).Ticks;
            start = first.Date.AddTicks((into + step.Ticks - 1) / step.Ticks * step.Ticks);
        }
        else
        {
            start = first.TimeOfDay == TimeSpan.Zero ? first.Date : first.Date.AddDays(1);
        }

        var ticks = new List<(DateTime, string)>();
        DateTime? previous = null;
        for (var at = start; at <= last && ticks.Count < 4 * MaxTimeTicks; at = at.Add(step))
        {
            string format = step < TimeSpan.FromMinutes(1) ? (previous is null || previous.Value.Date != at.Date ? "yyyy-MM-dd HH:mm:ss" : "HH:mm:ss")
                : step < TimeSpan.FromDays(1) ? (previous is null || previous.Value.Date != at.Date ? "yyyy-MM-dd HH:mm" : "HH:mm")
                : previous is null || previous.Value.Year != at.Year ? "yyyy-MM-dd" : "MM-dd";
            ticks.Add((at, at.ToString(format, CultureInfo.InvariantCulture)));
            previous = at;
            if (DateTime.MaxValue - at < step)
            {
                break;
            }
        }

        return ticks;
    }

    // Months and years on the calendar: the first month start at or after the first moment whose month (or year) is a whole step.
    private static List<(DateTime, string)> CalendarTicks(DateTime first, DateTime last, int months)
    {
        var ticks = new List<(DateTime, string)>();
        int index = (first.Year * 12) + first.Month - 1;
        if (first != new DateTime(first.Year, first.Month, 1, 0, 0, 0, first.Kind))
        {
            index++;
        }

        index = (index + months - 1) / months * months;
        for (; index / 12 <= 9999 && ticks.Count < 4 * MaxTimeTicks; index += months)
        {
            var at = new DateTime(index / 12, (index % 12) + 1, 1, 0, 0, 0, first.Kind);
            if (at > last)
            {
                break;
            }

            ticks.Add((at, at.ToString(months % 12 == 0 ? "yyyy" : "yyyy-MM", CultureInfo.InvariantCulture)));
        }

        return ticks;
    }

    private static void Text(StringBuilder svg, double x, double y, string text, string color, int size, string anchor, bool bold = false) =>
        svg.Append("<text x=\"").Append(F(x)).Append("\" y=\"").Append(F(y)).Append("\" fill=\"").Append(color).Append("\" font-size=\"").Append(N(size))
            .Append("\" text-anchor=\"").Append(anchor).Append('"').Append(bold ? " font-weight=\"600\"" : "").Append('>').Append(Escape(text)).Append("</text>");

    private static string Line(double x1, double y1, double x2, double y2, string color, double width) =>
        $"<line x1=\"{F(x1)}\" y1=\"{F(y1)}\" x2=\"{F(x2)}\" y2=\"{F(y2)}\" stroke=\"{color}\" stroke-width=\"{F(width)}\"/>";

    private static string F(double value) => Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}
