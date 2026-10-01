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
/// in four looks — "let's do all of them"; since 2026-09-30 the meters the <c>Show performance bar</c> checklist checks,
/// <see cref="PerfBarItems"/>, the network's among them — NET a share like the rest, NET↓ and NET↑ written as rates and
/// drawn as shares of the link): <see cref="PerfBarStyle.Text"/> writes each meter's label and value;
/// <see cref="PerfBarStyle.Gauge"/> draws a heavy line in half-cell steps (eighth blocks, a cell tall, until 2026-09-30); <see cref="PerfBarStyle.Spark"/> the last
/// <see cref="PerfSampler.HistoryLength"/> readings as a sparkline; <see cref="PerfBarStyle.Led"/> ten segments lit along the
/// theme's gradient. Values and the gauge's and sparkline's cells take the load's colour (<see cref="LoadColor"/>); labels,
/// separators and unlit cells are dim. Spectre's own charts (<c>BarChart</c>, <c>BreakdownChart</c>) are several rows of
/// the transcript's flow and have no one-row form, so the glyphs are drawn here in the theme's colours. A meter the machine
/// cannot read is left out. Too narrow a window shrinks the meters, then falls back to the text look, then cuts the row.
/// Later on 2026-09-29 (the user's asks) every look is centred on its row (<see cref="Centered"/>; at the row's right for a
/// few hours before), and the text look's values keep four cells as the drawn looks' do (<c>  8%</c>, <c>100%</c>), so
/// nothing shifts as a value gains a digit; a cut row stays at the left. Pure: the tests drive it.
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

    /// <summary>
    /// The row of the meters <paramref name="items"/> checks (<see cref="PerfBarItems"/>, 2026-09-30) in <paramref name="style"/>
    /// in <paramref name="cells"/> cells; null — no row — with none checked. A checked meter the machine cannot read is left
    /// out, so a row may be empty (GPU alone on a machine without one): the row stays, and the pane's layout with it.
    /// </summary>
    public static PerfRow? Render(PerfBarStyle style, IReadOnlySet<string> items, PerfSnapshot latest, IReadOnlyList<PerfSnapshot> history, int cells)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(history);
        if (items.Count == 0)
        {
            return null;
        }

        var meters = Meters(items, latest);
        if (style != PerfBarStyle.Text)
        {
            foreach (int width in MeterWidths)
            {
                var drawn = Drawn(style, meters, history, width);
                if (Width(drawn) <= cells)
                {
                    return Centered(drawn, cells);
                }
            }
        }

        var text = Text(meters);
        return Width(text) <= cells ? Centered(text, cells) : new PerfRow(Cut(text, cells));
    }

    /// <summary>The load's colour: <see cref="Theme.Good"/> under <see cref="GoodBelow"/>, <see cref="Theme.Warn"/> under <see cref="WarnBelow"/>, else <see cref="Theme.Bad"/>.</summary>
    public static Color LoadColor(double percent) => percent < GoodBelow ? Theme.Good : percent < WarnBelow ? Theme.Warn : Theme.Bad;

    /// <summary>The heavy-line bar for <paramref name="percent"/> in <paramref name="cells"/> cells, in half-cell steps: the filled part, then the track. Pinned.</summary>
    public static (string Filled, string Track) Gauge(double percent, int cells)
    {
        int halves = (int)Math.Round(PerfMath.Clamp(percent) / 100 * cells * 2, MidpointRounding.AwayFromZero);
        int whole = halves / 2;
        bool half = halves % 2 == 1;
        string filled = string.Concat(Enumerable.Repeat(PerfText.GaugeFull, whole)) + (half ? PerfText.GaugeHalf : "");
        int used = whole + (half ? 1 : 0);
        return (filled, string.Concat(Enumerable.Repeat(PerfText.GaugeTrack, cells - used)));
    }

    /// <summary>The sparkline glyph for <paramref name="percent"/>: eight heights over 0–100. Pinned.</summary>
    public static string SparkLevel(double percent) =>
        PerfText.SparkLevels[Math.Clamp((int)(PerfMath.Clamp(percent) / 100 * PerfText.SparkLevels.Length), 0, PerfText.SparkLevels.Length - 1)];

    /// <summary>How many of <paramref name="cells"/> LED segments <paramref name="percent"/> lights (a started segment counts). Pinned.</summary>
    public static int LedsLit(double percent, int cells) => (int)Math.Ceiling(Math.Round(PerfMath.Clamp(percent) / 100 * cells, 6));

    /// <summary>
    /// One meter as the row draws it: its label, the share that fills its gauge, colours it and picks its sparkline and LEDs
    /// (<see cref="Read"/> for each reading of the history), and the value written after it — the share, or a network
    /// direction's rate (2026-09-30).
    /// </summary>
    private readonly record struct Meter(string Label, double Value, string ValueText, Func<PerfSnapshot, double?> Read);

    private static List<Meter> Meters(IReadOnlySet<string> items, PerfSnapshot latest)
    {
        var meters = new List<Meter>(PerfBarItems.Names.Length);
        foreach (string id in PerfBarItems.Names.Where(items.Contains))
        {
            switch (id)
            {
                case PerfBarItems.Cpu: Share(PerfText.CpuLabel, s => s.Cpu); break;
                case PerfBarItems.Ram: Share(PerfText.RamLabel, s => s.Ram); break;
                case PerfBarItems.Gpu: Share(PerfText.GpuLabel, s => s.Gpu); break;
                case PerfBarItems.Vram: Share(PerfText.VramLabel, s => s.Vram); break;
                case PerfBarItems.Net: Share(PerfText.NetLabel, s => PerfMath.NetPercent(s.NetDown, s.NetUp, s.NetLink)); break;
                case PerfBarItems.NetDown: Rate(PerfText.NetDownLabel, s => s.NetDown); break;
                case PerfBarItems.NetUp: Rate(PerfText.NetUpLabel, s => s.NetUp); break;
            }
        }

        return meters;

        void Share(string label, Func<PerfSnapshot, double?> read)
        {
            if (read(latest) is { } value)
            {
                meters.Add(new Meter(label, PerfMath.Clamp(value), PerfText.Percent(value), read));
            }
        }

        // A direction's rate (2026-09-30): written as bits/s, drawn as its share of the link (none while the link is unknown).
        void Rate(string label, Func<PerfSnapshot, double?> rate)
        {
            if (rate(latest) is { } value)
            {
                meters.Add(new Meter(label, PerfMath.LinkPercent(value, latest.NetLink) ?? 0, PerfText.Rate(value), s => PerfMath.LinkPercent(rate(s), s.NetLink)));
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
            row.Add(new PerfSegment(meter.ValueText, new Style(LoadColor(meter.Value))));
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

            row.Add(new PerfSegment(" " + meter.ValueText, new Style(LoadColor(meter.Value))));
        }

        return row;
    }

    /// <summary>
    /// The row centred in <paramref name="cells"/> by a blank run ahead of it — half the room, the odd cell at the right, where
    /// the pane's erase blanks the rest; an empty row stays empty.
    /// </summary>
    private static PerfRow Centered(List<PerfSegment> row, int cells)
    {
        int pad = (cells - Width(row)) / 2;
        if (row.Count > 0 && pad > 0)
        {
            row.Insert(0, new PerfSegment(new string(' ', pad), Style.Plain));
        }

        return new PerfRow(row);
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
