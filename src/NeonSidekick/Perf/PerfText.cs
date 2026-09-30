using System.Globalization;

namespace NeonSidekick.Perf;

/// <summary>
/// The performance bar's words and glyphs (2026-09-29): the meters' labels, the separators, and the glyph sets the four
/// looks draw with. Pure statics so the tests can pin them; invariant culture.
/// </summary>
public static class PerfText
{
    /// <summary>
    /// The performance bar's glyph (later on 2026-09-29, the user's pick): the toolbar's item whose double-click is
    /// <c>/perf</c>, and <c>/perf</c>'s notices. A surrogate pair, two cells. Pinned.
    /// </summary>
    public const string Glyph = "📈";

    /// <summary>What <c>/perf</c> says it did: the look it turned on, or off. Pinned.</summary>
    public static string BarNotice(string name) =>
        name == "off" ? "(" + Glyph + " performance bar off)" : "(" + Glyph + " performance bar on: " + name + ")";

    /// <summary><c>/perf</c> given something that is not a look. Pinned.</summary>
    public const string UsageError = "/perf takes off, text, gauge, spark or led, or nothing to toggle.";

    public const string CpuLabel = "CPU";
    public const string RamLabel = "RAM";
    public const string GpuLabel = "GPU";
    public const string VramLabel = "VRAM";

    /// <summary>The network's share of the link (2026-09-30). Pinned.</summary>
    public const string NetLabel = "NET%";

    /// <summary>The download rate (2026-09-30). Pinned.</summary>
    public const string NetDownLabel = "NET↓";

    /// <summary>The upload rate (2026-09-30). Pinned.</summary>
    public const string NetUpLabel = "NET↑";

    /// <summary>
    /// A network rate in bits/s, five cells wide (2026-09-30), in the units links and Task Manager count in:
    /// <c> 850K</c>, <c>12.4M</c>, <c> 150M</c>, <c> 1.2G</c>. Pinned.
    /// </summary>
    public static string Rate(double bitsPerSecond)
    {
        double bits = double.IsNaN(bitsPerSecond) ? 0 : Math.Max(0, bitsPerSecond);
        string text = bits < 1e6 ? Scaled(bits / 1e3, "K", whole: true)
            : bits < 1e9 ? Scaled(bits / 1e6, "M", whole: bits >= 99.95e6)   // 99.96M would round to 100.0M, six cells
            : Scaled(bits / 1e9, "G", whole: bits >= 99.95e9);
        return text.PadLeft(5);

        static string Scaled(double value, string unit, bool whole) =>
            value.ToString(whole ? "0" : "0.0", CultureInfo.InvariantCulture) + unit;
    }

    /// <summary>Between two meters of the <c>text</c> look.</summary>
    public const string TextSeparator = " · ";

    /// <summary>Between two meters of the drawn looks (gauge, spark, led).</summary>
    public const string MeterSeparator = "   ";

    /// <summary>
    /// A whole cell of the gauge: a heavy line through the row's middle (2026-09-30, the user's ask: the gauge no taller than its
    /// labels; full blocks, <c>█</c> and the eighths <c>▏</c>–<c>▉</c> over a <c>░</c> track, filled the cell top to bottom until then).
    /// </summary>
    public const string GaugeFull = "━";

    /// <summary>Half a cell of the gauge: the heavy line's left half. No shorter glyph fills in eighths, so the gauge steps in halves.</summary>
    public const string GaugeHalf = "╸";

    /// <summary>The gauge's unfilled cells, a light line, dim.</summary>
    public const string GaugeTrack = "─";

    /// <summary>The sparkline's eight heights, lowest first.</summary>
    public static readonly string[] SparkLevels = ["▁", "▂", "▃", "▄", "▅", "▆", "▇", "█"];

    public const string LedOn = "▰";

    public const string LedOff = "▱";

    /// <summary>A meter's value, four cells wide: <c>  7%</c>, <c> 34%</c>, <c>100%</c>.</summary>
    public static string Percent(double value) =>
        string.Create(CultureInfo.InvariantCulture, $"{Math.Round(PerfMath.Clamp(value), MidpointRounding.AwayFromZero):0}%").PadLeft(4);
}
