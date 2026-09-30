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

    /// <summary>Between two meters of the <c>text</c> look.</summary>
    public const string TextSeparator = " · ";

    /// <summary>Between two meters of the drawn looks (gauge, spark, led).</summary>
    public const string MeterSeparator = "   ";

    /// <summary>The gauge's eighth blocks, one eighth to seven; a whole cell is <see cref="GaugeFull"/>.</summary>
    public static readonly string[] GaugeEighths = ["▏", "▎", "▍", "▌", "▋", "▊", "▉"];

    public const string GaugeFull = "█";

    /// <summary>The gauge's unfilled cells, dim.</summary>
    public const string GaugeTrack = "░";

    /// <summary>The sparkline's eight heights, lowest first.</summary>
    public static readonly string[] SparkLevels = ["▁", "▂", "▃", "▄", "▅", "▆", "▇", "█"];

    public const string LedOn = "▰";

    public const string LedOff = "▱";

    /// <summary>A meter's value, four cells wide: <c>  7%</c>, <c> 34%</c>, <c>100%</c>.</summary>
    public static string Percent(double value) =>
        string.Create(CultureInfo.InvariantCulture, $"{Math.Round(PerfMath.Clamp(value), MidpointRounding.AwayFromZero):0}%").PadLeft(4);
}
