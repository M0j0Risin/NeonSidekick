using NeonSidekick.App;
using NeonSidekick.Perf;
using Spectre.Console;

namespace NeonSidekick.UI;

/// <summary>One run of the performance bar's row in one style.</summary>
public readonly record struct PerfSegment(string Text, Style Style);

/// <summary>The performance bar's row as the pane writes it: styled runs, and their text for the change check.</summary>
public sealed record PerfRow(IReadOnlyList<PerfSegment> Segments)
{
    /// <summary>The row's text, what the pane compares with the drawn one.</summary>
    public string Text { get; } = string.Concat(Segments.Select(s => s.Text));
}

/// <summary>
/// The performance bar's row (2026-09-29, the user's ask: a third bar under the toolbar with CPU %, RAM %, GPU % and VRAM %,
/// in four looks — "let's do all of them"): <see cref="PerfBarStyle.Text"/> writes each meter's label and value;
/// <see cref="PerfBarStyle.Gauge"/> draws a bar in eighth-block steps; <see cref="PerfBarStyle.Spark"/> the last
/// <see cref="PerfSampler.HistoryLength"/> readings as a sparkline; <see cref="PerfBarStyle.Led"/> ten segments lit along the
/// theme's gradient. Values and the gauge's and sparkline's cells take the load's colour (<see cref="LoadColor"/>); labels,
/// separators and unlit cells are dim. Spectre's own charts (<c>BarChart</c>, <c>BreakdownChart</c>) are several rows of
/// the transcript's flow and have no one-row form, so the glyphs are drawn here in the theme's colours. A meter the machine
/// cannot read is left out. Too narrow a window shrinks the meters, then falls back to the text look, then cuts the row.
/// Pure: the tests drive it.
/// </summary>
public static class PerfBar
{
    /// <summary>A drawn meter's widest, in cells. Pinned.</summary>
    public const int MeterCells = 10;

    /// <summary>Below this share the load is <see cref="Theme.Good"/>; below <see cref="WarnBelow"/> <see cref="Theme.Warn"/>; else <see cref="Theme.Bad"/>.</summary>
    public const double GoodBelow = 60;

    public const double WarnBelow = 85;

    // The widths a drawn meter tries, widest first, before the row falls back to text.
    private static readonly int[] MeterWidths = [MeterCells, 8, 6, 4];

    /// <summary>The row for <paramref name="style"/> in <paramref name="cells"/> cells; null for <see cref="PerfBarStyle.Off"/>.</summary>
    public static PerfRow? Render(PerfBarStyle style, PerfSnapshot latest, IReadOnlyList<PerfSnapshot> history, int cells)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (style == PerfBarStyle.Off)
        {
            return null;
        }

        var meters = Meters(latest);
        if (style != PerfBarStyle.Text)
        {
            foreach (int width in MeterWidths)
            {
                var drawn = Drawn(style, meters, history, width);
                if (Width(drawn) <= cells)
                {
                    return new PerfRow(drawn);
                }
            }
        }

        return new PerfRow(Cut(Text(meters), cells));
    }

    /// <summary>The load's colour: <see cref="Theme.Good"/> under <see cref="GoodBelow"/>, <see cref="Theme.Warn"/> under <see cref="WarnBelow"/>, else <see cref="Theme.Bad"/>.</summary>
    public static Color LoadColor(double percent) => percent < GoodBelow ? Theme.Good : percent < WarnBelow ? Theme.Warn : Theme.Bad;

    /// <summary>The eighth-block bar for <paramref name="percent"/> in <paramref name="cells"/> cells: the filled part, then the track. Pinned.</summary>
    public static (string Filled, string Track) Gauge(double percent, int cells)
    {
        int eighths = (int)Math.Round(PerfMath.Clamp(percent) / 100 * cells * 8, MidpointRounding.AwayFromZero);
        int whole = eighths / 8;
        int part = eighths % 8;
        string filled = string.Concat(Enumerable.Repeat(PerfText.GaugeFull, whole)) + (part > 0 ? PerfText.GaugeEighths[part - 1] : "");
        int used = whole + (part > 0 ? 1 : 0);
        return (filled, string.Concat(Enumerable.Repeat(PerfText.GaugeTrack, cells - used)));
    }

    /// <summary>The sparkline glyph for <paramref name="percent"/>: eight heights over 0–100. Pinned.</summary>
    public static string SparkLevel(double percent) =>
        PerfText.SparkLevels[Math.Clamp((int)(PerfMath.Clamp(percent) / 100 * PerfText.SparkLevels.Length), 0, PerfText.SparkLevels.Length - 1)];

    /// <summary>How many of <paramref name="cells"/> LED segments <paramref name="percent"/> lights (a started segment counts). Pinned.</summary>
    public static int LedsLit(double percent, int cells) => (int)Math.Ceiling(Math.Round(PerfMath.Clamp(percent) / 100 * cells, 6));

    private readonly record struct Meter(string Label, double Value, Func<PerfSnapshot, double?> Read);

    private static List<Meter> Meters(PerfSnapshot latest)
    {
        var meters = new List<Meter>(4);
        Add(PerfText.CpuLabel, s => s.Cpu);
        Add(PerfText.RamLabel, s => s.Ram);
        Add(PerfText.GpuLabel, s => s.Gpu);
        Add(PerfText.VramLabel, s => s.Vram);
        return meters;

        void Add(string label, Func<PerfSnapshot, double?> read)
        {
            if (read(latest) is { } value)
            {
                meters.Add(new Meter(label, PerfMath.Clamp(value), read));
            }
        }
    }

    private static List<PerfSegment> Text(List<Meter> meters)
    {
        var row = new List<PerfSegment>();
        foreach (var meter in meters)
        {
            if (row.Count > 0)
            {
                row.Add(new PerfSegment(PerfText.TextSeparator, Theme.DimText));
            }

            row.Add(new PerfSegment(meter.Label + " ", Theme.DimText));
            row.Add(new PerfSegment(PerfText.Percent(meter.Value).TrimStart(), new Style(LoadColor(meter.Value))));
        }

        return row;
    }

    private static List<PerfSegment> Drawn(PerfBarStyle style, List<Meter> meters, IReadOnlyList<PerfSnapshot> history, int cells)
    {
        var row = new List<PerfSegment>();
        foreach (var meter in meters)
        {
            if (row.Count > 0)
            {
                row.Add(new PerfSegment(PerfText.MeterSeparator, Theme.DimText));
            }

            row.Add(new PerfSegment(meter.Label + " ", Theme.DimText));
            switch (style)
            {
                case PerfBarStyle.Gauge:
                    var (filled, track) = Gauge(meter.Value, cells);
                    row.Add(new PerfSegment(filled, new Style(LoadColor(meter.Value))));
                    row.Add(new PerfSegment(track, Theme.DimText));
                    break;
                case PerfBarStyle.Spark:
                    // The newest readings at the right; a history shorter than the meter leaves blanks at its left.
                    var values = history.Select(meter.Read).OfType<double>().TakeLast(cells).ToList();
                    if (values.Count < cells)
                    {
                        row.Add(new PerfSegment(new string(' ', cells - values.Count), Theme.DimText));
                    }

                    foreach (double value in values)
                    {
                        row.Add(new PerfSegment(SparkLevel(value), new Style(LoadColor(value))));
                    }

                    break;
                default:
                    int lit = LedsLit(meter.Value, cells);
                    for (int i = 0; i < cells; i++)
                    {
                        row.Add(i < lit
                            ? new PerfSegment(PerfText.LedOn, new Style(Theme.SampleGradient(cells == 1 ? 0 : i / (double)(cells - 1), Theme.GradientStops)))
                            : new PerfSegment(PerfText.LedOff, Theme.DimText));
                    }

                    break;
            }

            row.Add(new PerfSegment(" " + PerfText.Percent(meter.Value), new Style(LoadColor(meter.Value))));
        }

        return row;
    }

    private static int Width(IEnumerable<PerfSegment> row) => row.Sum(s => TextCells.Width(s.Text));

    /// <summary>The runs cut to <paramref name="cells"/> cells, the last one shortened where the edge falls.</summary>
    private static List<PerfSegment> Cut(List<PerfSegment> row, int cells)
    {
        var cut = new List<PerfSegment>();
        int left = Math.Max(0, cells);
        foreach (var segment in row)
        {
            int width = TextCells.Width(segment.Text);
            if (width <= left)
            {
                cut.Add(segment);
                left -= width;
                continue;
            }

            if (left > 0)
            {
                cut.Add(segment with { Text = segment.Text[..left] });   // the text look is one cell per character
            }

            break;
        }

        return cut;
    }
}
