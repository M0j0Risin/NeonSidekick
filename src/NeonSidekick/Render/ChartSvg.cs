using System.Globalization;
using System.Text;
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

/// <summary>The labels along the category axis and the series over them, row for row.</summary>
public sealed record ChartData(IReadOnlyList<string> Labels, IReadOnlyList<ChartSeries> Series);

/// <summary>
/// The rows of a table as <c>render_chart</c> reads them (2026-11-16): an <see cref="ISheetRows"/> that keeps the header and the typed cells
/// (<see cref="TableSource"/>'s column judging), stopping at <see cref="ChartSvg.MaxPoints"/> rows with <see cref="Cut"/> set.
/// </summary>
public sealed class ChartTable : ISheetRows
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
    /// a blank cell is a gap). Null with <paramref name="error"/> naming an unknown column or the first cell that is no number.
    /// </summary>
    public ChartData? Data(string x, IReadOnlyList<string> y, out string? error)
    {
        int Index(string name) => Columns.ToList().FindIndex(c => string.Equals(c.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
        error = null;
        if (Rows.Count == 0)
        {
            error = RenderText.NoRows;
            return null;
        }

        int xi = Index(x);
        if (xi < 0)
        {
            error = RenderText.NoColumn(x, Columns);
            return null;
        }

        var labels = Rows.Select(r => xi < r.Count ? Label(r[xi]) : "").ToList();
        var series = new List<ChartSeries>();
        foreach (string name in y)
        {
            int yi = Index(name);
            if (yi < 0)
            {
                error = RenderText.NoColumn(name, Columns);
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

        return new ChartData(labels, series);
    }

    /// <summary>A cell as a label: a number in its shortest invariant form, a date as ISO, text as it is.</summary>
    public static string Label(XlsxValue cell) => cell.Kind switch
    {
        XlsxKind.Blank => "",
        XlsxKind.Number => cell.Exact ?? cell.Number.ToString("G15", CultureInfo.InvariantCulture),
        XlsxKind.Bool => cell.Number != 0 ? "true" : "false",
        XlsxKind.Date => cell.When.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        XlsxKind.DateTime => cell.When.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        XlsxKind.Time => cell.When.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        _ => cell.Text ?? "",
    };

    /// <summary>A cell as a number: NaN for a blank (a gap), null for anything that reads as none.</summary>
    private static double? Number(XlsxValue cell) => cell.Kind switch
    {
        XlsxKind.Blank => double.NaN,
        XlsxKind.Number => cell.Number,
        XlsxKind.Text when (cell.Text ?? "").Trim() is { Length: 0 } => double.NaN,
        XlsxKind.Text when double.TryParse((cell.Text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && double.IsFinite(parsed) => parsed,
        _ => null,
    };
}

/// <summary>
/// A bar, line or pie chart as SVG (2026-11-16, plan sharded-hugging-puppy #9, the user's call: the app draws charts itself, the browser only
/// turns them into pixels): nice tick steps, a legend for two or more series, long labels cut with an ellipsis and slanted on a crowded axis, a
/// pie's smallest slices past <see cref="MaxSlices"/> folded into Other, and a categorical palette held to contrast against its ground. Every
/// number written is invariant and every text escaped: a table's cells are untrusted and never become markup. Pure.
/// </summary>
public static class ChartSvg
{
    public const int MaxPoints = 1000;
    public const int MaxSeries = 12;
    public const int MaxSlices = 12;

    /// <summary>The chart's height for its width: a 5 by 3 frame.</summary>
    public static int HeightFor(int width) => (int)Math.Round(width * 0.6);

    /// <summary>The series colors on a white ground, each at least 3:1 against it.</summary>
    public static readonly IReadOnlyList<string> LightPalette =
        ["#2563EB", "#DC2626", "#047857", "#B45309", "#7C3AED", "#DB2777", "#0E7490", "#4D7C0F", "#C2410C", "#4338CA", "#0F766E", "#9333EA"];

    /// <summary>The series colors on <see cref="DarkGround"/>, each at least 3:1 against it.</summary>
    public static readonly IReadOnlyList<string> DarkPalette =
        ["#60A5FA", "#F87171", "#34D399", "#FBBF24", "#A78BFA", "#F472B6", "#22D3EE", "#A3E635", "#FB923C", "#818CF8", "#2DD4BF", "#C084FC"];

    public const string LightGround = "#FFFFFF";
    public const string DarkGround = "#17151F";

    private const string Font = "'Segoe UI', system-ui, -apple-system, 'Helvetica Neue', Arial, sans-serif";
    private const double CharWidth = 7.2;   // a 13px label's average advance

    /// <summary>The SVG of <paramref name="data"/> drawn as <paramref name="spec"/> says; <paramref name="folded"/> counts a pie's slices put in Other.</summary>
    public static string Svg(ChartSpec spec, ChartData data, out int folded)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(data);
        folded = 0;
        int width = spec.Width, height = HeightFor(width);
        var palette = spec.Dark ? DarkPalette : LightPalette;
        string ground = spec.Dark ? DarkGround : LightGround;
        string ink = spec.Dark ? "#ECEAF2" : "#1F2328";
        string quiet = spec.Dark ? "#A8A3B8" : "#57606A";
        string grid = spec.Dark ? "#34303F" : "#E5E7EB";
        var svg = new StringBuilder();
        svg.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"").Append(N(width)).Append("\" height=\"").Append(N(height))
            .Append("\" viewBox=\"0 0 ").Append(N(width)).Append(' ').Append(N(height)).Append("\" font-family=\"").Append(Font).Append("\">");
        svg.Append("<rect width=\"100%\" height=\"100%\" fill=\"").Append(ground).Append("\"/>");
        double top = 16;
        if (!string.IsNullOrWhiteSpace(spec.Title))
        {
            Text(svg, width / 2.0, top + 20, Cut(spec.Title, (width - 40) / 10.0), ink, 20, "middle", bold: true);
            top += 36;
        }

        if (spec.Kind == ChartKind.Pie)
        {
            Pie(svg, data, palette, ink, width, height, top, out folded);
            return svg.Append("</svg>").ToString();
        }

        var names = data.Series.Select(s => s.Name).ToList();
        if (names.Count > 1)
        {
            top = Legend(svg, names, palette, ink, width, top);
        }

        Axes(svg, spec, data, palette, ink, quiet, grid, width, height, top);
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

    /// <summary>A tick's label: as many decimals as the step needs, invariant, no grouping.</summary>
    public static string Tick(double value, double step)
    {
        // The fewest decimals that write the step exactly (2.5 takes one, 0.25 two, 20 none).
        int decimals = 0;
        while (decimals < 6 && Math.Abs((step * Math.Pow(10, decimals)) - Math.Round(step * Math.Pow(10, decimals))) > 1e-6)
        {
            decimals++;
        }

        if (Math.Abs(value) < step / 1e6)
        {
            value = 0;
        }

        return Math.Abs(value) >= 1e12 ? value.ToString("0.##E+0", CultureInfo.InvariantCulture) : value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    /// <summary><paramref name="text"/> cut to <paramref name="chars"/> characters with an ellipsis.</summary>
    public static string Cut(string text, double chars)
    {
        int max = Math.Max(2, (int)chars);
        string flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length <= max ? flat : flat[..(max - 1)].TrimEnd() + "…";
    }

    /// <summary>Text made safe in XML: the five escaped, and any control character a space (XML can't hold one).</summary>
    public static string Escape(string text)
    {
        var safe = new StringBuilder(text.Length);
        foreach (char c in text)
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

    private static void Axes(StringBuilder svg, ChartSpec spec, ChartData data, IReadOnlyList<string> palette, string ink, string quiet, string grid, int width, int height, double top)
    {
        int count = data.Labels.Count;
        bool horizontal = spec.Kind == ChartKind.Bar && spec.Horizontal;
        bool stacked = spec.Kind == ChartKind.Bar && spec.Stacked && data.Series.Count > 1;

        // The value range: each value, or each label's stack of positives up and negatives down; zero always on the scale for bars.
        double lowest = 0, highest = 0;
        bool any = false;
        for (int i = 0; i < count; i++)
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

        var (min, max, step) = NiceScale(lowest, highest);
        string[] labels = data.Labels.Select(l => l.Length == 0 ? " " : l).ToArray();
        double longest = labels.Length == 0 ? 0 : labels.Max(l => Math.Min(l.Length, 24)) * CharWidth;
        string[] ticks = Enumerable.Range(0, (int)Math.Round((max - min) / step) + 1).Select(k => Tick(min + (k * step), step)).ToArray();
        double tickWidth = ticks.Max(t => t.Length) * CharWidth;

        double left = 20 + (spec.YLabel is { Length: > 0 } ? 24 : 0) + (horizontal ? Math.Min(longest, width * 0.3) : tickWidth) + 10;
        double right = width - 24;
        double bottomRoom = 36 + (spec.XLabel is { Length: > 0 } ? 24 : 0);
        double plotWidth = right - left;
        bool slant = !horizontal && count > 0 && longest > (plotWidth / count) - 6;
        if (slant)
        {
            bottomRoom += Math.Min(longest, 160) * 0.65;
        }

        double bottom = height - bottomRoom;
        double plotHeight = bottom - top - 10;
        double plotTop = top + 10;

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

        // The category axis.
        double band = count == 0 ? 0 : (horizontal ? plotHeight : plotWidth) / count;
        double CategoryAt(int i) => horizontal ? plotTop + (band * (i + 0.5)) : left + (band * (i + 0.5));
        int every = Math.Max(1, (int)Math.Ceiling(count / Math.Max(1.0, (horizontal ? plotHeight / 16 : plotWidth / 14))));
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

        if (spec.XLabel is { Length: > 0 } xLabel)
        {
            Text(svg, (left + right) / 2, height - 12, Cut(xLabel, plotWidth / CharWidth), quiet, 13, "middle");
        }

        if (spec.YLabel is { Length: > 0 } yLabel)
        {
            double cy = (plotTop + bottom) / 2;
            svg.Append("<text x=\"20\" y=\"").Append(F(cy)).Append("\" fill=\"").Append(quiet).Append("\" font-size=\"13\" text-anchor=\"middle\" transform=\"rotate(-90 20 ")
                .Append(F(cy)).Append(")\">").Append(Escape(Cut(yLabel, plotHeight / CharWidth))).Append("</text>");
        }

        if (spec.Kind == ChartKind.Line)
        {
            for (int s = 0; s < data.Series.Count; s++)
            {
                string color = palette[s % palette.Count];
                var path = new StringBuilder();
                bool pen = false;
                for (int i = 0; i < count; i++)
                {
                    double v = data.Series[s].Values[i];
                    if (double.IsNaN(v))
                    {
                        pen = false;
                        continue;
                    }

                    path.Append(pen ? " L" : " M").Append(F(CategoryAt(i))).Append(' ').Append(F(ValueAt(v)));
                    pen = true;
                }

                svg.Append("<path d=\"").Append(path.ToString().Trim()).Append("\" fill=\"none\" stroke=\"").Append(color).Append("\" stroke-width=\"2.5\" stroke-linejoin=\"round\" stroke-linecap=\"round\"/>");
                if (count <= 60)
                {
                    for (int i = 0; i < count; i++)
                    {
                        double v = data.Series[s].Values[i];
                        if (!double.IsNaN(v))
                        {
                            svg.Append("<circle cx=\"").Append(F(CategoryAt(i))).Append("\" cy=\"").Append(F(ValueAt(v))).Append("\" r=\"3.5\" fill=\"").Append(color).Append("\"/>");
                        }
                    }
                }
            }

            return;
        }

        // Bars: side by side in a band, or stacked in one.
        double inner = band * 0.78;
        int perBand = stacked ? 1 : data.Series.Count;
        double thickness = inner / perBand;
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
    }

    private static void Pie(StringBuilder svg, ChartData data, IReadOnlyList<string> palette, string ink, int width, int height, double top, out int folded)
    {
        var values = data.Series[0].Values;
        var slices = data.Labels.Select((label, i) => (Label: label, Value: double.IsNaN(values[i]) ? 0 : values[i])).Where(s => s.Value > 0).ToList();
        folded = 0;
        if (slices.Count > MaxSlices)
        {
            // The largest kept in their own order (by position, so two equal slices are never confused); the rest summed as Other.
            var kept = Enumerable.Range(0, slices.Count).OrderByDescending(i => slices[i].Value).ThenBy(i => i).Take(MaxSlices - 1).ToHashSet();
            double rest = Enumerable.Range(0, slices.Count).Where(i => !kept.Contains(i)).Sum(i => slices[i].Value);
            folded = slices.Count - kept.Count;
            slices = [.. Enumerable.Range(0, slices.Count).Where(kept.Contains).Select(i => slices[i]), (RenderText.Other, rest)];
        }

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
                    .Append(" Z\" fill=\"").Append(color).Append("\" stroke=\"#FFFFFF\" stroke-opacity=\"0.6\" stroke-width=\"1\"/>");
                angle = end;
            }

            // The legend beside it: the swatch, the label and its share.
            double lx = cx + radius + 40, ly = top + 24 + (i * 24);
            svg.Append("<rect x=\"").Append(F(lx)).Append("\" y=\"").Append(F(ly - 11)).Append("\" width=\"14\" height=\"14\" rx=\"3\" fill=\"").Append(color).Append("\"/>");
            string percent = (share * 100).ToString(share * 100 >= 10 ? "F0" : "F1", CultureInfo.InvariantCulture) + "%";
            Text(svg, lx + 22, ly + 1, Cut(slices[i].Label, Math.Max(8, (width - lx - 90) / CharWidth)) + "  " + percent, ink, 13, "start");
        }
    }

    private static void Text(StringBuilder svg, double x, double y, string text, string color, int size, string anchor, bool bold = false) =>
        svg.Append("<text x=\"").Append(F(x)).Append("\" y=\"").Append(F(y)).Append("\" fill=\"").Append(color).Append("\" font-size=\"").Append(N(size))
            .Append("\" text-anchor=\"").Append(anchor).Append('"').Append(bold ? " font-weight=\"600\"" : "").Append('>').Append(Escape(text)).Append("</text>");

    private static string Line(double x1, double y1, double x2, double y2, string color, double width) =>
        $"<line x1=\"{F(x1)}\" y1=\"{F(y1)}\" x2=\"{F(x2)}\" y2=\"{F(y2)}\" stroke=\"{color}\" stroke-width=\"{F(width)}\"/>";

    private static string F(double value) => Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}
